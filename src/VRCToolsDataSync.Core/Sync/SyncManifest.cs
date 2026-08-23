using System.Text.Json;
using System.Text.Json.Serialization;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

public sealed class SyncManifest
{
    /// <summary>
    /// 書き出す manifest の形式。
    /// <para>
    /// 2 で、実データの置き場所を内容から決まるキー (<see cref="BlobKeys"/>) に変えた。
    /// 1 を書いた版は <see cref="ManifestFile.RelativePath"/> をそのままキーとして扱う
    /// ため、2 の manifest からは目的のオブジェクトを見つけられない。読み込み側は
    /// 1 も扱えるが、逆は成り立たない。
    /// </para>
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Dictionary<string, ToolManifestEntry> Tools { get; set; } = new();
}

public sealed class ToolManifestEntry
{
    public long Version { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
}

public sealed class ManifestFile
{
    /// <summary>
    /// 同期先の中でのファイルの位置。区切りは常に '/' で、先頭に '/' は付けない
    /// (例: "vrcx/latest.sqlite3")。ローカルフォルダモードではクラウドフォルダからの
    /// 相対パス、S3 互換モードではキー接頭辞を除いたオブジェクトキーに対応する。
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// 実データを置いてあるキー。内容から決まる (<see cref="BlobKeys.FromSha256"/>)。
    /// <para>
    /// schemaVersion 1 の manifest には無い。その場合は
    /// <see cref="RelativePath"/> がそのままキーだったので、読み込み側は
    /// <see cref="ManifestFileKeys.StorageKeyOf"/> を通して解決する。
    /// </para>
    /// </summary>
    public string? BlobKey { get; set; }
}

/// <summary>
/// <see cref="ManifestFile"/> から実データのキーを取り出す。
/// </summary>
public static class ManifestFileKeys
{
    /// <summary>
    /// 実データが置いてあるキー。schemaVersion 1 の manifest (BlobKey が無い) では
    /// RelativePath がそのままキーだったので、そちらへ落とす。
    /// </summary>
    public static string StorageKeyOf(ManifestFile file)
        => string.IsNullOrEmpty(file.BlobKey) ? file.RelativePath : file.BlobKey;
}

/// <summary>
/// 読み込んだ manifest と、その時点の内容を表すタグの組。
/// タグは条件付き更新 (compare-and-swap) に使う。読み込み時点で manifest が
/// 存在しなかった場合と、同期先がタグを提供しない場合は null になる。
/// </summary>
public sealed record ManifestSnapshot(SyncManifest Manifest, string? VersionTag);

/// <summary>manifest.json のシリアライズ設定。同期先を問わず同じ形式で書き出す。</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// 同期先の manifest.json を読み書きする。
/// 実際の入出力は <see cref="ISyncStorage"/> に委ね、ここでは
/// 「読み込み → tool エントリ更新 → 保存」を競合に耐える形で組み立てる。
/// </summary>
public sealed class ManifestStore
{
    /// <summary>manifest のキー。同期先のルート直下に置く。</summary>
    public const string ManifestKey = "manifest.json";

    /// <summary>
    /// 条件付き更新が競合したときの再試行回数。S3 互換モードでは別 PC の Push と
    /// 衝突すると ETag が変わって保存が弾かれるので、読み直してやり直す。
    /// </summary>
    private const int MaxSaveAttempts = 5;

    private readonly ISyncStorage _storage;

    public ManifestStore(ISyncStorage storage)
    {
        _storage = storage;
    }

    public SyncManifest Load() => _storage.LoadManifest().Manifest;

    /// <summary>
    /// 指定 tool のエントリを read-modify-write で更新し、採番した version を返す。
    /// <paramref name="buildEntry"/> には採番済みの version が渡る。
    /// <para>
    /// 保存の直前に manifest を読み直すため、別プロセス / 別 SyncService が同時に
    /// 別 tool を Push していてもそのエントリを失わない。S3 互換モードでは
    /// さらに ETag による条件付き更新で、読み直しと保存の間に他 PC が割り込んだ
    /// ケースも検出してやり直す。
    /// </para>
    /// <para>
    /// <paramref name="expectedCurrentVersion"/> には、呼び出し側が送信内容を
    /// 決めるときに見た version を渡す。保存直前の manifest がそれと違っていれば、
    /// 送信の可否をその version 基準で判断した前提が崩れているため
    /// <see cref="ToolEntryChangedException"/> を投げる。ここで押し切ると、
    /// 「同じ内容だから送らない」と判断したオブジェクトを他 PC が上書きしている
    /// 場合に、manifest の記録と実データがずれる。
    /// </para>
    /// </summary>
    public long UpdateToolEntry(string toolKey, long expectedCurrentVersion, Func<long, ToolManifestEntry> buildEntry)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = _storage.LoadManifest();

            // この版が知らない形式の manifest には触らない。デシリアライズは知らない
            // フィールドを黙って捨てるため、読んで書き戻すだけで新しい版が書いた情報が
            // 落ちる。しかもその結果を現行形式として宣言することになり、新しい版から
            // 見て「形式は 2 だが中身が壊れている」manifest ができあがる。
            if (snapshot.Manifest.SchemaVersion > SyncManifest.CurrentSchemaVersion)
            {
                throw new SyncStorageException(
                    $"同期先の manifest.json は、この版が扱えない形式です " +
                    $"(schemaVersion={snapshot.Manifest.SchemaVersion}、" +
                    $"この版が扱えるのは {SyncManifest.CurrentSchemaVersion} まで)。" +
                    "他の PC がより新しい版で Push しています。VRCToolsDataSync を更新してください。");
            }

            var currentVersion =
                snapshot.Manifest.Tools.TryGetValue(toolKey, out var previous) ? previous.Version : 0;

            if (currentVersion != expectedCurrentVersion)
            {
                throw new ToolEntryChangedException(toolKey, expectedCurrentVersion, currentVersion);
            }

            var nextVersion = currentVersion + 1;
            snapshot.Manifest.Tools[toolKey] = buildEntry(nextVersion);

            // 読み込んだ manifest には、その manifest を書いた版の schemaVersion が
            // 入っている。ここで書き出す内容は現行形式 (BlobKey を含む) なので、
            // 宣言も現行値へ揃える。揃えないと、形式で分岐する読み手に 1 と伝えたまま
            // 2 の内容を渡すことになる。上で弾いているので、ここへ来るのは
            // CurrentSchemaVersion 以下、つまり引き上げにしかならない。
            snapshot.Manifest.SchemaVersion = SyncManifest.CurrentSchemaVersion;

            if (_storage.TrySaveManifest(snapshot.Manifest, snapshot.VersionTag))
            {
                return nextVersion;
            }

            if (attempt >= MaxSaveAttempts)
            {
                throw new SyncStorageConcurrencyException(
                    $"manifest.json の更新が {MaxSaveAttempts} 回続けて競合しました。" +
                    "他の PC が同時に Push している可能性があります。時間をおいて再実行してください。");
            }

            // 競合相手と歩調が揃ってライブロックしないよう、待ち時間を伸ばしながら再試行する。
            Thread.Sleep(TimeSpan.FromMilliseconds(150 * attempt));
        }
    }
}

/// <summary>
/// Push の途中で、対象 tool の manifest エントリが他の PC / プロセスによって
/// 書き換えられた。呼び出し側はコンフリクトとして扱い、先に Pull させる。
/// </summary>
public sealed class ToolEntryChangedException : Exception
{
    public ToolEntryChangedException(string toolKey, long expectedVersion, long actualVersion)
        : base($"Push の途中で {toolKey} の同期先が更新されました " +
               $"(expected={expectedVersion}, actual={actualVersion})")
    {
        ToolKey = toolKey;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public string ToolKey { get; }
    public long ExpectedVersion { get; }
    public long ActualVersion { get; }
}

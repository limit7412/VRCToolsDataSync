using System.Text.Json;
using System.Text.Json.Serialization;
using VRCToolsDataSync.Core.Storage;

namespace VRCToolsDataSync.Core.Sync;

public sealed class SyncManifest
{
    public int SchemaVersion { get; set; } = 1;
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
    /// </summary>
    public long UpdateToolEntry(string toolKey, Func<long, ToolManifestEntry> buildEntry)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = _storage.LoadManifest();
            var nextVersion =
                (snapshot.Manifest.Tools.TryGetValue(toolKey, out var previous) ? previous.Version : 0) + 1;
            snapshot.Manifest.Tools[toolKey] = buildEntry(nextVersion);

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

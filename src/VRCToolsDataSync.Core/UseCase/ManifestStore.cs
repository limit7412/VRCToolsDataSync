
using VRCToolsDataSync.Core.Domain;
namespace VRCToolsDataSync.Core.UseCase;

/// <summary>
/// 同期先の manifest.json を読み書きする。
/// 実際の入出力は <see cref="ISyncStorage"/> に委ね、ここでは
/// 「読み込み → tool エントリ更新 → 保存」を競合に耐える形で組み立てる。
/// </summary>
public sealed class ManifestStore
{
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

    /// <summary>
    /// manifest を読み込む。この版で扱えない形式なら投げる
    /// (<see cref="EnsureSupported"/>)。
    /// </summary>
    public SyncManifest Load()
    {
        var manifest = _storage.LoadManifest().Manifest;
        EnsureSupported(manifest);
        return manifest;
    }

    /// <summary>
    /// この版で扱える形式かを確かめる。扱えなければ <see cref="SyncStorageException"/>。
    /// <para>
    /// デシリアライズは知らないフィールドを黙って捨てる。捨てた結果を「manifest の
    /// すべて」として扱うと、内容を書き換える側が軒並み壊れる。Push は落ちた情報を
    /// 捨てたまま書き戻し、Pull は新しい形式が別の意味で使っているエントリを古い解釈で
    /// ローカルへ反映し (notes フォルダの削除まで行う)、回収は見えない参照を孤児と
    /// 判定して消す。読めた範囲で進めるより、扱えないと言って止まる方が安全である。
    /// </para>
    /// </summary>
    public static void EnsureSupported(SyncManifest manifest)
    {
        if (manifest.SchemaVersion <= SyncManifest.CurrentSchemaVersion) return;
        throw new SyncStorageException(
            $"同期先の manifest.json は、この版が扱えない形式です " +
            $"(schemaVersion={manifest.SchemaVersion}、" +
            $"この版が扱えるのは {SyncManifest.CurrentSchemaVersion} まで)。" +
            "他の PC がより新しい版で Push しています。VRCToolsDataSync を更新してください。");
    }

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

            // 保存の直前に読み直しているので、ここでも確かめる。Load を経由しない経路
            // なので、読み込みから保存までの間に他の PC がより新しい版で公開した場合を
            // ここで捕まえる。
            EnsureSupported(snapshot.Manifest);

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

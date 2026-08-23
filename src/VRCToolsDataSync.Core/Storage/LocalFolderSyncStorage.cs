using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCToolsDataSync.Core.Sync;
using VRCToolsDataSync.Core.Watch;

namespace VRCToolsDataSync.Core.Storage;

/// <summary>
/// ローカルの同期フォルダ (OneDrive など) を同期先として扱う。
/// 実 PC 間の転送はフォルダ同期クライアントに任せ、本実装はファイル操作だけを行う。
/// </summary>
public sealed class LocalFolderSyncStorage : ISyncStorage
{
    /// <summary>同期フォルダの同期履歴キーに付く接頭辞の先頭。</summary>
    public const string StateKeyScheme = "folder|";

    /// <summary>読み書きの権限を確認するために書いて読み戻す中身。</summary>
    private static readonly byte[] ProbePayload = "VRCToolsDataSync access check"u8.ToArray();

    /// <summary>書き出し中のファイルに付ける名前の先頭。回収も掃除もこれで見分ける。</summary>
    private const string StagingPrefix = ".building-";

    /// <summary>書き出し中のファイルを置き去りとみなすまでの時間。</summary>
    private static readonly TimeSpan StagingRetention = TimeSpan.FromDays(1);

    private readonly string _rootDirectory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;

    public LocalFolderSyncStorage(string rootDirectory, ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new SyncStorageConfigurationException("同期フォルダのパスが未設定です");
        }
        // 末尾の区切りを落として揃える。"D:\\sync" と "D:\\sync\\" が
        // 別の同期先として扱われると、同期履歴が分かれてしまう。
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory.Trim()));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<LocalFolderSyncStorage>()
                  ?? (ILogger)NullLogger<LocalFolderSyncStorage>.Instance;
    }

    public string RootDirectory => _rootDirectory;

    public string DisplayName => _rootDirectory;

    // フォルダごとに同期履歴を分ける。同じ接頭辞を共有すると、別のフォルダへ
    // 切り替えたときに前のフォルダの LastPulledVersion がそのまま使われ、
    // 起動時 Pull が「ローカルが最新」として省略されたり、Push が競合を
    // 見逃して切り替え先を上書きしたりする。
    // 更新前の settings.json が持つツールキーだけの履歴は、同じフォルダを
    // 指している場合に限り SyncRunner が引き継ぐ。
    public string StateKeyPrefix => $"{StateKeyScheme}{_rootDirectory.ToLowerInvariant()}|";

    /// <summary>
    /// フォルダが存在し、同期に必要な読み書きと削除ができるかを確かめる。
    /// 読み取り専用の場所や、同期クライアントがまだ実体化していないプレースホルダを
    /// 指しているケースを、設定を保存する前に弾く。
    /// <para>
    /// NTFS では作成・読み取り・属性の書き込み・削除が別々の権限なので、4 つとも
    /// 実際に試す。どれか 1 つでも欠けると、設定は保存できても後の同期が失敗する。
    /// </para>
    /// </summary>
    public void VerifyAccess()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            throw new SyncStorageConfigurationException($"同期フォルダが存在しません: {_rootDirectory}");
        }
        var probe = Path.Combine(_rootDirectory, $".vrctoolsdatasync-access-check-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, ProbePayload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStorageConfigurationException(
                $"同期フォルダへ書き込めません: {_rootDirectory} ({ex.Message})");
        }

        // 読み取りの可否。NTFS では読み取りだけを拒否できるので、書き込みが通っても
        // manifest の読み込みや Pull が失敗する構成があり得る。書いた内容を読み戻して
        // 確かめる (S3 側は LoadManifest で読み取りを確認しており、それに合わせる)。
        string? readFailure = null;
        try
        {
            if (!File.ReadAllBytes(probe).AsSpan().SequenceEqual(ProbePayload))
            {
                readFailure = "書いた内容と異なる内容が読み出されました";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            readFailure = ex.Message;
        }
        if (readFailure is not null)
        {
            // 読めないだけで削除は通ることがあるので、後始末は試みる。
            try { File.Delete(probe); } catch { /* best-effort cleanup */ }
            throw new SyncStorageConfigurationException(
                $"同期フォルダから読み取れません: {_rootDirectory} ({readFailure})");
        }

        // 最終更新時刻を書けるかも確かめる。NTFS では「書き込み」と「属性の書き込み」が
        // 別の権限なので、作成は通っても時刻だけ拒まれる構成があり得る。Push はこの時刻を
        // 必ず刻む (刻めなければ失敗させる) ため、ここで弾かないと設定は保存できても
        // 最初の Push で失敗する。
        try
        {
            File.SetLastWriteTimeUtc(probe, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(probe); } catch { /* best-effort cleanup */ }
            throw new SyncStorageConfigurationException(
                $"同期フォルダのファイルの更新時刻を変更できません: {_rootDirectory} ({ex.Message})");
        }

        // 削除の可否も確かめる。NTFS では「ファイルの作成」と「削除」が別の権限
        // なので、作成だけ許して削除を拒むフォルダを作れる。Push は実データを消さない
        // が、参照されなくなった実体の回収 (storage gc) が削除を行うため、そこを
        // 確認しないと設定を保存できても後で回収が失敗し続ける。
        //
        // 失敗した場合、検査用ファイルは残る。設定が保存されない以上、利用者は
        // 権限を直して再度試すことになるので、S3 側と同じ扱いにしている。
        try
        {
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStorageConfigurationException(
                $"同期フォルダのファイルを削除できません: {_rootDirectory} ({ex.Message})");
        }
    }

    public ManifestSnapshot LoadManifest()
    {
        var path = StorageKey.ToLocalPath(_rootDirectory, ManifestStore.ManifestKey);
        if (!File.Exists(path))
        {
            return new ManifestSnapshot(new SyncManifest(), null);
        }
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<SyncManifest>(stream, ManifestJson.Options) ?? new SyncManifest();
        return new ManifestSnapshot(manifest, null);
    }

    /// <summary>
    /// manifest を書き出す。ローカルフォルダには保存時に内容を比較して弾く手段が
    /// 無いため <paramref name="expectedTag"/> は使わず、常に成功を返す。
    /// <para>
    /// つまりこのモードでは compare-and-swap が効かない。読み直しと書き込みの間に
    /// 別プロセスが更新した場合、その更新は失われる
    /// (<see cref="ManifestStore.UpdateToolEntry"/> の version 検査で、同じ tool に
    /// 対する競合は Push 側で検出できるが、書き込み自体は不可分ではない)。
    /// 別 PC との衝突はフォルダ同期クライアントの競合検出に委ねる。
    /// </para>
    /// </summary>
    public bool TrySaveManifest(SyncManifest manifest, string? expectedTag)
    {
        var path = StorageKey.ToLocalPath(_rootDirectory, ManifestStore.ManifestKey);
        Directory.CreateDirectory(_rootDirectory);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, manifest, ManifestJson.Options);
            }
            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
        }
        return true;
    }

    /// <summary>
    /// 同期先へ書いた時刻を最終更新時刻として刻む。
    /// <para>
    /// 回収 (<see cref="BlobGarbageCollector"/>) の猶予期間は「同期先へ書かれてからの
    /// 経過時間」で判断する。ところが <see cref="File.Copy(string, string)"/> はコピー元の
    /// 最終更新時刻を引き継ぐため、何か月も前に作られた設定ファイルを Push すると、
    /// 書いた直後の実体が猶予期間を過ぎていると見なされる。manifest が他の PC へ
    /// 届く前にその PC で回収が走ると、届いた manifest が消えた実体を指す。
    /// </para>
    /// <para>
    /// 刻めなかった場合は Push を失敗させる。実体は元のファイルの古い時刻を持った
    /// ままなので、そのまま manifest を公開すると、公開が他の PC へ届く前にその PC の
    /// 回収が「猶予期間を過ぎた孤児」と判定して消しうる。届いた manifest は欠落を
    /// 指す。警告だけで進めると、この取りこぼしが利用者から見えない。
    /// </para>
    /// <para>
    /// ここで止めても実体が壊れることはない。参照されないまま残った実体は、次の
    /// 回収がまとめて片付ける。
    /// </para>
    /// </summary>
    private static void StampWriteTime(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStorageException(
                $"同期先へ書いた時刻を記録できません: {path} ({ex.Message})。" +
                "この時刻は不要になった実体を回収する判断に使うため、" +
                "記録できないまま進めると送ったばかりの実体が回収されうる。", ex);
        }
    }

    public IStagedUpload BeginUpload()
    {
        // 置き場所は内容から決まるので、確定するのは Commit 時。ただし最後の移動を
        // 安価に済ませたいので、書き出し自体は確定先と同じフォルダで行う
        // (別ドライブを経由すると数百 MB のコピーになる)。
        var blobDirectory = StorageKey.ToLocalPath(_rootDirectory, BlobKeys.Prefix.TrimEnd('/'));
        Directory.CreateDirectory(blobDirectory);
        PruneStaleStagingFiles(blobDirectory);
        // 別プロセスの Push と一時ファイル名が衝突しないよう GUID を含める。
        var stagingPath = Path.Combine(blobDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        return new StagedFile(stagingPath, this);
    }

    /// <summary>
    /// 置き去りになった書き出し中のファイルを片付ける。ここには SQLite の完全な複製
    /// (数百 MB) が置かれるうえ、同期フォルダの中なので放置すると同期クライアントが
    /// 他の PC へ配ってしまう。進行中の Push を消さないよう、十分に古いものだけを対象にする。
    /// </summary>
    private void PruneStaleStagingFiles(string blobDirectory)
    {
        var threshold = DateTime.UtcNow - StagingRetention;
        try
        {
            foreach (var path in Directory.EnumerateFiles(blobDirectory, StagingPrefix + "*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < threshold) File.Delete(path);
                }
                catch (IOException)
                {
                    // 別プロセスが書き込み中。次の機会に任せる。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "書き出し中ファイルの掃除に失敗しました: {Directory}", blobDirectory);
        }
    }

    public IEnumerable<StoredObject> List(string keyPrefix)
    {
        var root = StorageKey.ToLocalPath(_rootDirectory, keyPrefix.TrimEnd('/'));
        if (!Directory.Exists(root)) yield break;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // 書き出し中の一時ファイルは回収の対象にしない。
            var name = Path.GetFileName(path);
            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal)) continue;

            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var relative = Path.GetRelativePath(_rootDirectory, path);
            yield return new StoredObject(
                StorageKey.FromRelativePath(relative),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length);
        }
    }

    public bool TryDownload(string key, string localPath)
    {
        var source = StorageKey.ToLocalPath(_rootDirectory, key);
        if (!File.Exists(source))
        {
            return false;
        }
        AtomicFile.Copy(source, localPath, overwrite: true);
        return true;
    }

    public bool Exists(string key)
        => File.Exists(StorageKey.ToLocalPath(_rootDirectory, key));

    public StoredObject? Stat(string key)
    {
        var info = new FileInfo(StorageKey.ToLocalPath(_rootDirectory, key));
        if (!info.Exists) return null;
        return new StoredObject(key, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
    }

    /// <summary>
    /// 見たときのままなら削除する。
    /// <para>
    /// Win32 には「更新時刻が一致する場合だけ削除する」不可分な操作が無い。
    /// できるのは削除の直前に読み直すところまでで、そこから <see cref="File.Delete"/>
    /// までの一瞬は残る。幅は 1 回のシステムコールぶんで、猶予期間 (既定 7 日) に
    /// 対しては無視できるが、閉じ切ってはいない。
    /// </para>
    /// <para>
    /// 同期クライアントがファイルを掴んでいる、権限が足りないといった理由で削除自体が
    /// 失敗しうる。呼び出し側 (回収) が 1 件の失敗として扱えるよう、同期先の種類に
    /// よらない <see cref="SyncStorageException"/> に揃えて投げる。
    /// </para>
    /// </summary>
    public bool TryDelete(StoredObject expected)
    {
        var path = StorageKey.ToLocalPath(_rootDirectory, expected.Key);
        var info = new FileInfo(path);
        if (!info.Exists) return true;

        // 見たときと変わっていたら消さない。別の PC の Push が同じ内容を再利用して
        // 置き直した実体は、これから公開される manifest に参照される。
        var lastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (lastModified != expected.LastModified) return false;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStorageException($"ファイルを削除できません: {path} ({ex.Message})", ex);
        }
        return true;
    }

    public IManifestWatcher CreateManifestWatcher()
        => new CloudWatcher(this, logger: _loggerFactory?.CreateLogger<CloudWatcher>());

    private sealed class StagedFile : IStagedUpload
    {
        private readonly LocalFolderSyncStorage _storage;
        private bool _committed;

        public StagedFile(string stagingPath, LocalFolderSyncStorage storage)
        {
            LocalPath = stagingPath;
            _storage = storage;
        }

        public string LocalPath { get; }

        public void Commit(string key)
        {
            StorageKey.Validate(key);
            var destination = StorageKey.ToLocalPath(_storage._rootDirectory, key);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (File.Exists(destination))
            {
                // 内容から決まるキーなので、既にあるなら中身は同じ。置き換えずに済ませる。
                DiscardStaging();
            }
            else
            {
                try
                {
                    File.Move(LocalPath, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // 上の Exists から Move までの間に、別の Push が同じ内容を置いた。
                    // 内容から決まるキーなので相手が置いたものも中身は同じ。競合した側も
                    // 成功として扱う。ここで失敗させると、同じデータを同時に送った
                    // だけで Push 全体が落ちる。
                    DiscardStaging();
                }
            }
            // 既にあった場合も刻み直す。参照が切れて猶予期間を過ぎた実体を、別の世代が
            // 再び参照することがある。刻まないと、参照され直した直後に回収されうる。
            StampWriteTime(destination);
            _committed = true;
        }

        public void Dispose()
        {
            if (_committed) return;
            DiscardStaging();
        }

        /// <summary>
        /// 書き出しに使った一時ファイルを片付ける。消せなくても失敗にはしない。
        /// 残った分は次回以降の <see cref="BeginUpload"/> が回収する。
        /// </summary>
        private void DiscardStaging()
        {
            if (!File.Exists(LocalPath)) return;
            try { File.Delete(LocalPath); } catch { /* best-effort cleanup */ }
        }
    }
}

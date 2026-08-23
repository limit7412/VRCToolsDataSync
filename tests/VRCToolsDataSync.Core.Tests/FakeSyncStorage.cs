using System.Text.Json;
using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// メモリ上の同期先。実際の入出力を伴わずに、同期と回収の性質を確かめるために使う。
/// <para>
/// 実装の詳細を写すのではなく、<see cref="ISyncStorage"/> の契約だけを満たす。
/// 時刻は <see cref="Now"/> で明示的に動かす。猶予期間の判定を確かめるのに実時間を
/// 待つわけにはいかないため。
/// </para>
/// </summary>
internal sealed class FakeSyncStorage : ISyncStorage
{
    private readonly Dictionary<string, StoredEntry> _objects = new(StringComparer.Ordinal);
    private ManifestSnapshot? _manifest;

    /// <summary>この同期先が「今」だと思っている時刻。書き込みの記録に使う。</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>ここに載せたキーの削除は失敗する。1 件の失敗を作るために使う。</summary>
    public HashSet<string> DeleteFailures { get; } = new(StringComparer.Ordinal);

    /// <summary>呼ばれた操作の順序。読む順と列挙する順を確かめるのに使う。</summary>
    public List<string> Calls { get; } = new();

    /// <summary><see cref="List"/> が 1 件返すたびに呼ばれる。列挙中の割り込みを作る。</summary>
    public Action<string>? OnListed { get; set; }

    /// <summary>
    /// <see cref="BeginUpload"/> の直後に呼ばれる。ハッシュを取ってから実際に
    /// 書き出すまでの間に元ファイルが変わる状況を作る。
    /// </summary>
    public Action? OnBeginUpload { get; set; }

    public string DisplayName => "fake";

    public string StateKeyPrefix => "fake|";

    public void VerifyAccess() { }

    /// <summary>テストの前提として、キーに中身を置く。</summary>
    public void Seed(string key, string content, DateTimeOffset? lastModified = null)
        => _objects[key] = new StoredEntry(content, lastModified ?? Now);

    /// <summary>テストの前提として、manifest を置く。</summary>
    public void SeedManifest(SyncManifest manifest)
        => _manifest = new ManifestSnapshot(Clone(manifest), "tag-" + _objects.Count);

    /// <summary>manifest が存在しない状態に戻す。</summary>
    public void ClearManifest() => _manifest = null;

    public bool Has(string key) => _objects.ContainsKey(key);

    public string ContentOf(string key) => _objects[key].Content;

    public IReadOnlyCollection<string> Keys => _objects.Keys.ToList();

    public ManifestSnapshot LoadManifest()
    {
        Calls.Add("LoadManifest");
        // 実装と同じく、存在しない場合は空の manifest とタグ無しを返す。
        return _manifest is null
            ? new ManifestSnapshot(new SyncManifest(), null)
            : new ManifestSnapshot(Clone(_manifest.Manifest), _manifest.VersionTag);
    }

    public bool TrySaveManifest(SyncManifest manifest, string? expectedTag)
    {
        Calls.Add("TrySaveManifest");
        _manifest = new ManifestSnapshot(Clone(manifest), "tag-" + Guid.NewGuid().ToString("N"));
        return true;
    }

    public IStagedUpload BeginUpload()
    {
        Calls.Add("BeginUpload");
        var staged = new StagedUpload(this);
        OnBeginUpload?.Invoke();
        return staged;
    }

    public bool TryDownload(string key, string localPath)
    {
        if (!_objects.TryGetValue(key, out var entry)) return false;
        File.WriteAllText(localPath, entry.Content);
        return true;
    }

    public bool Exists(string key)
    {
        Calls.Add("Exists:" + key);
        return _objects.ContainsKey(key);
    }

    public StoredObject? Stat(string key)
    {
        Calls.Add("Stat:" + key);
        return _objects.TryGetValue(key, out var entry)
            ? new StoredObject(key, entry.LastModified, entry.Content.Length)
            : null;
    }

    public void Delete(string key)
    {
        Calls.Add("Delete:" + key);
        if (DeleteFailures.Contains(key))
        {
            throw new SyncStorageException($"削除できません (テスト): {key}");
        }
        _objects.Remove(key);
    }

    public IEnumerable<StoredObject> List(string keyPrefix)
    {
        Calls.Add("List:" + keyPrefix);
        // 列挙中に中身が変わっても壊れないよう、キーの一覧を先に固定する。
        // 実装 (ページングやディレクトリ走査) も一度に全部は返さない。
        foreach (var key in _objects.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList())
        {
            if (!_objects.TryGetValue(key, out var entry)) continue;
            // 写しを先に固定してから割り込ませる。呼び出し側が受け取るのは列挙時点の
            // 値で、その後に同期先が変わっている、という状況を作るため。
            var snapshot = new StoredObject(key, entry.LastModified, entry.Content.Length);
            OnListed?.Invoke(key);
            yield return snapshot;
        }
    }

    public IManifestWatcher CreateManifestWatcher() => throw new NotSupportedException();

    /// <summary>
    /// 実装は manifest を JSON として往復させるため、呼び出し側が持つ参照とは
    /// 切り離される。同じ挙動にしないと、テストだけが同一インスタンスの
    /// 書き換えを見てしまう。
    /// </summary>
    private static SyncManifest Clone(SyncManifest manifest)
        => JsonSerializer.Deserialize<SyncManifest>(
               JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson.Options),
               ManifestJson.Options)
           ?? new SyncManifest();

    private sealed record StoredEntry(string Content, DateTimeOffset LastModified);

    private sealed class StagedUpload : IStagedUpload
    {
        private readonly FakeSyncStorage _storage;
        private bool _committed;

        public StagedUpload(FakeSyncStorage storage)
        {
            _storage = storage;
            LocalPath = Path.Combine(Path.GetTempPath(), "vrctds-test-" + Guid.NewGuid().ToString("N"));
        }

        public string LocalPath { get; }

        public void Commit(string key)
        {
            _storage.Calls.Add("Commit:" + key);
            _storage._objects[key] = new StoredEntry(File.ReadAllText(LocalPath), _storage.Now);
            _committed = true;
            Cleanup();
        }

        public void Dispose()
        {
            if (!_committed) _storage.Calls.Add("Discard");
            Cleanup();
        }

        private void Cleanup()
        {
            if (File.Exists(LocalPath))
            {
                try { File.Delete(LocalPath); } catch { /* best-effort cleanup */ }
            }
        }
    }
}

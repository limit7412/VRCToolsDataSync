using System.Globalization;
using System.Text;
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

    /// <summary>
    /// <see cref="TrySaveManifest"/> の直前に呼ばれる。読み直しから保存までの間に
    /// 他の PC が manifest を更新した状況を作る。
    /// </summary>
    public Action? OnBeforeSaveManifest { get; set; }

    /// <summary>
    /// <see cref="Stat"/> が値を返した直後に呼ばれる。読み直しから削除までの間に
    /// 他の PC が同じキーへ書き直した状況を作る。
    /// </summary>
    public Action<string>? OnStat { get; set; }

    public string DisplayName => "fake";

    public string StateKeyPrefix => "fake|";

    public void VerifyAccess() { }

    /// <summary>テストの前提として、キーに中身を置く。</summary>
    public void Seed(string key, byte[] content, DateTimeOffset? lastModified = null)
        => _objects[key] = new StoredEntry(content, lastModified ?? Now, NextETag());

    /// <summary>テストの前提として、キーに中身を置く (テキストの場合)。</summary>
    public void Seed(string key, string content, DateTimeOffset? lastModified = null)
        => Seed(key, Encoding.UTF8.GetBytes(content), lastModified);

    /// <summary>テストの前提として、manifest を置く。</summary>
    public void SeedManifest(SyncManifest manifest)
        => _manifest = new ManifestSnapshot(Clone(manifest), "tag-" + _objects.Count);

    /// <summary>manifest が存在しない状態に戻す。</summary>
    public void ClearManifest() => _manifest = null;

    public bool Has(string key) => _objects.ContainsKey(key);

    /// <summary>置かれている中身。バイト列のまま返す。</summary>
    public byte[] ContentOf(string key) => _objects[key].Content;

    /// <summary>置かれている中身をテキストとして読む。ASCII の内容にだけ使う。</summary>
    public string TextOf(string key) => Encoding.UTF8.GetString(_objects[key].Content);

    public IReadOnlyCollection<string> Keys => _objects.Keys.ToList();

    public ManifestSnapshot LoadManifest()
    {
        Calls.Add("LoadManifest");
        // 実装と同じく、存在しない場合は空の manifest とタグ無しを返す。
        return _manifest is null
            ? new ManifestSnapshot(new SyncManifest(), null)
            : new ManifestSnapshot(Clone(_manifest.Manifest), _manifest.VersionTag);
    }

    /// <summary>
    /// タグを見て条件付きに保存する。S3 互換モードと同じ扱いにしておく。
    /// <para>
    /// タグを無視して常に成功させると、読み直しと保存の間に割り込まれた更新が
    /// 黙って失われる。<see cref="ManifestStore.UpdateToolEntry"/> の再試行は
    /// まさにその検出のためにあるので、無視する偽物では再試行の経路を
    /// 「通ったことにして」しまう。
    /// </para>
    /// </summary>
    public bool TrySaveManifest(SyncManifest manifest, string? expectedTag)
    {
        Calls.Add("TrySaveManifest");
        OnBeforeSaveManifest?.Invoke();

        // expectedTag が null なら「読んだ時点で manifest が無かった」。その間に
        // 誰かが作っていたら弾く (実装は If-None-Match:* で同じことをする)。
        if (!string.Equals(expectedTag, _manifest?.VersionTag, StringComparison.Ordinal))
        {
            Calls.Add("PreconditionFailed");
            return false;
        }

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
        File.WriteAllBytes(localPath, entry.Content);
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
        if (!_objects.TryGetValue(key, out var entry)) return null;

        // 返す値を先に固定してから割り込ませる。呼び出し側が持つのは読み直した時点の
        // 状態で、その後に同期先が変わっている、という状況を作るため。
        var snapshot = new StoredObject(key, entry.LastModified, entry.Content.Length, entry.ETag);
        OnStat?.Invoke(key);
        return snapshot;
    }

    /// <summary>
    /// 印が一致する場合だけ削除する。S3 互換モードの <c>If-Match</c> に相当する。
    /// <para>
    /// 印を見ずに常に消す偽物にすると、条件付き削除が効いていることを確かめられない。
    /// 実装より甘い偽物は、テストがあること自体を誤った安心に変える。
    /// </para>
    /// </summary>
    public bool TryDelete(StoredObject expected)
    {
        Calls.Add("TryDelete:" + expected.Key);
        if (DeleteFailures.Contains(expected.Key))
        {
            throw new SyncStorageException($"削除できません (テスト): {expected.Key}");
        }
        if (!_objects.TryGetValue(expected.Key, out var entry)) return true;
        if (!string.Equals(entry.ETag, expected.ETag, StringComparison.Ordinal))
        {
            Calls.Add("PreconditionFailed:" + expected.Key);
            return false;
        }
        _objects.Remove(expected.Key);
        return true;
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
            var snapshot = new StoredObject(key, entry.LastModified, entry.Content.Length, entry.ETag);
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

    /// <summary>
    /// 中身はバイト列で持つ。同期の対象には SQLite のような任意のバイト列が含まれる。
    /// 文字列に通すと不正な UTF-8 が置換され、ハッシュの元と保存した内容がずれるので、
    /// 「キーが表す内容と実際に置かれる内容が一致する」を確かめられなくなる。
    /// </summary>
    private sealed record StoredEntry(byte[] Content, DateTimeOffset LastModified, string ETag);

    private int _etagSeed;

    /// <summary>書き込むたびに変わる印。同じ内容でも書き直せば別の印になる。</summary>
    private string NextETag() => "\"etag-" + (++_etagSeed).ToString(CultureInfo.InvariantCulture) + "\"";

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
            _storage._objects[key] =
                new StoredEntry(File.ReadAllBytes(LocalPath), _storage.Now, _storage.NextETag());
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

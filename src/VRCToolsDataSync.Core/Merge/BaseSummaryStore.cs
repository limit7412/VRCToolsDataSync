using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCToolsDataSync.Core.Merge;

/// <summary>
/// 前回同期した時点の要約 (<see cref="DatabaseSummary"/>) を PC の中に保存する。
/// <para>
/// 同期先には置かない。要約は「この PC が最後に同期した時点」を表すもので、
/// PC ごとに違うため。同期履歴 (<c>ToolSyncState</c>) と同じく、同期先とツールの
/// 組ごとに分ける。キーには保存先のパスなどファイル名に使えない文字が入るので、
/// ハッシュにしてファイル名にする。
/// </para>
/// </summary>
public sealed class BaseSummaryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>ロックを待つ上限。要約の保存は一瞬で終わるので、これを超えるのは異常である。</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    public string RootDirectory { get; }

    public BaseSummaryStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    /// <summary>設定ファイルと同じフォルダの下の <c>base\</c> を使う。</summary>
    public static BaseSummaryStore NextTo(string settingsFilePath)
        => new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFilePath))!, "base"));

    /// <summary>
    /// 保存した要約を読む。無い場合、読めない場合、形式が古い場合は null を返す。
    /// 要約が無いことは「比べられない」ことを意味し、呼び出し側は従来どおりに振る舞う。
    /// </summary>
    public DatabaseSummary? Load(string stateKey)
    {
        var path = PathOf(stateKey);
        if (!File.Exists(path)) return null;
        // 読んでいる間に別のプロセスが置き換えると、Windows では置き換えの側が失敗する。
        using var _ = AcquireLock(path);
        if (!File.Exists(path)) return null;
        try
        {
            var summary = JsonSerializer.Deserialize<DatabaseSummary>(File.ReadAllText(path), JsonOptions);
            return summary is { Format: DatabaseSummary.CurrentFormat } ? summary : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string stateKey, DatabaseSummary summary)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = PathOf(stateKey);
        // 常駐中の GUI と CLI が同時に保存することがある。Windows では同じファイルへの
        // 置き換えが重なると片方が失敗し、その失敗を受けた呼び出し側が要約を消して
        // しまうので、置き換えをプロセスをまたいで順番に行う。
        using var _ = AcquireLock(path);
        // 書きかけで止まった要約を読むと、実際と違う基準で比べることになる。
        // 一時ファイルに書き切ってから置き換える。
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(summary, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// 要約を消す。要約を取り直せなかったときに使う。古い要約を残すと、それ以後の
    /// 比較が実際とずれた基準で行われるため。
    /// </summary>
    public void Delete(string stateKey)
    {
        var path = PathOf(stateKey);
        if (!File.Exists(path)) return;
        using var _ = AcquireLock(path);
        File.Delete(path);
    }

    /// <summary>
    /// マージ結果でローカルを置き換えようとしていることを記録する。置き換えた後に
    /// 要約と同期履歴を保存し終えるまで残し、途中で止まったことを次回に分かるようにする。
    /// 記録が残っている間、ローカルには未 Push の変更があるものとして扱う。
    /// </summary>
    public void MarkMergePending(string stateKey)
    {
        Directory.CreateDirectory(RootDirectory);
        File.WriteAllText(PendingPathOf(stateKey), DateTimeOffset.Now.ToString("O"));
    }

    /// <summary>
    /// 置き換えの直前の記録を印のファイルに書く。印は立ったままになる。置き換えた後、
    /// 要約と同期履歴を保存する前に止まっても、次回に祖先を復旧できるようにする。
    /// </summary>
    public void SavePendingReplace(string stateKey, MergePendingState pending)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = PendingPathOf(stateKey);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(pending, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>印のファイルに置き換えの直前の記録があれば返す。印だけか、読めなければ null。</summary>
    public MergePendingState? LoadPendingReplace(string stateKey)
    {
        var path = PendingPathOf(stateKey);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            return text.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<MergePendingState>(text, JsonOptions) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void ClearMergePending(string stateKey)
    {
        var path = PendingPathOf(stateKey);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool IsMergePending(string stateKey) => File.Exists(PendingPathOf(stateKey));

    private string PendingPathOf(string stateKey) => PathOf(stateKey) + ".merging";

    /// <summary>
    /// 最後に送った DB の写しの SHA-256 を控える。送った後に同期履歴を保存する前に
    /// 止まっても、次のマージで同期先が自分の送ったものだと見分けられる。古い値が残って
    /// いても、同期先の写しがそれと同じなら内容も同じなので、祖先にして差し支えない。
    /// </summary>
    public void SavePushedSha256(string stateKey, string sha256)
    {
        Directory.CreateDirectory(RootDirectory);
        File.WriteAllText(PushedPathOf(stateKey), sha256);
    }

    public string? LoadPushedSha256(string stateKey)
    {
        var path = PushedPathOf(stateKey);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? null : text;
    }

    private string PushedPathOf(string stateKey) => PathOf(stateKey) + ".pushed";

    /// <summary>
    /// 要約ごとのロックファイルを排他で開く。他のプロセスが開いていれば、閉じるまで待つ。
    /// ロックファイルは消さずに残す。消すと、待っていた側が別のファイルを作って
    /// 同時にロックを得てしまう。
    /// </summary>
    private static FileStream AcquireLock(string path)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private string PathOf(string stateKey)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stateKey)))[..32].ToLowerInvariant();
        return Path.Combine(RootDirectory, name + ".json");
    }
}

/// <summary>
/// ローカルをマージ結果で置き換える直前の記録。置き換えた後に保存する祖先の要約と、
/// 置き換える前のローカルの本体、置き換え後の本体それぞれの SHA-256 を持つ。次回は、
/// 今の本体がどちらと同じかで、置き換えが済んだかを見分ける。
/// <paramref name="AsideSuffix"/> は置き換えのときに WAL/SHM を脇へ退ける名前の末尾。置き換える前に
/// 止まっていたら、退けた WAL を戻すのに使う。
/// </summary>
public sealed record MergePendingState(
    DatabaseSummary? Summary,
    long RemoteVersion,
    string LocalSha256,
    string MergedSha256,
    string? AsideSuffix = null);

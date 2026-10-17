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

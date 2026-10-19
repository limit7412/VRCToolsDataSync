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
            // 要約を保存できたなら、送った写しの記録は要らない。要約は送った写しか、それより
            // 後の同期先の版のものなので、次のマージの祖先はこちらで足りる。
            DeletePushed(stateKey);
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
    /// 最後に送った DB の写しの記録 (<see cref="PushedRecord"/>) を控える。送った後に要約と
    /// 同期履歴を保存する前に止まっても、次のマージで同期先に自分の送った内容が入っていると
    /// 見分け、送った写しの要約を祖先にできる。要約を保存できれば (<see cref="Save"/>)
    /// この記録は要らなくなるので消す。
    /// </summary>
    public void SavePushed(string stateKey, PushedRecord record)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = PushedPathOf(stateKey);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions));
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
    /// 送信を始めるときの記録を控える。反映済みの記録が残っていれば (前回の Push の後に要約を
    /// 保存できていない)、新しい記録の <see cref="PushedRecord.Previous"/> に抱えて残す。
    /// 上書きして消すと、この送信が反映されなかったときに前の記録を失い、同期先が前回の版の
    /// 先へ進んだ次のマージで古い祖先を使って送った変更を二重に足す。
    /// </summary>
    public void BeginPushed(string stateKey, PushedRecord record)
    {
        var previous = LoadPushed(stateKey) switch
        {
            { Committed: true } committed => committed,
            { Committed: false, Previous: { } carried } => carried,
            _ => null,
        };
        SavePushed(stateKey, record with { Previous = previous });
    }

    /// <summary>
    /// 送った写しの manifest への反映が済んだことを記録に書き足す。記録の写しが
    /// <paramref name="sha256"/> と違えば (別の送信の記録なら) 何もしない。前の記録は、
    /// この版がその内容を取り込んでいるので要らない。
    /// </summary>
    public void MarkPushedCommitted(string stateKey, string sha256, long version)
    {
        var record = LoadPushed(stateKey);
        if (record is null || !string.Equals(record.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) return;
        SavePushed(stateKey, record with { Version = version, Committed = true, Previous = null });
    }

    /// <summary>
    /// 送った写しの記録を読む。無ければ null。以前の版が書いた SHA-256 だけの記録は、
    /// 写しのハッシュだけを持つ記録として読む。
    /// </summary>
    public PushedRecord? LoadPushed(string stateKey)
    {
        var path = PushedPathOf(stateKey);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        if (text.Length == 0) return null;
        if (!text.StartsWith('{')) return new PushedRecord(text);
        try
        {
            var record = JsonSerializer.Deserialize<PushedRecord>(text, JsonOptions);
            return record is { Sha256.Length: > 0 } ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void DeletePushed(string stateKey)
    {
        var path = PushedPathOf(stateKey);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// manifest へ反映できなかった送信の記録を捨てる。反映済みの記録は、要約を保存できる
    /// まで次のマージの祖先に要るので残す。
    /// </summary>
    public void DiscardUncommittedPushed(string stateKey)
    {
        // 捨てる記録が前の反映済みの記録を抱えていれば、それを戻す。
        if (LoadPushed(stateKey) is not { Committed: false } record) return;
        if (record.Previous is { } previous) SavePushed(stateKey, previous);
        else DeletePushed(stateKey);
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
/// <summary>
/// 最後に送った DB の写しの記録。<paramref name="Sha256"/> は写しのハッシュ、
/// <paramref name="Summary"/> はその要約、<paramref name="PlannedVersion"/> は manifest で
/// 取る予定の version。manifest への反映が済めば <paramref name="Committed"/> と、反映した
/// <paramref name="Version"/> を持つ。送った後に要約を保存する前に止まった場合、同期先の写しが
/// この写しそのものか、この版より後の版なら、同期先には送った変更が入っているので、
/// この要約を祖先にする。前回の同期時点の古い要約を祖先にすると、送った変更を同期先と
/// 手元の両方の変更と見て二重に足す。
/// <para>
/// 反映の直後に止まると、反映済みの記録を書けない。そのとき同期先の版が予定の版を越えて
/// いれば、送った変更が入っているかを見分けられないので、マージは中止する。予定の版を
/// 別の PC が取っていれば、反映されなかったと分かるので記録を捨てる。
/// </para>
/// <para>
/// <paramref name="Previous"/> は、この送信を始めたときに残っていた反映済みの記録。前回の
/// Push が反映された後に要約を保存できないまま、次の Push を始めた場合に残る。この送信が
/// 反映されなければ、前の記録が次のマージの祖先に要るので、捨てずに戻す。
/// </para>
/// </summary>
public sealed record PushedRecord(
    string Sha256,
    DatabaseSummary? Summary = null,
    long? Version = null,
    bool Committed = false,
    long? PlannedVersion = null,
    PushedRecord? Previous = null);

public sealed record MergePendingState(
    DatabaseSummary? Summary,
    long RemoteVersion,
    string LocalSha256,
    string MergedSha256,
    string? AsideSuffix = null);

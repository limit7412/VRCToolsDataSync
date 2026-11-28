using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Merge;

namespace VRCToolsDataSync.Core.UseCase;

/// <summary>
/// SQLite の本体の隣にある WAL/SHM を、名前の末尾をずらして脇へ退けた状態。
/// <para>
/// 本体を差し替える前に WAL/SHM を消すと、差し替えに失敗したときに本体は古いまま、
/// WAL にしか無い変更 (本体へまだ書き戻されていない変更) だけが失われる。そこで消さずに
/// 退けておき、差し替えに失敗したら <see cref="Restore"/> で戻し、差し替えが済んだら
/// <see cref="Discard"/> で消す。差し替えた本体の隣へ古い WAL を戻すと、古い WAL が
/// 別内容の本体に対して再生されて DB が壊れるので、差し替えが済んだら戻さない。
/// </para>
/// <para>
/// 退ける名前の末尾は、置き換えの直前の記録 (<see cref="MergePendingState.AsideSuffix"/>)
/// に残す値と同じにする。退けた後に処理ごと止まっても、次回に記録から退けた WAL を探せる。
/// </para>
/// </summary>
internal sealed class SidecarsAside
{
    private readonly List<(string Original, string Aside)> _moved;
    private readonly ILogger _logger;

    private SidecarsAside(List<(string Original, string Aside)> moved, ILogger logger)
    {
        _moved = moved;
        _logger = logger;
    }

    /// <summary>何も退けていない状態。WAL を退ける必要が無い経路で使う。</summary>
    public static SidecarsAside None(ILogger logger) => new(new List<(string, string)>(), logger);

    /// <summary>
    /// <paramref name="database"/> の WAL/SHM を、名前の末尾に <paramref name="suffix"/> を
    /// 付けて退ける。無いものは飛ばす。途中で失敗したら、それまでに退けたものを戻してから
    /// 例外を投げるので、失敗したときは何も退けていない状態に戻る。
    /// </summary>
    public static SidecarsAside MoveAside(string database, string suffix, ILogger logger)
    {
        var aside = new SidecarsAside(new List<(string, string)>(), logger);
        try
        {
            foreach (var path in VrcxSyncService.SidecarsOf(database))
            {
                if (!File.Exists(path)) continue;
                var target = path + suffix;
                File.Move(path, target);
                aside._moved.Add((path, target));
            }
        }
        catch
        {
            aside.Restore();
            throw;
        }
        return aside;
    }

    /// <summary>
    /// 退けたファイルを元の名前へ戻す。本体を差し替えずに止まったときに呼ぶ。
    /// 戻せなかったファイルは、手で戻せるよう場所をログに残してそのまま置く。
    /// </summary>
    public void Restore()
    {
        foreach (var (original, aside) in _moved)
        {
            try
            {
                File.Move(aside, original, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "退けたファイルを戻せませんでした。{Aside} を {Original} へ手で戻してください", aside, original);
            }
        }
        _moved.Clear();
    }

    /// <summary>退けたファイルを消す。本体を差し替えた後か、WAL を捨てると決めた後に呼ぶ。</summary>
    public void Discard()
    {
        foreach (var (_, aside) in _moved)
        {
            try { File.Delete(aside); } catch { /* best-effort cleanup */ }
        }
        _moved.Clear();
    }
}

using System.Diagnostics;

namespace VRCToolsDataSync.Core.Sync;

public sealed class RunningProcessException : InvalidOperationException
{
    public IReadOnlyList<string> ProcessNames { get; }

    public RunningProcessException(IReadOnlyList<string> processNames)
        : base($"同期対象のプロセスが実行中です: {string.Join(", ", processNames)}")
    {
        ProcessNames = processNames;
    }
}

public static class ProcessGuard
{
    // Process.GetProcessesByName は実行ファイル名から拡張子を除いた値で照合する
    // (例: "VRCX.exe" → "VRCX")。配布形態によって実行ファイル名が変わる可能性が
    // あるため、想定されうる候補をすべて列挙してどれか1つでもヒットすれば
    // 起動中と判定する。Issue #2 (P1): VRC Friend Connect の実体が
    // "VRCFriendConnect.exe" だった場合に AutoPush が発火しない件への対策。
    public static readonly IReadOnlyList<string> VrcxProcessNames = new[] { "VRCX" };
    public static readonly IReadOnlyList<string> FriendConnectProcessNames = new[]
    {
        "VRC Friend Connect",
        "VRCFriendConnect",
    };

    public static IReadOnlyList<string> FindRunning(IEnumerable<string> processNames)
    {
        var hits = new List<string>();
        foreach (var name in processNames)
        {
            // Process.GetProcessesByName が返す配列の各要素はネイティブハンドルを
            // 持っているため、ヒット有無を確認したら必ず Dispose する。FindRunning は
            // ポーリング経路で多用されるため、ここでリークすると蓄積する。
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    hits.Add(name);
                }
            }
            finally
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { /* best-effort */ }
                }
            }
        }
        return hits;
    }

    public static void EnsureNotRunning(IEnumerable<string> processNames)
    {
        var hits = FindRunning(processNames);
        if (hits.Count > 0)
        {
            throw new RunningProcessException(hits);
        }
    }
}

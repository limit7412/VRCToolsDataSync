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
    public static readonly IReadOnlyList<string> VrcxProcessNames = new[] { "VRCX" };
    public static readonly IReadOnlyList<string> FriendConnectProcessNames = new[] { "VRC Friend Connect" };

    public static IReadOnlyList<string> FindRunning(IEnumerable<string> processNames)
    {
        var hits = new List<string>();
        foreach (var name in processNames)
        {
            if (Process.GetProcessesByName(name).Length > 0)
            {
                hits.Add(name);
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

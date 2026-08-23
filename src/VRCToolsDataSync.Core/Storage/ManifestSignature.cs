namespace VRCToolsDataSync.Core.Storage;

/// <summary>
/// manifest の「中身が変わったか」を表す指紋。監視側が同じ内容で二重に通知しない
/// ための比較にだけ使う。
/// </summary>
internal static class ManifestSignature
{
    /// <summary>
    /// ETag を出す同期先はそれを使い、出さない同期先ではツールごとの version の組で
    /// 代用する。version は Push のたびに増えるので、これが同じなら通知すべき新しい
    /// 更新は無い。
    /// </summary>
    public static string Build(ManifestSnapshot snapshot)
    {
        if (snapshot.VersionTag is { Length: > 0 } tag)
        {
            return "tag:" + tag;
        }
        var versions = snapshot.Manifest.Tools
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.Version}");
        return "versions:" + string.Join(",", versions);
    }
}

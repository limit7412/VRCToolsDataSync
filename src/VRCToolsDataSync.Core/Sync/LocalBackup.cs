namespace VRCToolsDataSync.Core.Sync;

public sealed class LocalBackup
{
    public string RootDirectory { get; }
    public int RetainGenerations { get; }

    public LocalBackup(string? rootDirectory = null, int retainGenerations = 10)
    {
        RootDirectory = rootDirectory ?? DefaultRoot();
        RetainGenerations = retainGenerations;
    }

    public static string DefaultRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCToolsDataSync",
            "backup");
    }

    public string CreateSnapshot(string toolKey, IEnumerable<string> filesToBackup, IEnumerable<string>? directoriesToBackup = null)
    {
        // 同一ツールに対して 1 秒以内に複数回 Pull が走るケース (CLI 二重起動、
        // 自動通知からの連続承認 Pull など) で世代ディレクトリが衝突しないよう、
        // ミリ秒 + GUID 8 桁を含める。Prune の OrderByDescending(Name) で
        // 時系列が壊れないよう、ソート可能な辞書順を維持する点に注意。
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var destRoot = Path.Combine(RootDirectory, toolKey, timestamp);
        Directory.CreateDirectory(destRoot);

        foreach (var file in filesToBackup)
        {
            if (!File.Exists(file)) continue;
            var destFile = Path.Combine(destRoot, Path.GetFileName(file));
            AtomicFile.Copy(file, destFile, overwrite: true);
        }

        if (directoriesToBackup is not null)
        {
            foreach (var dir in directoriesToBackup)
            {
                if (!Directory.Exists(dir)) continue;
                var destDir = Path.Combine(destRoot, Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)));
                AtomicFile.CopyDirectory(dir, destDir, overwrite: true);
            }
        }

        Prune(toolKey);
        return destRoot;
    }

    public void Prune(string toolKey)
    {
        var toolRoot = Path.Combine(RootDirectory, toolKey);
        if (!Directory.Exists(toolRoot)) return;

        var generations = Directory.GetDirectories(toolRoot)
            .Select(p => new DirectoryInfo(p))
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var old in generations.Skip(RetainGenerations))
        {
            try
            {
                old.Delete(recursive: true);
            }
            catch
            {
                // best-effort: 古いバックアップの掃除に失敗しても続行
            }
        }
    }
}

namespace VRCToolsDataSync.Core.Sync;

public static class AtomicFile
{
    public static void Copy(string source, string destination, bool overwrite = true)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("コピー元ファイルが見つかりません", source);
        }

        var destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        var tmp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, tmp, overwrite: true);
            if (File.Exists(destination))
            {
                if (!overwrite)
                {
                    throw new IOException($"宛先ファイルが既に存在します: {destination}");
                }
                File.Replace(tmp, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, destination);
            }
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
        }
    }

    public static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite = true)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"コピー元ディレクトリが見つかりません: {sourceDir}");
        }

        Directory.CreateDirectory(destinationDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            var destFile = Path.Combine(destinationDir, relative);
            Copy(sourceFile, destFile, overwrite);
        }
    }
}

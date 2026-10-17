namespace VRCToolsDataSync.Core.Infra;

public sealed class VrcxPaths
{
    public string RootDirectory { get; }
    public string SqliteFile => Path.Combine(RootDirectory, "VRCX.sqlite3");
    public string SettingsJsonFile => Path.Combine(RootDirectory, "VRCX.json");

    public VrcxPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public static VrcxPaths Default()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCX");
        return new VrcxPaths(root);
    }

    public bool Exists() => Directory.Exists(RootDirectory) && File.Exists(SqliteFile);

    // VRCX の既知のインストール先候補を順に試す。null を返した場合、GUI 側で
    // 「実行ファイルを選択」のダイアログに誘導する想定。雑にデフォルトパスを
    // 決め打ちで返してしまうと、ユーザが起動を期待していないバイナリを動かす
    // 危険があるため、見つからなければ null を返す。
    public static string? TryFindExecutable()
    {
        // 標準的な VRCX インストール先は %LocalAppData%\Programs\VRCX。
        // Roaming から相対遡行する経路はフォルダリダイレクト環境で誤動作の
        // 可能性があるため採用しない。
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "VRCX", "VRCX.exe");
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            return Path.GetFullPath(path);
        }
        return null;
    }
}

public sealed class FriendConnectPaths
{
    public string RootDirectory { get; }
    public string DbDirectory => Path.Combine(RootDirectory, "db");

    /// <summary>
    /// 旧版の DB。v1.1 より前の Friend Connect が使っていたもので、現行版は書き込まない。
    /// 古くから使っている PC にだけ残っているので、同期では任意のファイルとして扱う。
    /// </summary>
    public string DbFile => Path.Combine(DbDirectory, "db.sqlite");

    /// <summary>現行の Friend Connect が使っている本体 DB。同期に必須。</summary>
    public string DbV11File => Path.Combine(DbDirectory, "db_1.1.sqlite");

    public string NotesDirectory => Path.Combine(RootDirectory, "notes");

    /// <summary>写真のサムネイル。DB の写真の記録と対応している。</summary>
    public string ThumbnailsDirectory => Path.Combine(RootDirectory, "thumbnails");

    public string ConfigJsonFile => Path.Combine(RootDirectory, "config.json");

    public FriendConnectPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public static FriendConnectPaths Default()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRC Friend Connect");
        return new FriendConnectPaths(root);
    }

    public bool Exists() => Directory.Exists(RootDirectory);

    // 実行ファイルの既知の置き場所を順に試す。見つからなければ null を返し、
    // GUI 側で「実行ファイルを選択」に誘導する (VRCX と同じ考え方)。
    // - インストーラ版: %LocalAppData%\Programs\vrc-friend-connect\VRC Friend Connect.exe
    //   BOOTH で配布されているのはこの形で、実機でもここに入っていた。
    // - Steam 配下: 以前はここを前提にしていた。配信を確認できていないが、
    //   探しても害は無いので候補に残す。追加ライブラリ (libraryfolders.vdf) は見ない。
    // 実行ファイル名候補は ProcessGuard.FriendConnectProcessNames と揃える。
    public static string? TryFindExecutable()
    {
        var exeNames = new[] { "VRC Friend Connect.exe", "VRCFriendConnect.exe" };
        foreach (var dir in CandidateInstallDirectories())
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var exe in exeNames)
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateInstallDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "vrc-friend-connect");
        }
        foreach (var programFiles in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            if (string.IsNullOrEmpty(programFiles)) continue;
            yield return Path.Combine(programFiles, "Steam", "steamapps", "common", "VRC Friend Connect");
        }
    }
}

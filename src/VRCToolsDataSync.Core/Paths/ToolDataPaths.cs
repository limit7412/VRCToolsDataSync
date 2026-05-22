namespace VRCToolsDataSync.Core.Paths;

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
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "VRCX", "VRCX.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "..", "Local", "Programs", "VRCX", "VRCX.exe"),
        };
        foreach (var path in candidates)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }
        return null;
    }
}

public sealed class FriendConnectPaths
{
    public string RootDirectory { get; }
    public string DbDirectory => Path.Combine(RootDirectory, "db");
    public string DbFile => Path.Combine(DbDirectory, "db.sqlite");
    public string DbV11File => Path.Combine(DbDirectory, "db_1.1.sqlite");
    public string NotesDirectory => Path.Combine(RootDirectory, "notes");
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

    // VRC Friend Connect は Steam 配布のため、Steam ライブラリ配下を探す。
    // - 既定: C:\Program Files (x86)\Steam\steamapps\common\VRC Friend Connect
    // - libraryfolders.vdf を解析して追加ライブラリも見るのは将来の課題。
    //   現状はユーザに「実行ファイルを選択」させる UI を出す前提なので、
    //   既定のパスだけ試して見つからなければ null を返す。
    // 実行ファイル名候補は ProcessGuard.FriendConnectProcessNames と揃える。
    public static string? TryFindExecutable()
    {
        var steamRoots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Steam"),
        };
        var exeNames = new[] { "VRC Friend Connect.exe", "VRCFriendConnect.exe" };
        foreach (var steam in steamRoots)
        {
            if (string.IsNullOrEmpty(steam)) continue;
            var commonDir = Path.Combine(steam, "steamapps", "common", "VRC Friend Connect");
            foreach (var exe in exeNames)
            {
                var full = Path.Combine(commonDir, exe);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
        }
        return null;
    }
}

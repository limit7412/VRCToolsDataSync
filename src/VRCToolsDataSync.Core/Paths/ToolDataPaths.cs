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
}

public sealed class FriendConnectPaths
{
    public string RootDirectory { get; }
    public string DbFile => Path.Combine(RootDirectory, "db.sqlite");
    public string DbV11File => Path.Combine(RootDirectory, "db_1.1.sqlite");
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
}

using Microsoft.Data.Sqlite;

namespace VRCToolsDataSync.Core.Sync;

public static class SqliteSnapshot
{
    public static void Create(string sourceDbPath, string destinationPath)
    {
        if (!File.Exists(sourceDbPath))
        {
            throw new FileNotFoundException("ソースのSQLiteファイルが見つかりません", sourceDbPath);
        }

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $dest";
        command.Parameters.AddWithValue("$dest", destinationPath);
        command.ExecuteNonQuery();
    }
}

using Microsoft.Data.Sqlite;

namespace VRCToolsDataSync.Core.Infra;

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
            // 接続をプールに残すと、Windows では写し元のファイルが開いたままになり、
            // 直後に同じファイルを置き換える処理 (マージ結果の書き戻しなど) が失敗する。
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $dest";
        command.Parameters.AddWithValue("$dest", destinationPath);
        command.ExecuteNonQuery();
    }
}

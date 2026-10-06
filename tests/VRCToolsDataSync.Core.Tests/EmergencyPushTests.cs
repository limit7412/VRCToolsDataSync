using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Domain;
using VRCToolsDataSync.Core.Infra;
using VRCToolsDataSync.Core.UseCase;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 非常用の強制 Push の性質を固定する。通常の Push は同期先の記録を信用して送信を
/// 省くため、実体の中身が壊れると Push し直しても直らない。非常用の強制 Push は
/// そこから抜け出す手段なので、「記録を信用しない」ことを確かめる。
/// </summary>
public sealed class EmergencyPushTests : IDisposable
{
    private const string SettingsKey = "vrcx/latest.json";

    private readonly string _root;

    public EmergencyPushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vrctds-emergency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // 接続プールが SQLite のファイルを掴んだままだと消せない。
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>VRCX のデータ一式 (SQLite と設定ファイル) を用意する。</summary>
    private VrcxSyncService CreateVrcx(string settingsJson)
    {
        var paths = new VrcxPaths(_root);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteFile,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS t (v TEXT); INSERT INTO t VALUES ('x');";
            command.ExecuteNonQuery();
        }
        File.WriteAllText(paths.SettingsJsonFile, settingsJson);
        return new VrcxSyncService(paths, new LocalBackup(Path.Combine(_root, "backup")));
    }

    private static SyncResult Push(
        ISyncService service, ISyncStorage storage, long? lastPulled, bool reuploadAll = false)
        => service.Push(new PushOptions
        {
            Storage = storage,
            MachineName = "test",
            ForceOverwriteOnConflict = false,
            ReuploadAll = reuploadAll,
            LastPulledVersion = lastPulled,
        });

    private static ManifestFile RecordOf(ISyncStorage storage, string relativePath)
        => new ManifestStore(storage).Load().Tools[VrcxSyncService.Key].Files
            .Single(f => f.RelativePath == relativePath);

    [Fact(DisplayName = "壊れた実体は通常の Push では直らず、非常用の強制 Push で直る")]
    public void EmergencyPushRepairsACorruptedBlob()
    {
        var storage = new FakeSyncStorage();
        var service = CreateVrcx("{\"a\":1}");
        var first = Push(service, storage, lastPulled: null);
        Assert.Equal(SyncOutcome.Success, first.Outcome);

        // 同期先の実体だけが壊れた状態を作る。manifest の記録はそのまま。
        var record = RecordOf(storage, SettingsKey);
        storage.Seed(record.BlobKey!, "broken");

        // 通常の Push は記録と実在だけを見て送信を省くので、壊れたまま残る。
        var normal = Push(service, storage, lastPulled: first.RemoteVersion);
        Assert.Equal(SyncOutcome.Success, normal.Outcome);
        Assert.Equal("broken", storage.TextOf(record.BlobKey!));

        var emergency = Push(service, storage, lastPulled: first.RemoteVersion, reuploadAll: true);

        Assert.Equal(SyncOutcome.Success, emergency.Outcome);
        Assert.Equal("{\"a\":1}", storage.TextOf(record.BlobKey!));
        Assert.Contains(SettingsKey, emergency.AffectedFiles);
        Assert.Contains("CommitReplacing:" + record.BlobKey, storage.Calls);
    }

    [Fact(DisplayName = "内容が同じでも version を進める")]
    public void EmergencyPushAdvancesTheVersionEvenWhenNothingChanged()
    {
        // 進めないと、壊れた実体の取得に失敗した他の PC が取り直しに来ない。
        var storage = new FakeSyncStorage();
        var service = CreateVrcx("{}");
        var first = Push(service, storage, lastPulled: null);

        var emergency = Push(service, storage, lastPulled: first.RemoteVersion, reuploadAll: true);

        Assert.Equal(SyncOutcome.Success, emergency.Outcome);
        Assert.Equal(first.RemoteVersion + 1, emergency.RemoteVersion);
    }

    [Fact(DisplayName = "リモートの方が新しくてもコンフリクトにしない")]
    public void EmergencyPushIgnoresANewerRemote()
    {
        var storage = new FakeSyncStorage();
        var service = CreateVrcx("{}");
        Push(service, storage, lastPulled: null);
        Push(CreateVrcx("{\"other\":true}"), storage, lastPulled: 1);

        // 最後に Pull した版 (1) より同期先 (2) が新しい。
        Assert.Equal(SyncOutcome.ConflictDetected, Push(service, storage, lastPulled: 1).Outcome);

        var emergency = Push(service, storage, lastPulled: 1, reuploadAll: true);

        Assert.Equal(SyncOutcome.Success, emergency.Outcome);
        Assert.Equal(3, emergency.RemoteVersion);
    }

    [Fact(DisplayName = "Push の途中で他の PC が更新しても止めずに上書きする")]
    public void EmergencyPushOverwritesAConcurrentUpdate()
    {
        // 全ファイルを置き換えてから manifest を書くので、途中で他の PC が更新して
        // いても manifest と実データはずれない。止めると非常用の意味が無い。
        var storage = new FakeSyncStorage();
        var service = CreateVrcx("{}");
        Push(service, storage, lastPulled: null);

        var interrupted = false;
        storage.OnBeforeSaveManifest = () =>
        {
            if (interrupted) return;
            interrupted = true;
            storage.OnBeforeSaveManifest = null;
            new ManifestStore(storage).UpdateToolEntry(VrcxSyncService.Key, 1, version => new ToolManifestEntry
            {
                Version = version,
                MachineName = "other",
                UpdatedAt = DateTimeOffset.Now,
            });
        };

        var emergency = Push(service, storage, lastPulled: 1, reuploadAll: true);

        Assert.Equal(SyncOutcome.Success, emergency.Outcome);
        var entry = new ManifestStore(storage).Load().Tools[VrcxSyncService.Key];
        Assert.Equal("test", entry.MachineName);
        Assert.Equal(3, entry.Version);
    }
}

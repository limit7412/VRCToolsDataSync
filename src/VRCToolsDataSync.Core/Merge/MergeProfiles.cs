using Microsoft.Data.Sqlite;

namespace VRCToolsDataSync.Core.Merge;

/// <summary>
/// VRCX と Friend Connect の規則。テーブル定義の出典は次のとおり。
/// <list type="bullet">
/// <item>VRCX: vrcx-team/VRCX の <c>src/services/database/</c> (index.js ほか)。</item>
/// <item>Friend Connect: 実機の <c>db_1.1.sqlite</c> を読み取った調査 (issue #87)。</item>
/// </list>
/// </summary>
public static class MergeProfiles
{
    /// <summary>
    /// VRCX がユーザーごとに付けるテーブル名の接頭辞。VRCX はユーザー ID から <c>-</c> と
    /// <c>_</c> を除いたものを使い、先頭が数字なら <c>_</c> を付ける (index.js の
    /// initUserTables)。今の ID は usr0123abcd... の形だが、古いアカウントの ID は
    /// 8Yw... のような英数字なので、それも合わせる。
    /// </summary>
    private const string UserPrefix = "_?[0-9A-Za-z]+_";

    public static MergeProfile Vrcx { get; } = new()
    {
        ToolKey = "vrcx",
        SupportsMerge = true,
        Rules = new MergeTableRule[]
        {
            // VRChat のログから作る履歴。UNIQUE 制約の列をそのままキーにする。
            new("gamelog_location", MergeTableKind.AppendOnly, new[] { "created_at", "location" }, OrderColumn: "created_at",
                GrowingColumns: new[] { "time" }),
            new("gamelog_join_leave", MergeTableKind.AppendOnly, new[] { "created_at", "type", "display_name" }, OrderColumn: "created_at"),
            new("gamelog_portal_spawn", MergeTableKind.AppendOnly, new[] { "created_at", "display_name" }, OrderColumn: "created_at"),
            new("gamelog_video_play", MergeTableKind.AppendOnly, new[] { "created_at", "video_url" }, OrderColumn: "created_at"),
            new("gamelog_resource_load", MergeTableKind.AppendOnly, new[] { "created_at", "resource_url" }, OrderColumn: "created_at"),
            new("gamelog_event", MergeTableKind.AppendOnly, new[] { "created_at", "data" }, OrderColumn: "created_at"),
            new("gamelog_external", MergeTableKind.AppendOnly, new[] { "created_at", "message" }, OrderColumn: "created_at"),

            // フレンドの動きの履歴。UNIQUE 制約が無いので、キーは本ツールで決める。
            new(UserPrefix + "feed_gps", MergeTableKind.AppendOnly, new[] { "created_at", "user_id", "location" }, OrderColumn: "created_at"),
            new(UserPrefix + "feed_status", MergeTableKind.AppendOnly, new[] { "created_at", "user_id", "status" }, OrderColumn: "created_at"),
            new(UserPrefix + "feed_bio", MergeTableKind.AppendOnly, new[] { "created_at", "user_id" }, OrderColumn: "created_at"),
            new(UserPrefix + "feed_avatar", MergeTableKind.AppendOnly, new[] { "created_at", "user_id", "avatar_name" }, OrderColumn: "created_at"),
            new(UserPrefix + "feed_online_offline", MergeTableKind.AppendOnly, new[] { "created_at", "user_id", "type" }, OrderColumn: "created_at"),
            new(UserPrefix + "friend_log_history", MergeTableKind.AppendOnly, new[] { "created_at", "user_id", "type" }, OrderColumn: "created_at"),
            new(UserPrefix + "notifications", MergeTableKind.AppendOnly, new[] { "id" },
                GrowingColumns: new[] { "expired" }),

            // 新しい形式の通知。VRCX は同じ id の行を上書きし、既読や期限を書き換え、
            // 利用者の操作で消す。追加専用ではないので、キー付きとして扱う。
            // 既読 (seen) と期限切れ (expires_at を現在時刻に早める) は updated_at を
            // 変えないので、両方が書き換えていたら、どちらの行を残してもこの 2 列は合成する。
            new(UserPrefix + "notifications_v2", MergeTableKind.Keyed, new[] { "id" }, EditedAtColumn: "updated_at",
                GrowingColumns: new[] { "seen" }, ShrinkingColumns: new[] { "expires_at" }),

            // 着用したアバターの履歴。VRCX はアバターごとに 1 行を持ち、着け直すと
            // created_at を書き換え、着用時間 (time) は累計に足していく。両方の PC で
            // 書き換えていたら、created_at は新しい方を残し、time は双方の増分を足す。
            new(UserPrefix + "avatar_history", MergeTableKind.Keyed, new[] { "avatar_id" }, EditedAtColumn: "created_at",
                GrowingColumns: new[] { "created_at" }, CounterColumns: new[] { "time" }),

            // ユーザーが手で編集するもの。
            new("memos", MergeTableKind.Keyed, new[] { "user_id" }, EditedAtColumn: "edited_at"),
            // VRChat 側に保存するユーザーのメモの写し。VRCX は addUserNote で同じ user_id の行を
            // 上書きし、deleteUserNote で消す (memos.js)。created_at は VRChat が付けた時刻。
            new(UserPrefix + "notes", MergeTableKind.Keyed, new[] { "user_id" }, EditedAtColumn: "created_at"),
            new("world_memos", MergeTableKind.Keyed, new[] { "world_id" }, EditedAtColumn: "edited_at"),
            new("avatar_memos", MergeTableKind.Keyed, new[] { "avatar_id" }, EditedAtColumn: "edited_at"),
            new("avatar_tags", MergeTableKind.Keyed, new[] { "avatar_id", "tag" }),
            new("favorite_world", MergeTableKind.Keyed, new[] { "world_id", "group_name" }, ValueColumns: Array.Empty<string>()),
            new("favorite_avatar", MergeTableKind.Keyed, new[] { "avatar_id", "group_name" }, ValueColumns: Array.Empty<string>()),
            new("favorite_friend", MergeTableKind.Keyed, new[] { "user_id", "group_name" }, ValueColumns: Array.Empty<string>()),
            new("favorite_print", MergeTableKind.Keyed, new[] { "print_id" }, ValueColumns: Array.Empty<string>()),

            // ここに無いテーブル (cache_*、activity_*_v2、mutual_graph_* など) は
            // 派生キャッシュか VRChat 側の写しで、マージでは同期先の値を使う。
        },
    };

    /// <summary>
    /// 本ツールが中身を読むことを確かめた Friend Connect のスキーマの版
    /// (<c>_prisma_migrations</c> の最新のマイグレーション名)。Friend Connect が
    /// 更新されてここに無い版になったら、規則を見直してから追加する。
    /// </summary>
    public static IReadOnlyCollection<string> KnownFriendConnectMigrations { get; } = new[]
    {
        "20260930120000_mcp_memory_indexes",
    };

    /// <summary>Friend Connect が自動で書き換える列。変化の検知から外す。</summary>
    private static readonly string[] FriendConnectAutoColumns = { "id", "createdAt", "updatedAt", "lastUpdateAt" };

    public static MergeProfile FriendConnect { get; } = new()
    {
        ToolKey = "friend-connect",
        Rules = new MergeTableRule[]
        {
            // User は VRChat のユーザー情報のキャッシュで、ほとんどの列は
            // Friend Connect が書き換える。ユーザーが書くのはメモだけ。
            new("User", MergeTableKind.Keyed, new[] { "apiId" }, ValueColumns: new[] { "note" },
                Filter: "note IS NOT NULL AND note <> ''"),

            // 以下は列の意味をまだ確かめていないので、マージはせず変化だけを見る。
            // Friend Connect への行単位の書き込みは、開発者への確認を待ってから扱う。
            new("Tag", MergeTableKind.Watched, Array.Empty<string>(), IgnoredColumns: FriendConnectAutoColumns),
            new("UserTag", MergeTableKind.Watched, Array.Empty<string>(), IgnoredColumns: FriendConnectAutoColumns),
            new("FavoriteLocal", MergeTableKind.Watched, Array.Empty<string>(), IgnoredColumns: FriendConnectAutoColumns),
            new("FavoriteLocalGroup", MergeTableKind.Watched, Array.Empty<string>(), IgnoredColumns: FriendConnectAutoColumns),
            new("UserNotificationSetting", MergeTableKind.Watched, Array.Empty<string>(), IgnoredColumns: FriendConnectAutoColumns),

            // 履歴 (Location、UserStatus など) は当面マージしない。自然キーが無く、
            // 外部キーが不安定な整数 id を指しているため。
        },
        CheckSchema = CheckFriendConnectSchema,
    };

    private static (string? Version, string? Reason) CheckFriendConnectSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT migration_name FROM _prisma_migrations " +
            "WHERE finished_at IS NOT NULL AND rolled_back_at IS NULL " +
            "ORDER BY migration_name DESC LIMIT 1";
        string? latest;
        try
        {
            latest = command.ExecuteScalar() as string;
        }
        catch (SqliteException ex)
        {
            return (null, $"スキーマの版を読めません: {ex.Message}");
        }
        if (latest is null)
        {
            return (null, "スキーマの版が記録されていません");
        }
        return KnownFriendConnectMigrations.Contains(latest)
            ? (latest, null)
            : (null, $"本ツールが確かめていないスキーマの版です: {latest}");
    }
}

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

    /// <summary>Friend Connect の User を PC をまたいで見分ける列。VRChat のユーザー ID。</summary>
    private static readonly ColumnReference UserByApiId = new("userId", "User", new[] { "apiId" });

    /// <summary>Friend Connect の World を PC をまたいで見分ける列。VRChat のワールド ID。</summary>
    private static readonly ColumnReference WorldByApiId = new("worldId", "World", new[] { "apiId" });

    /// <summary>Friend Connect の Tag を見分ける列。名前は種類 (user / world) ごとに付く。</summary>
    private static readonly ColumnReference TagByName = new("tagId", "Tag", new[] { "type", "name" });

    /// <summary>
    /// Friend Connect の FavoriteLocal がお気に入りのグループを指す列。groupKey は文字列で PC 間で
    /// 同じなので付け替えは要らないが、参照として持つことで、グループの行を消す前にそのグループへ
    /// 同期先だけで足されたお気に入りが無いかを確かめられる。
    /// </summary>
    private static readonly ColumnReference GroupByKey = new("groupKey", "FavoriteLocalGroup", new[] { "groupKey" }, IdColumn: "groupKey");

    /// <summary>
    /// Friend Connect の規則。すべてのテーブルの id が PC ごとに別の値を取る
    /// (AUTOINCREMENT で、消して入れ直す更新もある) ので、行は自然キーで突き合わせ、
    /// 別のテーブルを id で指す列は <see cref="ColumnReference"/> で自然キーに置き換える。
    /// 比べる列は手で編集するものに絞り、id と Friend Connect が自動で書き換える
    /// createdAt / updatedAt は比べない。編集時刻の列は信用できない (起動しているだけで
    /// updatedAt が書き換わる) ので持たず、両方で編集されていたら同期先の内容を残す。
    /// </summary>
    public static MergeProfile FriendConnect { get; } = new()
    {
        ToolKey = "friend-connect",
        SupportsMerge = true,
        Rules = new MergeTableRule[]
        {
            // 参照される側を先に並べる。マージはこの順に合わせる。

            // User は VRChat のユーザー情報のキャッシュで、ほとんどの列は Friend Connect が
            // 書き換える。ユーザーが書くのはメモ (note) だけ。履歴やタグがこの行を id で
            // 指すので、入れ直さずに書き換える。メモを消しても行は残るので、消えたキーは
            // 行の削除ではなく note の消去。同期先に無いユーザーのメモは User の行ごと写すが、
            // 最後に見た履歴を指す 3 列は制約の無い id なので消す (Friend Connect が埋め直す)。
            new("User", MergeTableKind.Keyed, new[] { "apiId" }, ValueColumns: new[] { "note" },
                Filter: "note IS NOT NULL AND note <> ''", UpdateInPlace: true, ClearOnDelete: true,
                ClearedColumns: new[] { "lastUserAvatarId", "lastLocationId", "lastUserStatusId" }),
            // タグの定義。UserTag / WorldTag が id で指すので、入れ直さずに書き換える。
            // 名前を変えると別のタグとして消して入れ直すことになる。旧名の行の削除は、参照する
            // テーブルを合わせた後まで遅らせ、同期先だけで付けていた旧名のタグ付けが残っていれば
            // 旧名の行も残す (SqliteMerger の参照される行の削除の扱い)。
            new("Tag", MergeTableKind.Keyed, new[] { "type", "name" }, ValueColumns: new[] { "description", "sort", "isSystem" },
                UpdateInPlace: true),
            new("FavoriteLocalGroup", MergeTableKind.Keyed, new[] { "groupKey" }, ValueColumns: new[] { "type", "name", "sort" },
                UpdateInPlace: true),

            // ユーザーやワールドへのタグ付け。行の有無だけが意味を持つ。
            new("UserTag", MergeTableKind.Keyed, new[] { "userId", "tagId" }, ValueColumns: Array.Empty<string>(),
                References: new[] { UserByApiId, TagByName }),
            new("WorldTag", MergeTableKind.Keyed, new[] { "worldId", "tagId" }, ValueColumns: Array.Empty<string>(),
                References: new[] { WorldByApiId, TagByName }),
            // ローカルのお気に入り。
            new("FavoriteLocal", MergeTableKind.Keyed, new[] { "groupKey", "userId", "worldId" }, ValueColumns: Array.Empty<string>(),
                References: new[] { GroupByKey, UserByApiId, WorldByApiId }),
            // ユーザーごとの通知設定。同じ (ユーザー、種類、通知先) の行が複数ありうるので、
            // キーの行をまとめて比べる。
            new("UserNotificationSetting", MergeTableKind.Keyed, new[] { "userId", "type", "notifyAPI" }, ValueColumns: new[] { "value" },
                References: new[] { UserByApiId }),
            // 写真のお気に入り。撮影時刻 (UNIQUE) がキーで、行の有無だけが意味を持つ。
            new("PhotoFavorite", MergeTableKind.Keyed, new[] { "shotAt" }, ValueColumns: Array.Empty<string>()),
            // ワールドへのメモ。1 ワールドに複数行を持てるので、作成時刻 (作成時に 1 回だけ書かれる)
            // をキーに含める。ワールドが同期先に無ければ写す。
            new("WorldNote", MergeTableKind.Keyed, new[] { "worldId", "createdAt" }, ValueColumns: new[] { "note" },
                References: new[] { WorldByApiId }),

            // Friend Connect の稼働区間。起動 1 回が 1 行で、動作中は endAt と seconds が毎分伸びる。
            // Friend Connect はこの行を消さないので、祖先の無い最初のマージでも和集合を取る。
            // 履歴の 2 台同時起動の期間を見分けるのに使う。
            new("ScanRuntime", MergeTableKind.AppendOnly, new[] { "startAt" }, GrowingColumns: new[] { "endAt", "seconds" },
                UnionWithoutAncestor: true),

            // 履歴 (Location、UserStatus、UserAvatar、NUserHistory、LocationMaster)、写真の一覧 (Photo)
            // と集計 (UserConnection、UserWorldVisit、UserAnalytics) はまだマージしない。履歴は
            // 整数 id の付け替えが追加専用に無く、集計は足すと二重に数える。ここに無いテーブルは
            // 同期先の内容がそのまま残る。
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

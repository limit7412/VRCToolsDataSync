using Microsoft.Data.Sqlite;
using VRCToolsDataSync.Core.Merge;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// Friend Connect の db_1.1.sqlite のうち、マージの規則に関わるテーブルの定義。本体に同梱されて
/// いる Prisma のテンプレート DB (resources/prisma/db.sqlite) から、列と制約をそのまま写した。
/// 外部キーは整数 id を指し、User を消すとタグ付けやお気に入りが連鎖して消える。
/// </summary>
internal static class FriendConnectSchema
{
    public const string Ddl = """
        CREATE TABLE "_prisma_migrations" (
            "id" TEXT PRIMARY KEY NOT NULL,
            "checksum" TEXT NOT NULL,
            "finished_at" DATETIME,
            "migration_name" TEXT NOT NULL,
            "logs" TEXT,
            "rolled_back_at" DATETIME,
            "started_at" DATETIME NOT NULL DEFAULT current_timestamp,
            "applied_steps_count" INTEGER UNSIGNED NOT NULL DEFAULT 0
        );
        CREATE TABLE "User" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "apiId" TEXT NOT NULL,
            "displayName" TEXT NOT NULL,
            "location" TEXT,
            "statusDescription" TEXT NOT NULL,
            "status" TEXT NOT NULL,
            "bio" TEXT,
            "currentAvatarImageUrl" TEXT NOT NULL,
            "currentAvatarThumbnailImageUrl" TEXT NOT NULL,
            "developerType" TEXT NOT NULL,
            "fallbackAvatar" TEXT NOT NULL,
            "isFriend" BOOLEAN NOT NULL,
            "last_platform" TEXT NOT NULL,
            "profilePicOverride" TEXT,
            "tags" TEXT,
            "userIcon" TEXT NOT NULL,
            "username" TEXT,
            "friendKey" TEXT,
            "isOwner" BOOLEAN NOT NULL DEFAULT false,
            "isOnline" BOOLEAN NOT NULL DEFAULT false,
            "isPublic" BOOLEAN NOT NULL DEFAULT false,
            "lastUserAvatarId" INTEGER,
            "lastLocationId" INTEGER,
            "lastUserStatusId" INTEGER,
            "score" INTEGER NOT NULL DEFAULT 0,
            "scoreLastUpdateAt" DATETIME,
            "lastContactAt" DATETIME,
            "lastUpdateAt" DATETIME NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        , "note" TEXT);
        CREATE INDEX "User_apiId_idx" ON "User"("apiId");
        CREATE TABLE "World" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "apiId" TEXT NOT NULL,
            "name" TEXT NOT NULL DEFAULT '',
            "authorName" TEXT NOT NULL DEFAULT '',
            "lastUpdateAt" DATETIME NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        );
        CREATE UNIQUE INDEX "World_apiId_key" ON "World"("apiId");
        CREATE TABLE "Tag" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "type" TEXT NOT NULL DEFAULT 'user',
            "name" TEXT NOT NULL,
            "description" TEXT,
            "sort" INTEGER NOT NULL DEFAULT 0,
            "isSystem" BOOLEAN NOT NULL DEFAULT false,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        );
        CREATE TABLE "UserTag" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "tagId" INTEGER NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "UserTag_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "UserTag_tagId_fkey" FOREIGN KEY ("tagId") REFERENCES "Tag" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE UNIQUE INDEX "UserTag_userId_tagId_key" ON "UserTag"("userId", "tagId");
        CREATE TABLE "WorldTag" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "worldId" INTEGER NOT NULL,
            "tagId" INTEGER NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "WorldTag_worldId_fkey" FOREIGN KEY ("worldId") REFERENCES "World" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "WorldTag_tagId_fkey" FOREIGN KEY ("tagId") REFERENCES "Tag" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE UNIQUE INDEX "WorldTag_worldId_tagId_key" ON "WorldTag"("worldId", "tagId");
        CREATE TABLE "FavoriteLocalGroup" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "type" TEXT NOT NULL DEFAULT 'friend',
            "groupKey" TEXT NOT NULL,
            "name" TEXT NOT NULL,
            "sort" INTEGER NOT NULL DEFAULT 0,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        );
        CREATE UNIQUE INDEX "FavoriteLocalGroup_groupKey_key" ON "FavoriteLocalGroup"("groupKey");
        CREATE TABLE "FavoriteLocal" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "groupKey" TEXT NOT NULL,
            "userId" INTEGER,
            "worldId" INTEGER,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "FavoriteLocal_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "FavoriteLocal_worldId_fkey" FOREIGN KEY ("worldId") REFERENCES "World" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "FavoriteLocal_groupKey_fkey" FOREIGN KEY ("groupKey") REFERENCES "FavoriteLocalGroup" ("groupKey") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE TABLE "UserNotificationSetting" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "type" TEXT NOT NULL,
            "notifyAPI" TEXT NOT NULL,
            "value" TEXT NOT NULL DEFAULT '',
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "UserNotificationSetting_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE TABLE "Group" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "apiId" TEXT NOT NULL,
            "name" TEXT NOT NULL,
            "shortCode" TEXT NOT NULL,
            "discriminator" TEXT NOT NULL,
            "description" TEXT NOT NULL,
            "ownerId" TEXT NOT NULL,
            "iconId" TEXT NOT NULL,
            "iconUrl" TEXT NOT NULL,
            "bannerId" TEXT NOT NULL,
            "bannerUrl" TEXT NOT NULL,
            "memberCount" INTEGER NOT NULL DEFAULT 0,
            "joinState" TEXT NOT NULL DEFAULT '',
            "lastUpdateAt" DATETIME NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        , "lastLocationUpdateAt" DATETIME);
        CREATE UNIQUE INDEX "Group_apiId_key" ON "Group"("apiId");
        CREATE TABLE "LocationMaster" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "location" TEXT NOT NULL,
            "worldId" INTEGER,
            "instanceNumber" TEXT,
            "instanceId" TEXT,
            "instanceType" INTEGER NOT NULL,
            "ownerId" INTEGER,
            "ownerApiId" TEXT,
            "ownerName" TEXT,
            "groupId" INTEGER,
            "groupApiId" TEXT,
            "nUsers" INTEGER NOT NULL,
            "nUsersFriend" INTEGER NOT NULL DEFAULT 0,
            "capacity" INTEGER NOT NULL,
            "lastUpdateAt" DATETIME,
            "nUserLastUpdateAt" DATETIME,
            CONSTRAINT "LocationMaster_ownerId_fkey" FOREIGN KEY ("ownerId") REFERENCES "User" ("id") ON DELETE SET NULL ON UPDATE CASCADE,
            CONSTRAINT "LocationMaster_worldId_fkey" FOREIGN KEY ("worldId") REFERENCES "World" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "LocationMaster_groupId_fkey" FOREIGN KEY ("groupId") REFERENCES "Group" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE INDEX "LocationMaster_worldId_idx" ON "LocationMaster"("worldId");
        CREATE TABLE "Location" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "locationKey" INTEGER NOT NULL,
            "withOwner" BOOLEAN NOT NULL DEFAULT false,
            "worldId" INTEGER,
            "instanceNumber" TEXT,
            "instanceId" TEXT,
            "instanceType" INTEGER NOT NULL,
            "startAt" DATETIME NOT NULL,
            "endAt" DATETIME NOT NULL,
            "seconds" INTEGER NOT NULL DEFAULT 0,
            CONSTRAINT "Location_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "Location_worldId_fkey" FOREIGN KEY ("worldId") REFERENCES "World" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "Location_locationKey_fkey" FOREIGN KEY ("locationKey") REFERENCES "LocationMaster" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE INDEX "Location_userId_idx" ON "Location"("userId");
        CREATE INDEX "Location_startAt_idx" ON "Location"("startAt");
        CREATE INDEX "Location_locationKey_idx" ON "Location"("locationKey");
        CREATE TABLE "Video" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "dateTime" DATETIME NOT NULL,
            "locationId" INTEGER NOT NULL,
            "url" TEXT NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "Video_locationId_fkey" FOREIGN KEY ("locationId") REFERENCES "Location" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE UNIQUE INDEX "Video_dateTime_key" ON "Video"("dateTime");
        CREATE TABLE "LocationNote" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "locationMasterId" INTEGER NOT NULL,
            "locationId" INTEGER NOT NULL,
            "note" TEXT NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "LocationNote_locationMasterId_fkey" FOREIGN KEY ("locationMasterId") REFERENCES "LocationMaster" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "LocationNote_locationId_fkey" FOREIGN KEY ("locationId") REFERENCES "Location" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE TABLE "Photo" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "locationId" INTEGER,
            "shotAt" DATETIME NOT NULL,
            "source" TEXT NOT NULL,
            "thumbnail" TEXT NOT NULL,
            "hasThumbnail" BOOLEAN NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "Photo_locationId_fkey" FOREIGN KEY ("locationId") REFERENCES "Location" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE UNIQUE INDEX "Photo_shotAt_key" ON "Photo"("shotAt");
        CREATE TABLE "PhotoFavorite" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "shotAt" DATETIME NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        );
        CREATE INDEX "PhotoFavorite_shotAt_idx" ON "PhotoFavorite"("shotAt");
        CREATE UNIQUE INDEX "PhotoFavorite_shotAt_key" ON "PhotoFavorite"("shotAt");
        CREATE TABLE "WorldNote" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "worldId" INTEGER NOT NULL,
            "note" TEXT NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "WorldNote_worldId_fkey" FOREIGN KEY ("worldId") REFERENCES "World" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE INDEX "WorldNote_worldId_idx" ON "WorldNote"("worldId");
        CREATE TABLE "ScanRuntime" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "startAt" DATETIME NOT NULL,
            "endAt" DATETIME NOT NULL,
            "seconds" INTEGER NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        );
        CREATE INDEX "ScanRuntime_startAt_endAt_idx" ON "ScanRuntime"("startAt", "endAt");
        CREATE TABLE "Avatar" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "avatar" TEXT NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL
        );
        CREATE UNIQUE INDEX "Avatar_avatar_key" ON "Avatar"("avatar");
        CREATE INDEX "Avatar_avatar_idx" ON "Avatar"("avatar");
        CREATE TABLE "UserAvatar" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "avatarId" INTEGER NOT NULL,
            "startAt" DATETIME NOT NULL,
            "endAt" DATETIME NOT NULL,
            "seconds" INTEGER NOT NULL DEFAULT 0,
            CONSTRAINT "UserAvatar_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "UserAvatar_avatarId_fkey" FOREIGN KEY ("avatarId") REFERENCES "Avatar" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE INDEX "UserAvatar_userId_idx" ON "UserAvatar"("userId");
        CREATE INDEX "UserAvatar_avatarId_idx" ON "UserAvatar"("avatarId");
        CREATE INDEX "UserAvatar_startAt_endAt_idx" ON "UserAvatar"("startAt", "endAt");
        CREATE TABLE "UserStatus" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "status" INTEGER NOT NULL,
            "statusDescription" TEXT NOT NULL,
            "startAt" DATETIME NOT NULL,
            "endAt" DATETIME NOT NULL,
            "seconds" INTEGER NOT NULL DEFAULT 0,
            CONSTRAINT "UserStatus_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE INDEX "UserStatus_id_userId_status_startAt_endAt_idx" ON "UserStatus"("id", "userId", "status", "startAt", "endAt");
        CREATE TABLE "UserConnection" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "targetUserId" INTEGER NOT NULL,
            "joinACount" INTEGER NOT NULL DEFAULT 0,
            "joinBCount" INTEGER NOT NULL DEFAULT 0,
            "leaveACount" INTEGER NOT NULL DEFAULT 0,
            "leaveBCount" INTEGER NOT NULL DEFAULT 0,
            "stayCount" INTEGER NOT NULL DEFAULT 0,
            "staySeconds" INTEGER NOT NULL DEFAULT 0,
            "staySeconds3Hour" INTEGER NOT NULL DEFAULT 0,
            "staySeconds6Hour" INTEGER NOT NULL DEFAULT 0,
            "staySeconds12Hour" INTEGER NOT NULL DEFAULT 0,
            "staySeconds1Day" INTEGER NOT NULL DEFAULT 0,
            "staySeconds2Day" INTEGER NOT NULL DEFAULT 0,
            "staySeconds3Day" INTEGER NOT NULL DEFAULT 0,
            "lastContactAt" DATETIME,
            "lastUpdateAt" DATETIME NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "UserConnection_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "UserConnection_targetUserId_fkey" FOREIGN KEY ("targetUserId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE INDEX "UserConnection_userId_targetUserId_lastContactAt_idx" ON "UserConnection"("userId", "targetUserId", "lastContactAt");
        CREATE UNIQUE INDEX "UserConnection_userId_targetUserId_key" ON "UserConnection"("userId", "targetUserId");
        CREATE TABLE "UserWorldVisit" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "worldId" INTEGER NOT NULL,
            "stayCount" INTEGER NOT NULL DEFAULT 0,
            "staySeconds" INTEGER NOT NULL DEFAULT 0,
            "stayAvg" INTEGER NOT NULL DEFAULT 0,
            "lastUpdateAt" DATETIME NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "UserWorldVisit_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
            CONSTRAINT "UserWorldVisit_worldId_fkey" FOREIGN KEY ("worldId") REFERENCES "World" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE INDEX "UserWorldVisit_userId_worldId_idx" ON "UserWorldVisit"("userId", "worldId");
        CREATE UNIQUE INDEX "UserWorldVisit_userId_worldId_key" ON "UserWorldVisit"("userId", "worldId");
        CREATE TABLE "UserAnalytics" (
            "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "userId" INTEGER NOT NULL,
            "instanceTypeRate" TEXT NOT NULL,
            "privateRate" REAL NOT NULL,
            "userStatusRate" TEXT NOT NULL,
            "activeHeatmap" TEXT NOT NULL,
            "stayAvgPerLocation" INTEGER NOT NULL,
            "loginAvgPerDay" INTEGER NOT NULL,
            "mostActivityHour" INTEGER NOT NULL,
            "lastUpdateAt" DATETIME NOT NULL,
            "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            "updatedAt" DATETIME NOT NULL,
            CONSTRAINT "UserAnalytics_userId_fkey" FOREIGN KEY ("userId") REFERENCES "User" ("id") ON DELETE CASCADE ON UPDATE CASCADE
        );
        CREATE UNIQUE INDEX "UserAnalytics_userId_key" ON "UserAnalytics"("userId");
        CREATE INDEX "UserAnalytics_userId_idx" ON "UserAnalytics"("userId");
        """;

    /// <summary>規則に関わるテーブルを作り、本ツールが確かめた版のマイグレーションを記録する。</summary>
    public static void Create(string path, string? migration = null)
    {
        migration ??= MergeProfiles.KnownFriendConnectMigrations.First();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Exec(path, Ddl + $"""
            INSERT INTO _prisma_migrations (id, checksum, finished_at, migration_name) VALUES ('1', '', 1, '20250202000000_init');
            INSERT INTO _prisma_migrations (id, checksum, finished_at, migration_name) VALUES ('2', '', 2, '{migration}');
            """);
    }

    /// <summary>
    /// User の行を足す SQL。Friend Connect が埋める NOT NULL の列には適当な値を入れる。
    /// <paramref name="lastLocationId"/> は、最後に見た履歴を指す制約の無い id。
    /// </summary>
    public static string InsertUser(string apiId, string displayName, string? note = null, long? lastLocationId = null)
        => "INSERT INTO User (apiId, displayName, statusDescription, status, currentAvatarImageUrl, currentAvatarThumbnailImageUrl, " +
           "developerType, fallbackAvatar, isFriend, last_platform, userIcon, lastUpdateAt, updatedAt, note, lastLocationId) VALUES (" +
           $"'{apiId}', '{displayName}', '', 'active', '', '', 'none', '', 1, 'standalonewindows', '', 1, 1, " +
           $"{(note is null ? "NULL" : "'" + note.Replace("'", "''") + "'")}, {(lastLocationId is null ? "NULL" : lastLocationId.Value.ToString())});";

    public static string InsertWorld(string apiId, string name)
        => $"INSERT INTO World (apiId, name, lastUpdateAt, updatedAt) VALUES ('{apiId}', '{name}', 1, 1);";

    public static string InsertTag(string name, string type = "user", string? description = null, int sort = 0)
        => $"INSERT INTO Tag (type, name, description, sort, updatedAt) VALUES ('{type}', '{name}', {(description is null ? "NULL" : "'" + description + "'")}, {sort}, 1);";

    /// <summary>ユーザー (apiId) にタグ (名前) を付ける。id はその DB での値を引く。</summary>
    public static string TagUser(string userApiId, string tagName)
        => "INSERT INTO UserTag (userId, tagId, updatedAt) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), (SELECT id FROM Tag WHERE type = 'user' AND name = '{tagName}'), 1);";

    public static string TagWorld(string worldApiId, string tagName)
        => "INSERT INTO WorldTag (worldId, tagId, updatedAt) VALUES (" +
           $"(SELECT id FROM World WHERE apiId = '{worldApiId}'), (SELECT id FROM Tag WHERE type = 'world' AND name = '{tagName}'), 1);";

    public static string InsertFavoriteGroup(string groupKey, string name, string type = "friend")
        => $"INSERT INTO FavoriteLocalGroup (type, groupKey, name, updatedAt) VALUES ('{type}', '{groupKey}', '{name}', 1);";

    public static string FavoriteUser(string groupKey, string userApiId)
        => $"INSERT INTO FavoriteLocal (groupKey, userId, updatedAt) VALUES ('{groupKey}', (SELECT id FROM User WHERE apiId = '{userApiId}'), 1);";

    public static string FavoriteWorld(string groupKey, string worldApiId)
        => $"INSERT INTO FavoriteLocal (groupKey, worldId, updatedAt) VALUES ('{groupKey}', (SELECT id FROM World WHERE apiId = '{worldApiId}'), 1);";

    public static string SetNotification(string userApiId, string type, string notifyApi, string value = "")
        => "INSERT INTO UserNotificationSetting (userId, type, notifyAPI, value, updatedAt) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), '{type}', '{notifyApi}', '{value}', 1);";

    /// <summary>写真のお気に入り。撮影時刻 (Unix ミリ秒) だけが意味を持つ。</summary>
    public static string FavoritePhoto(long shotAt)
        => $"INSERT INTO PhotoFavorite (shotAt, createdAt, updatedAt) VALUES ({shotAt}, 1, 1);";

    /// <summary>ワールドへのメモ。作成時刻は Friend Connect が作成時に 1 回だけ書く。</summary>
    public static string NoteWorld(string worldApiId, long createdAt, string note)
        => "INSERT INTO WorldNote (worldId, note, createdAt, updatedAt) VALUES (" +
           $"(SELECT id FROM World WHERE apiId = '{worldApiId}'), '{note.Replace("'", "''")}', {createdAt}, 1);";

    /// <summary>Friend Connect の稼働区間。起動 1 回が 1 行で、動作中は endAt と seconds が伸びる。</summary>
    public static string InsertScanRuntime(long startAt, long endAt)
        => $"INSERT INTO ScanRuntime (startAt, endAt, seconds, createdAt, updatedAt) VALUES ({startAt}, {endAt}, {(endAt - startAt) / 1000}, 1, 1);";

    public static string InsertAvatar(string avatar)
        => $"INSERT INTO Avatar (avatar, updatedAt) VALUES ('{avatar}', 1);";

    /// <summary>ユーザー (apiId) のステータスの履歴。時刻は Unix ミリ秒。</summary>
    public static string InsertUserStatus(string userApiId, long startAt, long endAt, int status = 1, string description = "")
        => "INSERT INTO UserStatus (userId, status, statusDescription, startAt, endAt, seconds) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), {status}, '{description}', {startAt}, {endAt}, {(endAt - startAt) / 1000});";

    /// <summary>ユーザー (apiId) のアバター (VRChat の ID) 着用の履歴。アバターの行は先に足しておく。</summary>
    public static string InsertUserAvatar(string userApiId, string avatar, long startAt, long endAt)
        => "INSERT INTO UserAvatar (userId, avatarId, startAt, endAt, seconds) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), (SELECT id FROM Avatar WHERE avatar = '{avatar}'), {startAt}, {endAt}, {(endAt - startAt) / 1000});";

    public static string InsertGroup(string apiId, string name)
        => $"INSERT INTO \"Group\" (apiId, name, shortCode, discriminator, description, ownerId, iconId, iconUrl, bannerId, bannerUrl, lastUpdateAt, updatedAt) " +
           $"VALUES ('{apiId}', '{name}', '', '', '', '', '', '', '', '', 1, 1);";

    /// <summary>
    /// インスタンスのマスタ。id は Friend Connect が入力から決める値なので、こちらで与える
    /// (負の値や、既存の行より小さい値もある)。ワールドと所有者は、その DB での id を引く。
    /// </summary>
    public static string InsertLocationMaster(long id, string location, string? worldApiId = null, string? ownerApiId = null, string? groupApiId = null, int nUsers = 1)
        => "INSERT INTO LocationMaster (id, location, worldId, instanceType, ownerId, ownerApiId, groupId, groupApiId, nUsers, capacity, lastUpdateAt) VALUES (" +
           $"{id}, '{location}', {(worldApiId is null ? "NULL" : $"(SELECT id FROM World WHERE apiId = '{worldApiId}')")}, 1, " +
           $"{(ownerApiId is null ? "NULL" : $"(SELECT id FROM User WHERE apiId = '{ownerApiId}')")}, {(ownerApiId is null ? "NULL" : $"'{ownerApiId}'")}, " +
           $"{(groupApiId is null ? "NULL" : $"(SELECT id FROM \"Group\" WHERE apiId = '{groupApiId}')")}, {(groupApiId is null ? "NULL" : $"'{groupApiId}'")}, {nUsers}, 16, 1);";

    /// <summary>ユーザー (apiId) のインスタンス (LocationMaster の id) への滞在。時刻は Unix ミリ秒。</summary>
    public static string InsertLocation(string userApiId, long locationMasterId, long startAt, long endAt, string? worldApiId = null)
        => "INSERT INTO Location (userId, locationKey, worldId, instanceType, startAt, endAt, seconds) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), {locationMasterId}, " +
           $"{(worldApiId is null ? "NULL" : $"(SELECT id FROM World WHERE apiId = '{worldApiId}')")}, 1, {startAt}, {endAt}, {(endAt - startAt) / 1000});";

    /// <summary>動画の再生履歴。滞在はユーザーと開始時刻で引く。</summary>
    public static string InsertVideo(long dateTime, string url, string userApiId, long locationStartAt)
        => $"INSERT INTO Video (dateTime, locationId, url, updatedAt) VALUES ({dateTime}, {LocationId(userApiId, locationStartAt)}, '{url}', 1);";

    /// <summary>インスタンス (LocationMaster の id) と滞在 (ユーザーと開始時刻) へのメモ。</summary>
    public static string NoteLocation(long locationMasterId, string userApiId, long locationStartAt, long createdAt, string note)
        => $"INSERT INTO LocationNote (locationMasterId, locationId, note, createdAt, updatedAt) VALUES ({locationMasterId}, {LocationId(userApiId, locationStartAt)}, '{note}', {createdAt}, 1);";

    /// <summary>写真。滞在 (ユーザーと開始時刻) は無くてもよい。</summary>
    public static string InsertPhoto(long shotAt, string thumbnail, string? userApiId = null, long locationStartAt = 0, string source = "C:\\photos\\p.png")
        => "INSERT INTO Photo (locationId, shotAt, source, thumbnail, hasThumbnail, updatedAt) VALUES (" +
           $"{(userApiId is null ? "NULL" : LocationId(userApiId, locationStartAt))}, {shotAt}, '{source}', '{thumbnail}', 1, 1);";

    /// <summary>
    /// 同席の集計。ユーザー (apiId) から相手 (apiId) を見た 1 行。秒数は実機と同じく小数を持ちうる。
    /// 回数のうち合流・退室の 4 列は 1 で固定し、同席の回数と秒数と最終接触時刻だけを与える。
    /// </summary>
    public static string InsertConnection(string userApiId, string targetApiId, long stayCount, double staySeconds, long? lastContactAt = null, long lastUpdateAt = 1)
        => "INSERT INTO UserConnection (userId, targetUserId, joinACount, joinBCount, leaveACount, leaveBCount, stayCount, staySeconds, " +
           "staySeconds3Hour, staySeconds6Hour, staySeconds12Hour, staySeconds1Day, staySeconds2Day, staySeconds3Day, lastContactAt, lastUpdateAt, updatedAt) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), (SELECT id FROM User WHERE apiId = '{targetApiId}'), 1, 1, 1, 1, {stayCount}, " +
           $"{staySeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, 0, 0, 0, 0, 0, 0, " +
           $"{(lastContactAt is null ? "NULL" : lastContactAt.Value.ToString())}, {lastUpdateAt}, 1);";

    /// <summary>ワールド訪問の集計。平均滞在時間は Friend Connect と同じく秒数を回数で割って切り捨てる。</summary>
    public static string InsertWorldVisit(string userApiId, string worldApiId, long stayCount, long staySeconds, long lastUpdateAt = 1)
        => "INSERT INTO UserWorldVisit (userId, worldId, stayCount, staySeconds, stayAvg, lastUpdateAt, updatedAt) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), (SELECT id FROM World WHERE apiId = '{worldApiId}'), " +
           $"{stayCount}, {staySeconds}, {(stayCount > 0 ? staySeconds / stayCount : 0)}, {lastUpdateAt}, 1);";

    /// <summary>ユーザーごとの分析結果。文字列の列には適当な JSON を入れる。</summary>
    public static string InsertAnalytics(string userApiId, int mostActivityHour = 1)
        => "INSERT INTO UserAnalytics (userId, instanceTypeRate, privateRate, userStatusRate, activeHeatmap, stayAvgPerLocation, loginAvgPerDay, mostActivityHour, lastUpdateAt, updatedAt) VALUES (" +
           $"(SELECT id FROM User WHERE apiId = '{userApiId}'), '{{}}', 0, '{{}}', '{{}}', 39, 287, {mostActivityHour}, 1, 1);";

    private static string LocationId(string userApiId, long startAt)
        => $"(SELECT id FROM Location WHERE userId = (SELECT id FROM User WHERE apiId = '{userApiId}') AND startAt = {startAt})";

    public static void Exec(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static List<string> Rows(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i)))));
        }
        return rows;
    }

    public static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}

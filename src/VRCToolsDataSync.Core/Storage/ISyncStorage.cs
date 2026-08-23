using VRCToolsDataSync.Core.Sync;

namespace VRCToolsDataSync.Core.Storage;

/// <summary>
/// 同期先の抽象。OneDrive などのローカル同期フォルダと、S3 互換オブジェクト
/// ストレージ (Cloudflare R2 / Amazon S3 / MinIO など) を同じ操作で扱う。
/// <para>
/// キーは同期先のルートからの位置を表す文字列で、区切りは常に '/'、先頭に '/' は
/// 付けない (例: "vrcx/latest.sqlite3")。ローカルフォルダ実装はこれをフォルダ
/// 構造へ、S3 互換実装はオブジェクトキーへ写す。
/// </para>
/// <para>
/// 実装はスレッドセーフでなければならない。<see cref="Watch.AutoSyncCoordinator"/>
/// が同じインスタンスを複数のスレッドから使う。
/// </para>
/// </summary>
public interface ISyncStorage
{
    /// <summary>ログと UI に出す同期先の表示名。</summary>
    string DisplayName { get; }

    /// <summary>
    /// <see cref="Settings.SyncSettings.ToolState"/> のキーに付ける接頭辞。
    /// <para>
    /// LastPulledVersion は「その同期先の manifest の version」に対する状態なので、
    /// 同期先を切り替えると意味を失う。同期先ごとに別のキーで持つことで、切り替え直後に
    /// 古い version でコンフリクト判定や Pull スキップが誤発火するのを防ぐ。
    /// 同期フォルダはフォルダのパスごと、S3 互換はエンドポイントとバケットと
    /// キー接頭辞ごとに分かれる。
    /// </para>
    /// </summary>
    string StateKeyPrefix { get; }

    /// <summary>
    /// 同期先を実際に使えるかを確かめる。設定画面や CLI の接続テストから呼ぶ。
    /// 読み取りだけでなく、同期に必要な書き込みと削除まで確認する。
    /// 使えない場合は <see cref="SyncStorageException"/> を投げる。
    /// </summary>
    void VerifyAccess();

    /// <summary>
    /// manifest を読み込む。存在しない場合は空の manifest と null のタグを返す。
    /// </summary>
    ManifestSnapshot LoadManifest();

    /// <summary>
    /// manifest を保存する。<paramref name="expectedTag"/> は
    /// <see cref="LoadManifest"/> が返したタグで、保存時点の同期先の内容と
    /// 食い違っていれば false を返して呼び出し側にやり直させる。
    /// タグを扱えない同期先は条件を無視して保存し、常に true を返す。
    /// </summary>
    bool TrySaveManifest(SyncManifest manifest, string? expectedTag);

    /// <summary>ローカルファイルを <paramref name="key"/> として同期先へ書き込む。</summary>
    void Upload(string localPath, string key);

    /// <summary>
    /// 生成してから書き込むファイル (SQLite の VACUUM INTO 出力) 用に、
    /// 書き込み先を確保する。<see cref="IStagedUpload.LocalPath"/> へ書き出してから
    /// <see cref="IStagedUpload.Commit"/> にキーを渡すと同期先へ反映される。
    /// Commit せずに破棄すれば同期先は変化しない。
    /// <para>
    /// キーを開始時ではなく Commit 時に決めるのは、置き場所を内容から決めているため。
    /// 書き出しが終わるまでハッシュが分からず、キーも確定しない。
    /// </para>
    /// </summary>
    IStagedUpload BeginUpload();

    /// <summary>
    /// <paramref name="key"/> を <paramref name="localPath"/> へ取り出す。
    /// 書き込みは中断しても元のファイルが壊れない形で行う。
    /// 同期先にキーが無ければ false を返す。
    /// </summary>
    bool TryDownload(string key, string localPath);

    /// <summary>キーが同期先にあるかを確かめる。</summary>
    bool Exists(string key);

    /// <summary>
    /// キーを削除する。存在しない場合は何もしない。
    /// 削除できなかった場合は <see cref="SyncStorageException"/> を投げる
    /// (呼び出し側が 1 件の失敗として扱えるよう、同期先の種類によらない型に揃える)。
    /// </summary>
    void Delete(string key);

    /// <summary>
    /// 接頭辞に一致するオブジェクトを列挙する。孤児の回収 (GC) だけが使う。
    /// <para>
    /// 同期の経路では使わない。同期先を列挙して差分を取ると、列挙してから
    /// 判断するまでの間に他の PC が上げたものまで巻き込むため、参照を基準に
    /// 判断する。回収では、その巻き込みを猶予期間で避ける。
    /// </para>
    /// </summary>
    IEnumerable<StoredObject> List(string keyPrefix);

    /// <summary>
    /// manifest の更新を監視する仕組みを作る。ローカルフォルダはファイル監視、
    /// S3 互換モードは定期的な問い合わせで実現する。
    /// </summary>
    IManifestWatcher CreateManifestWatcher();
}

/// <summary>
/// <see cref="ISyncStorage.BeginUpload"/> が返す書き込み枠。
/// Commit を呼ばずに Dispose した場合、同期先は変化せず一時ファイルだけが片付く。
/// </summary>
public interface IStagedUpload : IDisposable
{
    /// <summary>書き出し先のローカルパス。</summary>
    string LocalPath { get; }

    /// <summary><see cref="LocalPath"/> の内容を <paramref name="key"/> として確定させる。</summary>
    void Commit(string key);
}

/// <summary>manifest の更新通知。</summary>
public interface IManifestWatcher : IDisposable
{
    event Action<SyncManifest>? ManifestChanged;

    void Start();
}

/// <summary>同期先に置かれているオブジェクト 1 件。回収の判断に使う。</summary>
/// <param name="Key">同期先のルートから見たキー。</param>
/// <param name="LastModified">最後に書かれた時刻。猶予期間の判定に使う。</param>
/// <param name="Size">大きさ (バイト)。回収でどれだけ空いたかの報告に使う。</param>
public sealed record StoredObject(string Key, DateTimeOffset LastModified, long Size);

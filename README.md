# VRCToolsDataSync

VRCX と VRC Friend Connect のユーザーデータを、複数 PC 間で共有するための Windows 専用ツール。
保存先は OneDrive などのローカル同期フォルダか、S3 互換オブジェクトストレージ (Cloudflare R2 / Amazon S3 / MinIO など) から選べる。

## 概要

- **同期対象**
  - **VRCX**: `%AppData%\VRCX\VRCX.sqlite3` と `VRCX.json`
  - **VRC Friend Connect**: `%AppData%\VRC Friend Connect\` の次のファイル
    - `db\db_1.1.sqlite`（現行版の本体 DB。必須）
    - `db\db.sqlite`（v1.1 より前の版が使っていた DB。古くから使っている PC にだけあり、ある場合だけ同期する）
    - `config.json`（ライセンスキーと各種トークンを含む。同期先に置かれる点に注意）
    - `thumbnails\`（写真のサムネイル）と、存在する場合は `notes\`
- **保存先**: ローカル同期フォルダ（パスはユーザーが手動指定）または S3 互換オブジェクトストレージ
- **マージ戦略**: 既定は last-writer-wins（置き換え）。VRCX と VRC Friend Connect のそれぞれについて、試験的なマージ同期を別々に有効にできる（後述）。Pull 前にローカルを `%AppData%\VRCToolsDataSync\backup\<tool>\<timestamp>\` へ自動退避（既定10世代）
- **未同期の変更の保護**: 同期のたびに、DB のうち手で編集するデータと追加専用の履歴だけの要約を `%AppData%\VRCToolsDataSync\base\` に残す。Pull は、リモートが新しくても手元の要約が前回から変わっていれば上書きしない。マージ同期が有効ならマージして Push し、無効なら見送って知らせる。手元の変更を捨てて上書きするのは、非常用の強制 Pull に限る（後述）
- **転送の省略**: manifest に記録した SHA-256 と手元のファイルが一致する場合、送信も取得も行わない
- **プロセス検知ガード**: 同期対象のツール (`VRCX`, `VRC Friend Connect`) が実行中の場合は WAL ロックによる DB 破損を避けるため同期を拒否
- **暗号化**: 保存先のデータは暗号化しない。S3 互換モードのシークレットアクセスキーのみ、Windows の DPAPI で保護して `settings.json` に保存する

保存先に置くデータの形は [docs/architecture.md](docs/architecture.md) にまとめてある。

## 保存先の選び方

| | ローカル同期フォルダ | S3 互換ストレージ |
| --- | --- | --- |
| 転送を担うもの | OneDrive などの同期クライアント | 本ツール |
| 追加の準備 | フォルダを選ぶだけ | バケット作成と API キー発行 |
| 更新の検知 | ファイル監視（即時） | manifest の定期確認（60秒間隔） |
| 費用 | 契約中のストレージ容量のみ | 下記のとおり |

S3 互換モードの費用は、ほぼ Pull のダウンロード転送量だけで決まる。Cloudflare R2 は転送量が無料で、10GB までの保存も無料枠に収まるため、現実的な使い方では課金されない。Amazon S3 は転送量が月 100GB まで無料で、それを超えると $0.09/GB（東京リージョンは約 $0.114/GB）かかる。大きな DB を自動同期で頻繁に往復させる使い方では S3 が月数千円に達しうるので、既定の推奨は R2 とする。

S3 互換ストレージの準備と設定の手順は [docs/s3.md](docs/s3.md) を参照。

## 必要環境

- Windows 10 (build 17763) 以降
- .NET 10 SDK
- Windows App SDK 2.0 系（NuGet 経由で自動取得）

## セットアップ

```powershell
git clone https://github.com/limit7412/VRCToolsDataSync.git
cd VRCToolsDataSync
dotnet build VRCToolsDataSync.slnx
```

## CLI 使用例

```powershell
# 設定確認
dotnet run --project src\VRCToolsDataSync.Cli -- status

# VRCX
dotnet run --project src\VRCToolsDataSync.Cli -- push vrcx --cloud "D:\OneDrive\VRCToolsDataSync"
dotnet run --project src\VRCToolsDataSync.Cli -- pull vrcx --cloud "D:\OneDrive\VRCToolsDataSync"
# 非常用の強制 Pull (手元の変更を確かめずに上書き)
dotnet run --project src\VRCToolsDataSync.Cli -- pull vrcx --force

# VRC Friend Connect
dotnet run --project src\VRCToolsDataSync.Cli -- push friend-connect --cloud "D:\OneDrive\VRCToolsDataSync"
dotnet run --project src\VRCToolsDataSync.Cli -- pull friend-connect --cloud "D:\OneDrive\VRCToolsDataSync"
```

`--cloud` を省略した場合は `%AppData%\VRCToolsDataSync\settings.json` に保存されたパスを使用する。S3 互換モードでは `--cloud` は無視される。

### 終了コード

| コード | 意味 |
| --- | --- |
| 0 | 成功 |
| 1 | 想定外エラー |
| 2 | 設定不備 (保存先が未設定 / 到達できない) |
| 3 | コンフリクト（Push: リモートがローカルの最終 Pull より新しい / Push 中に他 PC が更新した。Pull: 手元に未 Push の変更があり、マージ同期が無効） |
| 4 | 同期対象が存在しない |
| 5 | プロセス実行中で同期不可 |

## GUI

```powershell
dotnet run --project src\VRCToolsDataSync.App
```

設定カードで保存先を選び (同期フォルダならパスを指定、S3 互換ストレージなら接続情報を入力) → 保存 → 各ツールのカードから Push/Pull。コンフリクト発生時は画面の上端に問い合わせが出るので、「先に Pull」「強制 Push」「キャンセル」を選択する。Pull が手元の未 Push の変更を見つけて見送ったときも同じ場所に問い合わせが出て、「強制 Pull (手元の変更を捨てる)」か「キャンセル」を選ぶ。

ウィンドウの × はタスクトレイへの最小化として扱う。終了はトレイメニューの「同期して終了」から行う。

### Windows ログイン時の自動起動

設定カードの「登録」で `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` に登録する。管理者権限は要らない。

「ウィンドウを出さずにタスクトレイへ常駐する」を選ぶと、登録するコマンドに `--minimized` が付き、ログイン時の起動でウィンドウが開かなくなる。**この指定が効くのは自動起動のときだけである。** 設定ファイルではなく登録するコマンドに持たせているので、手で起動したときはこれまでどおりウィンドウが開く。トレイのアイコンを作れなかった環境でも、実行ファイルをもう一度起動すれば常駐中のウィンドウが出る。

登録済みのままチェックを切り替えると、その場で登録し直す。更新の後に開き直す経路もこの指定を引き継ぐので、更新のたびに画面が現れることはない。

## マージ同期（試験的）

既定の同期は置き換えで、2 台の PC で並行して変更が生まれると、後から Push した側だけが残る。マージ同期を有効にすると、そのツールの DB について次の 2 つの場面で、上書きの代わりに「同期先の最新に、手元にしか無い変更を足す」処理を行い、結果をそのまま Push する。VRCX と VRC Friend Connect で別々に有効にする。

- Pull で、手元に未 Push の変更が見つかったとき。起動時の自動 Pull、手動の Pull、競合時の「先に Pull」、別の PC の更新の通知からの Pull のどれも同じ（非常用の強制 Pull を除く）
- Push が「リモートの方が新しい」ことで競合したとき（強制 Push を除く）

マージ同期が無効のときは、Pull は手元の変更を見つけると上書きせずに見送る。GUI では強制 Pull で上書きするかを尋ね、CLI は終了コード 3 で止まる。手元の変更を確かめられない場合（前回の同期時点の要約が無いなど）は、どちらの設定でも従来どおり上書きする。

### VRCX

足すのは、追加専用の履歴（`gamelog_*`、feed、フレンドの履歴、通知）と、手で編集するデータ（メモ、お気に入り、アバターのタグ）だけである。履歴のうち後から書き込まれる滞在時間と通知の期限切れは、大きい方の値を残す。手で編集するデータ、新しい形式の通知、着用したアバターの履歴は前回の同期時点と比べ、変えた側の内容を残す。両方で別々に編集されていたら編集時刻の新しい方を残す。ただし通知の既読と期限切れ、アバターの累計の着用時間と最後に着けた時刻は、どちらの行を残しても両方の値を合わせる（既読と期限切れは一方で付けていれば残し、着用時間は前回の同期時点の値に双方で延びた分を足す）。キャッシュなど規則の対象外のテーブルは同期先の内容がそのまま残る。手元の DB はマージ前にバックアップへ退避し、両方の DB の列が食い違う、前回の同期時点の要約が古い形式か列が減っている、前回の同期の後に手元で履歴の行を消した、同期先で消された履歴の行の滞在時間を手元で書き換えた可能性がある、前回の同期の後に増えた列の値が両方で違う、手元で足す古い履歴より新しい行が同期先に多すぎて id を振り直した記録を残せない、前回の同期時点にあったテーブルが片方で消されてもう片方で変わっている、前回の Push が同期先に反映されたか分からないまま同期先が進んでいる、前回の Push の後に別の PC が強制 Push で上書きした、マージの間に VRCX が DB を書き換えた、といった安全に合わせられない状態ではマージを中止して従来の動作に戻る。`VRCX.json` はマージせず、手元のものを Push する。

### VRC Friend Connect

足すのは、手で編集するデータだけである。ユーザーのメモ（`User.note`）、タグの定義（`Tag`）とユーザー・ワールドへのタグ付け（`UserTag` / `WorldTag`）、ローカルのお気に入り（`FavoriteLocalGroup` / `FavoriteLocal`）、ユーザーごとの通知設定（`UserNotificationSetting`）を、前回の同期時点と比べて変えた側の内容にそろえる。Friend Connect の DB では全テーブルの id が PC ごとに別の値を取るので、行は VRChat の ID やタグ名などの自然キーで突き合わせ、別のテーブルを id で指す列は同期先の id に付け替える。指す先のユーザーやワールドが同期先に無ければ、その行も手元から写す（最後に見た履歴を指す列は Friend Connect が埋め直すので空にする）。メモやタグの定義のように他の行から参照される行は、消して入れ直すと参照している行が連鎖して消えるので、行はそのまま列だけを書き換える。手元で消したタグやグループは、参照する側のテーブルを合わせた後に消し、前回の同期の後に同期先だけでそのタグやグループを使う行が足されていれば、消さずに残す。メモを消した場合は、User の行は残して `note` だけを消す。両方で別々に編集されていた場合は、Friend Connect の `updatedAt` は起動しているだけで書き換わって編集時刻として使えないため、同期先の内容を残す（手元の内容はバックアップに残る）。滞在やステータスの履歴、インスタンスの人数の時系列、同席やワールド訪問の集計、VRChat 側のお気に入りの写しなどはマージせず、同期先の内容がそのまま残る。マージの前に `_prisma_migrations` の最新のマイグレーション名が本ツールの確かめた版と一致することを確かめ、違えば中止する。`config.json` と `thumbnails\`、旧版の `db.sqlite` はマージせず、手元のものを Push する。

Friend Connect のマージは、Friend Connect が停止している間に、その DB のコピーに対して行単位で書き込み、結果をファイルとして配置する（元のファイルはバックアップに残る）。Friend Connect のコードや通信には触れないが、Friend Connect 自身が作ったものではない行が DB に入ることになる。利用規約上の扱いを利用者が判断して有効にする前提で、既定は無効である。

### 切り替えと実行

GUI では設定カードの「VRCX をマージで同期 (試験的)」「Friend Connect をマージで同期 (試験的)」で切り替える。CLI では次のとおり（`vrcx` を `friend-connect` に替えると Friend Connect）。

```powershell
# 有効化 / 無効化
dotnet run --project src\VRCToolsDataSync.Cli -- merge vrcx --enable
dotnet run --project src\VRCToolsDataSync.Cli -- merge vrcx --disable

# 何を足すかを確かめるだけ (手元にも同期先にも書かない)
dotnet run --project src\VRCToolsDataSync.Cli -- merge vrcx --dry-run

# 今すぐマージして Push する
dotnet run --project src\VRCToolsDataSync.Cli -- merge vrcx
```

## 非常用の強制 Push

通常の強制 Push (`--force` と、コンフリクト時の「強制 Push」) はコンフリクトの検査を飛ばすだけで、送信の省略はそのまま行う。省略の判断は、manifest に記録したハッシュと同期先に実体があるかどうかで決まる。そのため、同期先の実体の中身だけが壊れた場合 (同期クライアントの事故や手作業での上書き) は、Push し直しても壊れた実体が残り、他の PC の Pull が「manifest の記録と一致しません」で失敗し続ける。

非常用の強制 Push は、この状態を直すための操作である。通常の強制 Push とは次の点が違う。

- 送信を省かず、手元の全ファイルを送り直す。同期先に同じ置き場所の実体があっても置き換える
- 内容が前回と同じでも manifest の version を進め、他の PC に取り直させる
- Push の途中で他の PC が同期先を更新しても、コンフリクトとして止めずに上書きする

同期先はこの PC のデータで丸ごと置き換わり、他の PC が Push した変更のうちこの PC が取得していないものは失われる。全ファイルを送るので、データが大きいと時間と転送量もかかる。通常の Push で直らないときに限って使う。

```powershell
dotnet run --project src\VRCToolsDataSync.Cli -- push vrcx --emergency
dotnet run --project src\VRCToolsDataSync.Cli -- push friend-connect --emergency
```

GUI では各ツールのカードの「非常用の強制 Push...」から実行する。押すと確認が出るので、「置き換える」を選ぶと実行する。

## 非常用の強制 Pull

通常の Pull は、前回の同期の後に手元で変わったテーブルがあれば上書きしない。マージ同期が有効ならマージして Push し、無効なら見送って知らせる。手元の変更を捨てて同期先の内容に合わせたいときは、非常用の強制 Pull を使う。強制 Pull は手元の変更を確かめず、マージ同期の設定にかかわらず、同期先の内容でこの PC のデータを丸ごと置き換える。置き換える前のデータは通常の Pull と同じくバックアップへ退避する（`--no-backup` を付けた場合を除く）。

```powershell
dotnet run --project src\VRCToolsDataSync.Cli -- pull vrcx --force
dotnet run --project src\VRCToolsDataSync.Cli -- pull friend-connect --force
```

GUI では各ツールのカードの「非常用の強制 Pull...」から実行する。押すと確認が出るので、「置き換える」を選ぶと実行する。通常の Pull が手元の変更を見つけて見送ったときの問い合わせで「強制 Pull (手元の変更を捨てる)」を選んだ場合も同じ処理になる。

## ドキュメント

込み入った話は `docs/` に分けてある。

| 文書 | 内容 |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | 保存先のレイアウトと、プロジェクト構成・Core の層 |
| [docs/s3.md](docs/s3.md) | S3 互換ストレージの準備・設定・注意点 |
| [docs/gc.md](docs/gc.md) | ストレージ容量の解放 (猶予期間、未完了のアップロード) |
| [docs/update.md](docs/update.md) | 本体の自動アップデート、起動に失敗した場合の振る舞い、0.0.6 以前からの移行 |
| [docs/release.md](docs/release.md) | CI とリリースビルド |

## 第三者プロダクトに関する免責

本ツールは [VRCX](https://github.com/vrcx-team/VRCX)（vrcx-team, MIT License）および VRC Friend Connect（たぴおかシステムズ, クローズドソース）の作者・開発元と一切の提携・支援関係はありません。

- 本ツールは VRCX および VRC Friend Connect の本体コードやバイナリを再配布しません。ユーザーのローカル PC 上に存在する各アプリのデータファイル（SQLite / JSON / メモ）をコピー・スナップショット化し、ユーザーが指定した保存先（ローカル同期フォルダまたは S3 互換オブジェクトストレージ）経由で別 PC に反映するのみです。
- 本ツールは VRC Friend Connect の実行ファイルや通信の解析（リバースエンジニアリング）を行いません。DB は SQLite ファイルとして扱い、中身を読み取る場合は先にスキーマの版を確かめ、本ツールが対応を確認した版に限ります。既定の同期では DB への書き込みは行わず、`VACUUM INTO` による写しの作成とファイル単位の差し替えに留めます。利用者が明示的に有効にしたマージ同期に限り、Friend Connect が停止している間に DB のコピーへ手で編集するデータ（メモ・タグ・ローカルお気に入り・通知設定）の行を書き足し、結果をファイルとして配置します。
- VRCX および VRC Friend Connect 各々の利用規約遵守はユーザー自身の責任です。

## ライセンス

[LICENSE](./LICENSE) を参照。

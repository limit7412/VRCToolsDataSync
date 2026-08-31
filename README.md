# VRCToolsDataSync

VRCX と VRC Friend Connect のユーザーデータを、複数 PC 間で共有するための Windows 専用ツール。
保存先は OneDrive などのローカル同期フォルダか、S3 互換オブジェクトストレージ (Cloudflare R2 / Amazon S3 / MinIO など) から選べる。

## 概要

- **同期対象**
  - **VRCX**: `%AppData%\VRCX\VRCX.sqlite3` と `VRCX.json`
  - **VRC Friend Connect**: `%AppData%\VRC Friend Connect\db\` の `db.sqlite` / `db_1.1.sqlite` と、ルート直下の `config.json`、存在する場合は `notes\`
- **保存先**: ローカル同期フォルダ（パスはユーザーが手動指定）または S3 互換オブジェクトストレージ
- **マージ戦略**: last-writer-wins。Pull 前にローカルを `%AppData%\VRCToolsDataSync\backup\<tool>\<timestamp>\` へ自動退避（既定10世代）
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
| 3 | コンフリクト（リモートがローカルの最終 Pull より新しい / Push 中に他 PC が更新した） |
| 4 | 同期対象が存在しない |
| 5 | プロセス実行中で同期不可 |

## GUI

```powershell
dotnet run --project src\VRCToolsDataSync.App
```

設定カードで保存先を選び (同期フォルダならパスを指定、S3 互換ストレージなら接続情報を入力) → 保存 → 各ツールのカードから Push/Pull。コンフリクト発生時はダイアログで「先に Pull」「強制 Push」「キャンセル」を選択する。

ウィンドウの × はタスクトレイへの最小化として扱う。終了はトレイメニューの「同期して終了」から行う。

### Windows ログイン時の自動起動

設定カードの「登録」で `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` に登録する。管理者権限は要らない。

「ウィンドウを出さずにタスクトレイへ常駐する」を選ぶと、登録するコマンドに `--minimized` が付き、ログイン時の起動でウィンドウが開かなくなる。**この指定が効くのは自動起動のときだけである。** 設定ファイルではなく登録するコマンドに持たせているので、手で起動したときはこれまでどおりウィンドウが開く。トレイのアイコンを作れなかった環境でも、実行ファイルをもう一度起動すれば常駐中のウィンドウが出る。

登録済みのままチェックを切り替えると、その場で登録し直す。更新の後に開き直す経路もこの指定を引き継ぐので、更新のたびに画面が現れることはない。

## ドキュメント

込み入った話は `docs/` に分けてある。

| 文書 | 内容 |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | 保存先のレイアウトと、プロジェクト構成・Core の層 |
| [docs/s3.md](docs/s3.md) | S3 互換ストレージの準備・設定・注意点 |
| [docs/gc.md](docs/gc.md) | ストレージ容量の解放 (猶予期間、未完了のアップロード) |
| [docs/update.md](docs/update.md) | 本体の自動アップデートと、0.0.6 以前からの移行 |
| [docs/release.md](docs/release.md) | CI とリリースビルド |

## 第三者プロダクトに関する免責

本ツールは [VRCX](https://github.com/vrcx-team/VRCX)（vrcx-team, MIT License）および VRC Friend Connect（たぴおかシステムズ, クローズドソース）の作者・開発元と一切の提携・支援関係はありません。

- 本ツールは VRCX および VRC Friend Connect の本体コードやバイナリを再配布しません。ユーザーのローカル PC 上に存在する各アプリのデータファイル（SQLite / JSON / メモ）をコピー・スナップショット化し、ユーザーが指定した保存先（ローカル同期フォルダまたは S3 互換オブジェクトストレージ）経由で別 PC に反映するのみです。
- 本ツールは VRC Friend Connect のスキーマ解析・改変・リバースエンジニアリングを行いません。`VACUUM INTO` を含む SQLite 操作はファイル単位の取り扱いに留まります。
- VRCX および VRC Friend Connect 各々の利用規約遵守はユーザー自身の責任です。

## ライセンス

[LICENSE](./LICENSE) を参照。

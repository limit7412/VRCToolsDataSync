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

## 保存先のレイアウト

ローカル同期フォルダではフォルダ構造、S3 互換ストレージではオブジェクトキーとして、同じ形を使う。

```text
<保存先のルート>/
  manifest.json                    # ツール別の version / machineName / files[]
  vrcx/
    latest.sqlite3                 # VACUUM INTO で WAL を統合した単一ファイル
    latest.json                    # VRCX.json
  friend-connect/
    db/
      db.sqlite                    # VACUUM INTO スナップショット
      db_1.1.sqlite
    config.json
    notes/                         # 存在する場合のみ
```

S3 互換モードでは、この全体をバケット内の任意の接頭辞の下に置ける（`--prefix`）。

### 保存先の選び方

| | ローカル同期フォルダ | S3 互換ストレージ |
| --- | --- | --- |
| 転送を担うもの | OneDrive などの同期クライアント | 本ツール |
| 追加の準備 | フォルダを選ぶだけ | バケット作成と API キー発行 |
| 更新の検知 | ファイル監視（即時） | manifest の定期確認（60秒間隔） |
| 費用 | 契約中のストレージ容量のみ | 下記のとおり |

S3 互換モードの費用は、ほぼ Pull のダウンロード転送量だけで決まる。Cloudflare R2 は転送量が無料で、10GB までの保存も無料枠に収まるため、現実的な使い方では課金されない。Amazon S3 は転送量が月 100GB まで無料で、それを超えると $0.09/GB（東京リージョンは約 $0.114/GB）かかる。大きな DB を自動同期で頻繁に往復させる使い方では S3 が月数千円に達しうるので、既定の推奨は R2 とする。

## 構成

| プロジェクト | 役割 |
| --- | --- |
| `VRCToolsDataSync.Core` | 設定 / パス解決 / プロセス検知 / SQLite スナップショット / ハッシュ / バックアップ / manifest / 保存先の抽象 / SyncService |
| `VRCToolsDataSync.Cli` | `push` / `pull` / `status` / `storage` を提供するコンソール |
| `VRCToolsDataSync.App` | WinUI 3 (.NET 10) の GUI。設定編集と Push/Pull、コンフリクト解決ダイアログ |

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

## S3 互換ストレージを使う

### バケットと API キーを用意する

**Cloudflare R2 の場合**

1. Cloudflare ダッシュボードの R2 でバケットを作る。
2. 「R2 API トークン」を発行する。権限は「オブジェクトの読み取りと書き込み」、対象は作成したバケットのみに絞る。
3. 表示される Access Key ID / Secret Access Key と、エンドポイント `https://<アカウントID>.r2.cloudflarestorage.com` を控える。

**Amazon S3 の場合**

1. バケットを作る。パブリックアクセスはすべてブロックのままにする。
2. そのバケットに対する `s3:GetObject` / `s3:PutObject` / `s3:DeleteObject` / `s3:AbortMultipartUpload` / `s3:ListBucket` だけを許可する IAM ポリシーを作り、専用の IAM ユーザに付ける。
   `s3:AbortMultipartUpload` は 64MB を超えるファイルの送信に要る。本ツールはその場合マルチパートで送り、途中で失敗したら開始済みのアップロードを中断する。この権限が無いと中断できず、送信済みのパートが未完了のまま残って課金され続ける。念のため、未完了のマルチパートアップロードを数日で破棄するライフサイクルルールもバケットに付けておくとよい。
3. そのユーザのアクセスキーを発行し、エンドポイント `https://s3.<リージョン>.amazonaws.com` とあわせて控える。

### 本ツールに設定する

GUI の設定カードで「データの保存先」を「S3 互換ストレージ」に切り替えると、エンドポイント URL・バケット名・リージョン・キー接頭辞・アクセスキーを入力できる。「接続テスト」で到達性を確かめてから「設定を保存」する。

CLI でも設定できる。

```powershell
# Cloudflare R2
VRCToolsDataSync.Cli.exe storage s3 `
  --endpoint "https://<アカウントID>.r2.cloudflarestorage.com" `
  --bucket "vrctools" `
  --region auto `
  --access-key "<Access Key ID>"

# Amazon S3 (東京リージョン)
VRCToolsDataSync.Cli.exe storage s3 `
  --endpoint "https://s3.ap-northeast-1.amazonaws.com" `
  --bucket "vrctools" `
  --region ap-northeast-1 `
  --access-key "<Access Key ID>"
```

`--secret-key` を省略すると、シークレットアクセスキーは画面に表示されない形で入力を求められる。シェルの履歴やプロセス一覧に残さないため、こちらを勧める。スクリプトから渡す場合は環境変数 `VRCTOOLSDATASYNC_S3_SECRET_KEY` を使う。

設定は保存前に実際の接続を試し、到達できなければ保存せずに終了する。確認は読み取りだけでなく、検査用オブジェクトの書き込みと削除まで行う。Push はローカルから消えたファイルの削除も行うので、削除を許可しない API キーもここで弾かれる。あとから確認する場合は `storage test` を使う。

```powershell
VRCToolsDataSync.Cli.exe storage test
```

ローカル同期フォルダへ戻す場合は次のとおり。

```powershell
VRCToolsDataSync.Cli.exe storage local --path "D:\OneDrive\VRCToolsDataSync"
```

### 注意点

- エンドポイントは `https` のみ受け付ける。ファイルの送信では本文のハッシュを署名に含めない代わりに、内容の完全性を TLS に委ねているため。
- シークレットアクセスキーは DPAPI で保護して保存する。復号できるのは保存した Windows ユーザだけなので、`settings.json` を別 PC へコピーしても S3 の設定は引き継げない。PC ごとに設定し直すこと。
- 同期履歴 (`toolState`) は保存先ごとに別のキーで持つ。保存先を切り替えても、元の保存先の履歴はそのまま残る。同期フォルダも、フォルダのパスごとに別の履歴になる。

## GUI

```powershell
dotnet run --project src\VRCToolsDataSync.App
```

設定カードで保存先を選び (同期フォルダならパスを指定、S3 互換ストレージなら接続情報を入力) → 保存 → 各ツールのカードから Push/Pull。コンフリクト発生時はダイアログで「先に Pull」「強制 Push」「キャンセル」を選択する。

## CI

`.github/workflows/ci.yml` が PR と develop / master への push をトリガーに `VRCToolsDataSync.Cli` と `VRCToolsDataSync.App` を Release 構成でビルドする (`VRCToolsDataSync.Core` は両者からの参照でビルドされる)。
`VRCToolsDataSync.App` が WinUI 3 と Windows App SDK に依存しているため、ランナーは `windows-latest` を使う。

## リリースビルド

ローカルで self-contained な実行ファイルを作る場合:

```powershell
# x64 (既定)
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1

# arm64 など他アーキ
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Arch arm64
```

出力先は `artifacts/win-<arch>/{app,cli}/` と `artifacts/VRCToolsDataSync-win-<arch>.zip`。`app/VRCToolsDataSync.App.exe` が GUI、`cli/VRCToolsDataSync.Cli.exe` が CLI。

GitHub Actions の `release` ワークフロー (`.github/workflows/release.yml`) が x64 と arm64 をビルドし、GitHub Release に zip を添付する。
トリガーによって作られるリリースの状態が変わる。

| トリガー | タグ | リリース |
| --- | --- | --- |
| master への push | 直近のタグの patch を一つ進めた番号を自動採番 | 公開 |
| `0.0.4` 形式のタグの push | push したタグ | Draft |
| Actions タブからの手動実行 | 入力したタグ名（空ならアーティファクトのみ） | Draft |

master へのマージでリリースが公開されるため、develop から master へマージする操作がリリース操作にあたる。
minor や major を上げたいときは、先に目的の番号のタグを push してリリースを作る。

master への push が短い間隔で続いた場合、待機中のワークフローは後続の実行に置き換えられ、リリースは一本にまとまる。
番号は一つだけ進み、まとめられた変更はそのリリースのノートに含まれる (ノートは直前のタグからの差分で生成される)。

## 第三者プロダクトに関する免責

本ツールは [VRCX](https://github.com/vrcx-team/VRCX)（vrcx-team, MIT License）および VRC Friend Connect（たぴおかシステムズ, クローズドソース）の作者・開発元と一切の提携・支援関係はありません。

- 本ツールは VRCX および VRC Friend Connect の本体コードやバイナリを再配布しません。ユーザーのローカル PC 上に存在する各アプリのデータファイル（SQLite / JSON / メモ）をコピー・スナップショット化し、ユーザーが指定した保存先（ローカル同期フォルダまたは S3 互換オブジェクトストレージ）経由で別 PC に反映するのみです。
- 本ツールは VRC Friend Connect のスキーマ解析・改変・リバースエンジニアリングを行いません。`VACUUM INTO` を含む SQLite 操作はファイル単位の取り扱いに留まります。
- VRCX および VRC Friend Connect 各々の利用規約遵守はユーザー自身の責任です。

## ライセンス

[LICENSE](./LICENSE) を参照。

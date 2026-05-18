# VRCToolsDataSync

VRCX と VRC Friend Connect のユーザーデータを、OneDrive のローカル同期フォルダを介して複数 PC 間で共有するための Windows 専用ツール。

## 概要

- **同期対象**
  - **VRCX**: `%AppData%\VRCX\VRCX.sqlite3` と `VRCX.json`
  - **VRC Friend Connect**: `%AppData%\VRC Friend Connect\` 配下の `db.sqlite` / `db_1.1.sqlite` / `config.json` / `notes\`
- **同期チャネル**: OneDrive のローカル同期フォルダ（パスはユーザーが手動指定）
- **マージ戦略**: last-writer-wins。Pull 前にローカルを `%AppData%\VRCToolsDataSync\backup\<tool>\<timestamp>\` へ自動退避（既定10世代）
- **プロセス検知ガード**: 同期対象のツール (`VRCX`, `VRC Friend Connect`) が実行中の場合は WAL ロックによる DB 破損を避けるため同期を拒否
- **暗号化**: なし（同一ユーザーの OneDrive 配下で完結する想定）

## クラウド側レイアウト

```text
<OneDriveFolder>/
  manifest.json                    # ツール別の version / machineName / files[]
  vrcx/
    latest.sqlite3                 # VACUUM INTO で WAL を統合した単一ファイル
    latest.json                    # VRCX.json
  friend-connect/
    db.sqlite                      # VACUUM INTO スナップショット
    db_1.1.sqlite
    config.json
    notes/
```

## 構成

| プロジェクト | 役割 |
| --- | --- |
| `VRCToolsDataSync.Core` | 設定 / パス解決 / プロセス検知 / SQLite スナップショット / ハッシュ / バックアップ / manifest / SyncService |
| `VRCToolsDataSync.Cli` | `push` / `pull` / `status` を提供するコンソール |
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

`--cloud` を省略した場合は `%AppData%\VRCToolsDataSync\settings.json` に保存されたパスを使用する。

### 終了コード

| コード | 意味 |
| --- | --- |
| 0 | 成功 |
| 1 | 想定外エラー |
| 2 | 設定不備 (クラウドフォルダ未指定 / 存在しない) |
| 3 | コンフリクト（リモートがローカルの最終 Pull より新しい） |
| 4 | 同期対象が存在しない |
| 5 | プロセス実行中で同期不可 |

## GUI

```powershell
dotnet run --project src\VRCToolsDataSync.App
```

設定カードで OneDrive フォルダを選択 → 保存 → 各ツールのカードから Push/Pull。コンフリクト発生時はダイアログで「先に Pull」「強制 Push」「キャンセル」を選択する。

## リリースビルド

ローカルで self-contained な実行ファイルを作る場合:

```powershell
# x64 (既定)
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1

# arm64 など他アーキ
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Arch arm64
```

出力先は `artifacts/win-<arch>/{app,cli}/` と `artifacts/VRCToolsDataSync-win-<arch>.zip`。`app/VRCToolsDataSync.App.exe` が GUI、`cli/VRCToolsDataSync.Cli.exe` が CLI。

GitHub Actions では `v*` タグの push で自動的に x64 / arm64 をビルドし、Draft の GitHub Release に zip を添付する（`.github/workflows/release.yml`）。手動で動かしたい場合は Actions タブから `release` ワークフローの **Run workflow** で実行できる。

## 第三者プロダクトに関する免責

本ツールは [VRCX](https://github.com/vrcx-team/VRCX)（vrcx-team, MIT License）および VRC Friend Connect（たぴおかシステムズ, クローズドソース）の作者・開発元と一切の提携・支援関係はありません。

- 本ツールは VRCX および VRC Friend Connect の本体コードやバイナリを再配布しません。ユーザーのローカル PC 上に存在する各アプリのデータファイル（SQLite / JSON / メモ）をコピー・スナップショット化し、OneDrive のローカル同期フォルダ経由で別 PC に反映するのみです。
- 本ツールは VRC Friend Connect のスキーマ解析・改変・リバースエンジニアリングを行いません。`VACUUM INTO` を含む SQLite 操作はファイル単位の取り扱いに留まります。
- VRCX および VRC Friend Connect 各々の利用規約遵守はユーザー自身の責任です。

## ライセンス

[LICENSE](./LICENSE) を参照。

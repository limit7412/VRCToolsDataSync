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
- **マージ戦略**: 既定は last-writer-wins（置き換え）。VRCX と VRC Friend Connect のそれぞれについて、試験的なマージ同期を別々に有効にできる（[docs/merge-sync.md](docs/merge-sync.md)）。Pull 前にローカルを `%AppData%\VRCToolsDataSync\backup\<tool>\<timestamp>\` へ自動退避（既定10世代）
- **未同期の変更の保護**: 同期のたびに、DB のうち手で編集するデータと追加専用の履歴だけの要約を `%AppData%\VRCToolsDataSync\base\` に残す。Pull は、リモートが新しくても手元の要約が前回から変わっていれば上書きしない。マージ同期が有効ならマージして Push し、無効なら見送って知らせる。手元の変更を捨てて上書きするのは、非常用の強制 Pull に限る（[docs/emergency.md](docs/emergency.md)）
- **転送の省略**: manifest に記録した SHA-256 と手元のファイルが一致する場合、送信も取得も行わない
- **プロセス検知ガード**: 同期対象のツール (`VRCX`, `VRC Friend Connect`) が実行中の場合は WAL ロックによる DB 破損を避けるため同期を拒否
- **暗号化**: 保存先のデータは暗号化しない。S3 互換モードのシークレットアクセスキーのみ、Windows の DPAPI で保護して `settings.json` に保存する

保存先に置くデータの形は [docs/architecture.md](docs/architecture.md) にまとめてある。

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

## ドキュメント

使い方の詳細と込み入った話は `docs/` に分けてある。

| 文書 | 内容 |
| --- | --- |
| [docs/storage.md](docs/storage.md) | 保存先の選び方 (ローカル同期フォルダと S3 互換ストレージの違い、費用) |
| [docs/gui.md](docs/gui.md) | GUI の操作、Windows ログイン時の自動起動 |
| [docs/cli.md](docs/cli.md) | CLI の使用例と終了コード |
| [docs/merge-sync.md](docs/merge-sync.md) | マージ同期 (試験的) の対象・規則と切り替え方 (VRCX / VRC Friend Connect) |
| [docs/emergency.md](docs/emergency.md) | 非常用の強制 Push と強制 Pull |
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

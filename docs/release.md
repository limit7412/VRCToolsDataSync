# CI とリリース

## CI

`.github/workflows/ci.yml` が PR と master への push をトリガーに Release 構成でビルドする。
solution 全体 ([README のセットアップ手順](../README.md#セットアップ)と同じ `dotnet build VRCToolsDataSync.slnx`) と、RID と Platform を明示した `VRCToolsDataSync.App` の 2 通りを通す。前者は書いてあるとおりの手順が動くことの確認で、後者はリリースに近い経路の確認にあたる。
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

リリースは **stable** (安定版) と **test** (プレリリース) の 2 チャンネルに分かれている (issue #45)。
GitHub Actions がどちらも x64 と arm64 をビルドし、GitHub Release に zip を添付する。

| トリガー | ワークフロー | タグ | リリース |
| --- | --- | --- | --- |
| master への push | prerelease.yml | 直近の安定版タグの patch を一つ進めた `X.Y.(Z+1)-testN` を自動採番 | プレリリースとして公開 |
| `0.0.4` 形式のタグの push | release.yml | push したタグ | Draft |
| Actions タブからの手動実行 | release.yml | 入力したタグ名（空ならアーティファクトのみ） | Draft |

master へのマージは test チャンネルのプレリリースになる。ただし成果物の中身が変わらない push (tests やドキュメントのみ) では作られない。
安定版は人が出す。`X.Y.Z` のタグを push して作られた Draft を確認して公開するか、リリース画面から手動で作成する。
minor や major を上げたいときも、目的の番号のタグを push すればよい。

ビルドの前にタグを決め、`-p:Version` で成果物へ版を埋め込む。アプリはこの版を自動アップデートの「実行中の版」として使う。

master への push が短い間隔で続いた場合、待機中のワークフローは後続の実行に置き換えられ、プレリリースは一本にまとまる。
番号は一つだけ進み、まとめられた変更はそのリリースのノートに含まれる (ノートは直前のタグからの差分で生成される)。

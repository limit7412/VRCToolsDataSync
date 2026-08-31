# 保存先のレイアウトとコードの構成

保存先に置くデータの形と、プロジェクト・層の分け方をまとめる。
使い方は [README](../README.md) を参照。

## 保存先のレイアウト

ローカル同期フォルダではフォルダ構造、S3 互換ストレージではオブジェクトキーとして、同じ形を使う。

```text
<保存先のルート>/
  manifest.json                    # ツール別の version / machineName / files[]
  blobs/
    <sha256>                       # 実データ。置き場所は内容から決まる
```

実データの置き場所は**内容の SHA-256 から決める**。`manifest.json` の `files[]` が、ツールの中での位置 (`vrcx/latest.sqlite3` など) と、その実体がある `blobs/<sha256>` を対応付ける。

```json
{
  "relativePath": "vrcx/latest.sqlite3",
  "sha256": "9f2c…",
  "blobKey": "blobs/9f2c…"
}
```

固定のキーへ上書きしないので、**同じキーには常に同じ中身しか入らない**。2 台が同時に Push しても、manifest の記録と実体がずれることがない。同じ内容を二度送らずに済む利点もある (変わっていないファイルはキーも変わらないため、既にあるものとして送信を省ける)。

参照されなくなった実体はその場では消さない。他の PC が送っている最中のものを巻き込まないよう、猶予期間を置いて削除する (Push の後始末として 1 日 1 回自動で走るほか、GUI の「今すぐ解放」と `storage gc` で手動でも実行できる)。

S3 互換モードでは、この全体をバケット内の任意の接頭辞の下に置ける（`--prefix`）。

## 構成

| プロジェクト | 役割 |
| --- | --- |
| `VRCToolsDataSync.Core` | 設定 / パス解決 / プロセス検知 / SQLite スナップショット / ハッシュ / バックアップ / manifest / 保存先の抽象 / SyncService |
| `VRCToolsDataSync.Cli` | `push` / `pull` / `status` / `storage` を提供するコンソール |
| `VRCToolsDataSync.App` | WinUI 3 (.NET 10) の GUI。設定編集と Push/Pull、コンフリクトの解決 |

### Core の層 (issue #50)

`Core` はフォルダと名前空間で層を分けている。依存は下向きだけに保つ。

| フォルダ | 置くもの | 依存してよい先 |
| --- | --- | --- |
| `Domain` | モデル、境界 (`ISyncStorage` `ISyncService` `IReleaseRepository`)、外部に触れない規則 | なし |
| `Infra` | 境界の実装と、外部に触れるもの (S3 / ファイル / レジストリ / GitHub / SQLite / プロセス) | `Domain` |
| `UseCase` | 手順の組み立て (Push/Pull、起動時と終了時の同期、自動同期、容量の解放、更新確認) | `Domain` `Infra` |

`Cli` と `App` は合成ルートで、3 つすべてを参照する。

**`Domain` はどの層も参照しない。`Infra` は `UseCase` を参照しない。** 上の層への言及は `<see cref>` ではなく `<c>` で書く。参照を張ってしまうと、コードでは切れている向きが doc 経由で戻る。

`Infra` と `UseCase` の境目は「外に触れるか」で引いている。`ManifestStore` は `ISyncStorage` を通してしか読み書きしないので `UseCase`、`S3SyncStorage` は実際に通信するので `Infra` である。同期先の実装が manifest のキーを要るため、キーだけは `Domain` の `ManifestKeys` に置いてある。

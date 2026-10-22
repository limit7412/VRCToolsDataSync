# 非常用の強制 Push と強制 Pull

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

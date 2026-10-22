using VRCToolsDataSync.Core.Domain;

namespace VRCToolsDataSync.Core.UseCase;

/// <summary>ツールをまたいで同じ文面で返す結果。</summary>
internal static class SyncResults
{
    /// <summary>
    /// Pull で、手元に未 Push の変更があったため上書きを見送った結果。マージ同期が有効なら
    /// 呼び出し側 (SyncRunner) がこの結果を受けてマージに切り替えるので、この文面は
    /// マージが無効なときだけ利用者に届く。
    /// </summary>
    public static SyncResult LocalChangesPending(string displayName, long remoteVersion, IReadOnlyList<string> changedTables)
    {
        const int shown = 3;
        var tables = string.Join(", ", changedTables.Take(shown))
                     + (changedTables.Count > shown ? $" ほか {changedTables.Count - shown} 件" : string.Empty);
        return new SyncResult
        {
            Outcome = SyncOutcome.LocalChangesPending,
            RemoteVersion = remoteVersion,
            Message = $"リモートに新しい {displayName} のデータ (v{remoteVersion}) がありますが、" +
                      $"この PC にも前回の同期の後の変更 ({tables}) があるため、上書きを見送りました。" +
                      "両方を残すにはマージ同期を有効にしてもう一度 Pull してください。" +
                      "この PC の変更を捨てて同期先に合わせるには、非常用の強制 Pull を使ってください。",
        };
    }
}

using VRCToolsDataSync.Core.Domain;

namespace VRCToolsDataSync.Core.UseCase;

/// <summary>ツールをまたいで同じ文面で返す結果。</summary>
internal static class SyncResults
{
    /// <summary>
    /// 起動時の自動 Pull で、手元に未 Push の変更があったため上書きを見送った結果。
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
                      "Push すると競合の問い合わせが出るので、そこで Pull か強制 Push を選んでください。",
        };
    }
}

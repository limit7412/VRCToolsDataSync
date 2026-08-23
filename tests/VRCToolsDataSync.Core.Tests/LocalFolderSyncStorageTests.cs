using VRCToolsDataSync.Core.Storage;
using VRCToolsDataSync.Core.Sync;
using Xunit;

namespace VRCToolsDataSync.Core.Tests;

/// <summary>
/// 同期フォルダ実装の性質を固定する。ここだけは偽物ではなく実ファイルシステムで
/// 確かめる。壊れた 3 件 (時刻の引き継ぎ、同時 Commit、掃除の判定) は、
/// いずれもファイルシステムの挙動そのものが原因だったため。
/// </summary>
public sealed class LocalFolderSyncStorageTests : IDisposable
{
    private readonly string _root;
    private readonly string _work;

    public LocalFolderSyncStorageTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vrctds-folder-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "sync");
        _work = Path.Combine(baseDir, "work");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private LocalFolderSyncStorage Storage() => new(_root);

    /// <summary>最終更新時刻が古いファイルを用意する。</summary>
    private string WriteAgedFile(string name, string content, TimeSpan age)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact(DisplayName = "Commit した実体には、コピー元ではなく書いた時刻が刻まれる")]
    public void CommittedBlobCarriesTheWriteTimeNotTheSourceTime()
    {
        // #36 で見つかった不具合の回帰。File.Copy はコピー元の最終更新時刻を
        // 引き継ぐ。回収はこの時刻で猶予期間を測るため、何か月も前の設定ファイルを
        // 送ると、書いた直後の実体が最初から猶予期間外になる。
        var storage = Storage();
        var source = WriteAgedFile("config.json", "aged", TimeSpan.FromDays(90));

        var (file, sent) = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/config.json");

        Assert.True(sent);
        var stored = storage.Stat(file.BlobKey!);
        Assert.NotNull(stored);
        Assert.True(
            stored!.LastModified > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            $"書いた時刻が刻まれること (実際: {stored.LastModified:o})");
    }

    [Fact(DisplayName = "同じ内容を二度 Commit しても失敗しない")]
    public void CommittingTheSameContentTwiceSucceeds()
    {
        // 内容から決まるキーなので、確定先が既にあることは普通に起こる。
        var storage = Storage();
        var source = WriteAgedFile("note.txt", "same content", TimeSpan.FromDays(1));

        var first = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/notes/a.txt");
        var second = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/notes/b.txt");

        Assert.Equal(first.File.BlobKey, second.File.BlobKey);
        Assert.True(storage.Exists(first.File.BlobKey!));
    }

    [Fact(DisplayName = "同じ確定先へ並行に Commit しても全部成功する")]
    public void ConcurrentCommitsToTheSameDestinationAllSucceed()
    {
        // 逐次に送ると 2 回目は必ず「確定先が既にある」経路を通るため、
        // File.Exists の確認から File.Move までに別の Commit が割り込む経路を
        // 一度も通らない。同時に走らせて、そちらも通るようにする。
        //
        // 割り込みが起きるかどうかは実行のたびに変わる。ここで固定しているのは
        // 「同じ内容を同時に送っても、全部成功して実体が 1 つ残る」という結果で、
        // どちらの経路を通ったかではない。
        const int degree = 8;
        var storage = Storage();
        var sources = Enumerable.Range(0, degree)
            .Select(i => WriteAgedFile($"note{i}.txt", "identical content", TimeSpan.FromDays(1)))
            .ToArray();

        // スレッドプールではなく実スレッドを使う。Parallel.For や Task.Run だと
        // 8 本が同時に走る保証が無く、Barrier で待ち合わせると止まりうる。
        var barrier = new Barrier(degree);
        var results = new ManifestFile?[degree];
        var failures = new Exception?[degree];
        var threads = Enumerable.Range(0, degree)
            .Select(i => new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    results[i] = SyncTransfer.Send(
                        storage, Array.Empty<ManifestFile>(), sources[i], $"fc/notes/{i}.txt").File;
                }
                catch (Exception ex)
                {
                    failures[i] = ex;
                }
            }))
            .ToArray();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Commit が終わらない");

        Assert.All(failures, f => Assert.Null(f));
        // 内容が同じなのでキーも 1 つに揃い、実体も 1 つだけ残る。
        Assert.Single(results.Select(r => r!.BlobKey).Distinct());
        Assert.Single(storage.List(BlobKeys.Prefix));
    }

    [Fact(DisplayName = "既にある実体を送り直すと時刻が刻み直される")]
    public void RecommittingRefreshesTheWriteTime()
    {
        // 参照が切れて猶予期間を過ぎた実体を、別の世代が再び参照することがある。
        // 刻み直さないと、参照され直した直後に回収されうる。
        var storage = Storage();
        var source = WriteAgedFile("note.txt", "reused", TimeSpan.FromDays(1));
        var (file, _) = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/notes/a.txt");

        var blobPath = StorageKey.ToLocalPath(_root, file.BlobKey!);
        File.SetLastWriteTimeUtc(blobPath, DateTime.UtcNow - TimeSpan.FromDays(30));

        SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/notes/b.txt");

        var stored = storage.Stat(file.BlobKey!);
        Assert.NotNull(stored);
        Assert.True(
            stored!.LastModified > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            $"送り直しで刻み直されること (実際: {stored.LastModified:o})");
    }

    [Fact(DisplayName = "列挙は書き出し中のファイルを含めない")]
    public void ListSkipsFilesBeingWritten()
    {
        var storage = Storage();
        var source = WriteAgedFile("note.txt", "committed", TimeSpan.FromDays(1));
        var (file, _) = SyncTransfer.Send(storage, Array.Empty<ManifestFile>(), source, "fc/notes/a.txt");

        // 書き出し中のファイルを模して、blobs/ 直下に置く。
        var blobDirectory = StorageKey.ToLocalPath(_root, BlobKeys.Prefix.TrimEnd('/'));
        File.WriteAllText(Path.Combine(blobDirectory, ".building-" + Guid.NewGuid().ToString("N")), "in flight");

        var listed = storage.List(BlobKeys.Prefix).Select(o => o.Key).ToList();

        Assert.Equal(file.BlobKey, Assert.Single(listed));
    }

    [Fact(DisplayName = "存在しないキーの削除は何もしない")]
    public void DeletingAMissingKeyIsANoOp()
    {
        Storage().Delete(BlobKeys.Prefix + new string('a', 64));
    }

    [Fact(DisplayName = "存在しないキーの Stat は null を返す")]
    public void StatReturnsNullForAMissingKey()
        => Assert.Null(Storage().Stat(BlobKeys.Prefix + new string('a', 64)));

    [Fact(DisplayName = "Commit しなければ同期先は変わらない")]
    public void DiscardingAStagedUploadLeavesNothingBehind()
    {
        var storage = Storage();
        using (var staged = storage.BeginUpload())
        {
            File.WriteAllText(staged.LocalPath, "never committed");
        }

        Assert.Empty(storage.List(BlobKeys.Prefix));
    }

    [Fact(DisplayName = "読み書きと削除ができないフォルダは設定を保存する前に弾く")]
    public void VerifyAccessRejectsAMissingFolder()
    {
        var storage = new LocalFolderSyncStorage(Path.Combine(_root, "does-not-exist"));

        Assert.Throws<SyncStorageConfigurationException>(storage.VerifyAccess);
    }

    [Fact(DisplayName = "使えるフォルダなら接続確認は通る")]
    public void VerifyAccessAcceptsAWritableFolder()
    {
        Storage().VerifyAccess();

        // 検査に使ったファイルを残さない。
        Assert.Empty(Directory.GetFiles(_root));
    }
}

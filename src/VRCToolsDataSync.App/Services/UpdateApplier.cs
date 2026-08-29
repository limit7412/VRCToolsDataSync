using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using VRCToolsDataSync.Core.Settings;
using VRCToolsDataSync.Core.Update;

namespace VRCToolsDataSync_App.Services;

/// <summary>
/// 取得しておいた更新の適用を更新ヘルパ (cli の self-update apply) へ渡す
/// (issue #45 第 3 段階)。
/// <para>
/// 実行中の App は app\ 配下の DLL を掴んでいて、自分では置き換えられない。
/// 展開した新しい一式の cli をヘルパとして起動し、App の終了を待ってから
/// ディレクトリを入れ替えてもらう。ヘルパは展開先から動くため、置き換える
/// 対象のどれも掴まない。
/// </para>
/// </summary>
public static class UpdateApplier
{
    /// <summary>
    /// 起動シーケンスの先頭で呼ぶ。取得しておいた更新があれば、置き換えを
    /// ヘルパへ渡して true を返す。呼び出し側は App を立ち上げずに終了する。
    /// <para>
    /// 多重起動の抑止より後に呼ぶこと。既に常駐しているプロセスがある状態で
    /// 置き換えると、起動した新しい側が抑止に当たって即座に終わり、
    /// 置き換えだけが済んだ形になる。
    /// </para>
    /// </summary>
    public static bool TryHandOverToStaged(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("VRCToolsDataSync.App.SelfUpdate");

        // 適用に関わる間はクロスプロセスのロックを握る。ヘルパが動いている間は
        // ここで待たされ、こちらが展開している間はヘルパの側が待つ。握ったまま
        // 展開とヘルパの起動まで済ませるのは、その間に別の App が起動して同じ
        // 展開先を作り直すと、動いているヘルパの足元を崩すためである。
        using var applyLock = AcquireApplyLock(logger);

        // 取れなかった場合は更新に触らない。触れば、ロックが防ぐはずの競合
        // (動いているヘルパの展開元を作り直す等) がそのまま起きる。起動は続ける。
        // 取得しておいたものは残るので、次の起動が適用し直す。
        if (applyLock is null) return false;

        try
        {
            var stage = new UpdateStage(logger: logger);
            var root = UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory);

            // ここでは何も捨てない (discardMismatches: false)。この時点は置き換え
            // 直後の最初の起動かもしれず、退避した .old と取得済みの ZIP は、
            // この後の初期化が失敗した場合の復旧の材料になる。後始末は起動が
            // 成り立った後に CleanUpAfterSuccessfulStart が行う。
            var channel = LoadChannel(logger);
            var staged = stage.TryLoadVerified(channel, RunningVersion.Current(), discardMismatches: false);
            if (staged is null) return false;

            if (root is null)
            {
                // dotnet run や bin\ 配下の手元ビルド。作業ツリーを置き換える
                // わけにはいかないので、取得したものは残したまま適用しない。
                LogQuietly(() => logger.LogInformation("配布の形ではないため、取得済みの {Tag} は適用しない", staged.Tag));
                return false;
            }

            if (!TrySpawnUpdater(stage, root, logger)) return false;

            LogQuietly(() => logger.LogInformation("更新 {Tag} の適用をヘルパへ渡して終了する", staged.Tag));
            return true;
        }
        catch (Exception ex)
        {
            // 適用に入れなくても、今の版のまま起動は続けられる。
            LogQuietly(() => logger.LogWarning(ex, "取得済みの更新の適用に入れなかった"));
            return false;
        }
        finally
        {
            // ヘルパは起動直後にこのロックを待つ。ここで手放して先へ進ませる。
            applyLock.Release();
        }
    }

    /// <summary>
    /// 置き換えまわりの後始末。ウィンドウを立てられた後に呼ぶ。
    /// <para>
    /// 退避した .old と、置き換え済み・チャンネル外・壊れた取得をここで捨てる。
    /// 起動シーケンスの先頭で捨てると、置き換えた新しい版が初期化の途中で
    /// 失敗した場合に、旧版と取得済みの ZIP の両方を失って復旧できなくなる。
    /// 起動が成り立った後なら、どちらも復旧の材料として残す必要が無い。
    /// </para>
    /// </summary>
    /// <param name="stage">
    /// 常駐側が使っている置き場所。UpdateManager から渡す。省略すると既定の
    /// 置き場所を見る。
    /// </param>
    public static void CleanUpAfterSuccessfulStart(ILoggerFactory loggerFactory, UpdateStage? stage = null)
    {
        var logger = loggerFactory.CreateLogger("VRCToolsDataSync.App.SelfUpdate");
        try
        {
            // 後始末も適用と同じロックの下で行う。退避や展開先を消す操作なので、
            // 動いているヘルパと重なれば足元を崩す。取れなければ何もしない
            // (残ったものは次の起動の後始末が拾う)。
            var done = TryWithApplyLock(logger, () =>
            {
                var root = UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory);
                if (root is not null)
                {
                    UpdateInstaller.DiscardPrevious(root, logger);
                }

                // まだ適用できる取得 (非配布形で適用しなかった等) は残り、
                // 合わなくなった取得はここで捨てられる。
                stage ??= new UpdateStage(logger: logger);
                _ = stage.TryLoadVerified(LoadChannel(logger), RunningVersion.Current());

                // 途中で終わった取得と、適用が済んだ後に残る展開先を片付ける。
                stage.DiscardIncomplete();
                stage.DiscardIncoming();
                if (!File.Exists(stage.ZipPath))
                {
                    DeleteDirectoryQuietly(stage.ExtractDirectory, logger);
                }
            });

            if (!done)
            {
                LogQuietly(() => logger.LogInformation("更新の適用中のため、後始末は次の起動へ回す"));
            }
        }
        catch (Exception ex)
        {
            LogQuietly(() => logger.LogWarning(ex, "更新の後始末に失敗した"));
        }
    }

    /// <summary>
    /// 更新の適用に関わる間だけ握るクロスプロセスのロック。
    /// <para>
    /// 動いているヘルパがあれば、その完了まで待つ。待ちきれなければ null を
    /// 返す。呼び出し側は更新に触らずに進むこと。ここで起動そのものを諦めると、
    /// ヘルパが固まったときに App を開く手立てが無くなる。逆に、握れないまま
    /// 展開や昇格へ進めば、ロックを置いた意味が無い。
    /// </para>
    /// </summary>
    private static ApplyLock? AcquireApplyLock(ILogger logger)
    {
        Mutex? mutex = null;
        try
        {
            mutex = UpdateStage.CreateApplyMutex(UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory));
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(UpdaterWaitTimeout);
            }
            catch (AbandonedMutexException)
            {
                // ヘルパが握ったまま落ちた。所有権はこちらに渡っている。
                acquired = true;
            }

            if (acquired) return new ApplyLock(mutex);

            LogQuietly(() => logger.LogWarning("更新ヘルパの完了を待ちきれなかった。更新には触らずに続ける"));
            mutex.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            LogQuietly(() => logger.LogWarning(ex, "更新のロックを取れなかった"));
            mutex?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// ヘルパの完了を待つ上限。ヘルパは元の App の終了を最大 10 分待つため、
    /// それを見込んだ長さにする。
    /// </summary>
    private static readonly TimeSpan UpdaterWaitTimeout = TimeSpan.FromMinutes(11);

    /// <summary>
    /// 握った <see cref="Mutex"/> の後始末をまとめる。
    /// 解放は所有したスレッドから行う必要があるため、明示的な
    /// <see cref="Release"/> と <see cref="Dispose"/> を分けてある。
    /// </summary>
    private sealed class ApplyLock : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _released;

        public ApplyLock(Mutex mutex) => _mutex = mutex;

        public void Release()
        {
            if (_released) return;
            _released = true;
            try { _mutex.ReleaseMutex(); } catch { /* best-effort */ }
        }

        public void Dispose()
        {
            Release();
            _mutex.Dispose();
        }
    }

    /// <summary>
    /// 更新のロックを取ってから <paramref name="action"/> を行う。取れなければ
    /// 何もせず false を返す。
    /// <para>
    /// 取得した ZIP を置き換え待ちへ昇格させる側と、後始末の側から使う。昇格は
    /// staged の ZIP を入れ替えて展開先を消すため、適用の側と重なると、起動前の
    /// ヘルパを消したり、動いているヘルパの展開元を欠いたりする。
    /// </para>
    /// <para>
    /// 取れないまま行っては、ロックを置いた意味が無い。見送っても行き詰まりは
    /// しない。昇格なら次の確認が取得からやり直し、後始末なら次の起動が拾う。
    /// </para>
    /// </summary>
    public static bool TryWithApplyLock(ILogger logger, Action action)
    {
        using var applyLock = AcquireApplyLock(logger);
        if (applyLock is null) return false;

        try
        {
            action();
            return true;
        }
        finally
        {
            applyLock.Release();
        }
    }

    /// <summary>
    /// ログの失敗を流れから切り離す。
    /// <para>
    /// ログの出力先 (%AppData%) が書き込み不可だったり容量が尽きていたりすると、
    /// このリポジトリのロガーは例外を投げる。ここでそれが飛ぶと、起動を続ける
    /// ための catch から抜けて App が立ち上がらなくなる。
    /// </para>
    /// </summary>
    private static void LogQuietly(Action write)
    {
        try { write(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// ZIP を展開してヘルパを起動する。呼び出し側はこの後で App を終了させる。
    /// ヘルパは --wait-pid でこのプロセスの終了を待ってから置き換える。
    /// <para>
    /// 更新のロックを握った状態で呼ぶこと。展開先を作り直すため、動いている
    /// ヘルパと重なると足元を崩す。
    /// </para>
    /// </summary>
    public static bool TrySpawnUpdater(UpdateStage stage, string installRoot, ILogger logger)
    {
        var extracted = stage.ExtractForApply();
        var updater = Path.Combine(extracted, "cli", UpdateInstaller.CliExecutableName);
        if (!File.Exists(updater))
        {
            // ZIP の形が想定と違う。展開し直しても同じなので取得ごと捨てる。
            LogQuietly(() => logger.LogWarning("展開した一式に更新ヘルパが無いため捨てる: {Path}", updater));
            stage.Discard();
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = updater,
            WorkingDirectory = extracted,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("self-update");
        startInfo.ArgumentList.Add("apply");
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(extracted);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(installRoot);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--relaunch");

        Process.Start(startInfo);
        return true;
    }

    private static UpdateChannel LoadChannel(ILogger logger)
    {
        try
        {
            return new SettingsStore().Load().Update.Channel;
        }
        catch (Exception ex)
        {
            // 設定を読めない起動でも、安全側 (stable) の判定で先へ進める。
            LogQuietly(() => logger.LogWarning(ex, "更新チャンネルを読めなかったため stable として扱う"));
            return UpdateChannel.Stable;
        }
    }

    private static void DeleteDirectoryQuietly(string path, ILogger logger)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogQuietly(() => logger.LogWarning(ex, "消せなかった: {Path}", path));
        }
    }
}

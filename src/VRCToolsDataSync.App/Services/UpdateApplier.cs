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

        try
        {
            // 適用に関わる間はクロスプロセスのロックを握る。ヘルパが動いている
            // 間はここで待たされ、こちらが展開している間はヘルパの側が待つ。
            // 握ったまま展開とヘルパの起動まで済ませるのは、その間に別の App が
            // 起動して同じ展開先を作り直すと、動いているヘルパの足元を崩すため。
            //
            // 取れなかった場合は更新に触らない。触れば、ロックが防ぐはずの競合が
            // そのまま起きる。起動は続ける。取得しておいたものは残るので、次の
            // 起動が適用し直す。
            var handed = false;
            _ = TryWithApplyLock(logger, () =>
            {
                var stage = new UpdateStage(logger: logger);
                var root = UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory);

                // ここでは何も捨てない (discardMismatches: false)。この時点は
                // 置き換え直後の最初の起動かもしれず、退避した .old と取得済みの
                // ZIP は、この後の初期化が失敗した場合の復旧の材料になる。
                // 後始末は起動が成り立った後に CleanUpAfterSuccessfulStart が行う。
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
                handed = true;

                // ロックは手放さない。このプロセスの終了で、待っているヘルパへ
                // 渡る。
                return true;
            });

            return handed;
        }
        catch (Exception ex)
        {
            // 適用に入れなくても、今の版のまま起動は続けられる。
            LogQuietly(() => logger.LogWarning(ex, "取得済みの更新の適用に入れなかった"));
            return false;
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

                // 後始末はここで終わり。ロックは手放す。
                return false;
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
        => ApplyLock.TryAcquire(UpdaterWaitTimeout, logger);

    /// <summary>
    /// ヘルパの完了を待つ上限。ヘルパは元の App の終了を最大 10 分待つため、
    /// それを見込んだ長さにする。
    /// </summary>
    private static readonly TimeSpan UpdaterWaitTimeout = TimeSpan.FromMinutes(11);

    /// <summary>
    /// 握った <see cref="Mutex"/> と、それを所有するスレッドの組。
    /// <para>
    /// <see cref="Mutex"/> の所有権はスレッドに紐づき、取ったスレッドからしか
    /// 手放せない。ここでは、取得から解放までを専用のスレッドに閉じ込め、
    /// 外からは <see cref="Dispose"/> の合図だけで手放せるようにする。
    /// そうしないと、取得が <c>Task.Run</c> の中で、解放が UI スレッドから、
    /// というような組み合わせで解放が黙って失敗する。
    /// </para>
    /// <para>
    /// スレッドは背景スレッドなので、合図が来ないまま残ってもプロセスの終了を
    /// 妨げない。所有権はそのときに OS が手放し、待っているヘルパへ渡る。
    /// </para>
    /// </summary>
    private sealed class ApplyLock : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private bool _held;

        private ApplyLock() { }

        /// <summary>取れなければ null を返す。</summary>
        public static ApplyLock? TryAcquire(TimeSpan timeout, ILogger logger)
        {
            var owner = new ApplyLock();
            try
            {
                var thread = new Thread(() => owner.Own(timeout, logger))
                {
                    IsBackground = true,
                    Name = "VRCToolsDataSync.UpdateApplyLock",
                };
                thread.Start();
            }
            catch (Exception ex)
            {
                LogQuietly(() => logger.LogWarning(ex, "更新のロックを取れなかった"));
                return null;
            }

            owner._acquired.Wait();
            if (owner._held) return owner;

            owner.Dispose();
            return null;
        }

        /// <summary>専用スレッドの中身。取得を伝えた後、解放の合図まで持ち続ける。</summary>
        private void Own(TimeSpan timeout, ILogger logger)
        {
            Mutex? mutex = null;
            try
            {
                mutex = UpdateStage.CreateApplyMutex(UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory));
                try
                {
                    _held = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    // ヘルパが握ったまま落ちた。所有権はこちらに渡っている。
                    _held = true;
                }

                if (!_held)
                {
                    LogQuietly(() => logger.LogWarning("更新ヘルパの完了を待ちきれなかった。更新には触らずに続ける"));
                }
            }
            catch (Exception ex)
            {
                LogQuietly(() => logger.LogWarning(ex, "更新のロックを取れなかった"));
                _held = false;
            }
            finally
            {
                _acquired.Set();
            }

            if (!_held)
            {
                mutex?.Dispose();
                return;
            }

            _release.Wait();
            try { mutex!.ReleaseMutex(); } catch { /* best-effort */ }
            mutex!.Dispose();
        }

        /// <summary>手放す合図を送る。どのスレッドから呼んでもよい。</summary>
        public void Dispose() => _release.Set();
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
    /// <para>
    /// <paramref name="action"/> が true を返した場合はロックを手放さない。
    /// ヘルパを起こした後の話で、ここで手放すと、こちらが終わるまでの間に
    /// 別の待ち手 (裏で走っている取得の昇格) が先に握りうる。昇格は展開先を
    /// 消すため、起こしたばかりのヘルパの <c>--source</c> が欠ける。握ったまま
    /// 終われば、所有権は OS がヘルパへ渡す (ヘルパは放棄された mutex を
    /// 取得済みとして扱う)。
    /// </para>
    /// </summary>
    /// <param name="action">
    /// ロックの下で行うこと。ロックをこのプロセスの終了まで握り続けるなら
    /// true を返す。
    /// </param>
    /// <returns>ロックを取れて <paramref name="action"/> を行えたか。</returns>
    public static bool TryWithApplyLock(ILogger logger, Func<bool> action)
    {
        var applyLock = AcquireApplyLock(logger);
        if (applyLock is null) return false;

        var keep = false;
        try
        {
            keep = action();
            return true;
        }
        finally
        {
            if (keep)
            {
                // 掴んだままにする。参照を手放すと、GC が Mutex を片付けた
                // 拍子に所有権まで落ちる。
                _heldUntilExit = applyLock;
            }
            else
            {
                applyLock.Dispose();
            }
        }
    }

    /// <summary>
    /// プロセスの終了まで握り続けるロック。ここで参照を持つのは、GC に
    /// 片付けさせないためだけである。
    /// </summary>
    private static ApplyLock? _heldUntilExit;

    /// <summary>
    /// 終了まで握るはずだったロックを手放す。終了が取り消された経路から呼ぶ。
    /// <para>
    /// 「再起動して適用」の終了シーケンスは、必ずプロセスの終了に至るとは
    /// 限らない (ログオフの取り消しや、先に始まっていた終了処理との重なり)。
    /// 生き残ったまま握り続けると、この後の昇格も、起こしたヘルパの適用も
    /// 止まったままになる。
    /// </para>
    /// </summary>
    public static void ReleaseHeldApplyLock()
    {
        var held = Interlocked.Exchange(ref _heldUntilExit, null);
        held?.Dispose();
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

        // 起こしたヘルパがすぐ落ちていないか見る。exe があっても、自己完結の
        // ランタイムを欠いた一式なら、プロセスの生成だけ成功して引数を読む前に
        // 終わる。それを見ずに App を終わらせると、置き換えも起動し直しも
        // されないまま画面が消え、次の起動も同じところで終わる。
        //
        // ヘルパは最初にこちらが握っているロックを待つので、無事なら生きたままである。
        using var process = Process.Start(startInfo);
        if (process is null || process.WaitForExit(StartupProbeMilliseconds))
        {
            LogQuietly(() => logger.LogWarning(
                "更新ヘルパが起動直後に終了したため取得を捨てる (終了コード {Code})",
                process?.ExitCode));
            stage.Discard();
            return false;
        }

        return true;
    }

    /// <summary>
    /// 起こしたヘルパが立ち上がったと見なすまでの猶予。ヘルパは最初に
    /// こちらが握っているロックを待つので、これを越えて生きていれば動いている。
    /// </summary>
    private const int StartupProbeMilliseconds = 3000;

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

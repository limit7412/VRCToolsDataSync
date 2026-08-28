using System;
using System.Diagnostics;
using System.IO;
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
            var stage = new UpdateStage(logger: logger);
            var root = UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory);

            // 前回の置き換えが退避した .old は、次の起動、つまりここで消す。
            if (root is not null)
            {
                UpdateInstaller.DiscardPrevious(root, logger);
            }

            var channel = LoadChannel(logger);
            var staged = stage.TryLoadVerified(channel, RunningVersion.Current());
            if (staged is null)
            {
                // 途中で終わった取得と、適用が済んだ後に残る展開先を片付ける。
                // ZIP と記録は TryLoadVerified が「実行中より新しくない」等で
                // 消しているが、展開先だけが残る形はここで拾う。
                stage.DiscardIncomplete();
                if (!File.Exists(stage.ZipPath))
                {
                    DeleteDirectoryQuietly(stage.ExtractDirectory, logger);
                }
                return false;
            }

            if (root is null)
            {
                // dotnet run や bin\ 配下の手元ビルド。作業ツリーを置き換える
                // わけにはいかないので、取得したものは残したまま適用しない。
                logger.LogInformation("配布の形ではないため、取得済みの {Tag} は適用しない", staged.Tag);
                return false;
            }

            if (!TrySpawnUpdater(stage, root, logger)) return false;

            logger.LogInformation("更新 {Tag} の適用をヘルパへ渡して終了する", staged.Tag);
            return true;
        }
        catch (Exception ex)
        {
            // 適用に入れなくても、今の版のまま起動は続けられる。
            logger.LogWarning(ex, "取得済みの更新の適用に入れなかった");
            return false;
        }
    }

    /// <summary>
    /// ZIP を展開してヘルパを起動する。呼び出し側はこの後で App を終了させる。
    /// ヘルパは --wait-pid でこのプロセスの終了を待ってから置き換える。
    /// </summary>
    public static bool TrySpawnUpdater(UpdateStage stage, string installRoot, ILogger logger)
    {
        var extracted = stage.ExtractForApply();
        var updater = Path.Combine(extracted, "cli", "VRCToolsDataSync.Cli.exe");
        if (!File.Exists(updater))
        {
            // ZIP の形が想定と違う。展開し直しても同じなので取得ごと捨てる。
            logger.LogWarning("展開した一式に更新ヘルパが無いため捨てる: {Path}", updater);
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
            logger.LogWarning(ex, "更新チャンネルを読めなかったため stable として扱う");
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
            logger.LogWarning(ex, "消せなかった: {Path}", path);
        }
    }
}

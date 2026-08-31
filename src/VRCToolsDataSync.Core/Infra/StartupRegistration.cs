using Microsoft.Win32;

namespace VRCToolsDataSync.Core.Infra;

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run へのアプリ登録/解除。
/// ログイン時に Windows がこのパスのプログラムを自動起動する。
/// HKCU 配下のためユーザー権限のみで操作可能、管理者権限は不要。
/// </summary>
public static class StartupRegistration
{
    public const string DefaultValueName = "VRCToolsDataSync";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// ウィンドウを出さずにトレイへ常駐して起動する指定 (issue #54)。
    /// <para>
    /// 自動起動だけに効かせたいので、設定ファイルではなく登録するコマンドへ
    /// 付ける。手で起動したときは付かないため、これまでどおりウィンドウが開く。
    /// </para>
    /// </summary>
    public const string MinimizedSwitch = "--minimized";

    public static bool IsRegistered(string valueName = DefaultValueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key is null) return false;
        return key.GetValue(valueName) is string;
    }

    public static string? GetRegisteredCommand(string valueName = DefaultValueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    /// <param name="startMinimized">
    /// ウィンドウを出さずにトレイへ常駐して起動するか (issue #54)。
    /// </param>
    public static void Register(
        string executablePath,
        bool startMinimized = false,
        string valueName = DefaultValueName)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("実行ファイルパスが空です", nameof(executablePath));
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("HKCU\\...\\Run キーを開けませんでした");
        key.SetValue(valueName, BuildCommand(executablePath, startMinimized), RegistryValueKind.String);
    }

    public static void Unregister(string valueName = DefaultValueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;
        if (key.GetValue(valueName) is not null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>登録するコマンド行を組み立てる。</summary>
    internal static string BuildCommand(string executablePath, bool startMinimized)
    {
        // パスに空白が含まれる場合に備えてダブルクオートで囲む
        var command = executablePath.StartsWith("\"")
            ? executablePath
            : $"\"{executablePath}\"";

        return startMinimized ? $"{command} {MinimizedSwitch}" : command;
    }

    /// <summary>
    /// 登録されているコマンドが、トレイへ常駐して起動する指定を持つか (issue #54)。
    /// <para>
    /// 画面のチェックを登録内容から作り直すために使う。設定を別に持たないので、
    /// レジストリを手で書き換えられても表示が食い違わない。
    /// </para>
    /// </summary>
    public static bool StartsMinimized(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        // 実行ファイルのパスは引用符で囲んで書いている。パスの中に同じ綴りが
        // 含まれていても取り違えないよう、閉じ引用符から後ろだけを見る。
        // 引用符で始まらないコマンド (手で書かれたもの) は全体を見る。
        var arguments = command;
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            if (close < 0) return false;
            arguments = command[(close + 1)..];
        }

        return arguments
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(token, MinimizedSwitch, StringComparison.OrdinalIgnoreCase));
    }
}

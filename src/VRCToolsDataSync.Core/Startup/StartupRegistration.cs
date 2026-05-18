using Microsoft.Win32;

namespace VRCToolsDataSync.Core.Startup;

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run へのアプリ登録/解除。
/// ログイン時に Windows がこのパスのプログラムを自動起動する。
/// HKCU 配下のためユーザー権限のみで操作可能、管理者権限は不要。
/// </summary>
public static class StartupRegistration
{
    public const string DefaultValueName = "VRCToolsDataSync";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

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

    public static void Register(string executablePath, string valueName = DefaultValueName)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("実行ファイルパスが空です", nameof(executablePath));
        }

        // パスに空白が含まれる場合に備えてダブルクオートで囲む
        var command = executablePath.StartsWith("\"")
            ? executablePath
            : $"\"{executablePath}\"";

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("HKCU\\...\\Run キーを開けませんでした");
        key.SetValue(valueName, command, RegistryValueKind.String);
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
}

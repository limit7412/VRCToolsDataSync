using System.Reflection;

namespace VRCToolsDataSync.Core.Update;

/// <summary>実行中のプロセスに埋め込まれた版を読む。</summary>
public static class RunningVersion
{
    /// <summary>
    /// エントリアセンブリの InformationalVersion。
    /// <para>
    /// リリースのワークフローが build-release.ps1 の -Version 経由で埋め込む
    /// (0.1.0 / 0.1.0-test1 など)。埋め込まずにビルドすると csproj の既定値
    /// (0.0.0-dev) になり、<see cref="ReleaseVersion.Parse"/> が読めないことで
    /// リリース版と区別できる。
    /// </para>
    /// <para>
    /// SDK は既定でビルド元のコミット ID を "+" 付きで後ろに足す
    /// (0.1.0+abc123)。版の比較には使わないため、ここで落とす。
    /// </para>
    /// </summary>
    public static string Current()
    {
        var informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return "0.0.0-dev";

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

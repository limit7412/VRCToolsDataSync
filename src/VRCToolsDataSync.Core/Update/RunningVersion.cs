using System.Diagnostics;
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

        return WithoutBuildMetadata(informational);
    }

    /// <summary>
    /// 実行ファイルやアセンブリに埋め込まれた版を読む。読めなければ null。
    /// <para>
    /// 動いていない一式の版を知りたいときに使う。展開した更新が、記録のタグ
    /// どおりの版かを適用の前に確かめる用途がある。
    /// </para>
    /// </summary>
    public static string? OfFile(string path)
    {
        try
        {
            var product = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return string.IsNullOrEmpty(product) ? null : WithoutBuildMetadata(product);
        }
        catch (Exception)
        {
            // 読めない形式や消えたファイル。分からないものとして扱う。
            return null;
        }
    }

    /// <summary>SDK が足すコミット ID ("+abc123") を落とす。</summary>
    private static string WithoutBuildMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}

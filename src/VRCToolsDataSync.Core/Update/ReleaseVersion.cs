using System.Text.RegularExpressions;

namespace VRCToolsDataSync.Core.Update;

/// <summary>
/// リリースのタグが表す版 (issue #45)。
/// <para>
/// 運用しているタグは <c>X.Y.Z</c> (安定版) と <c>X.Y.Z-testN</c> (プレリリース) の
/// 2 つだけである。それ以外の綴りは版として扱わず、<see cref="Parse"/> が null を
/// 返すことで比較の対象にならないことを表す。手元ビルドの <c>0.0.0-dev</c> がこれにあたる。
/// </para>
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, int? Test)
    : IComparable<ReleaseVersion>
{
    // 手動で作る安定版のタグには v が付くことがある。確認する側は API のタグを
    // そのまま読むため、ここでも受け付ける。
    [GeneratedRegex(@"^[vV]?(\d+)\.(\d+)\.(\d+)(?:-test(\d+))?$")]
    private static partial Regex Pattern();

    /// <summary>安定版なら true。プレリリース番号を持てば false。</summary>
    public bool IsStable => Test is null;

    /// <summary>
    /// 版として読めれば返す。読めなければ null を返す。
    /// <para>
    /// 数値は Int32 に収まる範囲でだけ読む。正規表現は桁数を見ないため、
    /// 設定へ手で書かれた桁あふれの綴りも一致してしまう。int.TryParse で範囲外を
    /// 落とし、読み込みの経路ごと例外で落とさない。
    /// </para>
    /// </summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Pattern().Match(text.Trim());
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups[1].Value, out var major)) return null;
        if (!int.TryParse(match.Groups[2].Value, out var minor)) return null;
        if (!int.TryParse(match.Groups[3].Value, out var patch)) return null;

        if (!match.Groups[4].Success) return new ReleaseVersion(major, minor, patch, null);

        if (!int.TryParse(match.Groups[4].Value, out var test)) return null;
        return new ReleaseVersion(major, minor, patch, test);
    }

    /// <summary>
    /// 同じ X.Y.Z なら安定版のほうが新しい (SemVer のプレリリースと同じ扱い)。
    /// 0.0.2-test1 は 0.0.2 より古く、0.0.1 より新しい。
    /// </summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        if (Test is null && other.Test is null) return 0;
        if (Test is null) return 1;
        if (other.Test is null) return -1;
        return Test.Value.CompareTo(other.Test.Value);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString()
        => Test is { } test ? $"{Major}.{Minor}.{Patch}-test{test}" : $"{Major}.{Minor}.{Patch}";
}

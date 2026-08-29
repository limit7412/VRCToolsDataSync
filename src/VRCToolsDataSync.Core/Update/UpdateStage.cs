using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VRCToolsDataSync.Core.Update;

/// <summary>
/// 取得しておいた配布物に添える記録 (issue #45 第 3 段階)。
/// <para>
/// 取得したときに一度照合しているが、置き換える前にもう一度照合する。
/// 取得から次の起動までの間に壊れたものを、そのまま実行ファイルとして置く
/// わけにはいかない。
/// </para>
/// </summary>
public sealed class StagedMetadata
{
    public required string Tag { get; init; }
    public required string DigestHex { get; init; }
    public required long Size { get; init; }

    /// <summary>
    /// 安定版のチャンネルで拾う対象か。取得したときの <see cref="ReleaseInfo.IsStable"/> を残す。
    /// <para>
    /// タグの綴りだけでは足りない。手で作ったリリースにプレリリースの印だけが
    /// 付くことがあり、確認の側は綴りと印の両方を見ている。印は API の応答にしか
    /// 無いので、取得した時点で残しておかないと後から引けない。
    /// 古い記録には無いため既定は偽とする。分からないものを stable へ入れない。
    /// </para>
    /// </summary>
    public bool Stable { get; init; }
}

/// <summary>
/// 取得した ZIP と記録の置き場所 (%AppData%\VRCToolsDataSync\update)。
/// <para>
/// 常駐している間に取得と検証までを行い、置き換えは起動シーケンスの先頭で
/// 更新ヘルパ (cli の self-update apply) が行う。取得と置き換えを分けてあるのは、
/// 置き換えが失敗すると起動しないアプリが残る操作だからである。
/// </para>
/// </summary>
public sealed class UpdateStage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const int BufferSize = 64 * 1024;

    private readonly ILogger _logger;

    public string Directory { get; }

    /// <summary>取得した ZIP。</summary>
    public string ZipPath => Path.Combine(Directory, "staged.zip");

    /// <summary>ZIP に添える記録。</summary>
    public string MetadataPath => Path.Combine(Directory, "staged.json");

    /// <summary>置き換えの直前に ZIP を展開する場所。</summary>
    public string ExtractDirectory => Path.Combine(Directory, "extracted");

    /// <summary>
    /// 取得中の ZIP を書く場所。
    /// <para>
    /// 取得は正規の場所ではなくここへ書き、照合まで通ってから入れ替える。
    /// 直接書くと、既に取得済みの版がある状態で次の版の取得が途中で失敗した
    /// ときに、適用できたはずの前の版まで失われる。
    /// </para>
    /// </summary>
    public string IncomingZipPath => Path.Combine(Directory, "incoming.zip");

    public UpdateStage(string? directory = null, ILogger? logger = null)
    {
        Directory = directory ?? DefaultDirectory();
        _logger = logger ?? NullLogger.Instance;
    }

    public static string DefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCToolsDataSync", "update");

    /// <summary>
    /// 取得しておいた ZIP (<see cref="IncomingZipPath"/>) を、記録と一緒に
    /// 置き換え待ちの対にする。
    /// <para>
    /// 呼ぶのは取得と照合が通った後だけである。ここまでは正規の場所に触らないので、
    /// 途中で失敗しても前の版が適用できるまま残る。
    /// </para>
    /// </summary>
    public void PromoteIncoming(ReleaseInfo release, ReleaseAsset asset)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var metadata = new StagedMetadata
        {
            Tag = release.Tag,
            DigestHex = asset.DigestHex,
            Size = asset.Size,
            Stable = release.IsStable,
        };

        // 記録を先に消す。ZIP を入れ替えた後で記録を書けなかった場合、
        // 前の版の記録と新しい ZIP という食い違った対が残る。
        // 片方だけの状態なら DiscardIncomplete が起動時に片付ける。
        DeleteQuietly(MetadataPath);
        File.Move(IncomingZipPath, ZipPath, overwrite: true);
        File.WriteAllText(MetadataPath, JsonSerializer.Serialize(metadata, JsonOptions));

        // 前の版を展開したものが残っていても、もう対応しない。
        DeleteDirectoryQuietly(ExtractDirectory);
    }

    /// <summary>取得の途中で終わったものを消す。次の取得の前に呼ぶ。</summary>
    public void DiscardIncoming() => DeleteQuietly(IncomingZipPath);

    /// <summary>記録だけを読む。照合はしない。画面の表示に使う。</summary>
    public StagedMetadata? TryLoadMetadata()
    {
        try
        {
            if (!File.Exists(MetadataPath) || !File.Exists(ZipPath)) return null;
            return JsonSerializer.Deserialize<StagedMetadata>(File.ReadAllText(MetadataPath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取得済みで、今のチャンネルに合い、記録と照合の通る ZIP の記録。無ければ null を返す。
    /// <para>
    /// 合わないものは既定ではその場で捨てる。残しておいても次の起動でまた同じ
    /// 照合に落ちるだけであり、取り直しの機会を与えたほうがよい。
    /// </para>
    /// <para>
    /// <paramref name="discardMismatches"/> を偽にすると、合わないものを残したまま
    /// null を返す。起動シーケンスの先頭 (置き換え直後の最初の起動) で使う。
    /// そこで捨てると、置き換えた新しい版がこの後の初期化で失敗した場合に、
    /// 退避した旧版と取得済みの ZIP の両方を失って復旧できなくなる。
    /// 後始末は起動が成り立った後に、既定の破棄付きの呼び出しで行う。
    /// </para>
    /// </summary>
    public StagedMetadata? TryLoadVerified(UpdateChannel channel, string runningVersion, bool discardMismatches = true)
    {
        StagedMetadata? metadata;
        try
        {
            if (!File.Exists(MetadataPath) || !File.Exists(ZipPath)) return null;
            metadata = JsonSerializer.Deserialize<StagedMetadata>(File.ReadAllText(MetadataPath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "取得しておいた配布物を確かめられない");
            if (discardMismatches) Discard();
            return null;
        }
        if (metadata is null)
        {
            if (discardMismatches) Discard();
            return null;
        }

        // 今のチャンネルが拾う版かを見る。取得と置き換えの間には、設定を変えて
        // 再起動するだけの間がある。test で取ったプレリリースを取得済みのまま
        // stable へ変えられると、選び直した設定に反してプレリリースが入る。
        if (channel != UpdateChannel.Test && !metadata.Stable)
        {
            _logger.LogInformation("取得しておいた {Tag} は今のチャンネルの対象ではない", metadata.Tag);
            if (discardMismatches) Discard();
            return null;
        }

        // 実行中より新しいものだけを通す。取得してから起動しないまま日が経ち、
        // その間に手で新しい版へ入れ替えられていると、取っておいた古いほうへ
        // 引き戻すことになる。どちらかが版として読めなければ新しいものとして扱う。
        var running = ReleaseVersion.Parse(runningVersion);
        var staged = ReleaseVersion.Parse(metadata.Tag);
        if (running is not null && staged is not null && staged <= running)
        {
            _logger.LogInformation(
                "取得しておいた {Tag} は実行中の {Running} より新しくない", metadata.Tag, runningVersion);
            if (discardMismatches) Discard();
            return null;
        }

        if (!Verify(metadata))
        {
            _logger.LogWarning("取得しておいた配布物が記録と合わない: {Path}", ZipPath);
            if (discardMismatches) Discard();
            return null;
        }

        return metadata;
    }

    /// <summary>
    /// 取得しておいたものを消す。片方ずつ消す。まとめて括ると、先の 1 つが
    /// 消せなかったときにもう片方を試さないまま抜ける。
    /// </summary>
    public void Discard()
    {
        DeleteQuietly(ZipPath);
        DeleteQuietly(MetadataPath);
        DeleteQuietly(IncomingZipPath);
        DeleteDirectoryQuietly(ExtractDirectory);
    }

    /// <summary>
    /// 片方だけ残った取得を片付ける。
    /// <para>
    /// 取得の最中に終了すると、書きかけの ZIP だけが残る。記録は取得が終わって
    /// から書くためである。呼ぶのは起動時だけとする。取得の最中に呼ぶと、
    /// 書いている途中のものを消すことになる。
    /// </para>
    /// </summary>
    public void DiscardIncomplete()
    {
        var zip = File.Exists(ZipPath);
        var metadata = File.Exists(MetadataPath);
        if (zip == metadata) return;

        _logger.LogInformation("途中で終わった取得を片付ける: {Path}", ZipPath);
        Discard();
    }

    /// <summary>
    /// ZIP を展開して、更新ヘルパが使う一式を作る。展開先は毎回作り直す。
    /// 前回の失敗で残った中身に、今回の ZIP に無いファイルが混ざるためである。
    /// </summary>
    public string ExtractForApply()
    {
        DeleteDirectoryQuietly(ExtractDirectory);
        System.IO.Compression.ZipFile.ExtractToDirectory(ZipPath, ExtractDirectory);
        return ExtractDirectory;
    }

    /// <summary>記録された大きさと digest の両方を見る。大きさが先なのは、違っていれば読まずに落とせるため。</summary>
    private bool Verify(StagedMetadata metadata)
    {
        try
        {
            if (new FileInfo(ZipPath).Length != metadata.Size) return false;
            return string.Equals(DigestOf(ZipPath), metadata.DigestHex, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "取得しておいた配布物を読めない: {Path}", ZipPath);
            return false;
        }
    }

    private static string DigestOf(string path)
    {
        using var sha = SHA256.Create();
        using var file = File.OpenRead(path);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "消せなかった: {Path}", path);
        }
    }

    private void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "消せなかった: {Path}", path);
        }
    }
}

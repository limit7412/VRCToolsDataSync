using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    /// 取得した配布物の名前。アーキテクチャがここに表れる
    /// (VRCToolsDataSync-win-x64.zip / -arm64.zip)。
    /// <para>
    /// ARM64 の Windows では、ネイティブの版とエミュレーションの x64 版が
    /// 同じ置き場所を共有しうる。名前を残さないと、片方が取った ZIP を
    /// もう片方が自分のインストール先へ適用できてしまう。
    /// 古い記録には無いため既定は空とし、その場合は適用しない。
    /// </para>
    /// </summary>
    public string AssetName { get; init; } = string.Empty;

    /// <summary>
    /// 取得した時点のインストール先。
    /// <para>
    /// 同じ利用者が配布 ZIP を複数の場所へ展開していると、どのコピーも同じ
    /// 置き場所を共有する。ここを見ないと、コピー A が取った更新をコピー B が
    /// 自分のインストール先へ適用し、A は更新されないまま取得を失う。
    /// 配布の形でない場合 (dotnet run など) は空になる。
    /// </para>
    /// </summary>
    public string InstallRoot { get; init; } = string.Empty;

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

    /// <summary>昇格の途中で横へ書く記録。付け替えに失敗した場合はここに残る。</summary>
    private string IncomingMetadataPath => MetadataPath + ".new";

    /// <summary>昇格の間だけ古い記録を退避しておく場所。</summary>
    private string PreviousMetadataPath => MetadataPath + ".old";

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

    /// <summary>
    /// 実行中のインストール先。配布の形でなければ空文字を返す。
    /// 記録との突き合わせに使うため、判定を 1 か所に寄せてある。
    /// </summary>
    private static string CurrentInstallRoot =>
        UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory) ?? string.Empty;

    /// <summary>
    /// 置き場所。インストール先ごとに分ける。
    /// <para>
    /// 同じ利用者が配布 ZIP を複数の場所へ展開していると、共有した場合に
    /// 行き止まりが生まれる。一方が取った更新を、もう一方は「取得済み」と見て
    /// 取得を省く一方、インストール先が違うので適用はできない。
    /// 分けておけば、どのコピーも自分の取得を持てる。
    /// </para>
    /// </summary>
    public static string DefaultDirectory()
        => DirectoryFor(UpdateInstaller.FindInstallRoot(AppContext.BaseDirectory));

    /// <summary>
    /// 指定したインストール先の置き場所。
    /// <para>
    /// 自分の居場所と対象のインストール先が違う側から使う。更新ヘルパは
    /// 展開先 (<see cref="ExtractDirectory"/> の下) から動いており、そこも
    /// 配布 ZIP と同じ形をしているため、既定の置き場所を引くと自分の展開先を
    /// 基にした別の場所を掴む。ヘルパは <c>--target</c> からここを引くこと。
    /// </para>
    /// </summary>
    /// <param name="installRoot">
    /// インストール先。null は配布の形でない (dotnet run など) ことを表す。
    /// </param>
    public static string DirectoryFor(string? installRoot)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCToolsDataSync", "update");

        // 配布の形でない場合は適用まで進まないので、名前を分ける意味も無い。
        // 1 つにまとめる。
        return installRoot is null ? Path.Combine(root, "local") : Path.Combine(root, ScopeKeyOf(installRoot));
    }

    /// <summary>
    /// インストール先をディレクトリ名にする。パスはそのまま使えないので縮める。
    /// 大文字小文字は Windows のファイルシステムに合わせて畳む。
    /// </summary>
    private static string ScopeKeyOf(string installRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(installRoot).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }

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
            AssetName = asset.Name,
            InstallRoot = CurrentInstallRoot,
        };

        // 新しい記録は、対に触る前に横へ書いておく。書けない状況 (容量不足や
        // ACL) はここで分かるので、その場合は前の対が無傷のまま残る。
        var incomingMetadata = IncomingMetadataPath;
        File.WriteAllText(incomingMetadata, JsonSerializer.Serialize(metadata, JsonOptions));

        // 古い記録は正規の名前から外す。残したまま ZIP を入れ替えると、前の版の
        // 記録と新しい ZIP という食い違った対がそろった形で残り、次の照合で
        // 両方捨てられる。適用できたはずの前の版まで失う。
        //
        // ただし消さずに横へ退避する。この後の ZIP の入れ替えに失敗した場合、
        // 正規の場所に残るのは前の ZIP なので、退避した記録を戻せば前の対が
        // そのまま使える。消してしまうと、そちらも道連れになる。
        //
        // 外せなかった場合はここで止める。書きかけは呼び出し側が片付け、
        // 次の確認でやり直す。
        var previousMetadata = PreviousMetadataPath;
        _ = DeleteQuietly(previousMetadata, quiet: true);
        var previousKept = false;
        if (Present(MetadataPath) != false)
        {
            try
            {
                MoveWithRetry(MetadataPath, previousMetadata);
                previousKept = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteQuietly(incomingMetadata, quiet: true);
                throw new IOException($"取得済みの記録を退避できなかったため昇格しない: {MetadataPath}", ex);
            }
        }

        // 残るのは同じディレクトリ内の名前の付け替えだけ。掴まれていて一瞬
        // 失敗することがある (ウイルス対策ソフトなど) ので、短く待って
        // やり直す。
        try
        {
            MoveWithRetry(IncomingZipPath, ZipPath);
        }
        catch
        {
            // 正規の ZIP はまだ前のもの。退避した記録を戻せば、前の対が無傷で残る。
            if (previousKept) RestorePreviousMetadata();
            DeleteQuietly(incomingMetadata, quiet: true);
            throw;
        }

        MoveWithRetry(incomingMetadata, MetadataPath);
        _ = DeleteQuietly(previousMetadata, quiet: true);

        // 前の版を展開したものが残っていても、もう対応しない。
        DeleteDirectoryQuietly(ExtractDirectory);

        // 展開の失敗の数は前の ZIP のもの。新しい ZIP は 1 回目から数え直す。
        ForgetExtractFailures();
    }

    /// <summary>
    /// 更新ヘルパが動いている間だけ握るクロスプロセスのロックを作る。
    /// <para>
    /// ヘルパは展開先とインストール先を触る。その最中に App を起動されると、
    /// 新しいプロセスが同じ展開先を消して展開し直したり、旧版のファイルを
    /// 掴んだままヘルパのリネームとぶつかったりする。App は起動の先頭でこれを
    /// 待ち、ヘルパは適用の全体で握る。
    /// </para>
    /// <para>
    /// 名前はインストール先ごとに分ける。置き場所を分けた以上、別の場所へ
    /// 展開したコピーの適用を待つ理由が無い。
    /// </para>
    /// <para>
    /// 名前空間は <c>Global\</c> を使う。接頭辞を付けないと、名前は対話
    /// セッションごとの名前空間に作られる。同じ利用者がユーザーの切り替えや
    /// リモートデスクトップで 2 つのセッションを持つと、置き場所と
    /// インストール先は共有されるのに、ロックだけが互いに見えなくなる。
    /// </para>
    /// </summary>
    /// <param name="installRoot">
    /// インストール先。null は配布の形でない (dotnet run など) ことを表す。
    /// </param>
    public static Mutex CreateApplyMutex(string? installRoot)
    {
        var name = "VRCToolsDataSync.Update.Apply." + (installRoot is null ? "local" : ScopeKeyOf(installRoot));
        try
        {
            return new Mutex(initiallyOwned: false, name: @"Global\" + name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // Global\ の名前を作れない構成もある (権限を落とした環境や、
            // 名前に区切りを許さないプラットフォーム)。そこではセッション内
            // だけの名前で妥協する。同じセッションの重なりは防げる。
            return new Mutex(initiallyOwned: false, name: name);
        }
    }

    /// <summary>
    /// 同じディレクトリ内で名前を付け替える。掴まれている間は短く待って
    /// やり直し、それでも駄目なら投げる。
    /// </summary>
    private static void MoveWithRetry(string source, string destination)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>取得の途中で終わったものを消す。次の取得の前に呼ぶ。</summary>
    public void DiscardIncoming() => _ = DeleteQuietly(IncomingZipPath);

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
        catch (JsonException ex)
        {
            // 記録として読めない。中身が壊れていると分かったので捨ててよい。
            _logger.LogWarning(ex, "取得しておいた記録を読めない: {Path}", MetadataPath);
            if (discardMismatches) Discard();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなかっただけで、中身が壊れていると分かったわけではない。
            // 掴まれている間 (ウイルス対策ソフトなど) に捨てると、正しい取得を
            // 失う。しかも同じ理由で片方だけ消せて対が崩れることもある。
            // 適用はしないが、次の機会へ回す。
            _logger.LogWarning(ex, "取得しておいた記録を読めない: {Path}", MetadataPath);
            return null;
        }
        if (metadata is null)
        {
            if (discardMismatches) Discard();
            return null;
        }

        // 実行中のプロセスに合う配布物かを見る。ARM64 の Windows では
        // ネイティブの版とエミュレーションの x64 版が同じ置き場所を共有しうる。
        // 名前が合わないもの (と、名前を持たない古い記録) は適用しない。
        var expectedAsset = ReleaseAsset.NameForCurrentArchitecture();
        if (expectedAsset is null || !string.Equals(metadata.AssetName, expectedAsset, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "取得しておいた {Tag} は実行中のアーキテクチャ向けではない ({Actual} / {Expected})",
                metadata.Tag, metadata.AssetName, expectedAsset ?? "(配布なし)");
            if (discardMismatches) Discard();
            return null;
        }

        // 取得したときと同じインストール先かを見る。配布 ZIP を複数の場所へ
        // 展開している場合、どのコピーもこの置き場所を共有する。
        if (!string.Equals(metadata.InstallRoot, CurrentInstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "取得しておいた {Tag} は別のインストール先のものである ({Actual})",
                metadata.Tag, string.IsNullOrEmpty(metadata.InstallRoot) ? "(配布の形でない)" : metadata.InstallRoot);
            // 相手のコピーが適用できるよう、ここでは捨てない。
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

        // 記録のタグが版として読めなければ適用しない。読めないままだと、この後の
        // 前後の判定も、展開した一式との突き合わせも素通りになり、古い版へ
        // 引き戻す経路が空く。正規の経路で書いた記録は必ず読める (確認の側が
        // 読めたタグしか渡さない) ので、読めないのは壊れているか触られている。
        var staged = ReleaseVersion.Parse(metadata.Tag);
        if (staged is null)
        {
            _logger.LogWarning("取得しておいた記録のタグを版として読めない: {Tag}", metadata.Tag);
            if (discardMismatches) Discard();
            return null;
        }

        // 実行中より新しいものだけを通す。取得してから起動しないまま日が経ち、
        // その間に手で新しい版へ入れ替えられていると、取っておいた古いほうへ
        // 引き戻すことになる。実行中の版が読めない場合 (手元ビルドの 0.0.0-dev)
        // だけは、比べようがないので通す。
        var running = ReleaseVersion.Parse(runningVersion);
        if (running is not null && staged <= running)
        {
            _logger.LogInformation(
                "取得しておいた {Tag} は実行中の {Running} より新しくない", metadata.Tag, runningVersion);
            if (discardMismatches) Discard();
            return null;
        }

        try
        {
            if (!Verify(metadata))
            {
                _logger.LogWarning("取得しておいた配布物が記録と合わない: {Path}", ZipPath);
                if (discardMismatches) Discard();
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなかっただけで、中身が合わないと分かったわけではない。掴まれて
            // いる間 (ウイルス対策ソフトなど) に捨てると、正しい取得を失う。
            // 適用はしないが、次の機会へ回す。
            _logger.LogWarning(ex, "取得しておいた配布物を読めない: {Path}", ZipPath);
            return null;
        }

        return metadata;
    }

    /// <summary>
    /// 取得しておいたものを消す。片方ずつ消す。まとめて括ると、先の 1 つが
    /// 消せなかったときにもう片方を試さないまま抜ける。
    /// </summary>
    /// <summary>
    /// 取得しておいたものを消す。片方ずつ消す。まとめて括ると、先の 1 つが
    /// 消せなかったときにもう片方を試さないまま抜ける。
    /// <para>
    /// ZIP と記録の両方を消せたときだけ true を返す。呼び出し側が
    /// 「次の起動でまた同じものを適用しに行かないか」を判断できるようにするため。
    /// 展開先は消せなくても適用の判断に影響しないので、成否には含めない。
    /// </para>
    /// </summary>
    public bool Discard()
    {
        var zip = DeleteQuietly(ZipPath);
        var metadata = DeleteQuietly(MetadataPath);
        DeleteQuietly(IncomingZipPath);
        DeleteQuietly(IncomingMetadataPath, quiet: true);
        DeleteQuietly(PreviousMetadataPath, quiet: true);
        DeleteDirectoryQuietly(ExtractDirectory);
        ForgetExtractFailures();
        return zip && metadata;
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
        TryFinishInterruptedPromote();

        var zip = Present(ZipPath);
        var metadata = Present(MetadataPath);

        // 分からないもの (掴まれている等) がある間は触らない。片方が読めない
        // だけの状態を「無い」と取り違えて、そろっている対を崩すことになる。
        if (zip is null || metadata is null) return;
        if (zip == metadata) return;

        _logger.LogInformation("途中で終わった取得を片付ける: {Path}", ZipPath);
        Discard();
    }

    /// <summary>
    /// 昇格の最後の付け替えだけが済んでいない状態を、ここで仕上げる。
    /// <para>
    /// 昇格は「新しい記録を横へ書く → 古い記録を消す → ZIP を入れ替える →
    /// 記録を置く」の順で進む。最後の付け替えだけが失敗すると、新しい ZIP は
    /// 正規の場所に居るのに記録が無い、という形で止まる。片方だけの状態として
    /// 捨てると、照合まで通った取得を丸ごと捨てることになる。横に残っている
    /// 記録を置き直せば済むので、捨てる判断の前に一度試す。
    /// </para>
    /// <para>
    /// 置き直した対が食い違っている場合 (ZIP を入れ替える前に止まっていた場合)
    /// は、この後の照合が digest と大きさで気付いて捨てる。ここで確かめ直す
    /// 必要は無い。
    /// </para>
    /// </summary>
    private void TryFinishInterruptedPromote()
    {
        if (Present(ZipPath) != true) return;
        if (Present(MetadataPath) != false) return;

        if (Present(IncomingMetadataPath) == true)
        {
            // ZIP は新しいものに入れ替わっている。新しい記録を置けば対がそろう。
            try
            {
                _logger.LogInformation("昇格の途中で止まった記録を置き直す: {Path}", MetadataPath);
                MoveWithRetry(IncomingMetadataPath, MetadataPath);
                _ = DeleteQuietly(PreviousMetadataPath, quiet: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "昇格の途中で止まった記録を置き直せなかった: {Path}", MetadataPath);
            }
            return;
        }

        // 新しい記録が無く、退避した古い記録だけがある。ZIP の入れ替えに失敗し、
        // その場での戻しも失敗した後である。正規の ZIP は前のものなので、
        // 退避した記録を戻せば前の対が使える。
        if (Present(PreviousMetadataPath) == true) RestorePreviousMetadata();
    }

    /// <summary>退避しておいた古い記録を正規の名前へ戻す。</summary>
    private void RestorePreviousMetadata()
    {
        try
        {
            _logger.LogInformation("退避した記録を戻す: {Path}", MetadataPath);
            MoveWithRetry(PreviousMetadataPath, MetadataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "退避した記録を戻せなかった: {Path}", MetadataPath);
        }
    }

    /// <summary>
    /// ファイルがあるか。分からない場合は null を返す。
    /// <para>
    /// <see cref="File.Exists(string)"/> は、権限や I/O の失敗も「無い」として
    /// 返す。取得の対を扱う場面ではそれが困る。読めないだけのものを「無い」と
    /// 見なすと、そろっている対を崩したり、残っている対を消せたことにしたり
    /// する。開いてみて、無いと分かった場合だけ false にする。
    /// </para>
    /// </summary>
    public static bool? Present(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 置き換え待ちの対が残っているか。
    /// <para>
    /// 破棄の後に「次の起動が同じ適用へ入らないか」を判断するために使う。
    /// 適用へ進むのは対がそろっているときだけなので、片方でも無いと分かれば
    /// 「残っていない」でよい。逆に、読めないだけのものを「消えた」と
    /// 取り違えると、開いては閉じるのを繰り返す経路へ戻ってしまうので、
    /// 分からない側は残っているものとして数える。
    /// </para>
    /// </summary>
    public bool StagedPairRemains() => Present(ZipPath) != false && Present(MetadataPath) != false;

    /// <summary>
    /// ZIP を展開して、更新ヘルパが使う一式を作る。展開先は毎回作り直す。
    /// 前回の失敗で残った中身に、今回の ZIP に無いファイルが混ざるためである。
    /// </summary>
    public string ExtractForApply()
    {
        EnsureExtractable();

        // 前の展開が残っていたら消す。消し切れないまま重ねて展開すると、
        // 新しい版で消えたはずのファイルがそのまま残り、その混ざった一式を
        // インストールしてしまう。消し切れない場合はここで止める (取得は残る
        // ので、掴んでいたものが放されれば次の起動でやり直せる)。
        DeleteDirectoryQuietly(ExtractDirectory);
        if (System.IO.Directory.Exists(ExtractDirectory))
        {
            throw new IOException($"前回の展開先を消せなかったため展開しない: {ExtractDirectory}");
        }

        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(ZipPath, ExtractDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            throw ClassifyExtractFailure(ex);
        }

        ForgetExtractFailures();
        return ExtractDirectory;
    }

    /// <summary>
    /// 展開の失敗を、取得を残して待つものと、取得ごと捨てるものに分ける。
    /// <para>
    /// 一時的に掴まれている (ウイルス対策ソフトなど)、確認の後に空きが尽きた、
    /// といった失敗は配布物の問題ではないので、そのまま投げ直して取得を残す。
    /// 次の起動でやり直せる。
    /// </para>
    /// <para>
    /// ただし残すだけでは、Windows で作れない項目名のように何度やっても
    /// 失敗する配布物で、起動のたびに同じことを繰り返す。例外の種類では
    /// 一時的なものと見分けられないため、同じ ZIP で何回失敗したかを数え、
    /// <see cref="MaxExtractAttempts"/> 回に達したところで配布物の問題として
    /// <see cref="InvalidDataException"/> にそろえる。呼び出し側はこれを見て
    /// 取得ごと捨てる。数は昇格 (新しい ZIP) と展開の成功で 0 に戻す。
    /// </para>
    /// </summary>
    private Exception ClassifyExtractFailure(Exception ex)
    {
        var failures = RecordExtractFailure();
        if (failures < MaxExtractAttempts)
        {
            _logger.LogWarning(ex, "展開に失敗した ({Failures} 回目)。取得は残して次の機会にやり直す", failures);
            return ex;
        }

        return new InvalidDataException(
            $"{failures} 回続けて展開できなかったため配布物の問題として扱う: {ZipPath}", ex);
    }

    /// <summary>同じ ZIP の展開をあきらめるまでの回数。</summary>
    private const int MaxExtractAttempts = 3;

    private string ExtractFailureCountPath => Path.Combine(Directory, "extract-failures");

    /// <summary>
    /// 展開の失敗を 1 つ数えて、数えた後の回数を返す。
    /// <para>
    /// 記録 (<see cref="MetadataPath"/>) とは別のファイルに置く。書きかけで
    /// 落ちても、照合に使う対には触らない形にするためである。読めない場合は
    /// 0 から数え直す。数えられない状況 (容量不足など) は、そもそも取得を
    /// 残したい側なので、数が進まないことが困る形にはならない。
    /// </para>
    /// </summary>
    private int RecordExtractFailure()
    {
        var failures = 1;
        try
        {
            if (int.TryParse(File.ReadAllText(ExtractFailureCountPath), out var previous) && previous > 0)
            {
                failures = previous + 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなければ 1 回目として扱う。
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(ExtractFailureCountPath, failures.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "展開の失敗を数えられなかった: {Path}", ExtractFailureCountPath);
        }

        return failures;
    }

    /// <summary>展開の失敗の数を捨てる。新しい ZIP を置いたときと、展開が通ったときに呼ぶ。</summary>
    private void ForgetExtractFailures() => _ = DeleteQuietly(ExtractFailureCountPath, quiet: true);

    /// <summary>
    /// 展開が決まって失敗する形でないかを先に見る。
    /// <para>
    /// 同じパスの項目を 2 つ以上持つ ZIP は、書式としては正しく digest も通るが、
    /// 上書きしない <c>ExtractToDirectory</c> は 2 つ目で <see cref="IOException"/> を
    /// 投げる。展開の途中で落ちると、その例外が容量不足などと区別できない。
    /// 配布物そのものの問題として <see cref="InvalidDataException"/> にそろえ、
    /// 呼び出し側が取得ごと捨てられるようにする。
    /// </para>
    /// </summary>
    private void EnsureExtractable()
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(ZipPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;
        foreach (var entry in archive.Entries)
        {
            var key = ExtractionKeyOf(entry.FullName);

            // ディレクトリの項目は名前が空になる。同じ場所に重なっても害が
            // 無いので、名前の突き合わせの対象からは外す。ただし展開先の外を
            // 指すかどうかと、同じ場所にファイルが来ないかは、ディレクトリの
            // 項目でも見る。
            if (string.IsNullOrEmpty(entry.Name))
            {
                if (key is null)
                {
                    throw new InvalidDataException($"展開先の外を指す項目のある ZIP は展開できない: {entry.FullName}");
                }
                AddWithAncestors(directories, key);
                continue;
            }

            if (key is null)
            {
                // 展開先の外を指す項目。ExtractToDirectory も断るが、そちらの
                // IOException は容量不足などと区別できない。配布物そのものの
                // 問題として投げ分け、取得ごと捨てられるようにする。
                throw new InvalidDataException($"展開先の外を指す項目のある ZIP は展開できない: {entry.FullName}");
            }

            if (!seen.Add(key))
            {
                throw new InvalidDataException($"同じパスの項目が複数ある ZIP は展開できない: {entry.FullName}");
            }

            // ファイルの手前の段は、展開すればディレクトリになる。
            var parent = ParentOf(key);
            if (parent is not null) AddWithAncestors(directories, parent);

            // 足す前に見る。項目の大きさは ZIP の目録に書かれた値をそのまま
            // 受け取るので、桁を偽られると足し算が折り返し、負の合計として
            // 上限も空き容量の確認もすり抜ける。
            if (entry.Length < 0 || entry.Length > MaxExtractedSize - total)
            {
                throw new InvalidDataException(
                    $"展開後の大きさが桁違いの ZIP は展開しない: {MaxExtractedSize} バイトを超える");
            }

            total += entry.Length;
        }

        // 同じ場所をファイルとディレクトリの両方にはできない。app/foo という
        // ファイルと app/foo/bar.dll のような組み合わせは、名前が重なっていない
        // ので上の突き合わせでは通るが、展開すれば必ず失敗する。展開の失敗は
        // 一時的なものと見分けられず数回の空振りになるので、ここで断る。
        seen.IntersectWith(directories);
        if (seen.Count > 0)
        {
            throw new InvalidDataException(
                $"同じ場所がファイルとディレクトリの両方になる ZIP は展開できない: {string.Join(", ", seen)}");
        }

        EnsureSpaceFor(total);
    }

    /// <summary>段の手前を返す。手前が無い (最上段の) 場合は null。</summary>
    private static string? ParentOf(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? null : key[..slash];
    }

    /// <summary>ディレクトリとして扱う場所を、その手前の段まで含めて足す。</summary>
    private static void AddWithAncestors(HashSet<string> directories, string key)
    {
        for (string? current = key; current is not null; current = ParentOf(current))
        {
            if (!directories.Add(current)) return;
        }
    }

    /// <summary>
    /// 展開に足りる空きがあるかを先に見る。
    /// <para>
    /// 容量不足は配布物の問題ではないので、ここで <see cref="IOException"/> として
    /// 断り、取得は残したまま次の機会へ回す。逆にここを通れば、展開の最中の
    /// 失敗は「Windows で作れない名前」など配布物の形の問題として扱ってよい
    /// (<see cref="ExtractForApply"/> がそう扱う)。空きを読めない置き場所
    /// (ネットワーク越しなど) では見送る。読めないことを不足の証拠にはできない。
    /// </para>
    /// </summary>
    private void EnsureSpaceFor(long required)
    {
        long available;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Directory));
            if (string.IsNullOrEmpty(root)) return;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "空き容量を読めなかったため確認を見送る: {Directory}", Directory);
            return;
        }

        if (available >= required) return;

        throw new IOException(
            $"展開に足りる空きが無いため展開しない: {required} バイト必要だが {available} バイトしかない");
    }

    /// <summary>
    /// 展開後の大きさの上限。
    /// <para>
    /// 配布物は数百 MB を見込んでいる。圧縮率の高い誤った配布物をそのまま
    /// 展開すると %AppData% のあるドライブを使い切り、しかも容量不足は配布物の
    /// 問題ではないので捨てずに次の起動へ回す設計のため、起動のたびに同じことを
    /// 繰り返す。桁が違うものはここで断る。正確な見積もりではなく、
    /// 「あり得ない大きさ」の線として置いている。
    /// </para>
    /// </summary>
    private const long MaxExtractedSize = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 項目の名前を、実際に展開される先で見比べられる形へそろえる。
    /// <para>
    /// ZIP の区切りは "/" と決まっているが、"\" で書かれたものも出回る。
    /// Windows ではどちらも区切りとして扱われるため、<c>app/foo.dll</c> と
    /// <c>app\foo.dll</c> は同じ場所へ展開される。名前をそのまま見比べると
    /// 別物として通ってしまう。"." の区切りも同じ理由で落とす。
    /// </para>
    /// </summary>
    private static string? ExtractionKeyOf(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');

        // 先頭が区切りのもの (/payload.dll) と、ドライブの付いたもの
        // (C:/payload.dll) は展開先の外を指す。段に分ける前に断る。区切りは
        // 空の段として落ちてしまい、後の解決では相対のものと見分けられない。
        if (normalized.StartsWith('/')) return null;
        if (normalized.Length >= 2 && normalized[1] == ':') return null;

        var resolved = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                // 展開先より上へ戻る項目は、行き先を突き合わせようがない。
                // 呼び出し側が配布物の問題として扱えるよう null を返す。
                if (resolved.Count == 0) return null;
                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            // Win32 は段の末尾の点と空白を落とす。foo.dll と "foo.dll." は
            // 同じ場所へ展開されるので、そろえてから見比べる。落とすと空に
            // なる段 ("..." など) は Windows の名前として成り立たない。
            var trimmed = segment.TrimEnd('.', ' ');
            if (trimmed.Length == 0) return null;
            if (!IsCreatableOnWindows(trimmed)) return null;
            resolved.Add(trimmed);
        }

        return string.Join('/', resolved);
    }

    /// <summary>
    /// 段が Windows のファイル名として作れるかを見る。
    /// <para>
    /// 使えない文字 (<c>? * | " &lt; &gt; :</c> と制御文字) と、予約された装置名
    /// (<c>CON</c> や <c>LPT1</c>、<c>CON.dll</c> のように後ろに拡張子が付いた形も含む)
    /// は、Windows ではその名前のファイルを作れない。配布物の側でも作れない
    /// はずなので、正しい配布物を誤って断ることはない。
    /// </para>
    /// </summary>
    private static bool IsCreatableOnWindows(string segment)
    {
        foreach (var c in segment)
        {
            if (c < ' ') return false;
            if (InvalidNameChars.Contains(c)) return false;
        }

        // 装置名の判定は最初の "." より前だけを見る。CON.dll も CON と同じ装置を
        // 指すため、Windows では作れない。
        var dot = segment.IndexOf('.');
        var baseName = dot < 0 ? segment : segment[..dot];
        return !ReservedDeviceNames.Contains(baseName);
    }

    /// <summary>
    /// Windows のファイル名に使えない文字。区切りの "/" と "\" は段に分ける前に
    /// 処理済みなので、ここには現れない。
    /// </summary>
    private const string InvalidNameChars = "<>:\"|?*";

    /// <summary>
    /// 予約された装置名。末尾の空白と点は <see cref="ExtractionKeyOf"/> で
    /// 落としてから渡すため、ここでは素の名前だけを見る。
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    };

    /// <summary>記録された大きさと digest の両方を見る。大きさが先なのは、違っていれば読まずに落とせるため。</summary>
    /// <remarks>
    /// 読めなかった場合は投げる。「合わない」と同じ false で返すと、呼び出し側が
    /// 中身の問題と取り違えて捨ててしまう。
    /// </remarks>
    private bool Verify(StagedMetadata metadata)
    {
        if (new FileInfo(ZipPath).Length != metadata.Size) return false;
        return string.Equals(DigestOf(ZipPath), metadata.DigestHex, StringComparison.Ordinal);
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

    /// <summary>消せたか (もともと無かった場合も true) を返す。</summary>
    /// <param name="quiet">消せなくても支障の無いものを消すとき。警告を残さない。</param>
    private bool DeleteQuietly(string path, bool quiet = false)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (quiet) _logger.LogDebug(ex, "消せなかった: {Path}", path);
            else _logger.LogWarning(ex, "消せなかった: {Path}", path);
            return false;
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

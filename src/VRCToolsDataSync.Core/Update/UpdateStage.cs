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
        var incomingMetadata = MetadataPath + ".new";
        File.WriteAllText(incomingMetadata, JsonSerializer.Serialize(metadata, JsonOptions));

        // 記録を先に消す。ZIP を入れ替えた後で記録を置けなかった場合、
        // 前の版の記録と新しい ZIP という食い違った対が残る。
        // 片方だけの状態なら DiscardIncomplete が起動時に片付ける。
        //
        // 消せなかった場合はここで止める。古い記録を残したまま ZIP を入れ替えると、
        // 食い違った対がそろった形で残り、次の照合で両方捨てられる。適用できた
        // はずの前の版まで失う。書きかけは呼び出し側が片付け、次の確認でやり直す。
        if (!DeleteQuietly(MetadataPath))
        {
            DeleteQuietly(incomingMetadata);
            throw new IOException($"取得済みの記録を消せなかったため昇格しない: {MetadataPath}");
        }

        // 残るのは同じディレクトリ内の名前の付け替えだけ。掴まれていて一瞬
        // 失敗することがある (ウイルス対策ソフトなど) ので、短く待って
        // やり直す。
        MoveWithRetry(IncomingZipPath, ZipPath);
        MoveWithRetry(incomingMetadata, MetadataPath);

        // 前の版を展開したものが残っていても、もう対応しない。
        DeleteDirectoryQuietly(ExtractDirectory);
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
        DeleteDirectoryQuietly(ExtractDirectory);
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

        System.IO.Compression.ZipFile.ExtractToDirectory(ZipPath, ExtractDirectory);
        return ExtractDirectory;
    }

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
        var total = 0L;
        foreach (var entry in archive.Entries)
        {
            // ディレクトリの項目は名前が空になる。重なっても害が無い。
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var key = ExtractionKeyOf(entry.FullName);
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

            total += entry.Length;
            if (total > MaxExtractedSize)
            {
                throw new InvalidDataException(
                    $"展開後の大きさが桁違いの ZIP は展開しない: {total} バイトを超える");
            }
        }
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
        var resolved = new List<string>();
        foreach (var segment in fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
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
            resolved.Add(segment);
        }

        return string.Join('/', resolved);
    }

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
    private bool DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "消せなかった: {Path}", path);
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

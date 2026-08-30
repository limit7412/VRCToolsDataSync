using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VRCToolsDataSync.Core.Update;

/// <summary>
/// 退避したディレクトリを戻せなかった。
/// <para>
/// 正規の位置に実行ファイル一式が無い状態であり、置き換えの失敗の中でも扱いが違う。
/// 取得しておいたものを捨ててはならない。捨てると復旧の材料が .old だけになり、
/// そちらも戻せなかったからここへ来ている。
/// </para>
/// </summary>
public sealed class UpdateRollbackException : Exception
{
    public UpdateRollbackException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// インストール先に、新しい一式を置くだけの空きが無い。
/// <para>
/// 配布物の問題ではないので、取得しておいたものを捨ててはならない。空きを
/// 作れば同じ ZIP でやり直せる。数百 MB の取り直しを利用者に強いないための
/// 区別である。
/// </para>
/// </summary>
public sealed class UpdateCapacityException : Exception
{
    public UpdateCapacityException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 展開しておいた新しい一式で、インストール先を置き換える (issue #45 第 3 段階)。
/// <para>
/// 実行中のアプリは <c>app\</c> 配下の DLL を掴んでいるため、ディレクトリの
/// 差し替えはアプリが終了した後にしかできない。これを行う更新ヘルパ
/// (cli の self-update apply) は展開先から動くので、自分自身の置き換えにも
/// ならない。
/// </para>
/// <para>
/// 重い複製 (app.new / cli.new の用意) を先に済ませ、正規の位置に触るのは
/// 短いリネームだけにする。失敗の窓を狭め、失敗したらリネームで戻せるように
/// するためである。
/// </para>
/// </summary>
public class UpdateInstaller
{
    /// <summary>ZIP 直下に入るランチャーの名前。build-release.ps1 が作るものと一致させる。</summary>
    public const string LauncherName = "VRCToolsDataSync.cmd";

    /// <summary>app ディレクトリに入る GUI の実行ファイルの名前。</summary>
    public const string AppExecutableName = "VRCToolsDataSync.App.exe";

    /// <summary>cli ディレクトリに入る CLI (更新ヘルパ) の実行ファイルの名前。</summary>
    public const string CliExecutableName = "VRCToolsDataSync.Cli.exe";

    /// <summary>
    /// app ディレクトリに入る GUI の本体 (マネージドアセンブリ) の名前。
    /// <para>
    /// 配布は単一ファイルにまとめていないため、exe は起動の入り口 (apphost) で
    /// しかない。これが欠けると exe があっても起動できない。
    /// </para>
    /// </summary>
    public const string AppAssemblyName = "VRCToolsDataSync.App.dll";

    /// <summary>cli ディレクトリに入る CLI の本体 (マネージドアセンブリ) の名前。</summary>
    public const string CliAssemblyName = "VRCToolsDataSync.Cli.dll";

    /// <summary>
    /// 更新の適用を 1 回だけ見送らせるために App へ渡す切り替え。
    /// <para>
    /// 置き換えを断念したヘルパが App を開き直すとき、取得しておいたものを
    /// 残す場合に渡す。渡さないと、開き直った App が同じ取得をまたヘルパへ
    /// 渡し、ヘルパがまた同じ理由で断念して開き直す、という往復になる。
    /// </para>
    /// </summary>
    public const string SkipUpdateApplySwitch = "--skip-update-apply";

    /// <summary>
    /// App の多重起動を抑止する Named Mutex の名前。
    /// <para>
    /// 更新ヘルパも置き換えの間これを掴む。App はこの名前の Mutex が既にあるかで
    /// 「他に動いている」を判断するので、掴んでいる間は新しい App が立ち上がらない。
    /// 掴まないと、置き換えの最中に起動した App が旧 <c>app\</c> を読み込んで
    /// 掴み、入れ替えを失敗させたり、置き換え済みの一式をもう一度置き換えさせて
    /// 退避した旧版を上書きさせたりする。
    /// </para>
    /// <para>
    /// <c>Global\</c> は付けない。App 側と同じ名前でなければ意味が無く、
    /// あちらは対話セッション内の抑止として置かれている (#52)。
    /// </para>
    /// </summary>
    public const string SingleInstanceMutexName = "VRCToolsDataSync.App.SingleInstance";

    /// <summary>置き換えの対象になる、インストール先直下のディレクトリ。</summary>
    private static readonly string[] Parts = { "app", "cli" };

    private readonly string _sourceDirectory;
    private readonly string _targetDirectory;
    private readonly ILogger _logger;

    /// <param name="sourceDirectory">展開した新しい一式 (app / cli / ランチャーを含む)。</param>
    /// <param name="targetDirectory">インストール先のルート (app / ランチャーを含む)。</param>
    public UpdateInstaller(string sourceDirectory, string targetDirectory, ILogger? logger = null)
    {
        _sourceDirectory = sourceDirectory;
        _targetDirectory = targetDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// アプリの実行位置からインストール先のルートを探す。
    /// <para>
    /// 配布 ZIP の形 (&lt;ルート&gt;\app\VRCToolsDataSync.App.exe) だけを認める。
    /// dotnet run や bin\ 配下の手元ビルドでは null を返し、開発中の作業ツリーを
    /// 誤って置き換えないようにする。
    /// </para>
    /// </summary>
    public static string? FindInstallRoot(string appBaseDirectory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(appBaseDirectory);
        if (!string.Equals(Path.GetFileName(trimmed), "app", StringComparison.OrdinalIgnoreCase)) return null;

        var root = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(root)) return null;
        if (!File.Exists(Path.Combine(root, LauncherName))) return null;
        return root;
    }

    /// <summary>
    /// 置き換えを行う。手順は
    /// (1) 新しい一式を .new として複製、(2) 前回の .old を除去、
    /// (3) 現行を .old へリネームして .new を正規の位置へ、(4) ランチャーの上書き。
    /// リネームに失敗したら .old を戻す。戻せなければ
    /// <see cref="UpdateRollbackException"/> を投げる。そちらでは取得しておいた
    /// ZIP を捨ててはならない。復旧の材料になる。
    /// </summary>
    public void Apply()
    {
        ValidateLayout();
        EnsureSpaceForCopy();

        // (1)-(3) のどこで失敗しても、用意した .new は残さない。数百 MB あり、
        // 起動時の後始末も CLI 側の破棄も .new を見ないので、次の更新まで
        // 居座る。容量不足で失敗した場合は特に、その残骸が次の取得まで妨げる。
        try
        {
            PrepareAndSwap();
        }
        catch (UpdateRollbackException)
        {
            // 正規の位置が欠けたまま。手で直す材料になりうるものは残す。
            throw;
        }
        catch
        {
            foreach (var part in Parts)
            {
                LogQuietly(() => _logger.LogInformation("適用に失敗したため用意した一式を消す: {Part}.new", part));
                try { DeleteDirectoryIfExists(Path.Combine(_targetDirectory, part + ".new")); }
                catch { /* best-effort */ }
            }
            throw;
        }

        ReplaceLauncher();
    }

    /// <summary>
    /// インストール先に、新しい一式をもう 1 つ置くだけの空きがあるかを先に見る。
    /// <para>
    /// 複製の途中で空きが尽きると、呼び出し側 (更新ヘルパ) はそれを置き換えの
    /// 失敗として扱い、取得しておいた ZIP まで捨ててしまう。利用者は空きを
    /// 作った後、数百 MB を取り直すことになる。触る前に見て
    /// <see cref="UpdateCapacityException"/> で分けておけば、取得を残したまま
    /// 引き下がれる。
    /// </para>
    /// <para>
    /// 見るのはインストール先のドライブである。展開先とは別のドライブに
    /// 置かれている場合があり、そちらの空きは <c>UpdateStage</c> が展開の前に
    /// 確かめている。空きを読めない置き場所 (ネットワーク越しなど) では
    /// 見送る。読めないことを不足の証拠にはできない。
    /// </para>
    /// </summary>
    private void EnsureSpaceForCopy()
    {
        long required;
        long available;
        try
        {
            required = Parts.Sum(part => DirectorySize(Path.Combine(_sourceDirectory, part)));

            var root = Path.GetPathRoot(Path.GetFullPath(_targetDirectory));
            if (string.IsNullOrEmpty(root)) return;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogQuietly(() => _logger.LogDebug(ex, "空き容量を読めなかったため確認を見送る: {Target}", _targetDirectory));
            return;
        }

        if (available >= required) return;

        throw new UpdateCapacityException(
            $"インストール先に空きが足りないため置き換えない: {required} バイト必要だが {available} バイトしかない");
    }

    private static long DirectorySize(string path) =>
        new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    /// <summary>
    /// 用意 (複製) から入れ替えまで。失敗の後始末は呼び出し側が行う。
    /// </summary>
    private void PrepareAndSwap()
    {
        // (1) 重い複製を先に済ませる。ここまでは正規の位置に触らないので、
        //     何度失敗してもやり直せる。
        foreach (var part in Parts)
        {
            var fresh = Path.Combine(_targetDirectory, part + ".new");
            DeleteDirectoryIfExists(fresh);
            CopyDirectory(Path.Combine(_sourceDirectory, part), fresh);
        }

        // (2) 前回の置き換えが残した .old を先に除去する。残したまま進むと
        //     (3) のリネームが衝突する。消せない場合はここで止める。
        //     正規の位置にはまだ触っていないため、失敗しても壊れない。
        foreach (var part in Parts)
        {
            DeleteDirectoryIfExists(Path.Combine(_targetDirectory, part + ".old"));
        }

        // (3) 正規の位置に触るのはここからの短いリネームだけ。
        var swapped = new List<string>();
        foreach (var part in Parts)
        {
            var current = Path.Combine(_targetDirectory, part);
            var backup = Path.Combine(_targetDirectory, part + ".old");
            var fresh = Path.Combine(_targetDirectory, part + ".new");

            try
            {
                Move(current, backup);
            }
            catch (Exception ex)
            {
                // 退避できなかった。正規の位置は無傷なので、済ませた分だけ戻す。
                RollbackSwapped(swapped);
                throw new InvalidOperationException($"{part} を退避できなかった", ex);
            }

            try
            {
                Move(fresh, current);
            }
            catch (Exception ex)
            {
                // 入れられなかった。退避したものを戻してから伝える。
                // 戻すのにも失敗したら、正規の位置が空いたままになるため、
                // 通常の失敗とは別の例外で呼び出し側へ伝える。
                try
                {
                    Move(backup, current);
                }
                catch (Exception restore)
                {
                    LogQuietly(() => _logger.LogError(restore,
                        "退避した {Part} を戻せなかった。{Backup} を {Current} へ戻す必要がある",
                        part, backup, current));
                    throw new UpdateRollbackException(
                        $"退避した {part} を戻せなかった: {backup} を {current} へ戻す必要がある", restore);
                }
                RollbackSwapped(swapped);
                throw new InvalidOperationException($"{part} を置き換えられなかった", ex);
            }

            swapped.Add(part);
            LogQuietly(() => _logger.LogInformation("{Part} を置き換えた", part));
        }
    }

    /// <summary>
    /// (4) ランチャーを差し替える。同じディレクトリの一時ファイルへ書き切って
    /// から置き換える。直接上書きすると、書いている途中で容量が尽きた場合に
    /// 欠けたランチャーが残り、通常の起動手段ごと壊れる。
    /// ここで失敗しても app / cli の置き換えは成立しており、旧ランチャーは
    /// 無傷で、相対参照で新しい app を起動できるため、警告に留める。
    /// </summary>
    private void ReplaceLauncher()
    {
        var launcher = Path.Combine(_targetDirectory, LauncherName);
        var launcherTemp = launcher + ".new";
        try
        {
            File.Copy(Path.Combine(_sourceDirectory, LauncherName), launcherTemp, overwrite: true);
            File.Move(launcherTemp, launcher, overwrite: true);
        }
        catch (Exception ex)
        {
            LogQuietly(() => _logger.LogWarning(ex, "ランチャーを差し替えられなかった: {Name}", LauncherName));
            try { File.Delete(launcherTemp); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// インストール先が置き換えを終えた形になっているか。
    /// <para>
    /// 巻き戻しにも失敗した場合 (<see cref="UpdateRollbackException"/>)、正規の
    /// 位置が欠けたまま、入れ替え済みの側だけで起動できてしまうことがある。その
    /// まま後始末へ進むと、復旧の材料 (.old と取得しておいた ZIP) を消してしまう。
    /// 途中で終わった跡 (.new の残り) と、必要な一式の欠けを見る。
    /// </para>
    /// </summary>
    public static bool LooksComplete(string targetDirectory)
    {
        foreach (var part in Parts)
        {
            if (Directory.Exists(Path.Combine(targetDirectory, part + ".new"))) return false;
            if (!Directory.Exists(Path.Combine(targetDirectory, part))) return false;
        }

        foreach (var required in new[]
        {
            Path.Combine("app", AppExecutableName),
            Path.Combine("app", AppAssemblyName),
            Path.Combine("cli", CliExecutableName),
            Path.Combine("cli", CliAssemblyName),
        })
        {
            if (!File.Exists(Path.Combine(targetDirectory, required))) return false;
        }

        return true;
    }

    /// <summary>
    /// 置き換えの後始末。次の起動 (新しい版) から呼び、退避した .old を消す。
    /// 消せなくても常駐は続ける。次の機会にまた試す。
    /// </summary>
    public static void DiscardPrevious(string targetDirectory, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        foreach (var part in Parts)
        {
            var backup = Path.Combine(targetDirectory, part + ".old");
            try
            {
                if (!Directory.Exists(backup)) continue;
                Directory.Delete(backup, recursive: true);
                LogQuietly(() => log.LogInformation("置き換え前の {Part} を消した", part));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogQuietly(() => log.LogWarning(ex, "置き換え前の {Part} を消せなかった", part));
            }
        }
    }

    /// <summary>
    /// 形が想定どおりかを、正規の位置へ触る前に見る。
    /// 欠けた一式で進むと、リネームの途中で気付くことになり、戻す作業が増える。
    /// </summary>
    private void ValidateLayout()
    {
        foreach (var part in Parts)
        {
            if (!Directory.Exists(Path.Combine(_sourceDirectory, part)))
            {
                throw new InvalidOperationException($"展開した一式に {part} が無い: {_sourceDirectory}");
            }
            if (!Directory.Exists(Path.Combine(_targetDirectory, part)))
            {
                throw new InvalidOperationException($"インストール先に {part} が無い: {_targetDirectory}");
            }
        }
        if (!File.Exists(Path.Combine(_sourceDirectory, LauncherName)))
        {
            throw new InvalidOperationException($"展開した一式に {LauncherName} が無い: {_sourceDirectory}");
        }

        // ディレクトリの存在だけでは足りない。digest が保証するのは ZIP が配布物
        // そのものであることまでで、中身の形は保証しない。app の exe が欠けた
        // 一式で進むと、現行を退避した後に起動できないディレクトリへ置き換えて
        // しまう。ランチャーと再起動処理が参照する exe を必須として見る。
        //
        // 本体のアセンブリも見る。配布は単一ファイルにまとめていないので、exe は
        // 起動の入り口でしかない。exe だけそろっていても、隣の dll が欠けていれば
        // 起動できず、置き換えだけが成功して App を開けなくなる。
        foreach (var required in new[]
        {
            Path.Combine("app", AppExecutableName),
            Path.Combine("app", AppAssemblyName),
            Path.Combine("cli", CliExecutableName),
            Path.Combine("cli", CliAssemblyName),
        })
        {
            if (!File.Exists(Path.Combine(_sourceDirectory, required)))
            {
                throw new InvalidOperationException($"展開した一式に {required} が無い: {_sourceDirectory}");
            }
        }
    }

    /// <summary>入れ替えの途中で失敗したとき、済ませた分を逆順で戻す。</summary>
    private void RollbackSwapped(List<string> swapped)
    {
        for (var i = swapped.Count - 1; i >= 0; i--)
        {
            var current = Path.Combine(_targetDirectory, swapped[i]);
            var backup = Path.Combine(_targetDirectory, swapped[i] + ".old");
            try
            {
                DeleteDirectoryIfExists(current);
                Move(backup, current);
            }
            catch (Exception ex)
            {
                LogQuietly(() => _logger.LogError(ex,
                    "退避した {Part} を戻せなかった。{Backup} を {Current} へ戻す必要がある",
                    swapped[i], backup, current));
                throw new UpdateRollbackException(
                    $"退避した {swapped[i]} を戻せなかった: {backup} を {current} へ戻す必要がある", ex);
            }
        }
    }

    /// <summary>
    /// 入れ替えの最中のログは、失敗しても流れを止めない。
    /// <para>
    /// ログの出力先 (%AppData%) が書き込み不可だったり容量が尽きていたりすると、
    /// このリポジトリのロガーは例外を投げる。入れ替えの途中でそれが飛ぶと、
    /// 巻き戻しを通らずに抜けてしまい、新しい app と古い cli が混ざった一式が残る。
    /// </para>
    /// </summary>
    private static void LogQuietly(Action log)
    {
        try { log(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// 実ディレクトリの移動。置き換えの成否はこの呼び出しだけで決まる。
    /// 1 か所にまとめてあるのは失敗の場合分けをここへ寄せるためで、
    /// テストではここを差し替えて、戻しまで失敗する状況を作る。
    /// </summary>
    protected virtual void Move(string from, string to) => Directory.Move(from, to);

    /// <summary>複製もテストから差し替えられるよう分けておく。</summary>
    protected virtual void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var destination = Path.Combine(to, relative);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}

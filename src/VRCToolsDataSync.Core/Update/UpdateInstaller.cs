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
                    _logger.LogError(restore,
                        "退避した {Part} を戻せなかった。{Backup} を {Current} へ戻す必要がある",
                        part, backup, current);
                    throw new UpdateRollbackException(
                        $"退避した {part} を戻せなかった: {backup} を {current} へ戻す必要がある", restore);
                }
                RollbackSwapped(swapped);
                throw new InvalidOperationException($"{part} を置き換えられなかった", ex);
            }

            swapped.Add(part);
            _logger.LogInformation("{Part} を置き換えた", part);
        }

        // (4) ランチャーは 1 ファイルの上書きで済ませる。ここで失敗しても
        //     app / cli の置き換えは成立しており、旧ランチャーでも相対参照で
        //     新しい app を起動できるため、警告に留める。
        try
        {
            File.Copy(
                Path.Combine(_sourceDirectory, LauncherName),
                Path.Combine(_targetDirectory, LauncherName),
                overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ランチャーを上書きできなかった: {Name}", LauncherName);
        }
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
                log.LogInformation("置き換え前の {Part} を消した", part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "置き換え前の {Part} を消せなかった", part);
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
                _logger.LogError(ex,
                    "退避した {Part} を戻せなかった。{Backup} を {Current} へ戻す必要がある",
                    swapped[i], backup, current);
                throw new UpdateRollbackException(
                    $"退避した {swapped[i]} を戻せなかった: {backup} を {current} へ戻す必要がある", ex);
            }
        }
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

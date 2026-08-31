using System.Text.Json.Serialization;
using System.Text.Json;

namespace VRCToolsDataSync.Core.Domain;

/// <summary>
/// manifest そのものの置き場所。同期先のルート直下に置く。
/// <para>
/// 同期先の実装がキーを組み立てるために要る。読み書きの手順 (<c>ManifestStore</c>)
/// とは別の層にあるので、キーだけを Domain に置く。
/// </para>
/// </summary>
public static class ManifestKeys
{
    public const string Manifest = "manifest.json";
}

public sealed class SyncManifest
{
    /// <summary>
    /// 書き出す manifest の形式。
    /// <para>
    /// 2 で、実データの置き場所を内容から決まるキー (<see cref="BlobKeys"/>) に変えた。
    /// 1 を書いた版は <see cref="ManifestFile.RelativePath"/> をそのままキーとして扱う
    /// ため、2 の manifest からは目的のオブジェクトを見つけられない。読み込み側は
    /// 1 も扱えるが、逆は成り立たない。
    /// </para>
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Dictionary<string, ToolManifestEntry> Tools { get; set; } = new();
}

public sealed class ToolManifestEntry
{
    public long Version { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
}

public sealed class ManifestFile
{
    /// <summary>
    /// 同期先の中でのファイルの位置。区切りは常に '/' で、先頭に '/' は付けない
    /// (例: "vrcx/latest.sqlite3")。ローカルフォルダモードではクラウドフォルダからの
    /// 相対パス、S3 互換モードではキー接頭辞を除いたオブジェクトキーに対応する。
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// 実データを置いてあるキー。内容から決まる (<see cref="BlobKeys.FromSha256"/>)。
    /// <para>
    /// schemaVersion 1 の manifest には無い。その場合は
    /// <see cref="RelativePath"/> がそのままキーだったので、読み込み側は
    /// <see cref="ManifestFileKeys.StorageKeyOf"/> を通して解決する。
    /// </para>
    /// </summary>
    public string? BlobKey { get; set; }
}

/// <summary>
/// <see cref="ManifestFile"/> から実データのキーを取り出す。
/// </summary>
public static class ManifestFileKeys
{
    /// <summary>
    /// 実データが置いてあるキー。schemaVersion 1 の manifest (BlobKey が無い) では
    /// RelativePath がそのままキーだったので、そちらへ落とす。
    /// </summary>
    public static string StorageKeyOf(ManifestFile file)
        => string.IsNullOrEmpty(file.BlobKey) ? file.RelativePath : file.BlobKey;
}

/// <summary>
/// 読み込んだ manifest と、その時点の内容を表すタグの組。
/// タグは条件付き更新 (compare-and-swap) に使う。読み込み時点で manifest が
/// 存在しなかった場合と、同期先がタグを提供しない場合は null になる。
/// </summary>
public sealed record ManifestSnapshot(SyncManifest Manifest, string? VersionTag);

/// <summary>manifest.json のシリアライズ設定。同期先を問わず同じ形式で書き出す。</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

using System.Text.Json.Serialization;

namespace MindCraftLauncher.Core.Mods;

/// <summary>
/// Modrinth 公开接口（2026-11-11 实测可用，无需 API Key）：
///   搜索     GET /v2/search?query=&amp;limit=&amp;offset=&amp;facets=[[...]]
///   版本列表 GET /v2/project/{id|slug}/version?game_versions=["x"]&amp;loaders=["fabric"]
/// 下载走 cdn.modrinth.com 直链。CurseForge 需要 API Key，本版未接。
/// </summary>
public static class ModrinthApi
{
    public const string Base = "https://api.modrinth.com/v2";

    public static string SearchUrl(string query, string projectType, string? gameVersion, string? loader,
        int limit = 20, int offset = 0) =>
        $"{Base}/search?query={Uri.EscapeDataString(query ?? "")}" +
        $"&limit={limit}&offset={offset}" +
        $"&facets={Uri.EscapeDataString(BuildFacets(projectType, gameVersion, loader))}";

    /// <summary>facets 形如 [["project_type:mod"],["versions:1.20.1"],["categories:fabric"]]。</summary>
    public static string BuildFacets(string projectType, string? gameVersion, string? loader)
    {
        // 用逐字字符串拼 JSON，避免转义层数过多导致读错
        var facets = new List<string> { $@"[""project_type:{projectType}""]" };
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add($@"[""versions:{gameVersion}""]");
        if (!string.IsNullOrWhiteSpace(loader)) facets.Add($@"[""categories:{loader}""]");
        return "[" + string.Join(",", facets) + "]";
    }

    public static string VersionsUrl(string projectId, string? gameVersion, string? loader)
    {
        var url = $"{Base}/project/{Uri.EscapeDataString(projectId)}/version";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            parts.Add("game_versions=" + Uri.EscapeDataString($@"[""{gameVersion}""]"));
        if (!string.IsNullOrWhiteSpace(loader))
            parts.Add("loaders=" + Uri.EscapeDataString($@"[""{loader}""]"));
        return parts.Count > 0 ? url + "?" + string.Join("&", parts) : url;
    }

    public static string ProjectUrl(string projectId) =>
        $"{Base}/project/{Uri.EscapeDataString(projectId)}";

    public static string VersionUrl(string versionId) =>
        $"{Base}/version/{Uri.EscapeDataString(versionId)}";
}


public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")] public List<ModrinthHit> Hits { get; set; } = new();
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthHit
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();

    /// <summary>下载量友好显示。</summary>
    public string DownloadsText => Downloads >= 1_000_000
        ? (Downloads / 1_000_000.0).ToString("0.#") + "M"
        : Downloads >= 1_000 ? (Downloads / 1_000.0).ToString("0.#") + "k" : Downloads.ToString();

    public string Summary => Title + "　by " + Author + "　·　" + DownloadsText;
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("date_published")] public string DatePublished { get; set; } = "";
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();

    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();

    public int RequiredDependencyCount => Dependencies.Count(d =>
        string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase));

    public string Display => VersionNumber + "　（" + string.Join(", ", GameVersions) +
                             (Loaders.Count > 0 ? "｜" + string.Join("/", Loaders) : "") + "）" +
                             (RequiredDependencyCount > 0 ? $"　必需依赖 {RequiredDependencyCount} 个" : "");

    public override string ToString() => Display;
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("hashes")] public ModrinthHashes? Hashes { get; set; }
}

public sealed class ModrinthHashes
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
}
// ... (前面是你的 ModrinthHashes 类)
// 【在这里添加】--------------------------------------------------

public sealed class ModrinthProjectDetail
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
}

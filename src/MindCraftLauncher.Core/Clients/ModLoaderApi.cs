using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace MindCraftLauncher.Core.Clients;

/// <summary>
/// Fabric / Forge 的官方接口。下列端点与响应结构在 2026-11-01 实测可用。
/// NeoForge 的 maven 元数据接口当时取不到内容，未纳入实现——需要时按同样形状补一个 provider 即可。
/// </summary>
public static class ModLoaderApi
{
    // ---- Fabric ----
    public const string FabricMetaRoot = "https://meta.fabricmc.net";
    public const string FabricLoaderVersions = FabricMetaRoot + "/v2/versions/loader";

    /// <summary>某个 MC 版本可用的 loader 列表。</summary>
    public static string FabricLoadersFor(string gameVersion) => $"{FabricLoaderVersions}/{Uri.EscapeDataString(gameVersion)}";

    /// <summary>直接拿到可用的 version json（含 inheritsFrom / mainClass / libraries）。</summary>
    public static string FabricProfile(string gameVersion, string loaderVersion) =>
        $"{FabricLoaderVersions}/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

    /// <summary>BMCLAPI 的 Fabric 元数据镜像前缀（实测 /fabric-meta 可用）。</summary>
    public static string FabricMirror(string originalUrl) =>
        originalUrl.Replace(FabricMetaRoot, "https://bmclapi2.bangbang93.com/fabric-meta", StringComparison.OrdinalIgnoreCase);

    // ---- Forge ----
    public const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string ForgeMavenMetadataUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";

    public const string NeoForgeMavenMetadataUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    public static string NeoForgeInstallerJar(string version) =>
        $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{Uri.EscapeDataString(version)}/neoforge-{Uri.EscapeDataString(version)}-installer.jar";

    /// <summary>把 Minecraft 版本映射到 NeoForge Maven 版本前缀，例如 1.21.1 -> 21.1。</summary>
    public static string NeoForgeGamePrefix(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            throw new ArgumentException("无法识别 Minecraft 版本：" + gameVersion, nameof(gameVersion));
        var neoMajor = major == 1 ? minor : major * 10 + minor;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;
        return patch > 0 ? $"{neoMajor}.{patch}" : $"{neoMajor}.0";
    }

    /// <summary>Forge 安装包直链。</summary>
    public static string ForgeInstallerJar(string forgeVersion) =>
        $"https://maven.minecraftforge.net/net/minecraftforge/forge/{forgeVersion}/forge-{forgeVersion}-installer.jar";

    /// <summary>BMCLAPI 的 Forge maven 镜像（会 302 到分发节点）。</summary>
    public static string ForgeInstallerJarMirror(string forgeVersion) =>
        $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{forgeVersion}/forge-{forgeVersion}-installer.jar";

    /// <summary>Forge 版本号形如 "1.20.1-47.4.0"，前半段是 MC 版本。</summary>
    public static bool TrySplitForgeVersion(string forgeVersion, out string gameVersion, out string forgePart)
    {
        gameVersion = ""; forgePart = "";
        var idx = forgeVersion.IndexOf('-');
        if (idx <= 0 || idx >= forgeVersion.Length - 1) return false;
        gameVersion = forgeVersion[..idx];
        forgePart = forgeVersion[(idx + 1)..];
        return true;
    }

    /// <summary>从 maven-metadata.xml 里取所有版本号（倒序，最新在前）。</summary>
    public static List<string> ParseMavenMetadataVersions(string xml)
    {
        var doc = XDocument.Parse(xml);
        var versions = doc.Descendants("version")
            .Select(v => v.Value?.Trim() ?? "")
            .Where(v => v.Length > 0)
            .ToList();
        // maven-metadata 的 <version> 是升序，反转成最新在前
        versions.Reverse();
        return versions;
    }

    /// <summary>按 MC 版本过滤 Forge 版本号。</summary>
    public static List<string> FilterByGameVersion(IEnumerable<string> forgeVersions, string gameVersion) =>
        forgeVersions.Where(v => TrySplitForgeVersion(v, out var g, out _) && g == gameVersion).ToList();

    /// <summary>promotions_slim.json：{"promos":{"1.20.1-recommended":"47.2.0","1.20.1-latest":"47.4.0"}}。</summary>
    public static string? RecommendedVersion(PromotionsSlim? promotions, string gameVersion)
    {
        if (promotions?.Promos is null) return null;
        return promotions.Promos.TryGetValue(gameVersion + "-recommended", out var rec) && !string.IsNullOrEmpty(rec)
            ? $"{gameVersion}-{rec}"
            : null;
    }

    public static string? LatestVersion(PromotionsSlim? promotions, string gameVersion)
    {
        if (promotions?.Promos is null) return null;
        return promotions.Promos.TryGetValue(gameVersion + "-latest", out var latest) && !string.IsNullOrEmpty(latest)
            ? $"{gameVersion}-{latest}"
            : null;
    }

    /// <summary>比较两个 Forge 尾号（47.4.0 与 47.2.30），用于把推荐版排到列表最前。</summary>
    public static int CompareForgeParts(string a, string b)
    {
        var pa = a.Split('.'); var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var na = i < pa.Length && int.TryParse(pa[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
            var nb = i < pb.Length && int.TryParse(pb[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;
            if (na != nb) return nb.CompareTo(na);
        }
        return 0;
    }
}

public sealed class PromotionsSlim
{
    [JsonPropertyName("homepage")] public string Homepage { get; set; } = "";
    [JsonPropertyName("promos")] public Dictionary<string, string> Promos { get; set; } = new();
}

/// <summary>Fabric loader 条目。</summary>
public sealed class FabricLoaderEntry
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("stable")] public bool Stable { get; set; }
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
}

/// <summary>Fabric "某 MC 版本可用 loader" 的响应条目。</summary>
public sealed class FabricLoaderForVersion
{
    [JsonPropertyName("loader")] public FabricLoaderEntry Loader { get; set; } = new();
    [JsonPropertyName("intermediary")] public FabricLoaderEntry Intermediary { get; set; } = new();
}

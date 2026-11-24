using System.Text.Json.Serialization;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Clients;

/// <summary>
/// LabyMod 4 官方发布接口。以下端点与响应结构在 2026-11-01 实测可用：
///   频道列表   GET https://releases.r2.labymod.net/api/v1/channels.json                     → ["production","snapshot"]
///   频道清单   GET https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest/{ch}/latest.json
///   客户端jar  GET https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/labymod4/{ch}/{commit}.jar
///   资源包jar  GET https://releases.r2.labymod.net/api/v1/download/assets/labymod4/{ch}/{commit}/{asset}/{checksum}.jar
///   版本描述   minecraftVersions[].customManifestUrl（完整的 version json，mainClass 已是 LabyMod 的启动入口）
/// 接口如变动，改这一处常量即可，UI 与安装流程不用动。
/// </summary>
public static class LabyModApi
{
    public const string ChannelsUrl = "https://releases.r2.labymod.net/api/v1/channels.json";
    public const string ManifestUrlTemplate = "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest/{0}/latest.json";
    public const string ClientJarUrlTemplate = "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/download/labymod4/{0}/{1}.jar";
    public const string AssetJarUrlTemplate = "https://releases.r2.labymod.net/api/v1/download/assets/labymod4/{0}/{1}/{2}/{3}.jar";

    public static string ManifestUrl(string channel) => string.Format(ManifestUrlTemplate, channel);
    public static string ClientJarUrl(string channel, string commit) => string.Format(ClientJarUrlTemplate, channel, commit);
    public static string AssetJarUrl(string channel, string commit, string asset, string checksum) =>
        string.Format(AssetJarUrlTemplate, channel, commit, asset, checksum);
}

public sealed class LabyModManifest
{
    [JsonPropertyName("labyModVersion")] public string LabyModVersion { get; set; } = "";
    [JsonPropertyName("commitReference")] public string CommitReference { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("releaseTime")] public long ReleaseTime { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("minimumLauncherVersion")] public string MinimumLauncherVersion { get; set; } = "";
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; set; } = new();
    [JsonPropertyName("minecraftVersions")] public List<LabyModMcVersion> MinecraftVersions { get; set; } = new();

    public LabyModMcVersion? FindMcVersion(string tag) =>
        MinecraftVersions.FirstOrDefault(v => string.Equals(v.Tag, tag, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(v.Version, tag, StringComparison.OrdinalIgnoreCase));
}

public sealed class LabyModMcVersion
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("customManifestUrl")] public string CustomManifestUrl { get; set; } = "";
    [JsonPropertyName("runtime")] public Runtime? RuntimeVersion { get; set; }

    public sealed class Runtime
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version")] public int Version { get; set; }
    }
}

/// <summary>LabyMod 安装结果：额外需要注入 classpath 的 jar（按顺序），以及安装出的版本 id。</summary>
public sealed record LabyModInstallResult(string VersionId, string BaseVersionId, string LabyModVersion,
    string ClientJarPath, IReadOnlyList<string> ExtraClassPath, IReadOnlyList<string> AssetJars);

/// <summary>
/// LabyMod 安装器。策略：先保证基版本装好，再把 LabyMod 的 version json 落成独立版本
/// （id = "{tag}-LabyMod"），LabyMod-4.jar 与资源包 jar 放到 libraries / labymod-neo 目录，
/// 并作为额外 classpath 项返回给启动层。
/// </summary>
public sealed class LabyModInstaller
{
    public const string VersionSuffix = "-LabyMod";

    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly IDownloadSource _source;
    private readonly FeatureFlags _flags;
    private readonly VersionInstaller _versions;

    public LabyModInstaller(HttpFetcher http, GamePaths paths, IDownloadSource source,
        FeatureFlags flags, VersionInstaller versions)
    {
        _http = http;
        _paths = paths;
        _source = source;
        _flags = flags;
        _versions = versions;
    }

    public Task<List<string>> ListChannelsAsync(CancellationToken ct = default)
        => _http.GetJsonAsync<List<string>>(LabyModApi.ChannelsUrl, null, ct);

    public async Task<LabyModManifest> GetManifestAsync(string channel, CancellationToken ct = default)
    {
        _flags.EnsureAllowed(_flags.LabyModDownloadEnabled, "LabyMod 下载");
        return await _http.GetJsonAsync<LabyModManifest>(LabyModApi.ManifestUrl(channel), null, ct).ConfigureAwait(false);
    }

    public async Task<LabyModInstallResult> InstallAsync(string channel, string mcVersionTag,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureAllowed(_flags.LabyModDownloadEnabled, "LabyMod 下载");

        var manifest = await GetManifestAsync(channel, ct).ConfigureAwait(false);
        var target = manifest.FindMcVersion(mcVersionTag)
                     ?? throw new InvalidOperationException($"LabyMod {channel} 频道当前不支持 Minecraft {mcVersionTag}");

        // 1) 基版本必须先存在
        progress?.Report(new InstallProgress("base", 0, 4, $"先安装 Minecraft {target.Version}"));
        await _versions.InstallAsync(target.Version, progress, ct).ConfigureAwait(false);

        // 2) LabyMod 的 version json
        progress?.Report(new InstallProgress("profile", 1, 4, "读取 LabyMod 版本描述"));
        var profile = await _http.GetJsonAsync<VersionJson>(target.CustomManifestUrl, null, ct).ConfigureAwait(false);
        var newId = target.Version + VersionSuffix;
        profile.Id = newId;
        profile.InheritsFrom = target.Version;

        var versionIdLaby = newId;
        Directory.CreateDirectory(_paths.VersionRoot(versionIdLaby));
        File.WriteAllText(_paths.VersionJson(versionIdLaby), JsonOpt.Serialize(profile));

        // 3) 主 jar
        progress?.Report(new InstallProgress("jar", 2, 4, $"下载 LabyMod-4.jar（{manifest.LabyModVersion}）"));
        var jarPath = Path.Combine(_paths.Libraries, "net", "labymod", "LabyMod-4",
            manifest.LabyModVersion, $"LabyMod-4-{manifest.LabyModVersion}.jar");
        await _http.DownloadFileAsync(LabyModApi.ClientJarUrl(channel, manifest.CommitReference),
            jarPath, manifest.Sha1, null, ct).ConfigureAwait(false);

        // 4) 资源包 jar（shader / common / fonts / 主题 / i18n）
        progress?.Report(new InstallProgress("assets", 3, 4, $"下载 LabyMod 资源包（{manifest.Assets.Count} 个）"));
        var assetDir = Path.Combine(_paths.Root, "labymod-neo", "assets");
        Directory.CreateDirectory(assetDir);
        var assetJars = new List<string>();
        foreach (var (name, checksum) in manifest.Assets)
        {
            var local = Path.Combine(assetDir, name + ".jar");
            await _http.DownloadFileAsync(LabyModApi.AssetJarUrl(channel, manifest.CommitReference, name, checksum),
                local, checksum, null, ct).ConfigureAwait(false);
            assetJars.Add(local);
        }

        // launchwrapper 入口需要 LabyMod jar 紧跟在客户端 jar 之后进入 classpath
        var extra = new List<string> { jarPath };

        // LabyMod profile 里的 libraries 也要补齐（它比基版本多出一批依赖）
        await _versions.InstallLibrariesAsync(profile, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("done", 4, 4, $"LabyMod {manifest.LabyModVersion} 安装完成，版本：{newId}"));
        return new LabyModInstallResult(versionIdLaby, target.Version, manifest.LabyModVersion, jarPath, extra, assetJars);
    }
}

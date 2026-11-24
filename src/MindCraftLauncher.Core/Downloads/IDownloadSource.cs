namespace MindCraftLauncher.Core.Downloads;

/// <summary>
/// 下载源抽象。移植 PCL 时，把 PCL 里的 Mirror/下载源实现适配到这个接口即可，
/// UI 层只依赖 <see cref="DownloadSourceRegistry"/>。
/// </summary>
public interface IDownloadSource
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>版本清单地址（Mojang 原始地址，由实现决定是否重写）。</summary>
    string VersionManifestUrl { get; }

    /// <summary>把官方 URL 重写成本源的 URL。</summary>
    string RewriteUrl(string originalUrl);
}

/// <summary>Mojang 官方源，不做任何重写。</summary>
public sealed class MojangDownloadSource : IDownloadSource
{
    public const string ManifestV2 = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public string Id => "mojang";
    public string DisplayName => "Mojang 官方";
    public string VersionManifestUrl => ManifestV2;
    public string RewriteUrl(string originalUrl) => originalUrl;
}

/// <summary>
/// BMCLAPI（bangbang93）镜像。重写规则取自 BMCLAPI 官方说明，域名替换关系为：
/// launchermeta/piston-meta/piston-data.mojang.com → 镜像根；resources.download.minecraft.net → /assets；
/// libraries.minecraft.net → /maven；authlib-injector.yushi.moe → /mirrors/authlib-injector。
/// 镜像节点会通过 302 跳转到具体分发节点，HttpClient 默认跟随跳转。
/// </summary>
public sealed class BmclApiDownloadSource : IDownloadSource
{
    public const string Root = "https://bmclapi2.bangbang93.com";

    private static readonly (string From, string To)[] Rules =
    {
        ("https://piston-data.mojang.com", Root),
        ("https://piston-meta.mojang.com", Root),
        ("https://launchermeta.mojang.com", Root),
        ("http://launchermeta.mojang.com", Root),
        ("https://launcher.mojang.com", Root),
        ("http://launcher.mojang.com", Root),
        ("https://resources.download.minecraft.net", Root + "/assets"),
        ("http://resources.download.minecraft.net", Root + "/assets"),
        ("https://libraries.minecraft.net", Root + "/maven"),
        ("http://libraries.minecraft.net", Root + "/maven"),
        ("https://authlib-injector.yushi.moe", Root + "/mirrors/authlib-injector"),
        ("http://authlib-injector.yushi.moe", Root + "/mirrors/authlib-injector"),
    };

    public string Id => "bmclapi";
    public string DisplayName => "BMCLAPI 国内镜像";

    // 实测：/mc/game/version_manifest.json 与 _v2 都能取到内容一致的清单（302 后 200）
    public string VersionManifestUrl => Root + "/mc/game/version_manifest.json";

    public string RewriteUrl(string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl)) return originalUrl;
        foreach (var (from, to) in Rules)
        {
            if (originalUrl.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + originalUrl.Substring(from.Length);
        }
        return originalUrl;
    }
}

/// <summary>按配置挑选下载源。</summary>
public sealed class DownloadSourceRegistry
{
    private readonly List<IDownloadSource> _sources = new()
    {
        new MojangDownloadSource(),
        new BmclApiDownloadSource(),
    };

    public IReadOnlyList<IDownloadSource> All => _sources;

    public IDownloadSource Pick(string? id) =>
        _sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? _sources[0];
}

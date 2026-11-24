using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Clients;

/// <summary>
/// 加载器安装的统一入口。UI 只认这一个对象，
/// 以后要加 NeoForge / Quilt，只要在这里多注册一个 provider，界面不用改。
/// </summary>
public sealed class ModLoaderService
{
    private readonly FabricInstaller _fabric;
    private readonly ForgeInstaller _forge;
    private readonly NeoForgeInstaller _neoForge;
    private readonly OptiFineInstaller _optifine;

    public ModLoaderService(HttpFetcher http, GamePaths paths, FeatureFlags flags, VersionInstaller versions,
        IDownloadSource source, string? explicitJava)
    {
        var useMirror = string.Equals(source.Id, "bmclapi", StringComparison.OrdinalIgnoreCase);
        _fabric = new FabricInstaller(http, paths, flags, versions, useMirror);
        _forge = new ForgeInstaller(http, paths, flags, versions, useMirror, explicitJava);
        _neoForge = new NeoForgeInstaller(http, paths, flags, versions, explicitJava);
        _optifine = new OptiFineInstaller(http, paths, flags, versions, explicitJava);
    }

    /// <summary>列出某个 MC 版本可装的加载器版本。</summary>
    public Task<List<ModLoaderVersion>> ListAsync(string kind, string gameVersion, CancellationToken ct = default) =>
        kind switch
        {
            ModLoaderKind.Fabric => ListFabricAsync(gameVersion, ct),
            ModLoaderKind.Forge => _forge.ListAsync(gameVersion, ct),
            ModLoaderKind.NeoForge => _neoForge.ListAsync(gameVersion, ct),
            ModLoaderKind.OptiFine => _optifine.ListAsync(gameVersion, ct),
            _ => throw new ArgumentException("不支持的加载器类型：" + kind),
        };

    private async Task<List<ModLoaderVersion>> ListFabricAsync(string gameVersion, CancellationToken ct)
    {
        var entries = await _fabric.ListAsync(gameVersion, ct).ConfigureAwait(false);
        return entries
            .Select(e => new ModLoaderVersion(
                $"{gameVersion}-fabric-{e.Loader.Version}",
                gameVersion, e.Loader.Version, ModLoaderKind.Fabric,
                e.Loader.Stable ? "稳定版" : ""))
            .ToList();
    }

    /// <summary>安装加载器，返回版本 id（OptiFine 放进现有 Forge 版本的 mods 时返回那个版本 id）。</summary>
    public Task<string> InstallAsync(string kind, string gameVersion, string loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
        kind switch
        {
            ModLoaderKind.Fabric => _fabric.InstallAsync(gameVersion, loaderVersion, progress, ct),
            ModLoaderKind.Forge => _forge.InstallAsync(gameVersion, loaderVersion, progress, ct),
            ModLoaderKind.NeoForge => _neoForge.InstallAsync(gameVersion, loaderVersion, progress, ct),
            ModLoaderKind.OptiFine => InstallOptiFineAsync(gameVersion, loaderVersion, progress, ct),
            _ => throw new ArgumentException("不支持的加载器类型：" + kind),
        };

    private async Task<string> InstallOptiFineAsync(string gameVersion, string loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var result = await _optifine.InstallAsync(gameVersion, loaderVersion, progress, ct).ConfigureAwait(false);
        return result.VersionId;
    }
}
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Mods;

/// <summary>模组安装：把一个 project 的某个版本下载成 jar，放进目标版本的 mods 目录。</summary>
public sealed class ModInstaller
{
    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly FeatureFlags _flags;

    public ModInstaller(HttpFetcher http, GamePaths paths, FeatureFlags flags)
    {
        _http = http;
        _paths = paths;
        _flags = flags;
    }

    public Task<List<ModrinthVersion>> ListVersionsAsync(string projectId, string? gameVersion, string? loader,
        CancellationToken ct = default) =>
        _http.GetJsonAsync<List<ModrinthVersion>>(ModrinthApi.VersionsUrl(projectId, gameVersion, loader), null, ct);

    /// <summary>安装模组及其必需依赖，返回主模组落盘路径。</summary>
    public async Task<string> InstallAsync(string projectId, string versionId, string targetVersionId,
        bool versionIsolation, IProgress<InstallProgress>? progress, CancellationToken ct = default,
        string? gameVersion = null, string? loader = null)
    {
        _flags.EnsureGameDownload();
        var modsDir = _paths.Mods(targetVersionId, versionIsolation);
        Directory.CreateDirectory(modsDir);

        var installedVersions = new HashSet<string>(StringComparer.Ordinal);
        var mainPath = await InstallVersionTreeAsync(projectId, versionId, modsDir, gameVersion, loader,
            installedVersions, progress, ct).ConfigureAwait(false);
        progress?.Report(new InstallProgress("done", installedVersions.Count, installedVersions.Count,
            $"已安装 {installedVersions.Count} 个模组及必需依赖到 {modsDir}"));
        return mainPath;
    }

    private async Task<string> InstallVersionTreeAsync(string projectId, string versionId, string modsDir,
        string? gameVersion, string? loader, HashSet<string> installedVersions,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!installedVersions.Add(versionId)) return "";

        var version = await _http.GetJsonAsync<ModrinthVersion>(ModrinthApi.VersionUrl(versionId), null, ct)
            .ConfigureAwait(false);
        if (version.ProjectId.Length > 0 && !string.Equals(version.ProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Modrinth 版本 {versionId} 不属于项目 {projectId}");
        if (!string.IsNullOrWhiteSpace(gameVersion) && version.GameVersions.Count > 0 &&
            !version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"模组版本 {version.VersionNumber} 不支持 Minecraft {gameVersion}");
        if (!string.IsNullOrWhiteSpace(loader) && version.Loaders.Count > 0 &&
            !version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"模组版本 {version.VersionNumber} 不支持 {loader}");

        var file = version.PrimaryFile ?? throw new InvalidOperationException($"模组版本 {version.VersionNumber} 没有可下载文件");
        var fileName = Path.GetFileName(file.Filename);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            throw new InvalidOperationException($"模组文件名无效：{file.Filename}");
        var target = Path.Combine(modsDir, fileName);
        progress?.Report(new InstallProgress("mod", installedVersions.Count - 1, -1,
            $"下载 {fileName}（{file.Size / 1024} KB）"));
        await _http.DownloadFileAsync(file.Url, target, file.Hashes?.Sha1, null, ct).ConfigureAwait(false);

        foreach (var dependency in version.Dependencies.Where(d =>
                     string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dependency.VersionId) && string.IsNullOrWhiteSpace(dependency.ProjectId))
                throw new InvalidOperationException($"模组 {fileName} 声明了没有项目或版本 ID 的必需依赖");

            var dependencyVersionId = dependency.VersionId;
            var dependencyProjectId = dependency.ProjectId;
            if (!string.IsNullOrWhiteSpace(dependencyVersionId))
            {
                if (string.IsNullOrWhiteSpace(dependencyProjectId))
                {
                    var fixedVersion = await _http.GetJsonAsync<ModrinthVersion>(
                        ModrinthApi.VersionUrl(dependencyVersionId), null, ct).ConfigureAwait(false);
                    dependencyProjectId = fixedVersion.ProjectId;
                }
            }
            else
            {
                var candidates = await ListVersionsAsync(dependencyProjectId!, gameVersion, loader, ct).ConfigureAwait(false);
                var compatible = candidates.FirstOrDefault(v => v.PrimaryFile is not null &&
                    (string.IsNullOrWhiteSpace(gameVersion) || v.GameVersions.Count == 0 ||
                     v.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(loader) || v.Loaders.Count == 0 ||
                     v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)));
                dependencyVersionId = compatible?.Id
                    ?? throw new InvalidOperationException($"找不到依赖项目 {dependencyProjectId} 对应 {gameVersion ?? "当前游戏版本"}/{loader ?? "当前加载器"} 的兼容版本");
            }

            if (string.IsNullOrWhiteSpace(dependencyProjectId) || string.IsNullOrWhiteSpace(dependencyVersionId))
                throw new InvalidOperationException($"无法解析模组 {fileName} 的必需依赖项目");
            await InstallVersionTreeAsync(dependencyProjectId, dependencyVersionId, modsDir, gameVersion, loader,
                installedVersions, progress, ct).ConfigureAwait(false);
        }

        return target;
    }
}

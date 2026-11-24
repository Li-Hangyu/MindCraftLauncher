using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Mods;

public enum ClientPackKind
{
    ResourcePack,
    ShaderPack,
}

public sealed class ClientPackInstaller
{
    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly FeatureFlags _flags;

    public ClientPackInstaller(HttpFetcher http, GamePaths paths, FeatureFlags flags)
    {
        _http = http;
        _paths = paths;
        _flags = flags;
    }

    public async Task<string> InstallAsync(string projectId, string versionId, string targetVersionId,
        bool versionIsolation, ClientPackKind kind, IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        var version = await _http.GetJsonAsync<ModrinthVersion>(ModrinthApi.VersionUrl(versionId), null, ct)
            .ConfigureAwait(false);
        if (version.ProjectId.Length > 0 && !string.Equals(version.ProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Modrinth 版本 {versionId} 不属于项目 {projectId}");

        var file = version.PrimaryFile
            ?? throw new InvalidOperationException($"版本 {version.VersionNumber} 没有可下载文件");
        var fileName = Path.GetFileName(file.Filename);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            throw new InvalidOperationException($"资源包文件名无效：{file.Filename}");

        var folder = kind switch
        {
            ClientPackKind.ResourcePack => "resourcepacks",
            ClientPackKind.ShaderPack => "shaderpacks",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不支持的客户端资源类型"),
        };
        var targetDirectory = Path.Combine(_paths.WorkingDirectory(targetVersionId, versionIsolation), folder);
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, fileName);

        progress?.Report(new InstallProgress("download", 0, 1, $"下载 {fileName}"));
        await _http.DownloadFileAsync(file.Url, targetPath, file.Hashes?.Sha1, null, ct).ConfigureAwait(false);
        progress?.Report(new InstallProgress("done", 1, 1, $"已安装到 {targetDirectory}"));
        return targetPath;
    }
}

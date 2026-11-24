using System.Diagnostics;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Launch;

namespace MindCraftLauncher.Core.Clients;

public sealed class NeoForgeInstaller
{
    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly FeatureFlags _flags;
    private readonly VersionInstaller _versions;
    private readonly string? _explicitJava;

    public NeoForgeInstaller(HttpFetcher http, GamePaths paths, FeatureFlags flags,
        VersionInstaller versions, string? explicitJava)
    {
        _http = http;
        _paths = paths;
        _flags = flags;
        _versions = versions;
        _explicitJava = explicitJava;
    }

    public async Task<List<ModLoaderVersion>> ListAsync(string gameVersion, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();
        var prefix = ModLoaderApi.NeoForgeGamePrefix(gameVersion) + ".";
        var metadata = await _http.GetStringAsync(ModLoaderApi.NeoForgeMavenMetadataUrl, null, ct).ConfigureAwait(false);
        return ModLoaderApi.ParseMavenMetadataVersions(metadata)
            .Where(version => version.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(version => version, Comparer<string>.Create(ModLoaderApi.CompareForgeParts))
            .Select(version => new ModLoaderVersion(
                $"{gameVersion}-neoforge-{version}", gameVersion, version, ModLoaderKind.NeoForge,
                "NeoForge 需要 Java 21"))
            .ToList();
    }

    public async Task<string> InstallAsync(string gameVersion, string neoForgeVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        if (!File.Exists(_paths.VersionJson(gameVersion)))
        {
            progress?.Report(new InstallProgress("base", 0, 5, $"确认 Minecraft {gameVersion} 已安装"));
            await _versions.InstallAsync(gameVersion, progress, ct).ConfigureAwait(false);
        }

        var installerPath = Path.Combine(GameLocation.LauncherDataRoot(), $"neoforge-{neoForgeVersion}-installer.jar");
        progress?.Report(new InstallProgress("installer", 1, 5, $"下载 NeoForge {neoForgeVersion} 安装包"));
        await _http.DownloadFileAsync(ModLoaderApi.NeoForgeInstallerJar(neoForgeVersion), installerPath, null, null, ct)
            .ConfigureAwait(false);

        progress?.Report(new InstallProgress("java", 2, 5, "检查 NeoForge 所需的 Java 21"));
        var java = JavaLocator.Find(_explicitJava, _paths.Root, 21)
                   ?? throw new InvalidOperationException("NeoForge 安装需要 Java 21 或更高版本。请在【设置】页指定 Java 21 的 java.exe。");

        progress?.Report(new InstallProgress("run", 3, 5, "运行 NeoForge 安装器"));
        var output = await RunInstallerAsync(java.ExecutablePath, installerPath, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("verify", 4, 5, "检查 NeoForge 安装结果"));
        var installedId = FindInstalledVersion(gameVersion, neoForgeVersion)
                         ?? throw new InvalidOperationException("NeoForge 安装器运行结束，但未找到新版本。\n" + Truncate(output, 1500));

        var profile = new InstalledVersionRepository(_paths).Load(installedId);
        if (profile is not null)
            await _versions.InstallLibrariesAsync(profile, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("done", 5, 5, $"NeoForge {neoForgeVersion} 已安装：{installedId}"));
        return installedId;
    }

    private async Task<string> RunInstallerAsync(string javaPath, string installerPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = _paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 NeoForge 安装器进程");
        using var cancellation = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"NeoForge 安装器退出码 {process.ExitCode}：\n{Truncate(output, 1000)}");
        return output;
    }

    private string? FindInstalledVersion(string gameVersion, string neoForgeVersion)
    {
        if (!Directory.Exists(_paths.Versions)) return null;
        return Directory.EnumerateDirectories(_paths.Versions)
            .Select(Path.GetFileName)
            .Where(id => id is not null && id.Contains(gameVersion, StringComparison.OrdinalIgnoreCase)
                && id.Contains(neoForgeVersion, StringComparison.OrdinalIgnoreCase)
                && File.Exists(_paths.VersionJson(id)))
            .OrderByDescending(id => File.GetLastWriteTimeUtc(_paths.VersionJson(id!)))
            .FirstOrDefault();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

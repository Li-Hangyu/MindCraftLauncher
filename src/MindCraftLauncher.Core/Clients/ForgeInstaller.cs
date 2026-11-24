using System.Diagnostics;
using System.Text;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Launch;

namespace MindCraftLauncher.Core.Clients;

/// <summary>
/// Forge 安装。与 Fabric 不同，Forge 的 profile 要靠官方 installer 里的 processors 生成，
/// 因此这一步必须在本机跑一次 Java：<c>java -jar forge-installer.jar --installClient</c>。
/// 安装器会把 versions/&lt;id&gt;/&lt;id&gt;.json 与 libraries 一并写好，这里只负责准备 jar、选 Java、转发输出。
/// </summary>
public sealed class ForgeInstaller
{
    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly FeatureFlags _flags;
    private readonly VersionInstaller _versions;
    private readonly bool _useMirror;
    private readonly string? _explicitJava;

    public ForgeInstaller(HttpFetcher http, GamePaths paths, FeatureFlags flags, VersionInstaller versions,
        bool useMirror = false, string? explicitJava = null)
    {
        _http = http;
        _paths = paths;
        _flags = flags;
        _versions = versions;
        _useMirror = useMirror;
        _explicitJava = explicitJava;
    }

    /// <summary>某个 MC 版本可用的 Forge 版本，推荐版排在最前。</summary>
    public async Task<List<ModLoaderVersion>> ListAsync(string gameVersion, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        var xml = await _http.GetStringAsync(ModLoaderApi.ForgeMavenMetadataUrl, null, ct).ConfigureAwait(false);
        var all = ModLoaderApi.ParseMavenMetadataVersions(xml);
        var matched = ModLoaderApi.FilterByGameVersion(all, gameVersion);

        PromotionsSlim? promos = null;
        try { promos = await _http.GetJsonAsync<PromotionsSlim>(ModLoaderApi.ForgePromotionsUrl, null, ct).ConfigureAwait(false); }
        catch { /* 推荐版信息取不到不影响列表 */ }

        var recommended = ModLoaderApi.RecommendedVersion(promos, gameVersion);
        var latest = ModLoaderApi.LatestVersion(promos, gameVersion);

        var result = matched
            .OrderBy(v => ModLoaderApi.CompareForgeParts(
                ModLoaderApi.TrySplitForgeVersion(v, out _, out var p) ? p : v, "0"))
            .Select(v => new ModLoaderVersion(v, gameVersion, Tail(v), ModLoaderKind.Forge,
                v == recommended ? "推荐" : v == latest ? "最新" : ""))
            .ToList();

        if (recommended is not null && result.All(r => r.Id != recommended))
            result.Insert(0, new ModLoaderVersion(recommended, gameVersion, Tail(recommended), ModLoaderKind.Forge, "推荐"));

        return result;
    }

    public async Task<string> InstallAsync(string gameVersion, string forgeVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        if (!File.Exists(_paths.VersionJson(gameVersion)))
        {
            progress?.Report(new InstallProgress("base", 0, 5, $"确认 Minecraft {gameVersion} 已安装"));
            await _versions.InstallAsync(gameVersion, progress, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress("installer", 1, 5, $"下载 Forge {forgeVersion} 安装包"));
        var installerUrl = _useMirror ? ModLoaderApi.ForgeInstallerJarMirror(forgeVersion) : ModLoaderApi.ForgeInstallerJar(forgeVersion);
        var installerJar = Path.Combine(GameLocation.LauncherDataRoot(), $"forge-{forgeVersion}-installer.jar");
        await _http.DownloadFileAsync(installerUrl, installerJar, null, null, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("java", 2, 5, "定位安装器所需的 Java"));
        var java = JavaLocator.Find(_explicitJava, _paths.Root, RequiredJavaMajor(gameVersion))
                   ?? throw new InvalidOperationException(
                       $"没找到 Forge {forgeVersion} 需要的 Java（{RequiredJavaMajor(gameVersion)} 及以上）。" +
                       "请在【设置】页指定 java.exe。");

        progress?.Report(new InstallProgress("run", 3, 5, "运行 Forge 安装器（--installClient）"));
        var output = await RunInstallerAsync(java.ExecutablePath, installerJar, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("verify", 4, 5, "确认安装结果"));
        var installedId = FindInstalledForgeVersion(gameVersion, forgeVersion)
                          ?? throw new InvalidOperationException("Forge 安装器跑完了，但没在 versions 目录里找到新版本。\n输出：\n" + Truncate(output, 1500));

        // 安装器写出的 profile 引用的库，部分需要再补齐一次（老版本 Forge 不会自己下 libraries）
        var repo = new InstalledVersionRepository(_paths);
        var profile = repo.Load(installedId);
        if (profile is not null)
            await _versions.InstallLibrariesAsync(profile, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("done", 5, 5, $"Forge {forgeVersion} 安装完成，版本：{installedId}"));
        return installedId;
    }

    /// <summary>按 Minecraft 版本选择 Forge 安装器所需的 JDK。</summary>
    internal static int RequiredJavaMajor(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return 17;

        if (major > 1) return 25;
        if (minor <= 16) return 8;
        if (minor <= 20)
        {
            if (minor == 20 && parts.Length >= 3 && int.TryParse(parts[2], out var patch) && patch >= 5)
                return 21;
            return 17;
        }
        if (minor == 21) return 21;
        return minor >= 22 ? 25 : 21;
    }

    private async Task<string> RunInstallerAsync(string javaExe, string installerJar, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            WorkingDirectory = _paths.Root,      // --installClient 以当前目录作为 .minecraft
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerJar);
        psi.ArgumentList.Add("--installClient");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Forge 安装器");
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is { } d) sb.AppendLine(d); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) sb.AppendLine(d); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var text = sb.ToString();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Forge 安装器退出码 {p.ExitCode}：\n{Truncate(text, 800)}");
        return text;
    }

    /// <summary>安装器可能把版本目录命名成 1.20.1-forge-47.4.0，也可能带 NeoForge 风格，这里按实际落盘结果找。</summary>
    private string? FindInstalledForgeVersion(string gameVersion, string forgeVersion)
    {
        if (!Directory.Exists(_paths.Versions)) return null;
        var candidates = Directory.EnumerateDirectories(_paths.Versions)
            .Select(Path.GetFileName)
            .Where(n => n is not null && n.Contains(gameVersion) && n.Contains(forgeVersion))
            .Where(n => File.Exists(_paths.VersionJson(n!)))
            .ToList();
        return candidates.OrderByDescending(n => Directory.GetLastWriteTime(_paths.VersionJson(n!))).FirstOrDefault();
    }

    private static string Tail(string forgeVersion) =>
        ModLoaderApi.TrySplitForgeVersion(forgeVersion, out _, out var tail) ? tail : forgeVersion;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

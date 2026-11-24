using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Clients;

/// <summary>一个可安装的加载器版本条目。</summary>
public sealed record ModLoaderVersion(string Id, string GameVersion, string LoaderVersion, string Kind, string Note);

/// <summary>加载器种类。</summary>
public static class ModLoaderKind
{
    public const string Vanilla = "原版";
    public const string Fabric = "Fabric";
    public const string Forge = "Forge";
    public const string NeoForge = "NeoForge";
    public const string OptiFine = "OptiFine";

    public static readonly string[] All = { Fabric, Forge, NeoForge, OptiFine };
}

/// <summary>
/// Fabric 安装。Fabric 不需要跑安装器：官方 meta 接口直接给出可用的 version json，
/// 落盘 + 补齐 libraries 就完成，因此这一步不依赖本机 Java。
/// </summary>
public sealed class FabricInstaller
{
    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly FeatureFlags _flags;
    private readonly VersionInstaller _versions;
    private readonly bool _useMirror;

    public FabricInstaller(HttpFetcher http, GamePaths paths, FeatureFlags flags, VersionInstaller versions,
        bool useMirror = false)
    {
        _http = http;
        _paths = paths;
        _flags = flags;
        _versions = versions;
        _useMirror = useMirror;
    }

    public async Task<List<FabricLoaderForVersion>> ListAsync(string gameVersion, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();
        var url = ModLoaderApi.FabricLoadersFor(gameVersion);
        if (_useMirror) url = ModLoaderApi.FabricMirror(url);
        return await _http.GetJsonAsync<List<FabricLoaderForVersion>>(url, null, ct).ConfigureAwait(false);
    }

    /// <summary>安装 Fabric。会先确保基版本存在（Fabric profile 靠 inheritsFrom 继承）。</summary>
    public async Task<string> InstallAsync(string gameVersion, string loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        progress?.Report(new InstallProgress("base", 0, 4, $"确认 Minecraft {gameVersion} 已安装"));
        if (!File.Exists(_paths.VersionJson(gameVersion)))
            await _versions.InstallAsync(gameVersion, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("profile", 1, 4, $"读取 Fabric {loaderVersion} 版本描述"));
        var url = ModLoaderApi.FabricProfile(gameVersion, loaderVersion);
        if (_useMirror) url = ModLoaderApi.FabricMirror(url);
        var profile = await _http.GetJsonAsync<VersionJson>(url, null, ct).ConfigureAwait(false);

        // meta 给的 id 形如 fabric-loader-0.16.0-1.20.1，保持原样，便于与官方启动器互认
        progress?.Report(new InstallProgress("json", 2, 4, "写入版本 json " + profile.Id));
        _versions.WriteVersionJson(profile);

        progress?.Report(new InstallProgress("libraries", 3, 4, "下载 Fabric 依赖库"));
        await _versions.InstallLibrariesAsync(profile, progress, ct).ConfigureAwait(false);

        // Fabric 的客户端 jar 仍属于基版本：给它补一份继承链接，启动时才找得到 jar
        await LinkClientJarAsync(profile, gameVersion, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("done", 4, 4, $"Fabric {loaderVersion} 安装完成，版本：{profile.Id}"));
        return profile.Id;
    }

    /// <summary>
    /// Fabric 的 profile 里没有 downloads.client，客户端 jar 在基版本目录下。
    /// 这里做一个硬链接/复制，让 versions/&lt;fabricId&gt;/&lt;fabricId&gt;.jar 也能被 classpath 找到。
    /// </summary>
    private async Task LinkClientJarAsync(VersionJson profile, string gameVersion, CancellationToken ct)
    {
        var target = _paths.VersionJar(profile.Id);
        if (File.Exists(target)) return;

        var sourceJar = _paths.VersionJar(gameVersion);
        Directory.CreateDirectory(_paths.VersionRoot(profile.Id));

        if (File.Exists(sourceJar))
        {
            // 用复制而不是硬链接：跨卷与部分文件系统不支持硬链接，复制在任何情况下都成立
            File.Copy(sourceJar, target, true);
            return;
        }

        // 基版本 jar 不在时，直接从清单里再取一次
        var manifest = await _http.GetJsonAsync<VersionManifest>(_versions.Source.VersionManifestUrl,
            _versions.Source, ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == gameVersion)
                    ?? throw new InvalidOperationException($"清单中找不到版本 {gameVersion}");
        var json = await _http.GetJsonAsync<VersionJson>(entry.Url, _versions.Source, ct).ConfigureAwait(false);
        if (json.Downloads.Client is { } client)
            await _http.DownloadFileAsync(_versions.Source.RewriteUrl(client.Url), target, client.Sha1, null, ct)
                .ConfigureAwait(false);
    }
}

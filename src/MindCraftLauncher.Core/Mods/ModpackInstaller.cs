using System.IO.Compression;
using System.Text.Json;
using MindCraftLauncher.Core.Clients;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Launch;
using MindCraftLauncher.Core.Versions;

namespace MindCraftLauncher.Core.Mods;

/// <summary>
/// .mrpack 整合包安装。
///
/// 流程（全部按 Modrinth 规范，实测过的结构）：下载 mrpack → 读 modrinth.index.json →
/// 确保 dependencies 里的 minecraft 与加载器装好 → 把原加载器版本复制成独立的整合包版本 →
/// 按 files[].path 下载模组 → 解压 overrides/ 与 client-overrides/ → 写 packinfo.json 标记。
///
/// CurseForge 整合包（.zip + manifest.json）需要 CurseForge API Key 才能取文件直链，
/// 本版不接，遇到时给出明确提示而不是假装安装。
/// </summary>
public sealed class ModpackInstaller
{
    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly FeatureFlags _flags;
    private readonly VersionInstaller _versions;
    private readonly FabricInstaller _fabric;
    private readonly ForgeInstaller _forge;
    private readonly VersionManager _versionManager;

    public ModpackInstaller(HttpFetcher http, GamePaths paths, FeatureFlags flags, VersionInstaller versions,
        FabricInstaller fabric, ForgeInstaller forge, VersionManager versionManager)
    {
        _http = http;
        _paths = paths;
        _flags = flags;
        _versions = versions;
        _fabric = fabric;
        _forge = forge;
        _versionManager = versionManager;
    }

    /// <summary>安装 .mrpack，返回新建的版本 id。</summary>
    public async Task<string> InstallMrpackAsync(string mrpackUrl, string? displayName,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        var tmp = Path.Combine(GameLocation.LauncherDataRoot(), "cache",
            "pack-" + Guid.NewGuid().ToString("N")[..8] + ".mrpack");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);

        progress?.Report(new InstallProgress("download", 0, 6, "下载整合包"));
        await _http.DownloadFileAsync(mrpackUrl, tmp, null, null, ct).ConfigureAwait(false);

        try
        {
            return await InstallFromFileAsync(tmp, displayName, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public async Task<string> InstallFromFileAsync(string mrpackPath, string? displayName,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        ModpackIndex index;
        List<ZipArchiveEntry> entries;
        using (var zip = ZipFile.OpenRead(mrpackPath))
        {
            var entry = zip.GetEntry("modrinth.index.json")
                        ?? throw new InvalidOperationException(
                            "这不是 Modrinth 格式（.mrpack）整合包。CurseForge 的 .zip 整合包本版未支持。");
            using var stream = entry.Open();
            index = JsonSerializer.Deserialize<ModpackIndex>(stream, JsonOpt.Read)
                    ?? throw new InvalidOperationException("modrinth.index.json 解析失败");
            entries = zip.Entries.ToList();

            var gameVersion = index.MinecraftVersion;
            if (string.IsNullOrEmpty(gameVersion))
                throw new InvalidOperationException("整合包没有声明 minecraft 依赖，无法确定游戏版本");

            // 1) 基版本
            progress?.Report(new InstallProgress("base", 1, 6, $"确认 Minecraft {gameVersion}"));
            if (!File.Exists(_paths.VersionJson(gameVersion)))
                await _versions.InstallAsync(gameVersion, progress, ct).ConfigureAwait(false);

            // 2) 加载器
            progress?.Report(new InstallProgress("loader", 2, 6, "确认整合包要求的加载器"));
            var loaderVersionId = await EnsureLoaderAsync(index, gameVersion, progress, ct).ConfigureAwait(false);

            // 3) 复制成独立的整合包版本
            var packName = displayName ?? index.Name;
            var packId = VersionManager.SanitizeName(packName) + "-" + VersionManager.SanitizeName(index.VersionId);
            progress?.Report(new InstallProgress("version", 3, 6, "创建版本 " + packId));
            CreatePackVersion(packId, loaderVersionId, index, packName);

            // 4) 下载 files[]
            var files = index.ClientFiles.ToList();
            progress?.Report(new InstallProgress("files", 4, 6, $"下载整合包内容（{files.Count} 个文件）"));
            var packDir = _paths.VersionRoot(packId);
            var done = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var url = f.Url;
                var relative = SafeRelative(f.Path);
                if (url is null || relative is null) continue;

                var target = Path.Combine(packDir, relative);
                await _http.DownloadFileAsync(url, target, f.Hashes?.Sha1, null, ct).ConfigureAwait(false);
                done++;
                progress?.Report(new InstallProgress("files", 4, 6, $"整合包内容 {done}/{files.Count}"));
            }

            // 5) overrides
            progress?.Report(new InstallProgress("overrides", 5, 6, "解压 overrides"));
            ExtractOverrides(entries, packDir);

            progress?.Report(new InstallProgress("done", 6, 6, $"整合包安装完成，版本：{packId}"));
            return packId;
        }
    }

    private async Task<string> EnsureLoaderAsync(ModpackIndex index, string gameVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (index.FabricLoader is { Length: > 0 } fabric)
            return await _fabric.InstallAsync(gameVersion, fabric, progress, ct).ConfigureAwait(false);

        if (index.ForgeVersion is { Length: > 0 } forge)
        {
            var full = forge.Contains('-') ? forge : $"{gameVersion}-{forge}";
            return await _forge.InstallAsync(gameVersion, full, progress, ct).ConfigureAwait(false);
        }

        if (index.QuiltLoader is { Length: > 0 })
            throw new InvalidOperationException("这是 Quilt 整合包，本版未支持 Quilt 加载器。可先用 HMCL 装好 Quilt 版本再手动放入模组。");

        if (index.NeoForgeVersion is { Length: > 0 })
            throw new InvalidOperationException("这是 NeoForge 整合包，本版未支持 NeoForge 安装。可先用 HMCL 装好该版本后，把整合包 mods 放进对应目录。");

        // 没有任何加载器依赖：当作原版整合包，直接把基版本当目标
        return gameVersion;
    }

    /// <summary>把加载器版本复制成独立的整合包版本（json 改 id，jar 复制一份）。</summary>
    private void CreatePackVersion(string packId, string sourceVersionId, ModpackIndex index, string packName)
    {
        var dir = _paths.VersionRoot(packId);
        Directory.CreateDirectory(dir);

        var sourceJson = VersionResolver.Resolve(_paths, sourceVersionId)
                         ?? throw new InvalidOperationException("读取加载器版本失败：" + sourceVersionId);
        sourceJson.Id = packId;

        File.WriteAllText(_paths.VersionJson(packId), JsonOpt.Serialize(sourceJson));

        var sourceJar = _paths.VersionJar(sourceVersionId);
        if (File.Exists(sourceJar))
            File.Copy(sourceJar, _paths.VersionJar(packId), true);

        var info = new Dictionary<string, string>
        {
            ["name"] = packName,
            ["version"] = index.VersionId,
            ["minecraft"] = index.MinecraftVersion,
            ["sourceVersion"] = sourceVersionId,
            ["installedAt"] = DateTime.Now.ToString("s"),
        };
        File.WriteAllText(Path.Combine(dir, "packinfo.json"), JsonOpt.Serialize(info));
    }

    /// <summary>防目录穿越：只接受包内相对路径。</summary>
    private static string? SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/').TrimStart('/');
        if (p.Contains("..", StringComparison.Ordinal)) return null;
        return p.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void ExtractOverrides(List<ZipArchiveEntry> entries, string packDir)
    {
        foreach (var entry in entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            string? rest = null;
            if (name.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                rest = name["overrides/".Length..];
            else if (name.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                rest = name["client-overrides/".Length..];

            if (rest is null || rest.Length == 0) continue;
            if (rest.EndsWith("/", StringComparison.Ordinal)) continue;      // 目录项

            var relative = SafeRelative(rest);
            if (relative is null) continue;

            var target = Path.Combine(packDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                using var src = entry.Open();
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
            }
            catch
            {
                // 单个 override 失败不阻塞整体安装
            }
        }
    }
}

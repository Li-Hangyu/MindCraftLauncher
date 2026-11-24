using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Launch;

namespace MindCraftLauncher.Core.Clients;

/// <summary>BMCLAPI 的 OptiFine 条目（GET /optifine/{mcversion} 的响应元素）。</summary>
public sealed class OptiFineEntry
{
    [JsonPropertyName("mcversion")] public string McVersion { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("patch")] public string Patch { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("forge")] public string? Forge { get; set; }

    /// <summary>列表里显示的一行。</summary>
    public string Display => $"OptiFine {Type} {Patch}" + (string.IsNullOrEmpty(Forge) ? "" : $"　（配 {Forge}）");
}

/// <summary>OptiFine 安装结果。Mode：mods（放进 Forge 版本的 mods）/ installer（跑官方安装器）。</summary>
public sealed record OptiFineInstallResult(string VersionId, string Mode, string JarPath, string Output);

/// <summary>
/// OptiFine 安装。
///
/// 下载只能走 BMCLAPI 镜像：optifine.net 的下载链接带动态 token，直连拿不到文件，
/// 所以即使下载源选了 Mojang，这一步也用镜像（2026-11-01 实测 /optifine/{mc}/{type}/{patch} 直接返回 jar）。
///
/// 安装走两条路，按目标版本实际情况自动挑：
///   1) 本机已有同版本的 Forge 版本 → 直接把 OptiFine jar 放进该版本的 mods 目录。
///      这是 Forge 官方支持的用法，最稳，不需要跑任何安装器。
///   2) 纯净版 → 用官方安装器的无头入口 java -cp &lt;jar&gt; optifine.Installer 试一次，
///      成功会在 versions 下生成带 OptiFine 的新版本；失败则把 jar 路径和手动步骤抛给用户。
///
/// 第 2 条依赖 OptiFine 安装器的内部行为，属未实测路径：沙箱里没法验证，实机请先试一次。
/// </summary>
public sealed class OptiFineInstaller
{
    /// <summary>镜像根。OptiFine 的官方站点需要动态 token，只能走镜像。</summary>
    public const string MirrorRoot = "https://bmclapi2.bangbang93.com";

    private readonly HttpFetcher _http;
    private readonly GamePaths _paths;
    private readonly FeatureFlags _flags;
    private readonly VersionInstaller _versions;
    private readonly string? _explicitJava;

    public OptiFineInstaller(HttpFetcher http, GamePaths paths, FeatureFlags flags,
        VersionInstaller versions, string? explicitJava)
    {
        _http = http;
        _paths = paths;
        _flags = flags;
        _versions = versions;
        _explicitJava = explicitJava;
    }

    public static string ListUrl(string mcVersion) =>
        $"{MirrorRoot}/optifine/{Uri.EscapeDataString(mcVersion)}";

    public static string JarUrl(string mcVersion, string type, string patch) =>
        $"{MirrorRoot}/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}";

    /// <summary>某个 MC 版本可用的 OptiFine 列表，正式版（HD_U）排前面，同类型内新版在前。</summary>
    public async Task<List<ModLoaderVersion>> ListAsync(string gameVersion, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        var entries = await _http.GetJsonAsync<List<OptiFineEntry>>(ListUrl(gameVersion), null, ct)
            .ConfigureAwait(false);

        return entries
            .OrderByDescending(e => e.Type == "HD_U")
            .ThenByDescending(e => SplitPatch(e.Patch).Letters)
            .ThenByDescending(e => SplitPatch(e.Patch).Number)
            .Select(e => new ModLoaderVersion(
                $"{gameVersion}-OptiFine-{e.Type}-{e.Patch}",
                gameVersion,
                $"{e.Type}/{e.Patch}",          // LoaderVersion 直接存成 URL 片段，安装时不再二次拼装
                ModLoaderKind.OptiFine,
                BuildNote(e)))
            .ToList();
    }

    public async Task<OptiFineInstallResult> InstallAsync(string gameVersion, string typePatch,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        _flags.EnsureGameDownload();

        var (type, patch) = SplitTypePatch(typePatch);

        if (!File.Exists(_paths.VersionJson(gameVersion)))
        {
            progress?.Report(new InstallProgress("base", 0, 4, $"确认 Minecraft {gameVersion} 已安装"));
            await _versions.InstallAsync(gameVersion, progress, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress("optifine", 1, 4, $"下载 OptiFine {type} {patch}"));
        var jarDir = Path.Combine(GameLocation.LauncherDataRoot(), "optifine");
        Directory.CreateDirectory(jarDir);
        var jarPath = Path.Combine(jarDir, $"OptiFine_{gameVersion}_{type}_{patch}.jar");
        await _http.DownloadFileAsync(JarUrl(gameVersion, type, patch), jarPath, null, null, ct).ConfigureAwait(false);

        if (!File.Exists(jarPath) || new FileInfo(jarPath).Length < 100_000)
            throw new InvalidOperationException("OptiFine 安装包下载异常（文件过小）：" + jarPath);

        // ---- 路线 1：已有同版本 Forge，直接塞进 mods ----
        var repo = new InstalledVersionRepository(_paths);
        var forgeTarget = repo.List()
            .Where(v => v.Id.StartsWith(gameVersion, StringComparison.OrdinalIgnoreCase)
                        && v.Id.Contains("forge", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.InstalledAt)
            .FirstOrDefault();

        if (forgeTarget is not null)
        {
            progress?.Report(new InstallProgress("mods", 2, 4, $"放入 {forgeTarget.Id} 的 mods 目录"));
            var mods = _paths.Mods(forgeTarget.Id, versionIsolation: true);
            Directory.CreateDirectory(mods);
            File.Copy(jarPath, Path.Combine(mods, Path.GetFileName(jarPath)), true);

            progress?.Report(new InstallProgress("done", 4, 4, $"OptiFine 已放入 {forgeTarget.Id} 的 mods"));
            return new OptiFineInstallResult(forgeTarget.Id, "mods", jarPath, "");
        }

        // ---- 路线 2：纯净版，尝试官方安装器的无头入口 ----
        progress?.Report(new InstallProgress("run", 2, 4, "尝试静默运行 OptiFine 安装器"));
        var java = JavaLocator.Find(_explicitJava, _paths.Root, ForgeInstaller.RequiredJavaMajor(gameVersion));
        var startedAt = DateTime.Now;
        var output = "";

        if (java is not null)
        {
            output = await TryRunInstallerAsync(java.ExecutablePath, jarPath, ct).ConfigureAwait(false);
            var created = FindCreatedVersion(gameVersion, startedAt);
            if (created is not null)
            {
                progress?.Report(new InstallProgress("libraries", 3, 4, "补齐 OptiFine 版本依赖库"));
                var profile = repo.Load(created);
                if (profile is not null)
                    await _versions.InstallLibrariesAsync(profile, progress, ct).ConfigureAwait(false);

                progress?.Report(new InstallProgress("done", 4, 4, $"OptiFine 安装完成，版本：{created}"));
                return new OptiFineInstallResult(created, "installer", jarPath, output);
            }
        }

        throw new InvalidOperationException(
            "OptiFine 的静默安装没有成功——它本质是图形界面安装器，无头入口不保证可用。\n\n" +
            $"安装包已下载到：\n{jarPath}\n\n" +
            $"手动完成：双击这个 jar → 安装目录填 {_paths.Root} → 点 Install → 回【启动页】刷新版本列表。\n" +
            (output.Length > 0 ? "\n安装器输出：\n" + Truncate(output, 600) : ""));
    }

    /// <summary>typePatch 形如 "HD_U/I6"。</summary>
    internal static (string Type, string Patch) SplitTypePatch(string typePatch)
    {
        var parts = (typePatch ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new ArgumentException("OptiFine 版本标识格式应为 类型/补丁号，例如 HD_U/I6，实际：" + typePatch);
        return (parts[0], parts[1]);
    }

    /// <summary>把 "I6" / "pre4" 拆成字母段与数字段，用于排序。</summary>
    internal static (string Letters, int Number) SplitPatch(string patch)
    {
        var m = Regex.Match(patch ?? "", @"^([A-Za-z]*)(\d+)$");
        if (!m.Success) return (patch ?? "", 0);
        return (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value));
    }

    private static string BuildNote(OptiFineEntry e)
    {
        var notes = new List<string>();
        if (e.Patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase)) notes.Add("预览版");
        else if (e.Type == "HD_U") notes.Add("正式版");
        if (!string.IsNullOrEmpty(e.Forge)) notes.Add("配 " + e.Forge);
        return string.Join("｜", notes);
    }

    /// <summary>跑一次 java -cp &lt;jar&gt; optifine.Installer，工作目录设为游戏根目录。</summary>
    private async Task<string> TryRunInstallerAsync(string javaExe, string jarPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                WorkingDirectory = _paths.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(jarPath);
            psi.ArgumentList.Add("optifine.Installer");

            using var p = Process.Start(psi);
            if (p is null) return "无法启动 java 进程";

            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is { } d) sb.AppendLine(d); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) sb.AppendLine(d); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "安装器调用失败：" + ex.Message;
        }
    }

    private string? FindCreatedVersion(string gameVersion, DateTime since)
    {
        if (!Directory.Exists(_paths.Versions)) return null;
        var threshold = since.AddSeconds(-10);
        return Directory.EnumerateDirectories(_paths.Versions)
            .Select(Path.GetFileName)
            .Where(n => n is not null
                        && n.StartsWith(gameVersion, StringComparison.OrdinalIgnoreCase)
                        && n.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
            .Where(n => File.Exists(_paths.VersionJson(n!))
                        && File.GetLastWriteTime(_paths.VersionJson(n!)) >= threshold)
            .OrderByDescending(n => File.GetLastWriteTime(_paths.VersionJson(n!)))
            .FirstOrDefault();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

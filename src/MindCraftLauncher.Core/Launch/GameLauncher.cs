using MindCraftLauncher.Core.Accounts;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Launch; // 假设你的 VersionJson 等模型在这个命名空间
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MindCraftLauncher.Core.Launch;

/// <summary>一次启动所需的全部输入。</summary>
public sealed record LaunchRequest(
    string VersionId,
    LauncherAccount Account,
    int MemoryMb = 4096,
    string ExtraJvmArgs = "",
    string? ServerAddress = null,
    int ServerPort = 25565,
    bool VersionIsolation = true,
    IReadOnlyList<string>? ExtraClassPath = null,
    string? AuthlibInjectorJavaAgent = null);

/// <summary>组装出来的进程启动信息，便于 UI 展示"实际执行了什么"。</summary>
public sealed record LaunchPlan(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public string CommandLine => string.Join(' ', Arguments.Prepend(Executable).Select(Quote));

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
}

/// <summary>
/// 启动层：把 版本 json + 账号 + Java + 注入参数 拼成一条 java 命令行并拉起进程。
/// </summary>
public sealed class GameLauncher
{
    private readonly GamePaths _paths;
    private readonly LauncherConfig _config;

    public GameLauncher(GamePaths paths, LauncherConfig config)
    {
        _paths = paths;
        _config = config;
    }

    public LaunchPlan BuildPlan(LaunchRequest req)
    {
        // 关键：启动前先合并 inheritsFrom，否则 Fabric/Forge/整合包版本的 classpath 不完整
        var json = VersionResolver.Resolve(_paths, req.VersionId)
                   ?? throw new InvalidOperationException($"版本 {req.VersionId} 尚未安装或 json 损坏");

        var importedRuntimes = _config.Java.DiscoveredRuntimes.Select(runtime =>
            new JavaInstall(runtime.ExecutablePath, runtime.MajorVersion, runtime.Source));
        var java = JavaLocator.Find(_config.Java.ExplicitJavaPath, _paths.Root,
            json.JavaVersion.MajorVersion, importedRuntimes)
                   ?? throw new InvalidOperationException(
                       $"未找到 Java {json.JavaVersion.MajorVersion}（版本 {req.VersionId} 需要）。" +
                       "请在【设置】里指定对应的 javaw.exe，或安装对应版本的 JDK。");

        var natives = NativesExtractor.Extract(json, _paths, RuleEvaluator.CurrentOsName);
        var classpath = ClassPathBuilder.Build(_paths, json, req.ExtraClassPath);
        var workDir = _paths.WorkingDirectory(req.VersionId, req.VersionIsolation);

        var placeholders = BuildPlaceholders(json, req, workDir, natives, _paths);

        var args = new List<string>();

        // 1) JVM 参数：优先用版本 json 的 arguments.jvm（新版），否则用固定模板（老版本）
        var jvmFromJson = ArgumentExpander.Expand(json.Arguments?.Jvm, RuleEvaluator.CurrentOsName, placeholders);
        if (jvmFromJson.Count > 0)
        {
            args.AddRange(jvmFromJson);
        }
        else
        {
            // 老版本回退逻辑
            args.Add($"-Xms{Math.Min(req.MemoryMb / 4, 1024)}M");
            args.Add($"-Xmx{req.MemoryMb}M");
            args.Add("-Djava.library.path=\"" + natives + "\"");
            args.Add("-Dminecraft.launcher.brand=MindCraftLauncher");
            args.Add("-Dminecraft.launcher.version=1.0");
        }

        // 确保内存参数存在（如果 JSON 里没写的话）
        if (!args.Any(a => a.StartsWith("-Xmx")))
            args.Add($"-Xmx{req.MemoryMb}M");

        // 用户附加 JVM 参数
        if (!string.IsNullOrWhiteSpace(req.ExtraJvmArgs))
            args.AddRange(req.ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // 外置登录注入 (Authlib-Injector)
        if (!string.IsNullOrWhiteSpace(req.AuthlibInjectorJavaAgent))
            args.Add(req.AuthlibInjectorJavaAgent!);

        // 【关键修复】---------------------------------------------------------
        // 必须在这里插入 Classpath 和 主类名！
        // 顺序：JVM参数 -> [-cp xxx] -> [MainClass] -> 游戏参数

        // 1. 检查是否已有 -cp，如果没有则添加（防止重复）
        bool hasCp = args.Any(a => a == "-cp" || a.StartsWith("-cp="));
        if (!hasCp)
        {
            args.Add("-cp");
            args.Add(classpath);
        }

        // 2. 添加主类名 (这是解决 Unrecognized option: --username 的核心)
        if (!string.IsNullOrEmpty(json.MainClass))
        {
            args.Add(json.MainClass);
        }
        else
        {
            throw new InvalidOperationException($"版本 {req.VersionId} 的 json 中缺少 mainClass 字段，无法启动。");
        }
        // --------------------------------------------------------------------

        // 3) 游戏参数
        var gameArgs = json.Arguments?.Game is { Count: > 0 }
            ? ArgumentExpander.Expand(json.Arguments.Game, RuleEvaluator.CurrentOsName, placeholders)
            : ExpandLegacy(json.MinecraftArguments, placeholders);

        foreach (var g in gameArgs)
            if (!string.IsNullOrWhiteSpace(g)) args.Add(g);

        // 4) 陶瓦 / 红石联机用的直连参数
        if (!string.IsNullOrWhiteSpace(req.ServerAddress))
        {
            if (!args.Contains("--server"))
            {
                args.Add("--server");
                args.Add(req.ServerAddress!);
            }
            if (!args.Contains("--port") && req.ServerPort > 0)
            {
                args.Add("--port");
                args.Add(req.ServerPort.ToString());
            }
        }

        return new LaunchPlan(java.ExecutablePath, args, workDir);
    }

    public Process? Start(LaunchRequest req, out LaunchPlan plan)
    {
        plan = BuildPlan(req);
        var psi = new ProcessStartInfo
        {
            FileName = plan.Executable,
            WorkingDirectory = plan.WorkingDirectory,

            // 【关键修改】彻底隐藏黑窗口的黄金搭档
            UseShellExecute = false,      // 不使用系统默认程序打开，允许重定向
            CreateNoWindow = true,        // 明确告诉系统：不要创建新窗口！

            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // 使用 ArgumentList 避免手动拼接字符串导致的转义问题
        foreach (var a in plan.Arguments)
            psi.ArgumentList.Add(a);

        var p = Process.Start(psi);
        if (p is null) throw new InvalidOperationException("无法启动 java 进程");
        return p;
    }

    /// <summary>Mojang 占位符表，键名与 arguments 里的 ${...} 对齐。</summary>
    internal static Dictionary<string, string> BuildPlaceholders(VersionJson json, LaunchRequest req,
        string gameDir, string nativesDir, GamePaths paths)
    {
        return new Dictionary<string, string>
        {
            ["auth_player_name"] = req.Account.PlayerName,
            ["version_name"] = json.Id,
            ["game_directory"] = gameDir,
            ["assets_root"] = paths.Assets,
            ["assets_index_name"] = json.AssetIndex?.Id ?? json.Assets,
            ["auth_uuid"] = string.IsNullOrEmpty(req.Account.Uuid) ? Guid.NewGuid().ToString("N") : req.Account.Uuid,
            ["auth_access_token"] = string.IsNullOrEmpty(req.Account.AccessToken) ? req.Account.Uuid : req.Account.AccessToken,
            ["clientid"] = req.Account.ClientId ?? "",
            ["auth_xuid"] = req.Account.Xuid ?? "",
            ["user_type"] = req.Account.Kind switch
            {
                AccountKind.Microsoft => "msa",
                AccountKind.Yggdrasil => "mojang",
                _ => "legacy",
            },
            ["version_type"] = string.IsNullOrEmpty(json.Type) ? "release" : json.Type,
            ["user_properties"] = "{}",
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "MindCraftLauncher",
            ["launcher_version"] = "1.0",
            ["classpath"] = ClassPathBuilder.Build(paths, json, req.ExtraClassPath),
            ["classpath_separator"] = OperatingSystem.IsWindows() ? ";" : ":",
            ["library_directory"] = paths.Libraries,
        };
    }

    /// <summary>老版本（1.12 及更早）的参数是一个空格分隔的字符串。</summary>
    internal static List<string> ExpandLegacy(string? minecraftArguments, IDictionary<string, string> placeholders)
    {
        if (string.IsNullOrWhiteSpace(minecraftArguments)) return new List<string>();
        return minecraftArguments!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => ArgumentExpander.Substitute(a, placeholders))
            .ToList();
    }
}
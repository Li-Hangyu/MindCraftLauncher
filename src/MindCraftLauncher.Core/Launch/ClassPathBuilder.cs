using System.IO.Compression;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Launch;

/// <summary>
/// classpath 组装：客户端 jar → 附加 jar（如 LabyMod）→ 版本 json 里的 libraries（按当前系统规则过滤）。
/// Windows 的路径分隔符是 ';'，其他系统 ':'。
/// </summary>
public static class ClassPathBuilder
{
    public static string Build(GamePaths paths, VersionJson json, IEnumerable<string>? extraEntries = null,
        string osName = "")
    {
        if (string.IsNullOrEmpty(osName)) osName = RuleEvaluator.CurrentOsName;
        var sep = OperatingSystem.IsWindows() ? ";" : ":";

        var entries = new List<string>();

        // 客户端 jar：优先本版本目录里的，其次（Fabric/Forge 这类 inheritsFrom 版本）用父版本的
        var ownJar = paths.VersionJar(json.Id);
        if (File.Exists(ownJar))
        {
            entries.Add(ownJar);
        }
        else if (!string.IsNullOrEmpty(json.Jar) && File.Exists(paths.VersionJar(json.Jar)))
        {
            entries.Add(paths.VersionJar(json.Jar));
        }
        else
        {
            entries.Add(ownJar);   // 都不存在也放进去，让游戏自己报"缺 jar"而不是 classpath 静默少一项
        }

        if (extraEntries is not null) entries.AddRange(extraEntries.Where(File.Exists));

        foreach (var lib in json.Libraries)
        {
            if (!RuleEvaluator.Allows(lib.Rules, osName)) continue;

            var artifact = lib.Downloads?.Artifact;
            if (artifact is not null)
            {
                var local = Path.Combine(paths.Libraries, artifact.Path ?? MavenPath.FromCoordinate(lib.Name));
                if (File.Exists(local)) entries.Add(local);
                continue;
            }

            if (lib.IsNativeOnly)
            {
                // natives 需要解压到独立目录，这里指向已解压出来的 jar（由 NativesExtractor 处理）
                continue;
            }

            var fallback = Path.Combine(paths.Libraries, MavenPath.FromCoordinate(lib.Name));
            if (File.Exists(fallback)) entries.Add(fallback);
        }

        return string.Join(sep, entries.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 把 natives jar（lwjgl-platform 等）里的 dll/so 解到版本目录下的 natives 文件夹。
/// 现在的 MC 基本都用 downloads.artifact 直接给原生库，老版本才需要这一步。
/// </summary>
public static class NativesExtractor
{
    public static string Extract(VersionJson json, GamePaths paths, string? osName = null)
    {
        osName ??= RuleEvaluator.CurrentOsName;
        var dir = Path.Combine(paths.VersionRoot(json.Id), "natives-" + osName);
        Directory.CreateDirectory(dir);

        var key = osName switch
        {
            "windows" => "windows",
            "osx" => "macos",
            _ => "linux",
        };

        foreach (var lib in json.Libraries)
        {
            if (lib.Natives is null || !lib.Natives.TryGetValue(key, out var classifierValue)) continue;
            if (!RuleEvaluator.Allows(lib.Rules, osName)) continue;

            var classifier = lib.Downloads?.Classifiers;
            if (classifier is null) continue;
            var name = classifierValue.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
            if (!classifier.TryGetValue(name, out var file)) continue;

            var local = Path.Combine(paths.Libraries, file.Path ?? MavenPath.FromCoordinate(lib.Name, name));
            if (!File.Exists(local)) continue;
            UnzipInto(local, dir);
        }
        return dir;
    }

    private static void UnzipInto(string zipPath, string targetDir)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;   // 目录项
                var outPath = Path.Combine(targetDir, entry.Name);
                entry.ExtractToFile(outPath, true);
            }
        }
        catch
        {
            // 单个 natives 包解压失败不阻塞启动，交给游戏自己报错
        }
    }
}

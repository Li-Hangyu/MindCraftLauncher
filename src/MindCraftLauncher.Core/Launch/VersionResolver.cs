using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Launch;

/// <summary>
/// 启动前把 inheritsFrom 链合并成一份完整 json。
///
/// 这是修掉的一个真实 bug：安装阶段会把 Fabric/Forge 的 profile 与父版本合并，
/// 但启动阶段直接读 version json，没有合并——于是 classpath 里缺原版 libraries、
/// 客户端 jar 也指向错误路径，Fabric/Forge 版本一启动就崩。
/// </summary>
public static class VersionResolver
{
    private const int MaxDepth = 6;

    /// <summary>读取并合并 inheritsFrom 链，返回可直接用于启动的完整 json；读不到返回 null。</summary>
    public static VersionJson? Resolve(GamePaths paths, string versionId)
    {
        var child = ReadLocal(paths, versionId);
        if (child is null) return null;

        var merged = child;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { versionId };
        var depth = 0;

        while (!string.IsNullOrEmpty(merged.InheritsFrom) && depth++ < MaxDepth)
        {
            var parentId = merged.InheritsFrom!;
            if (!seen.Add(parentId)) break;                       // 环形继承保护
            var parent = ReadLocal(paths, parentId);
            if (parent is null) break;                            // 父版本没装，保持原样让启动期报错更明确
            merged = VersionInstaller.Merge(parent, merged);
        }

        // 子版本目录里没有客户端 jar 时，classpath 要回落到父版本的 jar。
        // 注意这里不判断文件是否已存在——父版本可能还没下完，只要有 id 就能拼出路径，
        // 真正的存在性检查交给 ClassPathBuilder。
        if (string.IsNullOrEmpty(merged.Jar) && !File.Exists(paths.VersionJar(versionId))
            && !string.IsNullOrEmpty(child.InheritsFrom))
        {
            merged.Jar = child.InheritsFrom;
        }

        return merged;
    }

    private static VersionJson? ReadLocal(GamePaths paths, string versionId)
    {
        var file = paths.VersionJson(versionId);
        if (!File.Exists(file)) return null;
        try { return JsonOpt.Deserialize<VersionJson>(File.ReadAllText(file)); }
        catch { return null; }
    }
}

using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Launch;

/// <summary>已安装版本的摘要，用于版本选项卡。</summary>
public sealed record InstalledVersion(
    string Id,
    string Type,
    int JavaMajor,
    bool IsLabyMod,
    bool IsModpack,
    string? PackName,
    DateTime InstalledAt)
{
    /// <summary>卡片上的副标题：类型 / 加载器来自 id 的后缀。</summary>
    public string KindLabel
    {
        get
        {
            var parts = new List<string>();
            if (IsModpack) parts.Add("整合包");
            if (IsLabyMod) parts.Add("LabyMod");
            if (Id.Contains("fabric", StringComparison.OrdinalIgnoreCase)) parts.Add("Fabric");
            else if (Id.Contains("forge", StringComparison.OrdinalIgnoreCase)) parts.Add("Forge");
            else if (Id.Contains("OptiFine", StringComparison.OrdinalIgnoreCase)) parts.Add("OptiFine");
            if (parts.Count == 0) parts.Add(Type == "snapshot" ? "快照" : "原版");
            return string.Join(" · ", parts);
        }
    }

    public string JavaLabel => JavaMajor > 0 ? $"Java {JavaMajor}" : "Java 未知";

    public string InstalledLabel => InstalledAt.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>读取 versions 目录，等价于 PCL 的"版本列表"逻辑。</summary>
public sealed class InstalledVersionRepository
{
    private readonly GamePaths _paths;

    public InstalledVersionRepository(GamePaths paths) => _paths = paths;

    public IReadOnlyList<InstalledVersion> List()
    {
        var list = new List<InstalledVersion>();
        if (!Directory.Exists(_paths.Versions)) return list;

        foreach (var dir in Directory.EnumerateDirectories(_paths.Versions))
        {
            var id = Path.GetFileName(dir);
            var json = Path.Combine(dir, id + ".json");
            if (!File.Exists(json)) continue;

            try
            {
                var v = JsonOpt.Deserialize<VersionJson>(File.ReadAllText(json));
                if (v is null) continue;

                var (isPack, packName) = ReadPackInfo(dir);
                list.Add(new InstalledVersion(
                    id,
                    string.IsNullOrEmpty(v.Type) ? "release" : v.Type,
                    v.JavaVersion.MajorVersion,
                    id.Contains("LabyMod", StringComparison.OrdinalIgnoreCase),
                    isPack,
                    packName,
                    Directory.GetLastWriteTime(json)));
            }
            catch { /* 坏 json 跳过，不阻塞整个列表 */ }
        }

        // 整合包排在前面，其余按安装时间倒序
        return list.OrderByDescending(v => v.IsModpack)
                   .ThenByDescending(v => v.InstalledAt)
                   .ToList();
    }

    public VersionJson? Load(string versionId)
    {
        var json = _paths.VersionJson(versionId);
        if (!File.Exists(json)) return null;
        try { return JsonOpt.Deserialize<VersionJson>(File.ReadAllText(json)); }
        catch { return null; }
    }

    public bool IsInstalled(string versionId) => File.Exists(_paths.VersionJson(versionId));

    private static (bool, string?) ReadPackInfo(string dir)
    {
        var info = Path.Combine(dir, "packinfo.json");
        if (!File.Exists(info)) return (false, null);
        try
        {
            var map = JsonOpt.Deserialize<Dictionary<string, string>>(File.ReadAllText(info));
            return (true, map is not null && map.TryGetValue("name", out var n) ? n : null);
        }
        catch { return (true, null); }
    }
}
namespace MindCraftLauncher.Core.Common;

/// <summary>
/// 游戏目录结构（.minecraft）。PCL2 原生逻辑里也有同名的路径约定，移植时把这些方法换成 PCL 的实现即可，
/// 上层 UI 只认这些路径。
/// </summary>
public sealed class GamePaths
{
    public GamePaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; private set; }

    /// <summary>运行时切换游戏目录；同一批服务持有此实例，因此无需重启即可使用新目录。</summary>
    public void Configure(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        Root = Path.GetFullPath(root);
    }

    public string Versions => Dir(Root, "versions");
    public string Libraries => Dir(Root, "libraries");
    public string Assets => Dir(Root, "assets");
    public string AssetsObjects => Dir(Assets, "objects");
    public string AssetsIndexes => Dir(Assets, "indexes");
    public string Runtimes => Dir(Root, "runtimes");
    public string Logs => Dir(Root, "logs");

    public string VersionRoot(string versionId) => Dir(Versions, SafeName(versionId));
    public string VersionJar(string versionId) => Path.Combine(VersionRoot(versionId), SafeName(versionId) + ".jar");
    public string VersionJson(string versionId) => Path.Combine(VersionRoot(versionId), SafeName(versionId) + ".json");
    public string AssetIndex(string assetsId) => Path.Combine(AssetsIndexes, assetsId + ".json");

    /// <summary>版本隔离开启时 mods 放在版本目录下，否则放全局 mods。</summary>
    public string Mods(string versionId, bool versionIsolation) =>
        versionIsolation ? Dir(VersionRoot(versionId), "mods") : Dir(Root, "mods");

    /// <summary>LabyMod 4 的客户端 jar 落在版本目录，命名与 PCL 处理第三方客户端的习惯一致。</summary>
    public string LabyModJar(string versionTag) => Path.Combine(VersionRoot(versionTag), "labymod-" + SafeName(versionTag) + ".jar");

    public string WorkingDirectory(string versionId, bool versionIsolation) =>
        versionIsolation ? VersionRoot(versionId) : Root;

    private static string SafeName(string raw)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            raw = raw.Replace(c, '_');
        return raw;
    }

    private static string Dir(string parent, string name)
    {
        var full = Path.Combine(parent, name);
        try { Directory.CreateDirectory(full); } catch { /* 只读目录等场景不阻塞 */ }
        return full;
    }
}

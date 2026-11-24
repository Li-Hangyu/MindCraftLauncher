using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.Versions;

/// <summary>
/// 版本的增删查。删除只动 versions/&lt;id&gt; 目录，不碰存档与全局 mods；
/// 调用方（UI）负责二次确认。
/// </summary>
public sealed class VersionManager
{
    private readonly GamePaths _paths;

    public VersionManager(GamePaths paths) => _paths = paths;

    public bool Exists(string versionId) => File.Exists(_paths.VersionJson(versionId));

    /// <summary>删除一个版本目录。返回是否真的删掉了。</summary>
    public bool Delete(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return false;

        // 版本 id 只能是纯名字：含路径分隔符或 .. 一律拒绝。
        // 注意不要走 VersionRoot()——那个方法会顺手 CreateDirectory，等于给删除操作凭空造一个目录。
        if (versionId.Contains("..", StringComparison.Ordinal)) return false;
        if (versionId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;

        var versionsRoot = Path.GetFullPath(_paths.Versions);
        var full = Path.GetFullPath(Path.Combine(versionsRoot, versionId));

        // 防误删：必须是 versions 下的直接子目录
        if (!full.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            if (!Directory.Exists(full)) return false;   // 不存在就算没删成，别假装成功
            Directory.Delete(full, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string FolderOf(string versionId) => _paths.VersionRoot(versionId);

    /// <summary>版本目录里是否有整合包标记文件。</summary>
    public bool IsModpack(string versionId) =>
        File.Exists(Path.Combine(_paths.VersionRoot(versionId), "packinfo.json"));

    /// <summary>把名字清成可用作目录名的形式。</summary>
    public static string SanitizeName(string raw)
    {
        var s = (raw ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        s = s.Replace(' ', '-');
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-");
        s = s.Trim('-', '.');
        if (s.Length == 0) s = "pack";
        return s.Length > 60 ? s[..60] : s;
    }
}

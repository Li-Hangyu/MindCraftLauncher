namespace MindCraftLauncher.Core.Common;

/// <summary>平台路径定位。测试里可以通过环境变量覆盖，避免污染真实用户目录。</summary>
public static class GameLocation
{
    private const string AppName = "MindCraftLauncher";
    private const string EnvOverride = "MINDCRAFTLAUNCHER_DATA";
    private const string EnvGameRoot = "MINDCRAFTLAUNCHER_GAMEROOT";

    /// <summary>启动器自身的数据目录（配置、账号缓存、authlib-injector jar、房间码历史）。</summary>
    public static string LauncherDataRoot()
    {
        var over = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(over)) return over;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var p = Path.Combine(appData, AppName);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>启动器 exe 所在目录，easytier-core 等外部程序优先在这里找。</summary>
    public static string LauncherBinaryRoot()
    {
        try { return AppContext.BaseDirectory; }
        catch { return Directory.GetCurrentDirectory(); }
    }

    /// <summary>默认游戏根目录：Windows 下 %APPDATA%\.minecraft；其他系统 ~/.minecraft。</summary>
    public static string DefaultMinecraftRoot()
    {
        var over = Environment.GetEnvironmentVariable(EnvGameRoot);
        if (!string.IsNullOrWhiteSpace(over)) return over;

        if (OperatingSystem.IsWindows())
        {
            var roaming = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(roaming)) return Path.Combine(roaming, ".minecraft");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
    }

    public static bool IsWindowsLike => OperatingSystem.IsWindows();
}

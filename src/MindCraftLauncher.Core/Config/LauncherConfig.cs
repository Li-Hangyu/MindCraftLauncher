using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.Config;

/// <summary>启动器全部可持久化配置。</summary>
public sealed class LauncherConfig
{
    public string GameRoot { get; set; } = GameLocation.DefaultMinecraftRoot();

    public FeatureFlags Flags { get; set; } = FeatureFlags.Default();

    /// <summary>下载源：mojang / bmclapi。</summary>
    public string DownloadSourceId { get; set; } = "mojang";

    /// <summary>easytier-core 可执行文件路径。为空时按 LauncherSettings 里的搜索规则自动定位。</summary>
    public string? EasyTierCorePath { get; set; }

    /// <summary>easytier-cli 可执行文件路径（读取组网状态用）。</summary>
    public string? EasyTierCliPath { get; set; }

    /// <summary>陶瓦联机的公共服务器（共享节点）地址，官方默认值来自 EasyTier 文档。</summary>
    public string EasyTierPublicPeer { get; set; } = "tcp://public.easytier.cn:11010";

    /// <summary>红石联机的认证服务器（Yggdrasil）基地址，例如 https://mcskin.cn。</summary>
    public string? RedstoneAuthServer { get; set; }

    /// <summary>微软登录用的 Azure AD 应用 client_id，留空则微软登录入口不可用。</summary>
    public string? MicrosoftClientId { get; set; }

    public string LastVersionId { get; set; } = "";

    public string LastAccountId { get; set; } = "";

    public JavaOptions Java { get; set; } = new();

    /// <summary>界面主题：dark / light。</summary>
    public string Theme { get; set; } = "dark";

    public sealed class JavaRuntimeConfig
    {
        public string ExecutablePath { get; set; } = "";
        public int MajorVersion { get; set; }
        public string Source { get; set; } = "扫描导入";
    }

    public sealed class JavaOptions
    {
        public string? ExplicitJavaPath { get; set; }
        public List<JavaRuntimeConfig> DiscoveredRuntimes { get; set; } = new();
        public int MaxMemoryMb { get; set; } = 4096;
        public int MinMemoryMb { get; set; } = 1024;
        public string ExtraJvmArgs { get; set; } = "";
        public bool VersionIsolation { get; set; } = true;
    }

    public GamePaths Paths() => new(GameRoot);
}

/// <summary>配置读写。位置：%APPDATA%/MindCraftLauncher/config.json（Linux 下走 ~/.config）。</summary>
public sealed class ConfigStore
{
    public ConfigStore(string? directory = null)
    {
        Directory = directory ?? GameLocation.LauncherDataRoot();
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "config.json");

    public LauncherConfig Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
                return JsonOpt.Deserialize<LauncherConfig>(File.ReadAllText(FilePath)) ?? new LauncherConfig();
        }
        catch
        {
            // 配置文件损坏时回退默认值，同时备份坏文件，别静默丢掉用户的配置
            try { System.IO.File.Move(FilePath, FilePath + ".broken." + DateTime.Now.Ticks, true); } catch { }
        }
        return new LauncherConfig();
    }

    public void Save(LauncherConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.File.WriteAllText(FilePath, JsonOpt.Serialize(config));
    }
}

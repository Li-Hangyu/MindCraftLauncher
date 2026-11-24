using MindCraftLauncher.Core.Accounts;
using MindCraftLauncher.Core.Clients;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.EasyTier;
using MindCraftLauncher.Core.Launch;
using MindCraftLauncher.Core.Mods;
using MindCraftLauncher.Core.Terracotta;
using MindCraftLauncher.Core.Versions;
using MindCraftLauncher.Core.Yggdrasil;

namespace MindCraftLauncher.Core;

/// <summary>
/// 组合根。UI 层只拿这一个对象，所有服务在这里装配；
/// 移植 PCL 内部逻辑时，把下面对应的实例换成 PCL 的实现即可，界面代码一行都不用改。
/// </summary>
public sealed class LauncherServices : IDisposable
{
    public LauncherServices(ConfigStore store)
    {
        Store = store;
        Config = store.Load();
        Paths = Config.Paths();
        Http = new HttpFetcher();
        Sources = new DownloadSourceRegistry();

        Versions = new VersionInstaller(Http, Paths, CurrentSource, Config.Flags);
        LabyMod = new LabyModInstaller(Http, Paths, CurrentSource, Config.Flags, Versions);
        ModLoaders = new ModLoaderService(Http, Paths, Config.Flags, Versions, CurrentSource, Config.Java.ExplicitJavaPath);
        Installed = new InstalledVersionRepository(Paths);
        VersionManager = new VersionManager(Paths);
        Launcher = new GameLauncher(Paths, Config);

        var useMirror = string.Equals(CurrentSource.Id, "bmclapi", StringComparison.OrdinalIgnoreCase);
        var fabric = new FabricInstaller(Http, Paths, Config.Flags, Versions, useMirror);
        var forge = new ForgeInstaller(Http, Paths, Config.Flags, Versions, useMirror, Config.Java.ExplicitJavaPath);
        Mods = new ModInstaller(Http, Paths, Config.Flags);
        ClientPacks = new ClientPackInstaller(Http, Paths, Config.Flags);
        Modpacks = new ModpackInstaller(Http, Paths, Config.Flags, Versions, fabric, forge, VersionManager);

        EasyTier = new EasyTierRunner();
        EasyTier.Configure(Config.EasyTierCorePath, Config.EasyTierCliPath);
        Terracotta = new TerracottaSession(EasyTier, Config.Flags);

        Accounts = new AccountStore();
        Servers = new ServerListStore();
        AuthlibInjector = new AuthlibInjectorManager(Http, CurrentSource);
    }

    public ConfigStore Store { get; }
    public LauncherConfig Config { get; }
    public GamePaths Paths { get; }
    public HttpFetcher Http { get; }
    public DownloadSourceRegistry Sources { get; }

    public FeatureFlags Flags => Config.Flags;
    public IDownloadSource CurrentSource => Sources.Pick(Config.DownloadSourceId);

    public VersionInstaller Versions { get; }
    public LabyModInstaller LabyMod { get; }
    public ModLoaderService ModLoaders { get; }
    public InstalledVersionRepository Installed { get; }
    public VersionManager VersionManager { get; }
    public GameLauncher Launcher { get; }

    public ModInstaller Mods { get; }
    public ClientPackInstaller ClientPacks { get; }
    public ModpackInstaller Modpacks { get; }

    public EasyTierRunner EasyTier { get; }
    public TerracottaSession Terracotta { get; }

    public AccountStore Accounts { get; }
    public ServerListStore Servers { get; }
    public AuthlibInjectorManager AuthlibInjector { get; }

    /// <summary>最近一次配置落盘失败的原因；成功时为 null。UI 用它提示"设置没保存成功"而不是直接崩。</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>写配置。磁盘只读 / 目录被占用时不再把异常抛到 UI 线程。</summary>
    public bool SaveConfig()
    {
        try
        {
            Config.EasyTierCorePath = EasyTier.CoreExecutable is { Length: > 0 } ? EasyTier.CoreExecutable : Config.EasyTierCorePath;
            Config.EasyTierCliPath = EasyTier.CliExecutable is { Length: > 0 } ? EasyTier.CliExecutable : Config.EasyTierCliPath;
            Store.Save(Config);
            LastSaveError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
            System.Diagnostics.Debug.WriteLine("[Config] 保存失败：" + ex);
            return false;
        }
    }

    public static LauncherServices CreateDefault() => new(new ConfigStore());

    public void Dispose()
    {
        Terracotta.Dispose();
        Http.Dispose();
    }
}

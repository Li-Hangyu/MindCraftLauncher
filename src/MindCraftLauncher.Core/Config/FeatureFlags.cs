namespace MindCraftLauncher.Core.Config;

/// <summary>
/// 上游 PCL2 因合规要求在 2026-10 前后关停了游戏本体下载，代码里对应的是一个"下载是否可用"的判断。
/// 移植版（Mind Craft Launcher）不把这个判断散落在各处，而是集中成下面这一个开关：
///
///   <c>GameDownloadEnabled</c>  ← 就是你要改的"那一行"
///
/// 默认 false，且打开时必须记录一次用户确认（<c>DownloadAcknowledgedAt</c>），否则调用方会拿到
/// <see cref="FeatureDisabledException"/>。想永久默认开启，只改 <see cref="Default"/> 里这一行即可。
/// </summary>
public sealed class FeatureFlags
{
    /// <summary>游戏本体（版本 jar / libraries / assets）下载总开关。这一行就是开关本身。</summary>
    public bool GameDownloadEnabled { get; set; }

    /// <summary>合规确认时间戳。为 null 表示用户还没在设置页勾选过"我已知悉来源与用途"。</summary>
    public DateTime? DownloadAcknowledgedAt { get; set; }

    /// <summary>第三方客户端（LabyMod）下载开关，独立于本体下载。</summary>
    public bool LabyModDownloadEnabled { get; set; } = true;

    /// <summary>陶瓦联机（EasyTier 组网）总开关。关闭时不会 spawn easytier-core 进程。</summary>
    public bool TerracottaLinkEnabled { get; set; } = true;

    /// <summary>红石联机（Yggdrasil 外置登录 + 服务器列表）总开关。</summary>
    public bool RedstoneLinkEnabled { get; set; } = true;

    public static FeatureFlags Default() => new()
    {
        GameDownloadEnabled = false,   // ← 要默认放开，把这里改成 true
        LabyModDownloadEnabled = true,
        TerracottaLinkEnabled = true,
        RedstoneLinkEnabled = true,
    };

    /// <summary>校验本体下载是否处于"可用且已被用户确认"的状态。</summary>
    public void EnsureGameDownload()
    {
        if (!GameDownloadEnabled)
            throw new FeatureDisabledException(
                "游戏本体下载功能处于关闭状态。需要在【设置】页打开开关并确认来源说明后才能使用。");

        if (DownloadAcknowledgedAt is null)
            throw new FeatureDisabledException(
                "首次使用本体下载需要先确认：文件仅从 Mojang 官方分发端点或其公开镜像获取，游戏仍需有效账号才能进入。");
    }

    public void EnsureAllowed(bool enabled, string featureName)
    {
        if (!enabled)
            throw new FeatureDisabledException($"{featureName} 功能当前处于关闭状态，可在【设置】页开启。");
    }
}

/// <summary>功能开关未打开时抛出，UI 层捕获后引导用户去设置页。</summary>
public class FeatureDisabledException : Exception
{
    public FeatureDisabledException(string message) : base(message) { }
}

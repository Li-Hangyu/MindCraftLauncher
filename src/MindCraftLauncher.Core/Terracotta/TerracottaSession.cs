using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.EasyTier;

namespace MindCraftLauncher.Core.Terracotta;

public enum TerracottaRole
{
    /// <summary>本机开房：需要游戏里"对局域网开放"的端口，并把监听器暴露给队友。</summary>
    Host,

    /// <summary>本机加入别人的房间：入网后直接连房主的虚拟 IP:端口。</summary>
    Joiner,
}

/// <summary>一次陶瓦联机会话的可视状态。</summary>
public sealed record TerracottaState(
    bool Running,
    TerracottaRole Role,
    string VirtualIpv4,
    int LanPort,
    string ConnectionAddress,
    IReadOnlyList<EasyTierPeer> Peers,
    string RoomCode,
    string Log);

/// <summary>
/// 陶瓦联机会话：EasyTier 组网 + 把游戏局域网端口暴露给队友。
/// 与 HMCL/PCL CE 的陶瓦联机同构——都基于 EasyTier，区别是本类把"连接地址"算出来交给 UI 一键复制。
/// </summary>
public sealed class TerracottaSession : IDisposable
{
    /// <summary>Minecraft "对局域网开放"默认端口范围的经验下限，仅用于输入合理性提示。</summary>
    public const int MinecraftLanPortHint = 23260;

    private readonly EasyTierRunner _runner;
    private readonly FeatureFlags _flags;
    private TerracottaRole _role = TerracottaRole.Host;
    private int _lanPort;
    private string _roomCode = "";

    public TerracottaSession(EasyTierRunner runner, FeatureFlags flags)
    {
        _runner = runner;
        _flags = flags;
    }

    public bool IsRunning => _runner.IsRunning;

    public event Action? StateChanged
    {
        add => _runner.LogReceived += _ => value?.Invoke();
        remove => _runner.LogReceived -= _ => value?.Invoke();
    }

    /// <summary>房主开房。lanPort 来自游戏内"创建一个局域网世界"后聊天框提示的端口号。</summary>
    public TerracottaState StartHost(string networkName, string networkSecret, int lanPort,
        string publicPeer, string? listener = null)
    {
        _flags.EnsureAllowed(_flags.TerracottaLinkEnabled, "陶瓦联机");
        EnsureLanPort(lanPort);

        _role = TerracottaRole.Host;
        _lanPort = lanPort;

        var profile = new EasyTierProfile
        {
            NetworkName = networkName,
            NetworkSecret = networkSecret,
            Dhcp = true,
            Hostname = Environment.MachineName,
            Peers = string.IsNullOrWhiteSpace(publicPeer) ? new List<string>() : new List<string> { publicPeer },
            // 房主必须监听，否则队友打不进 P2P 只能走中继
            Listeners = new List<string> { listener ?? DefaultListener(lanPort) },
        };

        _runner.Configure(_runner.CoreExecutable, _runner.CliExecutable);
        _runner.Start(profile);
        _roomCode = RoomCode.Encode(profile, lanPort);
        return new TerracottaState(true, _role, "", lanPort, "", Array.Empty<EasyTierPeer>(), _roomCode, _runner.LastOutput);
    }

    /// <summary>加入方：粘贴房主连接码入网。</summary>
    public TerracottaState Join(string connectionCode, string fallbackPublicPeer)
    {
        _flags.EnsureAllowed(_flags.TerracottaLinkEnabled, "陶瓦联机");

        var payload = RoomCode.Decode(connectionCode);
        _role = TerracottaRole.Joiner;
        _lanPort = payload.LanPort;
        _roomCode = connectionCode.Trim();

        var profile = RoomCode.ToProfile(payload, fallbackPublicPeer);
        _runner.Configure(_runner.CoreExecutable, _runner.CliExecutable);
        _runner.Start(profile);

        return new TerracottaState(true, _role, "", payload.LanPort, "", Array.Empty<EasyTierPeer>(), _roomCode, _runner.LastOutput);
    }

    /// <summary>读取组网状态，算出可以直接粘进"多人游戏-直接连接"的地址。</summary>
    public async Task<TerracottaState> RefreshAsync(CancellationToken ct = default)
    {
        var status = await _runner.QueryStatusAsync(ct).ConfigureAwait(false);

        var virtualIp = !string.IsNullOrEmpty(status.LocalIpv4)
            ? status.LocalIpv4
            : ExtractSelfIp(status.RawOutput);

        var address = BuildAddress(status, virtualIp);

        return new TerracottaState(_runner.IsRunning, _role, virtualIp, _lanPort, address,
            status.Peers, _roomCode, _runner.LastOutput);
    }

    /// <summary>
    /// 连接地址规则：
    /// 房主看到的是"自己的虚拟IP:局域网端口"（发给队友用）；
    /// 加入方看到的是"房主的虚拟IP:端口"（粘进多人游戏用）。
    /// </summary>
    internal string BuildAddress(EasyTierStatus status, string selfIpv4)
    {
        if (_lanPort <= 0) return "";

        if (_role == TerracottaRole.Host)
            return string.IsNullOrEmpty(selfIpv4) ? "" : $"{selfIpv4}:{_lanPort}";

        // 加入方：找一个非本机的、已连通的 peer，取它的 ipv4
        var peer = status.Peers.FirstOrDefault(p => !p.IsSelf && LooksLikeIpv4(p.Ipv4));
        return peer is null ? "" : $"{peer.Ipv4}:{_lanPort}";
    }

    public void Stop() => _runner.Stop();

    private static string DefaultListener(int lanPort) => "udp://0.0.0.0:11010";

    private static void EnsureLanPort(int lanPort)
    {
        if (lanPort is > 0 and < 65536) return;
        throw new ArgumentException($"端口号不正确（{lanPort}）。请在游戏中「对局域网开放」，把聊天框提示的 5 位端口号填进来。");
    }

    private static string ExtractSelfIp(string raw)
    {
        // 从 easytier-cli node 输出里找形如 10.x.x.x 的第一个地址
        foreach (var token in raw.Split(new[] { ' ', '\t', '\n', '\r', '|', ':' }, StringSplitOptions.RemoveEmptyEntries))
            if (LooksLikeIpv4(token)) return token;
        return "";
    }

    internal static bool LooksLikeIpv4(string s) => Ipv4.IsValid(s);

    public void Dispose() => _runner.Stop();
}

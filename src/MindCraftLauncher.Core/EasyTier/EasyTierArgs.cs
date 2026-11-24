using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.EasyTier;

/// <summary>
/// 一次组网的参数。字段与 easytier-core 的命令行一一对应（参数名来自 EasyTier 官方文档）：
/// --network-name / --network-secret / -i(=--ipv4) / -d(=--dhcp) / -p(=--peers) / -l(=--listeners) / --hostname。
/// </summary>
public sealed class EasyTierProfile
{
    public string NetworkName { get; set; } = "";
    public string NetworkSecret { get; set; } = "";

    /// <summary>true 时用 -d 让 EasyTier 自动分配虚拟 IP；false 时用 Ipv4 指定。</summary>
    public bool Dhcp { get; set; } = true;

    public string? Ipv4 { get; set; }

    /// <summary>要连接的对等节点（共享节点或房主），形如 tcp://public.easytier.cn:11010。</summary>
    public List<string> Peers { get; set; } = new();

    /// <summary>本节点监听器，留空则用 EasyTier 默认的 11010。房主需要显式监听才能被连入。</summary>
    public List<string> Listeners { get; set; } = new();

    public string? Hostname { get; set; }

    /// <summary>禁用加密一般不建议，除非两端一致设置。</summary>
    public bool DisableEncryption { get; set; }

    public string VirtualIpv4 { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(NetworkName))
            throw new ArgumentException("网络名称不能为空（--network-name）");
        if (NetworkName.Any(char.IsWhiteSpace))
            throw new ArgumentException("网络名称不能包含空格");
        if (string.IsNullOrWhiteSpace(NetworkSecret))
            throw new ArgumentException("网络密钥不能为空（--network-secret）");
        if (!Dhcp && string.IsNullOrWhiteSpace(Ipv4))
            throw new ArgumentException("未启用 DHCP 时必须指定虚拟 IPv4");
        if (!Dhcp && !Core.Common.Ipv4.IsValid(Ipv4))
            throw new ArgumentException("虚拟 IPv4 格式不正确：" + Ipv4);
    }
}

/// <summary>把 profile 变成进程参数。独立成纯函数便于单测覆盖。</summary>
public static class EasyTierArgs
{
    public static List<string> Build(EasyTierProfile p, bool forWindowsShellEscaping = false)
    {
        p.Validate();
        var args = new List<string>
        {
            "--network-name", p.NetworkName,
            "--network-secret", p.NetworkSecret,
        };

        if (p.Dhcp) args.Add("-d");
        else { args.Add("-i"); args.Add(p.Ipv4!); }

        foreach (var peer in p.Peers.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("-p");
            args.Add(peer.Trim());
        }

        foreach (var listener in p.Listeners.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("-l");
            args.Add(listener.Trim());
        }

        if (!string.IsNullOrWhiteSpace(p.Hostname))
        {
            args.Add("--hostname");
            args.Add(p.Hostname);
        }

        if (p.DisableEncryption) args.Add("--disable-encryption");

        return args;
    }

    /// <summary>拼出便于展示 / 排障的一行命令。</summary>
    public static string ToCommandLine(string executable, EasyTierProfile p)
    {
        var parts = new List<string> { Quote(executable) };
        parts.AddRange(Build(p).Select(Quote));
        return string.Join(' ', parts);
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
}

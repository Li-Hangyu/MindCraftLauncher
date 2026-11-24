namespace MindCraftLauncher.Core.EasyTier;

/// <summary>easytier-cli peer 的一行。</summary>
public sealed record EasyTierPeer(string Ipv4, string Hostname, string Cost, string LatencyMs,
    string LossRate, string RxBytes, string TxBytes, string TunnelProto, string NatType, string PeerId)
{
    /// <summary>cost 为 Local 表示这是本机自己。</summary>
    public bool IsSelf => string.Equals(Cost, "Local", StringComparison.OrdinalIgnoreCase);

    /// <summary>p2p 直连优先于 relay 中转。</summary>
    public bool IsDirect => !string.IsNullOrEmpty(Cost) && !IsSelf &&
                            !Cost.Contains("relay", StringComparison.OrdinalIgnoreCase);
}

/// <summary>组网状态快照。</summary>
public sealed record EasyTierStatus(bool Running, IReadOnlyList<EasyTierPeer> Peers, string LocalIpv4, string RawOutput);

/// <summary>
/// 解析 easytier-cli peer 的输出。官方输出是竖线分隔的表格：
/// | ipv4 | hostname | cost | lat_ms | loss_rate | rx_bytes | tx_bytes | tunnel_proto | nat_type | id | version |
/// 这里按表头名字取列，版本换列顺序也不受影响；解析不出来时把原文交给 UI 显示。
/// </summary>
public static class PeerTableParser
{
    public static EasyTierStatus Parse(string output, bool running)
    {
        var peers = new List<EasyTierPeer>();
        if (string.IsNullOrWhiteSpace(output))
            return new EasyTierStatus(running, peers, "", output ?? "");

        var rows = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('|') && l.EndsWith('|'))
            .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();

        if (rows.Count == 0)
            return new EasyTierStatus(running, peers, "", output);

        var header = rows[0].Select(h => h.ToLowerInvariant()).ToArray();
        int Col(params string[] names) => Array.FindIndex(header, h => names.Any(n => h.StartsWith(n)));

        var iIp = Col("ipv4");
        var iHost = Col("hostname");
        var iCost = Col("cost");
        var iLat = Col("lat_ms", "lat");
        var iLoss = Col("loss_rate", "loss");
        var iRx = Col("rx_bytes", "rx");
        var iTx = Col("tx_bytes", "tx");
        var iTunnel = Col("tunnel_proto", "tunnel");
        var iNat = Col("nat_type", "nat");
        var iId = Col("id");

        foreach (var cells in rows.Skip(1))
        {
            if (cells.All(string.IsNullOrEmpty)) continue;
            if (cells[0].All(c => c is '-' or ' ' or ':')) continue;   // 分隔行

            string Get(int idx) => idx >= 0 && idx < cells.Length ? cells[idx] : "";
            var peer = new EasyTierPeer(Get(iIp), Get(iHost), Get(iCost), Get(iLat), Get(iLoss),
                Get(iRx), Get(iTx), Get(iTunnel), Get(iNat), Get(iId));

            if (string.IsNullOrEmpty(peer.Ipv4) && string.IsNullOrEmpty(peer.PeerId)) continue;
            peers.Add(peer);
        }

        var local = peers.FirstOrDefault(p => p.IsSelf)?.Ipv4 ?? "";
        return new EasyTierStatus(running, peers, local, output);
    }
}

namespace MindCraftLauncher.Core.Common;

/// <summary>IPv4 字面量校验。EasyTier 的 --ipv4 与陶瓦连接码里的地址都用这一份判定。</summary>
public static class Ipv4
{
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length == 0 || p.Length > 3) return false;
            if (!int.TryParse(p, out var n) || n < 0 || n > 255) return false;
            if (p.Length > 1 && p[0] == '0') return false;   // 拒绝 01.2.3.4 这类写法
        }
        return true;
    }

    /// <summary>"10.1.2.3:25565" → ("10.1.2.3", 25565)；端口缺失返回 0。</summary>
    public static bool TryParseAddress(string? text, out string host, out int port)
    {
        host = ""; port = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var idx = s.LastIndexOf(':');
        if (idx > 0 && int.TryParse(s[(idx + 1)..], out var parsed) && parsed is > 0 and < 65536)
        {
            host = s[..idx];
            port = parsed;
        }
        else host = s;
        return IsValid(host);
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MindCraftLauncher.Core.EasyTier;

namespace MindCraftLauncher.Core.Terracotta;

/// <summary>房间码里承载的组网信息。这些字段就是加入同一个虚拟局域网所需的全部参数。</summary>
public sealed class RoomPayload
{
    [JsonPropertyName("n")] public string NetworkName { get; set; } = "";
    [JsonPropertyName("s")] public string NetworkSecret { get; set; } = "";
    [JsonPropertyName("p")] public List<string> Peers { get; set; } = new();
    [JsonPropertyName("i")] public string? Ipv4 { get; set; }
    [JsonPropertyName("d")] public bool Dhcp { get; set; } = true;
    [JsonPropertyName("l")] public int LanPort { get; set; }
}

/// <summary>
/// 陶瓦联机的"连接码"：把组网参数压成一段可复制的文本，好友粘贴即可入网。
/// 格式 <c>MCL1.&lt;base64url(json)&gt;</c>。
/// 注意：连接码里含网络密钥，等同于房间密码——UI 上要提示"发给谁谁就能进你的网"。
/// </summary>
public static class RoomCode
{
    public const string Prefix = "MCL1.";

    public static string Encode(RoomPayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        return Prefix + ToBase64Url(Convert.ToBase64String(json));
    }

    public static string Encode(EasyTierProfile profile, int lanPort) => Encode(new RoomPayload
    {
        NetworkName = profile.NetworkName,
        NetworkSecret = profile.NetworkSecret,
        Peers = profile.Peers.ToList(),
        Ipv4 = profile.Dhcp ? null : profile.Ipv4,
        Dhcp = profile.Dhcp,
        LanPort = lanPort,
    });

    public static RoomPayload Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new FormatException("连接码为空");
        var trimmed = code.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("连接码格式不正确，应以 " + Prefix + " 开头");

        var b64 = FromBase64Url(trimmed[Prefix.Length..].Trim());
        var json = Convert.FromBase64String(b64);
        var payload = JsonSerializer.Deserialize<RoomPayload>(json);
        if (payload is null || string.IsNullOrWhiteSpace(payload.NetworkName))
            throw new FormatException("连接码内容不完整，缺少网络名称");
        return payload;
    }

    public static bool TryDecode(string code, out RoomPayload? payload, out string? error)
    {
        try
        {
            payload = Decode(code);
            error = null;
            return payload.NetworkName.Length > 0;
        }
        catch (Exception ex)
        {
            payload = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把连接码转回可直接使用的组网参数。</summary>
    public static EasyTierProfile ToProfile(RoomPayload payload, string fallbackPeer)
    {
        var peers = payload.Peers.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (peers.Count == 0 && !string.IsNullOrWhiteSpace(fallbackPeer)) peers.Add(fallbackPeer);

        return new EasyTierProfile
        {
            NetworkName = payload.NetworkName,
            NetworkSecret = payload.NetworkSecret,
            Peers = peers,
            Dhcp = payload.Dhcp || string.IsNullOrWhiteSpace(payload.Ipv4),
            Ipv4 = payload.Ipv4,
            Hostname = Environment.MachineName,
        };
    }

    private static string ToBase64Url(string standard) =>
        standard.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FromBase64Url(string url) =>
        url.Replace('-', '+').Replace('_', '/').PadRight((url.Length + 3) / 4 * 4, '=');
}

using System.Text.Json.Serialization;
using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.Yggdrasil;

/// <summary>服务器列表条目。</summary>
public sealed class ServerEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 25565;
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("lastConnectedAt")] public DateTime? LastConnectedAt { get; set; }

    [JsonIgnore]
    public string HostPort
    {
        get
        {
            var addr = (Address ?? "").Trim();
            // 允许 "ip:port" 与 "ip" + 端口字段两种写法共存
            if (addr.Contains(':') && !addr.StartsWith('['))
            {
                var parts = addr.Split(':', 2);
                return $"{parts[0]}:{parts[1]}";
            }
            return $"{addr}:{Port}";
        }
    }
}

/// <summary>
/// 红石联机的服务器列表。
/// 说明：Minecraft 自己的 servers.dat 是 NBT 格式（gzip 压缩的二进制），本启动器不去改写它，
/// 而是在启动参数里用 --server/--port 直接连入，效果等价且不会破坏玩家手动维护的列表。
/// </summary>
public sealed class ServerListStore
{
    private readonly string _filePath;
    private readonly List<ServerEntry> _items;

    public ServerListStore(string? directory = null)
    {
        var dir = directory ?? GameLocation.LauncherDataRoot();
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "servers.json");
        _items = Read();
    }

    public IReadOnlyList<ServerEntry> All => _items;

    public ServerEntry Add(ServerEntry entry)
    {
        _items.Add(entry);
        Write();
        return entry;
    }

    public bool Remove(string id)
    {
        var changed = _items.RemoveAll(e => e.Id == id) > 0;
        if (changed) Write();
        return changed;
    }

    public void Touch(string id)
    {
        var e = _items.FirstOrDefault(x => x.Id == id);
        if (e is null) return;
        e.LastConnectedAt = DateTime.Now;
        Write();
    }

    /// <summary>解析 "192.168.1.5:51917" / "play.example.com" / 陶瓦连接码地址。</summary>
    public static ServerEntry Parse(string name, string hostPort, string note = "")
    {
        var s = (hostPort ?? "").Trim();
        if (s.Length == 0) throw new ArgumentException("服务器地址不能为空");

        var idx = s.LastIndexOf(':');
        var bracketEnd = s.LastIndexOf(']');
        string host;
        int port;

        if (idx > bracketEnd && idx > 0 && int.TryParse(s[(idx + 1)..], out var parsed) && parsed is > 0 and < 65536)
        {
            host = s[..idx];
            port = parsed;
        }
        else
        {
            host = s;
            port = 25565;
        }

        return new ServerEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? host : name.Trim(),
            Address = host,
            Port = port,
            Note = note ?? "",
        };
    }

    private List<ServerEntry> Read()
    {
        try
        {
            if (File.Exists(_filePath))
                return JsonOpt.Deserialize<List<ServerEntry>>(File.ReadAllText(_filePath)) ?? new();
        }
        catch { }
        return new();
    }

    private void Write() => File.WriteAllText(_filePath, JsonOpt.Serialize(_items));
}

using System.Text.Json.Serialization;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Yggdrasil;

namespace MindCraftLauncher.Core.Accounts;

public enum AccountKind
{
    /// <summary>离线模式：本地造 UUID，不联网认证。</summary>
    Offline,

    /// <summary>红石联机：Yggdrasil 外置登录（皮肤站）。</summary>
    Yggdrasil,

    /// <summary>微软正版登录。</summary>
    Microsoft,
}

/// <summary>一个可启动的游戏账号。</summary>
public sealed class LauncherAccount
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("kind")] public AccountKind Kind { get; set; } = AccountKind.Offline;
    [JsonPropertyName("playerName")] public string PlayerName { get; set; } = "";
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("authServerUrl")] public string? AuthServerUrl { get; set; }
    [JsonPropertyName("userName")] public string? UserName { get; set; }
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("clientToken")] public string? ClientToken { get; set; }
    [JsonPropertyName("lastUsedAt")] public DateTime? LastUsedAt { get; set; }
    [JsonPropertyName("xuid")] public string? Xuid { get; set; }
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }

    [JsonIgnore] public bool IsYggdrasil => Kind == AccountKind.Yggdrasil;
    [JsonIgnore] public bool NeedsOnlineAuth => Kind != AccountKind.Offline;

    [JsonIgnore] public string DisplayName =>
        Kind switch
        {
            AccountKind.Yggdrasil => PlayerName + "（红石外置登录）",
            AccountKind.Microsoft => PlayerName + "（微软）",
            _ => PlayerName + "（离线）",
        };

    public static LauncherAccount Offline(string playerName) => new()
    {
        Kind = AccountKind.Offline,
        PlayerName = playerName,
        Uuid = Crypto.OfflinePlayerUuid(playerName).ToString("N"),
    };

    public static LauncherAccount FromYggdrasil(string authServerUrl, YggdrasilSession session, string userName) => new()
    {
        Kind = AccountKind.Yggdrasil,
        AuthServerUrl = JavaAgentArgument.NormalizeUrl(authServerUrl),
        UserName = userName,
        PlayerName = session.PlayerName,
        Uuid = session.UuidDashless,
        AccessToken = session.AccessToken,
        ClientToken = session.ClientToken,
    };
}

/// <summary>
/// 账号仓库。accounts.json 落在启动器数据目录。
/// 提醒：accessToken 是明文存盘的（跟 PCL / HMCL 一样），换电脑或共享机器时记得删掉账号，
/// 红石外置登录的 token 泄漏等于别人能冒用你的角色名。
/// </summary>
public sealed class AccountStore
{
    private readonly string _filePath;
    private readonly object _sync = new();
    private List<LauncherAccount> _accounts;

    public AccountStore(string? directory = null)
    {
        var dir = directory ?? GameLocation.LauncherDataRoot();
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "accounts.json");
        _accounts = Read();
    }

    public IReadOnlyList<LauncherAccount> All
    {
        get { lock (_sync) return _accounts.ToList(); }
    }

    public LauncherAccount? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_sync) return _accounts.FirstOrDefault(a => a.Id == id);
    }

    public LauncherAccount Add(LauncherAccount account)
    {
        lock (_sync)
    {
            var existing = _accounts.FirstOrDefault(a => a.Kind == account.Kind
                && a.PlayerName == account.PlayerName
                && a.AuthServerUrl == account.AuthServerUrl);
            if (existing is not null) _accounts.Remove(existing);
            _accounts.Add(account);
            Write();
        }
        return account;
    }

    public void Update(LauncherAccount account)
    {
        lock (_sync)
        {
            var idx = _accounts.FindIndex(a => a.Id == account.Id);
            if (idx < 0) _accounts.Add(account);
            else _accounts[idx] = account;
            Write();
        }
    }

    public bool Remove(string id)
    {
        lock (_sync)
        {
            var changed = _accounts.RemoveAll(a => a.Id == id) > 0;
            if (changed) Write();
            return changed;
        }
    }

    private List<LauncherAccount> Read()
    {
        try
        {
            if (File.Exists(_filePath))
                return JsonOpt.Deserialize<List<LauncherAccount>>(File.ReadAllText(_filePath)) ?? new();
        }
        catch { /* 坏文件当作没有，Save 时会覆盖 */ }
        return new();
    }

    private void Write() => File.WriteAllText(_filePath, JsonOpt.Serialize(_accounts));
}

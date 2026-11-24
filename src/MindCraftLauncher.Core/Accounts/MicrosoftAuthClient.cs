using System.Text.Json.Serialization;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Accounts;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, string VerificationUriComplete,
    string DeviceCode, int ExpiresIn, int Interval, string Message);

public sealed record MsTokenResult(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// 微软正版登录（设备码流程）。端点是微软公开的 OAuth2 v2 接口，需要用户自己在 Azure AD 建一个
/// "桌面应用程序"拿到 client_id 填进设置页——这一点不做的情况下，正版登录入口会给出明确提示而不是静默失败。
/// 换到 Minecraft 访问令牌要走 4 跳：MS token → XBL → XSTS → Minecraft Services。
/// 本类的 JSON 解析部分有单测覆盖，真实账号链路需要在 Windows 上由使用者自己验证。
/// </summary>
public sealed class MicrosoftAuthClient
{
    public const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    public const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    public const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    public const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    public const string MinecraftAuthUrl = "https://api.minecraftservices.com/authentication/login_with_xp";
    public const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    public const string MinecraftEntitlementUrl = "https://api.minecraftservices.com/entitlements/minecraft";

    private const string Scope = "XboxLive.signin offline_access";
    private const string XstsRelyingParty = "https://auth.minecraft.net";
    private const string XblVersion = "1.4.251.0";
    private const string XstsVersion = "1.4.251.0";
    private const string MinecraftXpVersion = "2019-12-01";

    private readonly HttpFetcher _http;
    private readonly string _clientId;

    public MicrosoftAuthClient(HttpFetcher http, string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "未配置微软登录的 client_id。请在 Azure AD 注册一个桌面应用，把应用程序(客户端)ID 填到【设置】页。");
        _http = http;
        _clientId = clientId;
    }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostFormAsync(DeviceCodeUrl,
            new Dictionary<string, string> { ["client_id"] = _clientId, ["scope"] = Scope }, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("设备码请求失败：" + body);
        var r = JsonOpt.Deserialize<DeviceCodeResponse>(body)!;
        return new DeviceCodeInfo(r.UserCode, r.VerificationUri, r.VerificationUriComplete, r.DeviceCode,
            r.ExpiresIn, r.Interval, r.Message);
    }

    public async Task<MsTokenResult> PollTokenAsync(string deviceCode, CancellationToken ct = default)
    {
        using var resp = await _http.PostFormAsync(TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = _clientId,
            ["device_code"] = deviceCode,
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = JsonOpt.Deserialize<ErrorReponse>(body);
            // authorization_pending 表示用户还没在浏览器里确认，属于正常等待
            throw new DeviceFlowPendingException(err?.Error ?? "authorization_pending", err?.Description ?? "");
        }

        var t = JsonOpt.Deserialize<TokenResponse>(body)!;
        return new MsTokenResult(t.AccessToken, t.RefreshToken, t.ExpiresIn);
    }

    public async Task<string> ExchangeToXblTicketAsync(string msAccessToken, CancellationToken ct = default)
    {
        var payload = new
        {
            Properties = new Dictionary<string, object>
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = msAccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };
        var json = await PostJsonAndReadAsync(XblUrl, payload, ct).ConfigureAwait(false);
        return ExtractTokenAndUserHash(json).Token;
    }

    public async Task<string> ExchangeToXstsTicketAsync(string xblToken, CancellationToken ct = default)
    {
        var payload = new
        {
            Properties = new Dictionary<string, object>
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new[] { xblToken },
            },
            RelyingParty = XstsRelyingParty,
            TokenType = "JWT",
        };
        var json = await PostJsonAndReadAsync(XstsUrl, payload, ct).ConfigureAwait(false);
        return ExtractTokenAndUserHash(json).Token;
    }

    public async Task<string> LoginToMinecraftAsync(string xstsToken, string userHash, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["identityToken"] = $"XBL3.0 x={userHash};{xstsToken}",
            ["xpVersion"] = MinecraftXpVersion,
        };
        var json = await PostJsonAndReadAsync(MinecraftAuthUrl, payload, ct).ConfigureAwait(false);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString() ?? "";
    }

    /// <summary>取游戏档案（名字 + uuid）。没买 Minecraft 会返回 400/403。</summary>
    public async Task<(string Name, string Uuid)> GetProfileAsync(string minecraftAccessToken, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + minecraftAccessToken);
        using var resp = await _http.Client.SendAsync(msg, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("获取 Minecraft 档案失败（账号需已购买 Java 版）：" + body);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString() ?? "";
        var name = doc.RootElement.GetProperty("name").GetString() ?? "";
        return (name, id.Replace("-", ""));
    }

    public async Task<bool> HasMinecraftAsync(string minecraftAccessToken, CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, MinecraftEntitlementUrl);
        msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + minecraftAccessToken);
        using var resp = await _http.Client.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Contains("product_minecraft", StringComparison.OrdinalIgnoreCase)
               || body.Contains("game_minecraft", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>完整链路，返回可直接入库的账号。</summary>
    public async Task<LauncherAccount> FullLoginAsync(string deviceCode, CancellationToken ct = default)
    {
        var ms = await PollTokenAsync(deviceCode, ct).ConfigureAwait(false);
        var xbl = await ExchangeToXblTicketAsync(ms.AccessToken, ct).ConfigureAwait(false);
        var (xsts, userHash) = await ExchangeToXstsAsync(xbl, ct).ConfigureAwait(false);
        var mcs = await LoginToMinecraftAsync(xsts, userHash, ct).ConfigureAwait(false);
        var (name, uuid) = await GetProfileAsync(mcs, ct).ConfigureAwait(false);

        return new LauncherAccount
        {
            Kind = AccountKind.Microsoft,
            PlayerName = name,
            Uuid = uuid,
            AccessToken = mcs,
            ClientId = _clientId,
            LastUsedAt = DateTime.Now,
        };
    }

    private async Task<(string Token, string UserHash)> ExchangeToXstsAsync(string xblToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new Dictionary<string, object>
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new[] { xblToken },
            },
            RelyingParty = XstsRelyingParty,
            TokenType = "JWT",
        };
        var json = await PostJsonAndReadAsync(XstsUrl, payload, ct).ConfigureAwait(false);
        var (token, hash) = ExtractTokenAndUserHash(json);
        return (token, hash);
    }

    private async Task<string> PostJsonAndReadAsync(string url, object payload, CancellationToken ct)
    {
        using var resp = await _http.PostJsonAsync(url, payload, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{url} 返回 {(int)resp.StatusCode}：{body}");
        return body;
    }

    internal static (string Token, string UserHash) ExtractTokenAndUserHash(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var token = root.TryGetProperty("Token", out var t) ? (t.GetString() ?? "")
                  : root.TryGetProperty("token", out var t2) ? (t2.GetString() ?? "") : "";

        var hash = "";
        if (root.TryGetProperty("DisplayClaims", out var dc) &&
            dc.TryGetProperty("xui", out var xui) && xui.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in xui.EnumerateArray())
                if (item.TryGetProperty("uhs", out var u)) { hash = u.GetString() ?? ""; break; }
        }
        return (token, hash);
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
        [JsonPropertyName("verification_uri_complete")] public string VerificationUriComplete { get; set; } = "";
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class ErrorReponse
    {
        [JsonPropertyName("error")] public string Error { get; set; } = "";
        [JsonPropertyName("error_description")] public string Description { get; set; } = "";
    }
}

/// <summary>设备码流程中"用户尚未完成授权"不是错误，UI 用它控制继续轮询。</summary>
public class DeviceFlowPendingException : Exception
{
    public DeviceFlowPendingException(string error, string description)
        : base($"等待授权（{error}）" + (string.IsNullOrWhiteSpace(description) ? "" : "：" + description))
    {
        Error = error;
    }

    public string Error { get; }
    public bool StillPending => Error is "authorization_pending" or "slow_down";
}

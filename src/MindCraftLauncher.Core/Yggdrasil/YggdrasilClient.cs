using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Yggdrasil;

/// <summary>Yggdrasil 登录后的会话。gameProfile 里的 id 必须是去掉横杠的 32 位十六进制。</summary>
public sealed class YggdrasilSession
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = "";
    [JsonPropertyName("selectedProfile")] public GameProfile? SelectedProfile { get; set; }
    [JsonPropertyName("availableProfiles")] public List<GameProfile> AvailableProfiles { get; set; } = new();

    public string UuidDashless => (SelectedProfile?.Id ?? "").Replace("-", "");
    public string PlayerName => SelectedProfile?.Name ?? "";

    public sealed class GameProfile
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }
}

/// <summary>Yggdrasil 服务器自检信息（GET API 根地址）。</summary>
public sealed class YggdrasilServerInfo
{
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "";
    [JsonPropertyName("links")] public Dictionary<string, string> Links { get; set; } = new();
    [JsonPropertyName("skinDomains")] public List<string> SkinDomains { get; set; } = new();
    [JsonPropertyName("feature.enable_profile_edit")] public bool ProfileEditable { get; set; }
}

public class YggdrasilException : Exception
{
    public YggdrasilException(int status, string error, string cause)
        : base($"认证失败 HTTP {status} {error}" + (string.IsNullOrWhiteSpace(cause) ? "" : "：" + cause))
    {
        Status = status; Error = error; Cause2 = cause;
    }

    public int Status { get; }
    public string Error { get; }
    public string Cause2 { get; }

    /// <summary>403 通常是密码错或角色不存在；429 是服务器限流。</summary>
    public bool Retryable => Status == 429 || Status >= 500;
}

/// <summary>
/// Yggdrasil 外置登录客户端（红石联机的账号侧）。
/// 接口按 authlib-injector 规范：POST {apiRoot}/authenticate、/refresh、/validate、/invalidate，
/// GET {apiRoot} 取服务器信息。请求体里的 agent.name 固定 "Minecraft"。
/// </summary>
public sealed class YggdrasilClient : IDisposable
{
    private readonly HttpFetcher _http;
    private readonly string _apiRoot;

    public YggdrasilClient(HttpFetcher http, string authServerUrl)
    {
        _http = http;
        _apiRoot = JavaAgentArgument.NormalizeUrl(authServerUrl);
    }

    public string ApiRoot => _apiRoot;

    public async Task<YggdrasilServerInfo?> GetServerInfoAsync(CancellationToken ct = default)
    {
        try { return await _http.GetJsonAsync<YggdrasilServerInfo>(_apiRoot, null, ct).ConfigureAwait(false); }
        catch (HttpException) { return null; }   // 服务器不实现自检接口也不算错误
    }

    public async Task<YggdrasilSession> AuthenticateAsync(string username, string password,
        string? clientToken = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["agent"] = new Dictionary<string, string> { ["name"] = "Minecraft", ["version"] = "" },
            ["username"] = username,
            ["password"] = password,
            ["clientToken"] = clientToken ?? Guid.NewGuid().ToString("N"),
            ["requestUser"] = true,
        };
        using var resp = await _http.PostJsonAsync(_apiRoot + "/authenticate", payload, ct).ConfigureAwait(false);
        return await ParseResponseAsync<YggdrasilSession>(resp, ct).ConfigureAwait(false);
    }

    public async Task<YggdrasilSession> RefreshAsync(string accessToken, string clientToken, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["accessToken"] = accessToken,
            ["clientToken"] = clientToken,
            ["requestUser"] = "true",
        };
        using var resp = await _http.PostJsonAsync(_apiRoot + "/refresh", payload, ct).ConfigureAwait(false);
        return await ParseResponseAsync<YggdrasilSession>(resp, ct).ConfigureAwait(false);
    }

    /// <summary>validate：仅检查 token 是否仍有效，返回体为空。</summary>
    public async Task<bool> ValidateAsync(string accessToken, string clientToken, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string> { ["accessToken"] = accessToken, ["clientToken"] = clientToken };
        using var resp = await _http.PostJsonAsync(_apiRoot + "/validate", payload, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> InvalidateAsync(string accessToken, string clientToken, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string> { ["accessToken"] = accessToken, ["clientToken"] = clientToken };
        using var resp = await _http.PostJsonAsync(_apiRoot + "/invalidate", payload, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = JsonOpt.Deserialize<YggdrasilError>(body);
            throw new YggdrasilException((int)resp.StatusCode, err?.Error ?? "Unknown", err?.Cause ?? "");
        }
        var parsed = JsonOpt.Deserialize<T>(body);
        if (parsed is null) throw new InvalidOperationException("认证响应无法解析：" + Truncate(body));
        return parsed;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];

    public void Dispose() => _http.Dispose();
}

internal sealed class YggdrasilError
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("cause")] public string Cause { get; set; } = "";
}

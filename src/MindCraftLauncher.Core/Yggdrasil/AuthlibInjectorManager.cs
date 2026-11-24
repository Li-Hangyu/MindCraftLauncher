using System.Text.Json.Serialization;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.Core.Yggdrasil;

/// <summary>authlib-injector 的工件清单（latest.json）。</summary>
public sealed class AuthlibInjectorArtifact
{
    [JsonPropertyName("build_number")] public int BuildNumber { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("release_time")] public string ReleaseTime { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("checksums")] public Checksums? Checksum { get; set; }

    public sealed class Checksums
    {
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }
}

/// <summary>
/// authlib-injector jar 的分发与缓存。
/// 实测（2026-11-01）两个端点都可用，返回 v1.2.8 / build 56：
///   官方   https://authlib-injector.yushi.moe/artifact/latest.json
///   镜像   https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json
/// jar 的校验用的是 sha256（与 Mojang 文件的 sha1 不同，别弄混）。
/// </summary>
public sealed class AuthlibInjectorManager
{
    public const string OfficialArtifactUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    public const string MirrorArtifactUrl = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json";

    private readonly HttpFetcher _http;
    private readonly IDownloadSource _source;

    public AuthlibInjectorManager(HttpFetcher http, IDownloadSource source)
    {
        _http = http;
        _source = source;
    }

    public string LocalJarPath
    {
        get
        {
            var dir = GameLocation.LauncherDataRoot();
            return Path.Combine(dir, "authlib-injector.jar");
        }
    }

    public Task<AuthlibInjectorArtifact> FetchLatestAsync(CancellationToken ct = default)
        => _http.GetJsonAsync<AuthlibInjectorArtifact>(_source.RewriteUrl(OfficialArtifactUrl), null, ct);

    /// <summary>下载（或复用）authlib-injector.jar，返回本地路径。</summary>
    public async Task<string> EnsureJarAsync(bool forceUpdate, CancellationToken ct = default)
    {
        if (!forceUpdate && File.Exists(LocalJarPath)) return LocalJarPath;

        var meta = await FetchLatestAsync(ct).ConfigureAwait(false);
        var url = _source.RewriteUrl(meta.DownloadUrl);
        await _http.DownloadFileAsync(url, LocalJarPath, null, null, ct).ConfigureAwait(false);

        if (meta.Checksum?.Sha256 is { Length: > 0 } sha256)
        {
            var actual = await Crypto.Sha256FileAsync(LocalJarPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(LocalJarPath);
                throw new IntegrityException(url, sha256, actual);
            }
        }
        return LocalJarPath;
    }
}

/// <summary>
/// 生成 authlib-injector 的 -javaagent 参数。
/// 官方用法：-javaagent:&lt;jar 路径&gt;=&lt;认证服务器地址&gt;
/// 路径含空格必须整体加引号，否则 JVM 解析 agentarg 会截断。
/// </summary>
public static class JavaAgentArgument
{
    public static string Build(string jarPath, string authServerUrl)
    {
        if (string.IsNullOrWhiteSpace(jarPath)) throw new ArgumentException("authlib-injector jar 路径为空", nameof(jarPath));
        if (string.IsNullOrWhiteSpace(authServerUrl)) throw new ArgumentException("认证服务器地址为空", nameof(authServerUrl));

        var normalized = NormalizeUrl(authServerUrl);
        var path = jarPath.Trim();
        var needsQuote = path.Contains(' ');
        var prefix = needsQuote ? "\"" : "";
        var suffix = needsQuote ? "\"" : "";
        return $"-javaagent:{prefix}{path}{suffix}={normalized}";
    }

    /// <summary>
    /// 认证地址规范化：皮肤站给的往往是站点首页（https://mcskin.cn），
    /// 而 authlib-injector 需要的是 Yggdrasil API 根（https://mcskin.cn/api/yggdrasil）。
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase)) return u;
        return u + "/api/yggdrasil";
    }
}

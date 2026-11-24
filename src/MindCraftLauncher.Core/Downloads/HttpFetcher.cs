using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.Downloads;

/// <summary>
/// 唯一的 HTTP 出口。所有下载都带 UA（BMCLAPI 的使用规范要求使用它的程序标明来源），
/// 自动跟随跳转（BMCLAPI 会 302 到具体分发节点）、失败重试。
/// </summary>
public sealed class HttpFetcher : IDisposable
{
    /// <summary>对外声明的 User-Agent，便于镜像源站识别与统计。</summary>
    public const string UserAgentValue = "MindCraftLauncher/1.0";

    private readonly HttpClient _client;

    public HttpFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var h = handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(h) { Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/octet-stream, */*");
    }

    public HttpClient Client => _client;

    public async Task<T> GetJsonAsync<T>(string url, IDownloadSource? source = null, CancellationToken ct = default)
    {
        var text = await GetStringAsync(url, source, ct).ConfigureAwait(false);
        var obj = JsonOpt.Deserialize<T>(text);
        if (obj is null) throw new InvalidOperationException("JSON 解析结果为空：" + url);
        return obj;
    }

    public async Task<string> GetStringAsync(string url, IDownloadSource? source = null, CancellationToken ct = default)
    {
        var final = source?.RewriteUrl(url) ?? url;
        using var resp = await GetAsync(final, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, final).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct)
        => SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), option, ct);

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url),
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, url).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public Task<HttpResponseMessage> PostJsonAsync(string url, object payload, CancellationToken ct = default)
        => SendWithRetryAsync(() =>
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, url);
            msg.Content = new StringContent(JsonOpt.Serialize(payload), Encoding.UTF8, "application/json");
            return msg;
        }, HttpCompletionOption.ResponseHeadersRead, ct);

    public Task<HttpResponseMessage> PostFormAsync(string url, IDictionary<string, string> form, CancellationToken ct = default)
        => SendWithRetryAsync(() =>
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, url);
            msg.Content = new FormUrlEncodedContent(form);
            return msg;
        }, HttpCompletionOption.ResponseHeadersRead, ct);

    /// <summary>下载到文件：先写 .part，sha1 校验通过后再改名，避免中断留下半成品。</summary>
    public async Task DownloadFileAsync(string url, string targetPath, string? expectedSha1,
        IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(targetPath))
        {
            if (string.IsNullOrEmpty(expectedSha1) || await MatchesSha1(targetPath, expectedSha1!).ConfigureAwait(false))
            {
                progress?.Report(new TransferProgress(targetPath, 0, 0, "已存在，跳过"));
                return;
            }
        }

        var temp = targetPath + ".part";
        using (var resp = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url),
                   HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            await EnsureSuccess(resp, url).ConfigureAwait(false);
            var total = resp.Content.Headers.ContentLength ?? -1;

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(new TransferProgress(targetPath, done, total, null));
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(expectedSha1))
        {
            var actual = await Crypto.Sha1FileAsync(temp, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new IntegrityException(url, expectedSha1!, actual);
            }
        }

        File.Move(temp, targetPath, true);
        progress?.Report(new TransferProgress(targetPath, 0, 0, "完成"));
    }

    private static async Task<bool> MatchesSha1(string path, string expected)
    {
        try
        {
            return string.Equals(await Crypto.Sha1FileAsync(path).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory,
        HttpCompletionOption option, CancellationToken ct)
    {
        const int maxTry = 3;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxTry; attempt++)
        {
            try
            {
                using var msg = factory();
                var resp = await _client.SendAsync(msg, option, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return resp;

                // 4xx（429 除外）视为永久错误，直接返回给上层判断
                if ((int)resp.StatusCode < 500 && resp.StatusCode != (HttpStatusCode)429) return resp;

                var delay = RetryDelay(resp, attempt);
                resp.Dispose();
                if (attempt == maxTry) throw new HttpRequestException($"服务端持续错误，已重试 {maxTry} 次");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == maxTry) break;
                await Task.Delay(TimeSpan.FromMilliseconds(400L * attempt), ct).ConfigureAwait(false);
            }
        }

        throw lastError ?? new HttpRequestException("请求失败");
    }

    private static TimeSpan RetryDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d && d > TimeSpan.Zero) return d;
            if (ra.Date is { } date)
            {
                var delta = date - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero) return delta;
            }
        }
        return TimeSpan.FromMilliseconds(600L * attempt);
    }

    private static async Task EnsureSuccess(HttpResponseMessage resp, string url)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = Truncate(await SafeRead(resp).ConfigureAwait(false));
        throw new HttpException(resp.StatusCode, url, body);
    }

    private static async Task<string> SafeRead(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch { return ""; }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];

    public void Dispose() => _client.Dispose();
}

/// <summary>HTTP 层错误，带上 URL 与响应片段，方便 UI 直接展示排障信息。</summary>
public class HttpException : Exception
{
    public HttpException(HttpStatusCode code, string url, string body)
        : base($"HTTP {(int)code} {url}" + (string.IsNullOrWhiteSpace(body) ? "" : " :: " + body))
    {
        StatusCode = code;
        Url = url;
    }

    public HttpStatusCode StatusCode { get; }
    public string Url { get; }
}

public class IntegrityException : Exception
{
    public IntegrityException(string url, string expected, string actual)
        : base($"sha1 校验失败：{url} 期望 {expected} 实际 {actual}")
    {
        Url = url; Expected = expected; Actual = actual;
    }

    public string Url { get; }
    public string Expected { get; }
    public string Actual { get; }
}

public readonly record struct TransferProgress(string Path, long DoneBytes, long TotalBytes, string? Message);

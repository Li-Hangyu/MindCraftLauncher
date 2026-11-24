using System.Security.Cryptography;

namespace MindCraftLauncher.Core.Common;

/// <summary>哈希工具。Mojang 的文件校验用 sha1，authlib-injector 的工件清单用 sha256。</summary>
public static class Crypto
{
    public static async Task<string> Sha1FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA1.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return ToHex(hash);
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return ToHex(hash);
    }

    public static string Sha1Hex(ReadOnlySpan<byte> data) => ToHex(SHA1.HashData(data));

    /// <summary>Minecraft 离线模式 UUID：MD5("OfflinePlayer:" + 名字)，再把 version 改成 3、variant 改成 2。</summary>
    public static Guid OfflinePlayerUuid(string playerName)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version 3
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 2 (RFC 4122)
        // MD5 的前 4/2/2 字节是小端序，Guid 构造函数会按 B/E 混合序处理，这里用字节数组版本保证与 Java 端一致
        return new Guid(new ReadOnlySpan<byte>(bytes));
    }

    private static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}

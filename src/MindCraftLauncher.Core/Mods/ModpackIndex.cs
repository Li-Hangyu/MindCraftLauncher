using System.Text.Json.Serialization;

namespace MindCraftLauncher.Core.Mods;

/// <summary>
/// .mrpack 里的 modrinth.index.json。字段结构在 2026-11-11 实测确认：
/// dependencies 同时给出 minecraft 与 fabric-loader（Forge 整合包给 forge 或 neoforge）。
/// </summary>
public sealed class ModpackIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("game")] public string Game { get; set; } = "minecraft";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("files")] public List<ModpackFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();

    public string MinecraftVersion =>
        Dependencies.TryGetValue("minecraft", out var v) ? v : "";

    public string? FabricLoader =>
        Dependencies.TryGetValue("fabric-loader", out var v) ? v : null;

    public string? QuiltLoader =>
        Dependencies.TryGetValue("quilt-loader", out var v) ? v : null;

    /// <summary>Forge 依赖常写成纯版本号（47.4.0），也见过带游戏版本前缀的写法。</summary>
    public string? ForgeVersion =>
        Dependencies.TryGetValue("forge", out var v) ? v : null;

    public string? NeoForgeVersion =>
        Dependencies.TryGetValue("neoforge", out var v) ? v : null;

    /// <summary>客户端需要下载的文件（env.client == unsupported 的跳过）。</summary>
    public IEnumerable<ModpackFile> ClientFiles =>
        Files.Where(f => !string.Equals(f.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase));
}

public sealed class ModpackFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = new();
    [JsonPropertyName("hashes")] public ModrinthHashes? Hashes { get; set; }
    [JsonPropertyName("env")] public ModpackEnv? Env { get; set; }

    public string? Url => Downloads.FirstOrDefault();
}

public sealed class ModpackEnv
{
    [JsonPropertyName("client")] public string? Client { get; set; }
    [JsonPropertyName("server")] public string? Server { get; set; }
}

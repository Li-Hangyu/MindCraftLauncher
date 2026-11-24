using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindCraftLauncher.Core.Downloads;

/// <summary>version_manifest_v2.json。</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestPointers Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<VersionEntry> Versions { get; set; } = new();

    public sealed class LatestPointers
    {
        [JsonPropertyName("release")] public string Release { get; set; } = "";
        [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
    }

    public sealed class VersionEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("time")] public string Time { get; set; } = "";
        [JsonPropertyName("releaseTime")] public string ReleaseTime { get; set; } = "";
    }
}

/// <summary>版本 json（versions/&lt;id&gt;/&lt;id&gt;.json，即 Mojang 的 client version 描述）。</summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("mainClass")] public string MainClass { get; set; } = "";
    [JsonPropertyName("assets")] public string Assets { get; set; } = "legacy";
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }

    [JsonPropertyName("downloads")] public DownloadsBag Downloads { get; set; } = new();
    [JsonPropertyName("assetIndex")] public FileRef? AssetIndex { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryRef> Libraries { get; set; } = new();
    [JsonPropertyName("javaVersion")] public JavaVersionReq JavaVersion { get; set; } = new();

    /// <summary>1.13+ 用 arguments.game / arguments.jvm；老版本用 minecraftArguments 字符串。</summary>
    [JsonPropertyName("arguments")] public ArgumentsBag? Arguments { get; set; }

    /// <summary>Forge / LabyMod 这类 profile 会自带 maven 依赖与额外主类参数。</summary>
    [JsonPropertyName("jarmod")] public string? JarMod { get; set; }
    [JsonPropertyName("jar")] public string? Jar { get; set; }

    public sealed class DownloadsBag
    {
        [JsonPropertyName("client")] public FileRef? Client { get; set; }
        [JsonPropertyName("server")] public FileRef? Server { get; set; }
    }

    public sealed class ArgumentsBag
    {
        [JsonPropertyName("game")] public List<JsonElement>? Game { get; set; }
        [JsonPropertyName("jvm")] public List<JsonElement>? Jvm { get; set; }
    }
}

public sealed class FileRef
{
    /// <summary>assetIndex 用 id 表示索引名（如 "17" / "34"），普通文件项没有该字段。</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }

    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class LibraryRef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("rules")] public List<RuleRef> Rules { get; set; } = new();

    /// <summary>老格式（1.7 时代）的库只有 url，没有 downloads.artifact。</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    public bool IsNativeOnly => Natives is { Count: > 0 } && Downloads?.Artifact is null;
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public FileRef? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, FileRef>? Classifiers { get; set; }
}

public sealed class RuleRef
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";

    [JsonPropertyName("os")] public OsMatchRef? Os { get; set; }
}

public sealed class OsMatchRef
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}

public sealed class JavaVersionReq
{
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; } = 8;
    [JsonPropertyName("component")] public string? Component { get; set; }
}

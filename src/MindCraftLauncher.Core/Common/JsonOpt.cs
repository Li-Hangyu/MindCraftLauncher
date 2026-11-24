using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindCraftLauncher.Core.Common;

/// <summary>统一的 JSON 配置。Mojang / LabyMod 的字段多为 snake_case 或 camelCase，反序列化一律放开大小写匹配。</summary>
public static class JsonOpt
{
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Read);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Write);
}

using System.Text.Json;
using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.Downloads;

/// <summary>规则判定：libraries 的 rules 决定当前系统要不要这个库。</summary>
public static class RuleEvaluator
{
    /// <param name="osName">windows / osx / linux，取自 <see cref="CurrentOsName"/>。</param>
    public static bool Allows(IReadOnlyList<RuleRef>? rules, string osName)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            var matches = MatchesOs(rule.Os, osName);
            if (!matches) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool MatchesOs(OsMatchRef? os, string osName)
    {
        if (os is null) return true;                     // 无 os 限定 = 所有平台都命中
        if (string.IsNullOrEmpty(os.Name)) return true;
        return string.Equals(os.Name, osName, StringComparison.OrdinalIgnoreCase);
    }

    public static string CurrentOsName
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS()) return "osx";
            if (OperatingSystem.IsLinux()) return "linux";
            return "linux";
        }
    }
}

/// <summary>
/// arguments.game / arguments.jvm 的展开。新版 Mojang 用"对象或字符串"的混合数组，
/// 对象形式带 rules，命中才取其 value（value 本身还能是数组）。
/// </summary>
public static class ArgumentExpander
{
    public static List<string> Expand(List<JsonElement>? items, string osName, IDictionary<string, string> placeholders)
    {
        var result = new List<string>();
        if (items is null) return result;

        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(Substitute(item.GetString()!, placeholders));
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var rules = item.TryGetProperty("rules", out var r)
                    ? JsonSerializer.Deserialize<List<RuleRef>>(r.GetRawText(), JsonOpt.Read)
                    : null;

                if (!RuleEvaluator.Allows(rules, osName)) continue;

                if (item.TryGetProperty("value", out var v))
                {
                    if (v.ValueKind == JsonValueKind.String)
                        result.Add(Substitute(v.GetString()!, placeholders));
                    else if (v.ValueKind == JsonValueKind.Array)
                        foreach (var e in v.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.String)
                                result.Add(Substitute(e.GetString()!, placeholders));
                }
            }
        }
        return result;
    }

    /// <summary>把 ${xxx} / ${a.b:c} 这类占位符按表替换，表里没有的保留原样。</summary>
    public static string Substitute(string raw, IDictionary<string, string> placeholders)
    {
        if (!raw.Contains("${", StringComparison.Ordinal)) return raw;

        var sb = new System.Text.StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var start = raw.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(raw, i, raw.Length - i);
                break;
            }
            sb.Append(raw, i, start - i);
            var end = raw.IndexOf('}', start + 2);
            if (end < 0)
            {
                sb.Append(raw, start, raw.Length - start);
                break;
            }

            var token = raw.Substring(start + 2, end - start - 2);
            var key = token;
            var defaultValue = "";
            var colon = token.IndexOf(':');
            if (colon > 0)
            {
                key = token[..colon];
                defaultValue = token[(colon + 1)..];
            }
            sb.Append(placeholders.TryGetValue(key, out var val) ? val : defaultValue);
            i = end + 1;
        }
        return sb.ToString();
    }
}

/// <summary>
/// maven 坐标 → 本地相对路径。com.google.guava:guava:21.0 → com/google/guava/guava/21.0/guava-21.0.jar
/// 带 classifier 的：name:classifier 形式在 natives 里出现，这里单独处理。
/// </summary>
public static class MavenPath
{
    public static string FromCoordinate(string coordinate, string? classifier = null)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3) return coordinate.Replace(':', '/');

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var tail = parts.Length > 3 ? ":" + parts[3] : classifier is null ? "" : ":" + classifier;
        var suffix = string.IsNullOrEmpty(tail) ? "" : "-" + tail.TrimStart(':');
        return $"{group}/{artifact}/{version}/{artifact}-{version}{suffix}.jar";
    }
}

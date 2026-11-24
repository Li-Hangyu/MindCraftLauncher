using System.Runtime.CompilerServices;

namespace MindCraftLauncher.Tests;

/// <summary>极简测试框架：注册 → 运行 → 汇总，退出码非 0 表示有失败。</summary>
public static class TestRunner
{
    private static readonly List<(string Name, Action Body)> Cases = new();
    private static readonly List<string> Failures = new();

    public static void Test(string name, Action body) => Cases.Add((name, body));

    public static int Run(string? filter = null)
    {
        var passed = 0;
        foreach (var (name, body) in Cases)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                body();
                passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception ex)
            {
                Failures.Add(name + " :: " + ex.Message);
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.Message);
                if (ex is AssertException) Console.WriteLine("        " + (ex.StackTrace ?? "").Split('\n').Skip(1).FirstOrDefault()?.Trim());
            }
        }

        Console.WriteLine();
        Console.WriteLine($"共 {Cases.Count} 项，通过 {passed}，失败 {Failures.Count}");
        foreach (var f in Failures) Console.WriteLine("  ✗ " + f);
        return Failures.Count == 0 ? 0 : 1;
    }
}

public class AssertException : Exception
{
    public AssertException(string message) : base(message) { }
}

public static class Assert
{
    public static void True(bool condition, string because = "条件应为真")
    {
        if (!condition) throw new AssertException(because);
    }

    public static void False(bool condition, string because = "条件应为假") => True(!condition, because);

    public static void Equal<T>(T expected, T actual, string because = "")
    {
        if (expected is System.Collections.IEnumerable e1 && actual is System.Collections.IEnumerable a1
            && expected is not string)
        {
            Sequence(e1, a1, because);
            return;
        }
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"期望 <{expected}> 实际 <{actual}> {because}");
    }

    public static void Sequence(System.Collections.IEnumerable expected, System.Collections.IEnumerable actual, string because = "")
    {
        var e = expected.Cast<object?>().ToList();
        var a = actual.Cast<object?>().ToList();
        if (e.Count != a.Count || !e.Zip(a).All(x => Equals(x.First, x.Second)))
            throw new AssertException($"序列期望 [{string.Join(", ", e)}] 实际 [{string.Join(", ", a)}] {because}");
    }

    public static void Null(object? value, string because = "应为 null")
    {
        if (value is not null) throw new AssertException($"{because}，实际 <{value}>");
    }

    public static void NotEqual<T>(T notExpected, T actual, string because = "")
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertException($"不应等于 <{notExpected}> {because}");
    }

    public static void NotNull(object? value, string because = "不应为 null")
    {
        if (value is null) throw new AssertException(because);
    }

    public static void Contains(string substring, string actual, string because = "")
    {
        if (actual is null || !actual.Contains(substring, StringComparison.Ordinal))
            throw new AssertException($"应包含 <{substring}>，实际 <{Truncate(actual)}> {because}");
    }

    public static void DoesNotContain(string substring, string actual)
    {
        if (actual is not null && actual.Contains(substring, StringComparison.Ordinal))
            throw new AssertException($"不应包含 <{substring}>，实际 <{Truncate(actual)}>");
    }

    public static void Count<T>(int expected, IEnumerable<T> items)
    {
        var actual = items.Count();
        if (actual != expected) throw new AssertException($"元素个数期望 {expected} 实际 {actual}");
    }

    public static TException Throws<TException>(Action body) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertException($"应抛 {typeof(TException).Name}，实际抛 {ex.GetType().Name}: {ex.Message}");
        }
        throw new AssertException($"应抛 {typeof(TException).Name}，但没有抛出");
    }

    private static string Truncate(string? s) => s is null ? "<null>" : (s.Length <= 160 ? s : s[..160] + "…");
}

/// <summary>用临时目录做文件相关测试，自动清理。</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "mcl-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string name, string content = "")
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

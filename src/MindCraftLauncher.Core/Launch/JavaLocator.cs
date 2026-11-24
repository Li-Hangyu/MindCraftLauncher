using System.Collections.Concurrent;
using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.Launch;

/// <summary>找到的一个 Java 运行时。</summary>
public sealed record JavaInstall(string ExecutablePath, int MajorVersion, string Source)
{
    public string DisplayName => $"JDK {MajorVersion} — {ExecutablePath} ({Source})";
}

/// <summary>
/// Java 定位。优先级：设置里手动指定 → JAVA_HOME → 启动器托管的 runtimes 目录 → 常见安装目录 → PATH。
/// 版本匹配规则按 Mojang 的 javaVersion.majorVersion（如 1.12.2 要 8、1.20.1 要 17、26.x 要 25）。
/// </summary>
public sealed class JavaLocator
{
    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteTicks, int Major)> ProbeCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly string[] WinProbes =
    {
        @"C:\Program Files\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Zulu",
        @"C:\Program Files (x86)\Java",
    };

    public static IReadOnlyList<JavaInstall> Search(string gameRoot)
    {
        var candidates = new List<JavaInstall>();

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var exe = Bin(javaHome);
            if (File.Exists(exe)) candidates.Add(new JavaInstall(ToJavaw(exe), ProbeMajor(exe), "JAVA_HOME"));
        }

        var managed = Path.Combine(gameRoot, "runtimes");
        if (Directory.Exists(managed))
            foreach (var dir in Directory.EnumerateDirectories(managed))
            {
                var exe = Bin(dir);
                if (File.Exists(exe)) candidates.Add(new JavaInstall(ToJavaw(exe), ProbeMajor(exe), "启动器托管"));
            }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in WinProbes.Where(Directory.Exists))
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var exe = Bin(dir);
                if (File.Exists(exe)) candidates.Add(new JavaInstall(ToJavaw(exe), ProbeMajor(exe), "系统安装"));
            }

            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } local ? Path.Combine(local, "Programs") : null
                     }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)!)
            {
                SearchJdkDirectories(root!, candidates);
            }

            SearchJavaRegistry(candidates);
        }
        else
        {
            foreach (var jvmRoot in new[] { "/usr/lib/jvm", "/Library/Java/JavaVirtualMachines" }.Where(Directory.Exists))
            foreach (var dir in Directory.EnumerateDirectories(jvmRoot))
            {
                var exe = Bin(dir);
                if (File.Exists(exe)) candidates.Add(new JavaInstall(ToJavaw(exe), ProbeMajor(exe), "系统安装"));
            }
        }

        foreach (var name in new[] { "javaw", "java" })
        {
            var onPath = OnPath(name);
            if (onPath is not null)
            {
                var exe = ToJavaw(onPath);
                candidates.Add(new JavaInstall(exe, ProbeMajor(onPath), "PATH"));
            }
        }

        candidates = candidates.Where(c => File.Exists(c.ExecutablePath)).ToList();

        return candidates
            .Where(c => c.MajorVersion is 8 or 17 or 21 or 25)
            .GroupBy(c => c.ExecutablePath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => c.Source == "手动指定").ThenByDescending(c => c.MajorVersion).First())
            .OrderByDescending(c => c.MajorVersion)
            .ThenByDescending(c => c.Source == "手动指定")
            .ToList();
    }

    public static Task<IReadOnlyList<JavaInstall>> SearchAllDrivesAsync(
        string gameRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<JavaInstall>>(() =>
        {
            var candidates = Search(gameRoot).ToDictionary(
                install => Path.GetFullPath(install.ExecutablePath),
                install => install,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var visitedDirectories = 0;

            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;

                progress?.Report($"正在扫描磁盘 {drive.Name}，已找到 {candidates.Count} 个 JDK…");
                var pending = new Stack<string>();
                pending.Push(drive.RootDirectory.FullName);

                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = pending.Pop();
                    visitedDirectories++;

                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(directory, "javaw.exe", SearchOption.TopDirectoryOnly))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var javaw = Path.GetFullPath(file);
                            var java = Path.Combine(Path.GetDirectoryName(javaw)!, "java.exe");
                            var probe = File.Exists(java) ? java : javaw;
                            var major = ProbeMajor(probe);
                            if (major is not (8 or 17 or 21 or 25)) continue;
                            candidates[javaw] = new JavaInstall(javaw, major, drive.Name);
                        }

                        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                    pending.Push(child);
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }

                    if (visitedDirectories % 1000 == 0)
                        progress?.Report($"已扫描 {visitedDirectories:N0} 个目录，找到 {candidates.Count} 个 JDK…");
                }
            }

            return (IReadOnlyList<JavaInstall>)candidates.Values
                .OrderBy(install => install.MajorVersion)
                .ThenBy(install => install.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);

    public static JavaInstall? Find(string? explicitPath, string gameRoot, int requiredMajor,
        IEnumerable<JavaInstall>? importedRuntimes = null)
    {
        var targetMajor = NormalizeRequiredMajor(requiredMajor);
        var candidates = new List<JavaInstall>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var exe = ToJavaw(explicitPath);
            var probe = File.Exists(explicitPath) ? explicitPath : exe;
            if (File.Exists(probe))
            {
                var major = ProbeMajor(probe);
                if (NormalizeRequiredMajor(major) == targetMajor)
                    candidates.Add(new JavaInstall(exe, major, "手动指定"));
            }
        }

        candidates.AddRange(Search(gameRoot));
        if (importedRuntimes is not null) candidates.AddRange(importedRuntimes);

        return candidates
            .Where(c => NormalizeRequiredMajor(c.MajorVersion) == targetMajor)
            .OrderByDescending(c => c.Source == "手动指定")
            .ThenBy(c => c.MajorVersion)
            .FirstOrDefault();
    }

    /// <summary>
    /// Minecraft JSON 的 16 对应 JDK 17；只有特定新版会要求 JDK 21/25。
    /// 这里统一成应用实际可提供的 JDK 主版本。
    /// </summary>
    public static int NormalizeRequiredMajor(int major) => major switch
    {
        <= 8 => 8,
        9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 => 17,
        21 or 22 or 23 or 24 => 21,
        >= 25 => 25,
    };

    private static string Bin(string home) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(home, "bin", "java.exe")
            : Path.Combine(home, "bin", "java");

    public static string ToJavaw(string executablePath)
    {
        if (!OperatingSystem.IsWindows()) return executablePath;
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(directory)) return executablePath;
        var javaw = Path.Combine(directory, "javaw.exe");
        return File.Exists(javaw) ? javaw : executablePath;
    }

    private static void SearchJdkDirectories(string root, ICollection<JavaInstall> candidates)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var bin = Bin(dir);
                if (File.Exists(bin))
                    candidates.Add(new JavaInstall(ToJavaw(bin), ProbeMajor(bin), "系统安装"));

                var binRoot = Path.Combine(dir, "bin");
                if (File.Exists(Path.Combine(binRoot, "java.exe")))
                {
                    var nested = Path.Combine(binRoot, "java.exe");
                    candidates.Add(new JavaInstall(ToJavaw(nested), ProbeMajor(nested), "系统安装"));
                }
            }
        }
        catch { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SearchJavaRegistry(ICollection<JavaInstall> candidates)
    {
        try
        {
            using var keys = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem
                    ? Microsoft.Win32.RegistryView.Registry64
                    : Microsoft.Win32.RegistryView.Default);

            foreach (var keyName in new[]
                     {
                         @"SOFTWARE\JavaSoft\JDK",
                         @"SOFTWARE\JavaSoft\Java Development Kit",
                         @"SOFTWARE\Eclipse Adoptium\JDK",
                         @"SOFTWARE\Microsoft\JDK"
                     })
            {
                using var parent = keys.OpenSubKey(keyName);
                if (parent is null) continue;
                foreach (var subName in parent.GetSubKeyNames())
                {
                    using var sub = parent.OpenSubKey(subName);
                    var home = sub?.GetValue("JavaHome") as string ?? sub?.GetValue("Path") as string;
                    if (string.IsNullOrWhiteSpace(home)) continue;
                    var exe = Bin(home);
                    if (File.Exists(exe)) candidates.Add(new JavaInstall(ToJavaw(exe), ProbeMajor(exe), "注册表"));
                }
            }
        }
        catch { }
    }

    private static string? OnPath(string fileName)
    {
        var sep = OperatingSystem.IsWindows() ? ";" : ":";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? fileName + ".exe" : fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    /// <summary>跑一次 `java -version` 解析主版本；结果按文件指纹缓存，避免每次启动都重复探测。</summary>
    internal static int ProbeMajor(string javaExe)
    {
        try
        {
            var fullPath = Path.GetFullPath(javaExe);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return 0;

            var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            if (ProbeCache.TryGetValue(fullPath, out var cached) &&
                cached.Length == info.Length && cached.LastWriteTicks == lastWriteTicks)
                return cached.Major;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return 0;

            var stderr = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return 0;
            }

            var output = Task.WhenAll(stderr, stdout).GetAwaiter().GetResult();
            var major = ParseMajor(string.Join(Environment.NewLine, output));
            ProbeCache[fullPath] = (info.Length, lastWriteTicks, major);
            return major;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 解析 "openjdk version \"17.0.9\"" / "\"1.8.0_372\""：1.x 系列取第二段，其余取第一段。
    /// </summary>
    public static int ParseMajor(string versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(versionOutput, "version\\s+\"([^\"]+)\"");
        if (!m.Success) return 0;
        var v = m.Groups[1].Value;
        var parts = v.Split('.', '-', '_');
        if (parts.Length == 0) return 0;
        if (!int.TryParse(parts[0], out var first)) return 0;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second)) return second;
        return first;
    }
}

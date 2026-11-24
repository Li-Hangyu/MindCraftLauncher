using System.Diagnostics;
using System.Text;
using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.Core.EasyTier;

/// <summary>组网进程生命周期事件。</summary>
public sealed record EasyTierLogLine(string Source, string Text);

/// <summary>
/// easytier-core / easytier-cli 的进程封装。
/// Windows 上创建虚拟网卡需要管理员权限，未提权时 EasyTier 会直接失败——这里把 stderr 原样透出，
/// 由 UI 提示用户"以管理员身份重启启动器"，不吞错误。
/// </summary>
public sealed class EasyTierRunner : IDisposable
{
    private readonly object _sync = new();
    private Process? _core;
    private readonly StringBuilder _output = new();

    public event Action<EasyTierLogLine>? LogReceived;

    /// <summary>可执行文件名（不带 .exe 也能找到，Windows 会自动补）。</summary>
    public string CoreExecutable { get; private set; } = "";
    public string CliExecutable { get; private set; } = "";

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                try { return _core is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    public string LastOutput => _output.ToString();

    /// <summary>在指定目录 / PATH 中定位 easytier-core。找不到返回 null，由调用方提示用户下载。</summary>
    public static string? Locate(string? configuredPath, params string[] extraDirs)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var names = OperatingSystem.IsWindows()
            ? new[] { "easytier-core.exe", "easytier-core" }
            : new[] { "easytier-core" };

        foreach (var dir in extraDirs.Prepend(GameLocation.LauncherBinaryRoot()).Append(GameLocation.LauncherDataRoot()))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
                var sub = Path.Combine(dir, "easytier", name);
                if (File.Exists(sub)) return sub;
            }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(entry.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* PATH 里存在非法字符时跳过 */ }
            }
        }
        return null;
    }

    public void Configure(string? corePath, string? cliPath)
    {
        CoreExecutable = corePath ?? Locate(null) ?? "";
        CliExecutable = cliPath ?? Locate(null) ?? "";

        // cli 与 core 通常同目录：core 找到了就用同目录下的 cli
        if (string.IsNullOrEmpty(CliExecutable) && !string.IsNullOrEmpty(CoreExecutable))
        {
            var dir = Path.GetDirectoryName(CoreExecutable) ?? "";
            var name = OperatingSystem.IsWindows() ? "easytier-cli.exe" : "easytier-cli";
            var guess = Path.Combine(dir, name);
            if (File.Exists(guess)) CliExecutable = guess;
        }
    }

    /// <summary>启动组网进程。已经在跑就先 Stop。</summary>
    public void Start(EasyTierProfile profile)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(CoreExecutable))
                throw new EasyTierNotInstalledException();

            StopNoLock();

            var args = EasyTierArgs.Build(profile);
            var psi = new ProcessStartInfo
            {
                FileName = CoreExecutable,
                WorkingDirectory = Path.GetDirectoryName(CoreExecutable) ?? GameLocation.LauncherBinaryRoot(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            _output.Clear();
            _core = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _core.OutputDataReceived += (_, e) => Emit("core", e.Data);
            _core.ErrorDataReceived += (_, e) => Emit("core-err", e.Data);
            _core.Start();
            _core.BeginOutputReadLine();
            _core.BeginErrorReadLine();
        }
    }

    public void Stop()
    {
        lock (_sync) StopNoLock();
    }

    private void StopNoLock()
    {
        if (_core is null) return;
        try
        {
            if (!_core.HasExited)
            {
                // EasyTier 自己处理优雅退出；10 秒内不退再 Kill
                if (!_core.WaitForExit(10_000)) _core.Kill(entireProcessTree: true);
            }
        }
        catch { try { _core.Kill(entireProcessTree: true); } catch { } }
        finally { _core.Dispose(); _core = null; }
    }

    /// <summary>执行 easytier-cli 子命令（peer / node / route），用于读取组网状态。</summary>
    public async Task<string> RunCliAsync(string subCommand, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(CliExecutable))
            throw new EasyTierNotInstalledException("未找到 easytier-cli，请在设置里指定 EasyTier 目录");

        var psi = new ProcessStartInfo
        {
            FileName = CliExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(subCommand);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 easytier-cli");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var outText = await stdoutTask.ConfigureAwait(false);
        var errText = await stderrTask.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(outText) ? errText : outText;
    }

    public Task<EasyTierStatus> QueryStatusAsync(CancellationToken ct = default) =>
        QueryStatusAsync("peer", ct);

    public async Task<EasyTierStatus> QueryStatusAsync(string subCommand, CancellationToken ct)
    {
        try
        {
            var text = await RunCliAsync(subCommand, ct).ConfigureAwait(false);
            return PeerTableParser.Parse(text, IsRunning);
        }
        catch (Exception ex)
        {
            return new EasyTierStatus(IsRunning, Array.Empty<EasyTierPeer>(), "", "状态读取失败：" + ex.Message);
        }
    }

    private void Emit(string source, string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_output) _output.AppendLine(line);
        LogReceived?.Invoke(new EasyTierLogLine(source, line));
    }

    public void Dispose() => Stop();
}

public class EasyTierNotInstalledException : Exception
{
    public EasyTierNotInstalledException()
        : base("未找到 easytier-core。请下载 EasyTier 命令行版，把 easytier-core.exe / easytier-cli.exe 放到启动器目录，或在【设置】里指定路径。") { }

    public EasyTierNotInstalledException(string message) : base(message) { }
}

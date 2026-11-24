using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Accounts;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Launch;
using MindCraftLauncher.Core.Yggdrasil;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>启动页：账号 + 当前选中的版本（卡片形式，不再是下拉框）+ 日志。</summary>
public sealed class LaunchViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;
    private Process? _game;
    private CancellationTokenSource? _startupCts;
    private readonly Queue<string> _recentOutput = new();
    private readonly object _outputSync = new();
    private bool _stoppingGame;

    public LaunchViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        Accounts = new ObservableCollection<LauncherAccount>(services.Accounts.All);
        _offlinePlayerName = Accounts.FirstOrDefault(account => account.Kind == AccountKind.Offline)?.PlayerName ?? "Player";

        GameCommand = new RelayCommand(() => LaunchSelected(), () => SelectedAccount is not null && !Busy && _game is null);
        UseOfflineAccountCommand = new RelayCommand(UseOfflineAccount);
        CancelStartupCommand = new RelayCommand(CancelStartup, () => Busy);
        StopCommand = new RelayCommand(StopGame, () => _game is not null);
        CopyPlanCommand = new RelayCommand(CopyPlan, () => LastPlan is { Arguments.Count: > 0 });
        ClearLogCommand = new RelayCommand(() => Log = "");

        SelectedAccount = Accounts.FirstOrDefault();

        if (Versions.Count == 0)
            Log = "还没有安装任何版本。去【下载】页，使用上方选项安装游戏版本或整合包。";
    }

    public ObservableCollection<LauncherAccount> Accounts { get; }

    private LauncherAccount? _selectedAccount;
    public LauncherAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!Set(ref _selectedAccount, value)) return;
            _svc.Config.LastAccountId = value?.Id ?? "";
            if (value?.Kind == AccountKind.Offline)
            {
                OfflinePlayerName = value.PlayerName;
                OfflineAccountStatus = "离线账号已选中，可直接启动。";
            }
        }
    }

    private string _offlinePlayerName = "Player";
    public string OfflinePlayerName
    {
        get => _offlinePlayerName;
        set
        {
            if (!Set(ref _offlinePlayerName, value)) return;
            OfflineAccountStatus = IsValidOfflinePlayerName(value)
                ? "按 Enter 或点击“使用离线账号”后即可启动。"
                : "请输入 3–16 位，仅包含英文字母、数字或下划线。";
            UseOfflineAccountCommand.Refresh();
        }
    }

    private string _offlineAccountStatus = "输入 Minecraft 离线玩家名。";
    public string OfflineAccountStatus { get => _offlineAccountStatus; private set => Set(ref _offlineAccountStatus, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    // 【修复】改为完整属性，确保赋值时能触发界面更新
    private bool _isProgressIndeterminate = true;
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => Set(ref _isProgressIndeterminate, value);
    }

    // 【注意】保持 private set，请在 XAML 中使用 Mode=OneWay 绑定此属性
    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    private string _loadingMessage = "正在准备启动游戏…";
    public string LoadingMessage { get => _loadingMessage; private set => Set(ref _loadingMessage, value); }

    /// <summary>版本列表（只读，供跳转提示用）。真正的选择在【版本】页。</summary>
    public IReadOnlyList<string> Versions => _svc.Installed.List().Select(v => v.Id).ToList();

    public string SelectedVersionText => _main.Versions.SelectedText;

    public bool HasVersion => _main.Versions.SelectedId.Length > 0;

    private string _log = "";
    public string Log
    {
        get => _log;
        set => Set(ref _log, value);
    }

    private LaunchPlan? _lastPlan;
    public LaunchPlan? LastPlan
    {
        get => _lastPlan;
        private set => Set(ref _lastPlan, value);
    }

    /// <summary>陶瓦 / 红石直连参数注入点：由对应页面写入，启动时并入命令行。</summary>
    public string? PendingServerAddress { get; set; }
    public int PendingServerPort { get; set; } = 25565;

    public RelayCommand GameCommand { get; }
    public RelayCommand CancelStartupCommand { get; }
    public RelayCommand UseOfflineAccountCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CopyPlanCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public void NotifyVersionChanged()
    {
        Raise(nameof(SelectedVersionText));
        Raise(nameof(HasVersion));
        Raise(nameof(Versions));
    }

    public void ReloadVersions() => NotifyVersionChanged();

    public void ReloadAccounts()
    {
        Accounts.Clear();
        foreach (var a in _svc.Accounts.All) Accounts.Add(a);
        SelectedAccount ??= Accounts.FirstOrDefault();
    }

    private void UseOfflineAccount()
    {
        var playerName = OfflinePlayerName.Trim();
        if (!IsValidOfflinePlayerName(playerName))
        {
            OfflineAccountStatus = "名称无效：请输入 3–16 位英文字母、数字或下划线。";
            return;
        }

        try
        {
            var account = _svc.Accounts.Add(LauncherAccount.Offline(playerName));
            ReloadAccounts();
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == account.Id) ?? account;
            OfflineAccountStatus = $"离线账号“{playerName}”已选中，可启动游戏。";
            _main.ReportStatus("已选择离线账号：" + playerName);
        }
        catch (Exception ex)
        {
            OfflineAccountStatus = "创建离线账号失败：" + ex.Message;
            _main.ReportError("创建离线账号", ex);
        }
    }

    private static bool IsValidOfflinePlayerName(string? playerName) =>
        playerName is { Length: >= 3 and <= 16 } && playerName.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    /// <summary>由【版本】页的"启动"或右上角按钮调用。</summary>
    public void LaunchSelected()
    {
        var versionId = _main.Versions.SelectedId;
        if (versionId.Length == 0)
        {
            MessageBox.Show("先在【版本】页选一个版本，或去【下载】页装一个。", "还没有可用版本",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartGame(versionId);
    }

    private async void StartGame(string versionId)
    {
        if (Busy || _game is not null) return;
        var cts = new CancellationTokenSource();
        _startupCts = cts;
        Busy = true;

        // 重置进度状态
        ProgressValue = 0;
        IsProgressIndeterminate = true;

        try
        {
            var account = SelectedAccount ??= OfflineWhenEmpty();

            // 进度回调处理
            var progress = new Progress<InstallProgress>(item =>
            {
                LoadingMessage = item.Label;

                // 更新进度条样式（转圈 vs 具体进度）
                bool shouldBeIndeterminate = item.Total <= 0;
                if (IsProgressIndeterminate != shouldBeIndeterminate)
                {
                    IsProgressIndeterminate = shouldBeIndeterminate;
                }

                // 更新进度数值
                if (item.Total > 0)
                {
                    double calculatedValue = Math.Clamp(item.Current * 100.0 / item.Total, 0, 100);
                    // 简单的防抖，避免UI更新过于频繁
                    if (Math.Abs(ProgressValue - calculatedValue) > 0.5)
                    {
                        ProgressValue = calculatedValue;
                    }
                }
            });

            LoadingMessage = "正在扫描游戏文件完整性…";
            var integrity = await _svc.Versions.CheckIntegrityAsync(versionId, progress, cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();

            if (!integrity.IsHealthy)
            {
                AppendLog($"完整性扫描发现 {integrity.InvalidFiles.Count} 个缺失或损坏文件，开始自动修复。");
                if (!_svc.Flags.GameDownloadEnabled || _svc.Flags.DownloadAcknowledgedAt is null)
                {
                    var answer = MessageBox.Show(
                        $"检测到 {integrity.InvalidFiles.Count} 个缺失或损坏的游戏文件。自动修复需要从 Mojang 官方分发端点或公开镜像下载文件。\n\n是否启用本体下载并继续修复？",
                        "需要修复游戏文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return;
                    _svc.Config.Flags.GameDownloadEnabled = true;
                    _svc.Config.Flags.DownloadAcknowledgedAt = DateTime.Now;
                    if (!_svc.SaveConfig()) throw new IOException("无法保存下载来源确认设置：" + _svc.LastSaveError);
                }

                LoadingMessage = "正在自动修复缺失或损坏的游戏文件…";
                await _svc.Versions.RepairAsync(versionId, progress, cts.Token).ConfigureAwait(true);
                cts.Token.ThrowIfCancellationRequested();

                LoadingMessage = "正在复查修复结果…";
                var repaired = await _svc.Versions.CheckIntegrityAsync(versionId, progress, cts.Token).ConfigureAwait(true);
                if (!repaired.IsHealthy)
                    throw new InvalidOperationException($"自动修复后仍有 {repaired.InvalidFiles.Count} 个文件校验失败，请检查网络或手动重新安装该版本。");
            }

            LoadingMessage = "游戏文件检查通过，正在准备启动…";
            cts.Token.ThrowIfCancellationRequested();

            // 红石外置登录：把 authlib-injector 注入进去
            string? agent = null;
            if (account.Kind == AccountKind.Yggdrasil && !string.IsNullOrWhiteSpace(account.AuthServerUrl))
            {
                LoadingMessage = "正在准备外置登录组件…";
                var jarPath = await _svc.AuthlibInjector.EnsureJarAsync(false, cts.Token).ConfigureAwait(true);
                agent = JavaAgentArgument.Build(jarPath, account.AuthServerUrl!);
                AppendLog("红石联机：已注入 -javaagent " + account.AuthServerUrl);
            }

            var req = new LaunchRequest(
                versionId, account,
                _svc.Config.Java.MaxMemoryMb,
                _svc.Config.Java.ExtraJvmArgs,
                PendingServerAddress,
                PendingServerPort,
                _svc.Config.Java.VersionIsolation,
                ExtraClassPath: ExtraJarFor(versionId),
                AuthlibInjectorJavaAgent: agent);

            var plan = _svc.Launcher.BuildPlan(req);
            LastPlan = plan;
            AppendLog("执行：" + plan.CommandLine);

            _game = _svc.Launcher.Start(req, out _);
            if (_game is null) { AppendLog("启动失败：无法创建进程"); return; }

            var game = _game;
            _stoppingGame = false;
            var exitReported = 0;

            void ReportExit()
            {
                if (Interlocked.Exchange(ref exitReported, 1) != 0) return;
                var exitCode = game.ExitCode;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(_game, game)) return;
                    _game = null;
                    AppendLog($"游戏进程已退出，退出码 {exitCode}");
                    _main.ReportStatus(exitCode == 0 ? "游戏已正常退出" : $"游戏启动失败或异常退出（退出码 {exitCode}）");
                    if (exitCode != 0 && !_stoppingGame)
                        MessageBox.Show(BuildCrashAdvice(exitCode), "Minecraft 异常退出", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _stoppingGame = false;
                    RefreshCommands();
                }));
            }

            game.OutputDataReceived += (_, e) => OnLine(e.Data);
            game.ErrorDataReceived += (_, e) => OnLine(e.Data);
            game.Exited += (_, _) => ReportExit();
            game.EnableRaisingEvents = true;
            game.BeginOutputReadLine();
            game.BeginErrorReadLine();
            if (game.HasExited) ReportExit();

            _main.ReportStatus("游戏进程已启动 " + versionId + "｜" + account.PlayerName);
            RefreshCommands();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            AppendLog("启动检查或修复已取消。");
            _main.ReportStatus("启动已取消");
        }
        catch (Exception ex)
        {
            AppendLog("启动失败：" + ex.Message);
            MessageBox.Show(ex.Message + "\n\n请检查启动日志、Java 版本以及网络连接。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_startupCts, cts)) _startupCts = null;
            cts.Dispose();
            Busy = false;
        }
    }

    private void CancelStartup()
    {
        _startupCts?.Cancel();
        LoadingMessage = "正在取消启动…";
    }

    private void StopGame()
    {
        if (_game is null) return;
        _stoppingGame = true;
        try { _game.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            _stoppingGame = false;
            _main.ReportError("停止 Minecraft", ex);
            MessageBox.Show("无法停止游戏进程：" + ex.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildCrashAdvice(int exitCode)
    {
        string[] lines;
        lock (_outputSync) lines = _recentOutput.ToArray();
        var tail = string.Join(Environment.NewLine, lines.TakeLast(12));
        var text = string.Join(Environment.NewLine, lines);

        var advice = text.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase)
            ? "Java 版本不兼容：请在【设置】中为此 Minecraft 版本指定所需主版本的 Java。"
            : text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase)
                ? "内存不足：请关闭其他程序，或在【设置】中适当提高游戏最大内存。"
                : text.Contains("NoSuchFileException", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("IntegrityException", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("ZipException", StringComparison.OrdinalIgnoreCase)
                    ? "疑似游戏文件缺失或损坏：重新点击启动会再次扫描并尝试修复；检查网络及下载源设置。"
                    : text.Contains("GLFW", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("OpenGL", StringComparison.OrdinalIgnoreCase)
                        ? "图形初始化失败：更新显卡驱动，并暂时移除光影或图形增强模组后重试。"
                        : "请确认 Java 版本符合游戏要求，关闭近期新增的模组/光影，并查看下方日志末尾定位原因。";

        return $"Minecraft 已异常退出（退出码 {exitCode}）。\n\n建议：{advice}\n\n最近日志：\n{(tail.Length == 0 ? "（没有捕获到游戏输出）" : tail)}";
    }

    private LauncherAccount OfflineWhenEmpty()
    {
        var fallback = LauncherAccount.Offline("Player");
        _svc.Accounts.Add(fallback);
        Application.Current?.Dispatcher.Invoke(ReloadAccounts);
        return SelectedAccount ?? fallback;
    }

    /// <summary>装过 LabyMod 的版本，启动时需要把 LabyMod jar 附加进 classpath。</summary>
    private List<string>? ExtraJarFor(string versionId)
    {
        if (!versionId.Contains("-LabyMod", StringComparison.OrdinalIgnoreCase)) return null;

        var root = Path.Combine(_svc.Paths.Libraries, "net", "labymod", "LabyMod-4");
        if (!Directory.Exists(root)) return null;

        var jar = Directory.EnumerateFiles(root, "*.jar", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTime)
            .Take(1)
            .ToList();
        return jar.Count > 0 ? jar : null;
    }

    private void CopyPlan()
    {
        if (LastPlan is null) return;
        try { Clipboard.SetText(LastPlan.CommandLine); _main.ReportStatus("启动命令已复制"); }
        catch (Exception ex) { _main.ReportStatus("复制失败：" + ex.Message); }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_outputSync)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > 200) _recentOutput.Dequeue();
        }
        Application.Current?.Dispatcher.Invoke(() => AppendLog(line));
    }

    private void AppendLog(string line)
    {
        Log += (Log.Length > 0 ? "\n" : "") + line;
        if (Log.Length > 200_000) Log = Log[^100_000..];
    }

    private static void RefreshCommands() =>
        Application.Current?.Dispatcher.Invoke(System.Windows.Input.CommandManager.InvalidateRequerySuggested);
}
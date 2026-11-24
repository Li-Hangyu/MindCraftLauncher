using System.Collections.ObjectModel;
using System.Windows;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.EasyTier;
using MindCraftLauncher.Core.Launch;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>设置页：路径、Java、内存、下载源、功能开关、EasyTier 位置、微软 client_id。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;

    public SettingsViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        var c = services.Config;
        GameRoot = c.GameRoot;
        JavaPath = c.Java.ExplicitJavaPath ?? "";
        MaxMemory = c.Java.MaxMemoryMb;
        ExtraJvmArgs = c.Java.ExtraJvmArgs;
        VersionIsolation = c.Java.VersionIsolation;
        EasyTierCorePath = c.EasyTierCorePath ?? services.EasyTier.CoreExecutable ?? "";
        MicrosoftClientId = c.MicrosoftClientId ?? "";
        Acknowledged = c.Flags.DownloadAcknowledgedAt is not null;
        GameDownloadEnabled = c.Flags.GameDownloadEnabled;
        LabyModEnabled = c.Flags.LabyModDownloadEnabled;
        TerracottaEnabled = c.Flags.TerracottaLinkEnabled;
        RedstoneEnabled = c.Flags.RedstoneLinkEnabled;

        Themes = new ObservableCollection<string> { "dark", "light" };
        SelectedTheme = c.Theme;
        DownloadSources = new ObservableCollection<IDownloadSource>(services.Sources.All);
        SelectedSource = DownloadSources.FirstOrDefault(s => s.Id == c.DownloadSourceId) ?? DownloadSources.FirstOrDefault();

        SaveCommand = new RelayCommand(Save);
        BrowseGameRootCommand = new RelayCommand(BrowseGameRoot);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        BrowseEasyTierCommand = new RelayCommand(BrowseEasyTier);
        DetectEasyTierCommand = new RelayCommand(DetectEasyTier);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
        JavaInstalls = new ObservableCollection<JavaInstall>(services.Config.Java.DiscoveredRuntimes
            .Select(runtime => new JavaInstall(runtime.ExecutablePath, runtime.MajorVersion, runtime.Source)));
        _main.Versions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VersionsViewModel.SelectedId))
                SelectJavaForCurrentVersion();
        };
        SelectJavaForCurrentVersion();

        InstalledCount = services.Installed.List().Count;
    }

    public ObservableCollection<string> Themes { get; }
    public ObservableCollection<IDownloadSource> DownloadSources { get; }
    public ObservableCollection<JavaInstall> JavaInstalls { get; }

    private JavaInstall? _selectedJava;
    public JavaInstall? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (!Set(ref _selectedJava, value) || value is null) return;
            Status = $"已导入 JDK {value.MajorVersion}：启动时会按游戏版本自动选择。";
        }
    }

    private string _selectedTheme = "dark";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value)) return;
            _main.Theme = value;
        }
    }

    private IDownloadSource? _selectedSource;
    public IDownloadSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value)) return;
            _svc.Config.DownloadSourceId = value?.Id ?? "mojang";
            _svc.SaveConfig();
            if (_main.Download.SelectedSource?.Id != value?.Id)
                _main.Download.SelectedSource = value;
        }
    }

    private string _gameRoot = "";
    public string GameRoot { get => _gameRoot; set => Set(ref _gameRoot, value); }

    private string _javaPath = "";
    public string JavaPath { get => _javaPath; set => Set(ref _javaPath, value); }

    private int _maxMemory = 4096;
    public int MaxMemory { get => _maxMemory; set => Set(ref _maxMemory, value); }

    private string _extraJvmArgs = "";
    public string ExtraJvmArgs { get => _extraJvmArgs; set => Set(ref _extraJvmArgs, value); }

    private bool _versionIsolation = true;
    public bool VersionIsolation { get => _versionIsolation; set => Set(ref _versionIsolation, value); }

    private string _easyTierCorePath = "";
    public string EasyTierCorePath { get => _easyTierCorePath; set => Set(ref _easyTierCorePath, value); }

    private string _microsoftClientId = "";
    public string MicrosoftClientId { get => _microsoftClientId; set => Set(ref _microsoftClientId, value); }

    private bool _gameDownloadEnabled;
    public bool GameDownloadEnabled
    {
        get => _gameDownloadEnabled;
        set
        {
            if (!Set(ref _gameDownloadEnabled, value)) return;

            if (value && !Acknowledged)
            {
                _svc.Config.Flags.GameDownloadEnabled = false;
                _svc.SaveConfig();
            }
            else
            {
                _svc.Config.Flags.GameDownloadEnabled = value;
                if (!value) Acknowledged = false;
                _svc.SaveConfig();
            }

            if (_main.Download.GameDownloadEnabled != _svc.Config.Flags.GameDownloadEnabled)
                _main.Download.GameDownloadEnabled = _svc.Config.Flags.GameDownloadEnabled;
            Raise(nameof(Acknowledged));
        }
    }

    private bool _acknowledged;
    public bool Acknowledged
    {
        get => _acknowledged;
        set
        {
            if (!Set(ref _acknowledged, value)) return;
            _svc.Config.Flags.DownloadAcknowledgedAt = value ? DateTime.Now : null;
            _svc.SaveConfig();
            if (_main.Download.AcknowledgeCompliance != value)
                _main.Download.AcknowledgeCompliance = value;
        }
    }

    private bool _labyModEnabled;
    public bool LabyModEnabled
    {
        get => _labyModEnabled;
        set
        {
            if (!Set(ref _labyModEnabled, value)) return;
            _svc.Config.Flags.LabyModDownloadEnabled = value;
            _svc.SaveConfig();
        }
    }

    private bool _terracottaEnabled;
    public bool TerracottaEnabled
    {
        get => _terracottaEnabled;
        set
        {
            if (!Set(ref _terracottaEnabled, value)) return;
            _svc.Config.Flags.TerracottaLinkEnabled = value;
            _svc.SaveConfig();
        }
    }

    private bool _redstoneEnabled;
    public bool RedstoneEnabled
    {
        get => _redstoneEnabled;
        set
        {
            if (!Set(ref _redstoneEnabled, value)) return;
            _svc.Config.Flags.RedstoneLinkEnabled = value;
            _svc.SaveConfig();
        }
    }

    private int _installedCount;
    public int InstalledCount { get => _installedCount; private set => Set(ref _installedCount, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand BrowseGameRootCommand { get; }
    public RelayCommand BrowseJavaCommand { get; }
    public RelayCommand BrowseEasyTierCommand { get; }
    public RelayCommand DetectEasyTierCommand { get; }
    public AsyncRelayCommand DetectJavaCommand { get; }

    private void Save()
    {
        var c = _svc.Config;
        c.GameRoot = GameRoot;
        _svc.Paths.Configure(GameRoot);
        c.Java.ExplicitJavaPath = string.IsNullOrWhiteSpace(JavaPath)
            ? null
            : JavaLocator.ToJavaw(JavaPath);
        c.Java.MaxMemoryMb = MaxMemory;
        c.Java.ExtraJvmArgs = ExtraJvmArgs;
        c.Java.VersionIsolation = VersionIsolation;
        c.EasyTierCorePath = string.IsNullOrWhiteSpace(EasyTierCorePath) ? null : EasyTierCorePath;
        c.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId;
        c.DownloadSourceId = SelectedSource?.Id ?? "mojang";
        c.Theme = SelectedTheme;

        c.Flags.GameDownloadEnabled = GameDownloadEnabled;
        c.Flags.DownloadAcknowledgedAt = GameDownloadEnabled && Acknowledged ? DateTime.Now : null;
        c.Flags.LabyModDownloadEnabled = LabyModEnabled;
        c.Flags.TerracottaLinkEnabled = TerracottaEnabled;
        c.Flags.RedstoneLinkEnabled = RedstoneEnabled;

        if (GameDownloadEnabled && !Acknowledged)
        {
            c.Flags.GameDownloadEnabled = false;
            c.Flags.DownloadAcknowledgedAt = null;
            var rollbackSaved = _svc.SaveConfig();

            // 修掉的 bug：配置回滚了但界面上的开关还亮着，看起来像保存成功了
            GameDownloadEnabled = false;
            Acknowledged = false;

            Status = rollbackSaved
                ? "本体下载开关未保存：需要勾选来源确认。"
                : "设置保存失败：" + (_svc.LastSaveError ?? "未知写入错误");
            MessageBox.Show(Status, "设置", MessageBoxButton.OK,
                rollbackSaved ? MessageBoxImage.Information : MessageBoxImage.Error);
            return;
        }

        _svc.EasyTier.Configure(c.EasyTierCorePath, c.EasyTierCliPath);
        if (!_svc.SaveConfig())
        {
            Status = "设置保存失败：" + (_svc.LastSaveError ?? "未知写入错误");
            MessageBox.Show(Status, "设置", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        InstalledCount = _svc.Installed.List().Count;
        Status = "已保存并已应用，当前会话无需重启。";
        _main.StatusText = "就绪｜游戏目录：" + c.GameRoot;
        _main.ReportStatus("设置已保存并已应用");
    }

    private void BrowseGameRoot()
    {
        var path = PickFolder();
        if (path is not null) GameRoot = path;
    }

    private void BrowseJava()
    {
        var file = PickFile("Java 可执行文件|javaw.exe;java.exe|所有文件|*.*");
        if (file is not null) JavaPath = JavaLocator.ToJavaw(file);
    }

    private async Task DetectJavaAsync()
    {
        Status = "正在全局扫描可用磁盘中的 javaw.exe，请稍候…";
        var progress = new Progress<string>(message => Status = message);
        var found = await JavaLocator.SearchAllDrivesAsync(GameRoot, progress);

        JavaInstalls.Clear();
        foreach (var install in found) JavaInstalls.Add(install);
        SelectJavaForCurrentVersion();

        var c = _svc.Config.Java;
        c.DiscoveredRuntimes = found.Select(install => new LauncherConfig.JavaRuntimeConfig
        {
            ExecutablePath = install.ExecutablePath,
            MajorVersion = install.MajorVersion,
            Source = install.Source,
        }).ToList();

        if (!_svc.SaveConfig())
        {
            Status = $"扫描到 {found.Count} 个 JDK，但导入失败：{_svc.LastSaveError ?? "未知配置写入错误"}";
            return;
        }

        Status = found.Count == 0
            ? "扫描完成：未找到 JDK 8、17、21 或 25 的 javaw.exe。"
            : $"扫描并导入完成：找到 {found.Count} 个 JDK；启动时会按游戏版本自动选择。";
    }

    private void SelectJavaForCurrentVersion()
    {
        var selectedVersion = _main.Versions.SelectedId;
        var requiredMajor = selectedVersion.Length == 0
            ? 17
            : VersionResolver.Resolve(_svc.Paths, selectedVersion)?.JavaVersion.MajorVersion ?? 17;
        var targetMajor = JavaLocator.NormalizeRequiredMajor(requiredMajor);
        SelectedJava = JavaInstalls.FirstOrDefault(install => install.MajorVersion == targetMajor)
                       ?? JavaInstalls.FirstOrDefault();
    }

    private void BrowseEasyTier()
    {
        var file = PickFile("easytier-core|easytier-core.exe");
        if (file is not null) EasyTierCorePath = file;
    }

    private void DetectEasyTier()
    {
        var found = EasyTierRunner.Locate(null, GameRoot, _svc.Paths.Root);
        if (found is null)
        {
            Status = "没找到 easytier-core。请从 https://easytier.cn 下载命令行版，解压到启动器目录后重试。";
            EasyTierCorePath = "";
            return;
        }
        EasyTierCorePath = found;
        Status = "已找到：" + found;
    }

    private static string? PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择游戏目录（.minecraft）" };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private static string? PickFile(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择文件", Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}

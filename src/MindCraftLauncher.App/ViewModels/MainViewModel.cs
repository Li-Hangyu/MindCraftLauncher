using System.IO;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Common;
using MindCraftLauncher.Core.Mods;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>主窗口的视图模型：装配八个页面的子 VM，并维护全局状态栏、主题与页面跳转。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public MainViewModel(LauncherServices services)
    {
        Services = services;

        // 顺序有讲究：Versions 先建，其余页面在构造时会读它的选中版本
        Versions = new VersionsViewModel(services, this);
        Launch = new LaunchViewModel(services, this);
        Download = new DownloadViewModel(services, this);
        Modpacks = new ModpackViewModel(services, this);
        Mods = new ModViewModel(services, this);
        ResourcePacks = new ClientPackViewModel(services, this, "resourcepack", ClientPackKind.ResourcePack, "资源包");
        Shaders = new ClientPackViewModel(services, this, "shader", ClientPackKind.ShaderPack, "光影包");
        Terracotta = new TerracottaViewModel(services, this);
        Redstone = new RedstoneViewModel(services, this);
        Settings = new SettingsViewModel(services, this);

        foreach (var viewModel in new ObservableObject[] { Launch, Download, Modpacks, Mods, ResourcePacks, Shaders })
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Busy" or "IsLoading" or "LoadingMessage" or "ProgressValue" or "IsProgressIndeterminate")
                    RefreshLoadingState();
            };

        // 注意：Settings 的构造函数里会给 _main.Theme 赋值，那时本类的属性还没装配完，
        // 所以 Theme 的 setter 里所有对子 VM 的访问都必须判空。

        NavigateCommand = new RelayCommand(p => NavigateTo(p as string ?? "launch"));
        ToggleThemeCommand = new RelayCommand(() => Theme = ThemeManager.IsLight(Theme) ? ThemeManager.Dark : ThemeManager.Light);

        // 切版本时让启动页的标题跟着变
        Versions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VersionsViewModel.SelectedId) or nameof(VersionsViewModel.SelectedText))
                Launch.NotifyVersionChanged();
        };

        StatusText = "就绪｜游戏目录：" + services.Config.GameRoot;
        _theme = ThemeManager.Normalize(services.Config.Theme);
    }

    public LauncherServices Services { get; }

    public VersionsViewModel Versions { get; }
    public LaunchViewModel Launch { get; }
    public DownloadViewModel Download { get; }
    public ModpackViewModel Modpacks { get; }
    public ModViewModel Mods { get; }
    public ClientPackViewModel ResourcePacks { get; }
    public ClientPackViewModel Shaders { get; }

    private bool _loadingOverlayDismissed;

    private bool HasLoadingWork => Launch.Busy || Download.IsLoading || Modpacks.Busy || Mods.Busy || ResourcePacks.Busy || Shaders.Busy;

    public bool IsLoading => HasLoadingWork && !_loadingOverlayDismissed;

    public string LoadingMessage => Launch.Busy ? Launch.LoadingMessage
        : Download.IsLoading ? Download.LoadingMessage
        : Modpacks.Busy ? Modpacks.LoadingMessage
        : Mods.Busy ? Mods.LoadingMessage
        : ResourcePacks.Busy ? ResourcePacks.LoadingMessage
        : Shaders.LoadingMessage;

    public void DismissLoadingOverlay()
    {
        if (!HasLoadingWork) return;
        _loadingOverlayDismissed = true;
        Raise(nameof(IsLoading));
    }

    private void RefreshLoadingState()
    {
        if (!HasLoadingWork) _loadingOverlayDismissed = false;
        Raise(nameof(IsLoading));
        Raise(nameof(LoadingMessage));
    }

    public TerracottaViewModel Terracotta { get; }
    public RedstoneViewModel Redstone { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>页面跳转命令，参数是页面 tag：launch/versions/download/modpacks/mods/terracotta/redstone/settings。</summary>
    public RelayCommand NavigateCommand { get; }

    /// <summary>导航栏底部的一键换肤。</summary>
    public RelayCommand ToggleThemeCommand { get; }

    /// <summary>窗口订阅这个事件来真正切页面。</summary>
    public event Action<string>? NavigateRequested;

    private string _currentPageTag = "launch";

    public void SetCurrentPage(string tag) => _currentPageTag = tag;

    public void NavigateTo(string tag)
    {
        if (string.Equals(tag, _currentPageTag, StringComparison.Ordinal)) return;
        NavigateRequested?.Invoke(tag);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private string _theme = ThemeManager.Dark;
    public string Theme
    {
        get => _theme;
        set
        {
            var normalized = ThemeManager.Normalize(value);
            if (!Set(ref _theme, normalized)) return;

            ThemeManager.Apply(normalized);
            Services.Config.Theme = normalized;
            Services.SaveConfig();

            Raise(nameof(ThemeLabel));
            Raise(nameof(ThemeGlyph));

            // 设置页的下拉框跟着走（反过来设置页改主题时，下面的相等判断会拦住递归）
            if (Settings is not null && !string.Equals(Settings.SelectedTheme, normalized, StringComparison.OrdinalIgnoreCase))
                Settings.SelectedTheme = normalized;

            // 状态灯用的是主题里的画刷，换肤后要重新取一次
            Terracotta?.RefreshThemeBrushes();
            Redstone?.RefreshThemeBrushes();
        }
    }

    public string ThemeLabel => ThemeManager.IsLight(Theme) ? "LIGHT" : "DARK";

    /// <summary>月亮 / 太阳切换用的字形（Segoe MDL2 Assets）。</summary>
    public string ThemeGlyph => ThemeManager.IsLight(Theme) ? "\uE706" : "\uE708";

    public void ReportStatus(string message) => StatusText = message;

    public void ReportError(string context, Exception error)
    {
        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}";
        var logPath = Path.Combine(GameLocation.LauncherDataRoot(), "logs", "background-errors.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, entry);
            ReportStatus($"{context}失败：{error.Message}（详细信息已记录到日志）");
        }
        catch (Exception logError)
        {
            System.Diagnostics.Debug.WriteLine($"[BackgroundError] {entry}{logError}");
            ReportStatus($"{context}失败：{error.Message}；错误日志写入失败：{logError.Message}");
        }
    }

    public void Dispose()
    {
        // 停掉陶瓦页的轮询定时器；组网进程由 LauncherServices.Dispose 收尾
        Terracotta?.Dispose();
    }
}

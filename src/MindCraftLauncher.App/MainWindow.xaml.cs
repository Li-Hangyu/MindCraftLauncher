using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using MindCraftLauncher.App.ViewModels;

namespace MindCraftLauncher.App;

public partial class MainWindow : Window
{
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        // 修掉的 bug：原来 XAML 里直接 IsChecked="True" 会在 InitializeComponent 期间触发
        // Checked，那时 PageXxx 这些字段还没赋值，会空引用崩溃。现在等控件都建好再选中。
        _ready = true;
        NavLaunch.IsChecked = true;

        StateChanged += (_, _) => ApplyWindowState();

        Loaded += (_, _) =>
        {
            ApplyWindowState();
            ThemeManager.Apply((DataContext as MainViewModel)?.Theme);
            if (DataContext is MainViewModel vm)
                vm.NavigateRequested += ShowPage;
        };

        // 释放陶瓦状态轮询定时器，避免窗口关掉后 DispatcherTimer 还活着
        Closed += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }

    /// <summary>自定义外框的最大化补偿：WindowChrome 在最大化时会给窗口留出系统边框宽度。</summary>
    private void ApplyWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        Shell.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel vm || !vm.IsLoading) return;
        if (vm.Launch.Busy) vm.Launch.CancelStartupCommand.Execute(null);
        else vm.Download.CancelLoaderDetection();
        vm.DismissLoadingOverlay();
        e.Handled = true;
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not RadioButton { Tag: string tag } btn) return;
        if (btn.IsChecked != true) return;
        SwitchTo(tag);
    }

    /// <summary>页面跳转（供各页面的"去某某页"按钮调用）。同时把左侧导航选中态同步过去。</summary>
    private void ShowPage(string tag)
    {
        var nav = tag switch
        {
            "launch" => NavLaunch,
            "versions" => NavVersions,
            "download" or "modpacks" or "mods" or "resourcepacks" or "shaders" => NavDownload,
            "labymod" => NavLabyMod,
            "terracotta" => NavTerracotta,
            "redstone" => NavRedstone,
            "settings" => NavSettings,
            _ => NavLaunch,
        };
        nav.IsChecked = true;
        SwitchTo(tag);
    }

    private void SwitchTo(string tag)
    {
        if (PageLaunch is null) return;   // 初始化期间被回调时的保护
        (DataContext as MainViewModel)?.SetCurrentPage(tag);

        PageLaunch.Visibility = Visibility.Collapsed;
        PageVersions.Visibility = Visibility.Collapsed;
        PageDownload.Visibility = Visibility.Collapsed;
        PageLabyMod.Visibility = Visibility.Collapsed;
        PagePacks.Visibility = Visibility.Collapsed;
        PageMods.Visibility = Visibility.Collapsed;
        PageResourcePacks.Visibility = Visibility.Collapsed;
        PageShaders.Visibility = Visibility.Collapsed;
        PageTerracotta.Visibility = Visibility.Collapsed;
        PageRedstone.Visibility = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;

        UIElement target = tag switch
        {
            "launch" => PageLaunch,
            "versions" => PageVersions,
            "download" => PageDownload,
            "labymod" => PageLabyMod,
            "modpacks" => PagePacks,
            "mods" => PageMods,
            "resourcepacks" => PageResourcePacks,
            "shaders" => PageShaders,
            "terracotta" => PageTerracotta,
            "redstone" => PageRedstone,
            "settings" => PageSettings,
            _ => PageLaunch,
        };
        target.Visibility = Visibility.Visible;
        AnimateIn(target);
    }

    /// <summary>切页淡入 + 轻微上浮，纯视觉，不参与布局。</summary>
    private static void AnimateIn(UIElement target)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(210);

        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = 0;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        var offset = new TranslateTransform(0, 12);
        target.RenderTransform = offset;
        offset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }
}

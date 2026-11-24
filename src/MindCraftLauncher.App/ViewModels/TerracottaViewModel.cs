using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.EasyTier;
using MindCraftLauncher.Core.Terracotta;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>
/// 陶瓦联机箱体：真实调用 easytier-core 组网，读取 easytier-cli 状态，
/// 再把「虚拟IP:局域网端口」交给游戏直连。
/// </summary>
public sealed class TerracottaViewModel : ObservableObject, IDisposable
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public TerracottaViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        Peers = new ObservableCollection<EasyTierPeer>();

        NetworkName = "mc-" + Environment.MachineName.ToLowerInvariant().Replace(" ", "-");
        NetworkSecret = NewSecret();
        LanPortText = "25565";
        PublicPeer = services.Config.EasyTierPublicPeer;
        IsHost = true;

        StartCommand = new RelayCommand(StartHost, () => !Running);
        JoinCommand = new RelayCommand(JoinRoom, () => !Running && JoinCode.Trim().Length > 0);
        StopCommand = new RelayCommand(Stop, () => Running);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CopyCodeCommand = new RelayCommand(() => Copy(Code), () => Code.Length > 0);
        CopyAddressCommand = new RelayCommand(() => Copy(Address), () => Address.Length > 0);
        NewSecretCommand = new RelayCommand(() => NetworkSecret = NewSecret());
        LaunchDirectCommand = new RelayCommand(LaunchDirect, () => Address.Length > 0);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => { if (Running && !_disposed) await RefreshAsync(); };
        _timer.Start();

        EasyTierMissing = string.IsNullOrEmpty(services.EasyTier.CoreExecutable);
        LinkShort = EasyTierMissing ? "陶瓦：未找到 easytier-core" : "陶瓦：未组网";
    }

    public ObservableCollection<EasyTierPeer> Peers { get; }

    private bool _isHost = true;
    public bool IsHost
    {
        get => _isHost;
        set { if (Set(ref _isHost, value)) Raise(nameof(IsJoiner)); }
    }
    public bool IsJoiner { get => !IsHost; set => IsHost = !value; }

    private string _networkName = "";
    public string NetworkName { get => _networkName; set => Set(ref _networkName, value); }

    private string _networkSecret = "";
    public string NetworkSecret { get => _networkSecret; set => Set(ref _networkSecret, value); }

    private string _lanPortText = "";
    public string LanPortText { get => _lanPortText; set => Set(ref _lanPortText, value); }

    private string _publicPeer = "";
    public string PublicPeer { get => _publicPeer; set { if (Set(ref _publicPeer, value)) { _svc.Config.EasyTierPublicPeer = value; _svc.SaveConfig(); } } }

    private string _joinCode = "";
    public string JoinCode { get => _joinCode; set { if (Set(ref _joinCode, value)) Raise(nameof(CanJoin)); } }

    // ✅ 修复点：将 private set 改为 set，允许 XAML 双向绑定
    private string _code = "";
    public string Code { get => _code; set => Set(ref _code, value); }

    private string _address = "";
    public string Address { get => _address; private set => Set(ref _address, value); }

    private string _virtualIp = "";
    public string VirtualIp { get => _virtualIp; private set => Set(ref _virtualIp, value); }

    // ✅ 优化：Log 属性也建议开放 set，方便用户手动清空或编辑
    private string _log = "";
    public string Log { get => _log; set => Set(ref _log, value); }

    private bool _running;
    public bool Running
    {
        get => _running;
        private set
        {
            if (!Set(ref _running, value)) return;
            LinkShort = value ? "陶瓦：组网中" : "陶瓦：未组网";
            LinkBrush = value ? ThemeBrush("BrushOk", Brushes.SeaGreen) : ThemeBrush("BrushTextMute", Brushes.Gray);
            Raise(nameof(NotRunning));
        }
    }

    public bool NotRunning => !Running;

    private string _linkShort = "陶瓦：未组网";
    public string LinkShort { get => _linkShort; private set => Set(ref _linkShort, value); }

    private System.Windows.Media.Brush _linkBrush = System.Windows.Media.Brushes.Gray;
    public System.Windows.Media.Brush LinkBrush { get => _linkBrush; private set => Set(ref _linkBrush, value); }

    private bool _easyTierMissing;
    public bool EasyTierMissing { get => _easyTierMissing; set => Set(ref _easyTierMissing, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand JoinCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand CopyAddressCommand { get; }
    public RelayCommand NewSecretCommand { get; }
    public RelayCommand LaunchDirectCommand { get; }

    public bool CanJoin => !Running && JoinCode.Trim().Length > 0;

    private void StartHost()
    {
        try
        {
            EnsureEasyTier();
            var port = ParsePort(LanPortText);
            var state = _svc.Terracotta.StartHost(NetworkName, NetworkSecret, port, PublicPeer);
            Code = state.RoomCode;
            Running = true;
            Log = "已开房，把下面的连接码发给队友：\n" + Code;
            _main.ReportStatus("陶瓦联机：已开房，端口 " + port);
            _ = RefreshAsync();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void JoinRoom()
    {
        try
        {
            EnsureEasyTier();
            var state = _svc.Terracotta.Join(JoinCode, PublicPeer);
            Code = state.RoomCode;
            Running = true;
            Log = "正在入网…入网成功后地址栏会自动填上房主地址";
            _main.ReportStatus("陶瓦联机：正在入网");
            _ = RefreshAsync();
        }
        catch (Exception ex) { Fail(ex); }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var state = await _svc.Terracotta.RefreshAsync().ConfigureAwait(true);
            VirtualIp = state.VirtualIpv4;
            Address = state.ConnectionAddress;

            var sameSet = state.Peers.Count == Peers.Count;
            Peers.Clear();
            foreach (var p in state.Peers) Peers.Add(p);
            if (!sameSet) Raise(nameof(Peers));

            Running = state.Running;
            Log = state.Log;
        }
        catch (Exception ex)
        {
            Log = "状态读取失败：" + ex.Message;
        }
    }

    private void Stop()
    {
        _svc.Terracotta.Stop();
        Running = false;
        Address = "";
        Peers.Clear();
        Log = "已停止组网。";
        _main.ReportStatus("陶瓦联机：已停止");
    }

    private void LaunchDirect()
    {
        if (!Core.Common.Ipv4.TryParseAddress(Address, out var host, out var port))
        {
            MessageBox.Show("连接地址还不可用。请确认已入网，且房主在游戏里点了「对局域网开放」。", "陶瓦联机",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _main.Launch.PendingServerAddress = host;
        _main.Launch.PendingServerPort = port;
        _main.ReportStatus($"已设置直连：{host}:{port}，点右上角「启动游戏」即可进入");
    }

    private void EnsureEasyTier()
    {
        _svc.EasyTier.Configure(_svc.Config.EasyTierCorePath, _svc.Config.EasyTierCliPath);
        if (string.IsNullOrEmpty(_svc.EasyTier.CoreExecutable))
            throw new EasyTierNotInstalledException();
        EasyTierMissing = false;
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse((text ?? "").Trim(), out var port))
            throw new ArgumentException("端口号要填游戏聊天框里提示的那串数字，例如 51917");
        return port;
    }

    private void Fail(Exception ex)
    {
        Log = ex.Message;
        LinkShort = "陶瓦：异常";
        LinkBrush = ThemeBrush("BrushErr", Brushes.IndianRed);
        Running = ex is EasyTierNotInstalledException ? false : _svc.Terracotta.IsRunning;
        _main.ReportStatus("陶瓦联机：" + ex.Message);

        if (ex is FeatureDisabledException or EasyTierNotInstalledException or ArgumentException or FormatException)
            MessageBox.Show(ex.Message, "陶瓦联机", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(ex.Message + "\n\n如果是虚拟网卡创建失败，请关闭组网后以管理员身份重新启动启动器。",
                "陶瓦联机", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string NewSecret()
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rnd = new Random();
        return new string(Enumerable.Range(0, 12).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); _main.ReportStatus("已复制到剪贴板"); } catch { }
    }

    /// <summary>从当前主题里取画刷；主题里没有这个键时退回固定色，保证不会因为缺资源炸掉。</summary>
    private static Brush ThemeBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>换肤后重新给状态灯上色（画刷是单例对象，不会自动跟着 DynamicResource 变）。</summary>
    public void RefreshThemeBrushes()
    {
        LinkBrush = Running ? ThemeBrush("BrushOk", Brushes.SeaGreen) : ThemeBrush("BrushTextMute", Brushes.Gray);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
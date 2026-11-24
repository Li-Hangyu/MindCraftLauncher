using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Accounts;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Yggdrasil;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>
/// 红石联机箱体：Yggdrasil 外置登录（皮肤站账号认证）+ 启动器侧服务器列表。
/// 这一套不做组网，认证走 authlib-injector 注入，服务器地址由列表提供。
/// </summary>
public sealed class RedstoneViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;

    public RedstoneViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        AuthServer = services.Config.RedstoneAuthServer ?? "";
        Accounts = new ObservableCollection<LauncherAccount>(
            services.Accounts.All.Where(a => a.Kind == AccountKind.Yggdrasil));
        Servers = new ObservableCollection<ServerEntry>(services.Servers.All);

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => Accounts.Count >= 0);
        TestServerCommand = new AsyncRelayCommand(TestAsync);
        RemoveAccountCommand = new RelayCommand(RemoveSelectedAccount, () => SelectedAccount is not null);
        AddServerCommand = new AsyncRelayCommand(AddServerAsync);
        RemoveServerCommand = new RelayCommand(RemoveSelectedServer, () => SelectedServer is not null);
        DirectJoinCommand = new RelayCommand(DirectJoin, () => SelectedServer is not null);
        UpdateInjectorCommand = new AsyncRelayCommand(UpdateInjectorAsync);

        AccountShort = Accounts.Count > 0 ? "红石：" + Accounts[0].PlayerName : "红石：未登录";
        InjectorStatus = File.Exists(services.AuthlibInjector.LocalJarPath)
            ? "authlib-injector 已缓存"
            : "authlib-injector 未下载（首次登录时自动获取）";
    }

    public ObservableCollection<LauncherAccount> Accounts { get; }
    public ObservableCollection<ServerEntry> Servers { get; }

    private string _authServer = "";
    public string AuthServer
    {
        get => _authServer;
        set
        {
            if (Set(ref _authServer, value))
            {
                _svc.Config.RedstoneAuthServer = value;
                _svc.SaveConfig();
            }
        }
    }

    private string _userName = "";
    public string UserName { get => _userName; set => Set(ref _userName, value); }

    private string _password = "";
    public string Password { get => _password; set => Set(ref _password, value); }

    private LauncherAccount? _selectedAccount;
    public LauncherAccount? SelectedAccount { get => _selectedAccount; set => Set(ref _selectedAccount, value); }

    private ServerEntry? _selectedServer;
    public ServerEntry? SelectedServer { get => _selectedServer; set => Set(ref _selectedServer, value); }

    private string _newServerName = "";
    public string NewServerName { get => _newServerName; set => Set(ref _newServerName, value); }

    private string _newServerAddress = "";
    public string NewServerAddress { get => _newServerAddress; set => Set(ref _newServerAddress, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _serverName = "";
    public string ServerName { get => _serverName; private set => Set(ref _serverName, value); }

    private string _accountShort = "红石：未登录";
    public string AccountShort { get => _accountShort; private set => Set(ref _accountShort, value); }

    private System.Windows.Media.Brush _accountBrush = System.Windows.Media.Brushes.Gray;
    public System.Windows.Media.Brush AccountBrush { get => _accountBrush; private set => Set(ref _accountBrush, value); }

    private string _injectorStatus = "";
    public string InjectorStatus { get => _injectorStatus; private set => Set(ref _injectorStatus, value); }

    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand TestServerCommand { get; }
    public RelayCommand RemoveAccountCommand { get; }
    public AsyncRelayCommand AddServerCommand { get; }
    public RelayCommand RemoveServerCommand { get; }
    public RelayCommand DirectJoinCommand { get; }
    public AsyncRelayCommand UpdateInjectorCommand { get; }

    private async Task TestAsync()
    {
        try
        {
            var client = new YggdrasilClient(new HttpFetcher(), AuthServer);
            var info = await client.GetServerInfoAsync().ConfigureAwait(true);
            ServerName = info?.ServerName ?? "";
            Status = info is null
                ? "该服务器没有返回自检信息（可能未实现 serverInfo），但仍可尝试登录"
                : $"服务器名称：{info.ServerName}｜皮肤域：{(info.SkinDomains.Count > 0 ? string.Join(", ", info.SkinDomains) : "未声明")}";
        }
        catch (Exception ex)
        {
            Status = "连接失败：" + ex.Message;
        }
    }

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(AuthServer))
        {
            Status = "请先填写认证服务器地址";
            return;
        }
        try
        {
            // 先确保注入器就绪，避免登录成功却起不了游戏
            InjectorStatus = "正在准备 authlib-injector…";
            var jar = await _svc.AuthlibInjector.EnsureJarAsync(false).ConfigureAwait(true);
            InjectorStatus = "authlib-injector 就绪：" + Path.GetFileName(jar);

            using var client = new YggdrasilClient(new HttpFetcher(), AuthServer);
            var session = await client.AuthenticateAsync(UserName, Password).ConfigureAwait(true);
            var account = LauncherAccount.FromYggdrasil(AuthServer, session, UserName);
            _svc.Accounts.Add(account);

            Accounts.Clear();
            foreach (var a in _svc.Accounts.All.Where(x => x.Kind == AccountKind.Yggdrasil)) Accounts.Add(a);
            SelectedAccount = account;

            AccountShort = "红石：" + account.PlayerName;
            AccountBrush = ThemeBrush("BrushOk", System.Windows.Media.Brushes.SeaGreen);
            Status = $"登录成功：{account.PlayerName}｜UUID {account.Uuid}";
            Password = "";
            _main.Launch.ReloadAccounts();
            _main.ReportStatus("红石联机：已登录 " + account.PlayerName);
        }
        catch (Exception ex)
        {
            AccountBrush = ThemeBrush("BrushErr", System.Windows.Media.Brushes.IndianRed);
            Status = "登录失败：" + ex.Message;
        }
    }

    private void RemoveSelectedAccount()
    {
        if (SelectedAccount is null) return;
        _svc.Accounts.Remove(SelectedAccount.Id);
        Accounts.Remove(SelectedAccount);
        SelectedAccount = Accounts.FirstOrDefault();
        AccountShort = Accounts.Count > 0 ? "红石：" + Accounts[0].PlayerName : "红石：未登录";
        AccountBrush = Accounts.Count > 0 ? ThemeBrush("BrushOk", System.Windows.Media.Brushes.SeaGreen) : ThemeBrush("BrushTextMute", System.Windows.Media.Brushes.Gray);
        _main.Launch.ReloadAccounts();
    }

    private Task AddServerAsync()
    {
        try
        {
            var entry = ServerListStore.Parse(NewServerName, NewServerAddress);
            _svc.Servers.Add(entry);
            Servers.Add(entry);
            SelectedServer = entry;
            NewServerName = "";
            NewServerAddress = "";
            Status = "已添加 " + entry.HostPort;
        }
        catch (Exception ex)
        {
            Status = "添加失败：" + ex.Message;
        }
        return Task.CompletedTask;
    }

    private void RemoveSelectedServer()
    {
        if (SelectedServer is null) return;
        _svc.Servers.Remove(SelectedServer.Id);
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.FirstOrDefault();
    }

    private void DirectJoin()
    {
        if (SelectedServer is null) return;

        SelectedAccount ??= Accounts.FirstOrDefault();

        _main.Launch.PendingServerAddress = SelectedServer.Address;
        _main.Launch.PendingServerPort = SelectedServer.Port;
        _svc.Servers.Touch(SelectedServer.Id);
        _main.ReportStatus($"红石直连：{SelectedServer.HostPort}（{SelectedServer.Name}），点右上角启动游戏");
        Status = $"已设置直连目标 {SelectedServer.HostPort}";
    }

    private static System.Windows.Media.Brush ThemeBrush(string key, System.Windows.Media.Brush fallback) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

    /// <summary>换肤后重新给账号状态灯上色。</summary>
    public void RefreshThemeBrushes() =>
        AccountBrush = Accounts.Count > 0
            ? ThemeBrush("BrushOk", System.Windows.Media.Brushes.SeaGreen)
            : ThemeBrush("BrushTextMute", System.Windows.Media.Brushes.Gray);

    private async Task UpdateInjectorAsync()
    {
        try
        {
            InjectorStatus = "正在更新 authlib-injector…";
            var meta = await _svc.AuthlibInjector.FetchLatestAsync().ConfigureAwait(true);
            var jar = await _svc.AuthlibInjector.EnsureJarAsync(true).ConfigureAwait(true);
            InjectorStatus = $"已更新到 {meta.Version}（build {meta.BuildNumber}）→ {jar}";
        }
        catch (Exception ex)
        {
            InjectorStatus = "更新失败：" + ex.Message;
        }
    }
}

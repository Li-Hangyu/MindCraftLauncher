using System.Collections.ObjectModel;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Clients;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>下载页：Minecraft 版本本体 + LabyMod 客户端。</summary>
public sealed class DownloadViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;
    private List<VersionManifest.VersionEntry> _all = new();

    public DownloadViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        Versions = new ObservableCollection<VersionRow>();
        ReleaseVersions = new ObservableCollection<VersionRow>();
        TestVersions = new ObservableCollection<VersionRow>();
        LabyChannels = new ObservableCollection<string>();
        LabyVersions = new ObservableCollection<LabyRow>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedRow is not null && !Busy);
        OpenVersionCommand = new RelayCommand(p => OpenVersion(p as VersionRow));
        BackToVersionsCommand = new RelayCommand(() =>
        {
            CancelLoaderDetection();
            IsDetailVisible = false;
        });
        RefreshLabyCommand = new AsyncRelayCommand(RefreshLabyAsync);
        InstallLabyCommand = new AsyncRelayCommand(InstallLabyAsync, () => SelectedLabyRow is not null && !Busy);
        CancelInstallCommand = new RelayCommand(() => { _cts?.Cancel(); Status = "正在取消…"; }, () => Busy);
        PickVersionCommand = new RelayCommand(p => OpenVersion(p as VersionRow));

        LoaderKinds = new ObservableCollection<string>(new[] { ModLoaderKind.Vanilla }.Concat(ModLoaderKind.All));
        LoaderOptions = new ObservableCollection<LoaderAvailabilityRow>(LoaderKinds.Select(kind => new LoaderAvailabilityRow(
            kind, kind == ModLoaderKind.Vanilla ? "原版无需加载器" : "待检测")));
        LoaderVersions = new ObservableCollection<ModLoaderVersion>();
        SelectedLoaderKind = ModLoaderKind.Vanilla;
        SelectLoaderKindCommand = new RelayCommand(p =>
        {
            if (p is string kind && LoaderKinds.Contains(kind)) SelectedLoaderKind = kind;
        });
        RefreshLoadersCommand = new AsyncRelayCommand(RefreshLoadersAsync, () => LoaderGameVersion.Trim().Length > 0 && !Busy);
        InstallLoaderCommand = new AsyncRelayCommand(InstallLoaderAsync,
            () => (SelectedLoaderKind == ModLoaderKind.Vanilla || SelectedLoader is not null) && !Busy);
        LoaderGameVersion = _svc.Installed.List().Select(v => v.Id).FirstOrDefault(v => !v.Contains('-')) ?? "";

        DownloadSources = new ObservableCollection<IDownloadSource>(services.Sources.All);
        SelectedSource = DownloadSources.FirstOrDefault(s => s.Id == services.Config.DownloadSourceId) ?? DownloadSources.FirstOrDefault();

        GameDownloadEnabled = services.Config.Flags.GameDownloadEnabled;
        AcknowledgeCompliance = services.Config.Flags.DownloadAcknowledgedAt is not null;

        _ = RefreshAsync();
        _ = RefreshLabyAsync();
    }

    public ObservableCollection<VersionRow> Versions { get; }
    public ObservableCollection<VersionRow> ReleaseVersions { get; }
    public ObservableCollection<VersionRow> TestVersions { get; }
    public ObservableCollection<IDownloadSource> DownloadSources { get; }
    public ObservableCollection<string> LabyChannels { get; }
    public ObservableCollection<LabyRow> LabyVersions { get; }

    private IDownloadSource? _selectedSource;
    public IDownloadSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (Set(ref _selectedSource, value) && value is not null)
            {
                _svc.Config.DownloadSourceId = value.Id;
                _svc.SaveConfig();
            }
        }
    }

    private VersionRow? _selectedRow;
    public VersionRow? SelectedRow { get => _selectedRow; set => Set(ref _selectedRow, value); }

    private VersionRow? _detailVersion;
    public VersionRow? DetailVersion { get => _detailVersion; private set => Set(ref _detailVersion, value); }

    private bool _isDetailVisible;
    public bool IsDetailVisible { get => _isDetailVisible; private set => Set(ref _isDetailVisible, value); }

    private LabyRow? _selectedLabyRow;
    public LabyRow? SelectedLabyRow { get => _selectedLabyRow; set => Set(ref _selectedLabyRow, value); }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) ApplyFilter(); }
    }

    private bool _busy;
    private int _loadingCount;
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(NotBusy));
            Raise(nameof(ProgressValue));
            Raise(nameof(IsLoading));
        }
    }

    public bool NotBusy => !Busy;
    public bool IsLoading => Busy || _loadingCount > 0;

    private string _loadingMessage = "请稍候…";
    public string LoadingMessage { get => _loadingMessage; private set => Set(ref _loadingMessage, value); }

    private IDisposable BeginLoading(string message)
    {
        _loadingCount++;
        LoadingMessage = message;
        Raise(nameof(IsLoading));
        return new LoadingOperation(() =>
        {
            _loadingCount--;
            Raise(nameof(IsLoading));
        });
    }

    private sealed class LoadingOperation(Action complete) : IDisposable
    {
        private Action? _complete = complete;
        public void Dispose() => Interlocked.Exchange(ref _complete, null)?.Invoke();
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => Set(ref _progressValue, value);
    }

    private string _status = "加载中…";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _gameDownloadEnabled;
    public bool GameDownloadEnabled
    {
        get => _gameDownloadEnabled;
        set
        {
            if (!Set(ref _gameDownloadEnabled, value)) return;
            _svc.Config.Flags.GameDownloadEnabled = value;
            if (!value) { AcknowledgeCompliance = false; }
            _svc.SaveConfig();
            Raise(nameof(ComplianceVisible));
        }
    }

    /// <summary>打开本体下载时要勾的那一句确认。</summary>
    public bool ComplianceVisible => GameDownloadEnabled && !AcknowledgeCompliance;

    private bool _acknowledgeCompliance;
    public bool AcknowledgeCompliance
    {
        get => _acknowledgeCompliance;
        set
        {
            if (!Set(ref _acknowledgeCompliance, value)) return;
            _svc.Config.Flags.DownloadAcknowledgedAt = value ? DateTime.Now : null;
            _svc.SaveConfig();
            Raise(nameof(ComplianceVisible));
        }
    }

    private string? _labyChannel;
    public string? LabyChannel
    {
        get => _labyChannel;
        set { if (Set(ref _labyChannel, value)) _ = RefreshLabyAsync(); }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand OpenVersionCommand { get; }
    public RelayCommand BackToVersionsCommand { get; }
    public AsyncRelayCommand RefreshLabyCommand { get; }
    public AsyncRelayCommand InstallLabyCommand { get; }
    public RelayCommand CancelInstallCommand { get; }
    public RelayCommand PickVersionCommand { get; }

    public ObservableCollection<string> LoaderKinds { get; }
    public ObservableCollection<LoaderAvailabilityRow> LoaderOptions { get; }
    public ObservableCollection<ModLoaderVersion> LoaderVersions { get; }

    private readonly Dictionary<string, List<ModLoaderVersion>> _loaderVersionsByKind = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loaderDetectionCts;
    public bool IsDetectingLoaders => _loaderDetectionCts is not null;

    private string _selectedLoaderKind = ModLoaderKind.Fabric;
    public string SelectedLoaderKind
    {
        get => _selectedLoaderKind;
        set
        {
            if (!Set(ref _selectedLoaderKind, value)) return;
            LoaderVersions.Clear();
            SelectedLoader = null;
            LoaderStatus = value switch
            {
                ModLoaderKind.Vanilla => "原版无需安装加载器。",
                ModLoaderKind.Forge =>
                    "Forge 安装器需要本机 Java（1.17+ 要 Java 17，1.20.5+ 要 Java 21）。",
                ModLoaderKind.NeoForge => "NeoForge 安装需要 Java 21。",
                ModLoaderKind.OptiFine =>
                    "OptiFine 使用 BMCLAPI 获取安装文件；已装 Forge 时放入 mods，原版则尝试创建独立版本。",
                _ => "Fabric 无需本机运行安装器，可直接获取官方版本描述。",
            };
            Raise(nameof(IsForge));
            Raise(nameof(ShowLoaderOptions));
            Raise(nameof(InstallActionLabel));
            if (IsDetailVisible && value != ModLoaderKind.Vanilla)
            {
                if (_loaderVersionsByKind.TryGetValue(value, out var cached))
                    ShowLoaderVersions(value, cached);
                else
                    _ = RefreshLoadersAsync();
            }
        }
    }

    public bool IsForge => SelectedLoaderKind == ModLoaderKind.Forge;
    public bool ShowLoaderOptions => SelectedLoaderKind != ModLoaderKind.Vanilla;
    public string InstallActionLabel => SelectedLoaderKind == ModLoaderKind.Vanilla
        ? "安装原版"
        : "安装所选加载器";

    private string _loaderGameVersion = "";
    public string LoaderGameVersion
    {
        get => _loaderGameVersion;
        set
        {
            if (!Set(ref _loaderGameVersion, value)) return;
            LoaderVersions.Clear();
            _loaderVersionsByKind.Clear();
            foreach (var option in LoaderOptions.Where(option => option.Kind != ModLoaderKind.Vanilla))
                option.Availability = "待检测";
        }
    }

    private ModLoaderVersion? _selectedLoader;
    public ModLoaderVersion? SelectedLoader { get => _selectedLoader; set => Set(ref _selectedLoader, value); }

    private string _loaderStatus = "";
    public string LoaderStatus { get => _loaderStatus; set => Set(ref _loaderStatus, value); }

    public RelayCommand SelectLoaderKindCommand { get; }
    public AsyncRelayCommand RefreshLoadersCommand { get; }
    public AsyncRelayCommand InstallLoaderCommand { get; }

    private async Task RefreshLoadersAsync()
    {
        var game = LoaderGameVersion.Trim();
        if (game.Length == 0) { LoaderStatus = "请先填要装加载器的游戏版本，例如 1.20.1"; return; }

        if (SelectedLoaderKind == ModLoaderKind.Vanilla)
        {
            LoaderStatus = "原版无需安装加载器。";
            return;
        }

        LoaderStatus = $"读取 {SelectedLoaderKind} 列表…";
        using var loading = BeginLoading(LoaderStatus);
        try
        {
            var list = await _svc.ModLoaders.ListAsync(SelectedLoaderKind, game).ConfigureAwait(true);
            _loaderVersionsByKind[SelectedLoaderKind] = list;
            UpdateLoaderAvailability(SelectedLoaderKind, list.Count == 0 ? "无" : $"{list.Count} 个版本");
            ShowLoaderVersions(SelectedLoaderKind, list);
        }
        catch (FeatureDisabledException ex)
        {
            LoaderStatus = ex.Message;
            UpdateLoaderAvailability(SelectedLoaderKind, "不可用");
        }
        catch (Exception ex)
        {
            LoaderStatus = "读取失败：" + ex.Message;
            UpdateLoaderAvailability(SelectedLoaderKind, "检测失败");
            _main.ReportError("读取加载器版本", ex);
        }
    }

    private void ShowLoaderVersions(string kind, IReadOnlyList<ModLoaderVersion> versions)
    {
        if (!string.Equals(kind, SelectedLoaderKind, StringComparison.OrdinalIgnoreCase)) return;
        LoaderVersions.Clear();
        foreach (var version in versions) LoaderVersions.Add(version);
        SelectedLoader = LoaderVersions.FirstOrDefault(x => x.Note.Length > 0) ?? LoaderVersions.FirstOrDefault();
        LoaderStatus = versions.Count == 0 ? $"{LoaderGameVersion} 没有可用的 {kind} 版本" : $"{kind} 共 {versions.Count} 个版本可选";
    }

    private void UpdateLoaderAvailability(string kind, string availability)
    {
        var option = LoaderOptions.FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (option is not null) option.Availability = availability;
    }

    private async Task DetectLoaderVersionsAsync(string gameVersion)
    {
        _loaderDetectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loaderDetectionCts = cts;
        Raise(nameof(IsDetectingLoaders));
        _loaderVersionsByKind.Clear();
        foreach (var kind in ModLoaderKind.All) UpdateLoaderAvailability(kind, "检测中…");
        LoaderStatus = $"正在检查 {gameVersion} 的加载器版本…";
        using var loading = BeginLoading($"正在检查 Minecraft {gameVersion} 的加载器版本…");

        try
        {
            var checks = ModLoaderKind.All.Select(async kind =>
            {
                try
                {
                    var versions = await _svc.ModLoaders.ListAsync(kind, gameVersion, cts.Token).ConfigureAwait(false);
                    return new LoaderCheckResult(kind, versions, null);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return new LoaderCheckResult(kind, null, null);
                }
                catch (Exception ex)
                {
                    return new LoaderCheckResult(kind, null, ex);
                }
            });
            var results = await Task.WhenAll(checks).ConfigureAwait(true);
            if (cts.IsCancellationRequested || DetailVersion?.Id != gameVersion) return;

            foreach (var result in results)
            {
                if (result.Error is not null)
                {
                    UpdateLoaderAvailability(result.Kind, "检测失败");
                    _main.ReportError($"检测 {result.Kind} 版本", result.Error);
                    continue;
                }

                var versions = result.Versions ?? new List<ModLoaderVersion>();
                _loaderVersionsByKind[result.Kind] = versions;
                UpdateLoaderAvailability(result.Kind, versions.Count == 0 ? "无" : $"{versions.Count} 个版本");
            }
            LoaderStatus = "加载器检测完成。选择有可用版本的加载器继续。";
        }
        finally
        {
            if (ReferenceEquals(_loaderDetectionCts, cts))
            {
                _loaderDetectionCts = null;
                Raise(nameof(IsDetectingLoaders));
            }
            cts.Dispose();
        }
    }

    public bool CancelLoaderDetection()
    {
        if (_loaderDetectionCts is null) return false;
        _loaderDetectionCts.Cancel();
        LoaderStatus = "加载器检测已取消。";
        foreach (var option in LoaderOptions.Where(option => option.Kind != ModLoaderKind.Vanilla && option.Availability == "检测中…"))
            option.Availability = "已取消";
        return true;
    }

    private sealed record LoaderCheckResult(string Kind, List<ModLoaderVersion>? Versions, Exception? Error);

    private async Task InstallLoaderAsync()
    {
        var selectedVersion = DetailVersion;
        if (selectedVersion is null) return;
        var target = SelectedLoader;
        if (SelectedLoaderKind != ModLoaderKind.Vanilla && target is null) return;

        Busy = true;
        LoadingMessage = $"正在安装 Minecraft {selectedVersion.Id}…";
        ProgressValue = 0;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                ProgressValue = p.Total > 0 ? Math.Clamp(p.Current * 100.0 / p.Total, 0, 100) : ProgressValue;
                LoaderStatus = $"[{SelectedLoaderKind}/{p.Phase}] {p.Label}";
                LoadingMessage = LoaderStatus;
                _main.ReportStatus("安装中：" + p.Label);
            });

            string newId;
            if (SelectedLoaderKind == ModLoaderKind.Vanilla)
            {
                await _svc.Versions.InstallAsync(selectedVersion.Id, progress, _cts.Token).ConfigureAwait(true);
                newId = selectedVersion.Id;
                selectedVersion.Installed = true;
            }
            else
            {
                newId = await _svc.ModLoaders.InstallAsync(target!.Kind, target.GameVersion, target.LoaderVersion,
                    progress, _cts.Token).ConfigureAwait(true);
            }
            ProgressValue = 100;
            LoaderStatus = SelectedLoaderKind == ModLoaderKind.Vanilla
                ? $"原版 {newId} 已安装完成。"
                : $"{target!.Kind} {target.LoaderVersion} 已装好，版本：{newId}";
            _main.Versions.Refresh();
            _main.Launch.ReloadVersions();
            _main.ReportStatus(newId + " 已安装");
        }
        catch (FeatureDisabledException ex) { LoaderStatus = ex.Message; }
        catch (OperationCanceledException) { LoaderStatus = "安装已取消。"; }
        catch (Exception ex)
        {
            LoaderStatus = "安装失败：" + ex.Message;
            _main.ReportError("安装游戏版本或加载器", ex);
        }
        finally
        {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private CancellationTokenSource? _cts;

    public async Task RefreshAsync()
    {
        using var loading = BeginLoading("正在读取 Minecraft 版本清单…");
        try
        {
            Status = "读取版本清单…";
            var list = await _svc.Versions.ListVersionsAsync().ConfigureAwait(true);
            _all = list.ToList();
            ApplyFilter();
            var latestRelease = list.FirstOrDefault(v => v.Type == "release")?.Id ?? "未知";
            var latestTest = list.FirstOrDefault(v => v.Type != "release")?.Id ?? "无";
            Status = $"正式版 {list.Count(v => v.Type == "release")} 个；最新测试版 {latestTest}；最新正式版 {latestRelease}";
        }
        catch (Exception ex)
        {
            Status = "清单读取失败：" + ex.Message;
            _main.ReportError("后台检查 Minecraft 版本清单", ex);
        }
    }

    private void ApplyFilter()
    {
        var q = (Filter ?? "").Trim();
        var releaseQuery = _all.Where(v => v.Type == "release");
        if (q.Length > 0 && !q.Equals("release", StringComparison.OrdinalIgnoreCase)
            && !q.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
            releaseQuery = releaseQuery.Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase));

        var latestTest = _all.FirstOrDefault(v => v.Type != "release");
        var showLatestTest = latestTest is not null &&
            (q.Length == 0 || q.Equals("snapshot", StringComparison.OrdinalIgnoreCase)
             || (!q.Equals("release", StringComparison.OrdinalIgnoreCase) &&
                 latestTest.Id.Contains(q, StringComparison.OrdinalIgnoreCase)));

        var keep = SelectedRow?.Id;
        ReleaseVersions.Clear();
        TestVersions.Clear();
        Versions.Clear();
        foreach (var v in releaseQuery.Take(400))
        {
            var row = new VersionRow(v.Id, v.Type, v.ReleaseTime, _svc.Installed.IsInstalled(v.Id));
            ReleaseVersions.Add(row);
            Versions.Add(row);
        }
        if (showLatestTest && latestTest is not null)
        {
            var row = new VersionRow(latestTest.Id, latestTest.Type, latestTest.ReleaseTime,
                _svc.Installed.IsInstalled(latestTest.Id));
            TestVersions.Add(row);
            Versions.Add(row);
        }
        if (keep is not null) SelectedRow = Versions.FirstOrDefault(x => x.Id == keep);
    }

    private void OpenVersion(VersionRow? row)
    {
        if (row is null) return;
        foreach (var version in Versions) version.IsSelected = ReferenceEquals(version, row);
        SelectedRow = row;
        DetailVersion = row;
        LoaderGameVersion = row.Id;
        SelectedLoaderKind = ModLoaderKind.Vanilla;
        LoaderStatus = "请选择原版或加载器，然后安装。";
        IsDetailVisible = true;
        _ = DetectLoaderVersionsAsync(row.Id);
    }

    private async Task InstallAsync()
    {
        var row = SelectedRow;
        if (row is null) return;

        try
        {
            _svc.Flags.EnsureGameDownload();
        }
        catch (FeatureDisabledException ex)
        {
            Status = ex.Message;
            _main.ReportStatus("本体下载未开启：" + ex.Message);
            return;
        }

        Busy = true;
        LoadingMessage = $"正在安装 Minecraft {row.Id}…";
        ProgressValue = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressValue = p.Total > 0 ? Math.Clamp(p.Current * 100.0 / p.Total, 0, 100) : ProgressValue;
            Status = $"[{p.Phase}] {p.Label}";
            LoadingMessage = Status;
            _main.ReportStatus("下载中：" + p.Label);
        });

        try
        {
            await _svc.Versions.InstallAsync(row.Id, progress, _cts.Token).ConfigureAwait(true);
            ProgressValue = 100;
            Status = row.Id + " 安装完成";
            row.Installed = true;
            _main.Versions.Refresh();
            _main.Launch.ReloadVersions();
            _main.ReportStatus(row.Id + " 已安装");
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception ex)
        {
            Status = "安装失败：" + ex.Message;
            _main.ReportError("安装 Minecraft 版本", ex);
        }
        finally
        {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void CancelInstall() => _cts?.Cancel();

    private bool _labyRefreshing;

    private async Task RefreshLabyAsync()
    {
        if (_labyRefreshing) return;      // 防重入：设置 LabyChannel 会再次回调本方法
        _labyRefreshing = true;
        using var loading = BeginLoading("正在读取 LabyMod 频道和版本…");
        try
        {
            if (LabyChannels.Count == 0)
            {
                var channels = await _svc.LabyMod.ListChannelsAsync().ConfigureAwait(true);
                foreach (var c in channels) LabyChannels.Add(c);
                LabyChannel ??= LabyChannels.FirstOrDefault();
            }
            if (LabyChannel is null) return;

            var manifest = await _svc.LabyMod.GetManifestAsync(LabyChannel).ConfigureAwait(true);
            LabyVersions.Clear();
            foreach (var v in manifest.MinecraftVersions)
                LabyVersions.Add(new LabyRow(v.Tag, manifest.LabyModVersion, v.CustomManifestUrl));
            Status = $"LabyMod {manifest.LabyModVersion}（{LabyChannel}）支持 {manifest.MinecraftVersions.Count} 个游戏版本";
        }
        catch (Exception ex)
        {
            Status = "LabyMod 列表读取失败：" + ex.Message;
            _main.ReportError("读取 LabyMod 版本列表", ex);
        }
        finally
        {
            _labyRefreshing = false;
        }
    }

    private async Task InstallLabyAsync()
    {
        var row = SelectedLabyRow;
        if (row is null || LabyChannel is null) return;

        Busy = true;
        LoadingMessage = $"正在安装 LabyMod {row.Tag}…";
        ProgressValue = 0;
        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressValue = p.Total > 0 ? Math.Clamp(p.Current * 100.0 / p.Total, 0, 100) : ProgressValue;
            Status = $"[LabyMod/{p.Phase}] {p.Label}";
            LoadingMessage = Status;
        });

        try
        {
            var result = await _svc.LabyMod.InstallAsync(LabyChannel, row.Tag, progress).ConfigureAwait(true);
            ProgressValue = 100;
            Status = $"LabyMod {result.LabyModVersion} 已装入版本 {result.VersionId}";
            row.Installed = true;
            _main.Versions.Refresh();
            _main.Launch.ReloadVersions();
        }
        catch (Exception ex)
        {
            Status = "LabyMod 安装失败：" + ex.Message;
            _main.ReportError("安装 LabyMod", ex);
        }
        finally
        {
            Busy = false;
        }
    }
}

public sealed class VersionRow : ObservableObject
{
    public VersionRow(string id, string type, string releaseTime, bool installed)
    {
        Id = id; Type = type; ReleaseTime = releaseTime; _installed = installed;
    }

    public string Id { get; }
    public string Type { get; }
    public string ReleaseTime { get; }

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        // 修掉的 bug：以前这个 setter 不发通知，装完卡片上的「已安装」不会刷新
        set
        {
            if (Set(ref _installed, value))
            {
                Raise(nameof(Display));
                Raise(nameof(InstallState));
            }
        }
    }

    public string InstallState => Installed ? "✓ 已安装" : "未安装";

    public string TypeLabel => Type == "release" ? "正式版" : Type == "snapshot" ? "快照" : Type;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string Display => $"{Id}　{(Type == "release" ? "正式版" : Type == "snapshot" ? "快照" : Type)}{(Installed ? "　✓ 已安装" : "")}";

    public override string ToString() => Display;
}

public sealed class LoaderAvailabilityRow : ObservableObject
{
    public LoaderAvailabilityRow(string kind, string availability)
    {
        Kind = kind;
        _availability = availability;
    }

    public string Kind { get; }

    private string _availability;
    public string Availability { get => _availability; set => Set(ref _availability, value); }
}

public sealed class LabyRow : ObservableObject
{
    public LabyRow(string tag, string labyVersion, string manifestUrl)
    {
        Tag = tag; LabyVersion = labyVersion; ManifestUrl = manifestUrl;
    }

    public string Tag { get; }
    public string LabyVersion { get; }
    public string ManifestUrl { get; }

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        // 修掉的 bug：以前是普通属性，装完之后卡片上的「✓ 已安装」不会变
        set
        {
            if (Set(ref _installed, value)) Raise(nameof(Display));
        }
    }

    public string Display => $"{Tag}　（LabyMod {LabyVersion}{(Installed ? "　✓ 已安装" : "")}）";

    public override string ToString() => Display;
}

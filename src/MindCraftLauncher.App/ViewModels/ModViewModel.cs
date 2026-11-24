using MindCraftLauncher.App.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Mods;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>模组搜索结果卡片。</summary>
public sealed class ModCard : ObservableObject
{
    public ModCard(ModrinthHit hit)
    {
        ProjectId = hit.ProjectId;
        Title = hit.Title;
        Summary = hit.Summary;
        Description = hit.Description;
        IconUrl = hit.IconUrl;
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string ProjectId { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Description { get; }
    public string? IconUrl { get; }
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconUrl);
}

/// <summary>
/// 模组页：按「装到哪个版本 + 游戏版本 + 加载器」搜索 Modrinth，选中后把 jar 直接放进目标版本的 mods。
/// 只装 jar，不解析前置依赖——Modrinth 的依赖图会引入一堆版本约束，属于下一步的事。
/// </summary>
public sealed class ModViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;

    public ModViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        Hits = new ObservableCollection<ModCard>();
        Versions = new ObservableCollection<ModrinthVersion>();
        Loaders = new ObservableCollection<string> { "fabric", "forge", "neoforge", "quilt" };
        SupportedGameVersions = new ObservableCollection<string>();
        TargetVersions = new ObservableCollection<string>();

        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !Busy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedVersion is not null && TargetVersion is { Length: > 0 } && !Busy);
        RefreshTargetsCommand = new RelayCommand(RefreshTargets);
        PickCommand = new RelayCommand(p =>
        {
            if (p is not ModCard card) return;
            foreach (var h in Hits) h.IsSelected = ReferenceEquals(h, card);
            SelectedHit = card;
            IsDetailVisible = true;
        });
        BackToSearchCommand = new RelayCommand(() => IsDetailVisible = false);
        BackCommand = BackToSearchCommand;

        RefreshTargets();
    }

    public ObservableCollection<ModCard> Hits { get; }
    public ObservableCollection<ModrinthVersion> Versions { get; }
    public ObservableCollection<string> Loaders { get; }
    public ObservableCollection<string> SupportedGameVersions { get; }
    public ObservableCollection<string> TargetVersions { get; }

    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand RefreshTargetsCommand { get; }
    public RelayCommand PickCommand { get; }
    public RelayCommand BackToSearchCommand { get; }
    public RelayCommand BackCommand { get; }

    public void RefreshTargets()
    {
        var keep = TargetVersion;
        TargetVersions.Clear();
        foreach (var v in _svc.Installed.List()) TargetVersions.Add(v.Id);
        TargetVersion = TargetVersions.Contains(keep) ? keep : (_main.Versions.SelectedId.Length > 0
            ? _main.Versions.SelectedId
            : TargetVersions.FirstOrDefault() ?? "");
    }

    private bool _isDetailVisible;
    public bool IsDetailVisible { get => _isDetailVisible; private set => Set(ref _isDetailVisible, value); }

    private readonly List<ModrinthVersion> _allVersions = new();

    private string _selectedGameVersion = "";
    public string SelectedGameVersion
    {
        get => _selectedGameVersion;
        set
        {
            if (!Set(ref _selectedGameVersion, value)) return;
            GameVersion = value;
            ApplyVersionFilter();
        }
    }

    public string DetectedLoader => SelectedVersion?.Loaders.FirstOrDefault() ?? "通用 / 未指定";
    public string DetectedLoaders => DetectedLoader;
    public bool ShowTargetVersion => true;

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    private string _gameVersion = "";
    public string GameVersion { get => _gameVersion; set => Set(ref _gameVersion, value); }

    private string _loader = "fabric";
    public string Loader { get => _loader; set => Set(ref _loader, value); }

    private string _targetVersion = "";
    public string TargetVersion { get => _targetVersion; set => Set(ref _targetVersion, value); }

    private ModCard? _selectedHit;
    public ModCard? SelectedHit
    {
        get => _selectedHit;
        set
        {
            if (!Set(ref _selectedHit, value)) return;
            Versions.Clear();
            SelectedVersion = null;
            if (value is not null) _ = LoadVersionsAsync();
        }
    }

    private ModrinthVersion? _selectedVersion;
    public ModrinthVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Set(ref _selectedVersion, value)) return;
            Raise(nameof(DetectedLoader));
        }
    }

    private string _status = "先选择游戏版本和加载器，再搜索模组。安装时会自动补齐 Modrinth 标记的必需依赖。";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private string _loadingMessage = "请稍候…";
    public string LoadingMessage { get => _loadingMessage; private set => Set(ref _loadingMessage, value); }

    private async Task SearchAsync()
    {
        Busy = true;
        LoadingMessage = "正在搜索 Modrinth 模组…";
        Status = "搜索中…";
        try
        {
            var url = ModrinthApi.SearchUrl(Query, "mod",
                string.IsNullOrWhiteSpace(GameVersion) ? null : GameVersion.Trim(), null, 20);
            var result = await _svc.Http.GetJsonAsync<ModrinthSearchResponse>(url).ConfigureAwait(true);

            Hits.Clear();
            foreach (var h in result.Hits) Hits.Add(new ModCard(h));
            Status = result.Hits.Count == 0
                ? "没搜到，检查一下游戏版本与加载器过滤是否太窄。"
                : $"找到 {result.TotalHits} 个结果，已显示前 {result.Hits.Count} 个。";
        }
        catch (Exception ex)
        {
            Status = "搜索失败：" + ex.Message;
            _main.ReportError("搜索 Modrinth 模组", ex);
        }
        finally { Busy = false; }
    }

    private async Task LoadVersionsAsync()
    {
        if (SelectedHit is null) return;

        Busy = true;
        LoadingMessage = $"正在读取“{SelectedHit.Title}”的版本…";
        try
        {
            var list = await _svc.Mods.ListVersionsAsync(SelectedHit.ProjectId, null, null).ConfigureAwait(true);
            _allVersions.Clear();
            _allVersions.AddRange(list.Where(v => v.PrimaryFile is not null));

            SupportedGameVersions.Clear();
            foreach (var gameVersion in _allVersions.SelectMany(v => v.GameVersions)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                SupportedGameVersions.Add(gameVersion);

            var preferredVersion = SupportedGameVersions.Contains(GameVersion)
                ? GameVersion
                : SupportedGameVersions.FirstOrDefault() ?? "";
            SelectedGameVersion = preferredVersion;
            ApplyVersionFilter();
            Status = Versions.Count == 0
                ? "这个模组没有可下载的文件。"
                : $"找到 {Versions.Count} 个适用于 {(SelectedGameVersion.Length > 0 ? SelectedGameVersion : "全部游戏版本")} 的文件。加载器将根据所选文件自动识别。";
        }
        catch (Exception ex)
        {
            Status = "读取版本失败：" + ex.Message;
            _main.ReportError("读取 Modrinth 模组版本", ex);
        }
        finally { Busy = false; }
    }

    private void ApplyVersionFilter()
    {
        Versions.Clear();
        var matching = string.IsNullOrWhiteSpace(SelectedGameVersion)
            ? _allVersions
            : _allVersions.Where(v => v.GameVersions.Count == 0 ||
                v.GameVersions.Contains(SelectedGameVersion, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var version in matching) Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault();
    }

    private async Task InstallAsync()
    {
        var modVersion = SelectedVersion;
        if (modVersion is null || TargetVersion.Length == 0) return;
        var loader = modVersion.Loaders.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(SelectedGameVersion) || string.IsNullOrWhiteSpace(loader))
        {
            Status = "所选文件未声明游戏版本或加载器，无法可靠解析必需依赖。请改选一个明确支持当前游戏版本的文件。";
            return;
        }

        Busy = true;
        LoadingMessage = $"正在安装模组“{SelectedHit!.Title}”…";
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                Status = $"[{p.Phase}] {p.Label}";
                LoadingMessage = Status;
            });
            var path = await _svc.Mods.InstallAsync(SelectedHit!.ProjectId, modVersion.Id, TargetVersion,
                _svc.Config.Java.VersionIsolation, progress, gameVersion: SelectedGameVersion, loader: loader)
                .ConfigureAwait(true);

            Status = $"已装入 {TargetVersion} 的 mods 文件夹（含必需依赖）：{path}";
            _main.ReportStatus("模组已安装：" + System.IO.Path.GetFileName(path));
        }
        catch (FeatureDisabledException ex) { Status = ex.Message; }
        catch (Exception ex)
        {
            Status = "安装失败：" + ex.Message;
            _main.ReportError("安装 Modrinth 模组及依赖", ex);
        }
        finally { Busy = false; }
    }
}

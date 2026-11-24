using System.Collections.ObjectModel;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Mods;

namespace MindCraftLauncher.App.ViewModels;

public sealed class ClientPackViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;
    private readonly string _projectType;

    public ClientPackViewModel(LauncherServices services, MainViewModel main, string projectType,
        ClientPackKind packKind, string title)
    {
        _svc = services;
        _main = main;
        _projectType = projectType;
        PackKind = packKind;
        Title = title;
        Hits = new ObservableCollection<ModCard>();
        Versions = new ObservableCollection<ModrinthVersion>();
        SupportedGameVersions = new ObservableCollection<string>();
        TargetVersions = new ObservableCollection<string>();
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !Busy);
        InstallCommand = new AsyncRelayCommand(InstallAsync,
            () => SelectedVersion is not null && TargetVersion.Length > 0 && !Busy);
        RefreshTargetsCommand = new RelayCommand(RefreshTargets);
        BackCommand = new RelayCommand(() => IsDetailVisible = false);
        PickCommand = new RelayCommand(p =>
        {
            if (p is not ModCard card) return;
            foreach (var hit in Hits) hit.IsSelected = ReferenceEquals(hit, card);
            SelectedHit = card;
            IsDetailVisible = true;
        });
        RefreshTargets();
        GameVersion = TargetVersions.FirstOrDefault(v => !v.Contains('-')) ?? "";
    }

    public string Title { get; }
    public ClientPackKind PackKind { get; }
    public bool ShowTargetVersion => true;
    public ObservableCollection<ModCard> Hits { get; }
    public ObservableCollection<ModrinthVersion> Versions { get; }
    public ObservableCollection<string> SupportedGameVersions { get; }
    public ObservableCollection<string> TargetVersions { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand RefreshTargetsCommand { get; }
    public RelayCommand PickCommand { get; }
    public RelayCommand BackCommand { get; }

    private readonly List<ModrinthVersion> _allVersions = new();

    private bool _isDetailVisible;
    public bool IsDetailVisible { get => _isDetailVisible; private set => Set(ref _isDetailVisible, value); }

    private string _selectedGameVersion = "";
    public string SelectedGameVersion
    {
        get => _selectedGameVersion;
        set
        {
            if (!Set(ref _selectedGameVersion, value)) return;
            ApplyVersionFilter();
        }
    }

    public string DetectedLoaders => SelectedVersion?.Loaders.Count > 0
        ? string.Join(" / ", SelectedVersion.Loaders)
        : "通用 / 未指定";

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    private string _gameVersion = "";
    public string GameVersion { get => _gameVersion; set => Set(ref _gameVersion, value); }

    private string _targetVersion = "";
    public string TargetVersion
    {
        get => _targetVersion;
        set => Set(ref _targetVersion, value);
    }

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
            Raise(nameof(DetectedLoaders));
        }
    }

    private string _status = "从 Modrinth 搜索资源，选择项目和版本后安装到所选 Minecraft 实例。";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private string _loadingMessage = "请稍候…";
    public string LoadingMessage { get => _loadingMessage; private set => Set(ref _loadingMessage, value); }

    public void RefreshTargets()
    {
        var keep = TargetVersion;
        TargetVersions.Clear();
        foreach (var version in _svc.Installed.List()) TargetVersions.Add(version.Id);
        TargetVersion = TargetVersions.Contains(keep) ? keep :
            TargetVersions.Contains(_main.Versions.SelectedId) ? _main.Versions.SelectedId : TargetVersions.FirstOrDefault() ?? "";
    }

    private async Task SearchAsync()
    {
        Busy = true;
        LoadingMessage = $"正在搜索 Modrinth {Title}…";
        Status = "搜索中…";
        try
        {
            var game = string.IsNullOrWhiteSpace(GameVersion) ? null : GameVersion.Trim();
            var result = await _svc.Http.GetJsonAsync<ModrinthSearchResponse>(
                ModrinthApi.SearchUrl(Query, _projectType, game, null, 20)).ConfigureAwait(true);
            Hits.Clear();
            foreach (var hit in result.Hits) Hits.Add(new ModCard(hit));
            Status = result.Hits.Count == 0 ? "没有找到匹配结果，请调整关键词或游戏版本。" :
                $"找到 {result.TotalHits} 个结果，已显示前 {result.Hits.Count} 个。";
        }
        catch (Exception ex)
        {
            Status = "搜索失败：" + ex.Message;
            _main.ReportError($"搜索 Modrinth {Title}", ex);
        }
        finally { Busy = false; }
    }

    private async Task LoadVersionsAsync()
    {
        if (SelectedHit is null) return;
        Busy = true;
        LoadingMessage = $"正在读取“{SelectedHit.Title}”的版本…";
        Status = "读取项目版本…";
        try
        {
            var versions = await _svc.Http.GetJsonAsync<List<ModrinthVersion>>(
                ModrinthApi.VersionsUrl(SelectedHit.ProjectId, null, null)).ConfigureAwait(true);
            _allVersions.Clear();
            _allVersions.AddRange(versions.Where(v => v.PrimaryFile is not null));
            SupportedGameVersions.Clear();
            foreach (var version in _allVersions.SelectMany(v => v.GameVersions)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                SupportedGameVersions.Add(version);
            SelectedGameVersion = SupportedGameVersions.Contains(GameVersion)
                ? GameVersion
                : SupportedGameVersions.FirstOrDefault() ?? "";
            ApplyVersionFilter();
            Status = Versions.Count == 0 ? "没有可下载的兼容版本。" :
                $"找到 {Versions.Count} 个适用于 {(SelectedGameVersion.Length > 0 ? SelectedGameVersion : "全部游戏版本")} 的文件。";
        }
        catch (Exception ex)
        {
            Status = "读取版本失败：" + ex.Message;
            _main.ReportError($"读取 Modrinth {Title} 版本", ex);
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
        if (SelectedHit is null || SelectedVersion is null || TargetVersion.Length == 0) return;
        Busy = true;
        LoadingMessage = $"正在安装{Title}“{SelectedHit.Title}”…";
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                Status = $"[{p.Phase}] {p.Label}";
                LoadingMessage = Status;
            });
            var path = await _svc.ClientPacks.InstallAsync(SelectedHit.ProjectId, SelectedVersion.Id,
                TargetVersion, _svc.Config.Java.VersionIsolation, PackKind, progress).ConfigureAwait(true);
            Status = $"已安装：{path}";
            _main.ReportStatus($"{Title}已安装：{System.IO.Path.GetFileName(path)}");
        }
        catch (FeatureDisabledException ex) { Status = ex.Message; }
        catch (Exception ex)
        {
            Status = "安装失败：" + ex.Message;
            _main.ReportError($"安装 {Title}", ex);
        }
        finally { Busy = false; }
    }
}

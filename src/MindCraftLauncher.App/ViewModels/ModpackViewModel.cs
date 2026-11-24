using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Config;
using MindCraftLauncher.Core.Downloads;
using MindCraftLauncher.Core.Mods;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>整合包搜索结果卡片。</summary>
public sealed class ModpackCard : ObservableObject
{
    public ModpackCard(ModrinthHit hit)
    {
        Slug = hit.Slug;
        ProjectId = hit.ProjectId;
        Title = hit.Title;
        Summary = hit.Summary;
        IconUrl = hit.IconUrl;
        Description = hit.Description;
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string Slug { get; }
    public string ProjectId { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Description { get; }
    public string? IconUrl { get; }
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconUrl);
}

/// <summary>整合包页：搜索 Modrinth → 选版本 → 一键装成独立版本；也支持本地 .mrpack。</summary>
public sealed class ModpackViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;

    public ModpackViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        Hits = new ObservableCollection<ModpackCard>();
        Versions = new ObservableCollection<ModrinthVersion>();
        SupportedGameVersions = new ObservableCollection<string>();
        TargetVersions = new ObservableCollection<string>();

        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !Busy);
        LoadVersionsCommand = new AsyncRelayCommand(LoadVersionsAsync, () => SelectedHit is not null && !Busy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedVersion is not null && !Busy);
        InstallLocalCommand = new AsyncRelayCommand(InstallLocalAsync, () => !Busy);

        PickCommand = new RelayCommand(p =>
        {
            if (p is not ModpackCard card) return;
            foreach (var h in Hits) h.IsSelected = ReferenceEquals(h, card);
            SelectedHit = card;
            IsDetailVisible = true;
        });
        BackCommand = new RelayCommand(() => IsDetailVisible = false);

        GameVersion = _svc.Installed.List().FirstOrDefault(v => !v.Id.Contains('-'))?.Id ?? "";
    }

    public ObservableCollection<ModpackCard> Hits { get; }
    public ObservableCollection<ModrinthVersion> Versions { get; }
    public ObservableCollection<string> SupportedGameVersions { get; }
    public ObservableCollection<string> TargetVersions { get; }
    public bool ShowTargetVersion => false;

    private string _targetVersion = "";
    public string TargetVersion { get => _targetVersion; set => Set(ref _targetVersion, value); }
    private readonly List<ModrinthVersion> _allVersions = new();

    private bool _isDetailVisible;
    public bool IsDetailVisible { get => _isDetailVisible; private set => Set(ref _isDetailVisible, value); }

    public RelayCommand BackCommand { get; }

    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand LoadVersionsCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand InstallLocalCommand { get; }
    public RelayCommand PickCommand { get; }

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
        : "由整合包清单指定";

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    private string _gameVersion = "";
    public string GameVersion { get => _gameVersion; set => Set(ref _gameVersion, value); }

    private ModpackCard? _selectedHit;
    public ModpackCard? SelectedHit
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

    private string _status = "输入关键词搜索整合包（数据来自 Modrinth，无需 API Key）。";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private string _loadingMessage = "请稍候…";
    public string LoadingMessage { get => _loadingMessage; private set => Set(ref _loadingMessage, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private async Task SearchAsync()
    {
        Busy = true;
        LoadingMessage = "正在搜索 Modrinth 整合包…";
        Status = "搜索中…";
        try
        {
            var url = ModrinthApi.SearchUrl(Query, "modpack",
                string.IsNullOrWhiteSpace(GameVersion) ? null : GameVersion.Trim(), null, 20);
            var result = await _svc.Http.GetJsonAsync<ModrinthSearchResponse>(url).ConfigureAwait(true);

            Hits.Clear();
            foreach (var h in result.Hits) Hits.Add(new ModpackCard(h));
            Status = result.Hits.Count == 0
                ? "没搜到整合包，换个关键词或清空游戏版本过滤试试。"
                : $"找到 {result.TotalHits} 个结果，已显示前 {result.Hits.Count} 个。点一个查看可安装版本。";
        }
        catch (Exception ex)
        {
            Status = "搜索失败：" + ex.Message;
        }
        finally { Busy = false; }
    }

    private async Task LoadVersionsAsync()
    {
        if (SelectedHit is null) return;

        Busy = true;
        LoadingMessage = $"正在读取“{SelectedHit.Title}”的版本…";
        Status = "读取版本列表…";
        try
        {
            var url = ModrinthApi.VersionsUrl(SelectedHit.ProjectId, null, null);
            var list = await _svc.Http.GetJsonAsync<List<ModrinthVersion>>(url).ConfigureAwait(true);

            _allVersions.Clear();
            _allVersions.AddRange(list.Where(v => v.PrimaryFile is not null));
            SupportedGameVersions.Clear();
            foreach (var gameVersion in _allVersions.SelectMany(v => v.GameVersions)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                SupportedGameVersions.Add(gameVersion);
            SelectedGameVersion = SupportedGameVersions.Contains(GameVersion)
                ? GameVersion
                : SupportedGameVersions.FirstOrDefault() ?? "";
            ApplyVersionFilter();

            Status = Versions.Count == 0
                ? "这个整合包没有可下载的版本文件。"
                : $"找到 {Versions.Count} 个适用于 {(SelectedGameVersion.Length > 0 ? SelectedGameVersion : "全部游戏版本")} 的版本。";
        }
        catch (Exception ex)
        {
            Status = "读取版本失败：" + ex.Message;
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
        var target = SelectedVersion;
        if (target?.PrimaryFile is null) return;

        Busy = true;
        LoadingMessage = $"正在安装整合包“{target.Name}”…";
        Progress = 0;
        var progress = new Progress<InstallProgress>(p =>
        {
            Progress = p.Total > 0 ? Math.Clamp(p.Current * 100.0 / p.Total, 0, 100) : Progress;
            Status = $"[{p.Phase}] {p.Label}";
            LoadingMessage = Status;
            LoadingMessage = Status;
            _main.ReportStatus("安装整合包：" + p.Label);
        });

        try
        {
            var id = await _svc.Modpacks.InstallMrpackAsync(target.PrimaryFile.Url,
                target.Name + " " + target.VersionNumber, progress).ConfigureAwait(true);

            Progress = 100;
            Status = $"装好了，版本：{id}。去【版本】页点它就能启动。";
            _main.Versions.Refresh();
            _main.Launch.ReloadVersions();
            _main.ReportStatus("整合包已安装：" + id);
        }
        catch (FeatureDisabledException ex) { Status = ex.Message; }
        catch (Exception ex)
        {
            Status = "安装失败：" + ex.Message;
            _main.ReportStatus("整合包安装失败");
        }
        finally { Busy = false; }
    }

    private async Task InstallLocalAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        Busy = true;
        LoadingMessage = "正在安装本地整合包…";
        Progress = 0;
        var progress = new Progress<InstallProgress>(p =>
        {
            Progress = p.Total > 0 ? Math.Clamp(p.Current * 100.0 / p.Total, 0, 100) : Progress;
            Status = $"[{p.Phase}] {p.Label}";
            LoadingMessage = Status;
        });

        try
        {
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            var id = await _svc.Modpacks.InstallFromFileAsync(dlg.FileName, name, progress).ConfigureAwait(true);
            Progress = 100;
            Status = $"本地整合包装好了，版本：{id}";
            _main.Versions.Refresh();
            _main.Launch.ReloadVersions();
        }
        catch (FeatureDisabledException ex) { Status = ex.Message; }
        catch (Exception ex) { Status = "安装失败：" + ex.Message; }
        finally { Busy = false; }
    }
}

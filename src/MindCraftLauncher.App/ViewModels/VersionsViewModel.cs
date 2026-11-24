using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using MindCraftLauncher.Core;

namespace MindCraftLauncher.App.ViewModels;

/// <summary>一张版本卡片（选项卡）。</summary>
public sealed class VersionCard : ObservableObject
{
    public VersionCard(Core.Launch.InstalledVersion v, bool selected)
    {
        Id = v.Id;
        KindLabel = v.KindLabel;
        JavaLabel = v.JavaLabel;
        InstalledLabel = v.InstalledLabel;
        IsModpack = v.IsModpack;
        PackName = v.PackName ?? "";
        _isSelected = selected;
    }

    public string Id { get; }
    public string KindLabel { get; }
    public string JavaLabel { get; }
    public string InstalledLabel { get; }
    public bool IsModpack { get; }
    public string PackName { get; }

    public string Title => IsModpack && PackName.Length > 0 ? PackName : Id;
    public string SubTitle => IsModpack && PackName.Length > 0 ? Id : KindLabel;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// 版本页：每个已安装版本一张卡片（选项卡），不再用下拉框。
/// 选中状态由这里唯一持有，启动页读它。
/// </summary>
public sealed class VersionsViewModel : ObservableObject
{
    private readonly LauncherServices _svc;
    private readonly MainViewModel _main;

    public VersionsViewModel(LauncherServices services, MainViewModel main)
    {
        _svc = services;
        _main = main;

        Cards = new ObservableCollection<VersionCard>();

        RefreshCommand = new RelayCommand(Refresh);
        SelectCommand = new RelayCommand(p => Select(p as VersionCard));
        DeleteCommand = new RelayCommand(p => Delete(p as VersionCard));
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as VersionCard));
        LaunchCommand = new RelayCommand(p => Launch(p as VersionCard));

        Refresh();
    }

    public ObservableCollection<VersionCard> Cards { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SelectCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand LaunchCommand { get; }

    private string _selectedId = "";
    public string SelectedId
    {
        get => _selectedId;
        private set => Set(ref _selectedId, value);
    }

    public string SelectedText =>
        Cards.FirstOrDefault(c => c.IsSelected)?.Title ?? "（未选择版本）";

    public string Summary => Cards.Count == 0
        ? "还没有安装任何版本。去【下载】页切换到「整合包」选项安装。"
        : $"共 {Cards.Count} 个版本，其中整合包 {Cards.Count(c => c.IsModpack)} 个。点卡片即可切换，双击直接启动。";

    public void Refresh()
    {
        var keepId = SelectedId;
        var list = _svc.Installed.List();

        // 没有选中项时，回落到上次启动的版本，再回落到第一个
        var target = keepId;
        if (target.Length == 0 || list.All(v => v.Id != target))
            target = list.FirstOrDefault(v => v.Id == _svc.Config.LastVersionId)?.Id ?? list.FirstOrDefault()?.Id ?? "";

        Cards.Clear();
        foreach (var v in list)
            Cards.Add(new VersionCard(v, v.Id == target));

        SelectedId = target;
        Raise(nameof(SelectedText));
        Raise(nameof(Summary));
    }

    private void Select(VersionCard? card)
    {
        if (card is null) return;
        foreach (var c in Cards) c.IsSelected = ReferenceEquals(c, card);
        SelectedId = card.Id;

        _svc.Config.LastVersionId = card.Id;
        _svc.SaveConfig();

        Raise(nameof(SelectedText));
        _main.ReportStatus("已选择版本：" + card.Title);
    }

    private void Delete(VersionCard? card)
    {
        if (card is null) return;

        var answer = MessageBox.Show(
            $"确定删除版本「{card.Title}」？\n\n只删除 versions 下的这一个版本目录，不会动存档、全局 mods 和资源文件。",
            "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        if (_svc.VersionManager.Delete(card.Id))
        {
            if (SelectedId == card.Id) SelectedId = "";
            Refresh();
            _main.Launch.ReloadVersions();
            _main.ReportStatus("已删除版本 " + card.Id);
        }
        else
        {
            MessageBox.Show("删除失败，可能目录被占用或没有权限。", "删除版本", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder(VersionCard? card)
    {
        if (card is null) return;
        try
        {
            var dir = _svc.VersionManager.FolderOf(card.Id);
            if (!Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _main.ReportStatus("打开目录失败：" + ex.Message);
        }
    }

    private void Launch(VersionCard? card)
    {
        if (card is null) return;
        Select(card);
        _main.Launch.LaunchSelected();
    }
}
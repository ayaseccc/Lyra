using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Player.Core.Infra;
using Player.Core.Library;

namespace Player.App.ViewModels;

/// <summary>
/// 设置页 —— 媒体库组（PLAN 第 8 节）。P1 只做目录管理与重新扫描，
/// 输出组留给 P2、在线组留给 P3。
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly Func<bool, Task> _requestScan;

    public SettingsPageViewModel(LibraryService library, Func<bool, Task> requestScan)
    {
        _library = library;
        _requestScan = requestScan;

        Folders = new ObservableCollection<string>(ConfigService.Current.Library.Folders);
    }

    public string Title => "设置";

    public string Subtitle => "媒体库";

    public ObservableCollection<string> Folders { get; }

    [ObservableProperty]
    private string? _selectedFolder;

    public bool IsScanning => _library.IsScanning;

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择音乐文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(folder)) return;

        if (Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            return;

        Folders.Add(folder);
        PersistFolders();

        await _requestScan(false);
    }

    [RelayCommand]
    private async Task RemoveFolderAsync()
    {
        if (SelectedFolder is null) return;

        var folder = SelectedFolder;
        Folders.Remove(folder);
        SelectedFolder = null;
        PersistFolders();

        // 扫描器只负责"根目录之内"的曲目，移除根目录时要显式把它下面的曲目清出去
        await Task.Run(() => _library.RemoveTracksUnderRoot(folder));
        await _requestScan(false);
    }

    [RelayCommand]
    private Task RescanAsync() => _requestScan(true);

    [RelayCommand]
    private Task IncrementalScanAsync() => _requestScan(false);

    [RelayCommand]
    private void CancelScan() => _library.CancelScan();

    private void PersistFolders()
    {
        // 设置页开着的时候用户可能又拖了文件夹进来，先合并再写回，别把它抹掉
        var merged = ConfigService.Current.Library.Folders
            .Where(existing => Folders.Any(f => string.Equals(f, existing, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var folder in Folders)
        {
            if (!merged.Any(m => string.Equals(m, folder, StringComparison.OrdinalIgnoreCase)))
                merged.Add(folder);
        }

        ConfigService.Current.Library.Folders = merged;
        ConfigService.Save();
        _library.StartWatching();   // 根目录变了，监听也要跟着重建
    }
}

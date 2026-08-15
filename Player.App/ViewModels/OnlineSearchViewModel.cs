using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.Core.Online;

namespace Player.App.ViewModels;

/// <summary>搜索结果行（结果列含音质标注）。</summary>
public sealed class OnlineSearchItem
{
    public required OnlineTrack Track { get; init; }

    public required string SourceKey { get; init; }

    public required string SourceName { get; init; }

    public string Name => Track.Name;

    public string ArtistLine => Track.ArtistLine;

    public string Album => Track.Album;

    public string DurationText { get; init; } = string.Empty;

    /// <summary>请求音质档位标注（实际值在试听状态行显示）。</summary>
    public required string BrText { get; init; }
}

/// <summary>
/// P4 在线搜索：关键词 + 音源下拉 + 翻页；[source]_album 整张专辑；
/// 双击结果试听（临时播放，不写歌单/队列）。
/// </summary>
public sealed partial class OnlineSearchViewModel : ObservableObject
{
    private readonly IReadOnlyList<IOnlineSource> _sources;
    private readonly PlayerViewModel _player;
    private readonly Player.Core.Downloads.DownloadService? _downloads;
    private CancellationTokenSource? _searchCts;

    public OnlineSearchViewModel(IReadOnlyList<IOnlineSource> sources, PlayerViewModel player,
        Player.Core.Downloads.DownloadService? downloads = null)
    {
        _sources = sources;
        _player = player;
        _downloads = downloads;

        // 默认选中网易云（PLAN：默认音源 netease）；无 Key/不可用则自动落回 GD
        var preferred = sources.FirstOrDefault(s => s.Key == "netease" && s.IsAvailable) ?? sources.FirstOrDefault(s => s.IsAvailable) ?? sources.FirstOrDefault();
        _selectedSource = preferred is null ? null : new SourceOption(preferred);
        _selectedBr = 999;
    }

    public string Title => "在线搜索";

    public string Subtitle => "GD Studio 零 Key 零额度 · 网易云需 API Key";

    public sealed record SourceOption(IOnlineSource Source)
    {
        public string Name => Source.DisplayName;

        public bool Available => Source.IsAvailable;
    }

    public IReadOnlyList<SourceOption> Sources => _sources.Select(s => new SourceOption(s)).ToList();

    public IReadOnlyList<int> BrOptions { get; } = new[] { 999, 740, 320, 128 };

    [ObservableProperty]
    private SourceOption? _selectedSource;

    [ObservableProperty]
    private string _keyword = string.Empty;

    /// <summary>专辑模式：[source]_album 拉整张专辑。</summary>
    [ObservableProperty]
    private bool _isAlbumMode;

    [ObservableProperty]
    private int _selectedBr = 999;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = "输入关键词搜索，双击结果试听（临时播放，不写入歌单/队列）。";

    [ObservableProperty]
    private OnlineSearchItem? _selectedItem;

    public ObservableCollection<OnlineSearchItem> Results { get; } = new();

    public bool HasResults => Results.Count > 0;

    public bool IsOnlinePreview => _player.IsOnlinePreview;

    /// <summary>搜索按钮可用（搜索中禁用）。</summary>
    public bool CanSearch => !IsSearching;

    public bool HasPreviousPage => Page > 1;

    public string PageText => $"第 {Page} 页";

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanSearch));
    }

    partial void OnPageChanged(int value)
    {
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(PageText));
    }

    partial void OnSelectedSourceChanged(SourceOption? value)
    {
        if (value is not null) OnPropertyChanged(nameof(SelectedSourceName));
    }

    public string SelectedSourceName => SelectedSource?.Name ?? string.Empty;

    /// <summary>双击试听。</summary>
    public event Action<OnlineSearchItem>? PlayRequested;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Keyword))
        {
            StatusText = "先输入关键词";
            return;
        }
        await LoadAsync(1);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (HasMore && !IsSearching) await LoadAsync(Page + 1);
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (Page > 1 && !IsSearching) await LoadAsync(Page - 1);
    }

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedItem is { } item) PlayRequested?.Invoke(item);
    }

    /// <summary>下载选中结果（P4-5）。库内重复会进下载管理页等待确认。</summary>
    [RelayCommand]
    private void DownloadSelected()
    {
        if (SelectedItem is not { } item || _downloads is null) return;
        var duplicate = _downloads.Enqueue(item.Track, item.SourceKey, SelectedBr);
        if (duplicate.Status == Player.Core.Downloads.DownloadStatus.Duplicate)
        {
            StatusText = "该曲目与媒体库重复，已加入下载管理页等待确认";
            return;
        }

        // ChKSz 操作保留额度预估（PLAN P4 v2）
        var isFree = _sources.FirstOrDefault(s => s.Key == item.SourceKey)?.IsFree ?? true;
        StatusText = isFree
            ? $"已加入下载队列：{item.Track.Name}（GD 零额度）"
            : $"已加入下载队列：{item.Track.Name}（将消耗 1 次网易云额度）";
    }

    private async Task LoadAsync(int page)
    {
        var source = SelectedSource?.Source;
        if (source is null || !source.IsAvailable)
        {
            StatusText = "所选音源当前不可用，请换一个";
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        StatusText = "正在搜索…";
        try
        {
            var result = IsAlbumMode
                ? await source.SearchAlbumAsync(Keyword.Trim(), limit: 20, page: page, ct)
                : await source.SearchAsync(Keyword.Trim(), limit: 20, page: page, ct);

            if (ct.IsCancellationRequested) return;

            if (!result.Success)
            {
                StatusText = result.NotFound ? "没找到相关结果" : "搜索失败：" + result.Error;
                return;
            }

            Results.Clear();
            foreach (var t in result.Data ?? Array.Empty<OnlineTrack>())
            {
                Results.Add(new OnlineSearchItem
                {
                    Track = t,
                    SourceKey = source.Key,
                    SourceName = source.DisplayName,
                    DurationText = t.DurationMs > 0 ? FormatDuration(t.DurationMs) : string.Empty,
                    BrText = SelectedBr.ToString()
                });
            }

            Page = page;
            HasMore = result.Data is { Count: >= 20 };
            StatusText = Results.Count == 0
                ? "没找到相关结果"
                : $"找到 {Results.Count} 条（{source.DisplayName}{(IsAlbumMode ? " · 专辑" : "")}），双击试听";
            OnPropertyChanged(nameof(HasResults));
        }
        catch (OperationCanceledException)
        {
            // 被新搜索取消，忽略
        }
        catch (Exception ex)
        {
            // 兜底：任何意外都不让 AsyncRelayCommand 把异常抛到 UI 线程弹窗
            Serilog.Log.Warning(ex, "在线搜索失败");
            StatusText = "搜索出错：" + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private static string FormatDuration(long ms)
    {
        // 注意：TimeSpan 自定义格式里冒号必须转义，这里手工拼最稳（逐字串 @"h:mm:ss" 会抛 FormatException）
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1
            ? string.Format("{0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds)
            : string.Format("{0}:{1:00}", (int)span.TotalMinutes, span.Seconds);
    }
}

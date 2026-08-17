using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Player.Core.Downloads;
using Player.Core.Online;

namespace Player.App.ViewModels;

/// <summary>下载管理页：串行队列 / 进度 / 结果 / 重复确认（P4-5）。</summary>
public sealed partial class DownloadPageViewModel : ObservableObject, IDisposable
{
    private readonly DownloadService _service;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private int _refreshScheduled;

    public DownloadPageViewModel(DownloadService service)
    {
        _service = service;
        _dispatcher = System.Windows.Application.Current?.Dispatcher
                     ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        _service.ItemChanged += OnItemChanged;
        Refresh();
    }

    public string Title => "下载管理";

    public string Subtitle => string.Empty;

    public ObservableCollection<DownloadRow> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    public sealed class DownloadRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required DownloadItem Item { get; init; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        /// <summary>状态/进度变化时由页面 Refresh 调用（空属性名 = 全量刷新，进度条/状态/按钮可见性都更新）。</summary>
        public void Refresh() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

        public string Title => Item.Track.Name;

        public string ArtistLine => Item.Track.ArtistLine;

        public string StatusText => Item.Status switch
        {
            DownloadStatus.Queued => "排队中",
            DownloadStatus.Downloading => $"下载中 {Item.ProgressPercent}%",
            DownloadStatus.Completed => $"完成（实际 {QualityFormat.Br(Item.ActualBr)}）",
            DownloadStatus.Failed => "失败：" + Item.Error,
            DownloadStatus.Duplicate => "与媒体库重复：" + Item.Error,
            DownloadStatus.Cancelled => "已取消",
            _ => string.Empty
        };

        public bool IsDownloading => Item.Status == DownloadStatus.Downloading;

        public int Progress => Item.ProgressPercent;

        public bool IsDuplicate => Item.Status == DownloadStatus.Duplicate;

        /// <summary>排队中/下载中可取消（实机反馈：下载管理增加取消）。</summary>
        public bool IsCancellable => Item.IsCancellable;

        public bool IsDone => Item.IsDone;
    }

    private void OnItemChanged(DownloadItem item)
    {
        if (_disposed
            || _dispatcher.HasShutdownStarted
            || _dispatcher.HasShutdownFinished
            || Interlocked.Exchange(ref _refreshScheduled, 1) != 0)
            return;

        try
        {
            _dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    // Clear before taking the snapshot. An event racing the refresh
                    // then schedules one follow-up instead of being silently lost.
                    Interlocked.Exchange(ref _refreshScheduled, 0);
                    if (!_disposed) Refresh();
                }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
        }
    }

    private void Refresh()
    {
        var current = _service.Snapshot().ToList();

        // Snapshot is authoritative: DownloadService caps terminal history at 50
        // and disposes evicted items. Keeping stale rows would both grow this page
        // forever and expose actions backed by an already-disposed CTS.
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!current.Any(item => ReferenceEquals(item, Items[i].Item)))
                Items.RemoveAt(i);
        }

        for (var targetIndex = 0; targetIndex < current.Count; targetIndex++)
        {
            var item = current[targetIndex];
            var row = Items.FirstOrDefault(r => ReferenceEquals(r.Item, item));
            if (row is null)
            {
                Items.Insert(targetIndex, new DownloadRow { Item = item });
            }
            else
            {
                var oldIndex = Items.IndexOf(row);
                if (oldIndex != targetIndex) Items.Move(oldIndex, targetIndex);
                row.Refresh();   // 已有行也要刷新（进度/状态/取消按钮可见性），修复：行是普通类不通知
            }
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>重复确认：继续下载。</summary>
    public void Confirm(DownloadRow row) => _service.ConfirmDuplicate(row.Item);

    /// <summary>取消任务：重复等待确认 → 丢弃；排队/下载中 → 真正取消（实机反馈）。</summary>
    public void Cancel(DownloadRow row)
    {
        if (row.Item.Status == DownloadStatus.Duplicate)
            _service.CancelDuplicate(row.Item);
        else
            _service.Cancel(row.Item);
    }

    private volatile bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.ItemChanged -= OnItemChanged;
    }
}

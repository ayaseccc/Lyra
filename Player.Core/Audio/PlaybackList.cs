using Player.Core.Library;

namespace Player.Core.Audio;

/// <summary>播放模式（PLAN 第 4 节）。</summary>
public enum PlayMode
{
    /// <summary>顺序播放，放完最后一首停止。</summary>
    Sequential,

    /// <summary>列表循环。</summary>
    RepeatAll,

    /// <summary>单曲循环（自动续播时重复本曲；手动点下一首仍然换歌）。</summary>
    RepeatOne,

    /// <summary>随机播放。</summary>
    Shuffle
}

/// <summary>
/// 播放列表的事务快照。调用方可在尝试切歌前保存，并在整批候选都无法打开时
/// 原样恢复列表、游标和随机播放顺序。
/// </summary>
public sealed class PlaybackListSnapshot
{
    internal PlaybackListSnapshot(
        TrackRecord[] items,
        string sourceName,
        int currentIndex,
        PlayMode mode,
        int[] shuffleOrder,
        int shufflePosition)
    {
        Items = items;
        SourceName = sourceName;
        CurrentIndex = currentIndex;
        Mode = mode;
        ShuffleOrder = shuffleOrder;
        ShufflePosition = shufflePosition;
    }

    internal TrackRecord[] Items { get; }
    internal string SourceName { get; }
    internal int CurrentIndex { get; }
    internal PlayMode Mode { get; }
    internal int[] ShuffleOrder { get; }
    internal int ShufflePosition { get; }
}

/// <summary>
/// 当前播放列表。取代 P0 的 PlaybackQueue：内容由 PlaylistService / LibraryService
/// 提供（全部歌曲、某个歌单、某张专辑、某个文件夹虚拟歌单……），本类只管顺序与模式。
/// </summary>
public sealed class PlaybackList
{
    private readonly List<TrackRecord> _items = new();
    private readonly List<int> _shuffleOrder = new();
    private readonly Random _random;

    private int _shufflePosition = -1;
    private PlayMode _mode = PlayMode.RepeatAll;

    public PlaybackList() : this(new Random())
    {
    }

    internal PlaybackList(Random random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    /// <summary>列表来源的显示名，例如「全部歌曲」「文件夹：OST」。</summary>
    public string SourceName { get; private set; } = string.Empty;

    public IReadOnlyList<TrackRecord> Items => _items;

    public int Count => _items.Count;

    public int CurrentIndex { get; private set; } = -1;

    public TrackRecord? Current =>
        CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;

    public PlayMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            if (_mode == PlayMode.Shuffle) RebuildShuffleOrder();
        }
    }

    public void Replace(IEnumerable<TrackRecord> tracks, string sourceName, int startIndex = 0)
    {
        _items.Clear();
        _items.AddRange(tracks);
        SourceName = sourceName;

        CurrentIndex = _items.Count == 0
            ? -1
            : Math.Clamp(startIndex, 0, _items.Count - 1);

        RebuildShuffleOrder();
    }

    public PlaybackListSnapshot CaptureSnapshot() => new(
        _items.ToArray(),
        SourceName,
        CurrentIndex,
        _mode,
        _shuffleOrder.ToArray(),
        _shufflePosition);

    public void RestoreSnapshot(PlaybackListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _items.Clear();
        _items.AddRange(snapshot.Items);
        SourceName = snapshot.SourceName;
        CurrentIndex = snapshot.CurrentIndex;
        _mode = snapshot.Mode;
        _shuffleOrder.Clear();
        _shuffleOrder.AddRange(snapshot.ShuffleOrder);
        _shufflePosition = snapshot.ShufflePosition;
    }

    /// <summary>「下一首播放」：把曲目插到当前曲目之后（队列空则成为唯一曲目）。
    /// 当前播放位置不变；返回插入后第一首的位置，供调用方决定是否立即切换。</summary>
    public int InsertAfterCurrent(IReadOnlyList<TrackRecord> tracks)
    {
        if (tracks.Count == 0) return -1;

        if (_mode == PlayMode.Shuffle && _shuffleOrder.Count != _items.Count)
            RebuildShuffleOrder();

        var at = CurrentIndex < 0 ? 0 : CurrentIndex + 1;
        _items.InsertRange(at, tracks);

        if (_mode == PlayMode.Shuffle)
        {
            // InsertAfterCurrent 的契约在随机模式下也必须成立：插入批次先按
            // 原顺序播放，再回到插队前尚未播放的随机序列。
            for (var i = 0; i < _shuffleOrder.Count; i++)
            {
                if (_shuffleOrder[i] >= at)
                    _shuffleOrder[i] += tracks.Count;
            }

            var orderPosition = Math.Clamp(_shufflePosition + 1, 0, _shuffleOrder.Count);
            _shuffleOrder.InsertRange(orderPosition, Enumerable.Range(at, tracks.Count));
        }
        else
        {
            RebuildShuffleOrder();
        }

        return at;
    }

    public TrackRecord? MoveTo(int index)
    {
        if (index < 0 || index >= _items.Count) return null;

        CurrentIndex = index;
        if (_mode == PlayMode.Shuffle)
            RebuildShuffleOrder();
        else
            SyncShufflePosition();
        return Current;
    }

    public TrackRecord? MoveToTrack(TrackRecord track)
    {
        var index = _items.FindIndex(t => t.Id == track.Id && t.Id != 0);
        if (index < 0) index = _items.FindIndex(t =>
            string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));

        return index >= 0 ? MoveTo(index) : null;
    }

    /// <param name="userInitiated">
    /// true = 用户点了「下一首」；false = 上一首自然放完的自动续播。
    /// 单曲循环下二者行为不同：自动续播重复本曲，手动点击照常换歌。
    /// </param>
    public TrackRecord? MoveNext(bool userInitiated)
    {
        if (_items.Count == 0) return null;

        if (_mode == PlayMode.RepeatOne && !userInitiated)
            return Current;

        if (_mode == PlayMode.Shuffle)
            return MoveNextShuffle();

        var next = CurrentIndex + 1;

        if (next >= _items.Count)
        {
            // 顺序播放放到底就停：自动续播和手动点下一首都一样
            if (_mode == PlayMode.Sequential) return null;
            next = 0;   // 列表循环回到开头
        }

        return MoveTo(next);
    }

    public TrackRecord? MovePrevious()
    {
        if (_items.Count == 0) return null;

        if (_mode == PlayMode.Shuffle)
            return MovePreviousShuffle();

        var previous = CurrentIndex - 1;
        if (previous < 0)
        {
            if (_mode == PlayMode.Sequential) return null;
            previous = _items.Count - 1;
        }

        return MoveTo(previous);
    }

    /// <summary>
    /// 看一眼"自动续播时的下一曲"是谁，但不移动游标。用于提前预载做无缝衔接。
    /// 单曲循环下返回当前曲（重复播放同一首同样是无缝的）。
    /// </summary>
    public TrackRecord? PeekNext()
    {
        if (_items.Count == 0) return null;

        if (_mode == PlayMode.RepeatOne) return Current;

        if (_mode == PlayMode.Shuffle)
        {
            if (_shuffleOrder.Count != _items.Count) return null;   // 洗牌表还没建好
            var next = _shufflePosition + 1;
            if (next >= _shuffleOrder.Count) return null;           // 一轮放完要重洗，无法预知
            return _items[_shuffleOrder[next]];
        }

        var index = CurrentIndex + 1;
        if (index >= _items.Count)
            return _mode == PlayMode.RepeatAll ? _items[0] : null;

        return _items[index];
    }

    public void Clear()
    {
        _items.Clear();
        _shuffleOrder.Clear();
        _shufflePosition = -1;
        CurrentIndex = -1;
        SourceName = string.Empty;
    }

    // ---------------- 随机 ----------------

    private void RebuildShuffleOrder()
    {
        _shuffleOrder.Clear();
        if (_items.Count == 0)
        {
            _shufflePosition = -1;
            return;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            if (i != CurrentIndex) _shuffleOrder.Add(i);
        }

        // Fisher-Yates
        for (var i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }

        // A newly selected/current track is the start of a fresh shuffle cycle.
        // Keeping it at position 0 guarantees every other track is visited once
        // before reshuffling and gives the next cycle an explicit no-repeat anchor.
        if (CurrentIndex >= 0)
            _shuffleOrder.Insert(0, CurrentIndex);
        _shufflePosition = CurrentIndex >= 0 ? 0 : -1;
    }

    private void SyncShufflePosition()
    {
        _shufflePosition = _shuffleOrder.IndexOf(CurrentIndex);
    }

    private TrackRecord? MoveNextShuffle()
    {
        if (_shuffleOrder.Count != _items.Count) RebuildShuffleOrder();
        if (_shuffleOrder.Count == 0) return null;

        var next = _shufflePosition + 1;
        if (next >= _shuffleOrder.Count)
        {
            RebuildShuffleOrder();   // 一轮放完，重新洗牌
            next = _shuffleOrder.Count > 1 ? 1 : 0;
        }

        _shufflePosition = next;
        CurrentIndex = _shuffleOrder[next];
        return Current;
    }

    private TrackRecord? MovePreviousShuffle()
    {
        if (_shuffleOrder.Count == 0) return null;

        var previous = _shufflePosition - 1;
        if (previous < 0) previous = _shuffleOrder.Count - 1;

        _shufflePosition = previous;
        CurrentIndex = _shuffleOrder[previous];
        return Current;
    }
}

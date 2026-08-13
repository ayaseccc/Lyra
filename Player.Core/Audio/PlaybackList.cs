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
/// 当前播放列表。取代 P0 的 PlaybackQueue：内容由 PlaylistService / LibraryService
/// 提供（全部歌曲、某个歌单、某张专辑、某个文件夹虚拟歌单……），本类只管顺序与模式。
/// </summary>
public sealed class PlaybackList
{
    private readonly List<TrackRecord> _items = new();
    private readonly List<int> _shuffleOrder = new();
    private readonly Random _random = new();

    private int _shufflePosition = -1;
    private PlayMode _mode = PlayMode.RepeatAll;

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

    public TrackRecord? MoveTo(int index)
    {
        if (index < 0 || index >= _items.Count) return null;

        CurrentIndex = index;
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
            _shuffleOrder.Add(i);

        // Fisher-Yates
        for (var i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }

        SyncShufflePosition();
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
            next = 0;
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

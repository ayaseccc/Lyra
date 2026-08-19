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
        int[] forcedNextIndices,
        int? randomPreviewIndex)
    {
        Items = items;
        SourceName = sourceName;
        CurrentIndex = currentIndex;
        Mode = mode;
        ForcedNextIndices = forcedNextIndices;
        RandomPreviewIndex = randomPreviewIndex;
    }

    internal TrackRecord[] Items { get; }
    internal string SourceName { get; }
    internal int CurrentIndex { get; }
    internal PlayMode Mode { get; }
    internal int[] ForcedNextIndices { get; }
    internal int? RandomPreviewIndex { get; }
}

/// <summary>
/// 当前播放列表。取代 P0 的 PlaybackQueue：内容由 PlaylistService / LibraryService
/// 提供（全部歌曲、某个歌单、某张专辑、某个文件夹虚拟歌单……），本类只管顺序与模式。
/// </summary>
public sealed class PlaybackList
{
    private readonly List<TrackRecord> _items = new();
    private readonly List<int> _forcedNextIndices = new();
    private readonly Random _random;

    private int? _randomPreviewIndex;
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
            ResetRandomState();
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

        ResetRandomState();
    }

    public PlaybackListSnapshot CaptureSnapshot() => new(
        _items.ToArray(),
        SourceName,
        CurrentIndex,
        _mode,
        _forcedNextIndices.ToArray(),
        _randomPreviewIndex);

    public void RestoreSnapshot(PlaybackListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _items.Clear();
        _items.AddRange(snapshot.Items);
        SourceName = snapshot.SourceName;
        CurrentIndex = snapshot.CurrentIndex;
        _mode = snapshot.Mode;
        _forcedNextIndices.Clear();
        _forcedNextIndices.AddRange(snapshot.ForcedNextIndices);
        _randomPreviewIndex = snapshot.RandomPreviewIndex;
    }

    /// <summary>「下一首播放」：把曲目插到当前曲目之后（队列空则成为唯一曲目）。
    /// 当前播放位置不变；返回插入后第一首的位置，供调用方决定是否立即切换。</summary>
    public int InsertAfterCurrent(IReadOnlyList<TrackRecord> tracks)
    {
        if (tracks.Count == 0) return -1;

        var at = CurrentIndex < 0 ? 0 : CurrentIndex + 1;

        if (_mode == PlayMode.Shuffle)
        {
            ShiftRandomIndicesForInsert(at, tracks.Count);
        }
        _items.InsertRange(at, tracks);

        if (_mode == PlayMode.Shuffle)
        {
            // 随机模式仍要兑现“下一首播放”：插入批次优先且保持原顺序，
            // 消费完后再回到插入前已经锁定的随机预载候选。
            _forcedNextIndices.InsertRange(0, Enumerable.Range(at, tracks.Count));
        }
        else
        {
            ResetRandomState();
        }

        return at;
    }

    public TrackRecord? MoveTo(int index)
    {
        if (index < 0 || index >= _items.Count) return null;

        CurrentIndex = index;
        ResetRandomState();
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
            return null;

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
            if (_forcedNextIndices.Count > 0)
                return ItemAt(_forcedNextIndices[0]);

            _randomPreviewIndex ??= ChooseRandomNextIndex();
            return _randomPreviewIndex is { } randomIndex ? ItemAt(randomIndex) : null;
        }

        var index = CurrentIndex + 1;
        if (index >= _items.Count)
            return _mode == PlayMode.RepeatAll ? _items[0] : null;

        return _items[index];
    }

    public void Clear()
    {
        _items.Clear();
        ResetRandomState();
        CurrentIndex = -1;
        SourceName = string.Empty;
    }

    // ---------------- Random（foobar 语义：每次独立抽取、没有历史） ----------------

    private void ResetRandomState()
    {
        _forcedNextIndices.Clear();
        _randomPreviewIndex = null;
    }

    private void ShiftRandomIndicesForInsert(int at, int count)
    {
        for (var i = 0; i < _forcedNextIndices.Count; i++)
        {
            if (_forcedNextIndices[i] >= at)
                _forcedNextIndices[i] += count;
        }

        if (_randomPreviewIndex is { } preview && preview >= at)
            _randomPreviewIndex = preview + count;
    }

    private TrackRecord? MoveNextShuffle()
    {
        int next;
        if (_forcedNextIndices.Count > 0)
        {
            next = _forcedNextIndices[0];
            _forcedNextIndices.RemoveAt(0);
        }
        else
        {
            next = _randomPreviewIndex ?? ChooseRandomNextIndex();
            _randomPreviewIndex = null;
        }

        if (next < 0 || next >= _items.Count) return null;
        CurrentIndex = next;
        return Current;
    }

    private int ChooseRandomNextIndex()
    {
        if (_items.Count == 0) return -1;

        // foobar Random 是无历史、有放回抽取：每次都从完整列表独立选择，
        // 因而理论上允许连续抽中同一首。PeekNext 会锁定本次结果，供预载
        // 与真正切歌共用，但不会形成可返回的历史。
        return _random.Next(_items.Count);
    }

    private TrackRecord? ItemAt(int index)
        => index >= 0 && index < _items.Count ? _items[index] : null;

}

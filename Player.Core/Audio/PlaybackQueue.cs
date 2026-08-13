namespace Player.Core.Audio;

/// <summary>
/// P0 的最小播放队列（纯内存）：承载"拖进来的这一批文件"，让上一首/下一首可用。
/// P1 会被 PlaylistService / 媒体库列表取代（PLAN 第 5 节）。
/// </summary>
public sealed class PlaybackQueue
{
    private readonly List<string> _items = new();

    public IReadOnlyList<string> Items => _items;

    public int Count => _items.Count;

    public int CurrentIndex { get; private set; } = -1;

    public string? Current =>
        CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;

    public bool HasNext => CurrentIndex >= 0 && CurrentIndex + 1 < _items.Count;

    public bool HasPrevious => CurrentIndex > 0;

    /// <summary>整批替换，并把游标指向第一项。</summary>
    public void Replace(IEnumerable<string> paths)
    {
        _items.Clear();
        _items.AddRange(paths);
        CurrentIndex = _items.Count > 0 ? 0 : -1;
    }

    public void Add(IEnumerable<string> paths)
    {
        var before = _items.Count;
        _items.AddRange(paths);
        if (CurrentIndex < 0 && _items.Count > before)
            CurrentIndex = before;
    }

    public string? MoveNext()
    {
        if (!HasNext) return null;
        CurrentIndex++;
        return Current;
    }

    public string? MovePrevious()
    {
        if (!HasPrevious) return null;
        CurrentIndex--;
        return Current;
    }

    public string? MoveTo(int index)
    {
        if (index < 0 || index >= _items.Count) return null;
        CurrentIndex = index;
        return Current;
    }

    public void Clear()
    {
        _items.Clear();
        CurrentIndex = -1;
    }
}

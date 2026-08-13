namespace Player.Core.Online;

/// <summary>
/// 客户端节流：滑动窗口令牌桶。服务端限制 20 次/分，这里用 18 次/分留余量（PLAN 第 6 节）。
/// 判定逻辑做成纯函数（<see cref="TryTake"/>），方便 harness 离线断言。
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly Queue<DateTime> _stamps = new();
    private readonly int _capacity;
    private readonly TimeSpan _window;

    public TokenBucket(int capacity = 18, TimeSpan? window = null)
    {
        _capacity = Math.Max(1, capacity);
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public int Capacity => _capacity;

    /// <summary>窗口内还剩几次可用（给界面显示）。</summary>
    public int AvailableNow
    {
        get
        {
            lock (_gate)
            {
                Trim(DateTime.UtcNow);
                return Math.Max(0, _capacity - _stamps.Count);
            }
        }
    }

    /// <summary>
    /// 尝试取一个令牌。取到返回 true；取不到返回 false，并给出还要等多久。
    /// 纯粹按传入时间判断，不碰系统时钟，便于测试。
    /// </summary>
    public bool TryTake(DateTime utcNow, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            Trim(utcNow);

            if (_stamps.Count < _capacity)
            {
                _stamps.Enqueue(utcNow);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            // 最早那次调用滑出窗口时就能再发一次
            retryAfter = _window - (utcNow - _stamps.Peek());
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>排队等一个令牌。所有 API 调用都要先过这里。</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (TryTake(DateTime.UtcNow, out var retryAfter)) return;

            var delay = retryAfter + TimeSpan.FromMilliseconds(50);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Trim(DateTime utcNow)
    {
        while (_stamps.Count > 0 && utcNow - _stamps.Peek() >= _window)
            _stamps.Dequeue();
    }
}

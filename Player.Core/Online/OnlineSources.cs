using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Core.Online;

/// <summary>
/// 在线源注册表（P4）：GdSource 默认 + ChkszSource 网易云兜底。
/// 提供统一的按键查找、异步探测（不可用源灰显用 IsAvailable）。
/// </summary>
public sealed class OnlineSources : IDisposable
{
    private readonly List<IOnlineSource> _all = new();
    private readonly Dictionary<string, IOnlineSource> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _probeGate = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _probeTask;
    private bool _disposed;

    public OnlineSources(ChkszClient chksz)
    {
        Add(new GdSource());
        Add(new ChkszSource(chksz));
    }

    public IReadOnlyList<IOnlineSource> All => _all;

    /// <summary>默认源：GdSource（零 Key 零额度，P4 架构默认）。</summary>
    public IOnlineSource Default => _all[0];

    public IOnlineSource? Get(string key) => _byKey.TryGetValue(key, out var source) ? source : null;

    private void Add(IOnlineSource source)
    {
        _all.Add(source);
        _byKey[source.Key] = source;
    }

    /// <summary>
    /// 每次进入在线页可重新探测可用性；并发调用共享同一次正在运行的探测。
    /// 调用方取消只停止等待，不会让短暂离开搜索页中断底层探测；注册表释放时才终止请求。
    /// </summary>
    public Task ProbeAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Task probe;
        lock (_probeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_probeTask is null || _probeTask.IsCompleted)
                _probeTask = ProbeAllCoreAsync(_disposeCts.Token);
            probe = _probeTask;
        }

        return probe.WaitAsync(ct);
    }

    private async Task ProbeAllCoreAsync(CancellationToken ct)
    {
        foreach (var source in _all)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await source.ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 探测异常不外抛（源自身 ProbeAsync 通常已内化错误）
                Serilog.Log.Debug(ex, "探测在线源 {Key} 失败", source.Key);
            }
        }
    }

    public void Dispose()
    {
        Task? probeTask;
        lock (_probeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
            probeTask = _probeTask;
        }

        foreach (var source in _all)
        {
            if (source is not ChkszSource) source.Dispose();   // ChkszClient 由外部释放
        }

        if (probeTask is null || probeTask.IsCompleted)
        {
            _disposeCts.Dispose();
        }
        else
        {
            _ = probeTask.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception; // 退出取消不留未观察异常
                    ((CancellationTokenSource)state!).Dispose();
                },
                _disposeCts,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

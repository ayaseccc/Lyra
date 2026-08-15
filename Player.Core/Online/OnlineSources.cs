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

    /// <summary>逐个探测可用性（启动时异步调用；失败置 IsAvailable=false 用于灰显）。</summary>
    public async Task ProbeAllAsync(CancellationToken ct)
    {
        foreach (var source in _all)
        {
            try
            {
                await source.ProbeAsync(ct).ConfigureAwait(false);
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
        if (_disposed) return;
        _disposed = true;
        foreach (var source in _all)
        {
            if (source is not ChkszSource) source.Dispose();   // ChkszClient 由外部释放
        }
    }
}

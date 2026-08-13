using Serilog;

namespace Player.Core.Online;

/// <summary>
/// 今日额度追踪。数字**一律以 X-Quota-* 响应头为准**，代码里不写死 400（PLAN 第 6 节）。
/// </summary>
public sealed class QuotaTracker
{
    public int? FreeRemaining { get; private set; }

    public int? PaidRemaining { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>额度数字变了。可能在任意线程触发。</summary>
    public event EventHandler? Changed;

    /// <summary>播放条上的指示文案。</summary>
    public string DisplayText
    {
        get
        {
            if (FreeRemaining is null) return "额度未知";

            var text = $"API 剩 {FreeRemaining}";
            if (PaidRemaining is > 0) text += $" (+{PaidRemaining})";
            return text;
        }
    }

    public bool IsExhausted => FreeRemaining is <= 0 && PaidRemaining is null or <= 0;

    /// <summary>从响应头里取额度。头名大小写不敏感。</summary>
    public void Update(Func<string, string?> headerLookup)
    {
        var free = ParseHeader(headerLookup("X-Quota-Free-Remaining"));
        var paid = ParseHeader(headerLookup("X-Quota-Paid-Remaining"));

        if (free is null && paid is null) return;

        var changed = free != FreeRemaining || paid != PaidRemaining;

        if (free is not null) FreeRemaining = free;
        if (paid is not null) PaidRemaining = paid;
        UpdatedAt = DateTimeOffset.Now;

        if (!changed) return;

        Log.Debug("额度更新：免费 {Free}，付费 {Paid}", FreeRemaining, PaidRemaining);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>纯函数，便于离线断言：头值解析不出数字就返回 null（而不是 0，0 会被误当成额度用尽）。</summary>
    public static int? ParseHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value.Trim(), out var n) ? n : null;
    }
}

using System;

namespace Player.Core.Online;

/// <summary>在线 API 地址校验（P4 实机反馈：设置页允许自填地址）。</summary>
public static class OnlineUrl
{
    /// <summary>必须是 http(s):// 开头的绝对地址（空/非法返回 false，调用方回落官方默认）。</summary>
    public static bool IsHttp(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || url.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}

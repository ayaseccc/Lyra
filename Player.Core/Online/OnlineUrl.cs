using System;

namespace Player.Core.Online;

/// <summary>在线 API 地址校验（P4 实机反馈：设置页允许自填地址）。</summary>
public static class OnlineUrl
{
    /// <summary>必须是 http(s):// 开头的绝对地址（空/非法返回 false，调用方回落官方默认）。</summary>
    public static bool IsHttp(string? url) => TryGetHttpUri(url, out _);

    /// <summary>带凭据的端点只能使用 HTTPS。</summary>
    public static bool IsHttps(string? url) =>
        TryGetHttpUri(url, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}

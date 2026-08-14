using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Player.Core.Infra;

namespace Player.App.Infra;

/// <summary>
/// 封面缓存：hash → 已解码的位图。列表虚拟化后只有可见行会解码，
/// 统一按 160px 宽解码，避免整张 1500px 封面进内存。
/// </summary>
public static class CoverImageCache
{
    private const int DecodeWidth = 160;

    /// <summary>右侧大封面解码宽度（UI-R4：160px 放大到 ~300px 显示会糊，用 2.5 倍解码）。</summary>
    private const int LargeDecodeWidth = 760;

    /// <summary>缓存条目上限。超出后整体清空重来，避免长时间浏览大曲库时内存只涨不降。</summary>
    private const int MaxEntries = 600;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    /// <summary>大封面缓存：只留最近几张（760px 解码，内存占用大，不能进通用缓存）。</summary>
    private static readonly ConcurrentDictionary<string, ImageSource?> LargeCache = new();

    public static ImageSource? Get(string? coverHash)
    {
        if (string.IsNullOrEmpty(coverHash)) return null;

        if (Cache.Count > MaxEntries) Cache.Clear();

        return Cache.GetOrAdd(coverHash, hash => Decode(coverHash, DecodeWidth));
    }

    /// <summary>右侧信息栏大封面：高清解码（UI-R4 修复封面模糊）。</summary>
    public static ImageSource? GetLarge(string? coverHash)
    {
        if (string.IsNullOrEmpty(coverHash)) return null;

        if (LargeCache.Count > 8) LargeCache.Clear();

        return LargeCache.GetOrAdd(coverHash, hash => Decode(coverHash, LargeDecodeWidth));
    }

    private static ImageSource? Decode(string coverHash, int decodeWidth)
    {
        try
        {
            var path = Path.Combine(AppPaths.CoversDir, coverHash + ".jpg");
            if (!File.Exists(path)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = decodeWidth;
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立即读完并释放文件句柄
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();                                  // 冻结后可跨线程共享
            return image;
        }
        catch
        {
            return null;   // 封面坏了不影响播放
        }
    }

    public static void Clear() => Cache.Clear();
}

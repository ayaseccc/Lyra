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

    /// <summary>缓存条目上限。超出后整体清空重来，避免长时间浏览大曲库时内存只涨不降。</summary>
    private const int MaxEntries = 600;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    public static ImageSource? Get(string? coverHash)
    {
        if (string.IsNullOrEmpty(coverHash)) return null;

        if (Cache.Count > MaxEntries) Cache.Clear();

        return Cache.GetOrAdd(coverHash, hash =>
        {
            try
            {
                var path = Path.Combine(AppPaths.CoversDir, hash + ".jpg");
                if (!File.Exists(path)) return null;

                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = DecodeWidth;
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
        });
    }

    public static void Clear() => Cache.Clear();
}

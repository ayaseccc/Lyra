namespace Player.Core.Audio;

/// <summary>
/// 支持的音频格式（PLAN 第 5 节）。
/// mp3 / mp2 / mp1 / wav / aiff / ogg 由 bass.dll 内置支持；
/// flac→bassflac、ape→bassape、wv→basswv、opus→bassopus；
/// m4a / aac / alac 在 Windows 10+ 由 BASS 通过系统编解码器支持，另有 bass_aac / bassalac 插件兜底。
/// </summary>
public static class AudioFormats
{
    public static readonly string[] Extensions =
    {
        ".mp3", ".mp2", ".mp1", ".flac", ".m4a", ".aac", ".alac", ".ape",
        ".wv", ".ogg", ".oga", ".opus", ".wav", ".aiff", ".aif", ".aifc"
    };

    private static readonly HashSet<string> Lookup = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && Lookup.Contains(ext);
    }

    /// <summary>用于 WPF OpenFileDialog 的过滤器字符串。</summary>
    public static string DialogFilter =>
        "音频文件|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|所有文件|*.*";
}

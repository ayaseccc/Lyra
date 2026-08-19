using System;
using System.Collections.Generic;
using System.IO;

namespace Player.Core.Downloads;

/// <summary>
/// 下载命名模板渲染（纯函数，harness 可离线断言）。
/// 模板占位符：{AlbumArtist} / {Album} / {TrackNo} / {Title}；未知占位符原样保留。
/// </summary>
public static class DownloadTemplater
{
    private static readonly HashSet<string> ReservedDeviceNames = new(
        new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 渲染模板为相对路径（如 "周杰伦/叶惠美/01 - 晴天"），不处理后缀。
    /// 空值占位符（如在线曲目无 TrackNo）连同相邻的 " - " 分隔段一起清理，避免 "_ - 晴天" 之类残留。
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            var component = SanitizeComponent(value);
            var token = "{" + key + "}";
            if (component == "_" && value is null or "")
            {
                // 空值：连同相邻的 " - " 段一起移除（{TrackNo} - {Title} 与 " - {TrackNo}" 两种形态）
                result = result.Replace(token + " - ", string.Empty, StringComparison.Ordinal);
                result = result.Replace(" - " + token, string.Empty, StringComparison.Ordinal);
                result = result.Replace(token, string.Empty, StringComparison.Ordinal);
            }
            else
            {
                result = result.Replace(token, component, StringComparison.Ordinal);
            }
        }
        // 空值段清理可能留下双斜杠（{Album} 空时 {Artist}//{Title}），归一（审查修复）
        while (result.Contains("//", StringComparison.Ordinal))
            result = result.Replace("//", "/", StringComparison.Ordinal);
        return result.Trim('/', ' ');
    }

    /// <summary>
    /// 将渲染结果压平为单个文件名。下载界面选择的是目标目录，
    /// 因此默认不应再隐式创建歌手/专辑子目录；模板中的目录段只用于
    /// 兼容旧配置，实际落盘统一直接放到用户选择的目录。
    /// </summary>
    public static string FlattenRelativePath(string renderedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(renderedRelativePath)) return "_";

        // Windows treats both separators as path delimiters. Normalize explicitly
        // so the helper remains deterministic in harnesses running on other hosts.
        var normalized = renderedRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ' ');
        var fileName = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(fileName) ? "_" : fileName;
    }

    /// <summary>路径段清洗：去掉 Windows 非法字符与首尾空白。</summary>
    public static string SanitizeComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "_";   // 空白组件回 "_"，避免空路径段
        var trimmed = value.Trim();
        var chars = trimmed.ToCharArray();
        foreach (var c in Path.GetInvalidFileNameChars())
            for (var i = 0; i < chars.Length; i++)
                if (chars[i] == c) chars[i] = '_';
        var cleaned = new string(chars).Trim();
        // 不能是 Windows 保留名（CON/PRN/AUX/NUL/COM1..）与末尾点
        if (cleaned.Length > 0 && cleaned[^1] == '.') cleaned = cleaned[..^1] + "_";
        if (cleaned.Length == 0) cleaned = "_";
        var deviceStem = cleaned.Split('.', 2)[0];
        if (ReservedDeviceNames.Contains(deviceStem)) cleaned = "_" + cleaned;
        return cleaned;
    }

    /// <summary>规范化下载目标，并保证最终路径严格位于下载根目录中。</summary>
    public static string ResolveTargetPath(string downloadRoot, string renderedRelativePath, string extension)
    {
        if (string.IsNullOrWhiteSpace(downloadRoot))
            throw new ArgumentException("下载目录为空", nameof(downloadRoot));
        if (Path.IsPathRooted(renderedRelativePath))
            throw new InvalidOperationException("下载命名模板不能生成绝对路径");
        if (string.IsNullOrWhiteSpace(extension)
            || extension[0] != '.'
            || !string.Equals(Path.GetFileName(extension), extension, StringComparison.Ordinal))
            throw new InvalidOperationException("下载文件扩展名无效");

        var root = Path.GetFullPath(downloadRoot);
        var relative = string.IsNullOrWhiteSpace(renderedRelativePath) ? "_" : renderedRelativePath;
        var target = Path.GetFullPath(Path.Combine(root, relative + extension));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("下载命名模板生成的路径越出了下载目录");

        return target;
    }

    /// <summary>为冲突文件生成稳定名称：原名、原名 (2)、原名 (3)…</summary>
    public static string CollisionPath(string desiredPath, int ordinal)
    {
        if (ordinal <= 1) return desiredPath;
        var directory = Path.GetDirectoryName(desiredPath)
            ?? throw new InvalidOperationException("下载目标缺少目录");
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        return Path.Combine(directory, $"{stem} ({ordinal}){extension}");
    }

    /// <summary>从直链推断扩展名（.flac/.mp3/.m4a/.wav 等）；带参数去掉 query。</summary>
    public static string ExtensionFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return ".bin";
            return ext.ToLowerInvariant();
        }
        catch
        {
            return ".bin";
        }
    }
}

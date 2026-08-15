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
        return result.Trim('/', ' ');
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
        return cleaned;
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

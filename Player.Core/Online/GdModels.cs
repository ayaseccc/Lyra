using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Player.Core.Online;

/// <summary>
/// GD Studio API 模型（P4-1 打样 docs/api-samples/gd/，照真实响应结构，不猜字段）。
/// 注意：搜索/取流失败不是 HTTP 错误码，而是 200 + 特定 JSON 形态——见 README。
/// </summary>
public static class GdModels
{
    /// <summary>搜索/专辑列表项（types=search）。artist 是数组；url_id 文档标注废弃但接口仍在返回。</summary>
    public sealed record Track(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artist")] IReadOnlyList<string>? Artist,
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("pic_id")] string? PicId,
        [property: JsonPropertyName("url_id")] string? UrlId,
        [property: JsonPropertyName("lyric_id")] string? LyricId,
        [property: JsonPropertyName("source")] string? Source);

    /// <summary>取流结果（types=url）。Br=-1 且 Address 为空 = 取流失败（要降级或提示）。</summary>
    public sealed record Url(
        [property: JsonPropertyName("url")] string Address,
        [property: JsonPropertyName("br")] int Br,
        [property: JsonPropertyName("size")] long Size);

    /// <summary>专辑图（types=pic）。</summary>
    public sealed record Pic(
        [property: JsonPropertyName("url")] string Url);

    /// <summary>歌词（types=lyric）。Translation 对应接口的 tlyric（翻译），不一定会返回。</summary>
    public sealed record Lyric(
        [property: JsonPropertyName("lyric")] string? Text,
        [property: JsonPropertyName("tlyric")] string? Translation);
}

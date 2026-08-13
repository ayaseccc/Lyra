# ChKSz API 真实响应样本

这些是用真实 Key **各打一次样**得到的响应，已脱敏后入仓。写模型类以它们为准，不要凭文档猜字段（PLAN 第 6 节「实现须知」）。

打样时间：2026-08-14 · 基础地址 `https://api.chksz.com`

## 脱敏说明

- 请求 URL 里的 `apikey` 从不出现在样本里（响应体本身也不回显它）。
- `163_music` 的 `data.url` 是**有时效的直链**，已替换为占位符；实际使用时每次播放现取，不缓存。
- `163_playlist` 的 `tracks` 只保留前 3 条，原始条数记在 `_tracksTruncatedFrom`。

## 响应头（额度与限流的唯一依据）

每个响应都带：

```
x-quota-free-remaining: 399
x-quota-paid-remaining: 0
```

代码里**不许把 400 写死**，一律以这两个头为准（PLAN 第 6 节）。触发限流时应有 `retry-after`，本轮打样没触发，未观测到。

## 各端点结构要点

| 文件 | 端点 | 结构要点 |
| --- | --- | --- |
| `163_search.json` | `/api/163_search` | `data.songs[]`，字段 `id / name / artists / album / picUrl / duration`。**`artists` 是用 `/` 连接的字符串**（如 `周杰伦-/A-LNK`），不是数组；`duration` 单位毫秒；另有 `data.total` |
| `163_music.json` | `/api/163_music` | `data` 里有 `id / url / br / level / size / md5 / name / artist / album / picUrl`。`br` 是实际码率，`level` 回显请求的音质 |
| `163_music.error-404.json` | 同上 | 灰色歌曲/该音质无资源时 **HTTP 404**，体为 `{code:404, msg:"Music URL not found, song may be unavailable at this quality level"}`。这是 P4 音质回退链的触发点 |
| `163_lyric.json` | `/api/163_lyric` | `data` 恒有四个字符串字段 `lrc / tlyric / romalrc / klyric`，**后三个可能是空串**（本样本 `romalrc`、`klyric` 均为空） |
| `163_playlist.json` | `/api/163_playlist` | `data`：`id / name / coverImgUrl / creator.nickname / trackCount / tracks[]`；曲目字段是 `id / name / ar[] / al`，其中 `ar` 是 `[{name}]` 数组、`al` 是 `{name, picUrl}` 对象 |

## 与规划不一致、需要注意的地方

1. **歌单曲目没有时长字段**。PLAN 第 7.3 节的匹配算法写的是"标题+歌手规范化后精确/模糊匹配"，本来也没依赖时长；但 P5 做下载重复检测时提到的"时长差 < 2s"在歌单同步这条路径上拿不到时长，只能靠标题+歌手。
2. **搜索结果以 UGC/翻唱为主**。用「晴天 周杰伦」搜出来的前 5 条全是翻唱，原版（id 186016）不在其中。所以 P3 的歌词 ID 匹配**必须把时长差当作硬条件**（PLAN 第 7.2 节：时长差 < 3 秒），只靠标题+歌手会大量匹配到翻唱版本。
3. **`163_music` 的成功与否要看 HTTP 状态**：body 里的 `code` 与 HTTP 状态一致，失败时没有 `data` 字段。判断成功不能只看有没有 `data`（PLAN 第 6 节已强调）。

# GD Studio API 打样（P4-1，2026-08-15 实测）

基础地址：https://music-api.gdstudio.xyz/api.php（文档 docs/GD音乐API.txt）

## 样本清单

| 文件 | 端点 | 参数 | 说明 |
|---|---|---|---|
| gd-search.json | search | source=kuwo, name=周杰伦, count=3 | 搜索返回结构 |
| gd-search-album.json | search | source=kuwo_album, name=叶惠美, count=3 | 专辑曲目（高级用法 source_album） |
| gd-url-br999-netease.json | url | source=netease, id=1859659336, br=999 | 请求 999 实际返回 br=430（降级要标注实际值） |
| gd-url-br320-joox.json | url | source=joox, id=..., br=320 | 完整直链（vkey 有时效） |
| gd-url-br320.json / gd-url-br999.json | url | source=kuwo, id=228908 | 取流失败样本：url 空 + br=-1 |
| gd-pic.json | pic | source=kuwo, id=120/s3s94/93/211513640.jpg, size=300 | 专辑图返回 |
| gd-lyric.json | lyric | source=kuwo, id=228908 | LRC 歌词（晴天全量） |

## 实测结论（模型与源策略的输入）

1. **源可用性（2026-08-15 实测）**：
   - kuwo：搜索 / 专辑 / 图 / 词 ✅，取流 url ❌（恒空）
   - netease：搜索 / 专辑 空 []，取流 ✅（已知 id 可用；请求 br=999 返回 430）
   - joox：搜索 / 取流 ✅（全链路可用）
   - bilibili：搜索空 []
   - tencent / apple / ytmusic / spotify / qobuz / tidal：明确不支持（detail 字段报错）
   → 源状态会变，逐源可用性探测 + 不可用源灰显是刚需；默认源按 PLAN 为 netease（搜索空时自动落回可用源或提示）。
2. **错误形态**：
   - 源不支持：HTTP 200 + JSON detail 字段（不是 HTTP 错误码！）
   - 取流失败：HTTP 200 + url 空 + br=-1
   - 搜不到：HTTP 200 + 空数组 []
   → 模型与客户端必须按 JSON 内容判错，不能只看状态码。
3. **直链有时效**：url 响应的直链带 vkey/时间戳，不可收藏复用（已验证 HEAD 206 可下载，但会过期）。
4. 响应统一带 from 字段（模型忽略）。

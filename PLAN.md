# 本地音乐播放器 开发规划（PLAN.md）

> 技术栈：C# / .NET 8 / WPF + BASS（含 BASSASIO）
> 在线能力：ChKSz API（网易云为主，QQ/酷狗留作扩展）
> 用法：把本文件放在仓库根目录命名为 `PLAN.md`；分工模式为「规划归本文档、执行归 AI」，开工时把第 10 节的执行总则 + 对应阶段提示词发给执行 AI（Claude Opus / Claude Code / Codex 均可）。
> 日期：2026-08-13

---

## 1. 项目概述

做一个 Windows 桌面本地音乐播放器，定位类似 foobar2000：轻量、启动快、以曲库列表为核心，但界面走现代简洁风格。核心卖点有两个：一是 **ASIO / WASAPI 独占输出**，面向 HiFi 场景做到位完美（bit-perfect）播放；二是通过 **ChKSz API 接入网易云在线能力**，实现在线搜索播放、歌词自动获取、歌单单向同步、歌曲下载入库。

**非目标（首期明确不做）**：账号体系与社区功能、移动端、DSD 播放、QQ/酷狗音源（架构上预留，见第 12 节 Backlog）、插件系统。

## 2. 技术选型与依赖清单

| 领域 | 选择 | 理由 |
| --- | --- | --- |
| 运行时 / UI | .NET 8 (LTS) + WPF | Windows 桌面成熟方案，开发效率高，运行足够轻量 |
| MVVM | CommunityToolkit.Mvvm | 官方社区库，样板代码少 |
| UI 样式 | WPF-UI（`WPF-UI` NuGet，lepo.co） | 快速获得 Fluent 现代观感，避免自绘全部控件；保持紧凑密度 |
| 音频引擎 | BASS + 插件（un4seen） | ASIO/WASAPI 独占/无缝播放/URL 网络流全部现成，国内多款 HiFi 播放器同款方案；个人使用免费 |
| BASS 的 C# 封装 | ManagedBass、ManagedBass.Asio、ManagedBass.Mix、ManagedBass.Wasapi | 维护良好的官方风格封装 |
| 标签读写 | TagLibSharp | 读写 ID3/FLAC/APE 等标签与封面的事实标准 |
| 数据库 | Microsoft.Data.Sqlite（手写轻量 DAL，不用 EF） | 单文件、零部署、够快 |
| HTTP / JSON | HttpClient + System.Text.Json | 内置即可，无需第三方 |
| 日志 | Serilog（滚动文件） | 排查 ASIO 设备问题时非常需要 |

**需要下载的原生 DLL**（来自 www.un4seen.com，取 x64 版本，放到输出目录随程序分发）：

`bass.dll`、`bassasio.dll`、`bassmix.dll`、`basswasapi.dll`，以及格式插件 `bassflac.dll`、`bass_aac.dll`（aac/mp4）、`bassalac.dll`（alac）、`bassape.dll`、`basswv.dll`、`bassopus.dll`。插件通过 `Bass.PluginLoad()` 在启动时加载。

> **P0 实测修正（2026-08-13）**：① 原文把 alac 归在 `bass_aac` 名下，实际 ALAC 是独立 add-on `bassalac.dll`，`bass_aac` 只负责 AAC/MP4；② `bass_aac` 属第三方 add-on，下载路径为 `un4seen.com/files/z/2/bass_aac24.zip`，用 `files/bass_aac24.zip` 会 404；③ Windows 10 及以上 BASS 本身即可经系统编解码器播放 aac/m4a/alac，这两个插件只作兜底；④ 各 zip 的 64 位 DLL 在 `x64/` 子目录内，当前 BASS 版本 2.4.18.3。以上 7 个 DLL 已放入仓库 `native/x64/`，由 `Player.App.csproj` 自动复制到输出目录；`bassasio/bassmix/basswasapi` 留到 P2 再补。

**许可提示**：BASS 系列对非商业用途免费；若日后商用需向 un4seen 购买授权。BASSASIO 自带 ASIO 支持，**不需要**去 Steinberg 下载 ASIO SDK 或自己编译任何东西——这是选 BASS 而非 cpal/JUCE 路线省下的最大的坑。

## 3. 总体架构

解决方案分两个项目，UI 与核心逻辑严格分离（核心层不引用任何 WPF 类型，方便测试和未来换 UI）：

```text
Player.sln
├── Player.App     (WPF)  视图、ViewModel、样式、托盘/SMTC 等系统集成
└── Player.Core    (类库) 全部业务逻辑
    ├── Audio/      PlaybackEngine、OutputDeviceManager（ASIO/WASAPI/DirectSound 后端）
    ├── Library/    LibraryScanner、LibraryService、PlaylistService
    ├── Online/     ChkszClient、OnlineMusicService、QuotaTracker
    ├── Lyrics/     LyricsService、LrcParser
    ├── Download/   DownloadService（队列）
    └── Infra/      ConfigService、CacheService、Db、Log
```

层间通信用 CommunityToolkit 的 Messenger（如 `TrackChangedMessage`、`QuotaUpdatedMessage`、`PlaybackStateMessage`），避免服务间直接互相引用。

## 4. 音频引擎（ASIO 核心）

播放链路统一为：**解码流 →（可选 mixer）→ 输出后端**。本地文件用 `Bass.CreateStream(path, Decode | Float)`，在线播放用 `Bass.CreateStream(url, ...)` 网络流，两者进入同一条输出链路，因此在线歌曲同样走 ASIO。

输出后端做成可切换的三种实现，统一接口 `IOutputBackend`：

1. **ASIO**（BassAsio）：枚举 ASIO 设备；初始化时把 ASIO 采样率设为当前曲目采样率（设备支持即为位完美），通过 `AsioProcedure` 回调从解码流拉数据。
2. **WASAPI 独占 / 共享**（BassWasapi）：独占模式同样可位完美，作为没有 ASIO 驱动时的次选。
3. **DirectSound（BASS 默认输出）**：兜底，保证任何机器开箱能响。

关键设计点：

- **采样率策略**：切歌时若新曲采样率与当前 ASIO 采样率不同，重设 ASIO 采样率（允许极短间隙）；设置页提供"固定输出采样率 + 重采样"选项给不支持切换的设备。
- **无缝播放**：同采样率的连续曲目经 bassmix 的 decode mixer 衔接（`BassFlags.MixerNoRampin`），下一曲提前 5 秒预创建解码流。
- **音量**：软件音量用 float 属性衰减；ASIO 位完美场景在 UI 上提示"音量 100% 时为位完美输出"。
- **健壮性**：设备被占用/拔出时捕获错误，自动回退到默认 DirectSound 并弹提示，绝不崩溃；所有 BASS 错误码写日志。
- **常规能力**：播放/暂停/停止/精确 seek/上一首下一首/顺序、循环、单曲循环、随机模式/播放队列（插播"下一首播放"）。

## 5. 本地媒体库

- **扫描**：设置页配置多个根目录。首次全量扫描（并行读标签，进度条显示）；之后启动时按 `mtime + filesize` 快速比对增量，运行中用 `FileSystemWatcher` 监听变动。
- **支持格式**：mp3 / flac / m4a(aac·alac) / ape / wv / ogg / opus / wav / aiff。
- **搜索与排序**：顶部过滤框对 标题/歌手/专辑 即时过滤（万级曲库内存过滤足够，无需 FTS）；列表多列可点击排序，列显示 标题/歌手/专辑/时长/格式/采样率/位深。
- **歌单**：本地歌单增删改、拖拽排序；支持 m3u8 导入导出；文件与文件夹可直接拖入窗口加入列表。

SQLite 核心表结构：

```sql
tracks(id, path UNIQUE, title, artist, album, album_artist, track_no, disc_no,
       duration_ms, sample_rate, bit_depth, bitrate, file_size, mtime,
       added_at, play_count, last_played, cover_hash)   -- cover_hash 为 P1 实现时新增
playlists(id, name, sort_order, source, source_id, synced_at)   -- source: local | netease
playlist_items(playlist_id, position, track_id NULL,            -- 本地曲目指向 tracks
       online_id NULL, online_title, online_artist, online_album, online_duration_ms)
lyrics_cache(cache_key PRIMARY KEY, content_json, fetched_at)   -- cache_key 如 "163:12345"
settings(key PRIMARY KEY, value)
```

封面不入库，抽取后按内容 hash 存 `data/cache/covers/*.jpg`，列表用缩略图缓存。

## 6. ChKSz API 接入层

基础地址 `https://api.chksz.com`，所有业务接口通过 URL 查询参数传 `apikey`。首期只封装网易云四个端点，QQ/酷狗在接口层预留：

| 方法 | 端点 | 用途 |
| --- | --- | --- |
| `GetSongUrlAsync(id, level, type=json)` | `/api/163_music` | 解析播放/下载直链，level：`standard/exhigh/lossless/hires/jyeffect/sky/jymaster`（默认 jymaster） |
| `SearchAsync(keyword, limit=30, offset=0)` | `/api/163_search` | 搜索歌名/歌手/专辑 |
| `GetLyricAsync(id)` | `/api/163_lyric` | 原文 + 翻译 + 罗马音歌词（后两者可能为空） |
| `GetPlaylistAsync(id)` | `/api/163_playlist` | 歌单详情（名称/封面/创建者/曲目列表），大歌单要设长超时 |

**Key 管理**：Key 只存在 `data/config.json`，设置页里填写；永不写入日志、代码、仓库（`data/` 整目录进 `.gitignore`）。程序内日志打印 URL 时必须脱敏 apikey 参数。

**额度与限流**（这是接入层的核心职责，所有调用必须经过它）：

- 服务端限制：每分钟 20 次；免费额度当前为**每天 400 次**（UTC+8 结算；平台可能调整，运行时一律以 `X-Quota-Free-Remaining` 响应头为准，不要把数字写死在代码里），付费 1 LDC = 10 次。
- 客户端节流：内置令牌桶，上限 **18 次/分**留出余量；所有 API 调用排队通过。
- 每次响应读取 `X-Quota-Free-Remaining` / `X-Quota-Paid-Remaining` 响应头，更新状态栏的"今日额度"指示器。
- 错误处理按文档执行：`400` 修参数不重试；`401` 提示检查 Key；`402` 提示额度用尽（等次日或兑换 LDC）；`403` 转述原因并停止；`429` 按 `Retry-After` 等待后**最多重试一次**；`503` 提示稍后再试，不轮询。判断成功以 HTTP 状态和 `msg` 为准，不能只看返回里有没有 `data`。

**缓存策略**（省额度的关键，宁可多缓存）：

| 数据 | 策略 |
| --- | --- |
| 歌词 | 磁盘缓存永久保留，仅手动"重新获取"时刷新 |
| 歌单详情 | 磁盘缓存，点"同步"才重新拉取（一次同步 = 1 次调用） |
| 搜索结果 | 会话内存缓存（同关键词不重复请求） |
| 播放/下载 URL | **不缓存**，直链有时效，每次播放/下载现取 |
| 封面图片 | 下载一次永久缓存 |

**实现须知**：文档没有给出 `163_search` / `163_playlist` / `163_music(json)` 响应的完整字段定义。实现时第一步先用真实 Key 各打一次样、把 JSON 存为 `docs/api-samples/*.json`（脱敏），再据此写模型类，不要凭猜写反序列化。

## 7. 功能模块设计

### 7.1 在线搜索与播放

搜索页输入关键词 → `163_search` → 结果列表（标题/歌手/专辑/时长）。双击或点播放：按设置的默认音质调 `163_music` 取直链 → 交给播放引擎按 URL 流播放（同一条 ASIO 链路），显示缓冲状态。结果行提供：播放、下一首播放、加入歌单、下载、查看歌词。

**音质回退链**：高音质（jymaster/sky/hires）可能因资源或权限解析失败，失败时自动按 `设定音质 → lossless → exhigh → standard` 逐级降级重试（每级消耗 1 次额度，最多降 2 级），并在 UI 标注实际播放音质。

### 7.2 歌词

获取优先级：**同目录同名 `.lrc` 文件 > 本地缓存 > ChKSz API**。

- 本地歌曲想要在线歌词时，需要先匹配网易云 ID：用"标题 + 歌手"调一次搜索取最优匹配（标题+歌手规范化后相似度最高、时长差 < 3 秒），匹配结果存入曲目记录；允许在歌词页手动"重新匹配"从候选中改选。
- LRC 解析支持一行多时间标签、`[offset:]` 标签；API 返回的翻译/罗马音作为并行轨，歌词页可切换 原文/双语/罗马音。
- 歌词页：当前行高亮居中滚动、点击某行跳转进度、手动偏移微调（±0.1s 步进，保存到缓存）。纯文本无时间轴歌词降级为整篇静态显示。

### 7.3 歌单同步（网易云 → 本地，单向）

粘贴歌单链接或 ID → `163_playlist` → 创建 `source=netease` 的本地歌单。逐曲目做本地匹配：标题+歌手规范化后先精确匹配、再模糊匹配（去括号后缀、繁简归一）；命中的指向本地文件，未命中的存为在线条目（播放时走解析）。列表里用小图标区分 本地/在线 条目。

点"同步"= 重新拉取歌单做差量更新（新增补进、被删移除），**不触碰**用户手动加进这个歌单的本地曲目。一次同步只消耗 1 次 API 调用。

### 7.4 下载

下载管理页维护一个**串行队列**：任务间隔 ≥ 4 秒（配合全局令牌桶稳不触发限流），每首消耗 1 次额度，入队时显示"本次将消耗 N 次 / 今日剩余 M 次"，额度不足直接警告。

单曲流程：按设置音质取直链（复用 7.1 回退链）→ 下载到临时文件 → TagLibSharp 写入 标题/歌手/专辑/曲号/封面 → 已有歌词则写同名 `.lrc` → 按命名模板移入下载目录 `{AlbumArtist}/{Album}/{TrackNo} - {Title}.{ext}`（模板可配置）→ 触发媒体库增量扫描自动入库。失败自动重试 1 次；与库内重复（标题+歌手相同且时长差 < 2s）时提示是否仍要下载。

## 8. UI 结构（现代简洁，紧凑密度，默认深色）

- **左侧栏**：媒体库（全部歌曲/专辑/艺术家）、歌单区（本地歌单 + 同步歌单，带同步图标）、在线搜索、下载管理、设置。
- **中间主区**：曲目列表 + 顶部即时过滤框；列表行高保持紧凑（约 28px），信息密度向 foobar 看齐。
- **底部播放条**：封面缩略图、标题/歌手、可拖动进度条、控制按键、音量、播放模式、输出设备快捷切换菜单、**额度指示器**（如 `API 358/400`，数值来自响应头）。
- **正在播放/歌词页**：点击底部封面展开覆盖层：大封面 + 滚动歌词 + 双语切换 + 偏移微调。
- **设置页**分四组：输出（后端 ASIO/WASAPI/DirectSound、设备、独占开关、采样率策略、缓冲大小）；媒体库（目录管理、重新扫描）；在线（API Key、默认播放音质、默认下载音质、下载目录与命名模板）；外观（深/浅色、缩放）。
- **系统集成**：媒体键与 SMTC（Windows 系统媒体面板）、常用快捷键（空格播放暂停、Ctrl+←→ 切歌、Ctrl+F 搜索）、可选最小化到托盘、任务栏缩略图按钮。

## 9. 数据与目录布局（绿色便携）

程序目录内自包含，整个文件夹拷走即迁移：

```text
Player/
├── Player.exe、*.dll（含 bass 系列原生 dll）
└── data/
    ├── config.json          # 含 apikey，永不入仓库
    ├── library.db
    ├── cache/lyrics/  cache/covers/
    └── logs/
```

`config.json` 示例（Key 用占位符示意）：

```json
{
  "apiKey": "（在设置页填写，chksz 后台生成的 Key）",
  "output": { "backend": "asio", "device": "", "exclusive": true, "rateStrategy": "follow" },
  "library": { "folders": ["D:/Music"] },
  "online": { "playLevel": "lossless", "downloadLevel": "hires",
              "downloadDir": "D:/Music/Download",
              "namingTemplate": "{AlbumArtist}/{Album}/{TrackNo} - {Title}" }
}
```

## 10. 开发阶段划分（P0–P6）

总原则：每个阶段结束时程序都是**可运行、可日常使用**的状态。阶段总览：

| 阶段 | 内容 | 关键验收 |
| --- | --- | --- |
| P0 | 骨架 + 基础播放 | 拖入 flac/mp3 能播，暂停/进度/音量可用 |
| P1 | 媒体库 + 歌单 + 主界面 | 万级曲库流畅浏览搜索 |
| P2 | ASIO / WASAPI 独占 + 无缝 | ASIO 设备位完美出声，切歌无缝 |
| P3 | API 客户端 + 歌词 | 本地歌自动出滚动歌词，状态栏显示额度 |
| P4 | 在线搜索与播放 | 搜索双击即播，走 ASIO 链路 |
| P5 | 歌单同步 + 下载 | 歌单一键同步；下载带标签封面歌词入库 |
| P6 | 系统集成与发布 | SMTC/快捷键/托盘，产出便携 zip |

每个阶段的详细任务与验收标准如下。**分工模式：规划由本文档承载并持续维护，写码交给执行 AI（Claude Opus）**。每次新开执行会话时，先发下面这段执行总则，再跟上对应阶段的提示词：

```text
【执行总则】你是本仓库的执行开发者。规划已定稿在根目录 PLAN.md，先通读，不要重新设计架构。
1. 严格按指定 Phase 的范围实现：不做计划外功能、不擅自换库；发现规划与实测冲突
   （尤其 ChKSz API 的真实响应结构）时以实测为准，并把差异回写进 PLAN.md 对应小节。
2. API Key 只能从 data/config.json 读取；data/ 必须在 .gitignore 中；日志、报错信息、
   提交内容里不得出现 chksz_ 开头的字符串。
3. 额度与限流以 X-Quota-* / Retry-After 响应头为准，额度数字不写死在代码里。
4. 每个 Phase 结束时：自测、逐条核对该阶段验收标准，并输出核对结果清单。
```

### P0 骨架与基础播放

建解决方案（Player.App / Player.Core）、引入 NuGet 包、放置 bass 原生 dll、启动时初始化 BASS 并加载格式插件；实现最小 PlaybackEngine（DirectSound 输出）：打开文件、播放/暂停/停止、seek、音量；底部播放条雏形；文件拖入即播。验收：五种以上格式（mp3/flac/m4a/ape/wav）都能播放与拖动进度，退出无残留进程。

```text
通读仓库根目录 PLAN.md 的第 2、3、4 节和第 10 节 P0 部分，实现 P0：
搭建 Player.App(WPF)/Player.Core 解决方案，接入 ManagedBass，启动时 Bass.Init 并
PluginLoad 全部格式插件；实现 PlaybackEngine 的 打开/播放/暂停/seek/音量（先用
BASS 默认输出）；做一个底部播放条（封面占位、标题、进度条、控制键、音量），支持
把音频文件拖入窗口直接播放。原生 dll 若无法联网下载，生成 README 说明手动放置路径。
约束：核心逻辑全部放 Player.Core，不得在 Core 引用 WPF；完成后逐条核对 P0 验收标准。
```

**P0 实施记录（2026-08-13 完成）**：解决方案为仓库根目录下的 `Player.Core`（net8.0，不引用任何 WPF 类型）与 `Player.App`（net8.0-windows，WinExe，AssemblyName=Player，PlatformTarget=x64）。与规划的偏差两处：① 播放条的标题/艺术家暂由文件名按「歌手 - 标题」惯例推断，TagLibSharp 仍按计划留到 P1；② 为便于自测，拖放顺带支持了文件夹递归（原属 P1 范围）。另在 Core 内新增最小内存队列 `PlaybackQueue`，仅为让上一首/下一首可用，P1 由 PlaylistService 取代。包版本：ManagedBass 4.0.2、CommunityToolkit.Mvvm 8.4.2、Serilog 4.4.0 + Sinks.File 7.0.0。 **P0.1 修复（2026-08-13）**：进度条改为「鼠标按下或开始拖动即接管滑条 → 接管期间定时器完全不回写 → 松手（或拖动结束）才执行 seek → 立即以目标值乐观更新 UI，并设 700ms 静默窗口，引擎位置追上目标即提前解除」。根因确认为 seek 后立刻从 BASS 读回位置，而 BASS 在播放缓冲刷新前仍报告旧位置，于是滑块被拽回旧值；点击与拖动两条路径共用同一对 Begin/EndSeek，由 _isSeeking 保证一次操作只 seek 一次。音量确认为 UI→引擎单向写入，定时器不读也不回写音量。另把进度条的 LargeChange/SmallChange 设为 5 秒/1 秒（原为控件默认值 1，点击轨道时若未命中 move-to-point 只会挪动 1 秒，是「要反复操作才生效」的次因）。

**P0 实机验收记录（2026-08-13，用户实测）**：flac/mp3 播放、自动连播、退出无残留进程、非音频文件拒收均通过。wav/m4a/ape 等格式用户暂无样本，规划方已用 ffmpeg 合成整套测试音频放在 `publish/format-test/`（APE 无法由 ffmpeg 编码，留待真实样本出现再验）。**遗留缺陷 P0.1（进入 P1 前必修）**：进度条拖动/点击后偶发弹回旧位置、需反复操作才生效，音量滑条疑似同类问题。根因判断：UI 定时器将引擎状态回写滑条，与用户输入赛跑。修复要求：拖动期间（鼠标按下至松开）定时器不得回写该滑条，松手才执行 seek；点击跳转后立即以目标值乐观更新 UI，不等下一 tick 从引擎读回；音量滑条只做 UI→引擎单向写入，定时器不回写音量。

### P1 媒体库、歌单与主界面

SQLite 建库建表（第 5 节 schema）、目录扫描器（全量 + 增量 + FileSystemWatcher）、TagLibSharp 读标签与封面缓存；主窗口三栏布局（左侧栏/列表/播放条）、多列列表与即时过滤、点击排序；本地歌单 CRUD 与拖拽排序、m3u8 导入导出；**文件夹虚拟歌单（2026-08-13 用户新需求）**：用户曲库以文件夹组织，媒体库根目录下每个顶层子文件夹自动呈现为一个只读歌单（内容含其子目录递归，随扫描/监听自动更新），在左侧栏与手工歌单分组展示，更深层级的树状浏览进 backlog；双击列表播放、上下曲、播放模式。验收：10000 曲全量扫描（SSD）≤ 2 分钟，过滤输入无卡顿；重启后曲库与歌单完整保留；文件夹虚拟歌单随重扫自动更新。

```text
通读 PLAN.md 第 3、5、8 节和第 10 节 P1 部分，实现 P1：
SQLite 媒体库（建表照抄第 5 节 schema）、LibraryScanner（并行读标签、增量扫描、
FileSystemWatcher）、TagLibSharp 读标签与封面 hash 缓存（标题/艺术家改为标签优先、
文件名兜底）；主界面按第 8 节做三栏布局（WPF-UI 风格、紧凑行高）；列表多列排序 +
顶部即时过滤；歌单三类能力：手工歌单增删改/拖拽排序、m3u8 导入导出、文件夹虚拟
歌单（顶层子文件夹=只读歌单，随扫描自动更新，左侧栏分组展示）；双击播放、上下曲、
随机/循环模式；P0 的 PlaybackQueue 由 PlaylistService 取代，文件夹拖放升级为
「入库并开播」。
约束：扫描不得阻塞 UI 线程；完成后用一个大目录实测扫描耗时并核对 P1 验收标准。
```

**P1 实施记录（2026-08-13 完成，待用户实机验收）**：

- **数据层**：`Infra/Db.cs` 按第 5 节 schema 建库（WAL + busy_timeout），`tracks` 表比原 schema 多一列 `cover_hash`（封面按内容 hash 存盘，表里要有一列指过去）；`Infra/ConfigService.cs` 落地第 9 节的 config.json 结构（apiKey 字段先占位，P3 才用）。**文件夹虚拟歌单刻意不落库**，由曲目路径与根目录在内存中推导，这样根目录增删或改名后不会留下脏数据。
- **扫描**：`LibraryScanner` 全量/增量（判据 mtime + 文件大小）、并行读标签、分批事务写入、进度单调上报、可取消。**安全策略**：某个根目录本次不可访问（U 盘拔了、网络盘断了）时，其下曲目一律保留不删——否则会连带清空 `playlist_items`，盘回来后 id 变化导致歌单内容永久丢失。
- **标签**：TagLibSharp 标签优先、文件名兜底；兜底会先剥掉曲号前缀（`01 - 歌手 - 标题` 不会把 01 当成歌手，这是实测 10000 首样本时发现并修掉的）。
- **播放**：P0 的 `PlaybackQueue` 已删除，由 `Audio/PlaybackList` 取代（顺序/列表循环/单曲循环/随机四种模式，列表内容由 PlaylistService 与媒体库各视图提供）。
- **界面**：按用户决定引入 WPF-UI 4.3.0（FluentWindow + TitleBar + Mica + ControlsDictionary 的 Fluent 控件样式），播放器自有的调色板与控件样式在 `Themes/Player.xaml`；**刻意不引用 WPF-UI 的主题资源键**（StaticResource 键缺失会在启动时崩溃，自己定义可控）。左侧栏为「媒体库（全部歌曲/专辑/艺术家）+ 歌单 + 文件夹」三组导航。
- **与规划的偏差**：① 专辑/艺术家视图做成**虚拟化列表**（缩略图 + 名称 + 计数）而非封面墙——WPF 没有内置的虚拟化 WrapPanel，万级曲库下封面墙会卡；② 列表里暂无「正在播放」行高亮，播放中曲目只在播放条显示，进 backlog；③ 曲目加入歌单目前走右键菜单「添加到歌单」，把曲目拖到侧边栏歌单上的交互进 backlog。

**P1 实机验收记录（2026-08-13，用户实测）**：扫描不卡 UI、三栏布局、文件夹虚拟歌单（内容与数量正确）、即时过滤、列头排序、双击播放/上下曲/播放模式、全格式样本、重启后曲库歌单还原、退出无残留——均通过。**两组缺陷（P1.1，进入 P2 前必修）**：

① **手工歌单加歌链路事实不可用**：歌单的「添加音乐文件夹」入口点击无反应；用户按直觉把文件/文件夹/曲目拖到歌单上，被全局拖放接管走了「入库并开播」，歌单始终为空。修复要求：(a) 查明加歌入口命令未生效的原因并修复，右键「添加到歌单」全链路一并自测；(b) 原偏差③的 backlog 项**提升为必修**——从资源管理器拖文件/文件夹到侧边栏歌单 = 加入该歌单，从曲目列表拖行到侧边栏歌单 = 加入该歌单，歌单详情页内拖放 = 按落点插入；只有落在非歌单目标上的拖放才维持「入库并开播」。修复后 m3u8 导出/导入回路需用户重验（本轮因加不进歌未能测到）。

② **P0.1 点击路径回归**：点击进度条无效——滑块先到点击位置、松手后弹回旧位置；音量条无此问题。根因判断：单纯点击不触发 Thumb 的 DragStarted/DragCompleted，EndSeek 从未执行，700ms 静默窗口过期后定时器把位置拉回。拖动另偶发一瞬不跟手，疑似接管标志晚于鼠标按下。修复要求：接管以 PreviewMouseLeftButtonDown 为起点（Slider 会吞掉非 Preview 鼠标事件，必要时 AddHandler + handledEventsToo:true），释放时**无论点击还是拖动必然执行一次 seek**；接管期间定时器一律不回写。音量条无问题，不要改动它。

**P1.1 复验（2026-08-13，用户实测）：三条加歌路径、空歌单拖放、页内排序、m3u8 导出导入回路、进度条点击/拖动/按下移开松手，全部通过。P1 正式关闭。**

> **P2 验收设备（2026-08-13 确认）**：TOPPING **E1x2 OTG**（2 进 2 出 USB 声卡，专用 ASIO 驱动 + TOPPING Professional Control Center v1.13 面板）。面板右上角实时显示当前采样率与缓冲区大小——「ASIO 采样率跟随源文件」这条验收直接看面板数字。注意事项：① 用户当前缓冲区设为 32 samples，纯播放场景偏激进，若出现爆音/断续先建议调到 128–256 再排查代码；② ASIO 驱动通常单客户端独占，测试时先关掉其它占用 ASIO 的程序（面板本身不算）；③ 该设备有 Playback 1/2~7/8 多路回放与 Mix A–D 路由，播放器输出的是哪一对通道要在验收指引里写清楚，默认打 1/2，听不到声先查面板混音路由而不是代码。

**P2 实机验收记录（2026-08-14，用户实测，设备 TOPPING E1x2 OTG）**：验收对照表 7 项全部通过——ASIO 出声、采样率跟随（44.1/48/96/192k 随曲切换面板同步跳变）、WASAPI 独占、同采样率无缝、播放中拔 USB 不崩并回退系统输出、后端切换轰炸全程不重启、位完美指示准确。**P2 正式关闭。**
- **沙盒实测（2 核 Linux 容器，10000 个文件、10 个顶层文件夹 × 20 专辑 × 50 首）**：全量扫描 **1.7 秒**、增量（无变化）**0.1 秒**、增删各一 **0.3 秒**、万级内存过滤单次 **1.0 毫秒**；封面去重有效（4759 首命中同一封面只存 1 个文件）；歌单增删改 / 拖拽排序回写 / m3u8 导出再导入 500 首全部正确；重启后曲库、歌单、文件夹虚拟歌单完整重建。注意真实曲库单文件远大于测试样本（读标签只读文件头，仍应远低于 2 分钟验收线），最终以用户实机为准。

### P2 ASIO 与输出设备（本项目的灵魂，单独一个阶段慢慢调）

定义 IOutputBackend 抽象，落地三个实现：BassAsio（设备枚举、按曲目采样率初始化、AsioProcedure 拉流）、BassWasapi（独占/共享）、DirectSound 兜底；bassmix 无缝衔接与下一曲预载；采样率切换策略与"固定采样率"选项；设置页-输出组；设备热拔出回退。验收：ASIO 设备正常出声且采样率跟随源文件（用 DAC 面板确认）；WASAPI 独占出声；同采样率连续曲目无缝；播放中拔设备程序不崩并回退默认输出。

> **P2 前置已就绪（2026-08-13，规划方）**：`bassasio.dll`（1.4）、`bassmix.dll`、`basswasapi.dll` 三个 x64 原生库已由规划方下载并完成 PE 校验，放在 `native/x64/`（当前为未跟踪状态，P2 随阶段提交一并入仓即可），**无需重新下载**。

```text
通读 PLAN.md 第 4 节和第 10 节 P2 部分，实现 P2：
抽象 IOutputBackend，实现 ASIO(ManagedBass.Asio)/WASAPI 独占与共享/DirectSound
三后端与运行时切换；解码链改为 decode stream + bassmix，实现同采样率无缝播放与
下一曲预载；ASIO 采样率跟随源文件，提供固定采样率+重采样选项；实现设置页的
输出设备组（后端、设备下拉、独占开关、缓冲大小）；处理设备占用/拔出的回退与提示。
约束：所有 BASS/BassAsio 错误码写 Serilog 日志；切后端不需要重启程序；
完成后核对 P2 验收标准，无 ASIO 设备的环境用 ASIO4ALL 验证。
```

**P2 实施记录（2026-08-14 完成，待用户实机验收）**：

- **链路**：`解码流(Decode|Float) → BassMix mixer → IOutputBackend`。mixer 固定 2 声道，多声道源挂进来时加 `MixerChanDownMix` 下混，立体声源全程不被动到。后端要不要 decode 流由 `RequiresDecodingSource` 决定（ASIO/WASAPI 要，DirectSound 直接播放 mixer）。
- **ASIO**：用 `BASS_ASIO_ChannelEnableBASS(join:true)` 把 mixer 直接接到通道上，省掉自写 AsioProcedure。设备采样率在 `ChannelEnableBass` 之前用 `CheckRate` 校验并设置；缓冲区起不来会自动退回驱动首选值重试一次；起始声道可选（默认 Playback 1/2）并校验设备输出通道数。
- **并发模型（最关键的一条）**：`_control` 锁给控制路径（Open / 建拆链 / 切后端 / Dispose），`_swap` 锁只保护句柄指针。**mixtime 回调绝不允许拿 `_control`** —— 控制路径会在持锁时调用 `StreamFree` / `BassAsio.Free` 这类**会等待音频线程退出**的 API，回调去抢就必然死锁。
- **无缝**：mixer 建成 `MixerNonStop`，源用 `MixerChanNoRampin`；当前曲挂 mixtime 的 END sync，回调里把预载好的下一曲加进 mixer，交接在样本边界上完成。提前 5 秒预载（建流在后台线程）；下一曲采样率与当前输出不一致就不预载并记入"已拒绝"，避免每个 tick 重开文件。**重建链路后必须重挂 END sync**，否则这首放完就再没有任何回调（既不无缝也不续播）。
- **空闲不占设备**：输出设置在没播放时只记录不开设备。ASIO 驱动通常单客户端独占，空闲时占着会让其它程序打不开声卡。
- **设备回退**：ASIO 用 `BassAsio.SetNotify` 接驱动通知；WASAPI / DirectSound 没有通知，由引擎每秒一次的看门狗轮询（同时回收无缝交接攒下的旧句柄）。任何后端起不来或掉线都回退系统输出并提示，回退只尝试一次不会递归。ASIO 面板上改采样率会触发 `FormatChanged`，用**同一后端**重建链路而不是回退。
- **位完美判据**：音量 = 100% **且** 无重采样（曲目采样率 = 设备实际采样率）**且** 后端为 ASIO 或 WASAPI 独占。DirectSound 与 WASAPI 共享一律不算——它们必然经过系统混音。界面左下角有常驻指示，位完美时点亮蓝点。
- **ManagedBass 4.0.2 实测修正**（与文档/记忆不符，已按实测写）：`AsioInfo` 没有 `BufferLength` / `Granularity`（只有 Min/Max/Preferred）；`BassWasapi` 没有 `LastError`（用 `Bass.LastError`）；`BassMix.ChannelFlags` 返回 `BassFlags` 而非 bool；`WasapiInfo` 没有 `Volume`；`Bass.FreeDevice` 不存在。这些都是编译期就能发现的，做法是先用探针工程逐个验证再动手。
- **已知限制**：为了保证兜底可用，程序始终保持 BASS 默认输出设备处于初始化状态；个别系统上这可能让 WASAPI 独占打不开同一个端点。本机首选 ASIO，遇到再改共享模式。
- **沙盒验证**：`tools/Player.Harness`（随本阶段入仓）跑通 26 项无缝决策断言（采样率解析、能否无缝、预载时机、四种播放模式的下一曲预测、输出设置往返）+ 12 项媒体库端到端。**ASIO / WASAPI 的出声效果无法离线验证**，实机步骤见 `docs/ASIO-验收指引.md`（针对 TOPPING E1x2 OTG 写的，含缓冲区调整顺序与拔线等破坏性测试）。

### P3 ChKSz 客户端与歌词

ChkszClient（四端点封装、令牌桶 18 次/分、额度响应头解析、第 6 节错误映射、URL 日志脱敏）；QuotaTracker + 播放条额度指示器；设置页-在线组（Key 填写与校验）;先打样真实响应存 docs/api-samples；LyricsService（三级优先级、网易云 ID 匹配、缓存）与歌词页（滚动高亮、双语切换、偏移微调、点击跳转）。验收：填 Key 后播放本地歌自动匹配出滚动歌词；断网或 402 时功能优雅降级不崩；连续操作不触发 429。

```text
通读 PLAN.md 第 6、7.2 节和第 10 节 P3 部分，实现 P3：
实现 ChkszClient：163_music/163_search/163_lyric/163_playlist 四端点，全局令牌桶
18 次/分，解析 X-Quota-* 响应头广播额度，429 按 Retry-After 只重试一次，其余错误
按第 6 节映射为用户可读提示；apikey 只从 data/config.json 读取，日志脱敏。
先用真实 Key 各端点打样一次，把脱敏 JSON 存 docs/api-samples/ 再写模型类。
然后实现 LyricsService（.lrc > 缓存 > API，本地歌用 标题+歌手 搜索匹配网易云 ID，
支持手动重新匹配）和歌词页（当前行高亮滚动、原文/双语/罗马音切换、偏移微调）。
约束：额度指示器进播放条；所有在线失败都不得影响本地播放；核对 P3 验收标准。
```

**P3 实施记录（2026-08-14 完成，待用户实机验收）**：

- **接入层**（`Player.Core/Online/`）：`ChkszClient` 四端点封装（163_music / 163_search / 163_lyric / 163_playlist）、全局令牌桶 **18 次/分**、429 按 `Retry-After` 只重试一次、错误映射按第 6 节表、URL 日志脱敏（`Redact` 纯函数）；`QuotaTracker` 只认 `X-Quota-*` 响应头（400 不写死在代码里）；模型类按 `docs/api-samples/` 真实打样写。
- **歌词**：`Lyrics/LrcParser.cs`（一行多时间标签、`[offset:]`、元数据跳过、无时间轴降级整篇静态、翻译/罗马音按 500ms 容差并轨、二分查找当前行）；`LyricMatcher.cs`（规范化 = 全角→半角 + 去括号及内容 + 去空白标点 + 小写；**时长差 < 3s 是硬条件**——打样实测搜索以 UGC/翻唱为主，只靠标题+歌手会大量误配，见 api-samples README）；`LyricsService.cs` 三级优先级 **.lrc > 本地缓存 > API**，匹配结果持久化到 `tracks.netease_id`（重扫不覆盖，见下），同一首歌会话内只自动匹配一次（省额度），402 后在线能力整体降级 10 分钟、额度恢复自动解除。
- **UI**：点击底部封面展开歌词覆盖层——大封面 + 歌词列表（当前行高亮并滚动居中、点击行跳转进度）+ 原文/双语/罗马音切换 + 偏移微调（±0.1s，持久化）+ 「重新获取」「重新匹配」（候选对话框，支持清除匹配）+ 无时间轴歌词整篇静态显示 + 未找到时安静提示不弹窗；播放条右侧常驻额度指示（`API 剩 N`）；设置页新增在线组（Key 填写/保存/测试连接，Key 只存 `data/config.json`）。
- **数据库迁移**：`tracks` 表新增 `netease_id` 列（幂等 `ALTER TABLE`）。**实机抓到的坑**：旧库上 `CREATE INDEX ... ON tracks(netease_id)` 必须等列加好之后单独建，否则整批 DDL 失败导致启动直接退出——迁移顺序已在 `Db.EnsureSchema` 修正。歌词缓存走 `lyrics_cache` 表（`163:{id}` → lrc/tlyric/romalrc JSON；偏移 `offset:{path-hash}`）。扫描 Upsert 不触碰 netease_id 列，重扫不会冲掉用户匹配。
- **harness**：新增 `lyrics` 模式共 65 项断言（LRC 解析、令牌桶窗口、额度头解析、规范化与择优匹配、脱敏、错误映射、缓存存储往返、无 Key 离线降级），连同原 seamless 26 项全部通过。
- **离线构建辅助（本机实测，不入仓）**：本环境访问 nuget.org 的 TLS 握手失败，用 `tools/nuget-offline/`（从 `publish/` 产物重建的包目录）注册为 `fallbackPackageFolders`（根目录 `nuget.config`）；构建需 `-p:RestoreIgnoreFailedSources=true -p:UseSharedCompilation=false -m:1 -nodeReuse:false`。有网环境删掉 `nuget.config` 与 `tools/nuget-offline/` 即可恢复正常构建。
- **与规划偏差（已按实测执行）**：① 手动重新匹配做成独立小对话框（`RematchDialog`）而非页面内嵌；② 自动匹配失败会话内不重试（防止反复烧额度），重启后允许再试——PLAN 未规定此细节，按"额度优先"原则实现。


**P3 实机验收记录（2026-08-14，用户实测）**：Key 生效、额度随调用递减、自动匹配与手动候选链路可用、歌词行点击跳转有效；断网降级暂缓待测。发现一个严重显示缺陷与一组交互问题，并确立两项新需求（内嵌标签歌词、界面改版），定义见下两节。

### P3.1 歌词缺陷修复与内嵌歌词（先做，小步提交）

1. **歌词行显示为类型名**（严重）：歌词页每行显示 `Player.App.ViewModels.LyricDisplayLine` 而非歌词文本——ItemsControl 的 ItemTemplate 未生效、走了 ToString() 兜底。修复后由子代理专项复查歌词页全部模板/绑定/资源键（此类错误编译不报）。
2. **歌词页交互不可用**（严重）：右上「重新获取/重新匹配/关闭」与窗口最小化/关闭按钮重叠且点击无反应，除 Esc 外无法退出。要求：覆盖层不得占用窗口标题栏区域；「重新获取/重新匹配」移到页内不与窗口按钮冲突的位置；退出方式三选全有——页内明显的关闭按钮、Esc、点击封面收起。
3. **内嵌标签歌词**（新需求）：用户曲库大多用打标签软件把歌词内嵌进文件。TagLibSharp 读取内嵌歌词（USLT / LYRICS 等字段），内容含 LRC 时间轴则按 LRC 解析滚动，纯文本则静态展示。优先级改为：**同目录 .lrc > 内嵌标签歌词 > 本地缓存 > API**；有内嵌歌词的曲目不得发起任何 API 调用。harness lyrics 模式补内嵌歌词与新优先级断言。
4. **额度指示迁移**（用户反馈）：播放条上的「API 剩 N」移除，只在设置页在线组展示（免费/兑换余量、重置时间），数据管道保留。

### P3.2 界面改版（对照用户提供的 foobar 截图，多轮迭代）

用户反馈：当前界面丑、纯黑单调。目标观感对照 foobar 截图：信息密度高、装饰克制、配色随专辑封面。**分四步，每步单独提交 + 发布 + 等用户过目后再做下一步**：

1. **布局调整**：过滤/搜索框从主区顶部移入左侧栏顶部；左侧栏收窄（约 200px，可拖拽调宽）、降低其总占比；「播放全部/导出」收进更轻的位置（列表区右上小图标或右键菜单）。
2. **专辑分组视图**：曲目列表支持按专辑分组——组头一行（封面缩略图 + 专辑名 + 艺术家 + 年份），组内曲目行（曲号、标题、时长…）。保持虚拟化、万级曲库不卡；作为可切换的显示模式，不强制替换平铺列表。
3. **封面取色主题**：从当前播放曲目封面提取主色（缩图后取主色调即可），动态用于强调色（进度条、选中行、歌词当前行、高亮）并给背景极轻的同色 tint；基底保持深色，无封面回退默认强调色，切歌颜色平滑过渡。
4. **右侧信息栏（可折叠）**：大封面、技术信息（格式/码率/采样率/位深）、标签元数据（作词作曲等，标签里有才显示）、歌词联动预览；展开状态记住用户选择。

验收：每步用户目验；歌词页三种退出方式全可用；有内嵌歌词的歌播放时额度数字不动；分组视图万级曲库滚动流畅。

### P4 在线搜索与播放

在线搜索页（结果列表、分页加载）；URL 流播放接入现有引擎（含缓冲提示）；音质回退链；在线曲目加入歌单/播放队列；封面显示。验收：搜索到双击出声（网络正常时 ≤ 3 秒）；高音质解析失败能自动降级并标注；在线曲目通过 ASIO 输出。

```text
通读 PLAN.md 第 7.1 节和第 10 节 P4 部分，实现 P4：
在线搜索页：关键词搜索、结果列表（标题/歌手/专辑/时长）、滚动分页（offset）；
双击按设置音质解析直链并用 BASS URL 流经现有输出链路播放，UI 显示缓冲状态与
实际音质；实现 jymaster→lossless→exhigh→standard 音质回退链（最多降 2 级）；
结果行右键：播放/下一首播放/加入歌单/下载占位/查看歌词。
约束：直链不缓存，每次播放现取；搜索结果做会话内缓存；核对 P4 验收标准。
```

### P5 歌单同步与下载

歌单导入与同步（第 7.3 节：匹配算法、差量更新、本地/在线条目标识）；下载管理页与串行队列（第 7.4 节：额度预估、间隔 4 秒、标签封面歌词写入、命名模板、重复检测、失败重试）。验收：100 首歌单导入只耗 1 次 API 调用且匹配结果可人工核查修正；连续下载 3 首不触发 429，产物标签/封面/lrc 齐全并自动出现在媒体库。

```text
通读 PLAN.md 第 7.3、7.4 节和第 10 节 P5 部分，实现 P5：
歌单同步：粘贴链接/ID 导入网易云歌单，曲目按 规范化标题+歌手 先精确后模糊匹配
本地库，未命中存为在线条目；"同步"按钮做差量更新且不动用户手动添加的曲目。
下载：下载管理页 + 串行队列（间隔≥4s），入队前显示额度消耗预估；单曲流程为
解析直链(复用回退链)→下载→TagLibSharp 写标签与封面→写 .lrc→按模板改名入库→
触发增量扫描；重复检测与失败重试 1 次。
约束：全部请求过全局令牌桶；核对 P5 验收标准。
```

### P6 系统集成与发布

SMTC 与媒体键、全局快捷键、可选托盘、任务栏缩略图控制；深/浅主题细节打磨、异常兜底（全局 catch + 日志 + 友好弹窗）；发布脚本（framework-dependent 便携 zip，附带原生 dll 与首次运行引导：填 Key、选曲库目录、选输出设备）。验收：媒体键可控制播放；连续使用 2 小时内存稳定（< 300MB）；zip 拷到干净机器可运行（装有 .NET 8 运行时）。

```text
通读 PLAN.md 第 8、9 节和第 10 节 P6 部分，实现 P6：
接入 SMTC（System.Media 控件/媒体键，显示封面标题）、全局快捷键、最小化到
托盘选项、任务栏缩略图按钮；全局异常处理写日志并友好提示；完善深浅主题；
编写 publish.ps1 产出便携 zip（framework-dependent + bass 原生 dll + 首次运行
引导流程）。逐条跑一遍 P0-P6 全部验收标准做回归，输出核对清单。
```

## 11. 风险与注意事项

1. **API Key 已经暴露过一次**：Key 出现在你发给外部工具的文档文件里，建议去 `https://api.chksz.com/login` 后台**重置一次 Key**，之后只把新 Key 填进程序设置页。仓库必须 `.gitignore` 掉 `data/`，任何提交前检查一遍没有 `chksz_` 字样。
2. **额度已放宽，硬约束是速率**：免费额度当前为 400 次/天，日常在线播放 + 歌词很难碰顶；真正要防的是 **20 次/分的速率限制**（批量下载、批量匹配歌词时最容易撞上），因此全局令牌桶与下载串行队列的设计保持不变。额度数字一律以响应头为准，不写死在代码里；极端批量场景再考虑兑换 LDC。
3. **直链时效与版权**：解析出的 URL 有时效，不可收藏复用；部分歌曲高音质（sky/jymaster）可能解析失败，靠回退链兜底；个别灰色歌曲可能完全无资源，UI 要给明确提示而不是转圈。
4. **ASIO 环境**：需要声卡厂商的 ASIO 驱动；板载声卡可装 ASIO4ALL 用于开发测试。ASIO 回调线程内绝不能做耗时操作（磁盘/网络/UI），这是最容易写崩的地方。
5. **BASS 许可**：个人使用免费；如果哪天想上架收费，需购买 un4seen 授权。
6. **API 响应字段未完全文档化**：`163_search`/`163_playlist` 的字段要以打样为准（P3 第一步），不要凭空写模型。
7. **网易云 ID 匹配可能错**：本地歌匹配歌词依赖搜索，同名翻唱会串词，所以歌词页必须保留"重新匹配"入口。

## 12. Backlog（后续扩展，按优先级排列）

QQ 音乐/酷狗音源切换（ChkszClient 已预留，两家详情响应自带 `lrc` 字段可直接显示歌词）；CUE 整轨分轨支持（foobar 用户常见需求，需自写 CUE 解析 + 区间播放）；均衡器与音效（bass_fx.dll）；频谱/VU 可视化（`ChannelGetData` FFT，很适合放歌词页）；ReplayGain 回放增益；DSD 原生播放（bassdsd + DoP）；全局热键自定义；浅色主题精修；歌曲评分与智能歌单；手机遥控（局域网 HTTP，远期）。

---

*本文档由规划会话生成；实现过程中如与实际情况冲突（尤其 API 真实响应结构），以实测为准并回写更新本文档。*

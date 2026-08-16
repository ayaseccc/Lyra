# Player

Windows 本地音乐播放器（类 foobar2000 的轻量定位 + 现代简洁界面），核心卖点是 ASIO / WASAPI 独占的位完美输出，以及通过 ChKSz API 接入的网易云在线能力。

完整规划见 [PLAN.md](PLAN.md)。**当前进度：P0–P6、UI-R、L1–L3 代码侧已完成；P6 的多选/拖放文件关联、任务栏按钮/进度条目验、启动耗时、长时内存、万曲扫描与干净机运行仍需目标环境验收。**

ASIO 实机验收步骤见 [docs/ASIO-验收指引.md](docs/ASIO-验收指引.md)。

---

## 一、环境要求

- Windows 10 / 11（x64）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（只运行不开发的话，装 .NET 8 Desktop Runtime 即可）
- 可选：Visual Studio 2022（17.8+），装「.NET 桌面开发」工作负载

先确认本机装没装 SDK，在 PowerShell 里执行：

```powershell
dotnet --list-sdks
```

有输出且版本号以 `8.` 开头就没问题。如果提示「无法将 dotnet 项识别为…」，说明还没装，去上面的链接下载 **SDK x64** 安装即可（装完要重开一个终端窗口）。

> 懒得装 SDK 也可以：仓库里的 `publish\Player.exe` 是自包含（self-contained）版本，双击就能跑，不需要任何 .NET 运行时。

## 二、构建与运行

用命令行（在仓库根目录）：

```powershell
dotnet build                      # 构建整个解决方案
dotnet run --project Player.App   # 运行
```

发布一个可直接拷走的自包含版本：

```powershell
pwsh tools/publish.ps1 -Version 1.0.0
```

脚本使用 `win-x64` + `--self-contained true` 先发布到独立暂存目录，再生成 `Player-v1.0.0-win-x64.zip`；包内已带 .NET 与 Windows Desktop 运行时，目标机无需预装 .NET。

用 Visual Studio：双击 `Player.sln`，把 `Player.App` 设为启动项目，F5 即可。

## 三、目录结构

```text
MusicPlayer/
├── PLAN.md                 开发规划（唯一事实来源，改需求先改它）
├── Player.sln
├── docs/                   ASIO 实机验收指引等
├── tools/Player.Harness/   离线自测工具（无缝决策 + 媒体库端到端）
├── Player.Core/            全部业务逻辑，纯 net8.0，不引用任何 WPF 类型
│   ├── Audio/              BassRuntime、PlaybackEngine（decode → bassmix → 输出后端）、
│   │                       IOutputBackend + ASIO / WASAPI / 系统输出三后端、
│   │                       SeamlessPolicy、PlaybackList（四种播放模式）
│   ├── Library/            LibraryDb / LibraryScanner / LibraryWatcher / LibraryService
│   │                       TagReader（TagLibSharp）、PlaylistService、M3uFile
│   └── Infra/              AppPaths、Db（SQLite）、ConfigService、LogSetup（Serilog）
├── Player.App/             WPF 界面（net8.0-windows，产物 Player.exe）
│   ├── Themes/Player.xaml  播放器自有调色板与控件样式（Fluent 外观来自 WPF-UI）
│   ├── ViewModels/         Shell / Player / TrackList / Album / Artist / Settings
│   ├── Views/              InputDialog（新建、重命名歌单）
│   └── Converters/、Infra/ 封面缓存等
└── native/x64/             BASS 系列原生 DLL（x64），构建时自动复制到输出目录
```

运行期数据全部落在 exe 同级的 `data/` 下（便携绿色，整个文件夹拷走即迁移）：

```text
data/
├── config.json   媒体库目录、音量、播放模式；P3 起还会存 API Key —— 已在 .gitignore 中
├── library.db    SQLite 媒体库
├── cache/covers/ 封面缓存（按内容 hash 去重）
└── logs/         player-YYYYMMDD.log
```

## 四、BASS 原生 DLL

仓库的 `native/x64/` 里已经放好了 10 个 64 位 DLL（来自 [un4seen.com](https://www.un4seen.com/bass.html)，BASS 2.4.18.3）：

| 文件 | 作用 |
| --- | --- |
| `bass.dll` | 核心库，内置 mp3 / wav / aiff / ogg |
| `bassflac.dll` | FLAC |
| `bassape.dll` | Monkey's Audio (APE) |
| `basswv.dll` | WavPack |
| `bassopus.dll` | Opus |
| `bass_aac.dll` | AAC / MP4（第三方 add-on，下载路径在 `files/z/2/` 下） |
| `bassalac.dll` | ALAC |
| `bassasio.dll` | ASIO 输出（P2） |
| `bassmix.dll` | 混音器，无缝播放靠它（P2） |
| `basswasapi.dll` | WASAPI 独占 / 共享输出（P2） |

要手动补充或升级时：从官网下载对应 zip，取出其中 **`x64/` 子目录里的 DLL**，放进 `native/x64/`，重新构建即可。程序启动时会逐个 `Bass.PluginLoad`，加载结果写在日志里。

> BASS 个人非商业使用免费；将来若要商用需向 un4seen 购买授权（PLAN 第 11 节）。

## 五、已实现的功能

**播放（P0 / P0.1）**：BASS 默认输出（DirectSound），打开 / 播放 / 暂停 / 停止 / 精确 seek / 音量；进度条拖动与点击跳转不会弹回；曲目结束自动续播；支持 mp3 / flac / m4a / aac / alac / ape / wv / ogg / opus / wav / aiff。

**媒体库（P1）**：SQLite 持久化；多根目录管理；全量与增量扫描（判据 mtime + 文件大小）、并行读标签、FileSystemWatcher 自动跟随目录变化；TagLibSharp 读标签（缺标签时按文件名兜底，会先剥掉曲号前缀）；内嵌封面按内容 hash 去重缓存。

**浏览（P1）**：左侧栏三组导航 —— 媒体库（全部歌曲 / 专辑 / 艺术家）、手工歌单、**文件夹虚拟歌单**（媒体库根目录下每个顶层子文件夹自动成为一个只读歌单，含子目录递归，随扫描自动更新）；曲目列表七列（标题 / 歌手 / 专辑 / 时长 / 格式 / 采样率 / 位深）可点击列头排序、顶部即时过滤、列表虚拟化。

**歌单（P1）**：新建 / 重命名 / 删除、右键「添加到歌单」、从歌单移除、拖拽排序（未排序未过滤时）、m3u8 导入导出。

**播放列表与模式（P1）**：双击任意列表即以当前可见顺序开播；上一首 / 下一首；顺序、列表循环、单曲循环、随机四种模式（记忆到 config.json）。

**拖放（P1）**：拖入文件夹 = 加入媒体库并开播；拖入文件 = 直接播放（不入库）。

**输出与无缝（P2）**：三种输出后端可在运行时切换，不需要重启——ASIO（采样率跟随源文件，可位完美）、WASAPI 独占/共享、系统输出（兜底）；同采样率的连续曲目样本级无缝衔接（提前 5 秒预载）；采样率跟随或固定 + 重采样两种策略；设备被占用、拔出、驱动复位一律自动回退系统输出并提示；左下角常驻输出指示，位完美时点亮。

**在线与歌词（P3）**：ChKSz 四端点客户端（全局令牌桶 18 次/分、429 按 Retry-After 重试一次、额度以 `X-Quota-*` 响应头为准、URL 日志脱敏）；播放条右侧常驻额度指示；歌词三级优先级（同目录 `.lrc` > 本地缓存 > 在线），本地曲目按 标题+歌手+时长差<3s 自动匹配网易云 ID（结果持久化，可手动重新匹配）；歌词覆盖层（点击播放条封面展开）：滚动高亮当前行居中、点击行跳转、原文/双语/罗马音切换、偏移微调 ±0.1s 持久化；设置页新增在线组（API Key 填写 / 保存 / 测试连接）。所有在线失败安静降级，不影响本地播放。

**在线搜索与下载（P4–P5）**：多音源搜索、试听、音质回退、歌词/封面下载、标签写入、命名模板、重复检测、下载队列与取消；断网时安静降级，不影响本地播放。

**系统集成与发布（P6）**：SMTC/媒体键、全局快捷键、可选托盘、任务栏缩略图上一曲/播放暂停/下一曲按钮与进度状态；单实例命名管道转交、HKCU 九种音频格式文件关联、启动引导、损坏配置备份重建；`tools/publish.ps1` 生成无需预装 .NET 的 self-contained `win-x64` 便携 zip。

## 六、离线自测

```powershell
# 无缝衔接与播放模式的决策逻辑（纯函数，不需要声卡）
dotnet run --project tools/Player.Harness -- seamless

# 扫描 / 歌单 / 持久化端到端（需要一个真实的音乐目录）
dotnet run --project tools/Player.Harness -- library "D:\Music" 

# P3：LRC 解析 / 令牌桶 / 额度头 / 歌词匹配 / 缓存存储（纯逻辑，不需要网络）
dotnet run --project tools/Player.Harness -- lyrics
```

ASIO / WASAPI 的出声效果没法离线验证，按 [docs/ASIO-验收指引.md](docs/ASIO-验收指引.md) 在设备上实听。

## 七、许可与第三方服务

- **BASS / BASSASIO / BASSmix / BASSwasapi 及格式插件**：由 [un4seen](https://www.un4seen.com/) 提供，个人非商业使用免费；商业使用或上架收费前必须向 un4seen 取得对应授权。
- **GD Studio API**：上游以 **CC BY-NC** 提供，本项目仅按个人非商业用途接入；使用时应保留署名并遵守上游条款。
- **ChKSz API**：第三方在线服务，用户需使用自己的 Key 并遵守服务提供方的当前条款；本项目不附带 Key，也不授予在线音频或元数据的转授权。
- **开源组件**：WPF-UI、CommunityToolkit.Mvvm、ManagedBass、Microsoft.Data.Sqlite / SQLitePCLRaw、TagLibSharp、Serilog / Serilog.Sinks.File。著作权与许可分别归各上游项目，以 NuGet 包与上游仓库随附的许可文本为准。

## 八、安全约定

API Key 只能存在 `data/config.json` 并在设置页填写，**永远不进仓库、不进日志、不进任何报错信息**。提交前请确认 `git status` 里没有 `data/` 目录。

仓库附带 `.githooks/pre-commit` 安全检查。每个新克隆需执行一次 `git config core.hooksPath .githooks`，之后每次提交都会检查完整暂存区，阻止保留前缀意外入仓。

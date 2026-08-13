# Player

Windows 本地音乐播放器（类 foobar2000 的轻量定位 + 现代简洁界面），核心卖点是 ASIO / WASAPI 独占的位完美输出，以及通过 ChKSz API 接入的网易云在线能力。

完整规划见 [PLAN.md](PLAN.md)。**当前进度：P0（基础播放）+ P0.1（滑条修复）+ P1（媒体库 / 歌单 / 三栏主界面）已完成。**

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
dotnet publish Player.App -c Release -r win-x64 --self-contained -o publish
```

用 Visual Studio：双击 `Player.sln`，把 `Player.App` 设为启动项目，F5 即可。

## 三、目录结构

```text
MusicPlayer/
├── PLAN.md                 开发规划（唯一事实来源，改需求先改它）
├── Player.sln
├── Player.Core/            全部业务逻辑，纯 net8.0，不引用任何 WPF 类型
│   ├── Audio/              BassRuntime、PlaybackEngine、PlaybackList（含四种播放模式）
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

仓库的 `native/x64/` 里已经放好了 7 个 64 位 DLL（来自 [un4seen.com](https://www.un4seen.com/bass.html)，BASS 2.4.18.3）：

| 文件 | 作用 |
| --- | --- |
| `bass.dll` | 核心库，内置 mp3 / wav / aiff / ogg |
| `bassflac.dll` | FLAC |
| `bassape.dll` | Monkey's Audio (APE) |
| `basswv.dll` | WavPack |
| `bassopus.dll` | Opus |
| `bass_aac.dll` | AAC / MP4（第三方 add-on，下载路径在 `files/z/2/` 下） |
| `bassalac.dll` | ALAC |

要手动补充或升级时：从官网下载对应 zip，取出其中 **`x64/` 子目录里的 DLL**，放进 `native/x64/`，重新构建即可。程序启动时会逐个 `Bass.PluginLoad`，加载结果写在日志里。

P2 做 ASIO / WASAPI 时还需要再补 `bassasio.dll`、`bassmix.dll`、`basswasapi.dll`（同样取 x64 版本）。

> BASS 个人非商业使用免费；将来若要商用需向 un4seen 购买授权（PLAN 第 11 节）。

## 五、已实现的功能

**播放（P0 / P0.1）**：BASS 默认输出（DirectSound），打开 / 播放 / 暂停 / 停止 / 精确 seek / 音量；进度条拖动与点击跳转不会弹回；曲目结束自动续播；支持 mp3 / flac / m4a / aac / alac / ape / wv / ogg / opus / wav / aiff。

**媒体库（P1）**：SQLite 持久化；多根目录管理；全量与增量扫描（判据 mtime + 文件大小）、并行读标签、FileSystemWatcher 自动跟随目录变化；TagLibSharp 读标签（缺标签时按文件名兜底，会先剥掉曲号前缀）；内嵌封面按内容 hash 去重缓存。

**浏览（P1）**：左侧栏三组导航 —— 媒体库（全部歌曲 / 专辑 / 艺术家）、手工歌单、**文件夹虚拟歌单**（媒体库根目录下每个顶层子文件夹自动成为一个只读歌单，含子目录递归，随扫描自动更新）；曲目列表七列（标题 / 歌手 / 专辑 / 时长 / 格式 / 采样率 / 位深）可点击列头排序、顶部即时过滤、列表虚拟化。

**歌单（P1）**：新建 / 重命名 / 删除、右键「添加到歌单」、从歌单移除、拖拽排序（未排序未过滤时）、m3u8 导入导出。

**播放列表与模式（P1）**：双击任意列表即以当前可见顺序开播；上一首 / 下一首；顺序、列表循环、单曲循环、随机四种模式（记忆到 config.json）。

**拖放（P1）**：拖入文件夹 = 加入媒体库并开播；拖入文件 = 直接播放（不入库）。

**还没有的**（按规划分别属于后续阶段）：ASIO / WASAPI 独占与无缝播放（P2）；在线搜索、歌词、歌单同步、下载（P3–P5）；媒体键、托盘、SMTC（P6）。

## 六、安全约定

API Key 只能存在 `data/config.json` 并在设置页填写，**永远不进仓库、不进日志、不进任何报错信息**。提交前请确认 `git status` 里没有 `data/` 目录。

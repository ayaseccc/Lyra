# Player

Windows 本地音乐播放器（类 foobar2000 的轻量定位 + 现代简洁界面），核心卖点是 ASIO / WASAPI 独占的位完美输出，以及通过 ChKSz API 接入的网易云在线能力。

完整规划见 [PLAN.md](PLAN.md)。**当前进度：P0（骨架与基础播放）已完成。**

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

## 二、构建与运行

用命令行（在仓库根目录）：

```powershell
dotnet build                      # 构建整个解决方案
dotnet run --project Player.App   # 运行
```

发布一个可直接拷走的版本：

```powershell
dotnet publish Player.App -c Release -o publish
```

用 Visual Studio：双击 `Player.sln`，把 `Player.App` 设为启动项目，F5 即可。

## 三、目录结构

```text
MusicPlayer/
├── PLAN.md                 开发规划（唯一事实来源，改需求先改它）
├── Player.sln
├── Player.Core/            全部业务逻辑，纯 net8.0，不引用任何 WPF 类型
│   ├── Audio/              BassRuntime（初始化 + 插件加载）、PlaybackEngine、PlaybackQueue
│   └── Infra/              AppPaths（便携目录）、LogSetup（Serilog）
├── Player.App/             WPF 界面（net8.0-windows，产物 Player.exe）
│   ├── Themes/Dark.xaml    深色主题与控件样式
│   └── ViewModels/         PlayerViewModel
└── native/x64/             BASS 系列原生 DLL（x64），构建时自动复制到输出目录
```

运行期数据全部落在 exe 同级的 `data/` 下（便携绿色，整个文件夹拷走即迁移）：

```text
data/
├── config.json   P3 起使用，存放 API Key —— 已在 .gitignore 中，永不入仓库
├── library.db    P1 起使用
├── cache/        歌词、封面缓存
└── logs/         player-YYYYMMDD.log
```

## 四、BASS 原生 DLL

仓库的 `native/x64/` 里已经放好了 P0 需要的 7 个 64 位 DLL（来自 [un4seen.com](https://www.un4seen.com/bass.html)，BASS 2.4.18.3）：

| 文件 | 作用 |
| --- | --- |
| `bass.dll` | 核心库，内置 mp3 / wav / aiff / ogg |
| `bassflac.dll` | FLAC |
| `bassape.dll` | Monkey's Audio (APE) |
| `basswv.dll` | WavPack |
| `bassopus.dll` | Opus |
| `bass_aac.dll` | AAC / MP4（第三方 add-on，下载路径在 `files/z/2/` 下） |
| `bassalac.dll` | ALAC |

要手动补充或升级时：从官网下载对应 zip，取出其中 **`x64/` 子目录里的 DLL**，放进 `native/x64/`，重新构建即可（`Player.App.csproj` 会把该目录下所有 dll 复制到输出目录根部）。程序启动时会逐个 `Bass.PluginLoad`，加载结果写在日志里，界面顶部也会显示已加载插件数量。

P2 做 ASIO / WASAPI 时还需要再补 `bassasio.dll`、`bassmix.dll`、`basswasapi.dll`（同样取 x64 版本）。

> BASS 个人非商业使用免费；将来若要商用需向 un4seen 购买授权（PLAN 第 11 节）。

## 五、P0 已实现的功能

- 启动时初始化 BASS 默认输出设备（DirectSound），自动加载全部格式插件，失败有明确提示且不崩溃
- 播放引擎：打开 / 播放 / 暂停 / 停止 / 精确 seek / 音量，曲目结束自动播放下一首
- 底部播放条：封面占位、标题与艺术家、格式与采样率信息、可拖动可点击的进度条、上一首 / 播放暂停 / 停止 / 下一首、音量滑块
- 把音频文件或文件夹拖进窗口即播；也可以点「打开文件…」或用命令行传入路径
- 支持 mp3 / flac / m4a / aac / alac / ape / wv / ogg / opus / wav / aiff
- 日志写入 `data/logs/`，排查 BASS 错误码用

**P0 还没有的**（按规划分别属于后续阶段）：媒体库与曲库列表、歌单（P1）；ASIO / WASAPI 独占与无缝播放（P2）；在线搜索、歌词、下载（P3–P5）；媒体键与托盘（P6）。

## 六、安全约定

API Key 只能存在 `data/config.json` 并在设置页填写，**永远不进仓库、不进日志、不进任何报错信息**。提交前请确认 `git status` 里没有 `data/` 目录。

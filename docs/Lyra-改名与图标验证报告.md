# Lyra 改名与图标验证报告

日期：2026-08-18
范围：产品呈现层改名、透明图标接线、文件关联升级桥与便携发布包

## 核销清单

| 项目 | 状态 | 验证结果 |
| --- | --- | --- |
| 程序集与发布产物 | 通过 | `AssemblyName=Lyra`，输出 `Lyra.exe`；版本/产品/公司元数据为 Lyra 1.0.0 |
| 图标源稿 | 通过 | `assets/lyra-icon.svg`；透明底，淡紫 `#B9A2EF` → 淡粉 `#D7A9E7` / `#F0A9C9` 几何 L，右上 Vega 圆点 |
| ICO | 通过 | `Player.App/Assets/lyra.ico` 含 256/64/48/32/24/16 六个 32bpp PNG 帧；16px 使用像素网格微调；每帧四角透明 |
| WPF 窗口与托盘 | 通过 | 主窗、迷你窗、桌面歌词、设置/对话框、TitleBar、NotifyIcon 全部接入 Lyra 图标与呈现名 |
| SMTC 与日志 | 通过 | 空闲 SMTC 名称、托盘 tooltip 为 Lyra；日志文件名为 `lyra-*.log` |
| 单实例 | 通过 | 主协议使用 `Lyra_` / `Lyra_InstancePipe_`；升级期同时持有旧 Player Mutex 并监听旧管道，二级实例只向一个目标管道转交 |
| 文件关联 | 通过 | Lyra ProgID、Capabilities、RegisteredApplications、DefaultIcon 全套注册；不改受保护 UserChoice；旧 ProgID/Applications 桥仅在仍被引用时保留，旧候选隐藏并可转到 Lyra |
| data/ 兼容 | 通过 | `data/` 路径、数据库与配置格式未改，不需要迁移；用户只需把旧 `data/` 整体放到新程序目录 |
| 文档与许可 | 通过 | README、用户指南、第三方许可声明统一使用 Lyra；保留 GD Studio CC BY-NC 与 BASS 非商业许可说明 |

## 构建与自动化验证

- Release 严格构建：`0` 警告 / `0` 错误（`dotnet build Player.sln -c Release --no-restore -warnaserror`）。
- 九模式离线 harness：`348` 通过 / `0` 失败 / `1` 跳过；唯一跳过为需要目标声卡的 ASIO 人工听测。
- library harness：`12/12` 通过，覆盖全量/增量扫描、库外导入、歌单持久化与重启重建。
- pre-commit 自测：合法 PLAN/Harness 白名单内容通过；故意暂存的保留前缀内容被拦截。
- Windows `.flac` 关联实测：冷启动 `1` 个 Lyra 进程；运行中打开第二首后仍为 `1` 个进程；关闭到进程退出约 `92 ms`，紧接着重新打开可立即接替；最终残留 `0`。Lyra 新管道使用固定请求 ID + ACK/NACK，超时重试不会重复入队。
- ChKSz 新 Key 真实搜索：**失败**，HTTP `401`；额度头 `Free=unknown / Paid=unknown`。本次请求没有将 Key、查询串或响应正文写入日志/报告。该结果表示当前配置 Key 未被上游接受，需更新有效 Key 后再复测，不影响本地播放、构建和发布包。

## 发布包

`Lyra-v1.0.0-win-x64.zip`

- self-contained `win-x64`
- 285 个条目，77,767,068 字节
- SHA-256：`A8D362488BAC3AD9A077665A91DEEED6622478052B99A90C1817532A12C76BAB`
- 独立审计：无 `Player.exe` 主产物、无 `data/`/配置/日志/数据库/Key 前缀；恰好 9 个批准的 BASS DLL；无 `bass_aac.dll`
- 干净目录解压后 `Lyra.exe` 启动 3 秒存活；真实 Release 构建重启 4 秒存活；测试结束无 Lyra/Player 残留进程

旧 Player 二进制仍使用无 ACK 的兼容管道；改名过渡期若旧进程恰在退出窗口接收文件，旧客户端无法获知 NACK。这不影响 Lyra↔Lyra 的请求 ID/ACK 链路；升级后直接使用 Lyra 可消除该边界。

## 用户目验提示

首次升级请直接启动一次 `Lyra.exe`，再在 Windows“打开方式/默认应用”中为音频格式选择 Lyra。Windows 的 `UserChoice` 带哈希保护，应用不会静默改写用户已经选择的其他默认播放器。

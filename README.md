# Codex Token Glass

给 Windows 版 Codex 加一条贴附状态栏：token 用量、缓存命中率、模型输出吞吐，以及可交互的 Liquid Glass 详情和设置面板。

**这是独立的 Windows 桌面伴随程序。** 它贴附在官方 Codex 窗口上，不是 `codex plugin add` 安装的 MCP 插件，也不修改、注入或替换 Codex。

基于 [dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar) 修改，保留上游 MIT 许可及署名。图形材质使用 [LiquidGlass](https://github.com/shayann07/liquidglass)，不是 Apple 官方组件或原生 iOS API。

## 按钮尺寸修正与展开居中（glass.30）

一级详情面板的齿轮、统计胶囊和图标比例恢复为 glass.26 的尺寸；设置窗口关闭按钮缩至 32 DIP，与齿轮同尺寸、同角部间距，并缩小关闭符号。glass.28 将一级按钮放大的方向不符合原意，本版已纠正。统计窗口的关闭按钮仍未调整。

本次交接追加：停靠及手动放置的状态条展开详情时，面板以状态条为中心；靠近屏幕左右边缘时调整面板位置，保持已在屏幕内的状态条不跳动。新增 73 项定位检查，覆盖不同 DPI、上下展开、左右边缘及负坐标显示器。

交接复验：1022 项测速/统计检查、510 项应用自检、11 项隔离路由用例通过；23 个渲染场景文字裁边为 0。中转协议模拟测试也通过，覆盖 Unicode、重复完成、跨轮隔离、日志不可写及 EOF 转发。详情及未解决问题见 [glass.27–30 交接文档](docs/HANDOFF-GLASS-30.zh-CN.md)。

## 原生菜单避让（glass.29）

打开 Codex 输入框的模型、强度等菜单时，状态条暂时隐藏，关闭后重新布局，避免置顶文字覆盖原生菜单。后台只读取按钮的展开状态；菜单出现后可能仍有约 0.1 秒的避让延迟。菜单遮挡修复已在交接前由用户实机确认。

## 等待文字与按钮尺寸（glass.28）

等待状态按实际显示的句子测量宽度，去掉句尾省略号；空间不足时缩为“等待中 / Waiting”。中文测量与绘制使用相同字体，新增三个中英文等待句渲染场景。

本版曾将一级详情面板的统计、设置按钮与设置窗口的关闭按钮统一为 36 DIP；该尺寸调整已由 glass.30 更正。

## 会话识别与统计修复（glass.27）

重名对话通过 IPC 正在跟随的线程辅助区分，且只有该线程自身的显示名与可见标题一致时才采用；新增六项区分用例。重名对话在真实界面上的恢复情况仍待确认。

修复安装脚本误判单个非 .NET 10 SDK、日志半行重读丢记录，以及异常 JSON 结构导致统计停止更新的问题。已关闭月份在日志减少时保留归档；上一轮迟到的用量只计入完成数，不再打乱当前轮计时；999,950 起提前进位到 M，避免显示“1000.0K”。

## 自动字体与状态栏排版（glass.24）

状态移到左侧，之后依次为仪表盘速度、Tokens、缓存率。基础字号提高到 14 DIP，各组按真实文字宽度统一间距。后台读取 Codex 模型选择器的 UIA 字体属性，按本机字体和 DPI 重新测量；无法读取或字体未安装时明确回退到系统字体。

本机识别为 Segoe UI，字号未报告，使用 14 DIP 下限。437 项应用自检通过，20 个浅深色/DPI 绘制场景未报告裁边。独立原始事件中转仍是待实机接入的诊断原型，未改变正式取数链路。详见 [glass.24](docs/GLASS-24.zh-CN.txt)。

## 响应吞吐与快速恢复（glass.23）

底栏采用 DeepSeek Harness 的已完成响应统计方式：同一会话可计时响应的官方输出 token 总数，除以对应生成时长总和。响应完成后更新，等待期间保持读数；状态在右侧独立显示 Ready / Working / Output 等，不再展示 Last。

已核对本机 Harness 0.2.1-rc.5 与 dsh-codex 0.3.2。Codex 外挂使用 IPC 的 latestTokenUsageInfo 更新官方用量，以响应记录配对计时；它没有接管桌面端的原始网络流，因此同公式不等于保证同计时精度或同更新延迟。

首次先显示数据库/IPC 中已有的用量，历史响应统计在后台恢复。路径优先直接查数据库，缓存最近日志读取器；窗口标题事件唤醒识别，数据结果直接交给界面。

验证：840 项测速/统计检查、432 项应用自检、5 项隔离路由检查。32 MiB 合成日志首次后台读取返回约 0.85ms、完整统计约 53ms；单次本机探针约 278ms 识别到当前会话、839ms 取得历史吞吐。数字是本次测量，不是所有设备的延迟保证。

详细实现与边界见 [glass.23 复核记录](docs/GLASS-23.zh-CN.txt)。以下 glass.22 及更早章节为历史记录。

## 留白与工作状态（glass.22）

设置页清透度说明下保留 16 DIP 卡片内留白，状态提示及按钮顺延。工作阶段并排显示状态和 `Last 65.0 tok/s`（示例值），表示上次有效文本速度；恢复输出显示 `Speed`，结束后显示 `Ready`，没有有效读数时不编造速度。命令、搜索和思考仍使用各自状态。

本地验证：432 项应用检查、815 项测速/统计检查通过；设置页截图及数值输入、滑块联动、预设检查通过，真实全局配置未改变。

## 功能遮挡修复（glass.21）

恢复设置页被说明文字遮住的上下文数值输入框：输入框移至标题右侧，说明独占下一行。已实测手动输入、上下加减、滑块双向联动及三个容量预设，测试未改变全局配置。

[控件验证结果](docs/glass21-controls-validation.json)

## 材质与字号修复（glass.20）

一级菜单正文、数值和统计按钮适度加大。修复设置面板捕获旧菜单背景、透明重绘重复叠加及边缘层不同步的问题；避免设置与统计同时叠放。

[glass.20 界面验证](docs/glass20-validation.json) · [完整审查报告](docs/ROUTING-SPEED-REVIEW.zh-CN.txt)

## 测速与统计更新（glass.19）

- 文本输出显示实时 `Speed`；命令、检索、工具、思考等阶段显示相应状态。无数据时不再铺满破折号，平均速度留在详情。
- 对话检测独立运行，切换立即清理旧显示并按对话缓存数据；后台旧结果不能覆盖新页面。
- 修复 0% 清透度与原生圆形按钮的矩形接缝；全局上下文设置不再挂某个模型名。
- 新增 **API 等值**的每日、对话排行与每日对话视图；本月持续累计，月度汇总自动保存到本机。金额字号收小，按钮和表格字体适度加大。
- 427 项自检、791 项测速/统计检查、5 项隔离路由检查通过。45 个同口径积分测试误差为 0%，目标不超过 5%；这不代表隐藏推理或服务端瞬时速度误差保证。

[审查报告与复现步骤](docs/ROUTING-SPEED-REVIEW.zh-CN.txt) · [验证摘要](docs/glass19-validation.json)

月度文件：`%LOCALAPPDATA%\CodexStatusbar\monthly-api-equivalent.json`。API 等值不是现金收益或套餐账单。下方截图属于此前版本；源码与 Releases 安装包分别更新。

## 高清界面预览

[查看全部 39 张高清原图：中英 / 明暗 / 状态条 / 详情 / 设置 / 菜单](docs/SCREENSHOTS.md)

<table>
<tr><th>Windows 用量详情</th><th>Windows 设置</th></tr>
<tr><td><a href="https://raw.githubusercontent.com/weienjiang0308-cyber/codex-token-glass/main/assets/screenshots/windows/windows-details-light-zh.png"><img src="assets/screenshots/windows/windows-details-light-zh.png" width="390" alt="Windows 中文用量详情，4倍原图"></a></td><td><a href="https://raw.githubusercontent.com/weienjiang0308-cyber/codex-token-glass/main/assets/screenshots/windows/windows-settings-light-zh.png"><img src="assets/screenshots/windows/windows-settings-light-zh.png" width="390" alt="Windows 中文设置，4倍原图"></a></td></tr>
<tr><th>macOS 预览：用量详情</th><th>macOS 预览：设置</th></tr>
<tr><td><a href="https://raw.githubusercontent.com/weienjiang0308-cyber/codex-token-glass/main/assets/screenshots/macos/macos-details-dark-en.png"><img src="assets/screenshots/macos/macos-details-dark-en.png" width="390" alt="Mac 英文深色详情，4倍原图"></a></td><td><a href="https://raw.githubusercontent.com/weienjiang0308-cyber/codex-token-glass/main/assets/screenshots/macos/macos-settings-dark-en.png"><img src="assets/screenshots/macos/macos-settings-dark-en.png" width="390" alt="Mac 英文深色设置，4倍原图"></a></td></tr>
</table>

*全部使用合成数据与背景，未截取真实会话或桌面。Windows 设置原图 2208×2920；Mac 详情和设置原图 1640×3040。点击图片查看原始 PNG。Mac 界面属于[独立移植预览](https://github.com/weienjiang0308-cyber/codex-token-glass/pull/1)。*


## 功能

- 贴附到当前 Codex 对话，切换对话后跟随读取对应的本地记录。
- 输入、输出、缓存输入、未缓存输入、推理 token、累计总量与缓存命中率。
- 本轮平均生成吞吐、已记录响应峰值、任务总耗时、模型生成耗时。
- GPU 折射材质、展开与按压回弹，支持系统减少动态效果、减少透明度及高对比度设置。
- 中英切换、跟随 Codex 的明暗与自定义配色、可保存的玻璃清透度。
- 独立的每用户后台启动，避免监测器随 Codex 的执行进程树退出。
- 设置页可修改本地上下文与压缩阈值；**安装程序不自动改这些设置，更不会改 5% 缓冲**。

## 指标到底是不是真的？

不是所有数字都是“估计”。请分清两类：

| 指标 | 来源与含义 | 准确性边界 |
|---|---|---|
| 输入 / 输出 / 缓存 token | Codex 会话记录中的上游 usage 计数 | 读的是记录值，不按字符猜；受记录完整性与版本兼容性影响 |
| 缓存命中率 | `cached_input_tokens / input_tokens` | 从记录计数计算的比例，不是根据响应速度猜缓存 |
| 详情响应均速 | 可计时响应的官方输出 token 总和 ÷ 对应响应时长总和 | 日志时间统计；不是服务端逐 token 瞬时测速 |
| 状态栏 tok/s | 同一会话已完成、可计时响应的官方输出 token 总和 ÷ 对应时长总和 | 包含官方输出中的推理和工具参数；响应完成后更新，不是逐秒瞬时速度 |
| 峰值响应速度 | 已完成、可计时响应中的最高响应均速 | 不是某一瞬间的峰值；不足 500ms 的时间窗不参与峰值 |
| 任务耗时 | 本轮开始到结束的墙钟时间 | 包含工具与等待；与模型生成时间是不同口径 |

输出速度包含 Codex 上报的推理输出，不仅是屏幕上可见的正文。工具执行、用户思考和排队时间不计入“模型生成时间”分母。按响应累计对应的生成时长，重复 response_id 与镜像 usage 不重复累计。

日志通常在响应完成后更新，界面因此可能延迟；短响应、缺失时间戳、打开面板时只恢复到部分本轮历史，都会影响速度统计。不能把它当作服务端内部每秒解码轨迹或账单。

详见 [指标定义与跨产品口径](docs/METRICS.md)。

## 最省事：把下面这段发给你的 AI

完整可复制版本也保存在 [AI-INSTALL-PROMPT.zh-CN.txt](AI-INSTALL-PROMPT.zh-CN.txt)。

```text
请在我的 Windows 10/11 x64 电脑上安装并配置这个开源 Codex 状态栏：
https://github.com/weienjiang0308-cyber/codex-token-glass

请先阅读 README.md、install.ps1 和 docs/METRICS.md，确认这是 Windows 桌面伴随程序，
不要运行 codex plugin add，也不要把它当成浏览器扩展。

直接完成安装和验证，不要只给我教程。优先下载仓库 Releases 中的正式安装包，
校验 SHA256 后运行 install.ps1，界面选择中文、保留 English 切换，
跟随 Codex 配色，玻璃清透度设为 80，配置独立的每用户后台启动。
不要修改我的模型、API 地址、密钥、Codex 上下文窗口、自动压缩阈值或 5% 缓冲，
不要复制作者的本机配置，也不要上传我的会话日志。

如果没有可用 Release，则克隆源码后运行 install.ps1 -FromSource；
缺少 .NET 10 SDK 时，脚本可从微软下载并校验临时专用 SDK，不改系统 SDK。
已有本程序时使用正常退出/本用户控制通道更新，不强制结束 Codex 或其他工作任务。

完成后检查启动任务、进程和安装路径；如果 Codex 正在运行，打开详情和设置页，
核对当前对话的 token/缓存字段并给我一张截图。
速度必须说明为日志时间统计，不能声称是服务端瞬时实测。
系统策略或权限确实阻止某一步时，说明具体阻碍和需要我做的最少操作。
```

## 手动安装

要求：Windows 10/11 **x64**，已安装并至少运行过 Codex。OpenGL 显卡加速推荐；不可用时会使用兼容路径，动效性能可能降低。不需要 API key，不需要系统安装 .NET 运行时。

1. 从 [Releases](https://github.com/weienjiang0308-cyber/codex-token-glass/releases) 下载并校验 `CodexTokenGlass-win-x64.zip`。
2. 克隆/下载本仓库，在 PowerShell 中运行：

```powershell
.\install.ps1 -Language zh -GlassTransparency 80
```

本地已有安装包时：

```powershell
.\install.ps1 -PackagePath C:\Downloads\CodexTokenGlass-win-x64.zip -Language zh -GlassTransparency 80
```

从源码安装并自动准备专用构建 SDK：

```powershell
.\install.ps1 -FromSource -Language zh -GlassTransparency 80
```

查看计划、不做安装：

```powershell
.\install.ps1 -WhatIf
```

脚本策略若阻止运行，请遵循你电脑的策略，不要全局关闭安全策略。直接运行下载包里的 `CodexStatusbar.exe` 也可临时使用；要可靠地跨 Codex 重启运行，建议使用安装脚本配置后台启动。

## 安装与配置位置

- 程序：`%LOCALAPPDATA%\Programs\CodexTokenGlass\`
- 后台任务：`Codex Token Glass Watcher`，仅当前用户登录时运行，不需要管理员权限。
- 外观偏好：`$CODEX_HOME\codex-token-glass\ui.json`；未设置 `CODEX_HOME` 时位于 `~\.codex\codex-token-glass\ui.json`。
- 旧版 `%LOCALAPPDATA%\CodexStatusbar\ui.json` 在首次读取时可作为兼容来源。

安装器只写本程序文件、外观偏好及当前用户的启动项。只有你在设置页主动保存上下文配置时，才会修改 Codex 的 `model_context_window` 与 `model_auto_compact_token_limit`，且会保留原值备份。

本地配置不能创造服务端没有的能力，实际窗口还受模型与客户端约束。安装器不会帮你伪造模型能力表或自动提高窗口。

## 隐私和兼容性

- 通过本地 Codex IPC 跟随会话；IPC 不可用时，最近活动会话回退可能出现短暂识别偏差。
- 只读 `state_5.sqlite` 与会话 JSONL，不需要也不读取 `auth.json`。
- 为显示折射，打开面板时会把面板后方画面采样到**本机内存**。不上传，也不把该采样作为日志保存在磁盘；关闭重开会刷新背景。
- 当前画面来源是打开时的快照，不声称是 Apple 原生材质或持续实时桌面捕获。
- Codex 的本地记录与 IPC 属于可能变化的实现细节；本项目会尽力兼容，不能保证每次 Codex 更新都无需适配。

## 开发

```powershell
dotnet --list-sdks  # .NET 10 SDK
.\build.ps1
```

构建产物是自包含单文件 `dist\CodexStatusbar.exe`。完整验证：

```powershell
dist\CodexStatusbar.exe --self-test fixtures
```

不会为测试启动模型任务或消耗额外模型请求。文档中的演示素材必须用合成数据，禁止提交真实会话、数据库、账号配置、密钥或桌面截图。

## 致谢与许可

[MIT License](LICENSE)。这不是 OpenAI 官方项目。

- 上游：[dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar)
- 更早的基础：[soleillevant0125/codex-token-overlay](https://github.com/soleillevant0125/codex-token-overlay)
- Liquid Glass 渲染：[shayann07/liquidglass](https://github.com/shayann07/liquidglass)，Apache-2.0
- SkiaSharp、Inter、Lucide 许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与对应 assets 子目录。

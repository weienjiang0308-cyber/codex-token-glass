# 交接文档：glass.27 – glass.30（2026-11-10 至 2026-11-11）

本文档写给接手这个仓库的人或 AI。内容是 Claude 在这两天里对 Codex Token Glass 做的审计、修改、验证，以及还没做完的事。

## 一、当前状态

| 项目 | 状态 |
|---|---|
| 源码版本 | `1.0.0-rc3-glass.30`（`src/CodexStatusbar/CodexStatusbar.csproj`） |
| 已安装并运行的版本 | glass.30，位于 `%LOCALAPPDATA%\Programs\CodexStatusbar\CodexStatusbar.exe` |
| 开机自启 | 计划任务 `CodexStatusbar Watcher`，参数 `--background`，未改动 |
| git | 分支 `codex/responsive-live-status`，HEAD 仍是 `c8ec5e7`，**所有改动都未提交、未推送** |
| 远端 | `https://github.com/weienjiang0308-cyber/codex-token-glass.git` |

工作区里混着两批未提交的改动：

1. 之前的 AI 做的 glass.23 – glass.26（文档 `docs/GLASS-23` 到 `GLASS-26`、`HarnessThroughputProjection.cs`、`ResponseSpeedDisplay.cs`、`ComposerTypeface.cs`、`tools/Codex.StreamRelay/` 等）。
2. 本文档描述的 glass.27 – glass.30。

有些文件同时含两批改动（例如 `OverlayContext.cs`、`TokenStripForm.cs`、`ResponseSpeed.cs`、`Program.cs`），提交时不容易拆开。

## 二、glass.27：审计后修复的 7 个缺陷

每一条都先用未修改的源码复现，再修复，再确认。

### 1. 重名对话无法识别

- 现象：状态栏靠"窗口标题在 `state_5.sqlite` 里唯一"来确定当前对话，重名就放弃，一直显示"正在寻找当前 Codex 会话"。用户本机 73 个主对话里有 9 个重名。
- 修改：
  - `src/CodexStatusbar/VisibleConversationResolver.cs`：`VisibleConversationMonitor` 新增可选参数 `activeThread` 和静态方法 `Disambiguate`。标题不唯一时，用 IPC 正在跟随的线程 ID 区分，且只有该线程自己的显示名等于窗口标题时才采用，来源标记为 `visible-title-ipc`。
  - `src/CodexStatusbar/OverlayContext.cs`：构造 `_visibleMonitor` 时传入 `()=>_routeMonitor.GetStatus().ThreadId`。
  - `src/CodexStatusbar/Program.cs`：`--resolve-title-json` 增加可选的第 4 个参数（IPC 线程 ID），供测试使用。
- 验证：`tests/test_visible_routing.py` 新增 6 个用例，通过。**没有在真实界面上确认重名对话已恢复显示。**

### 2. `install.ps1 -FromSource` 误判已装 .NET 10

- 现象：系统只装一个非 10 版 SDK 时，`($x -match '^10\.') -ne $null` 恒为真，于是用旧 SDK 去编译 net10 项目。
- 修改：`install.ps1` 改为 `@(@(& $sdk.Source --list-sdks) -match '^10\.').Count -gt 0`。
- 验证：对"只有 7.0""只有 10.0""两者都有""都没有"四种输入分别得到 False / True / True / False。

### 3. 日志半行导致记录丢失

- 现象：读取时遇到只写了一半的行，这条记录会丢。原因有两个：
  - `TryUpdate` 从 `Offset` 重读时，又把内存里的 `_pending` 拼在前面，半行内容重复，完成后的记录变成坏 JSON。
  - 尾部片段以 `}` 结尾时，不管能否解析都会清空 `_pending`。
- 修改：`src/CodexStatusbar/RolloutReader.cs`，重读前清空 `_pending`；新增 `IsCompleteJson`，只有完整 JSON 才提前消费。
- 验证：把一条 `task_complete` 拆成两次写入，修复前状态卡在"进行中"并出现 `malformed-json`，修复后正常。

### 4. 一条异常记录让统计永久停更

- 现象：日志里出现根节点或 `payload` 不是对象的 JSON 行时，`UsageFile.Parse` 抛出 `InvalidOperationException`，后台循环没有兜底，直接退出。
- 修改：
  - `src/Codex.SpeedMetrics/UsageStatistics.cs`：先校验根节点和 `payload` 的类型，并捕获 `InvalidOperationException`。
  - `src/CodexStatusbar/UsageStatisticsService.cs`：`Loop` 里给 `RefreshAsync` 加兜底，单次失败只写 `Error`，不退出循环。

### 5. 月度归档被改小

- 现象：已关闭的月份只要含"未定价"响应，每次刷新都会用现存日志重算；日志被删后，存档会缩水。
- 修改：`src/Codex.SpeedMetrics/MonthlyApiArchive.cs`，已关闭月份重算出的响应数少于存档时，保留存档。

### 6. 上一轮迟到的用量记录打乱当前轮

- 现象：新一轮开始后，如果又来一条属于上一轮的 `token_usage_record`，分析器会回退到上一轮，导致新一轮的首个响应不计时。
- 修改：`src/Codex.SpeedMetrics/ResponseSpeed.cs`，`ResponseSpeedAnalyzer` 新增 `_earlierTurns`，这类记录只计入 `SessionCompletedResponses`。
- 验证：复现用例从 2/4 变为 3/4。

### 7. 999,950 – 999,999 显示成 "1000.0K"

- 修改：`src/CodexStatusbar/TpsTracker.cs`（`MetricFormat.CompactTokens`）和 `src/CodexStatusbar/OverlayPresentation.cs`（`FormatTokenCount`）的进位阈值改为 `999_950`。

### 配套测试

- `tests/SpeedMetrics.Tests/Program.cs`：新增 3 项回归检查（第 4、5、6 条），总数 1019 → 1022。
- `tests/test_visible_routing.py`：新增 6 个重名区分用例。

## 三、glass.28：两处界面调整

### 1. 等待状态文字不再被截断成省略号

- 原因：状态栏窗口的宽度是按 "Connecting to chat" 量的，画的却是更长的"新对话，等待 token 数据…"。
- 修改：
  - `src/CodexStatusbar/OverlayPresentation.cs`：`CreateWaiting` 去掉状态句尾的 `…`，并把状态句本身作为变体，即 `[状态句, "等待中" / "Waiting"]`；状态为空时仍是 `["Connecting to chat", "Connecting"]`。
  - `src/CodexStatusbar/TokenStripForm.cs`：删除收起状态下单独绘制 `StatusText` 的分支，统一走按变体测宽的路径；`StripPartWidth` 对含中文的文字改用与绘制相同的中文字体测宽。
  - `src/CodexStatusbar/RenderProbe.cs`：`RenderWaitingCase` 增加 `name`、`status` 参数，新增 3 个等待句渲染用例。
- 现在显示的文字：

  | 场景 | 中文 | 英文 |
  |---|---|---|
  | 新对话，还没有数据 | 新对话，等待 token 数据 | New chat, waiting for token data |
  | 还没识别出当前对话 | 正在寻找当前 Codex 会话 | Looking for the current Codex chat |
  | 空间太窄 | 等待中 | Waiting |

### 2. 按钮尺寸（这一版方向做反了，已在 glass.30 改正）

- glass.28 把一级面板的齿轮和"统计"放大到 36 DIP，设置面板的关闭按钮缩到 36 DIP。
- 用户的本意是：一级面板保持原样，把设置面板的关闭按钮缩小到和它一样。见第四节之后的 glass.30。

## 四、glass.29：Codex 菜单打开时状态条让开

- 现象：状态条是独立的置顶窗口，Codex 的模型/强度菜单画在它自己的窗口里，所以状态条的文字会盖在菜单上。
- 修改：
  - `src/CodexStatusbar/ComposerDock.cs`：
    - `ComposerDockSnapshot` 新增 `PopupOpen`。
    - `Walk` 收集输入框子节点里所有带 ExpandCollapse 模式的按钮（子节点本身和下一层，最多 16 个）。
    - 每轮循环读一次这些按钮的 `ExpandCollapseState`，任意一个为 Expanded 或 PartiallyExpanded 即 `PopupOpen = true`。只读属性，不调用任何模式。
    - 按钮失效（React 替换了节点）时，按 750 ms 限速触发一次重新遍历。
    - 有按钮可监视时，轮询间隔上限为 90 ms（原来空闲时是 250 ms）。
  - `src/CodexStatusbar/OverlayContext.cs`：`PlaceStrip` 遇到 `dock.PopupOpen` 就隐藏；新增 `ApplyComposerPopupState`，挂在 50 ms 的 `_routeTimer` 上，菜单开关时立即隐藏或重新布局。
  - `src/CodexStatusbar/Program.cs`：`--composer-font-probe` 的输出增加 `PopupOpen`，便于诊断。
- 验证：
  - 对正在运行的 Codex 实测，没有菜单打开时 `PopupOpen` 为 false。
  - 用户实机确认：打开模型/强度菜单后状态条不再叠在上面。
- 已知表现：菜单刚弹出时，状态条可能还会停留约 0.1 秒。

## 四点五、glass.30：按钮尺寸改回正确方向

- `src/CodexStatusbar/TokenStripForm.cs`：一级面板的齿轮（32 DIP，内缩 16）、"统计"胶囊（宽 1.65 倍、高 22/24、圆角 `D(22)`）和齿轮图标占比（0.6）全部恢复为 glass.26 时的数值。相对 `c8ec5e7` 的差异里，这几处与 glass.26 的未提交改动一致，不是这两天新加的。
- `src/CodexStatusbar/SettingsForm.cs`：关闭按钮由 44 DIP 改为 32 DIP，位置 (504,16)，与一级面板齿轮的尺寸和离角距离相同；"×" 的线长按按钮大小收一点（半长不超过按钮宽度的 14%）。
- 验证：`--demo-output` 和 `--settings-controls-probe` 的截图并排目视比较，两个圆形按钮大小一致；应用自检 437 项通过。**用户还没有在实机上确认。**

## 五、验证记录

| 检查 | 结果 | 对应版本 |
|---|---|---|
| `tests/SpeedMetrics.Tests` | 1022 项通过 | glass.27 时运行，之后没有再改这个库 |
| 发布版 `--self-test fixtures` | 437 项通过，0 失败 | glass.30 |
| `tests/test_visible_routing.py` | 11 个用例通过 | glass.28 |
| `--render-probe` + `tools/check_render_probe.py` | 23 个场景，文字裁边 0 个 | glass.28 |
| 等待文字 | 离屏渲染截图目视确认 | glass.28 |
| 按钮尺寸 | 两个面板截图并排目视确认 | glass.30 |
| 菜单遮挡 | 用户实机确认 | glass.29 |

没有做的验证：

- 重名对话在真实界面上是否恢复显示。
- glass.29、glass.30 没有重跑 `tests/SpeedMetrics.Tests`、路由测试和渲染探针（这次只改了 `ComposerDock.cs`、`OverlayContext.cs`、`Program.cs` 的一行输出）。
- `tests/test_stream_relay.py` 从未在这两天里运行过。

## 六、如何构建、测试、安装

系统自带的 dotnet 只有 7.0.410，构建要用仓库上一级目录里的私有 SDK 10.0.100。在仓库根目录下，用 PowerShell：

```powershell
$env:DOTNET_ROOT = (Resolve-Path ..\.tools\dotnet).Path
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

# 库测试
dotnet run --project tests/SpeedMetrics.Tests/SpeedMetrics.Tests.csproj -c Release

# 发布到临时目录（不要直接覆盖正在运行的安装）
dotnet publish src/CodexStatusbar/CodexStatusbar.csproj -c Release -r win-x64 --self-contained true -o $env:TEMP\ctg-publish

# 应用自检、路由测试、渲染探针
& $env:TEMP\ctg-publish\CodexStatusbar.exe --self-test fixtures
python tests/test_visible_routing.py $env:TEMP\ctg-publish\CodexStatusbar.exe
& $env:TEMP\ctg-publish\CodexStatusbar.exe --render-probe $env:TEMP\ctg-render
```

注意：`CodexStatusbar.exe` 是窗口程序，直接运行看不到输出，要用 `Start-Process -Wait -RedirectStandardOutput` 取结果和退出码。

这两天用的安装步骤（不是 `install.ps1`，因为现有安装在旧目录 `Programs\CodexStatusbar`，而 `install.ps1` 默认装到 `Programs\CodexTokenGlass`）：

1. 把现有 exe 复制为 `CodexStatusbar.before-glassNN-日期.exe`。
2. 运行现有 exe 加 `--control-exit`，等旧进程退出。
3. 用新 exe 覆盖。
4. `Start-ScheduledTask -TaskName 'CodexStatusbar Watcher'`。
5. 核对版本号和文件哈希。

同目录下现有的回退备份：

- `CodexStatusbar.before-glass27-20261110.exe`（即 glass.26）
- `CodexStatusbar.before-glass28-20261111.exe`（即 glass.27）
- `CodexStatusbar.before-glass29-20261111.exe`（即 glass.28）
- `CodexStatusbar.before-glass30-20261111.exe`（即 glass.29）

## 七、已知但没有修的问题

界面文字：

- 切换对话瞬间的"等待会话 {id} 的 token 数据"（`OverlayContext.cs` 里 `_pendingSessionVersion != _observedSessionVersion` 那段）只有中文，而且缩写 ID 中间仍带 `…`。建议改成不带 ID 的"正在切换对话 / Switching chat"。
- 启动瞬间的"正在寻找当前 Codex 会话…"（`OverlayContext` 构造函数里）没有英文版本。
- "价格未知"（`GlassStatisticsForm.cs` 的 `Price`）和"本对话 API 等值"菜单项在英文界面下仍是中文。
- 统计窗口的关闭按钮仍是 48 DIP，没有跟着改成 32。

稳定性，都只从代码读出、没有实际触发过：

- `CodexLiveStream.cs`：`ConsiderTurn` 等处遇到意外的 JSON 结构（例如 `items` 不是数组）会抛异常，导致每 1.5 秒断线重连。
- `LocalControl.cs`：管道创建失败时循环没有等待，可能占满一个 CPU 核。
- `Program.cs` 的 `FindRolloutPath`：对话不是桌面版创建的、或还没有日志文件时，每次轮询都递归扫描整个 sessions 目录。
- `ResponseSpeed.cs` 的 `IncrementalSpeedFile.ReadNew`：读修改时间和读文件长度之间有竞态，可能把日志误判为同长度重写而整份重读。结果不变，只多耗资源。

其他：

- 根目录 `VERSION` 文件仍是 `1.0.0-rc3-glass.22`，没有任何代码引用它。
- `install.ps1` 不清理临时目录，`-FromSource` 每次会留下几百 MB。
- `tools/install-watcher.ps1` 用的是旧目录名和旧任务名，和 `install.ps1` 不一致。
- `LiveSpeedDisplay.cs` 已经没有任何调用方。
- "均速"只统计带可见推理或正文的响应，纯工具调用的响应不计时。用户最大的 6 个日志里覆盖率在 66% – 92% 之间。这是设计取舍，不是算错，但没有在界面上说明。

没有审到的部分：`OverlayContext.cs`、`TokenStripForm.cs`、`OverlayLayout.cs`、`PositionFastPath.cs`、`ManualAttachment.cs` 等界面定位和渲染代码只做了关键词抽查和针对性阅读，没有逐行审。

## 八、接下来要做的事

1. **提交并推送**。建议分两次提交：先是 glass.23 – 26，再是 glass.27 – 30；拆不开的文件就合成一次并在说明里分段写明。推到 `codex/responsive-live-status` 后向 `main` 开 PR，不要强推 `main`。不要提交 `bin`、`obj`、`dist`。
2. 提交前重跑第六节的全部测试。
3. 在 `README.md` 顶部按现有格式补 glass.27 到 glass.30 四小节，并把 `VERSION` 改成 `1.0.0-rc3-glass.30`。
4. 请用户确认设置面板的关闭按钮大小合适，并打开一个重名的对话，确认状态栏不再停在"正在寻找当前 Codex 会话"。
5. 视用户意愿处理第七节里的界面文字问题。

## 九、相关材料的位置

- 上一级目录 `..\audit-probes\`：之前的 AI 留下的探针、实测记录和 `REVIEW.zh-CN.txt`。
- 上一级目录 `..\audit-deepseek-harness\`：DeepSeek Harness 的源码副本，只作测速算法的参照，没有审计。
- 上一级目录 `..\.tools\dotnet\`：私有 .NET SDK 10.0.100。

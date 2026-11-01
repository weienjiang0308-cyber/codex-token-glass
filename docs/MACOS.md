# macOS 移植预览

这是原生 macOS 客户端的第一版实现，处于预览阶段；Windows v0.1.0 正式版保持原样。

## 实现范围

- macOS 14+，Apple Silicon 与 Intel 分别打包。
- 原生 AppKit 菜单栏入口、SwiftUI 详情与设置、SF 系统字体与 SF Symbols 图标。
- macOS 26+ 使用 Apple 原生 `glassEffect`；14/15 使用系统 ultra-thin material，不把兼容材质称为原生 Liquid Glass。
- 中文 / English、明暗切换、跟随 Codex `appearanceTheme`（不可读时跟随系统）、玻璃清透度滑块。
- 可选窗口浮条：仅 Codex 位于前台时显示，按窗口外框定位；尚未实现 Windows 版的输入框工具栏定位。
- 原生登录项开关：由用户在设置中开启，不写入 shell 启动脚本。
- 默认自动跟随桌面本地通道报告的唯一本地会话；保留手动固定和主动选择“最近活动”模式。
- 多会话、远程订阅、连接中断时显示等待和明确来源，不自动用另一条最近会话代替目标。

不包括：多窗口的精确前台焦点识别、输入框工具栏定位、Codex 自定义强调色同步、修改上下文容量、服务端瞬时测速。

## 桌面会话自动跟随

数据 helper 连接当前数据目录下已有的 `ipc/ipc.sock`，完成观察客户端握手，读取 `thread-stream-following-changed`、断开和重置通知。当前已安装桌面代码在新客户端连接时，会向其重放已有订阅；观察器不发送会话启动、恢复或工具执行请求。

这是桌面内部广播的适配，不是把另一个 [App Server](https://learn.chatgpt.com/docs/app-server) 进程当成当前窗口。协议依据为已安装 `26.1027.3384.0` 桌面程序的跨平台实现，本机 Windows 同协议只读探测已收到成功握手和已有本地会话广播；Mac 与真实 Codex 对话的端到端验收仍待完成。

- 唯一的本地会话订阅稳定后自动选择；同一会话被多个客户端订阅会去重。
- 多个不同会话或混有远程订阅时，不根据事件先后猜前台焦点，改为提示固定目标。
- 自动模式断线、没有订阅或记录尚不可读时显示 `—`；“最近活动”只能由用户明确选择。
- 连接断开或客户端退出会清除旧订阅，重连重新握手，不继承旧会话。
- 只连接当前用户已有的本地端点并核对对端用户，不创建、删除或更改 Codex 的 socket。默认数据目录可兼容旧版临时 socket；自定义数据目录不使用全局旧端点，以免跨配置连接。
- 内部通道可能随 Codex 更新变化；不支持的版本会停止自动选择，固定会话与最近活动仍可用。

## 计数与速度保持原算法

`src/CodexTokenMetrics` 编译时直接链接 Windows 的以下源文件，不复制另一套算法：

- `TpsTracker.cs`：usage 解析、去重、模型时间窗、平均与峰值。
- `RolloutReader.cs`：增量 JSONL 读取与尾部恢复。
- `StateDatabase.cs`：只读 SQLite 查询。
- `CodexThemeSource.cs`：只读明暗偏好。

在独立 helper 进程中，仅为该程序集把 SQLite 库映射到 macOS 自带的 `/usr/lib/libsqlite3.dylib`。原 Windows 项目的文件与导入保持不变。

SwiftUI 接收 helper 的 JSON Lines 快照，不读取账号凭据，不经网络获取用量。输出速度仍然是日志口径；本次移植没有改为 Harness 的流式时间算法。

没有 usage 时显示 `—`，不补造零用量或 token/s。固定或自动识别的会话不可读时，不会静默显示另一条会话。用户主动选择的最近活动模式可能显示 CLI/后台会话，与前台对话不同。

## 构建

在 Mac 上准备 Xcode 26+（Swift 6.2+）与 .NET 10 SDK，然后在仓库根目录：

```bash
bash mac/build.sh
```

脚本根据本机架构编译共享 helper 与 SwiftUI 应用，运行合成数据测试与原生界面启动检查，生成：

```text
mac-dist/osx-arm64/CodexTokenGlass-osx-arm64.zip
mac-dist/osx-arm64/SHA256SUMS.txt
# Intel 上对应 osx-x64
```

GitHub 的 `macOS preview` 工作流分别使用 Apple Silicon 和 Intel 的 macOS runner。成功任务的 Artifacts 提供预览 ZIP 和 SHA256 清单；不要把 Windows 正式 Release 的 ZIP 用于 Mac。

## 安装

下载对应架构的预览 ZIP 和同一构建的 `SHA256SUMS.txt`，放在同一目录。在仓库根目录运行：

```bash
bash install-macos.sh --package /path/to/CodexTokenGlass-osx-arm64.zip
```

或在有构建环境的 Mac 上：

```bash
bash install-macos.sh --from-source
```

安装到 `~/Applications/Codex Token Glass.app`，不需要管理员权限。更新前从本应用菜单选择退出；安装器会保留旧应用备份，不结束 Codex。

预览包仅有 ad-hoc 签名，**没有 Apple Developer ID 签名和公证**。macOS 可能要求在“系统设置 → 隐私与安全性”中审核并允许打开。安装器不会移除 quarantine、关闭 Gatekeeper 或修改系统安全策略。普通分发仍需正式签名、公证和实机验收。

## 使用

1. 启动 Codex Token Glass，在系统菜单栏点击用量文字。
2. 默认读取 `CODEX_HOME` 或 `~/.codex`；Finder 启动的应用未必继承终端环境变量，可在设置中选择实际目录。
3. 默认选择“自动跟随桌面”，完成一次 Codex 响应后核对 token；有多个候选时通过选择器固定目标。需要原来的回退口径时，主动选择“最近活动（手动模式）”。
4. 在 macOS 系统设置中按需允许登录项。浮条不截屏、不录屏，不主动请求屏幕录制权限；若系统不返回窗口信息，菜单栏功能仍可使用。

本应用不会修改 Codex 的模型、API 地址、密钥、上下文窗口、压缩阈值或 5% 工具缓冲。

## 验证边界

- 数据层使用仓库合成 fixture 检查原算法一致性、旧版 usage、缺失数据、固定会话、增量重复、文件替换以及 JSON 桥接。
- 本地 Unix socket 测试覆盖真实连接、分段报文、订阅重放、EOF、无效帧、自动重连和取消阻塞读取；Mac runner 额外执行当前用户对端校验。所有测试使用临时合成端点，不连接开发者的真实会话。
- Mac CI 负责实际 Swift 编译、当前架构 helper 执行、应用签名校验、两页界面初始化和打包。
- CI 的启动检查不等于用户机器上 Codex 窗口贴附、Spaces、多屏、登录项审批及真实会话端到端验收。没有这类证据时不能声称已完成实机验证。

Apple 参考：[自定义 Liquid Glass](https://developer.apple.com/documentation/swiftui/applying-liquid-glass-to-custom-views)、[NSStatusItem](https://developer.apple.com/documentation/appkit/nsstatusitem)、[SMAppService](https://developer.apple.com/documentation/servicemanagement/smappservice)。

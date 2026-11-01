# macOS 移植预览

这是原生 macOS 客户端的第一版实现，处于预览阶段；Windows v0.1.0 正式版保持原样。

## 实现范围

- macOS 14+，Apple Silicon 与 Intel 分别打包。
- 原生 AppKit 菜单栏入口、SwiftUI 详情与设置、SF 系统字体与 SF Symbols 图标。
- macOS 26+ 使用 Apple 原生 `glassEffect`；14/15 使用系统 ultra-thin material，不把兼容材质称为原生 Liquid Glass。
- 中文 / English、明暗切换、跟随 Codex `appearanceTheme`（不可读时跟随系统）、玻璃清透度滑块。
- 可选窗口浮条：仅 Codex 位于前台时显示，按窗口外框定位；尚未实现 Windows 版的输入框工具栏定位。
- 原生登录项开关：由用户在设置中开启，不写入 shell 启动脚本。
- 会话选择器和“最近活动”模式。没有把最近活动会话冒充成已识别的前台对话，也没有接管 Codex 会话。

不包括：精确前台会话订阅、Codex 自定义强调色同步、修改上下文容量、服务端瞬时测速。这些不是通过改后缀或照搬 Windows 的窗口 API 就能完成的。

## 计数与速度保持原算法

`src/CodexTokenMetrics` 编译时直接链接 Windows 的以下源文件，不复制另一套算法：

- `TpsTracker.cs`：usage 解析、去重、模型时间窗、平均与峰值。
- `RolloutReader.cs`：增量 JSONL 读取与尾部恢复。
- `StateDatabase.cs`：只读 SQLite 查询。
- `CodexThemeSource.cs`：只读明暗偏好。

在独立 helper 进程中，仅为该程序集把 SQLite 库映射到 macOS 自带的 `/usr/lib/libsqlite3.dylib`。原 Windows 项目的文件与导入保持不变。

SwiftUI 接收 helper 的 JSON Lines 快照，不读取账号凭据，不经网络获取用量。输出速度仍然是日志口径；本次移植没有改为 Harness 的流式时间算法。

没有 usage 时显示 `—`，不补造零用量或 token/s。固定的会话不可读时，不会静默显示另一条会话。最近活动模式可能选择 CLI/后台会话，使用多会话时建议手动固定。

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
3. 完成一次 Codex 响应后核对 token；有多个会话时通过选择器固定目标会话。
4. 在 macOS 系统设置中按需允许登录项。浮条不截屏、不录屏，不主动请求屏幕录制权限；若系统不返回窗口信息，菜单栏功能仍可使用。

本应用不会修改 Codex 的模型、API 地址、密钥、上下文窗口、压缩阈值或 5% 工具缓冲。

## 验证边界

- 数据层使用仓库合成 fixture 检查原算法一致性、旧版 usage、缺失数据、固定会话、增量重复、文件替换以及 JSON 桥接。
- Mac CI 负责实际 Swift 编译、当前架构 helper 执行、应用签名校验、两页界面初始化和打包。
- CI 的启动检查不等于用户机器上 Codex 窗口贴附、Spaces、多屏、登录项审批及真实会话端到端验收。没有这类证据时不能声称已完成实机验证。

Apple 参考：[自定义 Liquid Glass](https://developer.apple.com/documentation/swiftui/applying-liquid-glass-to-custom-views)、[NSStatusItem](https://developer.apple.com/documentation/appkit/nsstatusitem)、[SMAppService](https://developer.apple.com/documentation/servicemanagement/smappservice)。

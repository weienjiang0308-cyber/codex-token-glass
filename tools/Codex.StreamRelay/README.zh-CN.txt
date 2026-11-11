Codex.StreamRelay：可回滚诊断原型（未接入正式桌面请求）

用途
在桌面实际启动的 Codex app-server stdin/stdout 之间转发 JSONL。
只修改 initialize 的原始完成通知屏蔽项，以及新 thread/start 的 experimentalRawEvents。
保留其他请求参数、权限询问和响应；桌面原来不接收的原始通知只写计时摘要，不额外交给桌面。
thread/resume 保持原样，不能承诺已有线程能开启原始事件。

记录内容
本机单调时钟、首个暴露给 app-server 客户端的文本/推理 delta、responseId、官方 outputTokens、完成时刻。
不记录正文、命令参数、认证请求参数或 usageMetadata。
没有首个 delta 的响应保持时长未知，尤其是隐藏推理/工具参数先生成的情况。
这是 app-server 事件观察，不是服务端内部解码时钟，也不是无损原始 SSE 抓取。

当前验证
tests/test_stream_relay.py 已通过 12 项模拟协议检查，没有发送模型请求。
尚未在真实桌面启动链路上验证；不能因此声称计时准确性提高了多少。
CODEX_CLI_PATH 覆盖会让桌面走自定义 CLI 分支，可能影响其打包运行时/沙箱集成，需要实机验证。

启用步骤（需要先确认重启时机）
1. 保存正在进行的工作，正常关闭 Codex。
2. 用 launch-observed.ps1 指定 DesktopExe、OriginalCodexExe、RelayExe 三个绝对路径启动。
3. 启动器只给本次进程设置 CODEX_CLI_PATH / CODEX_RAW_REAL_EXE，不修改全局环境或 config.toml。
4. 在一个新建、明确允许测试的对话中取得完整响应，按 responseId 对照侧车记录与 rollout usage。
5. 检查真实工具调用、审批、正常退出、断线重连；没有完成这些验证就不要接入正式速度显示。

回退
正常关闭本次 Codex，再从原快捷方式启动；恢复原启动链路。
侧车默认写入 %LOCALAPPDATA%/CodexTokenGlass/stream-observer/relay-<pid>.jsonl。
启动器发现 Codex 仍在运行会退出，不主动终止当前任务。

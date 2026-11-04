> glass.19：当前实时/平均定义、5% 验收口径和已知边界见 [审查报告](ROUTING-SPEED-REVIEW.zh-CN.txt)。下方是历史版本设计记录。

> glass.15：最新统计实现与边界以 [GLASS-15.txt](GLASS-15.txt) 为准。主速度为可计时响应的加权均速，短窗可见文本估算单列；缺失计时不补造分母。以下保留早期设计说明。

# 计数、缓存与速度：不要混为一谈

## 服务计数不是字符估计

Codex Token Glass 优先读取 Codex 持久化的 `token_usage_record` 与 `event_msg/token_count`：

- 累计输入：`input_tokens`
- 累计输出：`output_tokens`，包含服务记录的推理输出
- 已缓存输入：`cached_input_tokens`，它是输入的子集
- 总量：`input_tokens + output_tokens`
- 未缓存输入：`max(0, input_tokens - cached_input_tokens)`
- 命中率：`cached_input_tokens / input_tokens`

不要再次把 cached input 加到 input 上，否则会重复计算。累计值取最新记录，不把两个重复载体相加。数据库的 `tokens_used` 只作恢复/回退，数据库以只读方式打开。

统计的是当前会话可读取的本地使用记录，不是跨设备账号总用量或最终账单。缺字段、断开的会话路由、写入延迟或不兼容版本会影响显示，不能把“暂时读不到”当成“服务一定没有使用”。

## 速度是有定义的统计，不是物理上不可测

直接消费流式 API 的软件，可以从实际接收时间测量客户端观测吞吐。服务如果提供推理时长，也能计算服务口径吞吐。网络缓冲、分块、排队和隐藏推理会让这些口径互不相同；流式 chunk 也不一定等于一个 token。

本程序没有接管 Codex 的网络流，只能使用本地记录：

```text
平均速度 = 本轮输出 token / 本轮模型生成时间
```

生成时间取 `Reasoning` 与 `AgentMessage` 的时间窗并集，排除工具执行窗口。主显示值有平滑处理；缺乏完整本轮时间窗时不应伪造精确读数。

峰值是**已记录响应中最高的响应均速**，而不是服务端每一瞬间的解码峰值。低于 500ms 的时间窗不参与峰值；即便如此，短回复的速度仍可能有明显误差。任务总耗时包含等待与工具时间，不能拿来与模型生成时间混算。

## DeepSeek 的缓存量

[官方 Chat Completions 文档](https://api-docs.deepseek.com/api/create-chat-completion/)直接定义：

```text
prompt_tokens = prompt_cache_hit_tokens + prompt_cache_miss_tokens
hit_rate = prompt_cache_hit_tokens / prompt_tokens
```

这些是服务返回的使用计数，不是根据“回答比较快”猜出的缓存。DeepSeek Harness/其插件若读取这些响应字段，就能显示来自服务计数的命中率；但字段映射、第三方中转和不同版本仍可能出错，不能只凭软件名字保证某个面板正确。

## Claude 的缓存量

[Claude 官方缓存文档](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)定义了不同口径：

```text
total_input = input_tokens + cache_read_input_tokens + cache_creation_input_tokens
hit_rate = cache_read_input_tokens / total_input
```

这里的 `input_tokens` 是未从缓存读取、也未用于创建缓存的那部分输入，不能像 Codex/DeepSeek 一样直接把它当全部输入。Claude API/Claude Code/插件读到了真实 usage，并不保证下游百分比公式一定写对。

## 如何判断别的软件

1. 看数据是否来自服务响应的 `usage` 或未经改写的持久化 usage。
2. 核对分母：不同厂商的 input 字段是否包含缓存。
3. 核对是按请求、按轮次、按会话还是全账号统计。
4. 速度是否包含推理，是否排除等待/工具，时间取自服务还是客户端。
5. 经由第三方服务时，只能信任该服务提供的 usage，不能把它当作独立审计结果。

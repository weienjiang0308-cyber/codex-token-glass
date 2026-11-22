# 切模型与反代用量审计（2026-11-22，glass.60）

**结论：保留对话总账，单独建立「本次使用这个模型和线路」的分段账。切换只让新段从头统计，不清空对话历史，也不假定服务端缓存失效。** 当前状态栏是对话口径；真正的漏记、错归属和来源不明的问题，不能靠把几个计数器清零来修复。本次是审计，没有修改或安装应用。

## 本机证据

- 运行中的 Codex Token Glass：`1.0.0-rc3-glass.60`，安装文件 SHA-256 `A285CBCE87C244889FA933C37A9B42DD45BBCBFE82584AEAA96CC168469E1B49`。Magpie：`0.1.4676`；本机 Devin 插件：`@magpie-community/opencode-devin-auth@0.1.45`。
- 固定取样，避免活动日志改变结果：Codex `sessions/2026/11/22` 目录中，在 **00:00 ≤ UTC < 17:00** 产生逐响应记录的 4 个文件。按 `response_id` 去重，与 `D:\Magpie\data\usage.jsonl` 精确对照。不读凭据、不发送模型请求；报告不附原始对话、响应 ID 或账号标识。此范围不是所有设备、归档或账单。
- 861 条唯一 Codex 响应均能匹配 Magpie 成功记录，且输出数全部相等。将输入口径归一后，**855 条**的输入、缓存读、缓存写、输出四项全部相等（Vertex 74、Devin 781）；另外 **6 条**都在 Devin 路线，输入/缓存不一致。这证明两种观察值会分歧，不能证明哪一方代表最终账单，更不能相加或一律取大。
- 同窗口反向核查：这 4 个会话有 867 条 Magpie `agent=codex` 网关记录。多出的 6 条中，4 条为 499、1 条为 502；另一条 200 在 16:59:49 UTC 被网关记录，同 ID 于 **17:00:01** 才进入 Codex。按分钟或按天独立相减会制造一次假漏记。失败尝试也不等于已完成的模型响应。
- 这批会话的 `session_meta.model_provider` 写着 `openai`，却全部能匹配本机 Magpie。`openai` 是宿主登记的提供方名，不是物理直连证明。此样本验证了 Magpie，**没有**验证真实直连或任意其他反代。

### 三次真实切换

| 切换（同一个对话，依记录顺序） | 切后第一条 Codex 响应：缓存读 / 输入 | 含义 |
| --- | ---: | --- |
| Vertex Gemini → Devin 同名 Gemini | 0 / 284,989 | 模型名相同也可能换了线路。用已安装程序的 `--usage-record-json` 重放此切换的计数，显示 **95.52%**（13,728,256 / 14,371,851），是对话历史，不是新线路命中率。 |
| Devin Gemini → Devin Claude Opus fast | 516,888 / 520,087 | 换模型首条就可以报告命中。此条同时是源间冲突样本；这里只引用 Codex 口径，不据此断言跨模型共享缓存。 |
| Devin Claude Opus fast → 非 fast | 0 / 144,173 | 老模型的命中和速度不应延续到新区段；后台仍应保留前两段。 |

## 当前实现的问题（按影响排序）

| 优先级 | 位置（仓库相对路径与行号） | 实际行为及影响 |
| --- | --- | --- |
| P1 | `src/CodexStatusbar/TpsTracker.cs:765`、`:928`；`src/CodexStatusbar/Program.cs:1268`；`src/Codex.SpeedMetrics/ResponseSpeed.cs:383`、`ResponseSpeedDisplay.cs:21` | 前台忽略 `turn_context`，读整线程累计；IPC 也以线程总量较大者覆盖。均速、峰值和保留的上次速度按会话，不按模型段。用作对话数据合理，用来判断刚切换的模型则不成立。仅重置 UI 会在下次刷新被旧总量覆盖。 |
| P1 | `src/Codex.SpeedMetrics/UsageStatistics.cs:110`、`:181`、`:189` | 历史账本每个文件二选一：只要出现现代记录，整份文件的旧制记录不再参与，之后只产生旧制的区间也不收。甚至先设 `Modern=true` 再验证 ID。漏掉的是独立请求，不是应去重的镜像。前台也会在现代累计出现后忽略后续所有旧制累计（`TpsTracker.cs:997`）。 |
| P1 | `src/Codex.SpeedMetrics/UsageStatistics.cs:176`；`src/CodexStatusbar/GlassStatisticsForm.cs:148`；`src/Codex.SpeedMetrics/GatewayTimingStore.cs:288` | 无逐响应模型字段时，账本借用最近看到的模型，并非按该响应的 `turn_id` 回查。A 的晚到响应可算在 B；只有 `token_count` 的旧记录则固定记 `unknown`。模型统计以单个模型字符串分组；Magpie 目前只补计时，没带入实际线路和服务模型。 |
| P1 | `src/Codex.SpeedMetrics/UsageStatistics.cs:184`；`src/Codex.SpeedMetrics/ResponseSpeed.cs:329`；`src/CodexStatusbar/TpsTracker.cs:1044` | 历史解析把缺失或非整数的计数字段读成 0；只有 SQLite 总数时，前台还会把总数全填为输入、缓存填 0。两条路径都可能把“不知道”显示成“没有命中”，并污染合计。 |
| P2 | `src/Codex.SpeedMetrics/UsageStatistics.cs:111`；`src/Codex.SpeedMetrics/GatewayTimingStore.cs:92` | 历史按裸响应 ID 全局取第一条；网关按同一 ID 查单个条目。真正的 fork 副本应去重，但不同来源发生 ID 碰撞或同 ID 不同计数时，应保留冲突而非静默择一。本样本的 861 条未见碰撞，此项是兼容风险。 |
| P2 | `src/Codex.SpeedMetrics/ResponseSpeed.cs:249` | 已有隐藏推理和缓冲突发过滤，应保留。但网关解码窗取 `ms − 有效首 token 时间`，未使用已读取的 `flow_ms` 作交叉检查。样本中 36 条 `flow_ms` 在 1–79 ms、而按当前时长规则不属于突发。例：252 输出、总时长 5,286 ms、有效首 token 4,668 ms、流量窗 1 ms。不能据此断定模型以 252,000 tok/s 解码，也不能证明这 36 条都误报；应区分客户端观测、可用样本和未知。 |

**可复现的漏记与错归属：** 用项目的 `UsageLedger.Refresh` 依次喂入 A 的旧制累计（100 输入、10 输出）、B 的 `turn_context`、A 的迟到现代响应（200 输入、20 输出、缓存字段缺失）、B 的现代响应（300 输入、30 输出、200 缓存）。三条是独立请求，预期 660 tokens；实得 **550 tokens、2 条、两条均属 B、缓存率 40%、解析错误 0**。这验证了缺陷能触发，不等于真实历史恰好少了 110 tokens。原有测速测试 **1,033 项通过**，已安装版自测 **528 项、0 失败**；现有测试通过并未覆盖这些边界。

## Magpie 哪些做法值得借鉴

- [同版本 `Record` 字段定义](https://github.com/yetone/magpie/blob/v0.1.4676/internal/usage/usage.go#L75-L96)：`in` **不含**缓存读和写，完整输入 = `in + cache_read + cache_write`。`cache_unknown` 表示供应商没有计量，这类输入不能放进命中率分母。Codex / [OpenAI Responses](https://developers.openai.com/api/docs/guides/prompt-caching) 的 `input_tokens` **已含**缓存部分，不再加读写。Devin 插件 `index.mjs:1934` 也明确合成完整输入；在该转换分支，上游输出数缺失或为零时还可能用字符长度估算输出。因此两边数值相等也不是两套独立的服务端计量。
- [Codex 对账](https://github.com/yetone/magpie/blob/v0.1.4676/internal/usage/codex_identity.go#L45-L69)：在精确 ID 匹配路径，仅在两边各有唯一候选且来源合适时配对，排除被拒请求和插件自行上报。它还[分开跟踪累计检查点](https://github.com/yetone/magpie/blob/v0.1.4676/internal/sessions/codex_usage.go#L8-L16)，不把压缩和旧快照当成同一种累计。
- [模型速度排行](https://github.com/yetone/magpie/blob/v0.1.4676/internal/usage/speed_rank.go#L19-L34)取合格响应各自速率的**中位数**，回答“典型一条有多快”。Glass 目前用 `Σ可计时输出 / Σ对应时长`，回答“这批工作整体有多快”。两者都是有意义的指标，不应更换公式却沿用相同名称，也不应将端到端速率冒充生成速率。

## 兼容方案：一条响应只入账一次，提供三个视角

1. **请求账本**：保留会话、轮次、响应 ID、请求模型、实际模型（若返回）、线路、时间、计数字段及其来源。字段允许「已知零 / 未提供 / 冲突」。现代逐响应记录作主；老日志按同一累计区间差分，只补未覆盖区间；切换、压缩、回退、恢复和历史复制不互相借用基线。只有总量、没有可信明细时，就只显示总量。不能确定请求个数时标为未知，不把一个批量差值算成一次请求。
2. **显示三种口径，不改布局**：状态栏默认「当前连续模型段」；统计页保留「整个对话」及「按线路和模型汇总」。A→B→A 是三段，第三段不接第一段；历史 A 汇总可以合并两段。切后第一条有效记录前，段内已完成请求数为 0，速度与命中率显示 `—`；不能显示上一模型的保留值。若仅改了选择、没有发请求，记为待开始，不造一条请求；旧响应晚到归原段，不切回当前显示。
3. **三类线路，同一套上层算法**：Codex 是客户端，不是线路。原生直连用 Codex 的标准化逐响应数；只有存在可信的当时路由证据才标「直连」。Magpie 唯一匹配时补上游、实际模型和有效时钟；与宿主冲突时，前台用 Codex 口径，诊断保留两组值，绝不相加。其他反代若输出标准 Responses 用量，照常统计宿主可见数，不要求安装 Magpie；有受支持的网关格式与可信关联时才补字段。OpenAI 兼容的 Chat Completions 看 `prompt_tokens_details.cached_tokens`；Anthropic 原生输入、读取、创建是分量；未知协议保留未知。模型字符串、当前配置、端口、网关进程存在或时间接近都不能单独证明某个历史响应的线路。
4. **不确定性可见**：命中率 = `Σ已知缓存读 / Σ这些请求的完整输入`，不平均单次百分比；并给出覆盖数。推理已含在输出时不再相加。测速只把同段、同时有有效输出与时钟的响应放入分子分母。只有网关记录时先留待匹配，明确绑定会话且经过延迟重对账后才可能补入；后台保活、测试探针、失败重试不计入用户成功回复。账号日汇总单列，不能拿来填某个模型或反代的差额。

**落地顺序：** 先把三次真实切换、同模型换线路、A→B→A、混合载体、ID 碰撞、旧响应迟到、缺字段、双源分歧、跨时间窗到达、重启/压缩回放写成脱敏回归用例；再让新旧算法并行只读核对。验收看四件事：每个已确认响应恰好记一次；各段加未归属等于同口径的对话总账；当前段不显示旧速度；无证据的缓存绝不显示为 0%。最后才替换状态栏的数据源，保留现有 UI 和历史总账。

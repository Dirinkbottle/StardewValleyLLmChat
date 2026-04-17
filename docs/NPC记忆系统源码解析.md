# NPC 记忆系统源码解析

这份文档只讨论当前项目里的 NPC 记忆系统本身，不展开 LLM 路线控制、表情执行和 Farm 桥接。

目标是把现在这套记忆链条拆清楚：

- 哪些数据会被写入
- 数据分别落在哪些文件
- LLM 在请求前会自动拿到哪些记忆
- `memory_update` 这层结构化事实到底解决了什么问题
- 当前实现还有哪些局限

---

## 1. 当前记忆系统的分层

当前实现不是单一记忆池，而是三层并存：

1. **事件记忆**
   - 自由文本事件流
   - 例如玩家输入、NPC 回复、收礼、日终总结
   - 主要由 `Services/NpcLlmMemoryStore.cs` 管理
2. **向量检索层**
   - 给事件记忆生成 embedding
   - 检索时按余弦相似度排序
   - 仍由 `Services/NpcLlmMemoryStore.cs` 管理
3. **结构化事实层**
   - 高优先级、可覆盖、可删除的确定性知识
   - 例如长期喜好、今天临时状态、玩家纠正后的结论
   - 由 `Services/Memory/NpcLlmFactStore.cs` 管理

对应模型主要在：

- `Models/NpcLlmModels.cs`
  - `MemoryRecord`
  - `DayMemoryRecord`
- `Models/NpcMemoryModels.cs`
  - `MemoryFactRecord`
  - `MemoryFactUpdate`
  - `NpcMemoryFactScopes`

这三层的设计意图很明确：

- 事件记忆负责“留痕”
- 向量检索负责“从旧痕迹里找相关上下文”
- 结构化事实负责“把模糊上下文收敛成稳定结论”

---

## 2. 落盘结构

当前每个 NPC 的记忆目录在：

- `NpcMemories/<SaveFolderName>/<NpcName>/`

具体文件如下。

### 2.1 `profile.json`

由 `NpcLlmMemoryStore.GetOrCreateProfile(...)` 生成。

它记录的是 NPC 的静态档案和少量当前态，例如：

- `npc_name`
- `display_name`
- `default_map`
- `default_tile`
- `default_facing`
- `birthday`
- `gender`
- `is_married`
- `love_interest`
- `current_location`

这份文件每次构造请求快照时都会刷新一次，本质上更像“静态角色卡 + 当前基础资料缓存”。

### 2.2 `events.jsonl`

由 `NpcLlmMemoryStore.AppendEventRecord(...)` 追加写入。

这是事件记忆主文件，每行一条 `MemoryRecord`，字段包括：

- `Id`
- `NpcName`
- `EventType`
- `Text`
- `Metadata`
- `Timestamp`

只要项目里发生了被记录的互动，就会往这里追加，例如：

- `player_prompt`
- `npc_reply`
- `gift_received`
- `day_summary`

### 2.3 `vectors.json`

由 `NpcLlmMemoryStore.TryEmbedRecordAsync(...)` 维护。

它是：

- `record.Id -> float[] embedding`

的映射表。

也就是说，事件文本和向量是分开存的：

- 原文在 `events.jsonl`
- 向量在 `vectors.json`

检索时先读 `events.jsonl`，再按 `Id` 去 `vectors.json` 对齐。

### 2.4 `facts.json`

由 `NpcLlmFactStore` 维护。

这是结构化事实层，存的是 `MemoryFactRecord` 列表，每条事实都有明确字段：

- `Key`
- `Scope`
- `Category`
- `Summary`
- `Value`
- `SourceEventType`
- `GameDate`
- `UpdatedAt`
- `Metadata`

它不追求“完整对话上下文”，而追求：

- 明确
- 可覆盖
- 可删除
- 能表达当天有效或长期有效

### 2.5 `days.jsonl`

由 `NpcLlmMemoryStore.AppendDayRecord(...)` 写入。

它记录的是日终层面的聚合摘要，例如：

- 哪一天
- 当前 schedule key 是什么
- 行程摘要文本是什么

它更像日结日志，不是当前自动检索主入口。

### 2.6 `debug.jsonl`

由 `NpcLlmMemoryStore.AppendDebugRecord(...)` 写入。

它主要服务调试，不直接作为用户级记忆来源。这里保存的是每轮 agent 请求的摘要、命中记忆、工具调用和 patch 落地等调试信息。

---

## 3. 事件记忆是怎么进入系统的

### 3.1 玩家输入

在 `NpcAgentManager.SubmitPlayerPrompt(...)` 里：

1. 玩家输入先写成 `player_prompt`
2. 立即异步请求 embedding
3. 再把这个事件入队给 agent

这意味着：

- 这条输入几乎总会进入后续记忆检索池
- 但 embedding 写入是异步的，不保证同一瞬间已经落到 `vectors.json`

### 3.2 NPC 回复

在 `NpcAgentManager.ShowPendingSpeechIfPossible(...)` 里：

1. 真正弹出对话后
2. 才把这条对白写成 `npc_reply`
3. 然后异步补向量

所以“工具里已经执行 `npc_say_to_player`”和“记忆里出现 `npc_reply`”之间并不是同一时刻，中间要等对白真正被展示。

### 3.3 礼物事件

在 `NpcAgentManager.NotifyGiftReceived(...)` 里：

- 会写 `gift_received`
- 文本会包含送礼人和礼物名

### 3.4 日终摘要

在 `NpcAgentManager.OnDayEnding()` 的流程里：

- 当前有效 rule 会被转成摘要文本
- 一份写到 `days.jsonl`
- 另一份作为 `day_summary` 事件写到 `events.jsonl`
- 然后再为 `day_summary` 异步生成 embedding

这说明当前系统里，“一天的行程总结”既有结构化日结文件，也有可语义检索的事件记录。

---

## 4. 自动检索是怎么做的

### 4.1 请求开始前会先自动检索一轮

在 `NpcAgentManager.RunAgentRequestAsync(...)` 里，每次真正发起 LLM 请求前，都会先调用：

- `memoryStore.SearchMemoriesAsync(...)`

自动检索查询词来自四块拼接：

- `agentEvent.PlayerAction`
- `agentEvent.DialogueExcerpt`
- `agentEvent.GiftItem`
- `agentEvent.CurrentScheduleSummary`

这意味着当前自动检索并不只看玩家说的话，它还会把：

- 当前事件
- 当前 schedule 摘要

一起塞进检索 query。

优点是：

- 事件和日程能一起参与召回

缺点是：

- 如果 schedule 摘要文本权重太强，可能把检索拉向“今天行程相关的旧记忆”，而不是纯对话语义

### 4.2 向量检索只针对事件记忆，不针对 facts

`SearchMemoriesAsync(...)` 的流程是：

1. 为 query 生成 embedding
2. 读 `vectors.json`
3. 读 `events.jsonl`
4. 只保留有向量的事件
5. 算余弦相似度
6. 取 top K

这里的重点是：

- facts 不参与这套向量相似度排序
- facts 走的是另一条高优先级注入链

### 4.3 事件类型过滤不是简单白名单，而是带扩展映射

`NpcLlmMemoryStore.ExpandEventTypes(...)` 里定义了几个语义组：

- `dialogue` / `conversation`
  - 扩展成 `player_prompt` + `npc_reply`
- `player_interaction`
  - 扩展成 `player_prompt` + `gift_received`
- `gift`
  - 扩展成 `gift_received`

这能让工具层的筛选条件更接近语义，而不是逼模型死记底层事件名。

---

## 5. `memory_update` 为什么是必要的

用户之前举过一个很典型的例子：

- 星期一说“我讨厌酸菜鱼”
- 星期二说“今天我想吃酸菜鱼”

如果只有事件记忆 + 向量检索，那么之后检索到什么，取决于：

- 当前 query 如何写
- embedding 对“讨厌”和“想吃”的相似度怎么分布
- 两条事件谁更靠近当前语义

这时模型确实可能混淆。

也正因为如此，当前项目加了结构化事实层和 `memory_update` 工具。

它的设计目标不是替代事件记忆，而是把“已经被明确确认的结论”从事件流里提纯出来。

例如：

- 长期偏好：`scope=persistent`
- 今天临时状态：`scope=today`
- 玩家纠正旧认知：继续用相同 `key` 覆盖

这样就能表达：

- `food.preference.sour_fish`
  - `persistent`: 讨厌酸菜鱼
- `food.today.craving`
  - `today`: 今天想吃酸菜鱼

当前 system prompt 也明确写了：

- `today` 和 `persistent` 冲突时，`today` 优先

所以这层并不是“可有可无的辅助工具”，而是当前系统避免长期记忆和当天语境混淆的主解法之一。

---

## 6. 结构化事实层的具体规则

### 6.1 作用域只有两种

当前 `NpcMemoryFactScopes` 只有：

- `persistent`
- `today`

含义分别是：

- `persistent`
  - 长期有效
- `today`
  - 仅当天有效

### 6.2 `today` 事实会自动过期

`NpcLlmFactStore.PruneExpiredFacts(...)` 会在读取和写入前清理失效事实。

规则很简单：

- `persistent` 永远保留
- `today` 只有 `fact.GameDate == 当前游戏日期` 时才保留

这解决了“昨天的临时状态污染今天”的问题。

### 6.3 覆盖规则是 `key + scope`

`UpsertFact(...)` 的定位键是：

- `Key`
- `Scope`

也就是说：

- 同一个 `key` 的 `persistent` 和 `today` 可以同时存在
- 同 scope 下再次写同 key，会直接覆盖旧值

这非常适合表达：

- 长期认知
- 当日覆盖态

### 6.4 facts 的排序是“today 优先，再按类别和 key”

`GetActiveFacts(...)` 返回时会排序：

1. `today`
2. `persistent`
3. `Category`
4. `Key`

所以提示词里展示 facts 时，短期事实会天然排在长期事实前面。

---

## 7. 记忆是怎么进入提示词的

在 `NpcAgentManager.BuildSystemPrompt(...)` 里，最终喂给模型的记忆上下文有三部分：

1. **NPC 静态档案**
   - 来自 `profile.json`
2. **结构化事实记忆**
   - 来自 `factStore.GetActiveFacts(...)`
3. **自动检索记忆**
   - 来自 `memoryStore.SearchMemoriesAsync(...)`

注意优先级设计：

- facts 是直接放进 system prompt 的明确条目
- 自动检索记忆是“相关历史片段”
- 它们不是同一个层级

这意味着当前项目已经隐含了一条策略：

- 事件记忆用于提供语境
- 结构化事实用于提供结论

---

## 8. 工具层与记忆层的关系

当前提供给 LLM 的记忆相关工具主要有三个：

- `get_recent_memories`
- `search_memories`
- `memory_update`

它们的职责区分如下。

### 8.1 `get_recent_memories`

适合：

- 看最近发生了什么
- 不依赖 embedding
- 快速拿近几条对话或互动

### 8.2 `search_memories`

适合：

- 做语义召回
- 从长历史里找相近事件
- 回答“你之前说过什么/记得什么”之类的问题

### 8.3 `memory_update`

适合：

- 把长期偏好提炼成稳定结论
- 记录今天临时状态
- 覆盖或删除旧认知

如果模型没有调用 `memory_update`，那么许多“明确该被更新的结论”仍会停留在事件流里，之后只能靠 embedding 检索去猜。

---

## 9. 当前实现的优势

### 9.1 比纯 RAG 稳

如果只有 `events.jsonl + vectors.json`：

- 所有知识都在自由文本里
- 是否命中全看 embedding 和 query

加入 facts 后，系统至少多了一层：

- 可覆盖
- 可删除
- 有作用域
- 有日期边界

这使得“被玩家明确纠正的事实”终于有了稳定存储位。

### 9.2 比全结构化记忆灵活

如果只有 facts：

- 很难保存自然互动历史
- 很难回答“你刚刚说过什么”“上次发生了什么”

保留事件记忆后：

- 叙事类上下文仍然存在
- 对话回溯仍能靠语义召回完成

### 9.3 成本实现简单

当前方案不依赖数据库，也不依赖 ANN 索引：

- `jsonl`
- `json`
- 本地文件锁
- 线性扫描 + 余弦相似度

对当前规模的 NPC 记忆量来说，这种实现足够直接，也容易调试。

---

## 10. 当前实现的局限

### 10.1 事件记忆不会自动“合并结论”

目前事件层只是不断追加：

- 不会自动摘要
- 不会自动去重
- 不会自动把冲突结论折叠成一个统一状态

因此如果模型不主动调用 `memory_update`，旧事件和新事件会一直共存。

### 10.2 结构化事实仍依赖模型是否正确使用 key

虽然 facts 层支持覆盖，但前提是模型要继续使用同一个 key。

如果模型今天写：

- `food.preference`

明天又写：

- `food_like`

那它们仍会变成两条并列事实。

也就是说，当前系统已经有“覆盖机制”，但还没有“事实 key 规范治理”。

### 10.3 向量写入是异步的，存在短时间延迟

事件先写 `events.jsonl`，再异步写 `vectors.json`。

所以刚产生的一条事件：

- 立刻能出现在 recent memories
- 但不保证立刻能被语义检索命中

### 10.4 自动检索 query 里混入了 schedule 摘要

这有助于让“当前日程相关历史”也被召回，但副作用是：

- schedule 文本过长时
- 对话语义可能被 schedule 语义稀释

后续如果要继续优化，可以考虑把：

- 对话语义
- 日程语义

拆成两路检索，再做本地融合。

### 10.5 facts 目前不参与向量检索

这本身是设计选择，不一定是 bug。

但它的含义是：

- facts 只在 prompt 注入时直接给模型看
- 不会像 event memories 那样参与相似度排名

如果未来要做更复杂的长期人格记忆，也许可以考虑：

- facts 单独建 embedding
- 或者 facts 直接走规则优先级，不进向量层

当前版本选择的是第二种。

---

## 11. 对后续优化的建议

### 11.1 保留当前双层结构，不要退回纯事件检索

当前这套：

- 事件记忆
- 结构化事实

已经是必要分层。继续优化时，建议补强它，而不是删掉 facts 回退到纯 embedding 检索。

### 11.2 优先做事实 key 规范

比起再增加更多 memory tool，更重要的是：

- 给偏好、关系、临时状态建立更稳定的 key 命名约定

否则 facts 层会逐渐被“同义不同 key”稀释。

### 11.3 可以考虑补一层“记忆归并/重写”工具，但不要替代 `memory_update`

如果后面要继续增强，可以新增类似：

- `memory_reconcile`
- `memory_merge`

之类的高层工具，用于：

- 合并冲突事件
- 生成规范 key
- 把近期多条相似事件折叠成一条事实

但这类工具应该是：

- 建立在当前 facts 层之上

而不是把现在的 `memory_update` 废掉。

### 11.4 日后如果记忆量继续变大，再考虑索引和摘要

现在文件式实现够用，但当每个 NPC 的 `events.jsonl` 积累很多天之后，可以考虑：

- 定期摘要旧事件
- 热冷分层
- 更快的向量索引

这些属于扩展优化，不是当前必须项。

---

## 12. 一句话结论

当前项目里的记忆系统，本质上已经不是“只有 embedding 检索”的简易 RAG，而是：

- **事件流负责保留历史**
- **向量层负责召回相关片段**
- **结构化事实负责稳定结论和短长期冲突处理**

这也是为什么它现在已经能比纯事件检索更好地处理：

- 玩家纠正 NPC 认知
- 当天临时状态覆盖长期偏好
- 对话上下文与长期设定并存

真正还需要继续加强的，不是“再加一个随机记忆桶”，而是：

- 事实 key 的规范化
- 事件到事实的归并策略
- 自动检索 query 的拆路与重排

---

## 13. 2026-04 补充：`day_idle` 事实固化链路

这次新增了一条和普通对话、普通周期完全不同的记忆维护链：

- `day_idle`

它的定位非常明确：

- 不是现场决策
- 不是玩家对白
- 不是 schedule 调整
- 只负责把“今天已经发生过的内容”整理成 facts

### 13.1 `day_idle` 的触发源不是普通定时器，而是“暂停导致的周期跳过”

现在 `periodic_tick` 是否允许发起，不再靠本地近似判断，而是直接看：

- `Game1.shouldTimePass()`

当周期已经到期，但这时原版认为时间不该流逝，就会：

1. 跳过这轮 `periodic_tick`
2. 检查当天是否已经触发过全局 `day_idle`
3. 如果没有，就给所有启用且 provider 可用的 NPC 各排一条 `day_idle`

所以 `day_idle` 更像：

- “暂停期间顺手做一次后台维护”

而不是：

- “又来一轮普通 agent 行为”

### 13.2 `day_idle` 不走自动语义检索，而是直接读取当天事件流

普通请求里，manager 默认会做一次：

- `SearchMemoriesAsync(...)`

用语义检索从全历史事件里找 topK。

但 `day_idle` 不是这个逻辑。

现在它会直接调用：

- `NpcLlmMemoryStore.GetMemoriesForGameDate(...)`

从 `events.jsonl` 里筛出：

- `metadata.game_date == 当天`

然后按时间顺序提供“今日事件视图”。

这条链的意义非常大，因为它回答的是：

- “今天到底发生了什么”

而不是：

- “和今天大概相似的历史片段是什么”

这正适合做 facts 固化。

### 13.3 `day_idle` prompt 里有三层核心记忆输入

`day_idle` 专用 prompt 现在会同时拿到：

1. 今日事件视图
2. 当前 active facts
3. NPC 人格档案

其中：

- 今日事件视图告诉模型“今天真实发生了什么”
- active facts 告诉模型“当前系统已经认定了哪些结论”
- 人格档案则影响它如何判断哪些内容值得长期沉淀

这里已经不再依赖“自动 topK 检索猜今天发生过什么”，而是直接把当天事件流摊开给模型看。

### 13.4 `day_idle` 只允许 facts 维护，不允许世界副作用

虽然 `day_idle` 也是走同一套 tool loop，但运行上下文会强制关闭：

- `AllowSpeech`
- `AllowBehaviorControl`
- `AllowScheduleControl`

所以它本地只允许稳定使用的核心工具其实就是：

- `memory_update`

模型如果误调：

- `npc_say_to_player`
- 动作请求
- schedule 修改

本地会直接拦掉，不会真的对白、做动作或改行程。

### 13.5 事实固化的规则现在更明确了

`day_idle` 这轮不是自由发挥，它遵守固定规则：

- 长期偏好、稳定关系、持续习惯 -> `persistent`
- 仅当天有效的情绪、状态、临时意图 -> `today`
- 如果今天明确纠正了旧认知，优先覆盖已有同 key facts
- 不做记忆压缩
- 不删除 `events.jsonl`

所以这次新增的不是“第二套事件系统”，而是：

- 让 facts 层终于有了一条稳定的日维护入口

### 13.6 人格文件不等于 facts，但会影响 facts 的沉淀判断

这次还新增了：

- `Personality/<NpcName>/<NpcName>.md`

人格文件不是记忆库，也不会写进 `facts.json` 里替代 facts。

它的职责更像：

- 高权重的判断背景

facts 记录的是：

- 玩家今天说了什么
- NPC 当前认定了什么
- 某条偏好/关系是否长期成立

人格文件提供的是：

- 这个 NPC 平时怎么说话
- 怎么做事
- 喜欢什么表达方式
- 如何思考和取舍

两者现在分层明确：

- 人格影响“怎么看待信息”
- facts 负责“最终沉淀成什么结论”

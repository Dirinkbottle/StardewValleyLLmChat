# StardewMod

一个面向《Stardew Valley》与 SMAPI 的综合模组，当前主要提供两块能力：

- `NPC 路线编辑`
  在游戏内查看、编辑和保存 NPC 的日程与路径规则。
- `NPC LLM`
  为村民启用 LLM Agent，配置 provider、激活时间窗、行为权限、对话能力，并保留本地记忆与调试数据。

## 功能概览

### NPC 路线编辑

- 在模组菜单中选择可编辑 NPC。
- 查看已有 schedule / override。
- 编辑路径规则并保存到存档。
- 在世界层查看路线采样和预览覆盖层。

### NPC LLM

- 给每个 villager 单独启用或关闭 LLM。
- 为每个 NPC 选择聊天 provider。
- 配置每周时间窗和轮询周期。
- 控制是否允许移动行为、玩家对话、未来日程修改。
- 记录 NPC 记忆、事件、调试摘要和向量缓存。

## 安装

### 运行前置

- 《Stardew Valley》
- SMAPI

### 安装步骤

1. 把模组目录解压或复制到 `Stardew Valley/Mods/StardewMod/`。
2. 通过 SMAPI 启动游戏。
3. 首次启动后，如果模组目录里没有 `mod.toml`，模组会自动生成默认模板。
4. 按需编辑 `mod.toml`，再重新启动游戏。

安装后的目录通常类似：

```text
Stardew Valley/
└── Mods/
    └── StardewMod/
        ├── manifest.json
        ├── StardewMod.dll
        ├── mod.toml
        ├── Personality/
        └── image/
```

## 快速开始

### 打开菜单

- 进入任意存档后，按 `L` 打开模组主菜单。
- `L` 也是默认关闭菜单的快捷键。
- 这个快捷键和菜单倍率保存在 `config.json` 中。

### 主菜单入口

- `NPC 路线编辑`
  进入角色列表，编辑日程和路径。
- `NPC LLM`
  进入 villager 列表，配置每个 NPC 的 LLM 设置。

### 首次启用 NPC LLM

1. 先确认 `mod.toml` 里至少配置了一个可用聊天 provider。
2. 进入 `NPC LLM` 列表。
3. 选择一个 NPC。
4. 打开 `LLM 开关`。
5. 选择 `Provider`。
6. 视需要调整周期、时间窗、行为控制、对话控制、日程控制。

## 配置说明

这个项目的配置分三层，职责不要混：

- `config.json`
  只管本地 UI 和快捷键，例如 `OpenMenuKey`、`MenuScaleIndex`。
- `mod.toml`
  只管 LLM 全局文本配置，例如 provider、URL、Token、模型、超时、感知范围。
- 存档内 NPC 设置
  只管单个 NPC 是否启用、使用哪个 provider、每周时间窗、行为权限等。

### `mod.toml` 主要结构

```toml
[router]
[embeddings]
[debug]
[perception]
[broadcast]
[providers.<name>]
```

关键规则：

- 至少要有一个可用于聊天的 `providers.<name>`。
- `embeddings.provider_name` 必须指向一个真实存在的 provider 名。
- `tool_calling_required` 必须为 `true`。
- `kind` 目前只支持 `openai` 和 `anthropic`。
- Anthropic-compatible provider 当前不能作为 embedding provider 使用。

当前仓库根目录已经附带一个示例 [`mod.toml`](./mod.toml)，可以直接在此基础上改。

### `embeddings` 的作用

`[embeddings]` 不是拿来让 NPC 直接“说话”的模型配置，而是给记忆系统做语义检索用的。

它的职责是：

- 把 `events.jsonl` 里的事件文本转成向量，写入 `vectors.json`。
- 当 NPC 收到新触发事件时，把本轮 query 也转成向量。
- 用向量相似度从旧事件里找出最相关的记忆，再把这些记忆片段塞回本轮 prompt。

它不负责：

- 直接生成对白。
- 直接决定 schedule 修改。
- 直接决定动作或广播。

简化理解：

- `embedding model` 负责“找回忆”。
- `chat / llm model` 负责“做决定”。

如果你把 `embeddings.provider_name` 留空，模组仍然可以运行，但会失去自动语义记忆检索能力；此时更多依赖近期事件和结构化 facts，而不是从历史事件里按语义召回相关上下文。

### LLM 聊天模型的作用

`[providers.<name>]` 里可用于聊天的模型，才是真正参与 NPC 决策的模型。

它的职责是：

- 接收当前事件、当前游戏状态、当前 working schedule、人格档案、结构化 facts、自动检索到的相关记忆。
- 在本地工具约束下做多轮 tool loop 推理。
- 决定是否要：
  - 回复玩家
  - 对附近 NPC 说话
  - 写入或更新结构化记忆
  - 修改未来 schedule
  - 提交动作请求
  - 忽略某条广播

它同样不直接操作游戏对象。  
真正的游戏落地始终由本地 `NpcAgentManager + NpcLlmToolService` 执行，模型只是“提出工具调用和决策建议”，最后有没有权限执行、何时执行、以什么顺序执行，都由本地代码控制。

## 详细运作机制

这个模组的 NPC LLM 不是“玩家问一句，模型回一句”的单层聊天，而是一套本地 runtime 驱动的事件系统。比较准确的理解方式是：

```text
游戏事件/玩家输入/礼物/邻居感知
-> 本地事件入队
-> 选取一个可执行事件
-> 构造 prompt + 记忆 + facts + schedule + runtime snapshot
-> 调用聊天模型跑 tool loop
-> 本地工具层把结果转成对白 / 动作 / patch / 记忆 / 广播
-> 主线程按安全时机逐步落地
```

下面按四块解释。

### 1. 多级事件队列

每个 NPC 都不是直接“同步执行一个模型调用”，而是维护一套本地工作队列。

当前核心队列分层是：

- `pendingEvents`
  待处理事件队列，例如 `day_started`、`window_entered`、`periodic_tick`、`player_prompt`、`gift_received`、`npc_sync_encounter`、`npc_broadcast_observation`。
- `speechDisplayQueue`
  待向玩家弹出的对白队列。
- `immediateFeedbackQueue`
  tool loop 过程中允许提前投递的即时反馈事件。
- `realtimeActionQueue`
  可尽快落地的实时动作，例如表情、短暂停顿、面对玩家、NPC 对 NPC 说话。
- `deferredActionQueue`
  需要在本轮请求结束后再安全执行的动作，例如移动、部分路线动画、延迟行为。

这几层队列之外，还会有：

- `ActiveRequest`
  当前正在执行的 LLM 请求。
- `PendingRuntimeReset`
  等待安全时机再应用的运行态清理。

这套设计解决的是实时游戏里的时序问题：

- 事件可以先入队，不强迫立即打断主线程。
- tool loop 里产生的动作不会直接跨线程碰游戏对象。
- “对白”“实时动作”“延迟动作”可以分开发车，避免互相抢时机。

#### 事件如何入队和去重

事件不会无脑追加，系统会做本地调度：

- `day_idle`、`day_started` 这类后台系统事件不会重复堆积。
- 后台观察类事件会替换旧的同类观察事件，避免过时观察淤积。
- 新的 `player_prompt` 会替换旧的待处理玩家输入，保证优先响应最新一句。
- 更高优先级事件可以中断正在执行的请求，并把被打断的旧事件重新插回队头，等待后续重放。

所以这不是单队列 FIFO，而是“带去重、替换、抢占和回放”的本地事件调度器。

### 2. 内置小型 Agent

这里的“agent”不是纯远端模型，而是“远端聊天模型 + 本地工具层 + 本地执行器”的组合。

可以把它理解成一个受控的小型 agent runtime：

1. 本地先根据触发事件构造上下文。
2. 自动采样当前地图、时间、天气、附近 NPC、当前 working schedule、运行时状态。
3. 读取人格档案、结构化 facts、近期事件和语义检索记忆。
4. 把这些内容发给聊天模型。
5. 模型通过 provider 原生 tool calling 调用本地工具。
6. 本地工具层决定哪些工具允许、哪些参数合法、哪些动作应该立即执行，哪些要延后。

这个 agent 的关键特点是“本地强约束”：

- 模型不能直接操作底层 route 点栈。
- 模型不能直接碰 `Game1` 或 NPC 运行时对象。
- 模型只能通过本地暴露的工具接口工作。
- 不同事件类型使用不同的工具权限配置。

例如：

- `day_idle`
  更偏维护轮，只允许整理记忆 facts，不允许对白、动作和 schedule 修改。
- `ambient`
  背景观察轮，只允许轻量查询和记忆整理。
- `broadcast`
  可以响应广播、选择忽略广播、做有限对白或动作。
- `npc_sync`
  允许和附近 NPC 对话，但不是完全开放的全功能轮。
- `full`
  面向玩家直接请求时，权限最完整。

#### tool loop 怎么工作

一次请求不是固定单轮问答，而是一个本地可控的 tool loop：

1. 先给模型一份当前轮的 system prompt 和 user prompt。
2. 如果模型直接结束，没有工具调用，就输出最后结果。
3. 如果模型调用工具，本地执行工具并把结果回填到历史里。
4. 然后进入下一轮。
5. 每进新一轮前，本地都会重新采样 live state，而不是盲信上一轮推断。

这一步很重要，因为游戏世界是流动的：

- NPC 可能已经走到别的位置。
- 附近 NPC 可能已经离开感知半径。
- 当前安全改写时间 `SafeMutationTime` 可能已经变化。
- 玩家可能已经不在同图或不再可见。

所以本地 runtime 的职责不是“相信模型连续推理”，而是“每一轮都拿当前游戏真实状态纠偏”。

### 3. NPC-NPC-Player 交互链

这套系统里其实存在三种不同的对话关系：

- `Player -> NPC`
- `NPC -> Player`
- `NPC -> NPC`

它们不是走同一条落地路径。

#### Player -> NPC

当玩家与启用 LLM 且处于激活时间窗内的 NPC 右键交互时，模组会拦截原版对话，改为打开自定义输入框。

玩家提交文本后，本地会：

1. 把文本写成 `player_prompt` 事件记忆。
2. 异步为这条事件生成 embedding。
3. 把 `player_prompt` 事件高优先级入队。
4. 暂停该 NPC 的周期轮询，直到当前玩家对话链完全落地。

#### NPC -> Player

如果模型决定要真正回复玩家，它必须调用 `npc_say_to_player`。

这条工具不会直接立刻弹对话框，而是：

1. 先转成对白动作请求。
2. 进入 `speechDisplayQueue`。
3. 等到当前没有别的菜单占用、NPC 仍然能对玩家说话时，再通过原版对话框弹出。
4. 同时把最终回复写成 `npc_reply` 事件记忆，并异步生成 embedding。

所以 plain assistant 文本本身不会自动出现在游戏里。  
只有 `npc_say_to_player` 才会真正变成玩家能看到的对白。

#### NPC -> NPC

NPC 之间的说话也不是自由广播，而是受约束的近场对话。

模型若调用 `say_to_npc`，本地会先验证：

- 目标 NPC 已加载。
- 目标 NPC 已启用 LLM。
- 目标 NPC 的 provider 可用。
- 目标 NPC 在激活时间窗内。
- 双方在同一张地图。
- 双方距离没有超出感知半径。

验证通过后，消息会以动作请求的形式落地：

- 通过聊天气泡显示在世界中。
- 写入 `npc_to_npc_speech` 相关记忆。
- 可选继续广播给附近 NPC。

这意味着 README 里最该强调的一点是：  
NPC-NPC 对话不是“远程心灵感应”，而是“同图、近场、可见、通过本地校验后才允许发生”的现实感知式对话。

### 4. NPC 广播机制

广播机制是这套系统区别于普通单 NPC 聊天的另一层。

它解决的问题是：一个 NPC 的公开行为，不应该只有自己知道；附近其它启用了 Agent 的 NPC 可以把这件事当成观察事件。

广播来源主要有两类：

- `原生广播`
  例如玩家给某 NPC 送礼，附近 NPC 会收到这件事的观察事件。
- `工具广播`
  例如 NPC 做出带 `broadcast_to_nearby_npcs` 的说话或动作，本地会把这条动作转成广播 dispatch item。

#### 广播如何传播

本地会先把广播放进 `pendingBroadcastQueue`，再按 tick 排空。  
每个广播都有这些元数据：

- `broadcastId`
- `correlationId`
- `hop`
- `maxHops`
- `sender`
- `targetNpcName`
- `mentionedNpcNames`
- `summaryText`

传播时，本地会做这些控制：

- 不投给发送者自己。
- 同一 `correlationId` 已被目标 NPC 忽略时，不再重复投递。
- 同一投递不会重复送达。
- 只有已启用、provider 可用、处于激活时间窗内、且当前已加载的 NPC 才会接收。
- 超过 `max_hops` 后，不再继续扩散，而是生成一个“广播达到上限”的系统事件。

最终接收方看到的不是原始动作对象，而是一个新的本地事件：

- `npc_broadcast_observation`
- 或 `npc_broadcast_limit_reached`

然后它会像其它事件一样再进入自己的 agent 链路。

#### 为什么要有 `ignore_current_broadcast`

如果没有显式忽略机制，广播很容易在 NPC 之间来回放大，形成重复联想或循环反应。

因此当前工具层提供了 `ignore_current_broadcast`：

- NPC 可以明确标记“这条广播我看到了，但不想继续基于它行动”。
- 本地会把对应 `correlationId` 记进忽略集合。
- 之后同链路广播不会再递送给这个 NPC。

这使得广播系统更像“受控扩散”，而不是无限传播。

### 5. 这套机制最终在游戏里表现为什么

从玩家视角看，最终效果大致是：

1. 玩家输入、送礼、时间窗切换、周期轮询、NPC 相遇、附近广播都会先变成本地事件。
2. 每个 NPC 用自己的本地状态机和队列决定什么时候处理哪个事件。
3. 聊天模型只负责在当前上下文里做决策和发工具调用。
4. 本地 runtime 决定哪些决策能落地、何时落地、是否需要排队、是否允许广播。
5. 记忆系统持续把玩家互动、NPC 回复、日终总结和 NPC-NPC 互动沉淀到本地文件。

所以这个模组本质上不是“给 NPC 接一个 API”这么简单，而是一套：

- 有状态
- 有记忆
- 有事件优先级
- 有近场同步
- 有有限广播
- 有本地执行约束

的实时 NPC agent runtime。

## 调试命令

模组提供了几个常用的 SMAPI 控制台命令：

```text
npc_llm_state <NPC内部名>
npc_llm_schedule <NPC内部名>
npc_llm_prompt <NPC内部名> <文本>
```

示例：

```text
npc_llm_state Abigail
npc_llm_schedule Abigail
npc_llm_prompt Abigail 今天过得怎么样？
```

## 本地数据与调试文件

NPC 记忆和调试数据会写到：

```text
Mods/StardewMod/NpcMemories/<存档名>/<NPC内部名>/
```

常见文件包括：

- `profile.json`
- `events.jsonl`
- `days.jsonl`
- `debug.jsonl`
- `vectors.json`

## 从源码构建

### 环境要求

- .NET 6 SDK
- Stardew Valley 游戏目录
- SMAPI
- 可访问 NuGet 的网络环境

### 构建命令

这个项目使用 `Pathoschild.Stardew.ModBuildConfig`，建议显式传入 `GamePath`：

```bash
dotnet restore StardewMod.sln
dotnet build StardewMod.sln -c Debug -p:GamePath="D:\\SteamLibrary\\steamapps\\common\\Stardew Valley"
```

发布构建：

```bash
dotnet build StardewMod.sln -c Release -p:GamePath="D:\\SteamLibrary\\steamapps\\common\\Stardew Valley"
```

如果你在 WSL 或类 Unix 环境下构建，也可以传入挂载路径：

```bash
dotnet build StardewMod.sln -c Debug -p:GamePath="/mnt/d/SteamLibrary/steamapps/common/Stardew Valley"
```

### 构建产物

常见输出位置：

- `bin/Debug/net6.0/StardewMod 1.0.0.zip`
- `bin/Release/net6.0/StardewMod 1.0.0.zip`
- `bin/Debug/net6.0/StardewMod.dll`
- `bin/Release/net6.0/StardewMod.dll`

更推荐直接使用生成好的 zip 进行安装或分发。

## 仓库结构

```text
.
├── ModEntry.cs
├── ModConfig.cs
├── Models/
├── Services/
├── Ui/
├── docs/
├── manifest.json
├── mod.toml
└── StardewMod.csproj
```

目录职责：

- `Models/`
  数据模型、配置结构、运行时快照。
- `Services/`
  核心业务逻辑，包括 NPC Agent、LLM router、记忆、schedule 编辑等。
- `Ui/`
  菜单、叠加层、按钮和输入收集逻辑。
- `docs/`
  配置教程、源码分析、设计说明。

## 文档索引

- 使用、配置、编译教程：[docs/使用配置与编译教程.md](./docs/使用配置与编译教程.md)
- NPC 行为与控制源码解析：[docs/NPC行为与控制源码解析.md](./docs/NPC行为与控制源码解析.md)
- NPC 记忆系统源码解析：[docs/NPC记忆系统源码解析.md](./docs/NPC记忆系统源码解析.md)

## 开发说明

- `ModEntry.cs` 只负责初始化、事件注册和薄转发。
- 新功能应优先放入现有模块，不要把跨层逻辑塞回入口文件。
- UI 不直接改底层存储，业务修改应通过 Service 完成。
- `mod.toml` 负责全局 LLM 配置，per-NPC 配置应留在游戏内菜单和存档数据里。

## 参考

- SMAPI mod package 文档：<https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md>
- Stardew Valley Wiki, Modding: Player Guide / Getting Started：<https://stardewvalleywiki.com/Modding:Player_Guide/Getting_Started>
- Stardew Valley Wiki, Modding: Modder Guide / Test and Troubleshoot：<https://stardewvalleywiki.com/Modding:Modder_Guide/Test_and_Troubleshoot>

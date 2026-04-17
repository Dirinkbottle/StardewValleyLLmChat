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

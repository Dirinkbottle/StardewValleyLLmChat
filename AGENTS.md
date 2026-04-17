<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **StardewMod** (1127 symbols, 3930 relationships, 97 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## When Debugging

1. `gitnexus_query({query: "<error or symptom>"})` — find execution flows related to the issue
2. `gitnexus_context({name: "<suspect function>"})` — see all callers, callees, and process participation
3. `READ gitnexus://repo/StardewMod/process/{processName}` — trace the full execution flow step by step
4. For regressions: `gitnexus_detect_changes({scope: "compare", base_ref: "main"})` — see what your branch changed

## When Refactoring

- **Renaming**: MUST use `gitnexus_rename({symbol_name: "old", new_name: "new", dry_run: true})` first. Review the preview — graph edits are safe, text_search edits need manual review. Then run with `dry_run: false`.
- **Extracting/Splitting**: MUST run `gitnexus_context({name: "target"})` to see all incoming/outgoing refs, then `gitnexus_impact({target: "target", direction: "upstream"})` to find all external callers before moving code.
- After any refactor: run `gitnexus_detect_changes({scope: "all"})` to verify only expected files changed.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Tools Quick Reference

| Tool | When to use | Command |
|------|-------------|---------|
| `query` | Find code by concept | `gitnexus_query({query: "auth validation"})` |
| `context` | 360-degree view of one symbol | `gitnexus_context({name: "validateUser"})` |
| `impact` | Blast radius before editing | `gitnexus_impact({target: "X", direction: "upstream"})` |
| `detect_changes` | Pre-commit scope check | `gitnexus_detect_changes({scope: "staged"})` |
| `rename` | Safe multi-file rename | `gitnexus_rename({symbol_name: "old", new_name: "new", dry_run: true})` |
| `cypher` | Custom graph queries | `gitnexus_cypher({query: "MATCH ..."})` |

## Impact Risk Levels

| Depth | Meaning | Action |
|-------|---------|--------|
| d=1 | WILL BREAK — direct callers/importers | MUST update these |
| d=2 | LIKELY AFFECTED — indirect deps | Should test |
| d=3 | MAY NEED TESTING — transitive | Test if critical path |

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/StardewMod/context` | Codebase overview, check index freshness |
| `gitnexus://repo/StardewMod/clusters` | All functional areas |
| `gitnexus://repo/StardewMod/processes` | All execution flows |
| `gitnexus://repo/StardewMod/process/{name}` | Step-by-step execution trace |

## Self-Check Before Finishing

Before completing any code modification task, verify:
1. `gitnexus_impact` was run for all modified symbols
2. No HIGH/CRITICAL risk warnings were ignored
3. `gitnexus_detect_changes()` confirms changes match expected scope
4. All d=1 (WILL BREAK) dependents were updated

## Keeping the Index Fresh

After committing code changes, the GitNexus index becomes stale. Re-run analyze to update it:

```bash
npx gitnexus analyze
```

If the index previously included embeddings, preserve them by adding `--embeddings`:

```bash
npx gitnexus analyze --embeddings
```

To check whether embeddings exist, inspect `.gitnexus/meta.json` — the `stats.embeddings` field shows the count (0 means no embeddings). **Running analyze without `--embeddings` will delete any previously generated embeddings.**

> Claude Code users: A PostToolUse hook handles this automatically after `git commit` and `git merge`.

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->



# Repository Agent Guide

本文件定义本仓库的代码组织约束。目标只有三个：

1. 模块化。
2. 职责分明。
3. 不要把临时实现、调试残留和跨层逻辑污染进仓库。

## 总原则

- 优先在现有模块内扩展，不要把新功能随手塞进任意大文件。
- 一个文件只负责一个稳定主题；如果一个文件开始同时处理 UI、状态、序列化、运行时调度，就应该继续拆分。
- `ModEntry.cs` 只做组合、初始化、事件注册和薄转发，不承载业务逻辑。
- Model 保持为纯数据结构和少量本地无副作用辅助方法，不直接依赖业务服务。
- UI 不直接改底层存储；UI 通过 Service 完成规则读取、应用和保存。
- Service 不直接承担“全能工具箱”职责。跨主题逻辑必须拆到子模块或子文件。
- 不允许为了图省事新增 `Utils.cs`、`Helper.cs`、`Misc.cs`、`Temp.cs`、`Test.cs` 这类模糊文件名承接杂项。

## 仓库分层

### 根目录

- `ModEntry.cs`
  - 仅允许保留入口装配、事件绑定、薄分发。
  - 不要把业务判断、长流程、复杂状态机继续写回这里。

- `ModConfig.cs`
  - 仅放模组级静态配置。

### `Models/`

- 只放数据模型、枚举、快照、DTO、纯配置结构。
- 允许少量纯函数型方法，例如 `Clone()`、`Normalize()`、`DescribeCurrent()`。
- 不允许写 IO、SMAPI 事件处理、NPC 调度、副作用型逻辑。
- 大主题必须建子目录，例如 `Models/NpcLlm/`。

### `Services/`

- 只放业务服务、运行时控制器、编排逻辑。
- 大服务必须按主题拆 `partial` 或拆独立协作类。
- 拆分后的文件名必须能一眼看出职责，例如：
  - `NpcAgentManager.Requests.cs`
  - `NpcAgentManager.Directives.cs`
  - `NpcScheduleEditorService.Compilation.cs`
  - `NpcLlmToolService.Memory.cs`
- 如果一个服务已经超过约 400 到 500 行，并且出现明显子主题，就应继续拆分。

### `Ui/`

- 只放菜单、叠加层、按钮、纯展示和输入收集逻辑。
- UI 可以持有当前编辑态，但不要直接操作底层存档格式。
- UI 发起的数据修改必须通过 Service。
- UI 的输入接管必须成对出现：打开、接管、关闭、释放，避免残留输入状态。

### `docs/`

- 放技术报告、源码分析、设计说明。
- 文档不能替代代码边界；“文档里说明过”不等于代码里可以继续耦合。

## 当前模块边界

### `Services/NpcAgent/`

- 负责 NPC agent 运行时编排。
- 包括事件入队、请求生命周期、运行时 patch、即时指令执行、调试输出。
- 不负责路径编辑器 UI。
- 不负责 tool schema 定义。

### `Services/NpcScheduleEditor/`

- 负责日程编辑器、规则读取、规则保存、schedule 编译、路径预览。
- 负责原版 raw schedule 解析与 override 组装。
- 不负责 LLM 请求、记忆库、对话路由。

### `Services/NpcLlmTools/`

- 负责本地 tool schema、tool 分发、参数读取、tool 执行上下文。
- 不负责 NPC 实际执行循环本身。
- schedule 修改只生成/修改工作规则，不直接承接 agent 主循环。

### `Models/NpcLlm/`

- 负责 LLM 配置、指令、记忆记录、运行时快照。
- 不允许把 service 逻辑重新回灌到模型层。

## 新增代码的放置规则

- 新增功能前先判断它属于哪一层：
  - 数据定义：`Models/`
  - 业务编排：`Services/`
  - 菜单与显示：`Ui/`
  - 分析与说明：`docs/`
- 如果功能属于已有主题，优先进入对应子目录，而不是回到根目录。
- 如果新增的是某个大模块的子职责，优先新增子文件，不要继续扩大主文件。
- 文件命名必须体现职责，不允许使用含糊名词。

## 重构规则

- 拆文件时必须保持行为一致，不能借拆分之名偷偷改语义，除非任务本身明确要求。
- 迁移代码时保留原注释；如果注释已经过时，更新而不是直接删除。
- 大重构必须小步进行：
  1. 先建新文件。
  2. 再搬运对应方法。
  3. 立刻编译。
  4. 再继续下一块。
- 拆分后要核对前后方法集合或公开类型集合，避免误删。
- 不允许因为“看起来没用”就顺手删除未充分确认的逻辑。

## 禁止污染仓库的行为

- 不要留下临时调试日志、临时 HUD 文案、临时开关、临时命令。
- 不要引入和任务无关的重命名、格式化风暴、目录洗牌。
- 不要在多个地方复制相同逻辑；应提取到明确归属的模块。
- 不要把不同层的概念混在一起，例如：
  - 在 Model 里直接访问 `Game1` 以外的业务服务。
  - 在 UI 里直接读写存档结构。
  - 在 `ModEntry` 里堆长业务流程。
- 不要创建“过渡文件”后永久不清理。

## SMAPI / 游戏运行时约束

- 任何输入接管都必须考虑退出路径，尤其是 overlay、世界层菜单和自定义对话框。
- 任何会暂停 NPC、覆盖 schedule、插队即时动作的逻辑，都必须明确谁负责恢复。
- schedule、patch、directive 三套状态不能互相偷改来源；修改时必须清楚当前修改的是：
  - 原版规则
  - 存档 override
  - 运行时 patch

## 提交前自检

- 新增代码是否放在了正确层级。
- 是否把 unrelated 逻辑顺手塞进当前文件。
- 是否引入了新的超大文件。
- 是否保留并更新了关键注释。
- 是否完成编译验证。
- 如果做了拆分，是否核对前后功能一致性。

如果不能满足这些约束，优先继续整理结构，而不是继续把债务堆大。

# Stardew Valley NPC行为与控制源码解析

本文基于当前项目中的反编译源码快照整理：

- `StardewVallySource/StardewValley/NPC.cs`
- `StardewVallySource/StardewValley/GameLocation.cs`
- `StardewVallySource/StardewValley/Character.cs`
- `StardewVallySource/StardewValley.Pathfinding/PathFindController.cs`
- `StardewVallySource/StardewValley.Pathfinding/SchedulePathDescription.cs`
- `StardewVallySource/StardewValley.Pathfinding/WarpPathfindingCache.cs`

文中的行号对应当前仓库里的这份反编译结果，后续如果你替换了源码快照，行号可能会漂移。

## 1. 这套系统到底分几层

从源码看，NPC 的“行为/控制”不是一个单点，而是四层叠加：

1. **静态数据层**
   - NPC 默认出生点、默认朝向、对话资源、`Characters/schedules/<NPC名>` 日程资源。

2. **日程层**
   - 负责“今天该去哪、几点出发、到点后做什么”。
   - 核心入口在 `NPC.TryLoadSchedule()`、`NPC.parseMasterScheduleImpl()`、`NPC.checkSchedule()`。

3. **路径执行层**
   - 负责把“从 A 点到 B 点”变成具体的逐 tile 行走。
   - 核心类是 `PathFindController`。

4. **交互与临时接管层**
   - 玩家右键对话、送礼、配偶互动、特殊事件、临时控制器、路线终点动画，都可能临时覆盖正常日程行为。
   - 核心入口在 `NPC.checkAction()`、`NPC.update()`、`temporaryController`、`CurrentDialogue`。

可以把它理解成：

```text
日程资源 -> 解析成当天 schedule -> 到时间后挂上 PathFindController -> NPC 每帧移动
                                     -> 到终点时触发消息/动画/方形巡逻
玩家交互/事件/临时控制器 ------------------------------------------> 可中途覆盖
```

## 2. 关键源码入口总表

| 文件 | 行号 | 作用 |
|---|---:|---|
| `StardewValley/NPC.cs` | 230-330 | NPC 关键运行时字段，含默认位置、默认地图、`followSchedule`、`temporaryController`、`TemporaryDialogue` |
| `StardewValley/NPC.cs` | 533-538 | `Schedule` 属性，保存“今天的日程” |
| `StardewValley/NPC.cs` | 564-592 | `CurrentDialogue` 属性，按需加载并支持 `TemporaryDialogue` 覆盖 |
| `StardewValley/NPC.cs` | 3181-3305 | `NPC.update()`，运行时总调度，含临时控制器优先级 |
| `StardewValley/NPC.cs` | 3601-3907 | 对话重置、对话加载 |
| `StardewValley/NPC.cs` | 3908-3955 | `checkForNewCurrentDialogue()`，按地点/星期/好感动态切对话 |
| `StardewValley/NPC.cs` | 4092-4156 | `checkSchedule()`，到时间后切路线 |
| `StardewValley/NPC.cs` | 4618-4688 | 路线终点行为、终点动画加载 |
| `StardewValley/NPC.cs` | 5329-5404 | 路线栈拼接、跨场景路线生成 |
| `StardewValley/NPC.cs` | 5458-5738 | `parseMasterScheduleImpl()`，解析日程语义的核心 |
| `StardewValley/NPC.cs` | 5747-5750 | `SplitScheduleCommands()`，按 `/` 切分日程命令 |
| `StardewValley/NPC.cs` | 5754-5904 | `TryLoadSchedule()`，决定今天用哪一条 schedule key |
| `StardewValley/NPC.cs` | 6086-6219 | `dayUpdate()` / `OnDayStarted()` / `resetForNewDay()`，新的一天如何重置 NPC |
| `StardewValley/GameLocation.cs` | 7638-7678 | 玩家右键时，从地图分发到 `NPC.checkAction()` |
| `StardewValley/Character.cs` | 1058-1078 | 表情系统 `doEmote()` |
| `StardewValley/Character.cs` | 1442-1470 | 面向玩家 `faceTowardFarmerForPeriod()` |
| `StardewValley.Pathfinding/PathFindController.cs` | 55-142 | 控制器构造与运行模式 |
| `StardewValley.Pathfinding/PathFindController.cs` | 142-171 | `update()` |
| `StardewValley.Pathfinding/PathFindController.cs` | 174-250 | 通用 A* 寻路 |
| `StardewValley.Pathfinding/PathFindController.cs` | 253-341 | `moveCharacter()`，逐 tile 行走 |
| `StardewValley.Pathfinding/PathFindController.cs` | 343-417 | `handleWarps()`，跨图 warp |
| `StardewValley.Pathfinding/PathFindController.cs` | 425-509 | `findPathForNPCSchedules()` 和可走性判定 |
| `StardewValley.Pathfinding/SchedulePathDescription.cs` | 6-25 | 一段 schedule 路线的数据结构 |
| `StardewValley.Pathfinding/WarpPathfindingCache.cs` | 24-45 | 场景间路线缓存与跨图路径选择 |

## 3. NPC 的核心状态字段

`NPC.cs` 230-330、533-592 这一段，基本决定了 NPC 一天里怎么被驱动。

### 3.1 默认出生/归位相关

- `defaultFacingDirection`
  - 默认朝向。
- `defaultPosition`
  - 默认 tile 坐标，保存为像素向量。
- `defaultMap`
  - 默认所在地图。

这三个字段非常关键。源码里 NPC **每天重置时会先回到默认地图/默认位置**，不是直接从 schedule 第一条的目标点凭空生成。

### 3.2 日程相关

- `followSchedule`
  - 是否启用日程。
- `Schedule`
  - 今天已经解析完成的 schedule。
  - 类型是 `Dictionary<int, SchedulePathDescription>`，键是“出发时间”。
- `queuedSchedulePaths`
  - 已经到点、等待执行的 schedule 段。
- `directionsToNewLocation`
  - 当前正在执行的那一段 schedule。
- `previousEndPoint`
  - 上一条路线终点。

### 3.3 控制器相关

- `base.controller`
  - 普通移动控制器。日程走路时主要挂在这里。
- `temporaryController`
  - 临时控制器。优先级高于正常 `base.controller`。
  - 常用于事件、临时引导、配偶回家等场景。

### 3.4 对话相关

- `TemporaryDialogue`
  - 如果不为 `null`，`CurrentDialogue` 直接用它，不再读 `Game1.npcDialogues`。
- `CurrentDialogue`
  - 延迟加载属性。第一次访问时才会 `loadCurrentDialogue()`。
- `currentMarriageDialogue`
  - 配偶专用对话队列。
- `endOfRouteMessage`
  - 到达某条 schedule 终点后要说的话。

### 3.5 路线终点表现相关

- `endOfRouteBehaviorName`
  - 路线终点动作名。
- `goingToDoEndOfRouteAnimation`
  - 将要播放终点动画。
- `doingEndOfRouteAnimation`
  - 正在播放终点动画。
- `isWalkingInSquare`
  - 终点行为为 `square_*` 时进入小范围巡逻。

## 4. 一天开始时，NPC 是怎么被重置的

核心在：

- `NPC.dayUpdate()`，`NPC.cs:6086`
- `NPC.OnDayStarted()`，`NPC.cs:6144`
- `NPC.resetForNewDay()`，`NPC.cs:6165`

### 4.1 `dayUpdate()`：夜里保存阶段的重置

`dayUpdate()` 做的事情很重：

- 清空排队路线 `queuedSchedulePaths`
- 重置 `lastAttemptedSchedule`
- 清掉一些临时动画/外观状态
- 如果当前有 `defaultMap/defaultPosition`，调用 `Game1.warpCharacter(this, this.defaultMap.Value, this.defaultPosition.Value / 64f)` 把 NPC 送回“默认家”
- 更新隐身状态
- 调 `resetForNewDay(dayOfMonth)`
- 重新选外观、清头顶文字

这就是为什么：

- NPC 通常会先回默认位置
- 你只改了上午 7 点的 schedule 目标，不代表 7 点时会“出生”在目标点

### 4.2 `resetForNewDay()`：清状态并重新加载今日 schedule

`resetForNewDay()` 会：

- 停止移动
- 清除 `base.controller`
- 清除 `temporaryController`
- 清空当前路线信息
- 恢复默认朝向
- 重置 `previousEndPoint`
- 清掉终点动画状态
- 如果是村民，调用 `TryLoadSchedule()`
- 然后再调用 `performSpecialScheduleChanges()`

这说明：

- **NPC 的“今天 schedule”是每天重置时重新选择和解析的**
- 你运行时修改原始 schedule 资源后，如果 NPC 已经缓存过，需要额外处理缓存和已加载 schedule

### 4.3 `OnDayStarted()`：开日后的补充行为

`OnDayStarted()` 主要处理一些婚后逻辑，例如配偶职责，不是 schedule 主入口。

## 5. 运行时主循环：是谁在每帧驱动 NPC

核心在 `NPC.update(GameTime time, GameLocation location)`，`NPC.cs:3181`。

这个函数的优先级关系很重要：

1. 先处理 schedule delay、路线终点动画、服装切换、睡觉动画等状态同步
2. 如果 `returningToEndPoint`，执行“回终点”逻辑
3. 否则如果 `temporaryController != null`
   - 优先更新 `temporaryController`
   - 更新完成后，如果它是 `NPCSchedule` 类型，会再触发一次 `checkSchedule(Game1.timeOfDay)`
4. 否则走 `base.update(time, location)`
   - 普通的 `base.controller` 会在这里被驱动

结论：

- `temporaryController` 的优先级高于普通日程移动
- 普通日程移动不是在 `checkSchedule()` 里一步走完，而是挂一个 `PathFindController`，然后由每帧 update 去推进

## 6. 今天到底会选中哪一条 schedule

核心在 `NPC.TryLoadSchedule()`，`NPC.cs:5754-5904`。

源码里 schedule 选择优先级是有顺序的，不是“名字随便匹配一个”。

### 6.1 高层优先级

大致顺序如下：

1. 如果 schedule 资源本身没加载出来，清空 schedule
2. `GreenRain`
3. `islandScheduleName`
4. 被动节日相关 key
   - 已婚会优先 `marriage_...`
5. 已婚当天特殊 key
   - `marriage_<season>_<day>`
   - 某些配偶工作日会走 `marriageJob`
   - `marriage_<dayName>`
6. 普通 NPC 的日期 key
   - `<season>_<day>`
   - `<day>_<heart>`
   - `<day>`
7. 某些特殊 NPC 规则
   - 例如 Pam 的 `bus`
8. 下雨 key
   - `rain2`
   - `rain`
9. 星期与好感组合
   - `<season>_<dayName>_<heart>`
   - `<season>_<dayName>`
   - `<dayName>_<heart>`
   - `<dayName>`
10. 季节默认
   - `<season>`
   - `spring_<dayName>`
   - `spring`
11. 全部失败则 `ClearSchedule()`

### 6.2 这对 mod 的直接影响

- 改某个 NPC 的 schedule，不是只改一个 key 就够了
- 你必须先知道 **今天实际命中的 key 是哪个**
- 如果你只改了 `spring`，但今天命中了 `Mon`、`rain`、`spring_15`，那么你的改动不会生效

## 7. schedule 文件是怎么解析的

核心在：

- `NPC.parseMasterScheduleImpl()`，`NPC.cs:5458-5738`
- `NPC.SplitScheduleCommands()`，`NPC.cs:5747-5750`

### 7.1 原始脚本的切分方式

源码直接：

```csharp
return LegacyShims.SplitAndTrim(rawScript, '/', StringSplitOptions.RemoveEmptyEntries);
```

也就是说 schedule 原始脚本是按 `/` 切成多段命令的。

通常可以理解为：

```text
条件命令 / 条件命令 / 时间点1 地图 x y 朝向 行为 / 时间点2 地图 x y ...
```

### 7.2 第一段命令可能不是“时间点”

前几段命令可能是条件或跳转，不一定是具体路线。

源码里处理了这些特殊语义：

- `GOTO <key>`
- `NOT friendship <NPC> <hearts> ...`
- `MAIL <mailId>`

### 7.3 `GOTO`

有两种常见情况：

1. 第一段就是 `GOTO`
   - 直接跳转到另一条 schedule key 去解析
2. 在条件段之后出现 `GOTO`
   - 满足前置条件后，再跳转

特殊值：

- `GOTO season`
  - 会转成当前季节 key
  - 如果当前季节 key 不存在，回退到 `spring`
- `GOTO no_schedule`
  - 直接 `followSchedule = false`，当天不再按 schedule 行动

### 7.4 `NOT friendship`

源码只对 `NOT friendship` 做了专门处理。

含义不是“满足时执行”，而是：

- 如果任意农夫对指定 NPC 的好感心数达到阈值，则条件判定为真
- 条件为真时，当前 key 回退到 `spring`
- 条件不真时，跳过这一段条件命令，继续往后读当前脚本

这是一个“反条件分流”。

### 7.5 `MAIL`

`MAIL <mailId>` 用来根据邮件或世界状态决定是否跳过后续命令。

源码逻辑是：

- 如果没收到对应 mail，也没对应 world state，则只跳过 1 段
- 如果已经收到，则跳过 2 段

本质上它是在通过 `routesToSkip` 控制从第几段开始当成真正的 schedule 解析。

### 7.6 普通路线项的格式

普通项大致长这样：

```text
700 SeedShop 4 19 2
```

可解析字段：

1. 时间
2. 地图名
3. X tile
4. Y tile
5. 可选朝向
6. 可选终点行为或终点文本

如果地图位字段本身是数字，源码会把它当成“沿用上一条 location”。

### 7.7 `bed` 关键字

`bed` 是特殊地点，不是普通地图名。

源码分两种：

- 已婚 NPC
  - `bed` 会转成 `BusStop 9 23 3`
- 未婚 NPC
  - 会尝试读取 `default` 或 `spring` schedule 的最后一条位置
  - 如果读不到，再回退到 `defaultMap/defaultPosition`

此外，`bed` 还会尝试加载 `<npcname>_sleep` 作为终点睡觉动画。

### 7.8 `time == 0` 的特殊语义

这是最关键的一条。

当某段时间是 `0` 时，源码不会把它加入当天的“待出发路线表”，而是：

- 把这条记录当成**今天的默认起始地图与起始坐标**
- 更新 `default_map/default_x/default_y`
- 更新 `previousGameLocation/previousPosition`
- 更新朝向
- 更新 `previousEndPoint`
- 最后如果是主机端，会 `Game1.warpCharacter(this, default_map, new Point(default_x, default_y))`

这意味着：

- `0 Farm 64 15 2` 才接近“今天早上出生在农场”
- `700 Farm 64 15 2` 不是出生点，而是 **7:00 从上一位置出发去农场**

### 7.9 `a700` 的特殊语义

如果时间字段以 `a` 开头，比如：

```text
a700 Town 35 80 2
```

源码会把它理解为“**到达时间**”，不是出发时间。

解析流程：

1. 先按终点生成完整路线
2. 统计路线相邻点的总移动距离
3. 根据步行速度估算旅行时长
4. 再把 `700` 反推成更早的出发时间

所以：

- `a700` = 7:00 到达
- `700` = 7:00 出发

### 7.10 地图不可达时的替换逻辑

`changeScheduleForLocationAccessibility()` 会处理部分地图访问性问题：

- `JojaMart`
- `Railroad`
- `CommunityCenter`

如果地点不可进入：

- 某些地点会尝试找 `<Location>_Replacement`
- 某些地点直接回退到 `default` 或 `spring`

### 7.11 解析完后得到什么

最终得到的是：

- `Dictionary<int, SchedulePathDescription>`

其中每条 `SchedulePathDescription` 包含：

- `route`
- `time`
- `facingDirection`
- `endOfRouteBehavior`
- `endOfRouteMessage`
- `targetLocationName`
- `targetTile`

见 `SchedulePathDescription.cs:6-25`。

## 8. 路线是如何生成出来的

核心在 `NPC.pathfindToNextScheduleLocation()`，`NPC.cs:5343-5404`。

### 8.1 路线不是“你写什么点就走什么点”

schedule 里的普通语义是：

- 给出上一位置
- 给出目标位置
- 游戏自己算出一条可走路径

也就是说原版 schedule 更像：

```text
从上一节点到目标节点的自动寻路
```

不是：

```text
把我写进去的每个采样点都当成硬编码路径点
```

### 8.2 同图路线

如果 `startingLocation == endingLocation`：

- 直接用 `PathFindController.findPathForNPCSchedules(start, end, location, 30000, this)`

### 8.3 跨图路线

如果起点和终点不在同一地图：

1. 先调用 `getLocationRoute(startingLocation, endingLocation)`
2. 实际转到 `WarpPathfindingCache.GetLocationRoute(...)`
3. 得到一串场景名，例如：

```text
BusStop -> Town -> Mountain
```

4. 对每一张地图：
   - 找到通往下一张地图的 warp 点 `getWarpPointTo(...)`
   - 在当前地图内寻路到这个 warp 点
   - 再用 `getWarpPointTarget(...)` 得到进入下一图后的落点
5. 最后一张图再寻路到最终目标 tile

### 8.4 场景间路径不是随便都能有

`WarpPathfindingCache.cs:24-45` 说明了几点：

- 路线缓存来自游戏现有的 warp 和 door 网络
- 某些地图会被排除
- 某些路线有性别限制
- 如果找不到合法场景链，`GetLocationRoute()` 会返回 `null`

因此你想让 NPC 从杂货铺去农场、海边、山上，不是理论上都能去，而是必须满足：

- 地图网络里确实存在合法 warp 链
- 中间场景没有被排除
- 性别限制允许通过

## 9. A* 寻路器如何决定“能不能走”

核心在 `PathFindController.findPathForNPCSchedules()` 与 `isPositionImpassableForNPCSchedule()`：

- `PathFindController.cs:425-509`

### 9.1 寻路基本特性

- 四方向寻路，不走斜线
- 使用 A* 风格优先队列
- 有迭代上限，默认这里给到 `30000`
- 只保证“可达且较优”，不保证绝对最短视觉路线

### 9.2 地形偏好

`getPreferenceValueForTerrainType()` 会对不同地面类型做偏好：

- `stone`：`-7`
- `wood`：`-4`
- `dirt`：`-2`
- `grass`：`-1`

值越低越偏好，所以 NPC 会略微倾向走石路、木路，而不是完全随机。

### 9.3 不可通行判定

`isPositionImpassableForNPCSchedule()` 会拒绝这些 tile：

- `Buildings` 层有不可通过建筑 tile
- 带 `Action` 且不是 Door/Passable 的 tile
- `LockedDoorWarp`
- `Back` 层有 `NoPath`
- tile 本身就是 warp 点
- 不可通行地形特征、巨型地形特征

这就是为什么：

- 你在地图上任意画一条线，不代表 NPC 真能按这条线走
- 只要中间踩到 `NoPath`、锁门、不可走建筑格，这条 schedule 就可能寻路失败

## 10. 路线栈的顺序与连续性

这个问题和你当前 mod 的“采样绘制”直接相关。

### 10.1 顺序是有意义的

`NPC.addToStackForSchedule()`，`NPC.cs:5329-5338`，会把每段路径拼成一个 `Stack<Point>`。

`PathFindController.moveCharacter()` 每次都读取：

- `pathToEndPoint.Peek()` 作为当前目标点
- 到达后 `Pop()`

所以：

- 路径点顺序是严格有先后顺序的
- NPC 会按这个顺序消耗 route 栈

### 10.2 离散不连续点会怎样

如果你说的“离散不连续点”是指：

1. **原版 schedule 的两个目标点相距很远**
   - 没问题，游戏会自动 A* 补全中间连续路径

2. **你自己直接塞给 `route` 的点集本身不连续**
   - `PathFindController` 不会替你重新修正你已经给出的 route 栈
   - 运行时会按 `Peek()` 的目标 tile 去追
   - 如果下一目标点跟当前位置不邻接，角色仍会尝试朝那个 tile 方向移动，容易出现卡住、跳点、与 warp 不一致等问题

结论：

- **对原版 schedule 语义来说，离散目标点没问题，因为中间路径由游戏生成**
- **对你的采样式 mod 来说，如果你想直接控制 `route` 栈，就必须自己保证顺序、连续性和跨图 warp 合法性**

## 11. `checkSchedule()` 是怎么把“到点”变成“开始走”

核心在 `NPC.checkSchedule(int timeOfDay)`，`NPC.cs:4092-4156`。

逻辑顺序是：

1. 处理 `scheduleDelaySeconds`
2. 如果当前正在返回终点，直接不处理
3. 如果今天忽略 schedule 或 `Schedule == null`，返回
4. 如果 `lastAttemptedSchedule < timeOfDay`
   - 从 `Schedule` 中取出当前时间点对应的 `SchedulePathDescription`
   - 放进 `queuedSchedulePaths`
5. 如果当前 `base.controller` 还在走路，先不插入新路线
6. 如果队列里有已到点的路线，则取第一条
7. 调 `prepareToDisembarkOnNewSchedulePath()`
8. 如果没有 `temporaryController`
   - 把该条 schedule 赋给 `directionsToNewLocation`
   - 创建新的 `PathFindController(this.directionsToNewLocation.route, this, currentLocation)`
   - 设置 `finalFacingDirection`
   - 设置 `endBehaviorFunction`
9. 如果控制器路径为空，直接执行终点行为

关键结论：

- schedule 的字典 key 是“**出发时间**”
- 到时间后，NPC 不一定立刻到目标点，而是 **开始走**
- 如果当前还有路线没走完，新路线会等前一条结束

## 12. `PathFindController` 是如何让 NPC 真正动起来的

核心在：

- `PathFindController.update()`，`PathFindController.cs:142`
- `PathFindController.moveCharacter()`，`PathFindController.cs:253`
- `PathFindController.handleWarps()`，`PathFindController.cs:343`

### 12.1 `update()`

每帧做这些事：

- 路径空了就结束
- 非 NPC schedule 且场上没有玩家时，某些控制器可以直接瞬移到终点
- 否则调用 `moveCharacter()`
- 如果长时间完全没动，普通控制器会超时取消

注意：

- `NPCSchedule == true` 的控制器不会走“没玩家就直接传送到终点”的逻辑
- 所以日程路线是真走出来的

### 12.2 `moveCharacter()`

执行方式不是“一个 tile 一个 tile 跳”，而是：

- 看 `Peek()` 得到的目标 tile
- 按人物碰撞箱判断是否已经到达
- 没到就设置左右上下移动标志
- 调 `MovePosition(...)`

到达一个 tile 后：

- `Pop()` 当前点
- 如果 route 清空，停下、转朝向、设置 `endOfRouteMessage`
- 调终点行为函数

### 12.3 `handleWarps()`

当角色下一步会撞到 warp/door 时：

- 识别当前 warp
- 处理特殊地图替换
- 某些已婚 NPC 会特殊改写进出家门逻辑
- `Game1.warpCharacter(...)`
- 然后把路线栈前面已经无意义的点弹掉，直到和新落点重新对齐

所以跨图路线不是一口气寻完整个世界，而是：

- 每张图内一段
- 到门口 warp
- 到下一图再继续消费剩余 route 栈

## 13. 路线终点行为、动画、方形巡逻

核心在：

- `NPC.getRouteEndBehaviorFunction()`，`NPC.cs:4621`
- `NPC.loadEndOfRouteBehavior()`，`NPC.cs:4655`

### 13.1 可挂的终点行为

源码支持几类：

- 纯消息
- `square_*`
  - 进入方形巡逻
- `change_beach`
- `change_normal`
  - 衣装变化
- `Data/animationDescriptions` 中定义的动画名

### 13.2 终点文本的处理

如果 `endMessage != null`，或者行为字段本身看起来像引号文本：

- 会写入 `nextEndOfRouteMessage`
- 路走完后进 `endOfRouteMessage`

也就是：

- 某些 schedule 末尾说的话，不是在开始走时弹
- 而是在 route 结束后通过 NPC 对话系统读出来

## 14. 玩家右键 NPC 时发生了什么

调用链是：

1. `GameLocation.checkAction(...)`，`GameLocation.cs:7638-7678`
2. 遍历 `this.characters`
3. 命中 NPC 碰撞箱后调用 `n.checkAction(who, this)`
4. 进入 `NPC.checkAction(...)`，`NPC.cs:2464-2848`

### 14.1 `NPC.checkAction()` 的职责非常大

这里不仅是“弹对话框”，还包括：

- 隐身与睡觉状态判断
- 任务交互
- 送礼逻辑
- 配偶亲吻逻辑
- 好感和会话加成
- 选择当天可说的话
- 让 NPC 面向玩家
- 播放表情
- 最终调用 `Game1.drawDialogue(this)`

### 14.2 面向玩家和表情

用到的是 `Character.cs` 里的：

- `doEmote()`，`Character.cs:1058-1078`
- `faceTowardFarmerForPeriod()`，`Character.cs:1442-1470`

这意味着如果你想做 LLM NPC：

- 最稳的“接管说话前站住并朝向玩家”方式，不是自己硬写一套，而是复用现有 `faceTowardFarmerForPeriod(...)`
- 想表达情绪也可以直接走 `doEmote(...)`

## 15. NPC 对话是怎么决定的

核心在：

- `CurrentDialogue` 属性，`NPC.cs:564`
- `resetCurrentDialogue()`，`NPC.cs:3601`
- `loadCurrentDialogue()`，`NPC.cs:3608`
- `checkForNewCurrentDialogue()`，`NPC.cs:3908`

### 15.1 `CurrentDialogue` 有缓存

第一次访问 `CurrentDialogue` 时：

- 如果 `TemporaryDialogue != null`，直接返回它
- 否则从 `Game1.npcDialogues` 取
- 取不到时才执行 `loadCurrentDialogue()`

这说明：

- 游戏内 NPC 说话内容不是每次都重新读磁盘
- 是有运行时缓存的

### 15.2 `loadCurrentDialogue()` 的来源很多

它会综合：

- 绿色雨
- 家庭关系随机话题
- 婚后/订婚对话
- 离婚对话
- 雨天对话
- 季节/好感普通对话
- 额外晨间对话

### 15.3 `checkForNewCurrentDialogue()` 的动态覆盖

它会尝试这些 key：

- 事件对话
- `<seasonPrefix><location>_<tileX>_<tileY>`
- `<seasonPrefix><location>_<dayName>`
- `<seasonPrefix><location><hearts>`
- `<seasonPrefix><location>`

找到后会：

- `removeOnNextMove = true`
- 压到 `CurrentDialogue`

这就是为什么 NPC 在特定地点、特定星期、特定好感，能说出与平时不同的话。

## 16. `temporaryController` 到底是什么关系

这部分对你的路线编辑器、LLM 控制 NPC 都非常关键。

### 16.1 它不是普通 schedule

普通 schedule 路线：

- 一般挂到 `base.controller`

临时控制器：

- 存在于 `temporaryController`
- 在 `NPC.update()` 中优先运行

### 16.2 有了 `temporaryController` 之后会怎样

- 正常 `base.update()` 不会走
- 也就是正常日程移动会被压住
- 临时控制器结束后，如果它带 `NPCSchedule` 标记，源码会再补一次 `checkSchedule(Game1.timeOfDay)`

### 16.3 这对 AI/LLM 控制的意义

如果你想让 LLM 实时控制 NPC 去某地、演某个动作：

- 技术上是可行的
- 更稳的做法是 **把 AI 控制层做成临时接管层**
- 而不是直接篡改所有原生日程逻辑

推荐思路：

1. 保留原版日程系统作为“基础人格/基础生活规律”
2. 短时 AI 指令时挂 `temporaryController`
3. 指令结束后回到原 schedule

这样冲突最少。

## 17. 结合源码，回答几个常见误区

### 17.1 “我把皮埃尔 7 点设置到农场，为什么 7 点没出现在农场？”

因为 `700 Farm x y` 的语义是：

- 7:00 从上一位置出发去 `Farm x y`

不是：

- 7:00 直接生成在农场

要做到“7 点就在农场”，至少有两种方式：

1. 用 `0 Farm x y facing`
   - 这会把当天默认起点改成农场，并在主机端直接 warp 过去
2. 用更早的出发时间，保证他 7 点前走到

### 17.2 “我的路线必须从 NPC 脚下开始画吗？”

如果你是在做 **原版 schedule 语义**：

- 是的，逻辑起点始终来自“上一条 schedule 终点”或“当天默认起点”
- 你不能只在终点地图凭空画一段，期待 NPC 自动从那里接上

如果你是在做 **直接注入 `route` 栈的 mod 语义**：

- 你理论上可以从任意点开始
- 但如果这条 route 的第一点不是 NPC 当前所在位置附近，就很容易卡住或表现异常

### 17.3 “可以在别的场景画路线吗？”

可以，但有前提：

- 起点场景到目标场景之间必须存在合法 warp 链
- 每张地图内目标 tile 必须可达
- 你的采样数据如果跨图，必须同时描述 warp 前后怎么衔接

原版不是“自由世界导航网格”，而是“基于已知 warp/door 网络的跨场景寻路”。

### 17.4 “默认出生”和“自定义出生”有什么区别？

默认出生：

- 用 `defaultMap/defaultPosition/defaultFacingDirection`
- 每天夜里 `dayUpdate()` 会先把 NPC 送回这里

自定义出生：

- 一般指 schedule 里的 `time == 0` 项
- 它会临时改写“今天的默认起点”
- 只影响当天 schedule 解析后的起始位置

因此：

- `defaultMap/defaultPosition` 更像角色基础家/静态出生点
- `0 xxx` 更像“今天这一天的特殊开局位置”

## 18. 对 mod 开发的直接启发

下面这些是和你当前项目最相关的、源码已经明确给出答案的点。

### 18.1 修改 schedule 资源后，为什么进存档前后表现不一样

因为 `getMasterScheduleRawData()` 会把 `Characters/schedules/<NPC名>` 缓存到 `_masterScheduleData`。

如果你运行时改了资源，但不做额外处理：

- 已经加载的 NPC 可能还拿着旧缓存
- 已经解析出的 `Schedule` 也不会自动刷新

可用手段：

- `InvalidateMasterSchedule()`
- 再调用 `TryLoadSchedule()`
- 必要时 `resetForNewDay()` 或手动重建当日 schedule

### 18.2 你不一定要改 XNB

源码已经提供了：

- `TryLoadSchedule(string key, string rawSchedule)`
- `TryLoadSchedule(string key, Dictionary<int, SchedulePathDescription> schedule)`

这意味着 mod 完全可以：

- 不去真的改原始 XNB
- 直接在运行时给 NPC 注入一条原始 schedule 字符串
- 或直接注入解析好的 `Dictionary<int, SchedulePathDescription>`

### 18.3 如果你要做“次日生效”的路径编辑器

最稳妥的方式是：

1. 保存你自己的配置文件
2. 记住是给哪个 NPC、哪个 key 改的
3. 次日或进存档后，用 Harmony/SMAPI 内容注入或直接 `TryLoadSchedule(...)` 覆盖

### 18.4 如果你要做“立即预览”

推荐分两层：

1. **原始 schedule 预览**
   - 重新解析成 `Schedule`
   - 需要处理 `InvalidateMasterSchedule()` 与当前已加载状态
2. **纯采样路线预览**
   - 单独构建 `temporaryController`
   - 不强行伪装成原版 schedule

### 18.5 如果你要做 LLM 控制 NPC 去哪里

最小闭环建议：

1. 让 LLM 只输出结构化意图
   - 例如目标地点、目标 tile、朝向、情绪、动作名
2. 由本地代码校验：
   - 场景是否存在
   - 目标点是否可达
   - 是否需要临时中断当前日程
3. 可达时构造 `temporaryController`
4. 同步调用：
   - `faceTowardFarmerForPeriod()`
   - `doEmote()`
   - 终点消息或对话框

不要一开始就让 LLM 直接生成底层 route 栈。那一层过于脆弱，和地图、warp、碰撞、当前站位高度耦合。

## 19. 给当前项目的建议结论

如果你是为了继续完善这个 mod，下面这套分层会比较稳：

### 19.1 日程编辑层

负责：

- 选择 NPC
- 选择要编辑的 schedule key
- 解析当前 key 的原始脚本
- 编辑时间点、目标位置、朝向、终点行为、特殊语义

### 19.2 采样路线层

负责：

- 只做“预览”和“自定义硬路径”
- 需要自己保证顺序连续、跨图合法
- 最好保存为你自己的格式，不直接等价于原版 schedule 语义

### 19.3 原版 schedule 兼容层

负责：

- 把简单编辑结果转换回原版 schedule 字符串
- 能继续兼容 `GOTO`、`MAIL`、`NOT friendship`、`bed`、`a700`、`time==0`

### 19.4 AI/LLM 控制层

负责：

- 短时接管行为
- 优先用 `temporaryController`
- 不直接破坏当天 schedule 基础结构

## 20. 一句话总结

从源码看，Stardew Valley 的 NPC 行为核心不是“把 NPC 扔到某点”，而是：

**先把 NPC 放回当天起点，按优先级选出今天 schedule，用 schedule 在指定时间生成一段或多段可达路径，再由 `PathFindController` 每帧驱动行走；玩家交互、终点动画、临时控制器、对话缓存则在外层不断覆盖和修饰这个基础流程。**

如果你后续继续做这个 mod，最值得抓住的几个硬点是：

- `time == 0` 才是当天起点语义
- `700` 是出发时间，`a700` 是到达时间
- 路线必须建立在合法 warp 链和可通行 tile 上
- `temporaryController` 是“临时接管 NPC”的最好入口
- `InvalidateMasterSchedule() + TryLoadSchedule(...)` 是运行时刷新 schedule 的关键组合

## 21. 原版跨图 warp 链与 Farm 的特殊性

这一节补充当前项目里最容易误判的一点：原版 NPC 跨图并不是“看到目标地图名就会换图”，而是依赖一条预先计算好的 **location route + tile route + warp 触发链**。

### 21.1 原版跨图的完整流程

入口在 `NPC.pathfindToNextScheduleLocation()`，`StardewValley/NPC.cs:5343-5385`。

原版逻辑大致是：

1. 如果 `startingLocation == endingLocation`
   - 直接在当前地图内调用 `PathFindController.findPathForNPCSchedules()`，从起点 tile 走到终点 tile。
2. 如果是跨地图
   - 先调用 `getLocationRoute(startingLocation, endingLocation)`。
   - 这个方法最终走到 `WarpPathfindingCache.GetLocationRoute(...)`，`StardewValley.Pathfinding/WarpPathfindingCache.cs:45-61`。
3. 拿到一串地图名以后，比如：
   - `BusStop -> Town -> SeedShop`
4. 再逐段执行：
   - 在当前地图里通过 `GameLocation.getWarpPointTo(nextLocation)` 找到通往下一张图的 warp/door tile，`StardewValley/GameLocation.cs:2967-2996`
   - 用 `PathFindController.findPathForNPCSchedules(...)` 在当前地图内先走到这个 warp tile
   - 再用 `GameLocation.getWarpPointTarget(...)` 计算 NPC 穿过这个 warp 后在下一张图的起始 tile，`StardewValley/GameLocation.cs:3001-3035`
5. NPC 实际换图不是 schedule 编译时发生的，而是在运行中的 `PathFindController.handleWarps(...)` 里检测到角色踩到 warp/door 时才触发，`StardewValley.Pathfinding/PathFindController.cs:343-417`

因此，原版跨图成功必须同时满足三件事：

- 地图级 route 存在
- 当前地图里到 warp 点的 tile 路径存在
- 行走过程中真的踩到了 warp 点

任何一层缺失，最终都不会跨图。

### 21.2 为什么 `Farm -> BusStop` 在原版 generic 桥接里经常失败

关键原因在 `WarpPathfindingCache` 的初始化：

- `WarpPathfindingCache.IgnoreLocationNames = new HashSet<string> { "Backwoods", "Cellar", "Farm" }`
- 位置：`StardewValley.Pathfinding/WarpPathfindingCache.cs:148-157`

也就是说，原版全局 location route 缓存 **明确不把 `Farm` 当成普通 NPC 路由节点**。

后果是：

- `WarpPathfindingCache.GetLocationRoute("Farm", "BusStop", gender)` 往往拿不到结果
- `NPC.pathfindToNextScheduleLocation()` 在 `startingLocation != endingLocation` 且 `locationsRoute == null` 时，不会继续帮你兜底生成一条通用跨图链
- 最终返回的 `SchedulePathDescription.route` 为空或无效

这就是为什么：

- 你自定义了 `0 Farm (...)` 作为起点后
- 后续站点写成 `BusStop` 或 `SeedShop`
- 原版 generic schedule 跨图不一定能接上

### 21.3 为什么有时你感觉“NPC 又能从农场走出去”

这通常不意味着原版对 `Farm` 的 generic 跨图完全可用，而是以下情况之一：

1. 当次真正参与桥接的 `startingLocation` 已经不是 `Farm`
   - 例如上一站已经把锚点推进到 `BusStop` 或别的地图。
2. 你看到的是已经挂好的 controller 在继续运行
   - 不是当前这一刻又重新从 `Farm` 调了一次 `pathfindToNextScheduleLocation()`。
3. 走的是原版针对婚后/返家/农场活动的特殊流程
   - 而不是通用 `WarpPathfindingCache`。

原版源码里农场相关确实有专门逻辑，比如：

- `NPC.returnHomeFromFarmPosition(Farm farm)`，`StardewValley/NPC.cs:6221-6247`
- `NPC.arriveAtFarmHouse(FarmHouse farmHouse)`，`StardewValley/NPC.cs:6964-6998`

这进一步说明，`Farm` 本来就不是被设计成“普通 NPC 任意通勤的全局中转点”。

### 21.4 为什么当前项目选择“模组内 Farm 定向桥接”

如果直接把原版 `WarpPathfindingCache` 里的 `Farm` 删出忽略列表，会有两个高风险副作用：

1. 它是全局缓存
   - 会影响所有 NPC，不只是当前被 LLM/编辑器控制的 NPC。
2. `GetLocationRoute(...)` 不是智能选最优
   - 只是返回第一个匹配终点、且性别条件满足的 route，见 `WarpPathfindingCache.cs:45-61`
   - 一旦 `Farm` 加进去，其他 NPC 可能把玩家农场当成普通中转路线。

因此当前项目没有直接 patch 原版全局缓存，而是新增了两层本地服务：

- `Services/ScheduleRouting/FarmLocationRouteResolver.cs`
  - 只在 `Farm` 作为起点或终点时参与
  - 从当前 `Farm` 地图真实存在的 `warps` / `doors` 读取直接可达邻居
- `Services/ScheduleRouting/NpcScheduleRouteBridgeService.cs`
  - 先用上面的本地解析器补 `Farm`
  - 其余部分继续复用原版 `WarpPathfindingCache` 与 `PathFindController.findPathForNPCSchedules(...)`

这个方案的原则是：

- **只修当前项目需要的 `Farm` 场景**
- **不把玩家农场重新变成全村通用中转点**

## 22. 当前项目里“表情”和“行为”到底建立在什么之上

从当前实现看，模组给 LLM 暴露的即时动作并不都是同一类原版能力。至少要区分下面三条：

### 22.1 `DoEmote`：头顶图标表情，不是角色身体动画

当前项目里，`ImmediateNpcDirectiveType.DoEmote` 最终走的是：

- `Services/NpcAgentManager.cs:934-938`
- 调用 `npc.doEmote(directive.EmoteId)`

原版 `Character.doEmote(...)` 在：

- `StardewValley/Character.cs:1058-1068`

它做的事情是：

- 如果 `!isEmoting`
  - 设置 `isEmoting = true`
  - 设置 `currentEmote`
  - 初始化 `currentEmoteFrame`
  - 重置 `emoteInterval`

随后由 `Character.updateEmote(...)` 驱动表情图标帧推进：

- `StardewValley/Character.cs:1082-1122`

这套系统本质上是：

- 头顶冒一个原版 emote icon
- 不是角色本体精灵帧动画
- 也不是 Farmer 那套复杂自定义动作表情系统

#### 22.1.1 原版 emote id 常量

定义在 `StardewValley/Character.cs:25-55`：

- `8 = questionMarkEmote`
- `12 = angryEmote`
- `16 = exclamationEmote`
- `20 = heartEmote`
- `24 = sleepEmote`
- `28 = sadEmote`
- `32 = happyEmote`
- `36 = xEmote`
- `40 = pauseEmote`
- `52 = videoGameEmote`
- `56 = musicNoteEmote`
- `60 = blushEmote`

这点非常重要，因为日志里模型把“开心”多次映射成了 `emote_id = 20`，而原版 `20` 实际上是 **心形**，不是 `happy`。真正的 `happy` 是 `32`。

### 22.2 `PlayEndBehavior`：原版 route end behavior / 精灵动画

当前项目里，`ImmediateNpcDirectiveType.PlayEndBehavior` 最终走的是：

- `Services/NpcAgentManager.cs:1405-1423`
- 调用 `npc.StartActivityRouteEndBehavior(directive.EndBehavior, null)`

原版链路在：

- `NPC.StartActivityRouteEndBehavior(...)`，`StardewValley/NPC.cs:4613-4618`
- `NPC.getRouteEndBehaviorFunction(...)`，`StardewValley/NPC.cs:4621-4649`
- `NPC.loadEndOfRouteBehavior(...)`，`StardewValley/NPC.cs:4652-4678`

这条链和 `DoEmote` 完全不是一回事：

- `DoEmote` 是头顶图标
- `PlayEndBehavior` 是原版路线终点行为
  - 可以是 `square_*`
  - 也可以是动画描述表里定义的 route end animation

所以如果你要的是“原版 NPC 某些身体动作/终点动画”，应优先考虑 `PlayEndBehavior` 这一路，而不是 `DoEmote`。

### 22.3 其它即时动作的原版基础

当前项目的几个即时动作分别建立在这些原版能力上：

- `MoveToTile`
  - 通过 `npc.pathfindToNextScheduleLocation(...)` 生成一条路径
  - 再挂到 `npc.temporaryController`
  - 属于临时接管移动
- `FacePlayer`
  - 通过 `npc.faceTowardFarmerForPeriod(...)`
- `PauseAndWait`
  - 通过 `npc.movementPause` + `faceTowardFarmerForPeriod(...)`
- `PlayEndBehavior`
  - 通过 `StartActivityRouteEndBehavior(...)`
- `DoEmote`
  - 通过 `Character.doEmote(...)`

因此，“行为”这个词在当前项目里其实混了三类东西：

1. 头顶图标表情
2. 原版终点行为/角色动画
3. 临时控制器移动与朝向

后续如果要把工具设计得更稳定，最好在术语上明确拆开，而不要继续都叫“行为”。

## 23. 为什么“展示 10 次开心表情，1 次思考”最后基本没有按预期展示

你提供的日志里，这个现象不是单一原因，而是三个机制叠加出来的。

### 23.1 第一层原因：tool loop 轮数先把请求截断了

日志里这一轮的 router 配置是：

- `max_rounds=10`

对应代码在：

- `Services/NpcLlmRouter.cs:54-100`

router 的行为是：

- 每一轮只能先拿模型回复
- 再执行这一轮返回的工具调用
- 一共最多跑 `maxRounds` 轮

而你的这次日志里，模型执行顺序大致是：

1. `get_today_schedule`
2. `enqueue_immediate_action` 第 1 次
3. `enqueue_immediate_action` 第 2 次
4. `enqueue_immediate_action` 第 3 次
5. `enqueue_immediate_action` 第 4 次
6. `enqueue_immediate_action` 第 5 次
7. `enqueue_immediate_action` 第 6 次
8. `enqueue_immediate_action` 第 7 次
9. `enqueue_immediate_action` 第 8 次
10. `enqueue_immediate_action` 第 9 次

然后就触发了：

- `tool loop 超过 10 轮上限，返回最后一轮结果`

也就是说，这一轮实际上：

- 没有完成你要求的“10 次开心 + 1 次思考”
- 只来得及排进 9 个 `DoEmote`
- 连说话兜底都没来得及走到

因此，从日志层面看，这轮请求本来就已经在“规划阶段”被截断了。

### 23.2 第二层原因：即时动作队列当前是“逐 tick 直接出队”，没有节拍控制

执行入口在：

- `Services/NpcAgentManager.cs:908-950`

当前逻辑是：

- 只要 `InflightTask == null` 且 `ImmediateDirectives.Count > 0`
- 每次 `Update()` 就出队 1 个 directive
- 对 `DoEmote` 没有等待、没有 ack、没有“上一个表情播完再播下一个”的机制

所以一串 `DoEmote` 会在连续 update 中被非常快地消耗掉。

从日志上看就是：

- `开始执行即时动作 DoEmote`
- 连续刷很多次

但这不代表屏幕上真的播出了很多个可分辨的表情。

### 23.3 第三层原因：原版 `doEmote()` 在正在表情时会直接忽略新的调用

这是最关键的一层。

原版 `Character.doEmote(...)` 的入口条件是：

```csharp
if (!this.isEmoting && ...)
{
    ...
}
```

位置：

- `StardewValley/Character.cs:1058-1068`

也就是说：

- 只要 NPC 还处于 `isEmoting == true`
- 后续新的 `doEmote(...)` 调用就会被直接忽略

而一个表情要完整播完，需要 `updateEmote(...)` 经过若干帧推进：

- 前导 20ms 帧推进
- 中段每 250ms 推进
- 最后再 fade out
- 位置：`StardewValley/Character.cs:1082-1122`

因此，当前项目里这 9 个 `DoEmote` 的真实表现更接近于：

1. 第一个 `DoEmote` 成功进入 `isEmoting = true`
2. 随后若干个 `DoEmote` 被快速出队
3. 但因为 NPC 还在表情中，后续调用大多直接 no-op
4. 日志仍然会打印“开始执行即时动作 DoEmote”
   - 因为日志写在真正调用 `npc.doEmote(...)` 之前
   - 不是“原版确认表情已开始”的回执

这就是为什么你从日志看好像执行了很多次，但游戏里并没有真的看到 10 次分开的表情。

### 23.4 为什么“最后一次开心的动作是在对话同时展示的”

这一点由 `NpcAgentManager.Update()` 的调用顺序决定：

- `HoldNpcForConversationIfNeeded(...)`
- `ProcessCompletedRequestIfNeeded(...)`
- `ShowPendingSpeechIfPossible(...)`
- `RefreshConversationPeriodicLock(...)`
- `ExecuteNextDirective(...)`

位置在：

- `Services/NpcAgentManager.cs:188-213`

也就是说，当同一轮请求同时产出了：

- 1 个 `npc_say_to_player`
- 1 个 `DoEmote`

当前 update 中会先：

1. `ShowPendingSpeechIfPossible(...)`
   - 先弹出对话框
2. 然后同一帧或紧随其后的下一次 update
   - `ExecuteNextDirective(...)`
   - 开始执行 `DoEmote`

所以你看到的现象会像是：

- 对话弹出来的同时
- NPC 头顶表情也冒出来了

这不是巧合，而是当前 update 顺序的直接结果。

### 23.5 为什么“展示生气”那次看起来更像是正常工作的

你日志里“展示生气”这轮只排了：

- 1 个 `npc_say_to_player`
- 1 个 `DoEmote(12)`

工具数量少，没有在 tool loop 里被截断；而且只排了单个 `DoEmote`，不会出现“同类表情在极短时间内被后续队列反复覆盖/忽略”的问题。

所以它更接近当前实现能稳定承载的上限：

- 单次对话
- 外加一个单独的图标表情

### 23.6 当前实现下，这个问题的直接结论

当前项目里，`DoEmote` 更像：

- “立刻尝试触发一个原版头顶图标”

而不是：

- “排程播放一串有节拍、有确认、有间隔的表情序列”

因此：

- 单个 `DoEmote` 基本可用
- 多个连续 `DoEmote` 现在不可靠
- “10 次开心再 1 次思考”这类命令，本质上已经超出了当前即时动作队列的语义承载能力

## 24. 对后续实现的建议

如果要让 LLM 真正稳定地“连续做 10 次表情、再做 1 次思考”，建议后续按下面方向改：

### 24.1 不再让模型直接传裸 `emote_id`

更稳的方式是本地提供受控枚举，例如：

- `happy`
- `heart`
- `angry`
- `question`
- `pause`

由本地代码再映射成原版常量值。

这样至少不会再出现：

- 模型嘴上说“开心”
- 实际传 `20`（heart）

### 24.2 队列里需要“等待上一动作完成”的节拍器

对于 `DoEmote`，至少要增加：

- 当前 NPC 是否仍在 `isEmoting`
- 若还在表情中，则不要立刻消费下一条同类指令

否则队列只是“日志上出队成功”，不是“画面上逐条播完”。

### 24.3 表情序列最好提升成一个高层工具

与其让模型调用 11 次 `enqueue_immediate_action`，更合理的是：

- 提供一个 `enqueue_emote_sequence`
- 参数里包含序列与次数
- 本地代码负责定时播放

这样可以同时规避：

- tool loop 轮数上限
- 多轮工具调用耗时过长
- 表情重复触发被原版 `isEmoting` 直接忽略

### 24.4 “原版角色身体动画”和“头顶表情”要继续分开建模

后续如果你要做更像 Stardew 原版 NPC 的表现系统，建议明确分成：

1. **头顶图标**
   - 走 `DoEmote`
2. **原版终点行为 / 身体动画**
   - 走 `PlayEndBehavior`
3. **玩家对话期的短时 pose / 朝向 / 停顿**
   - 走 `FacePlayer` + `PauseAndWait`
4. **连续动作编排**
   - 本地状态机或序列执行器

不要再把这几种东西都混在一个“行为”概念里，否则 LLM 很容易做出语义正确、但底层机制完全不匹配的调用。

## 25. 当前版本已落地的修复与补充结论

上面第 24 节写的是设计建议；当前代码里，这一轮已经把其中最关键的几项真正落地了。这里补一份“实现后”的技术结论，方便后面继续查问题时直接对照源码。

### 25.1 表情控制已经改成“受控语义名 -> 本地映射”

之前的问题之一，是模型可以直接传裸 `emote_id`。这会导致两个风险：

- 模型语义和原版常量不一致
- 后期你想统一替换、屏蔽、扩展某类表情时，没有本地收口点

现在已经改成由本地提供受控枚举，再由代码映射到原版常量：

- `happy`
- `heart`
- `angry`
- `question`
- `pause`

实现位置：

- `Models/NpcLlmModels.cs`
  - `NpcEmoteCatalog`
- `Services/NpcLlmToolService.cs`
  - `BuildDirectiveSchema()`
  - `EnqueueImmediateAction(...)`

也就是说，现在模型不能再稳定地依赖“随便写一个整数 emote id”，而是必须走本地定义过的语义名。这样后续如果你想把“开心”从一个头顶图标换成另一个图标，改本地映射即可，不需要重新教模型一遍底层常量。

### 25.2 连续表情不再靠模型手搓 10 次工具调用

之前“展示 10 次开心，1 次思考”失败，不是模型没有理解中文，而是当前架构下：

- 每次 `enqueue_immediate_action` 只是把一条指令排进本地队列
- 队列的消费速度和原版 `isEmoting` 状态并不同步
- tool loop 还有轮数上限

因此模型就算连续发出 10 次 `DoEmote`，日志上看起来像“都入队了”，也不代表画面上真的逐条播完。

现在已经新增高层工具：

- `enqueue_emote_sequence`

它的语义是：

- 模型只描述序列
- 本地把序列展开成即时指令
- 本地负责等待上一条表情或停顿结束，再继续下一条

这样连续表情的责任就从模型转回到本地执行器，不再要求模型在多轮 tool loop 里手工维持节拍。

### 25.3 即时动作队列现在有了“节拍器”

这一点是解决“日志里出队成功，但画面没播完”的关键。

现在运行态里新增了两个重要字段：

- `NextImmediateDirectiveNotBeforeUtc`
- `ActiveImmediateDirectiveSummary`

执行逻辑也从“看到队列就立刻 `Dequeue()`”改成：

1. 先 `Peek()` 当前队头
2. 先判断能不能执行
3. 只有能执行时才真正 `Dequeue()`

约束条件包括：

- 如果当前时间还没到 `NextImmediateDirectiveNotBeforeUtc`，不消费下一条
- 如果当前要播的是 `DoEmote`，但 `npc.isEmoting == true`，不消费下一条
- 如果当前要播的是 `PauseAndWait`，但 `npc.movementPause > 0`，不消费下一条

这意味着队列终于开始对“前一个动作是否真的结束”负责，而不是只对“我有没有把命令写进日志”负责。

所以你之前看到的现象：

- 连续 9 条 `DoEmote`
- 日志全部写了“开始执行即时动作 DoEmote”
- 但游戏里没有逐个显示

本质就是旧实现缺少节拍控制。现在这部分已经补上。

### 25.4 为什么之前的 10 次表情大多数没有展示

结合你给的日志，旧问题可以拆成四层：

1. 模型把“开心”理解成一连串同类动作
2. tool loop 一轮一轮不断追加 `enqueue_immediate_action`
3. 本地队列在旧实现中没有充分等待前一条表情彻底结束
4. 原版 `NPC.doEmote(...)` 只是“尝试触发一个当前表情状态”，不是内建的“可靠序列播放器”

于是最终结果就是：

- 第一部分表情可能被覆盖、忽略或根本没来得及在画面上稳定呈现
- 最后那次“开心”之所以你看到了，是因为它和一条 `npc_say_to_player` 一起被处理，正好落在对白弹窗出现前后的那个可观察窗口里

换句话说，问题不在于“DeepSeek 不会数数”，而在于旧版本地执行器根本没有给“连续表情序列”提供可靠的承载语义。

### 25.5 `npc_say_to_player` 延迟弹出的真正原因

这个延迟主要不是 Stardew 原版“对白系统天生就慢”，而是当前 mod 的执行顺序决定的。

`NpcAgentManager.Update()` 里的顺序是：

1. `HoldNpcForConversationIfNeeded(...)`
2. `ProcessCompletedRequestIfNeeded(...)`
3. `ShowPendingSpeechIfPossible(...)`
4. `RefreshConversationPeriodicLock(...)`
5. `ExecuteNextDirective(...)`

关键点在于：

- `npc_say_to_player` 并不会在模型调用工具的那一刻立刻弹窗
- 它先只是变成一条本地 directive
- 要等整轮请求结束、结果被 `ProcessCompletedRequestIfNeeded(...)` 落地后
- 下一步 `ShowPendingSpeechIfPossible(...)` 才真正调用 `Game1.DrawDialogue(...)`

因此你在后台日志里看到：

- tool 已经执行 `npc_say_to_player`

并不等于：

- 游戏界面此刻已经弹出对话

两者中间至少隔着：

- 整个 tool loop 剩余轮次
- 本地请求收尾
- UI 菜单状态检查

现在已经额外补了一个小优化：

- 如果当前打开的是 `NpcChatPromptMenu`
- `ShowPendingSpeechIfPossible(...)` 会先主动关闭它，再尝试弹出对白

这能消掉一类“输入框还占着菜单，导致对白继续等”的延迟，但它只能算缓解，不是彻底重构。

### 25.6 为什么说对白延迟主要是本 mod 流程问题，而不是原版机制问题

原版对白真正显示的核心调用仍然很直接：

- `Game1.DrawDialogue(...)`

真正把时间拖长的是前面的 LLM 与工具链：

- 先做 embedding
- 再做 memory search
- 再做多轮 tool loop
- 直到模型 finally 不再继续调工具
- 本地才把 `SpeakToPlayer` 这类 directive 统一落地

所以如果模型在已经决定要回答之后，还继续：

- 查 runtime
- 查 schedule
- 查记忆
- 再补无关工具调用

对白就会继续往后拖。

也正因为如此，当前 system prompt 已经补了新的明确约束：

- 如果已经调用 `npc_say_to_player`
- 且没有别的必要动作
- 就应立刻结束本轮

这条约束不是“优化 prompt 文案而已”，而是在现有架构下直接减少对白可感知延迟。

### 25.7 运行时上下文这次也扩展了，目的就是减少 LLM 乱改当前 schedule

之前模型改 schedule 时，一个典型问题是：

- 它只知道“今天的规则长什么样”
- 但不知道 NPC 此刻到底是不是已经在走当前这段路
- 也不知道当前 controller、当前表情、临时控制器、对话锁、等待状态等运行信息

于是它会做出两类高风险操作：

1. 在 NPC 正在执行某段路径时，直接把那一段删掉或重写
2. 不知道 NPC 当前已经在移动/停顿/表情中，又重复下发会互相打架的动作

这次新加的 runtime snapshot 至少把以下信息建模进去了：

- 当前位置、朝向、是否在移动
- 当前是否 `isEmoting`
- 当前表情 id 与受控语义名
- `movementPause`
- `ignoreScheduleToday`
- `currentScheduleDelay`
- schedule controller / temporary controller
- 当前站、下一站、`SafeMutationTime`
- 对话等待、待显示 speech 数量、待执行 directive 数量

然后 schedule 相关工具又引入了：

- `allow_interrupt_current_schedule`

默认情况下，如果 LLM 想改写的时间点早于当前安全边界，就会被本地自动夹到：

- `SafeMutationTime`

也就是：

- 尽量别去篡改正在执行的那一段
- 优先从下一段或者安全时间点以后改

这一步很重要，因为“当前 NPC 走到哪了”和“schedule 文本上写了什么”并不是一回事。只看 schedule 文本而忽略 controller 运行态，就很容易出现：

- 文本上已经改成去 `BugTop`
- 但角色实际 controller 仍沿着旧路径或旧地图继续跑

### 25.8 这和前面的 warp / Farm 桥接分析是同一个问题链条

前面第 21 节已经分析过：

- 原版跨图不是“只要改了目标地图名就一定会到”
- 它依赖 location route、当前地图内 tile 路径、warp 触发链三层同时成立
- `Farm` 又不在原版通用 location route 缓存的普通节点里

所以从 LLM 侧看，一个“修改站点”的动作，实际会同时撞上两类风险：

1. **运行时风险**
   - NPC 也许已经在执行当前段 controller
   - 这时强改当前 schedule，会造成“文本改了，当前 controller 没同步”
2. **跨图桥接风险**
   - 就算 schedule 文本改成功
   - 如果新旧锚点之间的跨图链不成立，NPC 仍然不会按你以为的方式过图

这也是为什么当前项目必须同时做两件事：

- 在运行时给 LLM 更多 controller / schedule 执行态信息
- 在路径系统里对 `Farm` 做模组内定向桥接，而不是幻想原版 generic cache 会自动兜底

因此，“NPC 9 点应该去 BugTop，结果时间到了人还在 Farm，只是坐标像对了”这类 bug，通常不是单点故障，而是：

- 当前执行段被错误改写
- 再叠加跨图桥接失败

两边一起看，问题才讲得通。

---

## 26. 2026-04-07 即时反馈队列与动作分流重构

这一节覆盖前文若干“旧术语”。

前面一些章节里提到的：

- `ImmediateNpcDirective`
- `ImmediateNpcDirectiveType`
- `ImmediateDirectives`
- `PendingSpeeches`
- `ActiveImmediateDirectiveSummary`

现在都已经不是当前代码里的正式命名了。

之所以要改，不是为了好看，而是因为旧名字会误导人以为：

- 只要工具叫“即时动作”
- 它就一定在 tool 执行当下立刻碰游戏对象

实际上旧实现里并不是这样。

旧链路是：

1. tool 先把 directive 写进请求上下文
2. 等整轮 tool loop 结束
3. 再由请求收尾阶段统一转进运行时队列
4. 最后主线程下一轮 `Update()` 才慢慢执行

所以“工具已经成功调用 `npc_say_to_player`”和“玩家已经在游戏里看见对白”之间，本来就隔着至少一层整轮请求收尾。

### 26.1 新术语

当前代码里的动作/反馈链已经拆成四层：

- `NpcActionRequest`
  - LLM tool 产出的“动作请求”
  - 只描述想做什么，不直接代表已经触碰游戏对象
- `ImmediateFeedbackQueue`
  - tool loop 期间由异步请求线程投递
  - 这里只放纯数据事件，不直接操作 `Game1` / `NPC`
- `SpeechDisplayQueue`
  - 主线程侧等待真正弹对话框的对白队列
- `RealtimeActionQueue`
  - 主线程侧、允许在请求尚未完全结束时就开始消费的轻量动作队列
- `DeferredActionQueue`
  - 仍然保留到请求完成后再提交的动作队列

对应地，旧的 `ImmediateDirectives` 概念已经被拆成：

- “即时反馈事件”
- “实时动作”
- “延迟动作”

三者不再混用。

### 26.2 为什么不能按“起后台线程直接消费事件”来做

这次实现没有采用“在 `HoldNpcForConversationIfNeeded(...)` 前开一个线程直接执行事件”的方案。

原因很简单：

- 当前项目里其实已经没有这个名字的函数
- 等价位置是 agent 主循环 `Update()` 里的对话/动作处理段
- 更关键的是，`Game1.DrawDialogue`、`npc.doEmote(...)`、`npc.faceTowardFarmerForPeriod(...)`、controller 切换这类操作都不应该在后台线程直接碰

所以现在的方案是：

- 异步请求线程只负责把 `NpcImmediateFeedbackEvent` 塞进 `ImmediateFeedbackQueue`
- 真正的对白弹窗、表情、朝向、停顿，仍然只在主线程 drain 时执行

这样既避免跨线程直接碰游戏对象，也不会把 tool loop 卡死在“等主线程动作完成”上。

### 26.3 tool 现在也有本地类型建模

`AiToolDefinition` 现在除了名称、描述、输入 schema 以外，还多了两类本地元数据：

- `ToolKind`
  - `Query`
  - `Mutation`
  - `ActionRequest`
- `DispatchPolicy`
  - `None`
  - `Immediate`
  - `Deferred`
  - `Mixed`

这两个字段不会传给模型作为 API 协议的一部分，它们是本地运行时路由信息。

当前大致分类是：

- 查询类工具
  - `get_npc_profile`
  - `get_recent_memories`
  - `search_memories`
  - `get_today_schedule`
  - `get_runtime_state`
- 立即本地状态修改
  - `memory_update`
- 请求完成后再提交
  - `replace_future_schedule`
  - `insert_schedule_stops`
  - `update_schedule_stops`
  - `remove_schedule_stops`
  - `replace_entire_schedule`
- 动作请求工具
  - `enqueue_immediate_action`
  - `enqueue_emote_sequence`
  - `npc_say_to_player`

其中：

- `npc_say_to_player` 是 `Immediate`
- `enqueue_emote_sequence` 是 `Immediate`
- `enqueue_immediate_action` 是 `Mixed`

之所以 `enqueue_immediate_action` 是 `Mixed`，是因为它只是“提交动作请求”的工具，而不是所有动作都必须走同一条时序。

### 26.4 动作请求现在由本地自动分流

LLM 现在只需要提交：

- 要说什么
- 要做什么动作
- 目标位置/表情/持续时间等参数

它不需要再手工推测本地应该走哪条队列。

当前默认分流策略是：

- `SpeakToPlayer` -> `ImmediateFeedback`
- `DoEmote` -> `ImmediateFeedback`
- `FacePlayer` -> `ImmediateFeedback`
- `PauseAndWait` -> `ImmediateFeedback`
- `MoveToTile` -> `DeferredCommit`
- `PlayEndBehavior` -> `DeferredCommit`

也就是说：

- 对白、表情、朝向、短暂停顿
  - 优先尽快让玩家看到
- 改 controller、切移动目标、触发 end behavior
  - 默认等整轮请求结束后再提交

这一步就是为了避免“一个看起来只是轻量反馈的 tool”顺手在 tool loop 中途直接把 NPC 当前 controller 改掉。

### 26.5 当前真实执行链

以 `npc_say_to_player` 为例，现在链路变成：

1. provider 返回 tool call
2. `NpcLlmToolService` 解析参数，产出 `NpcActionRequest`
3. `NpcToolExecutionContext.EnqueueActionRequest(...)` 根据动作类型决定默认 `DispatchMode`
4. 如果是即时反馈类动作，就生成 `NpcImmediateFeedbackEvent`
5. 异步请求线程仅把事件塞进 `ImmediateFeedbackQueue`
6. 主线程 `Update()` 每帧先 drain 这条队列
7. 若是说话，则转进 `SpeechDisplayQueue`
8. `ShowPendingSpeechIfPossible(...)` 再真正 `Game1.DrawDialogue(...)`

于是现在的时序变成：

- 不必再等整个 tool loop 完全收尾
- 只要 tool 已经产出对白请求
- 主线程下一帧就可以开始显示

同理，表情序列现在会先转进：

- `RealtimeActionQueue`

然后由主线程动作执行器按节拍消耗，而不是仍然全部压到“请求结束以后”。

### 26.6 为什么这能解决“日志里 tool 已经成功，但游戏里反馈还慢半拍”

因为这次真正切断了旧的强耦合点：

- 旧实现里，`npc_say_to_player` 只是往请求上下文里塞数据
- 新实现里，`npc_say_to_player` 可以在 tool loop 期间直接产出“待主线程落地”的即时反馈事件

要注意：

- 这不代表 tool loop 本身一定立刻结束
- 只是即时反馈不再被“整轮请求收尾”硬性阻塞

所以现在的延迟被拆成两部分：

1. 模型多久产出第一个正确的动作请求
2. 主线程下一帧多久把它从即时反馈队列 drain 出来

而不是过去那样还要再叠一层：

3. 整个 tool loop 何时完全结束

### 26.7 仍然保留的约束

这次重构并没有把所有东西都变成“真正流式执行”。

当前仍然保留三条硬边界：

1. schedule patch 仍然是“请求完成后再提交”
2. `MoveToTile` / `PlayEndBehavior` 默认仍是延迟动作
3. 即时反馈虽可提前显示，但不会自动强制结束 router 的 tool loop

所以 prompt 里仍然保留这条约束：

- 如果已经调用 `npc_say_to_player`
- 且没有别的必要动作
- 就应立刻结束本轮

这条规则仍然必要，因为：

- 即时反馈链解决的是“本地别再白等整轮请求收尾”
- 不是“允许模型无限多跑几轮无关工具”

### 26.8 对调试和日志的直接好处

现在控制台/运行时快照里能更清楚地区分：

- `speech_display_queue`
- `immediate_feedback_events`
- `realtime_actions`
- `deferred_actions`

这样以后再看日志时，就不会再把：

- “工具已经提交了动作请求”
- “主线程已经接到了即时反馈事件”
- “动作已经真正执行”

三件事混成一件。

这对排查下面这类问题尤其重要：

- tool 成功了，但对白为什么还没弹
- 表情为什么已经进队了，却还没播
- 哪些动作是即时反馈，哪些动作是请求完成后才会落地

---

## 27. 2026-04-07 tool loop 提示词工程与 reason 可视化

这次又补了一轮，不是新增工具，而是约束模型：

- 少做“为了完整而完整”的工具查询
- 每次调用工具都要说明理由
- 对话链结束时把工具调用理由完整打出来

### 27.1 为什么要专门压缩 tool loop

当前项目里，真正拖慢体感的往往不是单次 HTTP，而是：

1. 先查一个工具
2. 再进下一轮 tool loop
3. 模型继续补查
4. 再进下一轮

对于复杂任务，这样做是合理的。

但对于简单任务，例如：

- “8:30 去某地”
- “9:20 插一个站点，别影响后面”
- “确认一下我喜欢什么”

如果模型还按“先把上下文补齐到最完整”来做，就会出现：

- 方案本身不复杂
- 但多查了几轮工具
- 玩家体感上像过了很久才回复

所以现在 prompt 明确告诉模型：

- 一次额外工具调用，再加一轮新的模型思考
- 通常就可能额外吃掉大约 20 到 30 分钟游戏内时间

这个数字不是硬精度计时器，而是给模型的决策权重。

目的就是逼它在简单任务上做取舍，而不是默认“多查总比少查安全”。

### 27.2 当前时间表不再只给文本摘要

之前 prompt 里已经有：

- 当前 schedule 摘要

但这还不够强，因为模型仍会倾向于把它当“人类可读摘要”，然后再去查一次 `get_today_schedule`。

现在 prompt 里除了：

- 文本摘要

还直接附带了：

- 当前完整时间表的结构化 JSON 明细

也就是把：

- 起点
- 各 stop 的 index
- time
- time_mode
- location
- target tile
- facing
- end behavior/message

直接放进 prompt。

这意味着对于很多简单修改任务，模型理论上已经有足够信息可以直接决定：

- 插到哪个位置
- 后续是否需要改
- 是否还值得再查 `get_today_schedule`

### 27.3 prompt 里新增的取舍原则

现在 system prompt 明确加入了这些约束：

- 简单任务默认不要为了“更完整”而继续补查工具
- 能 0 次额外查询就不要查
- 必须查时尽量控制在 1 次
- 只有缺关键字段、关键索引或冲突信息时才继续下一轮
- 记忆查询不是默认动作
- 只有当任务真的依赖长期偏好、当天状态、纠正历史或人物关系时才查记忆

所以以后如果仍出现“简单改单点却查很多轮”的情况，那就不再是“prompt 没说”，而是：

- 模型没有遵守 prompt
- 或者当前工具设计仍让它觉得缺关键字段

这两种情况就可以继续定向优化，而不是盲目再加大上下文。

### 27.4 所有 tool 现在都允许带 `reason`

之前并不是每个 tool 的 schema 都显式带 `reason`。

现在至少在 tool 顶层，查询类也统一支持：

- `reason`

例如：

- `get_npc_profile`
- `get_recent_memories`
- `search_memories`
- `get_today_schedule`
- `get_runtime_state`

动作类和修改类原本大多已经支持，这次则统一到“所有工具都能表达调用动机”。

对 `enqueue_immediate_action` 还额外做了兼容：

- 先看顶层 `reason`
- 没有时再回退到 `action.reason`

这样即使模型还沿用旧写法，也不会丢失理由。

### 27.5 reason 不再只藏在原始 args 里

以前虽然有些工具参数里已经带了 `reason`，但如果要快速看一轮请求做了什么，仍然得：

- 去翻每条 `args=...`
- 自己肉眼找 `reason`

现在 tool 执行时会直接提取 reason，并格式化成统一摘要，例如：

- `search_memories [Query/None] reason=需要确认玩家长期偏好`

然后这份摘要会进入：

- 本轮请求的 tool summary
- 运行时最近 tool 调用列表
- debug 记录

于是“它为什么调这个工具”第一次变成了显式的一等信息，而不是散落在原始 JSON 参数里的隐含字段。

### 27.6 每轮请求结束时的输出也更完整

现在请求结束不再只打一条：

- `本轮工具调用：A, B, C`

而是会输出：

- 调用总数
- 每条工具调用的顺序摘要
- 每条摘要里包含 tool 名、tool kind、dispatch policy、reason

这样你看日志时可以直接回答：

- 它用了哪些工具
- 每个工具是查询、修改还是动作请求
- 为什么它觉得这一步非调不可

这对排查“为什么一个简单任务被它做复杂了”尤其有用。

因为现在你可以直接看到：

- 它是不是为了查记忆而多绕了一轮
- 它是不是已经有 schedule 明细却还重复查 `get_today_schedule`
- 它是不是在 `npc_say_to_player` 之后还继续查了无关工具

## 28. 每轮 tool loop 重采样与结构化可见元数据

这次又补了一个很关键的缺口：

- 以前 prompt 虽然已经很长
- 但它是“请求开始时一次性冻结”的

也就是说，一次请求里如果 tool loop 跑了 3 到 5 轮：

- 时间可能已经继续流逝
- NPC 可能已经换了地图、换了朝向、开始或结束走路
- 玩家也可能已经不在同一张地图
- working schedule 甚至已经在前一轮被 schedule tool 改过

但旧实现里：

- system prompt 仍然用第一轮开头那份快照
- `get_runtime_state` 也只是把请求开始时的 `RuntimeSummary` 原样返回

这会带来两个问题：

- 模型后几轮仍在拿旧现场继续推理
- 它会把“刚才改过的 working schedule”和“游戏此刻真实运行态”混在一起

### 28.1 新增了结构化 prompt 采样模型

现在新增了 [`Models/NpcLlm/PromptContextModels.cs`](/mnt/d/Oherfile/cache/VisualStudio/project/console/StardewMod/Models/NpcLlm/PromptContextModels.cs)。

核心模型是：

- `NpcAgentPromptSnapshot`

它不再只塞一个难维护的 `WorldSummary` 字符串，而是拆成：

- `Temporal`
- `Weather`
- `Festival`
- `Npc`
- `Farmer`
- `Relationship`

这样后面继续扩字段时，就不需要再把一堆 `key=value` 文本硬拼进 prompt。

### 28.2 农夫信息现在按“NPC 是否看得见”建模

[`Services/NpcAgent/NpcAgentManager.PromptSampling.cs`](/mnt/d/Oherfile/cache/VisualStudio/project/console/StardewMod/Services/NpcAgent/NpcAgentManager.PromptSampling.cs) 里新增了 `BuildVisibleFarmerMetadata(...)`。

现在会先判断：

- NPC 和农夫是否在同一张地图

如果不在同图，就不会继续把这些视觉信息伪装成“NPC 已观察到”：

- 农夫地图
- 朝向
- 手持物
- 当前工具
- 体力
- 状态效果

这时只会给出：

- `IsVisibleToNpc=false`
- `IsSameMap=false`
- `VisibilityNote=farmer_not_visible_to_npc_because_different_map`

如果在同图，才会附带：

- 农夫朝向
- 手持物
- 当前工具
- 体力与最大体力
- 当前可见 buff / debuff

这样模型至少不会在“根本不在同图”的情况下，继续编造玩家手里拿着什么。

### 28.3 天气和节日不再靠零散硬编码拼进去

这次也把公共环境信息拆清楚了。

天气部分现在显式建模：

- 当前天气种类
- 明日天气种类
- `isRaining`
- `isSnowing`
- `isLightning`
- `isDebrisWeather`
- `isGreenRain`
- `weatherIcon`

节日部分则区分：

- active festival
- passive festival

并尽量给出：

- `FestivalType`
- `FestivalId`
- `FestivalName`
- `FestivalLocationName`
- `StartTime`
- `EndTime`
- `IsFestivalOpenNow`

active festival 走：

- `Utility.isFestivalDay(...)`
- `Event.tryToLoadFestivalData(...)`

passive festival 走：

- `Utility.TryGetPassiveFestivalDataForDay(...)`
- `Utility.IsPassiveFestivalOpen(...)`

所以后面如果你还想继续把“节日当前是否已开场”“第几天的被动节日”之类信息扩进去，就有明确落点了。

### 28.4 每一轮都会重新采样，而不是只在请求开始时采一次

旧实现里 `RunToolLoopAsync(...)` 直接拿一个固定 `systemPrompt` 字符串。

现在改成：

- router 每一轮都会调用 `systemPromptFactory`

也就是每次进入新一轮 tool loop 前，都会重新执行：

- `context.RefreshLiveSampling(promptRound)`

这一步会刷新：

- 当前时间
- 当前结构化环境元数据
- 当前 `working schedule` 摘要
- 当前 `working schedule` 结构化明细
- 当前真实运行态 `RuntimeSummary`

所以现在 system prompt 里的“当前轮上下文”是真正按轮更新的，不再是请求头那一份老样本。

### 28.5 `working schedule` 和 `runtime state` 的语义现在被明确区分

这点非常重要。

现在 prompt 里明确区分了两类真相：

- `working schedule`
- `runtime state`

其中：

- `working schedule` 是当前 tool loop 内部继续修改用的基准
- `runtime state` 是游戏此刻真实 NPC 运行状态

这两者在一次请求里不一定完全相同。

例如：

- 你在第 2 轮已经调用了 `insert_schedule_stops`
- 但 patch 还没在请求结束前真正落地到游戏里

那么：

- 下一轮 prompt 里的 `working schedule` 应该已经是改后的版本
- 但 `runtime state` 仍可能反映当前游戏里还没切过去的旧执行态

旧实现最容易把这两层混掉，这次算是把边界立住了。

### 28.6 `get_runtime_state` 现在真的是“实时”的

以前 `get_runtime_state` 返回的其实不是 live state，而是：

- 请求启动时塞进 `NpcToolExecutionContext.RuntimeSummary` 的冻结副本

这会导致一个很隐蔽的问题：

- 模型以为自己查询了“当前运行态”
- 其实拿到的还是几轮之前的旧状态

现在 [`Services/NpcLlmTools/NpcLlmToolService.cs`](/mnt/d/Oherfile/cache/VisualStudio/project/console/StardewMod/Services/NpcLlmTools/NpcLlmToolService.cs) 里会在实际执行 `get_runtime_state` 前再次触发：

- `context.RefreshLiveSampling(...)`

然后返回刷新后的 `RuntimeSummary`。

这意味着从语义上讲，`get_runtime_state` 终于和它的名字一致了。

### 28.7 对 prompt engineering 的直接影响

这次不是单纯“多塞一点上下文”。

真正变化是：

- prompt 明确告诉模型每轮上下文都会重采样
- 明确告诉模型发生冲突时以当前轮采样为准
- 明确告诉模型 `Farmer.IsVisibleToNpc=false` 时不要继续编造玩家可见状态
- 明确告诉模型 `working schedule` 才是本次请求内部继续修改的基准

这样后面如果还出现：

- 明明玩家已经换图了，模型还在用旧地图推理
- 明明同轮已经改过 schedule，它还按旧 stop index 继续动刀
- 明明不在同图，它还说“我看见你手里拿着锄头”

那就不是“采样机制没提供”，而是模型没有遵守当前 prompt 语义。

### 28.8 `periodic_tick` 现在受原版 `Game1.shouldTimePass()` 统一闸门控制

这次把“是否允许发起周期任务”的暂停判定，收口到了原版：

- `Game1.shouldTimePass()`

也就是说，下面这类典型暂停场景现在都会直接挡住 `periodic_tick`：

- 菜单打开
- 原版事件中
- 各类 overlay / clickable menu 占用
- `isTimePaused`
- 原版 pause / freeze controls

行为上变成：

1. 周期时间到了
2. 先看当前是否还有 inflight / pending / 对话锁
3. 再看 `Game1.shouldTimePass()`
4. 如果原版认为时间不该流逝，这轮就不发 `periodic_tick`

这样做的直接好处是：

- 不再维护一套和原版近似但可能漂移的暂停判断
- LLM 周期行为和游戏真实“时间是否流动”语义对齐

### 28.9 暂停跳过周期时，会触发“全局每天一次”的 `day_idle` 批处理

新增了一条系统维护支线：

- `day_idle`

它不是玩家交互，也不是现场行为决策，而是：

- 每天一次的后台事实整理任务

触发方式不是“到点就跑”，而是：

- 当天第一次出现“周期已经到期，但 `Game1.shouldTimePass()==false`”时
- manager 会为所有“已启用且 provider 可用”的 NPC 各排一条 `day_idle`

这里有几个关键点：

- 这是全局每天一次，不是每个 NPC 各自独立触发多次
- 同一天再次进入暂停跳过，不会重复整批排队
- `day_started` 会重置这组协调状态

这套协调状态现在由 manager 内存字段维护：

- `LastDayIdleGameDate`
- `DayIdleBatchQueuedForDate`
- `DayIdleBatchCompletedForDate`
- `pendingGlobalDayIdleNpcs`

所以 `day_idle` 更像：

- “今天的系统维护批次”

而不是普通行为事件。

### 28.10 `day_idle` 可以脱离激活时间窗继续跑，但不允许世界副作用

普通请求仍然受激活时间窗控制。

但 `day_idle` 是系统维护任务，所以现在 manager 会额外识别：

- 当前 NPC 是否仍有待完成的维护任务

只要这条维护任务还没完成，即使 NPC 已经离开 active window，主循环也不会完全跳过它。

不过这个“越过时间窗”只对后台整理生效，不代表它还能继续改世界。

在 `RunAgentRequestAsync(...)` 里，`day_idle` 会强制关闭：

- `AllowSpeech = false`
- `AllowBehaviorControl = false`
- `AllowScheduleControl = false`

所以它可以在暂停期间后台跑，但只能做：

- facts 整理

不能做：

- 对话
- 动作
- schedule 修改

### 28.11 prompt 现在正式拆成“人格档案”与“基础资料”两层

之前 prompt 里的静态角色信息比较散：

- 一部分来自 `profile.json`
- 一部分写死在 prompt 语义里

现在这层被重构成两个来源：

1. `NPC 人格档案`
2. `基础资料`

其中：

- 人格档案是高权重输入
- 基础资料是原版派生的客观资料

人格档案来源优先级是：

1. `Personality/<NpcName>/<NpcName>.md`
2. 如果文件缺失、为空或解析失败，则回退到代码内生成的 fallback

fallback 不是空白，而是基于原版 NPC 数据即时生成的基础人格，因此：

- 没有人格文件时系统也不会崩
- 只是“玩家手写人格”的优先级更高

Markdown 解析目前支持这些章节：

- `名字`
- `性别`
- `说话方式`
- `做事方式`
- `娱乐方式`
- `爱好`
- `讨厌`
- `喜欢`
- `秘密`
- `思考方式`

实现策略不是严格 schema，而是：

- 宽松提取结构化字段
- 同时保留整份 raw markdown

所以 prompt 里现在可以直接拿到：

- `personality_source=file|fallback`
- 结构化人格字段
- 原始 Markdown 正文

### 28.12 人格会影响 loop 倾向，但不能越权

这次的人格系统不是“装饰文本”。

它的设计目标很明确：

- 影响说话方式
- 影响做事风格
- 影响对玩家/其他 NPC 的配合度
- 影响是否值得多跑几轮工具
- 影响是否倾向于简短拒绝或少查一轮记忆

但边界也被写死了：

- 不能绕过系统规则
- 不能绕过工具权限
- 不能忽略安全约束
- 不能在必须回复时直接不回复

也就是说，人格只影响“怎么做选择”，不影响“有没有资格越过底线”。

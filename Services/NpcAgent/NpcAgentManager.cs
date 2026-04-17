using System.Diagnostics;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewMod.Models;
using StardewMod.Services.Memory;
using StardewMod.Ui;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Pathfinding;
using SObject = StardewValley.Object;

namespace StardewMod.Services;

/// <summary>
/// NPC Agent 的主控器。这里负责激活时间窗、发起 LLM 请求、应用 patch、执行即时动作和记录记忆。
/// </summary>
internal sealed partial class NpcAgentManager
{
    private const string SaveDataKey = "npc-llm-agent-data";
    private readonly ModConfig modConfig;
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly NpcScheduleEditorService scheduleEditorService;
    private readonly NpcLlmConfigService configService;
    private readonly NpcLlmRouter router;
    private readonly NpcLlmMemoryStore memoryStore;
    private readonly NpcLlmFactStore factStore;
    private readonly NpcPersonalityService personalityService;
    private readonly NpcLlmToolService toolService;
    private readonly NpcLlmConsoleLogger logger;
    private readonly Dictionary<string, NpcAgentRuntimeState> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NpcSyncPairRuntimeState> syncPairStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NpcPerceptionNeighborhood> perceptionNeighborhoods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<NpcBroadcastDispatchItem> pendingBroadcastQueue = new();
    private readonly Dictionary<string, HashSet<string>> consumedBroadcastDeliveriesByNpc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> ignoredBroadcastCorrelationsByNpc = new(StringComparer.OrdinalIgnoreCase);
    private NpcLlmSaveData saveData = new();
    private string lastDayIdleGameDate = string.Empty;
    private string dayIdleBatchQueuedForDate = string.Empty;
    private string dayIdleBatchCompletedForDate = string.Empty;
    private readonly HashSet<string> pendingGlobalDayIdleNpcs = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? chatBubbleTexture;

    public NpcAgentManager(
        ModConfig modConfig,
        IModHelper helper,
        IMonitor monitor,
        NpcScheduleEditorService scheduleEditorService,
        NpcLlmConfigService configService,
        NpcLlmRouter router,
        NpcLlmMemoryStore memoryStore,
        NpcLlmFactStore factStore,
        NpcPersonalityService personalityService,
        NpcLlmToolService toolService,
        NpcLlmConsoleLogger logger)
    {
        this.modConfig = modConfig;
        this.helper = helper;
        this.monitor = monitor;
        this.scheduleEditorService = scheduleEditorService;
        this.configService = configService;
        this.router = router;
        this.memoryStore = memoryStore;
        this.factStore = factStore;
        this.personalityService = personalityService;
        this.toolService = toolService;
        this.logger = logger;
        Instance = this;
    }

    public static NpcAgentManager? Instance { get; private set; }

    public string ConfigPath => this.configService.ConfigPath;

    public void LoadFromSave()
    {
        this.configService.LoadOrCreate();
        this.saveData = this.helper.Data.ReadSaveData<NpcLlmSaveData>(SaveDataKey) ?? new NpcLlmSaveData();
        this.logger.Info("Agent", $"读取 NPC LLM 存档配置，npc_count={this.saveData.Npcs.Count}");
        foreach ((string npcName, NpcAgentSettings settings) in this.saveData.Npcs)
        {
            this.NormalizeSettings(settings);
            NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
            state.BaselineScheduleKey = settings.BaselineScheduleKeyHint;
            this.logger.Debug("Agent", $"载入设置 enabled={settings.Enabled} provider={settings.ProviderName} interval={settings.PeriodicIntervalSeconds}s", npcName);
        }
    }

    public void OnDayStarted()
    {
        this.configService.LoadOrCreate();
        this.ResetDayIdleCoordinatorState();
        this.ResetBroadcastRuntimeState();
        int enabledCount = this.saveData.Npcs.Count(pair => pair.Value.Enabled);
        this.logger.Info("Lifecycle", $"DayStarted -> 重新初始化 {enabledCount} 个已启用 NPC Agent。");
        foreach ((string npcName, NpcAgentSettings settings) in this.saveData.Npcs)
        {
            if (!settings.Enabled)
            {
                if (this.states.TryGetValue(npcName, out NpcAgentRuntimeState? disabledState))
                {
                    disabledState.IdleStatusOverride = "disabled";
                    this.ReleaseAllSyncPairsForNpc(npcName, preserveCooldown: false);
                    this.ClearRuntimeOverride(
                        disabledState,
                        restoreBaseline: false,
                        logChange: false,
                        cancellationReason: NpcRequestCancellationReason.AgentDisabled,
                        reason: "disabled");
                }

                continue;
            }

            NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
            state.IdleStatusOverride = string.Empty;
            if (!HasQueuedOrActiveLlmWork(state))
            {
                this.ClearRuntimeOverride(state, restoreBaseline: false);
                this.TryCaptureBaseline(npcName, state, settings);
            }
            else
            {
                this.logger.Info("Lifecycle", "DayStarted 检测到已有 LLM 链路在运行，跳过抢占式清空，改为等待当前链路结束后再处理 day_started。", npcName);
            }

            this.EnqueueEvent(
                npcName,
                this.BuildEvent(npcName, "day_started", "新的一天开始", string.Empty, string.Empty, 0, settings),
                interruptInflight: false);
        }
    }

    public void OnDayEnding()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        this.logger.Info("Lifecycle", "DayEnding -> 开始写入 NPC 行程记忆。");
        foreach ((string npcName, NpcAgentSettings settings) in this.saveData.Npcs)
        {
            if (!settings.Enabled)
            {
                continue;
            }

            NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
            EditableScheduleRule? effectiveRule = state.ActivePatch?.Rule?.Clone()
                ?? state.BaselineRule?.Clone()
                ?? this.TryGetCurrentRule(npcName);
            if (effectiveRule is null)
            {
                continue;
            }

            string summary = this.scheduleEditorService.BuildRuleSummary(effectiveRule);
            string gameDate = this.BuildGameDateString();
            this.memoryStore.AppendDayRecord(npcName, gameDate, effectiveRule.RuleKey, summary);
            this.logger.Debug("Lifecycle", $"写入日终摘要 schedule_key={effectiveRule.RuleKey}", npcName);
            MemoryRecord dayRecord = this.memoryStore.AppendEventRecord(
                npcName,
                "day_summary",
                $"{gameDate} 行程总结\n{summary}",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["schedule_key"] = effectiveRule.RuleKey
                });
            _ = this.memoryStore.TryEmbedRecordAsync(npcName, dayRecord, CancellationToken.None);
        }
    }

    public void ClearForTitle()
    {
        this.logger.Info("Lifecycle", $"ReturnedToTitle -> 清空 {this.states.Count} 个运行时状态。");
        foreach (NpcAgentRuntimeState state in this.states.Values)
        {
            this.TryCancelActiveRequest(state, NpcRequestCancellationReason.ReturnedToTitle, "returned_to_title");
        }

        this.states.Clear();
        this.syncPairStates.Clear();
        this.ResetBroadcastRuntimeState();
        this.saveData = new NpcLlmSaveData();
        this.ResetDayIdleCoordinatorState();
    }

    public void Update()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        this.RefreshNpcPerceptionNeighborhoods();
        this.DrainPendingBroadcastQueue();

        foreach ((string npcName, NpcAgentSettings settings) in this.saveData.Npcs.ToList())
        {
            this.NormalizeSettings(settings);
            NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
            bool providerUsable = this.IsProviderUsable(settings);
            bool shouldBeActive = settings.Enabled && providerUsable && this.IsWithinActiveWindow(settings);

            if (shouldBeActive && !state.IsWithinActiveWindow)
            {
                state.IsWithinActiveWindow = true;
                state.IdleStatusOverride = string.Empty;
                state.PushDebugLine("进入 LLM 激活时间窗。");
                this.logger.Info("Agent", $"进入激活时间窗 provider={settings.ProviderName}", npcName);
                this.TryCaptureBaseline(npcName, state, settings);
                this.EnqueueEvent(
                    npcName,
                    this.BuildEvent(npcName, "window_entered", "进入激活时间窗", string.Empty, string.Empty, 0, settings),
                    interruptInflight: true);
            }
            else if (!shouldBeActive && state.IsWithinActiveWindow)
            {
                state.IsWithinActiveWindow = false;
                state.PushDebugLine("离开 LLM 激活时间窗，恢复基线。");
                this.logger.Info("Agent", "离开激活时间窗，恢复基线 schedule。", npcName);
                this.ClearRuntimeOverride(
                    state,
                    restoreBaseline: true,
                    cancellationReason: NpcRequestCancellationReason.LeftActiveWindow,
                    reason: "window_exited");
            }

            bool shouldProcessMaintenance = settings.Enabled && providerUsable && this.HasPendingMaintenanceWork(npcName, state);
            if (shouldProcessMaintenance)
            {
                this.EnsureDayIdleEventQueuedIfNeeded(npcName, state, settings);
            }

            bool hasRuntimeWork = HasQueuedOrActiveLlmWork(state) || state.PendingRuntimeReset is not null;
            if (!state.IsWithinActiveWindow && !shouldProcessMaintenance && !hasRuntimeWork)
            {
                continue;
            }

            this.ProcessCompletedRequestIfNeeded(npcName, state);
            if (this.TryApplyQueuedRuntimeReset(state))
            {
                continue;
            }

            if (state.PendingRuntimeReset is not null)
            {
                continue;
            }

            if (!state.IsWithinActiveWindow && !shouldProcessMaintenance)
            {
                continue;
            }

            this.DrainImmediateFeedbackQueue(npcName, state);
            this.ShowPendingSpeechIfPossible(npcName, state);
            this.ExecuteNextRealtimeAction(npcName, state);
            this.RefreshConversationPeriodicLock(npcName, state);

            if (state.IsWithinActiveWindow &&
                !this.HasInflightRequest(state) &&
                state.Queues.PendingEventCount == 0 &&
                !state.PausePeriodicUntilConversationSettles &&
                !state.WaitingForPlayerResponse &&
                DateTimeOffset.UtcNow - state.LastPeriodicTriggeredAt >= TimeSpan.FromSeconds(Math.Max(10, settings.PeriodicIntervalSeconds)))
            {
                if (Game1.shouldTimePass())
                {
                    state.LastPeriodicTriggeredAt = DateTimeOffset.UtcNow;
                    state.LastPeriodicPauseSkipLoggedAt = DateTimeOffset.MinValue;
                    this.logger.Debug("Event", "周期到期，发起 periodic_tick。", npcName);
                    this.EnqueueEvent(
                        npcName,
                        this.BuildEvent(npcName, "periodic_tick", "周期轮询", string.Empty, string.Empty, 0, settings),
                        interruptInflight: false);
                }
                else
                {
                    this.LogPeriodicPausedSkip(npcName, state);
                    this.TryQueueGlobalDayIdleBatch();
                }
            }

            if (!this.HasInflightRequest(state) && state.Queues.PendingEventCount > 0)
            {
                this.StartNextRequest(npcName, state, settings);
            }

            if (!this.HasInflightRequest(state) && state.Queues.PendingDeferredActionCount > 0)
            {
                this.ExecuteNextDeferredAction(npcName, state);
            }
        }

        this.UpdateNpcSyncEncounters();
        this.DrainPendingBroadcastQueue();
        this.PruneExpiredChatBubbles();
    }

}

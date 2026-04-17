using StardewMod.Models;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private static bool IsDayIdleEventType(string? eventType)
    {
        return string.Equals(eventType, "day_idle", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsDayIdleEvent(NpcAgentEvent agentEvent)
    {
        return IsDayIdleEventType(agentEvent.EventType);
    }

    private bool IsProviderUsable(NpcAgentSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.ProviderName) &&
            this.router.GetUsableProviderNames().Contains(settings.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    private bool HasPendingMaintenanceWork(string npcName, NpcAgentRuntimeState state)
    {
        return this.NeedsDayIdleForCurrentBatch(npcName) ||
            this.HasQueuedDayIdleEvent(state) ||
            this.IsDayIdleInflight(state);
    }

    private bool NeedsDayIdleForCurrentBatch(string npcName)
    {
        return string.Equals(this.dayIdleBatchQueuedForDate, this.BuildGameDateString(), StringComparison.OrdinalIgnoreCase) &&
            this.pendingGlobalDayIdleNpcs.Contains(npcName);
    }

    private bool HasQueuedDayIdleEvent(NpcAgentRuntimeState state)
    {
        return state.Queues.AnyPendingEvent(agentEvent => IsDayIdleEventType(agentEvent.EventType));
    }

    private bool IsDayIdleInflight(NpcAgentRuntimeState state)
    {
        return state.ActiveRequest is not null &&
            this.HasInflightRequest(state) &&
            IsDayIdleEventType(state.ActiveRequest.TriggerEvent.EventType);
    }

    private void TryQueueGlobalDayIdleBatch()
    {
        string gameDate = this.BuildGameDateString();
        if (string.Equals(this.dayIdleBatchQueuedForDate, gameDate, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        this.lastDayIdleGameDate = gameDate;
        this.dayIdleBatchQueuedForDate = gameDate;
        this.dayIdleBatchCompletedForDate = string.Empty;
        this.pendingGlobalDayIdleNpcs.Clear();

        int queuedCount = 0;
        foreach ((string npcName, NpcAgentSettings settings) in this.saveData.Npcs
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            this.NormalizeSettings(settings);
            if (!settings.Enabled || !this.IsProviderUsable(settings))
            {
                continue;
            }

            this.pendingGlobalDayIdleNpcs.Add(npcName);
            NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
            this.EnsureDayIdleEventQueuedIfNeeded(npcName, state, settings);
            queuedCount++;
        }

        if (queuedCount == 0)
        {
            this.dayIdleBatchCompletedForDate = gameDate;
            this.logger.Info("DayIdle", $"周期因暂停跳过，但当天没有可执行的 day_idle 目标 game_date={gameDate}");
            return;
        }

        this.logger.Info("DayIdle", $"周期因暂停跳过，已排队全局 day_idle 批处理 game_date={gameDate} npc_count={queuedCount}");
    }

    private void EnsureDayIdleEventQueuedIfNeeded(string npcName, NpcAgentRuntimeState state, NpcAgentSettings settings)
    {
        if (!this.NeedsDayIdleForCurrentBatch(npcName) ||
            this.HasQueuedDayIdleEvent(state) ||
            this.IsDayIdleInflight(state))
        {
            return;
        }

        this.EnqueueEvent(
            npcName,
            this.BuildEvent(npcName, "day_idle", "系统日维护", string.Empty, string.Empty, 0, settings),
            interruptInflight: false);
        this.logger.Info("DayIdle", "补入 day_idle 系统任务。", npcName);
    }

    private void MarkDayIdleCompletedIfNeeded(string npcName)
    {
        if (!this.pendingGlobalDayIdleNpcs.Remove(npcName))
        {
            return;
        }

        string gameDate = this.BuildGameDateString();
        if (this.pendingGlobalDayIdleNpcs.Count == 0 &&
            string.Equals(this.dayIdleBatchQueuedForDate, gameDate, StringComparison.OrdinalIgnoreCase))
        {
            this.dayIdleBatchCompletedForDate = gameDate;
            this.logger.Info("DayIdle", $"全局 day_idle 批处理已完成 game_date={gameDate}");
            return;
        }

        this.logger.Info("DayIdle", $"day_idle 完成，remaining={this.pendingGlobalDayIdleNpcs.Count}", npcName);
    }

    private void ResetDayIdleCoordinatorState()
    {
        this.lastDayIdleGameDate = string.Empty;
        this.dayIdleBatchQueuedForDate = string.Empty;
        this.dayIdleBatchCompletedForDate = string.Empty;
        this.pendingGlobalDayIdleNpcs.Clear();
    }

    private void LogPeriodicPausedSkip(string npcName, NpcAgentRuntimeState state)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - state.LastPeriodicPauseSkipLoggedAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        state.LastPeriodicPauseSkipLoggedAt = now;
        this.logger.Info("Event", "周期到期但原版时间暂停，本轮跳过 periodic_tick，并检查全局 day_idle 批处理。", npcName);
    }
}

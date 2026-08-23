using Microsoft.Xna.Framework.Graphics;
using StardewMod.Models;
using StardewMod.Ui;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    public IReadOnlyList<string> BuildConsoleStateReport(string npcName)
    {
        List<string> lines = new();
        NpcAgentSettings settings = this.GetSettings(npcName);
        NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
        NPC? npc = Context.IsWorldReady ? Game1.getCharacterFromName(npcName) : null;

        lines.Add($"npc={npcName}");
        lines.Add($"enabled={settings.Enabled} provider={settings.ProviderName} within_window={state.IsWithinActiveWindow} today_windows={this.GetTodayWindowSummary(settings)}");
        lines.Add($"inflight={state.InflightStatus} waiting_player_response={state.WaitingForPlayerResponse} awaiting_dialogue_close={state.AwaitingConversationDialogueClose} periodic_pause={state.PausePeriodicUntilConversationSettles}");
        lines.Add($"pending_events={state.Queues.PendingEventCount} speech_display_queue={state.Queues.PendingSpeechCount} immediate_feedback_events={state.Queues.PendingImmediateFeedbackCount} realtime_actions={state.Queues.PendingRealtimeActionCount} deferred_actions={state.Queues.PendingDeferredActionCount} dropped_events={state.DroppedPendingEventCount} last_dropped={state.LastDroppedEventType}");
        lines.Add($"baseline_rule={state.BaselineScheduleKey} active_patch={(state.ActivePatch?.RevisionId ?? "<none>")} last_trigger={state.LastTrigger} last_duration={state.LastRequestDuration}");

        if (npc is not null)
        {
            NpcAgentRuntimeSummary runtimeSummary = this.GetRuntimeSummary(npcName);
            lines.Add(
                $"npc_location={npc.currentLocation?.NameOrUniqueName ?? "<null>"} tile={npc.TilePoint.X},{npc.TilePoint.Y} facing={npc.FacingDirection} schedule_key={npc.ScheduleKey ?? "<null>"} controller={(npc.controller is null ? "no" : "yes")} temp_controller={(npc.temporaryController is null ? "no" : "yes")} ignore_schedule_today={npc.ignoreScheduleToday} movement_pause={npc.movementPause}");
            lines.Add($"current_emote={runtimeSummary.LiveState.CurrentEmoteName}:{runtimeSummary.LiveState.CurrentEmoteId} mood={runtimeSummary.LiveState.MoodHint} safe_mutation_time={runtimeSummary.ScheduleState.SafeMutationTime} current_stop={runtimeSummary.ScheduleState.CurrentStop.Summary} next_stop={runtimeSummary.ScheduleState.NextStop.Summary}");
        }
        else
        {
            lines.Add("npc_not_loaded=true");
        }

        if (state.RecentToolCalls.Count > 0)
        {
            lines.Add($"recent_tools={string.Join(", ", state.RecentToolCalls)}");
        }

        if (state.RecentDebugLines.Count > 0)
        {
            lines.Add("recent_debug:");
            lines.AddRange(state.RecentDebugLines.Select(line => "  " + line));
        }

        return lines;
    }

    public IReadOnlyList<string> BuildConsoleScheduleReport(string npcName)
    {
        List<string> lines = new();
        NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
        EditableScheduleRule? rule = state.ActivePatch?.Rule?.Clone()
            ?? state.BaselineRule?.Clone()
            ?? this.TryGetCurrentRule(npcName);

        if (rule is null)
        {
            lines.Add($"npc={npcName} schedule=<unavailable>");
            return lines;
        }

        string source = state.ActivePatch?.Rule is not null
            ? $"runtime_patch:{state.ActivePatch!.RevisionId}"
            : state.BaselineRule is not null
                ? "baseline_snapshot"
                : "current_rule";
        lines.Add($"npc={npcName} schedule_source={source} rule_key={rule.RuleKey}");
        lines.Add($"summary_lines={rule.Stops.Count + 1}");
        lines.AddRange(this.scheduleEditorService.BuildRuleSummary(rule)
            .Split('\n', StringSplitOptions.None)
            .Select(line => "  " + line));
        return lines;
    }

    public void DrawWorldOverlay(SpriteBatch spriteBatch)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null)
        {
            return;
        }

        this.DrawNpcChatBubbles(spriteBatch);

        foreach ((string npcName, NpcAgentRuntimeState state) in this.states)
        {
            if (!this.ShouldDrawThinkingBubble(state))
            {
                continue;
            }

            NPC? npc = Game1.getCharacterFromName(npcName);
            if (npc is null || npc.currentLocation != Game1.currentLocation)
            {
                continue;
            }

            MenuDrawHelper.DrawThinkingBubble(spriteBatch, npc);
        }
    }

    private bool ShouldDrawThinkingBubble(NpcAgentRuntimeState state)
    {
        if (this.HasVisibleChatBubble(state))
        {
            return false;
        }

        if (!state.WaitingForPlayerResponse)
        {
            return false;
        }

        if (this.HasInflightRequest(state))
        {
            return true;
        }

        return state.Queues.AnyPendingEvent(agentEvent => string.Equals(agentEvent.EventType, "player_prompt", StringComparison.OrdinalIgnoreCase));
    }
}

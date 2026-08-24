using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Pathfinding;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    public NpcAgentRuntimeSummary GetRuntimeSummary(string npcName)
    {
        NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
        NPC? npc = Context.IsWorldReady ? Game1.getCharacterFromName(npcName) : null;
        EditableScheduleRule? activeRule = state.ActivePatch?.Rule?.Clone()
            ?? state.BaselineRule?.Clone()
            ?? this.TryGetCurrentRule(npcName);
        return new NpcAgentRuntimeSummary
        {
            NpcName = npcName,
            ProviderName = this.saveData.Npcs.TryGetValue(npcName, out NpcAgentSettings? settings) ? settings.ProviderName : string.Empty,
            IsWithinActiveWindow = state.IsWithinActiveWindow,
            BaselineScheduleKey = state.BaselineScheduleKey,
            PatchRevisionId = state.ActivePatch?.RevisionId ?? string.Empty,
            LastTrigger = state.LastTrigger,
            LastRequestDuration = state.LastRequestDuration,
            InflightStatus = state.InflightStatus,
            RecentToolCalls = state.RecentToolCalls.ToList(),
            LastPatchSummary = state.LastPatchSummary,
            LastRejectionReason = state.LastRejectionReason,
            RecentDebugLines = state.RecentDebugLines.ToList(),
            LiveState = this.BuildLiveRuntimeSnapshot(npc, state),
            ScheduleState = this.BuildScheduleExecutionSnapshot(npc, state, activeRule),
            ConversationState = this.BuildConversationRuntimeSnapshot(state)
        };
    }

    private NpcLiveRuntimeSnapshot BuildLiveRuntimeSnapshot(NPC? npc, NpcAgentRuntimeState state)
    {
        if (!Context.IsWorldReady || npc is null)
        {
            return new NpcLiveRuntimeSnapshot
            {
                MoodHint = "npc_not_loaded"
            };
        }

        Stack<Dialogue>? currentDialogue = null;
        try
        {
            currentDialogue = npc.CurrentDialogue;
        }
        catch
        {
        }

        return new NpcLiveRuntimeSnapshot
        {
            LocationName = npc.currentLocation?.NameOrUniqueName ?? string.Empty,
            TileX = npc.TilePoint.X,
            TileY = npc.TilePoint.Y,
            FacingDirection = npc.FacingDirection,
            IsMoving = npc.isMoving(),
            IsSleeping = npc.isSleeping.Value,
            IsEmoting = npc.isEmoting,
            CurrentEmoteId = npc.CurrentEmote,
            CurrentEmoteName = NpcEmoteCatalog.DescribeCurrent(npc.CurrentEmote),
            CurrentEmoteFrameIndex = npc.CurrentEmoteIndex,
            MovementPauseMilliseconds = npc.movementPause,
            IsDoingRouteAnimation = npc.doingEndOfRouteAnimation.Value,
            IsGoingToDoRouteAnimation = npc.goingToDoEndOfRouteAnimation.Value,
            CurrentRouteAnimationName = npc.endOfRouteBehaviorName.Value ?? string.Empty,
            IgnoreScheduleToday = npc.ignoreScheduleToday,
            CurrentScheduleDelay = npc.currentScheduleDelay,
            HasDialogueStack = currentDialogue is not null && currentDialogue.Count > 0,
            DialogueLineCount = currentDialogue?.Count ?? 0,
            LoadedDialogueKey = npc.LoadedDialogueKey,
            Age = npc.Age,
            Manners = npc.Manners,
            SocialAnxiety = npc.SocialAnxiety,
            Optimism = npc.Optimism,
            MoveTowardPlayerThreshold = npc.moveTowardPlayerThreshold.Value,
            MoodHint = this.BuildMoodHint(npc, state),
            ScheduleController = this.BuildControllerSnapshot(npc.controller, "schedule"),
            TemporaryController = this.BuildControllerSnapshot(npc.temporaryController, "temporary")
        };
    }

    private NpcScheduleExecutionSnapshot BuildScheduleExecutionSnapshot(NPC? npc, NpcAgentRuntimeState state, EditableScheduleRule? activeRule)
    {
        NpcScheduleExecutionSnapshot snapshot = new()
        {
            Source = state.ActivePatch?.Rule is not null
                ? "runtime_patch"
                : state.BaselineRule is not null
                    ? "baseline_rule"
                    : "current_rule",
            RuleKey = activeRule?.RuleKey ?? string.Empty,
            HasActiveRule = activeRule is not null,
            IsFollowingSchedulePath = npc is not null && npc.controller is not null && !npc.ignoreScheduleToday,
            IsExecutingActionRequest = !string.IsNullOrWhiteSpace(state.ActiveActionSummary),
            IsUnderTemporaryController = npc?.temporaryController is not null,
            SafeMutationTime = Context.IsWorldReady ? Game1.timeOfDay : 600,
            MutationGuidance = "当前没有正在执行的 schedule 路径，可按需要调整未来站点。"
        };

        if (!Context.IsWorldReady || npc is null || activeRule is null)
        {
            return snapshot;
        }

        List<NpcRuntimeScheduledStopSnapshot> stops = this.BuildScheduledStopSnapshots(npc, activeRule);
        if (stops.Count == 0)
        {
            return snapshot;
        }

        int currentTime = Game1.timeOfDay;
        int currentIndex = -1;
        int nextIndex = -1;
        for (int i = 0; i < stops.Count; i++)
        {
            if (stops[i].EffectiveTime <= currentTime)
            {
                currentIndex = i;
                continue;
            }

            nextIndex = i;
            break;
        }

        if (currentIndex >= 0)
        {
            snapshot.CurrentStop = stops[currentIndex];
        }

        if (nextIndex >= 0)
        {
            snapshot.NextStop = stops[nextIndex];
        }

        if (snapshot.IsFollowingSchedulePath)
        {
            snapshot.CurrentExecutionProtected = true;
            snapshot.SafeMutationTime = snapshot.NextStop.Exists
                ? snapshot.NextStop.EffectiveTime
                : ScheduleTimeHelper.LatestStopTime;
            string currentStopLabel = snapshot.CurrentStop.Exists
                ? $"第 {snapshot.CurrentStop.Index + 1} 站"
                : "当前首段路径";
            snapshot.MutationGuidance = snapshot.NextStop.Exists
                ? $"NPC 当前正沿 schedule 执行{currentStopLabel}，除非玩家明确要求中断，否则改动应从 {Game1.getTimeOfDayString(snapshot.SafeMutationTime)} 之后开始。"
                : "NPC 当前正在执行最后一段 schedule，除非玩家明确要求中断，否则不要改写当前执行段。";
        }

        return snapshot;
    }

    private NpcConversationRuntimeSnapshot BuildConversationRuntimeSnapshot(NpcAgentRuntimeState state)
    {
        return new NpcConversationRuntimeSnapshot
        {
            WaitingForPlayerResponse = state.WaitingForPlayerResponse,
            PausePeriodicUntilConversationSettles = state.PausePeriodicUntilConversationSettles,
            AwaitingConversationDialogueClose = state.AwaitingConversationDialogueClose,
            PendingEventCount = state.Queues.PendingEventCount,
            PendingSpeechDisplayCount = state.Queues.PendingSpeechCount,
            PendingImmediateFeedbackCount = state.Queues.PendingImmediateFeedbackCount,
            PendingRealtimeActionCount = state.Queues.PendingRealtimeActionCount,
            PendingDeferredActionCount = state.Queues.PendingDeferredActionCount,
            DroppedPendingEventCount = state.DroppedPendingEventCount,
            LastDroppedEventType = state.LastDroppedEventType,
            ActiveActionSummary = state.ActiveActionSummary,
            HasActiveChatBubble = state.ActiveChatBubble is not null
        };
    }

    private List<NpcRuntimeScheduledStopSnapshot> BuildScheduledStopSnapshots(NPC npc, EditableScheduleRule rule)
    {
        EditableScheduleRule normalizedRule = rule.Clone();
        normalizedRule.NormalizeBeforeSave();

        try
        {
            List<KeyValuePair<int, SchedulePathDescription>> compiledStops = this.scheduleEditorService
                .BuildScheduleDictionaryFromRule(npc, normalizedRule)
                .OrderBy(pair => pair.Key)
                .ToList();
            List<EditableScheduleStop> orderedStops = normalizedRule.Stops.OrderBy(stop => stop.Time).ToList();

            List<NpcRuntimeScheduledStopSnapshot> snapshots = new();
            int count = Math.Min(compiledStops.Count, orderedStops.Count);
            for (int i = 0; i < count; i++)
            {
                EditableScheduleStop stop = orderedStops[i];
                int effectiveTime = compiledStops[i].Key;
                snapshots.Add(new NpcRuntimeScheduledStopSnapshot
                {
                    Exists = true,
                    Index = i,
                    EffectiveTime = effectiveTime,
                    DeclaredTime = stop.Time,
                    TimeMode = stop.TimeMode.ToString(),
                    LocationName = stop.LocationName,
                    TargetTileX = stop.TargetTile.X,
                    TargetTileY = stop.TargetTile.Y,
                    FacingDirection = stop.FacingDirection,
                    EndBehavior = stop.EndBehavior,
                    EndMessage = stop.EndMessage,
                    Summary = $"{Game1.getTimeOfDayString(effectiveTime)} -> {stop.LocationName} ({stop.TargetTile.X}, {stop.TargetTile.Y}) [{stop.TimeMode}]"
                });
            }

            return snapshots;
        }
        catch
        {
            return normalizedRule.Stops
                .OrderBy(stop => stop.Time)
                .Select((stop, index) => new NpcRuntimeScheduledStopSnapshot
                {
                    Exists = true,
                    Index = index,
                    EffectiveTime = stop.Time,
                    DeclaredTime = stop.Time,
                    TimeMode = stop.TimeMode.ToString(),
                    LocationName = stop.LocationName,
                    TargetTileX = stop.TargetTile.X,
                    TargetTileY = stop.TargetTile.Y,
                    FacingDirection = stop.FacingDirection,
                    EndBehavior = stop.EndBehavior,
                    EndMessage = stop.EndMessage,
                    Summary = $"{Game1.getTimeOfDayString(stop.Time)} -> {stop.LocationName} ({stop.TargetTile.X}, {stop.TargetTile.Y}) [{stop.TimeMode}]"
                })
                .ToList();
        }
    }

    private NpcControllerRuntimeSnapshot BuildControllerSnapshot(PathFindController? controller, string kind)
    {
        if (controller is null)
        {
            return new NpcControllerRuntimeSnapshot
            {
                Kind = kind
            };
        }

        string locationName = controller.location?.NameOrUniqueName ?? string.Empty;
        int remainingPathNodes = controller.pathToEndPoint?.Count ?? 0;
        return new NpcControllerRuntimeSnapshot
        {
            Active = true,
            Kind = kind,
            LocationName = locationName,
            EndTileX = controller.endPoint.X,
            EndTileY = controller.endPoint.Y,
            FinalFacingDirection = controller.finalFacingDirection,
            RemainingPathNodes = remainingPathNodes,
            Summary = $"{kind}: {locationName} ({controller.endPoint.X}, {controller.endPoint.Y}) path_nodes={remainingPathNodes}"
        };
    }

    private string BuildMoodHint(NPC npc, NpcAgentRuntimeState state)
    {
        if (npc.isSleeping.Value)
        {
            return "sleeping";
        }

        if (npc.isEmoting)
        {
            string emoteName = NpcEmoteCatalog.DescribeCurrent(npc.CurrentEmote);
            if (!string.IsNullOrWhiteSpace(emoteName))
            {
                return emoteName;
            }
        }

        if (npc.doingEndOfRouteAnimation.Value || npc.goingToDoEndOfRouteAnimation.Value)
        {
            string animationName = npc.endOfRouteBehaviorName.Value ?? string.Empty;
            return string.IsNullOrWhiteSpace(animationName)
                ? "route_animation"
                : $"route_animation:{animationName}";
        }

        if (npc.isMoving())
        {
            return "moving";
        }

        return "neutral";
    }
}

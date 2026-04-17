using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private static readonly TimeSpan SyncEncounterCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SyncDiagnosticLogInterval = TimeSpan.FromSeconds(15);
    private DateTimeOffset lastSyncDiagnosticLoggedAt = DateTimeOffset.MinValue;
    private string lastSyncDiagnosticMessage = string.Empty;

    private static bool IsNpcSyncEncounterEventType(string? eventType)
    {
        return string.Equals(eventType, "npc_sync_encounter", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNpcSyncEventType(string? eventType)
    {
        return IsNpcSyncEncounterEventType(eventType);
    }

    private bool IsNpcSyncEvent(NpcAgentEvent agentEvent)
    {
        return IsNpcSyncEventType(agentEvent.EventType);
    }

    private static bool IsAmbientObservationEventType(string? eventType)
    {
        return string.Equals(eventType, "day_started", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventType, "window_entered", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventType, "periodic_tick", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAmbientObservationEvent(NpcAgentEvent agentEvent)
    {
        return IsAmbientObservationEventType(agentEvent.EventType);
    }

    private static bool IsScheduleControlAllowedForEventType(string? eventType)
    {
        return string.Equals(eventType, "player_prompt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNpcSpeechAllowedForEventType(string? eventType)
    {
        return string.Equals(eventType, "player_prompt", StringComparison.OrdinalIgnoreCase) ||
            IsBroadcastEventType(eventType) ||
            IsNpcSyncEventType(eventType);
    }

    private static bool IsReactivePlayerEventType(string? eventType)
    {
        return string.Equals(eventType, "gift_received", StringComparison.OrdinalIgnoreCase);
    }

    private NpcToolAccessProfile GetToolAccessProfile(NpcAgentEvent agentEvent)
    {
        if (this.IsDayIdleEvent(agentEvent))
        {
            return NpcToolAccessProfile.Maintenance;
        }

        if (this.IsAmbientObservationEvent(agentEvent))
        {
            return NpcToolAccessProfile.Ambient;
        }

        if (IsReactivePlayerEventType(agentEvent.EventType))
        {
            return NpcToolAccessProfile.Reactive;
        }

        if (IsBroadcastEventType(agentEvent.EventType))
        {
            return NpcToolAccessProfile.Broadcast;
        }

        return this.IsNpcSyncEvent(agentEvent)
            ? NpcToolAccessProfile.NpcSync
            : NpcToolAccessProfile.Full;
    }

    private void UpdateNpcSyncEncounters()
    {
        HashSet<string> pairsWithinPerceptionRadius = new(StringComparer.OrdinalIgnoreCase);
        if (!Context.IsWorldReady || !Game1.shouldTimePass())
        {
            this.ReconcilePerceptionRadiusPairs(pairsWithinPerceptionRadius);
            return;
        }

        List<(string NpcName, NpcAgentSettings Settings, NPC? Npc)> enabledUsableEntries = this.saveData.Npcs
            .Where(pair =>
            {
                this.NormalizeSettings(pair.Value);
                return pair.Value.Enabled && this.IsProviderUsable(pair.Value);
            })
            .Select(pair =>
            {
                NPC? loadedNpc = Game1.getCharacterFromName(pair.Key);
                return (NpcName: pair.Key, Settings: pair.Value, Npc: (NPC?)loadedNpc);
            })
            .ToList();
        List<NPC> candidates = enabledUsableEntries
            .Where(entry => this.IsWithinActiveWindow(entry.Settings) && entry.Npc?.currentLocation is not null)
            .Select(entry => entry.Npc!)
            .OrderBy(npc => npc.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabledUsableEntries.Count >= 2 && candidates.Count < 2)
        {
            this.ReconcilePerceptionRadiusPairs(pairsWithinPerceptionRadius);
            int activeWindowCount = enabledUsableEntries.Count(entry => this.IsWithinActiveWindow(entry.Settings));
            int loadedCount = enabledUsableEntries.Count(entry => entry.Npc?.currentLocation is not null);
            this.TryLogSyncDiagnostic(
                $"NPC 同步未触发：已启用且 provider 可用的 NPC 有 {enabledUsableEntries.Count} 个，但当前在激活时间窗内的只有 {activeWindowCount} 个，已加载到地图上的只有 {loadedCount} 个，真正可参与同步的只有 {candidates.Count} 个。若你希望一直可对话，可在 NPC LLM 列表点击“全部开启(全天)”。");
            return;
        }

        Dictionary<string, NPC> candidateMap = candidates.ToDictionary(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase);
        foreach (NPC left in candidates)
        {
            NpcAgentRuntimeState leftState = this.GetOrCreateState(left.Name);
            NpcPerceptionNeighborhood neighborhood = this.GetNeighborhood(left.Name);
            foreach (NpcPerceptionNeighbor neighbor in neighborhood.NearbyNpcs
                .OrderBy(item => item.DistanceTiles)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Compare(left.Name, neighbor.NpcName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (!candidateMap.TryGetValue(neighbor.NpcName, out NPC? right))
                {
                    continue;
                }

                NpcAgentRuntimeState rightState = this.GetOrCreateState(right.Name);
                string pairKey = BuildSyncPairKey(left.Name, right.Name);
                string mapName = neighborhood.MapName;
                pairsWithinPerceptionRadius.Add(pairKey);
                this.TrackPerceptionRadiusEntry(left, right, pairKey, mapName, neighbor.DistanceTiles);

                NpcSyncPairRuntimeState pairState = this.GetOrCreateSyncPairState(left.Name, right.Name);
                if (pairState.Perception.EncounterTriggeredInCurrentSession)
                {
                    continue;
                }

                if (!this.IsNpcAvailableForSync(leftState))
                {
                    this.TryLogSyncDiagnostic($"NPC 同步未触发：pair={pairKey}，{left.Name} 当前不可参与同步，原因={this.DescribeSyncAvailability(leftState)}");
                    continue;
                }

                if (!this.IsNpcAvailableForSync(rightState))
                {
                    this.TryLogSyncDiagnostic($"NPC 同步未触发：pair={pairKey}，{right.Name} 当前不可参与同步，原因={this.DescribeSyncAvailability(rightState)}");
                    continue;
                }

                if (pairState.Conversation.IsActive)
                {
                    this.TryLogSyncDiagnostic($"NPC 同步未触发：pair={pairKey} 正在进行中。");
                    continue;
                }

                if (string.Equals(pairState.Cooldown.MapName, mapName, StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.UtcNow - pairState.Cooldown.LastTriggeredAtUtc < SyncEncounterCooldown)
                {
                    double remainingSeconds = Math.Max(0d, (SyncEncounterCooldown - (DateTimeOffset.UtcNow - pairState.Cooldown.LastTriggeredAtUtc)).TotalSeconds);
                    this.TryLogSyncDiagnostic($"NPC 同步未触发：pair={pairKey} 仍在冷却中，剩余约 {remainingSeconds:0.0}s。");
                    continue;
                }

                NPC initiator = Game1.random.NextDouble() < 0.5d ? left : right;
                NPC receiver = ReferenceEquals(initiator, left) ? right : left;
                this.QueueNpcSyncEncounter(initiator, receiver, pairKey);
                this.ReconcilePerceptionRadiusPairs(pairsWithinPerceptionRadius);
                return;
            }
        }

        this.ReconcilePerceptionRadiusPairs(pairsWithinPerceptionRadius);
    }

    private bool HasSyncConversationPendingOrInflight(NpcAgentRuntimeState state)
    {
        if (state.ActiveRequest is not null &&
            this.HasInflightRequest(state) &&
            IsNpcSyncEventType(state.ActiveRequest.TriggerEvent.EventType))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(state.ActiveRequest?.SyncPairKey))
        {
            return true;
        }

        return state.Queues.AnyPendingEvent(agentEvent => IsNpcSyncEvent(agentEvent));
    }

    private bool IsNpcAvailableForSync(NpcAgentRuntimeState state)
    {
        if (this.HasSyncConversationPendingOrInflight(state))
        {
            return false;
        }

        if (state.WaitingForPlayerResponse || state.PausePeriodicUntilConversationSettles || state.AwaitingConversationDialogueClose)
        {
            return false;
        }

        if (this.HasInflightRequest(state))
        {
            return false;
        }

        if (state.Queues.HasQueuedWork)
        {
            return false;
        }

        return true;
    }

    private string DescribeSyncAvailability(NpcAgentRuntimeState state)
    {
        if (this.HasSyncConversationPendingOrInflight(state))
        {
            return "已有同步对话在排队或进行中";
        }

        if (state.WaitingForPlayerResponse)
        {
            return "正在等待玩家对话链结束";
        }

        if (state.PausePeriodicUntilConversationSettles || state.AwaitingConversationDialogueClose)
        {
            return "当前对话链尚未完全落地";
        }

        if (this.HasInflightRequest(state))
        {
            return $"仍有请求在执行 status={state.InflightStatus}";
        }

        if (state.Queues.PendingEventCount > 0)
        {
            return $"存在待处理事件 count={state.Queues.PendingEventCount}";
        }

        if (state.Queues.HasImmediateFeedback)
        {
            return "即时反馈队列仍有未处理事件";
        }

        if (state.Queues.PendingRealtimeActionCount > 0)
        {
            return $"实时动作队列未清空 count={state.Queues.PendingRealtimeActionCount}";
        }

        if (state.Queues.PendingDeferredActionCount > 0)
        {
            return $"延迟动作队列未清空 count={state.Queues.PendingDeferredActionCount}";
        }

        return "未知忙碌状态";
    }

    private void TryLogSyncDiagnostic(string message)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.Equals(message, this.lastSyncDiagnosticMessage, StringComparison.Ordinal) &&
            now - this.lastSyncDiagnosticLoggedAt < SyncDiagnosticLogInterval)
        {
            return;
        }

        if (now - this.lastSyncDiagnosticLoggedAt < SyncDiagnosticLogInterval &&
            !string.Equals(message, this.lastSyncDiagnosticMessage, StringComparison.Ordinal))
        {
            return;
        }

        this.lastSyncDiagnosticLoggedAt = now;
        this.lastSyncDiagnosticMessage = message;
        this.logger.Info("Sync", message);
    }

    private void TrackPerceptionRadiusEntry(NPC leftNpc, NPC rightNpc, string pairKey, string mapName, double distanceTiles)
    {
        NpcSyncPairRuntimeState pairState = this.GetOrCreateSyncPairState(leftNpc.Name, rightNpc.Name);
        if (pairState.Perception.IsWithinPerceptionRadius &&
            string.Equals(pairState.Perception.MapName, mapName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        pairState.Perception.IsWithinPerceptionRadius = true;
        pairState.Perception.MapName = mapName;
        pairState.Perception.EncounterTriggeredInCurrentSession = false;
        this.logger.Info(
            "Perception",
            $"NPC 进入 NPC 感知半径 pair={pairKey} left={leftNpc.Name} right={rightNpc.Name} map={mapName} distance_tiles={Math.Round(distanceTiles, 2):0.##}",
            leftNpc.Name);
    }

    private void ReconcilePerceptionRadiusPairs(HashSet<string> pairsWithinPerceptionRadius)
    {
        foreach (NpcSyncPairRuntimeState pairState in this.syncPairStates.Values)
        {
            if (!pairState.Perception.IsWithinPerceptionRadius ||
                pairsWithinPerceptionRadius.Contains(pairState.PairKey))
            {
                continue;
            }

            pairState.Perception.IsWithinPerceptionRadius = false;
            pairState.Perception.MapName = string.Empty;
            pairState.Perception.EncounterTriggeredInCurrentSession = false;
        }
    }

    private void QueueNpcSyncEncounter(NPC initiator, NPC receiver, string syncPairKey)
    {
        NpcSyncPairRuntimeState pairState = this.GetOrCreateSyncPairState(initiator.Name, receiver.Name);
        string mapName = initiator.currentLocation?.NameOrUniqueName ?? string.Empty;
        pairState.Cooldown.MapName = mapName;
        pairState.Cooldown.LastTriggeredAtUtc = DateTimeOffset.UtcNow;
        pairState.Conversation.MapName = mapName;
        pairState.Conversation.IsActive = true;
        pairState.Conversation.InitiatorNpcName = initiator.Name;
        pairState.Perception.EncounterTriggeredInCurrentSession = true;

        this.AppendNpcSyncEncounterMemory(initiator, receiver, syncPairKey);

        NpcAgentSettings initiatorSettings = this.GetSettings(initiator.Name);
        NpcAgentEvent encounterEvent = this.BuildNpcSyncEvent(
            initiator.Name,
            "npc_sync_encounter",
            receiver,
            string.Empty,
            syncPairKey,
            initiatorSettings);
        this.EnqueueEvent(initiator.Name, encounterEvent, interruptInflight: false);
        this.logger.Info("Sync", $"触发 NPC 同步事件 pair={syncPairKey} initiator={initiator.Name} receiver={receiver.Name} map={pairState.Conversation.MapName}", initiator.Name);
    }

    private NpcAgentEvent BuildNpcSyncEvent(
        string npcName,
        string eventType,
        NPC otherNpc,
        string otherNpcMessage,
        string syncPairKey,
        NpcAgentSettings settings)
    {
        string actionText = $"你在路上遇见了 {otherNpc.displayName}。";
        NpcAgentEvent agentEvent = this.BuildEvent(
            npcName,
            eventType,
            actionText,
            otherNpcMessage,
            string.Empty,
            0,
            settings);
        agentEvent.LocationName = otherNpc.currentLocation?.NameOrUniqueName ?? string.Empty;
        agentEvent.OtherNpcName = otherNpc.Name;
        agentEvent.OtherNpcDisplayName = otherNpc.displayName;
        agentEvent.OtherNpcMessage = otherNpcMessage;
        agentEvent.SyncPairKey = syncPairKey;
        agentEvent.Metadata["trigger_kind"] = "npc_sync";
        agentEvent.Metadata["other_npc_name"] = otherNpc.Name;
        if (!string.IsNullOrWhiteSpace(otherNpcMessage))
        {
            agentEvent.Metadata["other_npc_message"] = otherNpcMessage;
        }

        return agentEvent;
    }

    private static string BuildSyncPairKey(string leftNpcName, string rightNpcName)
    {
        string[] names = new[] { leftNpcName.Trim(), rightNpcName.Trim() };
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return string.Join('|', names);
    }

    private NpcSyncPairRuntimeState GetOrCreateSyncPairState(string leftNpcName, string rightNpcName)
    {
        string pairKey = BuildSyncPairKey(leftNpcName, rightNpcName);
        if (!this.syncPairStates.TryGetValue(pairKey, out NpcSyncPairRuntimeState? state))
        {
            string[] names = pairKey.Split('|', StringSplitOptions.RemoveEmptyEntries);
            state = new NpcSyncPairRuntimeState
            {
                PairKey = pairKey,
                NpcAName = names.ElementAtOrDefault(0) ?? leftNpcName,
                NpcBName = names.ElementAtOrDefault(1) ?? rightNpcName
            };
            this.syncPairStates[pairKey] = state;
        }

        return state;
    }

    private NpcSyncTargetValidationResult ValidateNpcSyncSpeechTarget(string speakerNpcName, string targetNpcName)
    {
        if (!Context.IsWorldReady)
        {
            return new NpcSyncTargetValidationResult
            {
                Ok = false,
                Error = "当前不在存档内。"
            };
        }

        NPC? speakerNpc = Game1.getCharacterFromName(speakerNpcName);
        NPC? targetNpc = Game1.getCharacterFromName(targetNpcName);
        if (speakerNpc is null || targetNpc is null)
        {
            return new NpcSyncTargetValidationResult
            {
                Ok = false,
                Error = "说话方或目标 NPC 未加载。"
            };
        }

        if (string.Equals(speakerNpc.Name, targetNpc.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new NpcSyncTargetValidationResult
            {
                Ok = false,
                Error = "不能对自己使用 say_to_npc。"
            };
        }

        NpcAgentSettings targetSettings = this.GetSettings(targetNpc.Name);
        if (!targetSettings.Enabled || !this.IsProviderUsable(targetSettings) || !this.IsWithinActiveWindow(targetSettings))
        {
            return new NpcSyncTargetValidationResult
            {
                Ok = false,
                Error = $"目标 NPC {targetNpc.displayName} 当前不可参与同步对话。"
            };
        }

        if (speakerNpc.currentLocation != targetNpc.currentLocation)
        {
            return new NpcSyncTargetValidationResult
            {
                Ok = false,
                Error = $"目标 NPC {targetNpc.displayName} 不在同一张地图。"
            };
        }

        double distanceTiles = ComputeTileDistance(
            speakerNpc.TilePoint.X,
            speakerNpc.TilePoint.Y,
            targetNpc.TilePoint.X,
            targetNpc.TilePoint.Y);
        int radiusTiles = this.GetNpcPerceptionRadiusTiles();
        if (distanceTiles > radiusTiles)
        {
            return new NpcSyncTargetValidationResult
            {
                Ok = false,
                Error = $"目标 NPC {targetNpc.displayName} 已离开感知半径。"
            };
        }

        return new NpcSyncTargetValidationResult
        {
            Ok = true,
            TargetNpcName = targetNpc.Name,
            TargetDisplayName = targetNpc.displayName,
            MapName = targetNpc.currentLocation?.NameOrUniqueName ?? string.Empty,
            TileX = targetNpc.TilePoint.X,
            TileY = targetNpc.TilePoint.Y,
            DistanceTiles = Math.Round(distanceTiles, 2)
        };
    }

    private void ApplyNpcSyncSpeechDirective(NPC speakerNpc, NpcActionRequest actionRequest)
    {
        NpcSyncTargetValidationResult validation = this.ValidateNpcSyncSpeechTarget(speakerNpc.Name, actionRequest.TargetNpcName);
        if (!validation.Ok)
        {
            this.logger.Warn("Sync", $"NPC-NPC 对话落地失败 target={actionRequest.TargetNpcName} error={validation.Error}", speakerNpc.Name);
            if (!string.IsNullOrWhiteSpace(actionRequest.SyncPairKey))
            {
                this.ReleaseSyncPair(actionRequest.SyncPairKey, preserveCooldown: true);
            }

            return;
        }

        NPC? targetNpc = Game1.getCharacterFromName(validation.TargetNpcName);
        if (targetNpc is null)
        {
            return;
        }

        this.ShowNpcChatBubble(speakerNpc.Name, targetNpc.Name, actionRequest.Message);
        this.AppendNpcToNpcSpeechMemory(speakerNpc, targetNpc, actionRequest.Message, actionRequest.SyncPairKey);

        if (string.IsNullOrWhiteSpace(actionRequest.SyncPairKey))
        {
            this.RegisterDirectNpcSpeechCooldown(speakerNpc, targetNpc);
        }
    }

    private void RegisterDirectNpcSpeechCooldown(NPC speakerNpc, NPC targetNpc)
    {
        NpcSyncPairRuntimeState pairState = this.GetOrCreateSyncPairState(speakerNpc.Name, targetNpc.Name);
        string mapName = speakerNpc.currentLocation?.NameOrUniqueName ?? string.Empty;
        pairState.Cooldown.MapName = mapName;
        pairState.Cooldown.LastTriggeredAtUtc = DateTimeOffset.UtcNow;
        pairState.Conversation.IsActive = false;
        pairState.Conversation.MapName = string.Empty;
        pairState.Conversation.InitiatorNpcName = string.Empty;
        pairState.Perception.IsWithinPerceptionRadius = true;
        pairState.Perception.MapName = mapName;
        pairState.Perception.EncounterTriggeredInCurrentSession = true;
        this.logger.Debug("Sync", $"直接 NPC-NPC 对话已占用 pair={pairState.PairKey}，本轮感知会话内不再自动触发重复同步。", speakerNpc.Name);
    }

    private void OnSyncRequestCompleted(AgentRequestResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SyncPairKey))
        {
            return;
        }

        if (IsNpcSyncEncounterEventType(result.Trigger))
        {
            this.ReleaseSyncPair(result.SyncPairKey, preserveCooldown: true);
        }
    }

    private void OnSyncRequestCancelled(NpcActiveRequestRuntime activeRequest)
    {
        this.ReleaseSyncPairForFailure(activeRequest.SyncPairKey, activeRequest.TriggerEvent.EventType);
    }

    private void OnSyncRequestFailed(NpcActiveRequestRuntime activeRequest)
    {
        this.ReleaseSyncPairForFailure(activeRequest.SyncPairKey, activeRequest.TriggerEvent.EventType);
    }

    private void ReleaseSyncPairForFailure(string syncPairKey, string? triggerEventType)
    {
        if (string.IsNullOrWhiteSpace(syncPairKey))
        {
            return;
        }

        if (!this.syncPairStates.ContainsKey(syncPairKey))
        {
            return;
        }

        if (IsNpcSyncEncounterEventType(triggerEventType))
        {
            this.ReleaseSyncPair(syncPairKey, preserveCooldown: true);
        }
    }

    private void ReleaseSyncPair(string syncPairKey, bool preserveCooldown)
    {
        if (string.IsNullOrWhiteSpace(syncPairKey) || !this.syncPairStates.TryGetValue(syncPairKey, out NpcSyncPairRuntimeState? pairState))
        {
            return;
        }

        pairState.Conversation.IsActive = false;
        pairState.Conversation.MapName = string.Empty;
        pairState.Conversation.InitiatorNpcName = string.Empty;
        if (!preserveCooldown)
        {
            pairState.Cooldown.LastTriggeredAtUtc = DateTimeOffset.MinValue;
            pairState.Cooldown.MapName = string.Empty;
        }
    }

    private void ReleaseAllSyncPairsForNpc(string npcName, bool preserveCooldown)
    {
        foreach (NpcSyncPairRuntimeState pairState in this.syncPairStates.Values)
        {
            if (!string.Equals(pairState.NpcAName, npcName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(pairState.NpcBName, npcName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pairState.Perception.IsWithinPerceptionRadius = false;
            pairState.Perception.MapName = string.Empty;
            pairState.Perception.EncounterTriggeredInCurrentSession = false;
            this.ReleaseSyncPair(pairState.PairKey, preserveCooldown);
        }
    }

    private void AppendNpcSyncEncounterMemory(NPC initiator, NPC receiver, string syncPairKey)
    {
        string locationName = initiator.currentLocation?.NameOrUniqueName ?? string.Empty;
        Dictionary<string, string> initiatorMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["other_npc_name"] = receiver.Name,
            ["other_npc_display_name"] = receiver.displayName,
            ["sync_pair_key"] = syncPairKey,
            ["location"] = locationName
        };
        Dictionary<string, string> receiverMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["other_npc_name"] = initiator.Name,
            ["other_npc_display_name"] = initiator.displayName,
            ["sync_pair_key"] = syncPairKey,
            ["location"] = locationName
        };

        MemoryRecord initiatorRecord = this.memoryStore.AppendEventRecord(
            initiator.Name,
            "npc_sync_encounter",
            $"在 {locationName} 遇见了 {receiver.displayName}（{receiver.Name}）。",
            initiatorMetadata);
        MemoryRecord receiverRecord = this.memoryStore.AppendEventRecord(
            receiver.Name,
            "npc_sync_encounter",
            $"在 {locationName} 遇见了 {initiator.displayName}（{initiator.Name}）。",
            receiverMetadata);
        _ = this.memoryStore.TryEmbedRecordAsync(initiator.Name, initiatorRecord, CancellationToken.None);
        _ = this.memoryStore.TryEmbedRecordAsync(receiver.Name, receiverRecord, CancellationToken.None);
    }

    private void AppendNpcToNpcSpeechMemory(NPC speakerNpc, NPC targetNpc, string message, string syncPairKey)
    {
        Dictionary<string, string> speakerMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["other_npc_name"] = targetNpc.Name,
            ["other_npc_display_name"] = targetNpc.displayName,
            ["sync_pair_key"] = syncPairKey
        };
        Dictionary<string, string> targetMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["other_npc_name"] = speakerNpc.Name,
            ["other_npc_display_name"] = speakerNpc.displayName,
            ["sync_pair_key"] = syncPairKey
        };

        MemoryRecord speakerRecord = this.memoryStore.AppendEventRecord(
            speakerNpc.Name,
            "npc_to_npc_speech",
            $"对 {targetNpc.displayName}（{targetNpc.Name}）说：{message}",
            speakerMetadata);
        MemoryRecord targetRecord = this.memoryStore.AppendEventRecord(
            targetNpc.Name,
            "npc_to_npc_speech",
            $"{speakerNpc.displayName}（{speakerNpc.Name}）对你说：{message}",
            targetMetadata);
        _ = this.memoryStore.TryEmbedRecordAsync(speakerNpc.Name, speakerRecord, CancellationToken.None);
        _ = this.memoryStore.TryEmbedRecordAsync(targetNpc.Name, targetRecord, CancellationToken.None);
    }

    private string BuildNpcSyncSystemPrompt(
        NpcAgentEvent agentEvent,
        NpcAgentPromptSnapshot snapshot,
        IReadOnlyList<string> basicProfileLines,
        string personalitySource,
        string personalityMarkdown,
        IReadOnlyList<string> factLines,
        IReadOnlyList<string> memoryLines,
        NpcAgentRuntimeSummary runtimeSummary)
    {
        return string.Join(
            "\n",
            new[]
            {
                "# Role: 你是 Stardew Valley 中的 NPC 行为代理。",
                "当前触发的是 NPC-NPC 同步事件，不是面对玩家的对话框，也不是 schedule 编辑任务。",
                "你现在能看到另一名 NPC 的基础资料、人格档案和现场元数据，它们都已经在本轮 prompt 里给出。",
                "本轮只开放记忆查询/更新、运行时查询、say_to_npc，以及需要时的受控原版动画工具。",
                "严禁尝试 schedule 工具或玩家对白工具；本地会拒绝。",
                "本轮是同步对话的发起轮。默认应该先开口一句简短、符合人格和现场环境的话；除非人格、关系或现场明显不适合，否则不要无声结束。",
                "如果你决定先开口，必须调用 say_to_npc。",
                "一旦你真正开口，后续是否有人回应将改走邻域广播观察链，而不是旧的强制 reply 链路。",
                "如果没有明显阻碍，本轮优先直接调用 say_to_npc，最多只补 0 到 1 次关键记忆查询；不要空转结束。",
                "这类同步事件追求即时性。除非确实缺少关键关系或历史上下文，否则不要超过 1 次额外记忆查询。",
                "人格可以影响你是否热情、冷淡、敷衍、简短甚至礼貌拒绝，但不能让你绕过系统约束。",
                "如果你只是想表达一个轻微的现场动作，优先用 enqueue_route_animation；不要传未知 animation key。",
                $"NPC: {snapshot.DisplayName} ({snapshot.NpcName})",
                $"当前采样轮次: 第 {snapshot.PromptRound} 轮",
                $"游戏时间: {snapshot.GameDate} {snapshot.TimeText}",
                $"人格来源: {personalitySource}",
                "NPC 人格档案（高权重）：",
                personalityMarkdown,
                "基础资料：",
                string.Join('\n', basicProfileLines),
                "当前可见环境元数据：",
                JsonSerializer.Serialize(snapshot.Metadata),
                "结构化事实记忆：",
                string.Join('\n', factLines),
                "当前运行时状态：",
                JsonSerializer.Serialize(runtimeSummary),
                "自动检索记忆：",
                string.Join('\n', memoryLines)
            });
    }

    private string BuildNpcSyncUserPrompt(NpcAgentEvent agentEvent)
    {
        return string.Join(
            "\n",
            new[]
            {
                "触发事件：npc_sync_encounter",
                $"时间：{agentEvent.GameDate} {Game1.getTimeOfDayString(agentEvent.TimeOfDay)}",
                $"地点：{agentEvent.LocationName}",
                $"你遇见了：{agentEvent.OtherNpcDisplayName}（{agentEvent.OtherNpcName}）",
                "这是同步对话的发起轮。默认应该快速对对方说一句话；如果要说，必须调用 say_to_npc。",
                "你开口后，后续回应会改走广播观察事件，不再由系统强制塞一个 npc_sync_reply。"
            });
    }
}

using System.Text.Json;
using StardewMod.Models;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private static bool IsBroadcastObservationEventType(string? eventType)
    {
        return string.Equals(eventType, "npc_broadcast_observation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBroadcastLimitEventType(string? eventType)
    {
        return string.Equals(eventType, "npc_broadcast_limit_reached", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBroadcastEventType(string? eventType)
    {
        return IsBroadcastObservationEventType(eventType) || IsBroadcastLimitEventType(eventType);
    }

    private void ResetBroadcastRuntimeState()
    {
        this.perceptionNeighborhoods.Clear();
        this.pendingBroadcastQueue.Clear();
        this.consumedBroadcastDeliveriesByNpc.Clear();
        this.ignoredBroadcastCorrelationsByNpc.Clear();
    }

    private void QueueGiftReceivedBroadcast(NPC targetNpc, Farmer giver, SObject? gift)
    {
        NpcPerceptionNeighborhood neighborhood = this.GetNeighborhood(targetNpc.Name, preferLive: true);
        List<string> recipients = neighborhood.NearbyNpcs
            .Select(neighbor => neighbor.NpcName)
            .Where(npcName => !string.Equals(npcName, targetNpc.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        string giftName = gift?.DisplayName ?? string.Empty;
        string correlationId = Guid.NewGuid().ToString("N");
        this.pendingBroadcastQueue.Enqueue(new NpcBroadcastDispatchItem
        {
            BroadcastId = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            Hop = 1,
            MaxHops = Math.Max(1, this.configService.Current.Broadcast.MaxHops),
            SourceKind = NpcBroadcastSourceKind.Native,
            SourceName = "gift_received",
            SenderActorType = NpcBroadcastSenderActorType.Player,
            SenderName = giver.Name,
            MapName = neighborhood.MapName,
            TargetNpcName = targetNpc.Name,
            RecipientNpcNames = recipients,
            MentionedNpcNames = new List<string> { targetNpc.Name },
            SummaryText = string.IsNullOrWhiteSpace(giftName)
                ? $"{giver.Name} 刚在 {neighborhood.MapName} 给了 {targetNpc.displayName} 一份礼物。"
                : $"{giver.Name} 刚在 {neighborhood.MapName} 给了 {targetNpc.displayName} 礼物：{giftName}。",
            Payload = new NpcBroadcastPayload
            {
                NativeEventName = "gift_received",
                GiftItemName = giftName,
                TargetNpcName = targetNpc.Name,
                TargetNpcDisplayName = targetNpc.displayName,
                SummaryHint = "player_gift_visible_event"
            }
        });
        this.logger.Info("Broadcast", $"入队原生广播 source=gift_received sender={giver.Name} target={targetNpc.Name} recipients={recipients.Count}", targetNpc.Name);
    }

    private void QueueActionBroadcast(NPC senderNpc, NpcActionRequest actionRequest)
    {
        if (!actionRequest.BroadcastToNearbyNpcs)
        {
            return;
        }

        NpcPerceptionNeighborhood neighborhood = this.GetNeighborhood(senderNpc.Name, preferLive: true);
        List<string> recipients = neighborhood.NearbyNpcs
            .Select(neighbor => neighbor.NpcName)
            .Where(npcName => !string.Equals(npcName, senderNpc.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        string targetDisplayName = string.Empty;
        if (!string.IsNullOrWhiteSpace(actionRequest.TargetNpcName))
        {
            targetDisplayName = Game1.getCharacterFromName(actionRequest.TargetNpcName)?.displayName ?? actionRequest.TargetNpcName;
        }

        if (actionRequest.BroadcastHop > actionRequest.BroadcastMaxHops)
        {
            this.QueueBroadcastLimitReached(senderNpc, actionRequest, neighborhood, recipients);
            return;
        }

        List<string> mentionedNpcNames = actionRequest.MentionedNpcNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        this.pendingBroadcastQueue.Enqueue(new NpcBroadcastDispatchItem
        {
            BroadcastId = Guid.NewGuid().ToString("N"),
            CorrelationId = string.IsNullOrWhiteSpace(actionRequest.BroadcastCorrelationId)
                ? Guid.NewGuid().ToString("N")
                : actionRequest.BroadcastCorrelationId,
            Hop = Math.Max(1, actionRequest.BroadcastHop),
            MaxHops = Math.Max(1, actionRequest.BroadcastMaxHops),
            SourceKind = NpcBroadcastSourceKind.Tool,
            SourceName = string.IsNullOrWhiteSpace(actionRequest.SourceToolName) ? actionRequest.Type.ToString() : actionRequest.SourceToolName,
            SenderActorType = NpcBroadcastSenderActorType.Npc,
            SenderName = senderNpc.Name,
            MapName = neighborhood.MapName,
            TargetNpcName = actionRequest.TargetNpcName,
            RecipientNpcNames = recipients,
            MentionedNpcNames = mentionedNpcNames,
            SummaryText = this.BuildActionBroadcastSummary(senderNpc, actionRequest, targetDisplayName),
            Payload = new NpcBroadcastPayload
            {
                ActionType = actionRequest.Type.ToString(),
                Message = actionRequest.Message,
                TargetNpcName = actionRequest.TargetNpcName,
                TargetNpcDisplayName = targetDisplayName,
                SummaryHint = actionRequest.BroadcastSummaryHint,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source_tool_name"] = actionRequest.SourceToolName,
                    ["reason"] = actionRequest.Reason
                }
            }
        });
        this.logger.Info("Broadcast", $"入队工具广播 source={actionRequest.SourceToolName} hop={actionRequest.BroadcastHop}/{actionRequest.BroadcastMaxHops} recipients={recipients.Count}", senderNpc.Name);
    }

    private void QueueBroadcastLimitReached(
        NPC senderNpc,
        NpcActionRequest actionRequest,
        NpcPerceptionNeighborhood neighborhood,
        List<string> recipients)
    {
        this.pendingBroadcastQueue.Enqueue(new NpcBroadcastDispatchItem
        {
            BroadcastId = Guid.NewGuid().ToString("N"),
            CorrelationId = string.IsNullOrWhiteSpace(actionRequest.BroadcastCorrelationId)
                ? Guid.NewGuid().ToString("N")
                : actionRequest.BroadcastCorrelationId,
            Hop = Math.Max(1, actionRequest.BroadcastHop),
            MaxHops = Math.Max(1, actionRequest.BroadcastMaxHops),
            SourceKind = NpcBroadcastSourceKind.System,
            SourceName = "broadcast_limit_reached",
            SenderActorType = NpcBroadcastSenderActorType.System,
            SenderName = "system",
            MapName = neighborhood.MapName,
            TargetNpcName = actionRequest.TargetNpcName,
            RecipientNpcNames = recipients,
            MentionedNpcNames = actionRequest.MentionedNpcNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SummaryText = $"围绕 {senderNpc.displayName} 的公开广播已达到扩散上限，当前这跳不会继续向外传播。",
            Payload = new NpcBroadcastPayload
            {
                ActionType = actionRequest.Type.ToString(),
                Message = actionRequest.Message,
                TargetNpcName = actionRequest.TargetNpcName,
                SummaryHint = actionRequest.BroadcastSummaryHint,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["attempted_source_tool_name"] = actionRequest.SourceToolName,
                    ["attempted_sender_name"] = senderNpc.Name
                }
            }
        });
        this.logger.Info("Broadcast", $"广播达到 hop 上限 correlation={actionRequest.BroadcastCorrelationId} hop={actionRequest.BroadcastHop}/{actionRequest.BroadcastMaxHops}", senderNpc.Name);
    }

    private void DrainPendingBroadcastQueue()
    {
        while (this.pendingBroadcastQueue.Count > 0)
        {
            NpcBroadcastDispatchItem dispatchItem = this.pendingBroadcastQueue.Dequeue();
            this.DispatchBroadcastItem(dispatchItem);
        }
    }

    private void DispatchBroadcastItem(NpcBroadcastDispatchItem dispatchItem)
    {
        foreach (string recipientNpcName in dispatchItem.RecipientNpcNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!this.ShouldDeliverBroadcast(dispatchItem, recipientNpcName, out NpcAgentSettings? recipientSettings))
            {
                continue;
            }

            this.MarkBroadcastDelivered(dispatchItem, recipientNpcName);
            NpcAgentEvent broadcastEvent = this.BuildBroadcastEvent(dispatchItem, recipientNpcName, recipientSettings!);
            this.EnqueueEvent(recipientNpcName, broadcastEvent, interruptInflight: false);
        }
    }

    private bool ShouldDeliverBroadcast(NpcBroadcastDispatchItem dispatchItem, string recipientNpcName, out NpcAgentSettings? recipientSettings)
    {
        recipientSettings = null;
        if (string.Equals(dispatchItem.SenderName, recipientNpcName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (this.IsBroadcastIgnoredByNpc(recipientNpcName, dispatchItem.CorrelationId))
        {
            return false;
        }

        if (this.HasBroadcastBeenDelivered(recipientNpcName, dispatchItem))
        {
            return false;
        }

        recipientSettings = this.GetSettings(recipientNpcName);
        if (!recipientSettings.Enabled || !this.IsProviderUsable(recipientSettings) || !this.IsWithinActiveWindow(recipientSettings))
        {
            return false;
        }

        NPC? recipientNpc = Game1.getCharacterFromName(recipientNpcName);
        if (recipientNpc?.currentLocation is null)
        {
            return false;
        }

        return true;
    }

    private NpcAgentEvent BuildBroadcastEvent(NpcBroadcastDispatchItem dispatchItem, string recipientNpcName, NpcAgentSettings recipientSettings)
    {
        bool isLimitReached = dispatchItem.SourceKind == NpcBroadcastSourceKind.System &&
            string.Equals(dispatchItem.SourceName, "broadcast_limit_reached", StringComparison.OrdinalIgnoreCase);
        string eventType = isLimitReached
            ? "npc_broadcast_limit_reached"
            : "npc_broadcast_observation";
        NpcAgentEvent agentEvent = this.BuildEvent(
            recipientNpcName,
            eventType,
            dispatchItem.SummaryText,
            dispatchItem.Payload.Message,
            dispatchItem.Payload.GiftItemName,
            0,
            recipientSettings);
        agentEvent.LocationName = dispatchItem.MapName;
        agentEvent.OtherNpcName = dispatchItem.TargetNpcName;
        agentEvent.OtherNpcDisplayName = dispatchItem.Payload.TargetNpcDisplayName;
        agentEvent.OtherNpcMessage = dispatchItem.Payload.Message;
        agentEvent.Metadata["trigger_kind"] = "broadcast";
        agentEvent.Metadata["broadcast_source_name"] = dispatchItem.SourceName;
        agentEvent.BroadcastContext = this.BuildBroadcastContext(dispatchItem, recipientNpcName, isLimitReached);
        return agentEvent;
    }

    private NpcBroadcastContext BuildBroadcastContext(NpcBroadcastDispatchItem dispatchItem, string recipientNpcName, bool isLimitReached)
    {
        bool isNamedInSummaryOrMentions = dispatchItem.MentionedNpcNames.Contains(recipientNpcName, StringComparer.OrdinalIgnoreCase);
        NPC? recipientNpc = Game1.getCharacterFromName(recipientNpcName);
        if (!isNamedInSummaryOrMentions && recipientNpc is not null)
        {
            isNamedInSummaryOrMentions =
                dispatchItem.SummaryText.Contains(recipientNpcName, StringComparison.OrdinalIgnoreCase) ||
                dispatchItem.SummaryText.Contains(recipientNpc.displayName, StringComparison.OrdinalIgnoreCase);
        }

        NpcBroadcastContext context = new()
        {
            BroadcastId = dispatchItem.BroadcastId,
            CorrelationId = dispatchItem.CorrelationId,
            Hop = dispatchItem.Hop,
            MaxHops = dispatchItem.MaxHops,
            SourceKind = dispatchItem.SourceKind,
            SourceName = dispatchItem.SourceName,
            SenderActorType = dispatchItem.SenderActorType,
            SenderName = dispatchItem.SenderName,
            MapName = dispatchItem.MapName,
            RecipientNpcName = recipientNpcName,
            TargetNpcName = dispatchItem.TargetNpcName,
            IsDirectTarget = string.Equals(dispatchItem.TargetNpcName, recipientNpcName, StringComparison.OrdinalIgnoreCase),
            IsNamedInSummaryOrMentions = isNamedInSummaryOrMentions,
            RecipientNpcNames = dispatchItem.RecipientNpcNames.ToList(),
            MentionedNpcNames = dispatchItem.MentionedNpcNames.ToList(),
            SummaryText = dispatchItem.SummaryText,
            Payload = CloneBroadcastPayload(dispatchItem.Payload)
        };
        if (isLimitReached)
        {
            context.StopContext = new NpcBroadcastStopContext
            {
                Reason = "max_hops_reached",
                AttemptedSourceName = dispatchItem.Payload.Metadata.TryGetValue("attempted_source_tool_name", out string? sourceName)
                    ? sourceName
                    : string.Empty,
                AttemptedSenderName = dispatchItem.Payload.Metadata.TryGetValue("attempted_sender_name", out string? senderName)
                    ? senderName
                    : string.Empty,
                AttemptedHop = dispatchItem.Hop
            };
        }

        return context;
    }

    private static NpcBroadcastPayload CloneBroadcastPayload(NpcBroadcastPayload source)
    {
        return new NpcBroadcastPayload
        {
            ActionType = source.ActionType,
            Message = source.Message,
            TargetNpcName = source.TargetNpcName,
            TargetNpcDisplayName = source.TargetNpcDisplayName,
            GiftItemName = source.GiftItemName,
            NativeEventName = source.NativeEventName,
            SummaryHint = source.SummaryHint,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private bool HasBroadcastBeenDelivered(string recipientNpcName, NpcBroadcastDispatchItem dispatchItem)
    {
        if (!this.consumedBroadcastDeliveriesByNpc.TryGetValue(recipientNpcName, out HashSet<string>? consumedKeys))
        {
            return false;
        }

        return consumedKeys.Contains(this.BuildBroadcastDeliveryKey(dispatchItem));
    }

    private void MarkBroadcastDelivered(NpcBroadcastDispatchItem dispatchItem, string recipientNpcName)
    {
        if (!this.consumedBroadcastDeliveriesByNpc.TryGetValue(recipientNpcName, out HashSet<string>? consumedKeys))
        {
            consumedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            this.consumedBroadcastDeliveriesByNpc[recipientNpcName] = consumedKeys;
        }

        consumedKeys.Add(this.BuildBroadcastDeliveryKey(dispatchItem));
    }

    private void RegisterIgnoredBroadcastCorrelation(string npcName, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(correlationId))
        {
            return;
        }

        if (!this.ignoredBroadcastCorrelationsByNpc.TryGetValue(npcName, out HashSet<string>? ignoredCorrelations))
        {
            ignoredCorrelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            this.ignoredBroadcastCorrelationsByNpc[npcName] = ignoredCorrelations;
        }

        ignoredCorrelations.Add(correlationId);
    }

    private bool IsBroadcastIgnoredByNpc(string npcName, string correlationId)
    {
        return this.ignoredBroadcastCorrelationsByNpc.TryGetValue(npcName, out HashSet<string>? ignoredCorrelations) &&
            ignoredCorrelations.Contains(correlationId);
    }

    private string BuildBroadcastDeliveryKey(NpcBroadcastDispatchItem dispatchItem)
    {
        return string.Join(
            "|",
            dispatchItem.CorrelationId,
            dispatchItem.Hop.ToString(),
            dispatchItem.SenderName,
            dispatchItem.SourceName);
    }

    private string BuildActionBroadcastSummary(NPC senderNpc, NpcActionRequest actionRequest, string targetDisplayName)
    {
        return actionRequest.Type switch
        {
            NpcActionRequestType.SpeakToNpc when !string.IsNullOrWhiteSpace(targetDisplayName) =>
                $"{senderNpc.displayName} 对 {targetDisplayName} 说：{actionRequest.Message}",
            NpcActionRequestType.SpeakToPlayer =>
                $"{senderNpc.displayName} 刚对玩家说：{actionRequest.Message}",
            NpcActionRequestType.DoEmote when !string.IsNullOrWhiteSpace(actionRequest.EmoteName) =>
                $"{senderNpc.displayName} 做了一个 {actionRequest.EmoteName} 表情。",
            NpcActionRequestType.PlayRouteAnimation when !string.IsNullOrWhiteSpace(actionRequest.AnimationName) =>
                $"{senderNpc.displayName} 做了一个 {actionRequest.AnimationName} 动画。",
            NpcActionRequestType.FacePlayer =>
                $"{senderNpc.displayName} 转身看向了玩家。",
            NpcActionRequestType.PauseAndWait =>
                $"{senderNpc.displayName} 停顿了一下。",
            NpcActionRequestType.MoveToTile =>
                $"{senderNpc.displayName} 朝 {actionRequest.TargetLocationName} 的 ({actionRequest.TargetTile.X}, {actionRequest.TargetTile.Y}) 走去。",
            _ => $"{senderNpc.displayName} 执行了一个公开可见动作。"
        };
    }

    private string BuildBroadcastSystemPrompt(
        NpcAgentEvent agentEvent,
        NpcAgentPromptSnapshot snapshot,
        IReadOnlyList<string> basicProfileLines,
        string personalitySource,
        string personalityMarkdown,
        IReadOnlyList<string> factLines,
        IReadOnlyList<string> memoryLines,
        NpcAgentRuntimeSummary runtimeSummary)
    {
        NpcBroadcastContext broadcastContext = agentEvent.BroadcastContext!;
        bool isLimitReached = IsBroadcastLimitEventType(agentEvent.EventType);
        string eventRoleLine = isLimitReached
            ? "当前触发的是广播停止事件。广播已经达到扩散上限，你不能继续外扩。"
            : "当前触发的是邻域广播观察事件。你是这条公开可见消息的旁观者或直接对象。";
        string responseLine = isLimitReached
            ? "你可以选择做一句收束性的本地回应、沉淀记忆，或显式调用 ignore_current_broadcast。即使你调用带广播能力的动作工具，也会被本地强制改成不再广播。"
            : "你可以选择不回应、只做记忆沉淀，或做本地回应。是否回应完全由你决定，本地不会只保留一个回应者。";

        return string.Join(
            "\n",
            new[]
            {
                "# Role: 你是 Stardew Valley 中的 NPC 行为代理。",
                eventRoleLine,
                "广播事件是观察级、非打断事件。它不会抢占玩家对话链，也不代表你必须马上开口。",
                responseLine,
                "如果你决定回应，优先根据当前环境、人格、与你和发送者/目标的关系来判断是否值得回应。",
                "在这类事件里，可用工具只包含记忆查询/更新、运行态查询、可见动作、say_to_npc、npc_say_to_player 和 ignore_current_broadcast；严禁 schedule mutation。",
                "请显式参考以下结构化判断因子：is_direct_target、is_named_in_summary_or_mentions、sender_name、source_kind、source_name、hop、max_hops、current_event_type。",
                "如果广播与你关系不大，或者你觉得没必要回应，直接结束即可；不要为了显得活跃而强行插话。",
                $"NPC: {snapshot.DisplayName} ({snapshot.NpcName})",
                $"当前采样轮次: 第 {snapshot.PromptRound} 轮",
                $"游戏时间: {snapshot.GameDate} {snapshot.TimeText}",
                $"人格来源: {personalitySource}",
                "广播结构化上下文：",
                JsonSerializer.Serialize(new
                {
                    current_event_type = agentEvent.EventType,
                    is_direct_target = broadcastContext.IsDirectTarget,
                    is_named_in_summary_or_mentions = broadcastContext.IsNamedInSummaryOrMentions,
                    sender_name = broadcastContext.SenderName,
                    sender_actor_type = broadcastContext.SenderActorType.ToString().ToLowerInvariant(),
                    source_kind = broadcastContext.SourceKind.ToString().ToLowerInvariant(),
                    source_name = broadcastContext.SourceName,
                    hop = broadcastContext.Hop,
                    max_hops = broadcastContext.MaxHops,
                    summary_text = broadcastContext.SummaryText,
                    mentioned_npc_names = broadcastContext.MentionedNpcNames,
                    target_npc_name = broadcastContext.TargetNpcName,
                    payload = broadcastContext.Payload,
                    stop_context = broadcastContext.StopContext
                }),
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

    private string BuildBroadcastUserPrompt(NpcAgentEvent agentEvent)
    {
        NpcBroadcastContext broadcastContext = agentEvent.BroadcastContext!;
        return string.Join(
            "\n",
            new[]
            {
                $"触发事件：{agentEvent.EventType}",
                $"时间：{agentEvent.GameDate} {Game1.getTimeOfDayString(agentEvent.TimeOfDay)}",
                $"地点：{agentEvent.LocationName}",
                $"sender_name：{broadcastContext.SenderName}",
                $"source_kind：{broadcastContext.SourceKind.ToString().ToLowerInvariant()}",
                $"source_name：{broadcastContext.SourceName}",
                $"hop：{broadcastContext.Hop}",
                $"max_hops：{broadcastContext.MaxHops}",
                $"is_direct_target：{broadcastContext.IsDirectTarget}",
                $"is_named_in_summary_or_mentions：{broadcastContext.IsNamedInSummaryOrMentions}",
                $"summary：{broadcastContext.SummaryText}",
                "你正在处理一条公开可见广播。你可以不回应，也可以本地回应，但不要把自己当成必须接话的强制轮。"
            });
    }
}

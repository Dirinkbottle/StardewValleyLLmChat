using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private NpcAgentEvent? PrepareInterruptedRequestReplay(string npcName, NpcAgentRuntimeState state, string interruptingEventType)
    {
        this.PromoteInterruptedImmediateFeedbackToFront(npcName, state, interruptingEventType);
        if (state.ActiveRequest?.TriggerEvent is not NpcAgentEvent activeEvent ||
            !ShouldReplayInterruptedEvent(activeEvent.EventType, interruptingEventType))
        {
            return null;
        }

        NpcAgentEvent replayEvent = CloneAgentEvent(activeEvent);
        replayEvent.Metadata["replayed_after_interrupt"] = "true";
        replayEvent.Metadata["interrupting_event_type"] = interruptingEventType;
        return replayEvent;
    }

    private bool IsInflightCancellationPending(NpcAgentRuntimeState state)
    {
        return state.ActiveRequest is not null && state.ActiveRequest.Phase == NpcRequestPhase.Cancelling;
    }

    private bool ShouldPrependInterruptedImmediateFeedback(NpcAgentRuntimeState state, NpcImmediateFeedbackEvent feedbackEvent)
    {
        return state.ActiveRequest is not null &&
            string.Equals(state.ActiveRequest.RequestId, feedbackEvent.RequestId, StringComparison.OrdinalIgnoreCase) &&
            this.IsInflightCancellationPending(state);
    }

    private void PromoteInterruptedImmediateFeedbackToFront(string npcName, NpcAgentRuntimeState state, string interruptingEventType)
    {
        if (state.ActiveRequest is null || string.IsNullOrWhiteSpace(state.ActiveRequest.RequestId))
        {
            return;
        }

        List<NpcImmediateFeedbackEvent> matchingEvents = state.Queues.ExtractImmediateFeedbackForRequest(state.ActiveRequest.RequestId);

        for (int i = matchingEvents.Count - 1; i >= 0; i--)
        {
            NpcImmediateFeedbackEvent feedbackEvent = matchingEvents[i];
            this.RouteCommittedActionRequest(npcName, state, feedbackEvent.Action, fromImmediateFeedback: true, feedbackEvent.SourceToolName, prepend: true);
        }

        if (matchingEvents.Count == 0)
        {
            return;
        }

        state.PushDebugLine($"中断补偿：已把 {matchingEvents.Count} 个待落地即时动作插回队头。");
        this.logger.Debug(
            "Event",
            $"请求 {state.ActiveRequest.RequestId} 被 {interruptingEventType} 打断，已把 {matchingEvents.Count} 个待落地即时动作插回原队列头部。",
            npcName);
    }

    private static bool ShouldReplayInterruptedEvent(string? interruptedEventType, string interruptingEventType)
    {
        if (string.IsNullOrWhiteSpace(interruptedEventType) ||
            string.Equals(interruptedEventType, interruptingEventType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(interruptedEventType, "periodic_tick", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(interruptedEventType, "window_entered", StringComparison.OrdinalIgnoreCase);
    }

    private static NpcAgentEvent CloneAgentEvent(NpcAgentEvent source)
    {
        return new NpcAgentEvent
        {
            EventType = source.EventType,
            NpcName = source.NpcName,
            GameDate = source.GameDate,
            TimeOfDay = source.TimeOfDay,
            LocationName = source.LocationName,
            PlayerAction = source.PlayerAction,
            DialogueExcerpt = source.DialogueExcerpt,
            GiftItem = source.GiftItem,
            FriendshipDelta = source.FriendshipDelta,
            CurrentScheduleSummary = source.CurrentScheduleSummary,
            OtherNpcName = source.OtherNpcName,
            OtherNpcDisplayName = source.OtherNpcDisplayName,
            OtherNpcMessage = source.OtherNpcMessage,
            SyncPairKey = source.SyncPairKey,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase),
            BroadcastContext = source.BroadcastContext is null
                ? null
                : new NpcBroadcastContext
                {
                    BroadcastId = source.BroadcastContext.BroadcastId,
                    CorrelationId = source.BroadcastContext.CorrelationId,
                    Hop = source.BroadcastContext.Hop,
                    MaxHops = source.BroadcastContext.MaxHops,
                    SourceKind = source.BroadcastContext.SourceKind,
                    SourceName = source.BroadcastContext.SourceName,
                    SenderActorType = source.BroadcastContext.SenderActorType,
                    SenderName = source.BroadcastContext.SenderName,
                    MapName = source.BroadcastContext.MapName,
                    RecipientNpcName = source.BroadcastContext.RecipientNpcName,
                    TargetNpcName = source.BroadcastContext.TargetNpcName,
                    IsDirectTarget = source.BroadcastContext.IsDirectTarget,
                    IsNamedInSummaryOrMentions = source.BroadcastContext.IsNamedInSummaryOrMentions,
                    RecipientNpcNames = source.BroadcastContext.RecipientNpcNames.ToList(),
                    MentionedNpcNames = source.BroadcastContext.MentionedNpcNames.ToList(),
                    SummaryText = source.BroadcastContext.SummaryText,
                    Payload = new NpcBroadcastPayload
                    {
                        ActionType = source.BroadcastContext.Payload.ActionType,
                        Message = source.BroadcastContext.Payload.Message,
                        TargetNpcName = source.BroadcastContext.Payload.TargetNpcName,
                        TargetNpcDisplayName = source.BroadcastContext.Payload.TargetNpcDisplayName,
                        GiftItemName = source.BroadcastContext.Payload.GiftItemName,
                        NativeEventName = source.BroadcastContext.Payload.NativeEventName,
                        SummaryHint = source.BroadcastContext.Payload.SummaryHint,
                        Metadata = new Dictionary<string, string>(source.BroadcastContext.Payload.Metadata, StringComparer.OrdinalIgnoreCase)
                    },
                    StopContext = source.BroadcastContext.StopContext is null
                        ? null
                        : new NpcBroadcastStopContext
                        {
                            Reason = source.BroadcastContext.StopContext.Reason,
                            AttemptedSourceName = source.BroadcastContext.StopContext.AttemptedSourceName,
                            AttemptedSenderName = source.BroadcastContext.StopContext.AttemptedSenderName,
                            AttemptedHop = source.BroadcastContext.StopContext.AttemptedHop
                        }
                }
        };
    }
}

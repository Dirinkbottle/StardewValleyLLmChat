using System.Diagnostics;
using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private const int ImmediateFeedbackMaxItemsPerTick = 4;
    private const int ImmediateFeedbackMaxMillisecondsPerTick = 2;

    private bool PublishImmediateFeedbackEvent(NpcAgentRuntimeState state, NpcImmediateFeedbackEvent feedbackEvent)
    {
        if (!state.AcceptingAsyncFeedback || string.IsNullOrWhiteSpace(feedbackEvent.RequestId))
        {
            return false;
        }

        state.Queues.EnqueueImmediateFeedback(feedbackEvent);
        return true;
    }

    private void DrainImmediateFeedbackQueue(string npcName, NpcAgentRuntimeState state)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int processed = 0;
        List<NpcImmediateFeedbackEvent> prependEvents = new();
        List<NpcImmediateFeedbackEvent> appendEvents = new();
        while (processed < ImmediateFeedbackMaxItemsPerTick &&
               stopwatch.ElapsedMilliseconds < ImmediateFeedbackMaxMillisecondsPerTick &&
               state.Queues.TryDequeueImmediateFeedback(out NpcImmediateFeedbackEvent? feedbackEvent))
        {
            if (feedbackEvent is null)
            {
                continue;
            }

            processed++;
            if (!this.ShouldAcceptImmediateFeedback(state, feedbackEvent))
            {
                this.logger.Debug("Feedback", $"丢弃过期即时反馈 event={feedbackEvent.EventId} request={feedbackEvent.RequestId} source={feedbackEvent.SourceToolName}", npcName);
                continue;
            }

            if (this.ShouldPrependInterruptedImmediateFeedback(state, feedbackEvent))
            {
                prependEvents.Add(feedbackEvent);
            }
            else
            {
                appendEvents.Add(feedbackEvent);
            }
        }

        for (int i = prependEvents.Count - 1; i >= 0; i--)
        {
            NpcImmediateFeedbackEvent feedbackEvent = prependEvents[i];
            this.RouteCommittedActionRequest(npcName, state, feedbackEvent.Action, fromImmediateFeedback: true, feedbackEvent.SourceToolName, prepend: true);
        }

        foreach (NpcImmediateFeedbackEvent feedbackEvent in appendEvents)
        {
            this.RouteCommittedActionRequest(npcName, state, feedbackEvent.Action, fromImmediateFeedback: true, feedbackEvent.SourceToolName, prepend: false);
        }
    }

    private void FlushImmediateFeedbackForCompletedRequest(
        string npcName,
        NpcAgentRuntimeState state,
        NpcActiveRequestRuntime activeRequest)
    {
        List<NpcImmediateFeedbackEvent> feedbackEvents = state.Queues.ExtractImmediateFeedbackForRequest(activeRequest.RequestId);
        if (feedbackEvents.Count == 0)
        {
            return;
        }

        bool prepend = activeRequest.Phase == NpcRequestPhase.Cancelling;
        if (prepend)
        {
            for (int i = feedbackEvents.Count - 1; i >= 0; i--)
            {
                NpcImmediateFeedbackEvent feedbackEvent = feedbackEvents[i];
                this.RouteCommittedActionRequest(npcName, state, feedbackEvent.Action, fromImmediateFeedback: true, feedbackEvent.SourceToolName, prepend: true);
            }
        }
        else
        {
            foreach (NpcImmediateFeedbackEvent feedbackEvent in feedbackEvents)
            {
                this.RouteCommittedActionRequest(npcName, state, feedbackEvent.Action, fromImmediateFeedback: true, feedbackEvent.SourceToolName, prepend: false);
            }
        }

        this.logger.Debug(
            "Feedback",
            $"请求结束前提交剩余即时反馈 count={feedbackEvents.Count} request={activeRequest.RequestId} prepend={prepend}",
            npcName);
    }

    private bool ShouldAcceptImmediateFeedback(NpcAgentRuntimeState state, NpcImmediateFeedbackEvent feedbackEvent)
    {
        if (string.IsNullOrWhiteSpace(feedbackEvent.RequestId))
        {
            return false;
        }

        return state.ActiveRequest is not null &&
            string.Equals(state.ActiveRequest.RequestId, feedbackEvent.RequestId, StringComparison.OrdinalIgnoreCase);
    }

    private void RouteCommittedActionRequest(string npcName, NpcAgentRuntimeState state, NpcActionRequest actionRequest, bool fromImmediateFeedback, string sourceToolName = "", bool prepend = false)
    {
        string logChannel = fromImmediateFeedback ? "Feedback" : "Action";
        if (actionRequest.Type == NpcActionRequestType.SpeakToPlayer)
        {
            state.Queues.EnqueueSpeech(actionRequest, prepend);
            this.logger.Info(logChannel, $"排队对白：{this.logger.Summarize(actionRequest.Message, 120)} remaining_queue={state.Queues.PendingSpeechCount}", npcName);
            return;
        }

        if (actionRequest.DispatchMode == NpcActionDispatchMode.ImmediateFeedback)
        {
            state.Queues.EnqueueRealtimeAction(actionRequest, prepend);
            this.logger.Info(logChannel, $"排队实时动作：{this.BuildActionSummary(actionRequest)} source={sourceToolName}", npcName);
            return;
        }

        state.Queues.EnqueueDeferredAction(actionRequest, prepend);
        this.logger.Info(logChannel, $"排队延迟动作：{this.BuildActionSummary(actionRequest)} source={sourceToolName}", npcName);
    }
}

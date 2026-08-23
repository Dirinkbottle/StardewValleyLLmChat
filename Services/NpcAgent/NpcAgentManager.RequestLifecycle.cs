using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private bool HasInflightRequest(NpcAgentRuntimeState state)
    {
        return state.ActiveRequest?.Task is Task<AgentRequestResult> task && !task.IsCompleted;
    }

    private bool HasRequestAwaitingFinalization(NpcAgentRuntimeState state)
    {
        return state.ActiveRequest?.Task is not null;
    }

    private bool TryCancelActiveRequest(
        NpcAgentRuntimeState state,
        NpcRequestCancellationReason cancellationReason,
        string cancellationDetail)
    {
        if (!this.HasInflightRequest(state) || state.ActiveRequest is null)
        {
            return false;
        }

        if (state.ActiveRequest.Phase == NpcRequestPhase.Cancelling)
        {
            return false;
        }

        state.ActiveRequest.Phase = NpcRequestPhase.Cancelling;
        state.ActiveRequest.CancellationReason = cancellationReason;
        state.ActiveRequest.CancellationDetail = cancellationDetail;
        state.ActiveRequest.Cancellation?.Cancel();
        return true;
    }

    private void FinalizeActiveRequest(NpcAgentRuntimeState state)
    {
        if (state.ActiveRequest is null)
        {
            return;
        }

        state.ActiveRequest.Cancellation?.Dispose();
        state.ActiveRequest = null;
    }

    private void CancelAndDetachActiveRequestForTitle(NpcAgentRuntimeState state)
    {
        state.AcceptingAsyncFeedback = false;
        NpcActiveRequestRuntime? activeRequest = state.ActiveRequest;
        if (activeRequest is null)
        {
            state.Queues.ClearAll();
            return;
        }

        if (activeRequest.Phase != NpcRequestPhase.Cancelling)
        {
            activeRequest.Phase = NpcRequestPhase.Cancelling;
            activeRequest.CancellationReason = NpcRequestCancellationReason.ReturnedToTitle;
            activeRequest.CancellationDetail = "returned_to_title";
            activeRequest.Cancellation?.Cancel();
        }

        CancellationTokenSource? cancellation = activeRequest.Cancellation;
        Task<AgentRequestResult>? requestTask = activeRequest.Task;
        state.ActiveRequest = null;
        state.Queues.ClearAll();

        if (requestTask is null)
        {
            cancellation?.Dispose();
            return;
        }

        _ = requestTask.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    _ = completedTask.Exception;
                }

                cancellation?.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void QueueRuntimeReset(NpcAgentRuntimeState state, bool restoreBaseline, bool logChange, string reason)
    {
        if (state.PendingRuntimeReset is null)
        {
            state.PendingRuntimeReset = new NpcQueuedRuntimeReset
            {
                RestoreBaseline = restoreBaseline,
                LogChange = logChange,
                Reason = reason
            };
            return;
        }

        state.PendingRuntimeReset.RestoreBaseline |= restoreBaseline;
        state.PendingRuntimeReset.LogChange |= logChange;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            state.PendingRuntimeReset.Reason = reason;
        }
    }

    private bool TryApplyQueuedRuntimeReset(NpcAgentRuntimeState state)
    {
        if (state.PendingRuntimeReset is null || this.HasRequestAwaitingFinalization(state))
        {
            return false;
        }

        NpcQueuedRuntimeReset reset = state.PendingRuntimeReset;
        state.PendingRuntimeReset = null;
        state.Queues.ClearAll();
        state.WaitingForPlayerResponse = false;
        state.PausePeriodicUntilConversationSettles = false;
        state.AwaitingConversationDialogueClose = false;
        state.NextActionNotBeforeUtc = DateTimeOffset.MinValue;
        state.ActiveActionSummary = string.Empty;
        this.ReleaseAllSyncPairsForNpc(state.NpcName, preserveCooldown: false);

        if (Context.IsWorldReady)
        {
            NPC? loadedNpc = Game1.getCharacterFromName(state.NpcName);
            if (loadedNpc is not null)
            {
                loadedNpc.controller = null;
                loadedNpc.temporaryController = null;
                loadedNpc.Halt();
            }
        }

        state.ActivePatch = null;
        state.LastPatchSummary = string.Empty;
        if (reset.LogChange)
        {
            this.logger.Info("Agent", $"清空运行时覆盖 restore_baseline={reset.RestoreBaseline} reason={reset.Reason}", state.NpcName);
        }

        if (!reset.RestoreBaseline || !Context.IsWorldReady)
        {
            return true;
        }

        NPC? npc = Game1.getCharacterFromName(state.NpcName);
        if (npc is not null && state.BaselineRule is not null)
        {
            this.scheduleEditorService.TryApplyLiveRule(npc, state.BaselineRule, preserveCurrentMovement: true);
            this.logger.Info("Patch", $"已恢复基线规则 {state.BaselineRule.RuleKey}", state.NpcName);
        }

        return true;
    }
}

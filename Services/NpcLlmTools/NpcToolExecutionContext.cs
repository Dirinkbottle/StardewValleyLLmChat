using StardewMod.Models;

namespace StardewMod.Services;

internal sealed class NpcToolExecutionContext
{
    private readonly object toolCallsLock = new();
    private readonly object memoryHitsLock = new();

    public string RequestId { get; init; } = string.Empty;

    public NpcAgentPromptSnapshot Snapshot { get; set; } = new();

    public string NpcName { get; init; } = string.Empty;

    public Dictionary<string, string> BasicProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public NpcPersonalityProfile PersonalityProfile { get; set; } = new();

    public EditableScheduleRule WorkingRule { get; set; } = new();

    public NpcAgentEvent TriggerEvent { get; init; } = new();

    public NpcAgentRuntimeSummary RuntimeSummary { get; set; } = new();

    public NpcLlmMemoryStore MemoryStore { get; init; } = null!;

    public List<MemoryFactRecord> ActiveFacts { get; set; } = new();

    public Func<int, NpcPromptRefreshState>? RefreshSamplingContext { get; set; }

    public NpcToolAccessProfile ToolAccessProfile { get; init; } = NpcToolAccessProfile.Full;

    public int BroadcastMaxHops { get; init; } = 5;

    public bool AllowBehaviorControl { get; init; }

    public bool AllowSpeech { get; init; }

    public bool AllowNpcSpeech { get; init; }

    public bool AllowScheduleControl { get; init; }

    public bool ScheduleTouched { get; set; }

    public int? ApplyFromTime { get; set; }

    public string PatchReason { get; set; } = string.Empty;

    public Func<NpcImmediateFeedbackEvent, bool>? PublishImmediateFeedback { get; init; }

    public Func<string, NpcSyncTargetValidationResult>? ValidateNpcSpeechTarget { get; init; }

    public List<NpcActionRequest> DeferredActionRequests { get; } = new();

    public List<string> ToolCalls { get; } = new();

    public List<string> MemoryHits { get; } = new();

    public List<string> IgnoredBroadcastCorrelationIds { get; } = new();

    public bool RequestedSpeech { get; private set; }

    public bool RequestedNpcSpeech { get; private set; }

    public int ImmediateFeedbackCount { get; private set; }

    public bool IgnoreCurrentBroadcastInvoked { get; private set; }

    public void RefreshLiveSampling(int round)
    {
        if (this.RefreshSamplingContext is null)
        {
            return;
        }

        NpcPromptRefreshState refreshState = this.RefreshSamplingContext(round);
        this.Snapshot = refreshState.Snapshot;
        this.RuntimeSummary = refreshState.RuntimeSummary;
        this.BasicProfile = refreshState.BasicProfile;
        this.PersonalityProfile = refreshState.PersonalityProfile;
    }

    public void EnqueueActionRequest(string sourceToolName, NpcActionRequest actionRequest)
    {
        this.StampActionRequest(sourceToolName, actionRequest);
        actionRequest.DispatchMode = NpcActionDispatchPolicy.GetDefaultMode(actionRequest.Type);
        if (actionRequest.Type == NpcActionRequestType.SpeakToPlayer)
        {
            this.RequestedSpeech = true;
        }
        else if (actionRequest.Type == NpcActionRequestType.SpeakToNpc)
        {
            this.RequestedNpcSpeech = true;
        }

        if (actionRequest.DispatchMode == NpcActionDispatchMode.ImmediateFeedback &&
            this.PublishImmediateFeedback?.Invoke(new NpcImmediateFeedbackEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                RequestId = this.RequestId,
                SourceToolName = sourceToolName,
                Action = actionRequest
            }) == true)
        {
            this.ImmediateFeedbackCount++;
            return;
        }

        this.DeferredActionRequests.Add(actionRequest);
    }

    public bool TryIgnoreCurrentBroadcast()
    {
        NpcBroadcastContext? broadcastContext = this.TriggerEvent.BroadcastContext;
        if (broadcastContext is null || string.IsNullOrWhiteSpace(broadcastContext.CorrelationId))
        {
            return false;
        }

        this.IgnoreCurrentBroadcastInvoked = true;
        if (!this.IgnoredBroadcastCorrelationIds.Any(existing => string.Equals(existing, broadcastContext.CorrelationId, StringComparison.OrdinalIgnoreCase)))
        {
            this.IgnoredBroadcastCorrelationIds.Add(broadcastContext.CorrelationId);
        }

        return true;
    }

    public void RecordMemoryHits(IEnumerable<string> memoryIds)
    {
        lock (this.memoryHitsLock)
        {
            this.MemoryHits.AddRange(memoryIds.Where(memoryId => !string.IsNullOrWhiteSpace(memoryId)));
        }
    }

    public void RecordToolCall(string toolCallSummary)
    {
        if (string.IsNullOrWhiteSpace(toolCallSummary))
        {
            return;
        }

        lock (this.toolCallsLock)
        {
            this.ToolCalls.Add(toolCallSummary);
        }
    }

    private void StampActionRequest(string sourceToolName, NpcActionRequest actionRequest)
    {
        if (string.IsNullOrWhiteSpace(actionRequest.SourceToolName))
        {
            actionRequest.SourceToolName = sourceToolName;
        }

        NpcBroadcastContext? currentBroadcast = this.TriggerEvent.BroadcastContext;
        if (string.IsNullOrWhiteSpace(actionRequest.BroadcastCorrelationId))
        {
            actionRequest.BroadcastCorrelationId = currentBroadcast?.CorrelationId ?? Guid.NewGuid().ToString("N");
        }

        if (actionRequest.BroadcastHop <= 0)
        {
            actionRequest.BroadcastHop = currentBroadcast is null
                ? 1
                : currentBroadcast.Hop + 1;
        }

        if (actionRequest.BroadcastMaxHops <= 0)
        {
            actionRequest.BroadcastMaxHops = Math.Max(1, currentBroadcast?.MaxHops ?? this.BroadcastMaxHops);
        }

        if (this.IgnoreCurrentBroadcastInvoked ||
            string.Equals(this.TriggerEvent.EventType, "npc_broadcast_limit_reached", StringComparison.OrdinalIgnoreCase))
        {
            actionRequest.BroadcastToNearbyNpcs = false;
        }

        if (string.IsNullOrWhiteSpace(actionRequest.BroadcastSummaryHint))
        {
            actionRequest.BroadcastSummaryHint = !string.IsNullOrWhiteSpace(actionRequest.Message)
                ? actionRequest.Message
                : !string.IsNullOrWhiteSpace(actionRequest.Reason)
                    ? actionRequest.Reason
                    : actionRequest.Type.ToString();
        }

        if (!string.IsNullOrWhiteSpace(actionRequest.TargetNpcName) &&
            !actionRequest.MentionedNpcNames.Any(name => string.Equals(name, actionRequest.TargetNpcName, StringComparison.OrdinalIgnoreCase)))
        {
            actionRequest.MentionedNpcNames.Add(actionRequest.TargetNpcName);
        }
    }
}

internal readonly record struct ScheduleMutationPlan(
    int RequestedApplyFromTime,
    int EffectiveApplyFromTime,
    bool GuardApplied,
    string GuardMessage);

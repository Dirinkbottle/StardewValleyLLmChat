using System.Text.Json.Serialization;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewMod.Models;

/// <summary>
/// AI 菜单的 NPC 摘要。
/// </summary>
public sealed class NpcAgentMenuEntry
{
    public string InternalName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public Texture2D Portrait { get; init; } = null!;

    public bool Enabled { get; init; }

    public string ProviderName { get; init; } = string.Empty;

    public bool IsWithinActiveWindow { get; init; }

    public string ActiveWindowSummary { get; init; } = string.Empty;
}

/// <summary>
/// 运行时状态的可读摘要。
/// </summary>
public sealed class NpcAgentRuntimeSummary
{
    public string NpcName { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public bool IsWithinActiveWindow { get; set; }

    public string BaselineScheduleKey { get; set; } = string.Empty;

    public string PatchRevisionId { get; set; } = string.Empty;

    public string LastTrigger { get; set; } = string.Empty;

    public string LastRequestDuration { get; set; } = string.Empty;

    public string InflightStatus { get; set; } = string.Empty;

    public List<string> RecentToolCalls { get; set; } = new();

    public string LastPatchSummary { get; set; } = string.Empty;

    public string LastRejectionReason { get; set; } = string.Empty;

    public List<string> RecentDebugLines { get; set; } = new();

    public NpcLiveRuntimeSnapshot LiveState { get; set; } = new();

    public NpcScheduleExecutionSnapshot ScheduleState { get; set; } = new();

    public NpcConversationRuntimeSnapshot ConversationState { get; set; } = new();
}

/// <summary>
/// NPC 当前的现场状态。
/// </summary>
public sealed class NpcLiveRuntimeSnapshot
{
    public string LocationName { get; set; } = string.Empty;

    public int TileX { get; set; }

    public int TileY { get; set; }

    public int FacingDirection { get; set; }

    public bool IsMoving { get; set; }

    public bool IsSleeping { get; set; }

    public bool IsEmoting { get; set; }

    public int CurrentEmoteId { get; set; } = -1;

    public string CurrentEmoteName { get; set; } = string.Empty;

    public int CurrentEmoteFrameIndex { get; set; } = -1;

    public int MovementPauseMilliseconds { get; set; }

    public bool IsDoingRouteAnimation { get; set; }

    public bool IsGoingToDoRouteAnimation { get; set; }

    public string CurrentRouteAnimationName { get; set; } = string.Empty;

    public bool IgnoreScheduleToday { get; set; }

    public float CurrentScheduleDelay { get; set; }

    public bool HasDialogueStack { get; set; }

    public int DialogueLineCount { get; set; }

    public bool IsCurrentDialogueQuestion { get; set; }

    public string DialogueFile { get; set; } = string.Empty;

    public string DialogueKey { get; set; } = string.Empty;

    public string LoadedDialogueKey { get; set; } = string.Empty;

    public int Age { get; set; }

    public int Manners { get; set; }

    public int SocialAnxiety { get; set; }

    public int Optimism { get; set; }

    public int MoveTowardPlayerThreshold { get; set; }

    public string MoodHint { get; set; } = string.Empty;

    public NpcControllerRuntimeSnapshot ScheduleController { get; set; } = new();

    public NpcControllerRuntimeSnapshot TemporaryController { get; set; } = new();
}

/// <summary>
/// 控制器运行态快照。
/// </summary>
public sealed class NpcControllerRuntimeSnapshot
{
    public bool Active { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string LocationName { get; set; } = string.Empty;

    public int EndTileX { get; set; }

    public int EndTileY { get; set; }

    public int FinalFacingDirection { get; set; }

    public int RemainingPathNodes { get; set; }

    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// 当前 schedule 的执行态。
/// </summary>
public sealed class NpcScheduleExecutionSnapshot
{
    public string Source { get; set; } = string.Empty;

    public string RuleKey { get; set; } = string.Empty;

    public bool HasActiveRule { get; set; }

    public bool IsFollowingSchedulePath { get; set; }

    public bool IsExecutingActionRequest { get; set; }

    public bool IsUnderTemporaryController { get; set; }

    public int SafeMutationTime { get; set; } = 600;

    public bool CurrentExecutionProtected { get; set; }

    public string MutationGuidance { get; set; } = string.Empty;

    public NpcRuntimeScheduledStopSnapshot CurrentStop { get; set; } = new();

    public NpcRuntimeScheduledStopSnapshot NextStop { get; set; } = new();
}

/// <summary>
/// 当前/下一站的简化快照。
/// </summary>
public sealed class NpcRuntimeScheduledStopSnapshot
{
    public bool Exists { get; set; }

    public int Index { get; set; } = -1;

    public int EffectiveTime { get; set; }

    public int DeclaredTime { get; set; }

    public string TimeMode { get; set; } = string.Empty;

    public string LocationName { get; set; } = string.Empty;

    public int TargetTileX { get; set; }

    public int TargetTileY { get; set; }

    public int FacingDirection { get; set; }

    public string EndBehavior { get; set; } = string.Empty;

    public string EndMessage { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// 对话与动作队列运行态。
/// </summary>
public sealed class NpcConversationRuntimeSnapshot
{
    public bool WaitingForPlayerResponse { get; set; }

    public bool PausePeriodicUntilConversationSettles { get; set; }

    public bool AwaitingConversationDialogueClose { get; set; }

    public int PendingEventCount { get; set; }

    public int PendingSpeechDisplayCount { get; set; }

    public int PendingImmediateFeedbackCount { get; set; }

    public int PendingRealtimeActionCount { get; set; }

    public int PendingDeferredActionCount { get; set; }

    public int DroppedPendingEventCount { get; set; }

    public string LastDroppedEventType { get; set; } = string.Empty;

    public string ActiveActionSummary { get; set; } = string.Empty;

    public bool HasActiveChatBubble { get; set; }
}

/// <summary>
/// 单个 NPC 在内存中的运行时状态。
/// </summary>
public sealed class NpcAgentRuntimeState
{
    public string NpcName { get; init; } = string.Empty;

    public bool IsWithinActiveWindow { get; set; }

    public DateTimeOffset LastPeriodicTriggeredAt { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset LastPeriodicPauseSkipLoggedAt { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset LastRequestFinishedAt { get; set; } = DateTimeOffset.MinValue;

    public string LastTrigger { get; set; } = string.Empty;

    public string BaselineScheduleKey { get; set; } = string.Empty;

    public EditableScheduleRule? BaselineRule { get; set; }

    public RuntimeSchedulePatch? ActivePatch { get; set; }

    public NpcAgentWorkQueues Queues { get; } = new();

    public int DroppedPendingEventCount { get; set; }

    public string LastDroppedEventType { get; set; } = string.Empty;

    [JsonIgnore]
    public NpcActiveRequestRuntime? ActiveRequest { get; set; }

    [JsonIgnore]
    public NpcQueuedRuntimeReset? PendingRuntimeReset { get; set; }

    [JsonIgnore]
    public bool AcceptingAsyncFeedback { get; set; } = true;

    public string IdleStatusOverride { get; set; } = string.Empty;

    public string InflightStatus => this.ActiveRequest?.BuildStatusText() ??
        (string.IsNullOrWhiteSpace(this.IdleStatusOverride) ? "idle" : this.IdleStatusOverride);

    public string LastRequestDuration { get; set; } = string.Empty;

    public string LastPatchSummary { get; set; } = string.Empty;

    public string LastRejectionReason { get; set; } = string.Empty;

    public List<string> RecentToolCalls { get; } = new();

    public List<string> RecentDebugLines { get; } = new();

    public bool WaitingForPlayerResponse { get; set; }

    public bool PausePeriodicUntilConversationSettles { get; set; }

    public bool AwaitingConversationDialogueClose { get; set; }

    public DateTimeOffset NextActionNotBeforeUtc { get; set; } = DateTimeOffset.MinValue;

    public string ActiveActionSummary { get; set; } = string.Empty;

    public NpcChatBubbleDisplayState? ActiveChatBubble { get; set; }

    public void PushDebugLine(string message)
    {
        this.RecentDebugLines.Add(message);
        while (this.RecentDebugLines.Count > 12)
        {
            this.RecentDebugLines.RemoveAt(0);
        }
    }

    public void PushToolCall(string message)
    {
        this.RecentToolCalls.Add(message);
        while (this.RecentToolCalls.Count > 8)
        {
            this.RecentToolCalls.RemoveAt(0);
        }
    }
}

/// <summary>
/// 一次 LLM 请求在本地执行完后的结果。
/// </summary>
public sealed class AgentRequestResult
{
    public string NpcName { get; set; } = string.Empty;

    public string Trigger { get; set; } = string.Empty;

    public string ResponseSummary { get; set; } = string.Empty;

    public RuntimeSchedulePatch? Patch { get; set; }

    public List<NpcActionRequest> DeferredActionRequests { get; set; } = new();

    public bool RequestedSpeech { get; set; }

    public bool RequestedNpcSpeech { get; set; }

    public int ImmediateFeedbackCount { get; set; }

    public string RejectionReason { get; set; } = string.Empty;

    public List<string> ToolCalls { get; set; } = new();

    public List<string> MemoryHits { get; set; } = new();

    public List<string> IgnoredBroadcastCorrelationIds { get; set; } = new();

    public string OtherNpcName { get; set; } = string.Empty;

    public string SyncPairKey { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }
}

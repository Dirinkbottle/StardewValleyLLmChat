using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace StardewMod.Models;

/// <summary>
/// 单次 LLM 请求的运行阶段。
/// </summary>
public enum NpcRequestPhase
{
    Running = 0,
    Cancelling = 1
}

/// <summary>
/// LLM 请求被取消的原因。
/// </summary>
public enum NpcRequestCancellationReason
{
    None = 0,
    ReplacedByHigherPriorityEvent = 1,
    RuntimeReset = 2,
    AgentDisabled = 3,
    LeftActiveWindow = 4,
    ReturnedToTitle = 5
}

/// <summary>
/// 单个 NPC 当前活动中的请求。
/// </summary>
public sealed class NpcActiveRequestRuntime
{
    public string RequestId { get; set; } = string.Empty;

    public NpcAgentEvent TriggerEvent { get; set; } = new();

    public string SyncPairKey { get; set; } = string.Empty;

    public string OtherNpcName { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public NpcRequestPhase Phase { get; set; } = NpcRequestPhase.Running;

    public NpcRequestCancellationReason CancellationReason { get; set; } = NpcRequestCancellationReason.None;

    public string CancellationDetail { get; set; } = string.Empty;

    [JsonIgnore]
    public CancellationTokenSource? Cancellation { get; set; }

    [JsonIgnore]
    public Task<AgentRequestResult>? Task { get; set; }

    public string BuildStatusText()
    {
        if (this.Phase == NpcRequestPhase.Running)
        {
            return $"running:{this.TriggerEvent.EventType}";
        }

        return $"cancelling:{this.BuildCancellationLabel()}";
    }

    private string BuildCancellationLabel()
    {
        string detail = this.CancellationDetail?.Trim() ?? string.Empty;
        return this.CancellationReason switch
        {
            NpcRequestCancellationReason.ReplacedByHigherPriorityEvent when !string.IsNullOrWhiteSpace(detail) => $"replaced_by_{detail}",
            NpcRequestCancellationReason.ReplacedByHigherPriorityEvent => "replaced_by_event",
            NpcRequestCancellationReason.RuntimeReset => "runtime_reset",
            NpcRequestCancellationReason.AgentDisabled => "disabled",
            NpcRequestCancellationReason.LeftActiveWindow => "window_exited",
            NpcRequestCancellationReason.ReturnedToTitle => "returned_to_title",
            _ => "unknown"
        };
    }
}

/// <summary>
/// 等待在安全时机执行的运行态清理请求。
/// </summary>
public sealed class NpcQueuedRuntimeReset
{
    public bool RestoreBaseline { get; set; }

    public bool LogChange { get; set; }

    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 统一管理 NPC 运行时的待处理事件、对白和动作队列。
/// </summary>
public sealed class NpcAgentWorkQueues
{
    private readonly ConcurrentQueue<NpcImmediateFeedbackEvent> immediateFeedbackQueue = new();
    private readonly Queue<NpcActionRequest> realtimeActionQueue = new();
    private readonly Queue<NpcActionRequest> deferredActionQueue = new();
    private readonly Queue<NpcAgentEvent> pendingEvents = new();
    private readonly Queue<NpcActionRequest> speechDisplayQueue = new();

    public int PendingEventCount => this.pendingEvents.Count;

    public int PendingSpeechCount => this.speechDisplayQueue.Count;

    public int PendingImmediateFeedbackCount => this.immediateFeedbackQueue.Count;

    public int PendingRealtimeActionCount => this.realtimeActionQueue.Count;

    public int PendingDeferredActionCount => this.deferredActionQueue.Count;

    public bool HasImmediateFeedback => !this.immediateFeedbackQueue.IsEmpty;

    public bool HasQueuedWork =>
        this.pendingEvents.Count > 0 ||
        this.speechDisplayQueue.Count > 0 ||
        !this.immediateFeedbackQueue.IsEmpty ||
        this.realtimeActionQueue.Count > 0 ||
        this.deferredActionQueue.Count > 0;

    public IEnumerable<NpcAgentEvent> EnumeratePendingEvents()
    {
        return this.pendingEvents;
    }

    public void EnqueuePendingEvent(NpcAgentEvent agentEvent, bool prepend = false)
    {
        if (prepend)
        {
            PrependQueueItem(this.pendingEvents, agentEvent);
            return;
        }

        this.pendingEvents.Enqueue(agentEvent);
    }

    public bool TryPeekPendingEvent(out NpcAgentEvent? agentEvent)
    {
        if (this.pendingEvents.Count > 0)
        {
            agentEvent = this.pendingEvents.Peek();
            return true;
        }

        agentEvent = null;
        return false;
    }

    public bool TryDequeuePendingEvent(out NpcAgentEvent? agentEvent)
    {
        if (this.pendingEvents.Count > 0)
        {
            agentEvent = this.pendingEvents.Dequeue();
            return true;
        }

        agentEvent = null;
        return false;
    }

    public bool AnyPendingEvent(Func<NpcAgentEvent, bool> predicate)
    {
        return this.pendingEvents.Any(predicate);
    }

    public int RemovePendingEvents(Func<NpcAgentEvent, bool> predicate)
    {
        return RebuildQueue(this.pendingEvents, predicate);
    }

    public void TrimPendingEventsTo(int maxCount)
    {
        while (this.pendingEvents.Count > maxCount)
        {
            this.pendingEvents.Dequeue();
        }
    }

    public int ClearPendingEvents()
    {
        int count = this.pendingEvents.Count;
        this.pendingEvents.Clear();
        return count;
    }

    public void EnqueueSpeech(NpcActionRequest actionRequest, bool prepend = false)
    {
        if (prepend)
        {
            PrependQueueItem(this.speechDisplayQueue, actionRequest);
            return;
        }

        this.speechDisplayQueue.Enqueue(actionRequest);
    }

    public bool TryPeekSpeech(out NpcActionRequest? actionRequest)
    {
        if (this.speechDisplayQueue.Count > 0)
        {
            actionRequest = this.speechDisplayQueue.Peek();
            return true;
        }

        actionRequest = null;
        return false;
    }

    public bool TryDequeueSpeech(out NpcActionRequest? actionRequest)
    {
        if (this.speechDisplayQueue.Count > 0)
        {
            actionRequest = this.speechDisplayQueue.Dequeue();
            return true;
        }

        actionRequest = null;
        return false;
    }

    public int ClearSpeech()
    {
        int count = this.speechDisplayQueue.Count;
        this.speechDisplayQueue.Clear();
        return count;
    }

    public void EnqueueRealtimeAction(NpcActionRequest actionRequest, bool prepend = false)
    {
        if (prepend)
        {
            PrependQueueItem(this.realtimeActionQueue, actionRequest);
            return;
        }

        this.realtimeActionQueue.Enqueue(actionRequest);
    }

    public bool TryPeekRealtimeAction(out NpcActionRequest? actionRequest)
    {
        if (this.realtimeActionQueue.Count > 0)
        {
            actionRequest = this.realtimeActionQueue.Peek();
            return true;
        }

        actionRequest = null;
        return false;
    }

    public bool TryDequeueRealtimeAction(out NpcActionRequest? actionRequest)
    {
        if (this.realtimeActionQueue.Count > 0)
        {
            actionRequest = this.realtimeActionQueue.Dequeue();
            return true;
        }

        actionRequest = null;
        return false;
    }

    public int ClearRealtimeActions()
    {
        int count = this.realtimeActionQueue.Count;
        this.realtimeActionQueue.Clear();
        return count;
    }

    public void EnqueueDeferredAction(NpcActionRequest actionRequest, bool prepend = false)
    {
        if (prepend)
        {
            PrependQueueItem(this.deferredActionQueue, actionRequest);
            return;
        }

        this.deferredActionQueue.Enqueue(actionRequest);
    }

    public bool TryPeekDeferredAction(out NpcActionRequest? actionRequest)
    {
        if (this.deferredActionQueue.Count > 0)
        {
            actionRequest = this.deferredActionQueue.Peek();
            return true;
        }

        actionRequest = null;
        return false;
    }

    public bool TryDequeueDeferredAction(out NpcActionRequest? actionRequest)
    {
        if (this.deferredActionQueue.Count > 0)
        {
            actionRequest = this.deferredActionQueue.Dequeue();
            return true;
        }

        actionRequest = null;
        return false;
    }

    public int ClearDeferredActions()
    {
        int count = this.deferredActionQueue.Count;
        this.deferredActionQueue.Clear();
        return count;
    }

    public void EnqueueImmediateFeedback(NpcImmediateFeedbackEvent feedbackEvent)
    {
        this.immediateFeedbackQueue.Enqueue(feedbackEvent);
    }

    public bool TryDequeueImmediateFeedback(out NpcImmediateFeedbackEvent? feedbackEvent)
    {
        return this.immediateFeedbackQueue.TryDequeue(out feedbackEvent);
    }

    public List<NpcImmediateFeedbackEvent> ExtractImmediateFeedbackForRequest(string requestId)
    {
        List<NpcImmediateFeedbackEvent> matchingEvents = new();
        List<NpcImmediateFeedbackEvent> remainingEvents = new();
        while (this.immediateFeedbackQueue.TryDequeue(out NpcImmediateFeedbackEvent? feedbackEvent))
        {
            if (string.Equals(requestId, feedbackEvent.RequestId, StringComparison.OrdinalIgnoreCase))
            {
                matchingEvents.Add(feedbackEvent);
            }
            else
            {
                remainingEvents.Add(feedbackEvent);
            }
        }

        foreach (NpcImmediateFeedbackEvent feedbackEvent in remainingEvents)
        {
            this.immediateFeedbackQueue.Enqueue(feedbackEvent);
        }

        return matchingEvents;
    }

    public int ClearImmediateFeedback()
    {
        int count = 0;
        while (this.immediateFeedbackQueue.TryDequeue(out _))
        {
            count++;
        }

        return count;
    }

    public void ClearAll()
    {
        this.ClearPendingEvents();
        this.ClearSpeech();
        this.ClearImmediateFeedback();
        this.ClearRealtimeActions();
        this.ClearDeferredActions();
    }

    private static int RebuildQueue<T>(Queue<T> queue, Func<T, bool> removePredicate)
    {
        if (queue.Count == 0)
        {
            return 0;
        }

        List<T> keptItems = new(queue.Count);
        int removedCount = 0;
        while (queue.Count > 0)
        {
            T item = queue.Dequeue();
            if (removePredicate(item))
            {
                removedCount++;
                continue;
            }

            keptItems.Add(item);
        }

        foreach (T item in keptItems)
        {
            queue.Enqueue(item);
        }

        return removedCount;
    }

    private static void PrependQueueItem<T>(Queue<T> queue, T item)
    {
        List<T> reordered = new(queue.Count + 1) { item };
        reordered.AddRange(queue);
        queue.Clear();
        foreach (T entry in reordered)
        {
            queue.Enqueue(entry);
        }
    }
}

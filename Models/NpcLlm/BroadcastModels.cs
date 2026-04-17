namespace StardewMod.Models;

/// <summary>
/// 广播来源类型。
/// </summary>
public enum NpcBroadcastSourceKind
{
    Tool = 0,
    Native = 1,
    System = 2
}

/// <summary>
/// 广播发送者的主体类型。
/// </summary>
public enum NpcBroadcastSenderActorType
{
    Npc = 0,
    Player = 1,
    System = 2
}

/// <summary>
/// 一个 NPC 当前邻域内可见的其它 NPC。
/// </summary>
public sealed class NpcPerceptionNeighbor
{
    public string NpcName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public double DistanceTiles { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public int FacingDirection { get; set; }

    public bool CanReceiveSyncSpeechNow { get; set; }

    public bool IsMentionedCandidate { get; set; }
}

/// <summary>
/// 某个 NPC 当前的感知邻域快照。
/// </summary>
public sealed class NpcPerceptionNeighborhood
{
    public string OwnerNpcName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public int RadiusTiles { get; set; }

    public List<NpcPerceptionNeighbor> NearbyNpcs { get; set; } = new();
}

/// <summary>
/// 广播附带的结构化载荷。
/// </summary>
public sealed class NpcBroadcastPayload
{
    public string ActionType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string TargetNpcName { get; set; } = string.Empty;

    public string TargetNpcDisplayName { get; set; } = string.Empty;

    public string GiftItemName { get; set; } = string.Empty;

    public string NativeEventName { get; set; } = string.Empty;

    public string SummaryHint { get; set; } = string.Empty;

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 广播达到上限后的停止上下文。
/// </summary>
public sealed class NpcBroadcastStopContext
{
    public string Reason { get; set; } = string.Empty;

    public string AttemptedSourceName { get; set; } = string.Empty;

    public string AttemptedSenderName { get; set; } = string.Empty;

    public int AttemptedHop { get; set; }
}

/// <summary>
/// 广播分发计划。
/// </summary>
public sealed class NpcBroadcastDispatchItem
{
    public string BroadcastId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public int Hop { get; set; }

    public int MaxHops { get; set; }

    public NpcBroadcastSourceKind SourceKind { get; set; } = NpcBroadcastSourceKind.Tool;

    public string SourceName { get; set; } = string.Empty;

    public NpcBroadcastSenderActorType SenderActorType { get; set; } = NpcBroadcastSenderActorType.Npc;

    public string SenderName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public string TargetNpcName { get; set; } = string.Empty;

    public List<string> RecipientNpcNames { get; set; } = new();

    public List<string> MentionedNpcNames { get; set; } = new();

    public string SummaryText { get; set; } = string.Empty;

    public NpcBroadcastPayload Payload { get; set; } = new();
}

/// <summary>
/// 投递给单个 NPC 的广播上下文。
/// </summary>
public sealed class NpcBroadcastContext
{
    public string BroadcastId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public int Hop { get; set; }

    public int MaxHops { get; set; }

    public NpcBroadcastSourceKind SourceKind { get; set; } = NpcBroadcastSourceKind.Tool;

    public string SourceName { get; set; } = string.Empty;

    public NpcBroadcastSenderActorType SenderActorType { get; set; } = NpcBroadcastSenderActorType.Npc;

    public string SenderName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public string RecipientNpcName { get; set; } = string.Empty;

    public string TargetNpcName { get; set; } = string.Empty;

    public bool IsDirectTarget { get; set; }

    public bool IsNamedInSummaryOrMentions { get; set; }

    public List<string> RecipientNpcNames { get; set; } = new();

    public List<string> MentionedNpcNames { get; set; } = new();

    public string SummaryText { get; set; } = string.Empty;

    public NpcBroadcastPayload Payload { get; set; } = new();

    public NpcBroadcastStopContext? StopContext { get; set; }
}

namespace StardewMod.Models;

/// <summary>
/// NPC 对另一名 NPC 的可见元数据。
/// </summary>
public sealed class NpcVisibleOtherNpcMetadata
{
    public bool Exists { get; set; }

    public bool IsSameMap { get; set; }

    public bool IsWithinPerceptionRadius { get; set; }

    public string VisibilityNote { get; set; } = string.Empty;

    public string RelationshipNote { get; set; } = string.Empty;

    public string NpcName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public int TileX { get; set; }

    public int TileY { get; set; }

    public int FacingDirection { get; set; }

    public bool IsMoving { get; set; }

    public bool IsEmoting { get; set; }

    public int CurrentEmoteId { get; set; } = -1;

    public string CurrentEmoteName { get; set; } = string.Empty;

    public string MoodHint { get; set; } = string.Empty;

    public double DistanceTiles { get; set; }

    public int PerceptionRadiusTiles { get; set; }

    public Dictionary<string, string> BasicProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public NpcPersonalityProfile PersonalityProfile { get; set; } = new();
}

/// <summary>
/// 普通 prompt 中可见的附近 NPC 简表。
/// </summary>
public sealed class NpcNearbyNpcMetadata
{
    public string NpcName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public int TileX { get; set; }

    public int TileY { get; set; }

    public int FacingDirection { get; set; }

    public bool IsMoving { get; set; }

    public bool IsEmoting { get; set; }

    public string CurrentEmoteName { get; set; } = string.Empty;

    public string MoodHint { get; set; } = string.Empty;

    public double DistanceTiles { get; set; }

    public bool CanReceiveSyncSpeechNow { get; set; }

    public string SyncAvailabilityNote { get; set; } = string.Empty;
}

/// <summary>
/// 同步对话本身的占用状态。
/// </summary>
public sealed class NpcSyncConversationRuntimeState
{
    public string MapName { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string InitiatorNpcName { get; set; } = string.Empty;
}

/// <summary>
/// 感知半径会话状态。
/// </summary>
public sealed class NpcSyncPerceptionRuntimeState
{
    public bool IsWithinPerceptionRadius { get; set; }

    public string MapName { get; set; } = string.Empty;

    public bool EncounterTriggeredInCurrentSession { get; set; }
}

/// <summary>
/// 同步/直接 NPC 对话共用的节流冷却状态。
/// </summary>
public sealed class NpcSyncCooldownRuntimeState
{
    public string MapName { get; set; } = string.Empty;

    public DateTimeOffset LastTriggeredAtUtc { get; set; } = DateTimeOffset.MinValue;
}

/// <summary>
/// 同一对 NPC 的运行态同步状态。
/// </summary>
public sealed class NpcSyncPairRuntimeState
{
    public string PairKey { get; set; } = string.Empty;

    public string NpcAName { get; set; } = string.Empty;

    public string NpcBName { get; set; } = string.Empty;

    public NpcSyncConversationRuntimeState Conversation { get; set; } = new();

    public NpcSyncPerceptionRuntimeState Perception { get; set; } = new();

    public NpcSyncCooldownRuntimeState Cooldown { get; set; } = new();
}

/// <summary>
/// say_to_npc 工具在本地校验目标时返回的结构。
/// </summary>
public sealed class NpcSyncTargetValidationResult
{
    public bool Ok { get; set; }

    public string Error { get; set; } = string.Empty;

    public string TargetNpcName { get; set; } = string.Empty;

    public string TargetDisplayName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public int TileX { get; set; }

    public int TileY { get; set; }

    public double DistanceTiles { get; set; }
}

/// <summary>
/// 世界层显示的 NPC 聊天气泡状态。
/// </summary>
public sealed class NpcChatBubbleDisplayState
{
    public string SourceNpcName { get; set; } = string.Empty;

    public string TargetNpcName { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int DurationMilliseconds { get; set; } = 2600;

    public bool IsExpired(DateTimeOffset now)
    {
        return now >= this.ExpiresAtUtc;
    }
}

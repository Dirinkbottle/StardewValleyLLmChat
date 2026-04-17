namespace StardewMod.Models;

/// <summary>
/// 受控表情名目录。统一由本地负责把语义名映射到原版常量。
/// </summary>
public static class NpcEmoteCatalog
{
    public const string Happy = "happy";
    public const string Heart = "heart";
    public const string Angry = "angry";
    public const string Question = "question";
    public const string Pause = "pause";

    public static IReadOnlyList<string> ControlledNames { get; } = new[]
    {
        Happy,
        Heart,
        Angry,
        Question
    };

    public static IReadOnlyList<string> ControlledNamesWithPause { get; } = new[]
    {
        Happy,
        Heart,
        Angry,
        Question,
        Pause
    };

    public static bool TryGetGameEmoteId(string? emoteName, out int emoteId)
    {
        switch ((emoteName ?? string.Empty).Trim().ToLowerInvariant())
        {
            case Happy:
                emoteId = 32;
                return true;
            case Heart:
                emoteId = 20;
                return true;
            case Angry:
                emoteId = 12;
                return true;
            case Question:
                emoteId = 8;
                return true;
            default:
                emoteId = -1;
                return false;
        }
    }

    public static string Normalize(string? emoteName)
    {
        string value = (emoteName ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            Happy => Happy,
            Heart => Heart,
            Angry => Angry,
            Question => Question,
            Pause => Pause,
            _ => string.Empty
        };
    }

    public static string DescribeCurrent(int emoteId)
    {
        return emoteId switch
        {
            32 => Happy,
            20 => Heart,
            12 => Angry,
            8 => Question,
            < 0 => string.Empty,
            _ => $"custom_{emoteId}"
        };
    }
}

/// <summary>
/// 动作请求类型。
/// </summary>
public enum NpcActionRequestType
{
    MoveToTile = 0,
    SpeakToPlayer = 1,
    DoEmote = 2,
    FacePlayer = 3,
    PlayEndBehavior = 4,
    PauseAndWait = 5,
    PlayRouteAnimation = 6,
    SpeakToNpc = 7
}

/// <summary>
/// 动作请求的投递方式。
/// </summary>
public enum NpcActionDispatchMode
{
    ImmediateFeedback = 0,
    DeferredCommit = 1
}

/// <summary>
/// 按动作类型决定默认的投递方式。
/// </summary>
public static class NpcActionDispatchPolicy
{
    public static NpcActionDispatchMode GetDefaultMode(NpcActionRequestType type)
    {
        return type switch
        {
            NpcActionRequestType.SpeakToPlayer => NpcActionDispatchMode.ImmediateFeedback,
            NpcActionRequestType.DoEmote => NpcActionDispatchMode.ImmediateFeedback,
            NpcActionRequestType.FacePlayer => NpcActionDispatchMode.ImmediateFeedback,
            NpcActionRequestType.PauseAndWait => NpcActionDispatchMode.ImmediateFeedback,
            NpcActionRequestType.PlayRouteAnimation => NpcActionDispatchMode.ImmediateFeedback,
            NpcActionRequestType.SpeakToNpc => NpcActionDispatchMode.ImmediateFeedback,
            _ => NpcActionDispatchMode.DeferredCommit
        };
    }
}

/// <summary>
/// 由工具层产出的动作请求。本地会再决定放入即时反馈链还是请求完成后的延迟执行链。
/// </summary>
public sealed class NpcActionRequest
{
    public NpcActionRequestType Type { get; set; }

    public NpcActionDispatchMode DispatchMode { get; set; } = NpcActionDispatchMode.DeferredCommit;

    public string TargetLocationName { get; set; } = string.Empty;

    public TilePointData TargetTile { get; set; } = new();

    public int FacingDirection { get; set; } = 2;

    public string EmoteName { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string EndBehavior { get; set; } = string.Empty;

    public string AnimationName { get; set; } = string.Empty;

    public string TargetNpcName { get; set; } = string.Empty;

    public string SyncPairKey { get; set; } = string.Empty;

    public string SourceToolName { get; set; } = string.Empty;

    public bool BroadcastToNearbyNpcs { get; set; }

    public string BroadcastSummaryHint { get; set; } = string.Empty;

    public string BroadcastCorrelationId { get; set; } = string.Empty;

    public int BroadcastHop { get; set; }

    public int BroadcastMaxHops { get; set; }

    public List<string> MentionedNpcNames { get; set; } = new();

    public int DurationMilliseconds { get; set; } = 3000;

    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 在 tool loop 期间产生、等待主线程落地的即时反馈事件。
/// </summary>
public sealed class NpcImmediateFeedbackEvent
{
    public string EventId { get; set; } = string.Empty;

    public string RequestId { get; set; } = string.Empty;

    public string SourceToolName { get; set; } = string.Empty;

    public NpcActionRequest Action { get; set; } = new();
}

/// <summary>
/// 周期与同步事件共用的输入事件。
/// </summary>
public sealed class NpcAgentEvent
{
    public string EventType { get; set; } = string.Empty;

    public string NpcName { get; set; } = string.Empty;

    public string GameDate { get; set; } = string.Empty;

    public int TimeOfDay { get; set; }

    public string LocationName { get; set; } = string.Empty;

    public string PlayerAction { get; set; } = string.Empty;

    public string DialogueExcerpt { get; set; } = string.Empty;

    public string GiftItem { get; set; } = string.Empty;

    public int FriendshipDelta { get; set; }

    public string CurrentScheduleSummary { get; set; } = string.Empty;

    public string OtherNpcName { get; set; } = string.Empty;

    public string OtherNpcDisplayName { get; set; } = string.Empty;

    public string OtherNpcMessage { get; set; } = string.Empty;

    public string SyncPairKey { get; set; } = string.Empty;

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public NpcBroadcastContext? BroadcastContext { get; set; }
}

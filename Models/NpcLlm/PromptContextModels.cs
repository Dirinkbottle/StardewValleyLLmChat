namespace StardewMod.Models;

/// <summary>
/// 每轮 prompt 开始前重新采样得到的快照。
/// </summary>
public sealed class NpcAgentPromptSnapshot
{
    public string NpcName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int PromptRound { get; set; } = 1;

    public string GameDate { get; set; } = string.Empty;

    public int TimeOfDay { get; set; }

    public string TimeText { get; set; } = string.Empty;

    public NpcPromptMetadata Metadata { get; set; } = new();

    public string ScheduleSummary { get; set; } = string.Empty;

    public string ScheduleDetailJson { get; set; } = string.Empty;
}

/// <summary>
/// 提示词内可直接序列化的分组元数据。
/// </summary>
public sealed class NpcPromptMetadata
{
    public NpcTemporalMetadata Temporal { get; set; } = new();

    public NpcWeatherMetadata Weather { get; set; } = new();

    public NpcFestivalMetadata Festival { get; set; } = new();

    public NpcObservedNpcMetadata Npc { get; set; } = new();

    public NpcVisibleFarmerMetadata Farmer { get; set; } = new();

    public NpcVisibleOtherNpcMetadata OtherNpc { get; set; } = new();

    public List<NpcPerceptionNeighbor> NearbyNpcs { get; set; } = new();

    public NpcRelationshipMetadata Relationship { get; set; } = new();
}

public sealed class NpcTemporalMetadata
{
    public string DateText { get; set; } = string.Empty;

    public string Season { get; set; } = string.Empty;

    public int DayOfMonth { get; set; }

    public int Year { get; set; }

    public string DayOfWeek { get; set; } = string.Empty;

    public int TimeOfDay { get; set; }

    public string TimeText { get; set; } = string.Empty;

    public bool IsNight { get; set; }
}

public sealed class NpcWeatherMetadata
{
    public string CurrentKind { get; set; } = string.Empty;

    public string TomorrowKind { get; set; } = string.Empty;

    public bool IsRaining { get; set; }

    public bool IsSnowing { get; set; }

    public bool IsLightning { get; set; }

    public bool IsDebrisWeather { get; set; }

    public bool IsGreenRain { get; set; }

    public int WeatherIcon { get; set; }
}

public sealed class NpcFestivalMetadata
{
    public bool HasFestivalToday { get; set; }

    public bool IsActiveFestivalDay { get; set; }

    public bool IsPassiveFestivalDay { get; set; }

    public bool IsFestivalOpenNow { get; set; }

    public string FestivalType { get; set; } = "none";

    public string FestivalId { get; set; } = string.Empty;

    public string FestivalName { get; set; } = string.Empty;

    public string FestivalLocationName { get; set; } = string.Empty;

    public int StartTime { get; set; }

    public int EndTime { get; set; }

    public int PassiveFestivalDayIndex { get; set; } = -1;
}

public sealed class NpcObservedNpcMetadata
{
    public string MapName { get; set; } = string.Empty;

    public int TileX { get; set; }

    public int TileY { get; set; }

    public int FacingDirection { get; set; }

    public bool IsMoving { get; set; }

    public bool IsEmoting { get; set; }

    public int CurrentEmoteId { get; set; } = -1;

    public string CurrentEmoteName { get; set; } = string.Empty;

    public string MoodHint { get; set; } = string.Empty;
}

public sealed class NpcVisibleFarmerMetadata
{
    public bool IsVisibleToNpc { get; set; }

    public bool IsSameMap { get; set; }

    public bool IsWithinPerceptionRadius { get; set; }

    public double DistanceTiles { get; set; }

    public int PerceptionRadiusTiles { get; set; }

    public string VisibilityNote { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public int TileX { get; set; }

    public int TileY { get; set; }

    public int FacingDirection { get; set; }

    public string HeldObjectQualifiedItemId { get; set; } = string.Empty;

    public string HeldObjectDisplayName { get; set; } = string.Empty;

    public string CurrentToolQualifiedItemId { get; set; } = string.Empty;

    public string CurrentToolDisplayName { get; set; } = string.Empty;

    public float Stamina { get; set; }

    public int MaxStamina { get; set; }

    public List<NpcVisibleStatusEffectMetadata> StatusEffects { get; set; } = new();
}

public sealed class NpcVisibleStatusEffectMetadata
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int RemainingMilliseconds { get; set; }

    public bool Visible { get; set; }

    public bool HasStatEffects { get; set; }
}

public sealed class NpcRelationshipMetadata
{
    public int FriendshipHearts { get; set; }
}

public sealed class NpcPromptRefreshState
{
    public NpcAgentPromptSnapshot Snapshot { get; set; } = new();

    public NpcAgentRuntimeSummary RuntimeSummary { get; set; } = new();

    public Dictionary<string, string> BasicProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public NpcPersonalityProfile PersonalityProfile { get; set; } = new();
}

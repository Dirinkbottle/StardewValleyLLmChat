using StardewValley;

namespace StardewMod.Models;

/// <summary>
/// NPC LLM 设置的按存档保存数据。
/// </summary>
public sealed class NpcLlmSaveData
{
    public Dictionary<string, NpcAgentSettings> Npcs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 单个 NPC 的 Agent 配置。
/// </summary>
public sealed class NpcAgentSettings
{
    private static readonly string[] DayKeys = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    public bool Enabled { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public int PeriodicIntervalSeconds { get; set; } = 45;

    public bool AllowBehaviorControl { get; set; } = true;

    public bool AllowSpeech { get; set; } = true;

    public bool AllowScheduleControl { get; set; } = true;

    public string BaselineScheduleKeyHint { get; set; } = string.Empty;

    public Dictionary<string, List<AgentTimeWindow>> DayWindows { get; set; } = CreateDefaultDayWindows();

    public static Dictionary<string, List<AgentTimeWindow>> CreateDefaultDayWindows()
    {
        return new Dictionary<string, List<AgentTimeWindow>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mon"] = new List<AgentTimeWindow>(),
            ["Tue"] = new List<AgentTimeWindow>(),
            ["Wed"] = new List<AgentTimeWindow>(),
            ["Thu"] = new List<AgentTimeWindow>(),
            ["Fri"] = new List<AgentTimeWindow>(),
            ["Sat"] = new List<AgentTimeWindow>(),
            ["Sun"] = new List<AgentTimeWindow>()
        };
    }

    public static Dictionary<string, List<AgentTimeWindow>> CreateAlwaysOnDayWindows()
    {
        return DayKeys.ToDictionary(
            day => day,
            _ => new List<AgentTimeWindow>
            {
                new()
                {
                    StartTime = 600,
                    EndTime = 2600
                }
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAlwaysOnAllWeek()
    {
        return DayKeys.All(day =>
            this.DayWindows.TryGetValue(day, out List<AgentTimeWindow>? windows) &&
            windows.Count == 1 &&
            windows[0].StartTime == 600 &&
            windows[0].EndTime == 2600);
    }

    public NpcAgentSettings Clone()
    {
        return new NpcAgentSettings
        {
            Enabled = this.Enabled,
            ProviderName = this.ProviderName,
            PeriodicIntervalSeconds = this.PeriodicIntervalSeconds,
            AllowBehaviorControl = this.AllowBehaviorControl,
            AllowSpeech = this.AllowSpeech,
            AllowScheduleControl = this.AllowScheduleControl,
            BaselineScheduleKeyHint = this.BaselineScheduleKeyHint,
            DayWindows = this.DayWindows.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(window => window.Clone()).ToList(),
                StringComparer.OrdinalIgnoreCase)
        };
    }
}

/// <summary>
/// 周期激活的时间窗。
/// </summary>
public sealed class AgentTimeWindow
{
    public int StartTime { get; set; } = 600;

    public int EndTime { get; set; } = 2600;

    public bool Contains(int timeOfDay)
    {
        return timeOfDay >= this.StartTime && timeOfDay < this.EndTime;
    }

    public AgentTimeWindow Clone()
    {
        return new AgentTimeWindow
        {
            StartTime = this.StartTime,
            EndTime = this.EndTime
        };
    }

    public override string ToString()
    {
        return $"{Game1.getTimeOfDayString(this.StartTime)} - {Game1.getTimeOfDayString(this.EndTime)}";
    }
}

/// <summary>
/// 当前会话内的运行时 patch。
/// </summary>
public sealed class RuntimeSchedulePatch
{
    public string RevisionId { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int ApplyFromTime { get; set; } = 600;

    public EditableScheduleRule Rule { get; set; } = new();

    public bool ExpiresAtWindowEnd { get; set; } = true;

    public string DiffSummary { get; set; } = string.Empty;
}

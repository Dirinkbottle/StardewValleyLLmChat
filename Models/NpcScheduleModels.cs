using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewMod.Models;

/// <summary>
/// 按存档保存的 NPC 路线编辑数据。
/// </summary>
public sealed class NpcRouteEditorSaveData
{
    public Dictionary<string, NpcScheduleNpcData> Npcs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 单个 NPC 的全部规则覆盖。
/// </summary>
public sealed class NpcScheduleNpcData
{
    public Dictionary<string, NpcScheduleOverrideData> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 某个规则键对应的完整覆盖日程。
/// </summary>
public sealed class NpcScheduleOverrideData
{
    public string RuleKey { get; set; } = string.Empty;

    public NpcScheduleStartPointData StartPoint { get; set; } = new();

    public List<NpcScheduleStopData> Stops { get; set; } = new();
}

/// <summary>
/// 某条规则的日初出生点配置。
/// </summary>
public sealed class NpcScheduleStartPointData
{
    public bool UseCustomStartPoint { get; set; }

    public string LocationName { get; set; } = string.Empty;

    public int FacingDirection { get; set; } = 2;

    public TilePointData Tile { get; set; } = new();
}

/// <summary>
/// 时间字段的语义。Departure 表示几点出发，Arrival 表示几点到达。
/// </summary>
public enum ScheduleTimeMode
{
    Departure = 0,
    Arrival = 1
}

/// <summary>
/// 可序列化的单段日程停靠点。
/// </summary>
public sealed class NpcScheduleStopData
{
    public int Time { get; set; } = 700;

    public ScheduleTimeMode TimeMode { get; set; } = ScheduleTimeMode.Departure;

    public string LocationName { get; set; } = string.Empty;

    public int FacingDirection { get; set; } = 2;

    public string EndBehavior { get; set; } = string.Empty;

    public string EndMessage { get; set; } = string.Empty;

    public TilePointData TargetTile { get; set; } = new();

    public List<TilePointData> RouteTiles { get; set; } = new();
}

/// <summary>
/// 可序列化的 tile 坐标。
/// </summary>
public sealed class TilePointData
{
    public TilePointData()
    {
    }

    public TilePointData(int x, int y)
    {
        this.X = x;
        this.Y = y;
    }

    public TilePointData(Point point)
    {
        this.X = point.X;
        this.Y = point.Y;
    }

    public int X { get; set; }

    public int Y { get; set; }

    public Point ToPoint()
    {
        return new Point(this.X, this.Y);
    }

    public TilePointData Clone()
    {
        return new TilePointData(this.X, this.Y);
    }
}

/// <summary>
/// NPC 选择菜单的展示数据。
/// </summary>
public sealed class NpcMenuEntry
{
    public string InternalName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public Texture2D Portrait { get; init; } = null!;
}

/// <summary>
/// 日程规则列表页使用的摘要信息。
/// </summary>
public sealed class ScheduleRuleSummary
{
    public string RuleKey { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public bool HasOverride { get; init; }

    public bool HasRuntimePatch { get; init; }

    public string RuntimePatchRevisionId { get; init; } = string.Empty;

    public int StopCount { get; init; }

    public string PreviewText { get; init; } = string.Empty;
}

/// <summary>
/// 编辑器使用的可变规则模型。
/// </summary>
public sealed class EditableScheduleRule
{
    public string NpcName { get; init; } = string.Empty;

    public string RuleKey { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string PreviewText { get; init; } = string.Empty;

    public bool IsOverride { get; set; }

    public EditableScheduleStartPoint StartPoint { get; set; } = new();

    public List<EditableScheduleStop> Stops { get; set; } = new();

    /// <summary>
    /// 保存前统一整理时间和路径数据，避免生成非法日程。
    /// </summary>
    public void NormalizeBeforeSave()
    {
        foreach (EditableScheduleStop stop in this.Stops)
        {
            stop.Time = ScheduleTimeHelper.NormalizeStopTime(stop.Time);
        }

        this.Stops = this.Stops
            .OrderBy(stop => stop.Time)
            .ToList();

        int nextMinimumTime = ScheduleTimeHelper.EarliestStopTime;
        foreach (EditableScheduleStop stop in this.Stops)
        {
            stop.Time = Math.Max(stop.Time, nextMinimumTime);
            stop.Time = ScheduleTimeHelper.NormalizeStopTime(stop.Time);

            if (stop.RouteTiles.Count == 0)
            {
                stop.RouteTiles.Add(stop.TargetTile.Clone());
            }

            stop.TargetTile = stop.RouteTiles[^1].Clone();
            nextMinimumTime = ScheduleTimeHelper.AddMinutes(stop.Time, 10);
        }
    }

    public EditableScheduleRule Clone()
    {
        return new EditableScheduleRule
        {
            NpcName = this.NpcName,
            RuleKey = this.RuleKey,
            DisplayName = this.DisplayName,
            Category = this.Category,
            PreviewText = this.PreviewText,
            IsOverride = this.IsOverride,
            StartPoint = this.StartPoint.Clone(),
            Stops = this.Stops.Select(stop => stop.Clone()).ToList()
        };
    }
}

/// <summary>
/// schedule 编辑器专用的时间工具。统一把可编辑时间限制在 06:00 到次日 02:00。
/// </summary>
internal static class ScheduleTimeHelper
{
    public const int EarliestStopTime = 600;
    public const int LatestStopTime = 2600;

    public static int NormalizeStopTime(int time)
    {
        if (time <= 0)
        {
            return EarliestStopTime;
        }

        if (time < EarliestStopTime)
        {
            time += 2400;
        }

        return Math.Clamp(time, EarliestStopTime, LatestStopTime);
    }

    public static int AddMinutes(int time, int deltaMinutes)
    {
        int normalized = NormalizeStopTime(time);
        int minutes = Utility.ConvertTimeToMinutes(normalized) + deltaMinutes;
        int minimumMinutes = Utility.ConvertTimeToMinutes(EarliestStopTime);
        int maximumMinutes = Utility.ConvertTimeToMinutes(LatestStopTime);
        return Utility.ConvertMinutesToTime(Math.Clamp(minutes, minimumMinutes, maximumMinutes));
    }
}

/// <summary>
/// 编辑器中使用的日初出生点设置。
/// </summary>
public sealed class EditableScheduleStartPoint
{
    public bool UseCustomStartPoint { get; set; }

    public string LocationName { get; set; } = string.Empty;

    public int FacingDirection { get; set; } = 2;

    public TilePointData Tile { get; set; } = new();

    public EditableScheduleStartPoint Clone()
    {
        return new EditableScheduleStartPoint
        {
            UseCustomStartPoint = this.UseCustomStartPoint,
            LocationName = this.LocationName,
            FacingDirection = this.FacingDirection,
            Tile = this.Tile.Clone()
        };
    }
}

/// <summary>
/// 编辑器使用的单段停靠点。
/// </summary>
public sealed class EditableScheduleStop
{
    public int Time { get; set; } = 700;

    public ScheduleTimeMode TimeMode { get; set; } = ScheduleTimeMode.Departure;

    public string LocationName { get; set; } = string.Empty;

    public int FacingDirection { get; set; } = 2;

    public string EndBehavior { get; set; } = string.Empty;

    public string EndMessage { get; set; } = string.Empty;

    public TilePointData TargetTile { get; set; } = new();

    public List<TilePointData> RouteTiles { get; set; } = new();

    public EditableScheduleStop Clone()
    {
        return new EditableScheduleStop
        {
            Time = this.Time,
            TimeMode = this.TimeMode,
            LocationName = this.LocationName,
            FacingDirection = this.FacingDirection,
            EndBehavior = this.EndBehavior,
            EndMessage = this.EndMessage,
            TargetTile = this.TargetTile.Clone(),
            RouteTiles = this.RouteTiles.Select(tile => tile.Clone()).ToList()
        };
    }
}

/// <summary>
/// 对原版 schedule key 做简单的人类可读解析。
/// </summary>
public static class ScheduleRuleClassifier
{
    private static readonly Dictionary<string, string> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = "周一",
        ["Tue"] = "周二",
        ["Wed"] = "周三",
        ["Thu"] = "周四",
        ["Fri"] = "周五",
        ["Sat"] = "周六",
        ["Sun"] = "周日"
    };

    private static readonly Dictionary<string, string> SeasonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spring"] = "春季",
        ["summer"] = "夏季",
        ["fall"] = "秋季",
        ["winter"] = "冬季"
    };

    public static string GetDisplayName(string key)
    {
        if (string.Equals(key, "rain", StringComparison.OrdinalIgnoreCase))
        {
            return "雨天";
        }

        if (string.Equals(key, "rain2", StringComparison.OrdinalIgnoreCase))
        {
            return "雨天备用";
        }

        if (string.Equals(key, "GreenRain", StringComparison.OrdinalIgnoreCase))
        {
            return "绿色雨";
        }

        if (SeasonNames.TryGetValue(key, out string? seasonName))
        {
            return $"{seasonName}常规";
        }

        if (DayNames.TryGetValue(key, out string? dayName))
        {
            return dayName;
        }

        string[] parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && SeasonNames.TryGetValue(parts[0], out string? seasonPart))
        {
            if (int.TryParse(parts[1], out int seasonDay))
            {
                return $"{seasonPart}第 {seasonDay} 天";
            }

            if (DayNames.TryGetValue(parts[1], out string? weekdayPart))
            {
                return $"{seasonPart}{weekdayPart}";
            }
        }

        if (int.TryParse(key, out int monthDay))
        {
            return $"每月第 {monthDay} 天";
        }

        if (parts.Length >= 2 && int.TryParse(parts[^1], out int festivalDay))
        {
            return $"{HumanizeIdentifier(string.Join("_", parts.Take(parts.Length - 1)))} 第 {festivalDay} 天";
        }

        return HumanizeIdentifier(key);
    }

    public static string GetCategory(string key)
    {
        if (string.Equals(key, "rain", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "rain2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "GreenRain", StringComparison.OrdinalIgnoreCase))
        {
            return "天气规则";
        }

        if (SeasonNames.ContainsKey(key))
        {
            return "季节常规";
        }

        if (DayNames.ContainsKey(key))
        {
            return "每周规则";
        }

        string[] parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && SeasonNames.ContainsKey(parts[0]) && int.TryParse(parts[1], out _))
        {
            return "季节日期";
        }

        if (parts.Length == 2 && SeasonNames.ContainsKey(parts[0]) && DayNames.ContainsKey(parts[1]))
        {
            return "季节星期";
        }

        if (int.TryParse(key, out _))
        {
            return "每月日期";
        }

        if (key.Contains("Festival", StringComparison.OrdinalIgnoreCase))
        {
            return "特殊节日";
        }

        if (key.StartsWith("marriage_", StringComparison.OrdinalIgnoreCase))
        {
            return "婚后规则";
        }

        return "特殊规则";
    }

    public static string BuildPreview(string? rawScript)
    {
        if (string.IsNullOrWhiteSpace(rawScript))
        {
            return "无原始脚本";
        }

        string normalized = rawScript.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return normalized.Length <= 72
            ? normalized
            : normalized[..72] + "...";
    }

    private static string HumanizeIdentifier(string value)
    {
        return value
            .Replace('_', ' ')
            .Replace("Festival", " Festival")
            .Trim();
    }
}

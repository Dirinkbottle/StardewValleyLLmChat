using Microsoft.Xna.Framework;
using StardewMod.Models;
using StardewMod.Services.ScheduleRouting;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace StardewMod.Services;

internal sealed partial class NpcScheduleEditorService
{
    /// <summary>
    /// 将任意可编辑规则编译回原版可执行的当天 schedule。
    /// </summary>
    public Dictionary<int, SchedulePathDescription> BuildScheduleDictionaryFromRule(NPC npc, EditableScheduleRule rule)
    {
        EditableScheduleRule cloned = rule.Clone();
        cloned.NormalizeBeforeSave();

        NpcScheduleOverrideData overrideData = new()
        {
            RuleKey = cloned.RuleKey,
            StartPoint = this.SerializeStartPoint(cloned.StartPoint),
            Stops = cloned.Stops.Select(this.SerializeStop).ToList()
        };

        return this.BuildScheduleDictionary(npc, overrideData);
    }

    /// <summary>
    /// 把可编辑规则直接应用到当前 NPC，不改存档，只改运行时。
    /// </summary>
    public bool TryApplyLiveRule(NPC npc, EditableScheduleRule rule, bool preserveCurrentMovement = true)
    {
        try
        {
            Dictionary<int, SchedulePathDescription> schedule = this.BuildScheduleDictionaryFromRule(npc, rule);
            npc.TryLoadSchedule(rule.RuleKey, schedule);

            if (!preserveCurrentMovement)
            {
                npc.queuedSchedulePaths.Clear();
                npc.lastAttemptedSchedule = -1;
                npc.checkSchedule(Game1.timeOfDay);
            }

            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"运行时应用 {npc.Name} 的规则 {rule.RuleKey} 失败：{ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private bool TryApplyOverrideForKey(NPC npc, string? ruleKey)
    {
        if (string.IsNullOrWhiteSpace(ruleKey) || !this.TryGetOverride(npc.Name, ruleKey, out NpcScheduleOverrideData overrideData))
        {
            return false;
        }

        Dictionary<int, SchedulePathDescription> schedule = this.BuildScheduleDictionary(npc, overrideData);
        npc.queuedSchedulePaths.Clear();
        npc.lastAttemptedSchedule = -1;
        this.ApplyStartPointIfNeeded(npc, overrideData.StartPoint);
        npc.TryLoadSchedule(ruleKey, schedule);
        return true;
    }

    private Dictionary<int, SchedulePathDescription> BuildScheduleDictionary(NPC npc, NpcScheduleOverrideData overrideData)
    {
        Dictionary<int, SchedulePathDescription> schedule = new();
        RouteAnchor anchor = this.GetInitialAnchor(npc, overrideData.StartPoint);
        int previousEffectiveTime = 610;

        foreach (NpcScheduleStopData stop in overrideData.Stops.OrderBy(stop => stop.Time))
        {
            string destinationLocation = string.IsNullOrWhiteSpace(stop.LocationName)
                ? anchor.LocationName
                : stop.LocationName;
            bool isCrossLocation = !string.Equals(anchor.LocationName, destinationLocation, StringComparison.OrdinalIgnoreCase);
            List<Point> routePoints = this.BuildRoutePoints(npc, overrideData.RuleKey, anchor, stop);
            Point targetTile = stop.TargetTile.ToPoint();
            if (isCrossLocation && routePoints.Count == 0)
            {
                this.monitor.Log(
                    $"规则 {overrideData.RuleKey} 的跨图站点已跳过：{anchor.LocationName} ({anchor.Tile.X}, {anchor.Tile.Y}) -> {destinationLocation} ({targetTile.X}, {targetTile.Y})。自动桥接失败后不会伪造同地图坐标路径，也不会推进后续锚点。",
                    LogLevel.Warn);
                continue;
            }

            int effectiveTime = this.GetEffectiveScheduleTime(routePoints, stop.Time, stop.TimeMode, previousEffectiveTime);
            while (schedule.ContainsKey(effectiveTime))
            {
                effectiveTime = Utility.ModifyTime(effectiveTime, 10);
            }

            Stack<Point> routeStack = new(routePoints.AsEnumerable().Reverse());
            SchedulePathDescription description = new(
                routeStack,
                stop.FacingDirection,
                string.IsNullOrWhiteSpace(stop.EndBehavior) ? null : stop.EndBehavior,
                string.IsNullOrWhiteSpace(stop.EndMessage) ? null : stop.EndMessage,
                destinationLocation,
                targetTile)
            {
                time = effectiveTime
            };

            schedule[effectiveTime] = description;
            anchor = new RouteAnchor(destinationLocation, targetTile);
            previousEffectiveTime = effectiveTime;
        }

        return schedule;
    }

    private NpcScheduleStartPointData SerializeStartPoint(EditableScheduleStartPoint startPoint)
    {
        return new NpcScheduleStartPointData
        {
            UseCustomStartPoint = startPoint.UseCustomStartPoint,
            LocationName = startPoint.LocationName,
            FacingDirection = startPoint.FacingDirection,
            Tile = startPoint.Tile.Clone()
        };
    }

    private NpcScheduleStopData SerializeStop(EditableScheduleStop stop)
    {
        return new NpcScheduleStopData
        {
            Time = stop.Time,
            TimeMode = stop.TimeMode,
            LocationName = stop.LocationName,
            FacingDirection = stop.FacingDirection,
            EndBehavior = stop.EndBehavior,
            EndMessage = stop.EndMessage,
            TargetTile = stop.TargetTile.Clone(),
            RouteTiles = stop.RouteTiles.Select(tile => tile.Clone()).ToList()
        };
    }

    private EditableScheduleStartPoint DeserializeStartPoint(NpcScheduleStartPointData? startPoint, string npcName)
    {
        EditableScheduleStartPoint defaults = this.GetDefaultStartPoint(this.RequireNpc(npcName));
        if (startPoint is null)
        {
            return defaults;
        }

        return new EditableScheduleStartPoint
        {
            UseCustomStartPoint = startPoint.UseCustomStartPoint,
            LocationName = string.IsNullOrWhiteSpace(startPoint.LocationName) ? defaults.LocationName : startPoint.LocationName,
            FacingDirection = startPoint.FacingDirection,
            Tile = startPoint.Tile.Clone()
        };
    }

    private EditableScheduleStop DeserializeStop(NpcScheduleStopData stop)
    {
        return new EditableScheduleStop
        {
            Time = ScheduleTimeHelper.NormalizeStopTime(stop.Time),
            TimeMode = stop.TimeMode,
            LocationName = stop.LocationName,
            FacingDirection = stop.FacingDirection,
            EndBehavior = stop.EndBehavior,
            EndMessage = stop.EndMessage,
            TargetTile = stop.TargetTile.Clone(),
            RouteTiles = stop.RouteTiles.Select(tile => tile.Clone()).ToList()
        };
    }

    private EditableScheduleStartPoint GetDefaultStartPoint(NPC npc)
    {
        return new EditableScheduleStartPoint
        {
            UseCustomStartPoint = false,
            LocationName = npc.isMarried() ? "BusStop" : npc.DefaultMap,
            FacingDirection = npc.DefaultFacingDirection,
            Tile = new TilePointData(npc.isMarried() ? new Point(10, 23) : this.GetNpcDefaultTile(npc))
        };
    }

    private Point GetNpcDefaultTile(NPC npc)
    {
        return new Point((int)npc.DefaultPosition.X / 64, (int)npc.DefaultPosition.Y / 64);
    }

    private void ApplyStartPointIfNeeded(NPC npc, NpcScheduleStartPointData? startPoint)
    {
        if (startPoint is null || !startPoint.UseCustomStartPoint || string.IsNullOrWhiteSpace(startPoint.LocationName))
        {
            return;
        }

        try
        {
            Point tile = startPoint.Tile.ToPoint();
            Game1.warpCharacter(npc, startPoint.LocationName, tile);
            npc.faceDirection(startPoint.FacingDirection);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"将 {npc.Name} 送到自定义出生点 {startPoint.LocationName} ({startPoint.Tile.X}, {startPoint.Tile.Y}) 时失败：{ex.Message}", LogLevel.Warn);
        }
    }

    private RouteAnchor GetInitialAnchor(NPC npc, NpcScheduleStartPointData? startPoint)
    {
        if (startPoint?.UseCustomStartPoint == true && !string.IsNullOrWhiteSpace(startPoint.LocationName))
        {
            return new RouteAnchor(startPoint.LocationName, startPoint.Tile.ToPoint());
        }

        EditableScheduleStartPoint defaults = this.GetDefaultStartPoint(npc);
        return new RouteAnchor(defaults.LocationName, defaults.Tile.ToPoint());
    }

    private RouteAnchor GetAnchorForEditableStop(NPC npc, EditableScheduleRule rule, int stopIndex)
    {
        if (stopIndex <= 0)
        {
            EditableScheduleStartPoint startPoint = rule.StartPoint.UseCustomStartPoint
                ? rule.StartPoint
                : this.GetDefaultStartPoint(npc);
            return new RouteAnchor(startPoint.LocationName, startPoint.Tile.ToPoint());
        }

        EditableScheduleStop previousStop = rule.Stops[stopIndex - 1];
        return new RouteAnchor(previousStop.LocationName, previousStop.TargetTile.ToPoint());
    }

    private List<Point> BuildRoutePoints(NPC npc, string ruleKey, RouteAnchor anchor, NpcScheduleStopData stop)
    {
        EditableScheduleStop editableStop = new()
        {
            Time = stop.Time,
            TimeMode = stop.TimeMode,
            LocationName = stop.LocationName,
            FacingDirection = stop.FacingDirection,
            EndBehavior = stop.EndBehavior,
            EndMessage = stop.EndMessage,
            TargetTile = stop.TargetTile.Clone(),
            RouteTiles = stop.RouteTiles.Select(tile => tile.Clone()).ToList()
        };

        return this.BuildCompiledRoutePreview(npc, ruleKey, anchor, editableStop, logWarnings: true).FlattenPoints();
    }

    private CompiledRoutePreview BuildCompiledRoutePreview(NPC npc, string ruleKey, RouteAnchor anchor, EditableScheduleStop stop, bool logWarnings)
    {
        string destinationLocation = string.IsNullOrWhiteSpace(stop.LocationName)
            ? anchor.LocationName
            : stop.LocationName;
        bool isCrossLocation = !string.Equals(anchor.LocationName, destinationLocation, StringComparison.OrdinalIgnoreCase);
        Point destinationTile = stop.TargetTile.ToPoint();
        bool hasManualRoute = stop.RouteTiles.Count > 0;
        List<Point> manualRoute = stop.RouteTiles.Select(tile => tile.ToPoint()).ToList();
        if (manualRoute.Count == 0)
        {
            manualRoute.Add(destinationTile);
        }

        Point stitchTile = manualRoute[0];
        CompiledRoutePreview autoPreview = this.TryBuildAutoRoutePreview(npc, ruleKey, anchor.LocationName, anchor.Tile, destinationLocation, stitchTile, logWarnings);
        if (!hasManualRoute && autoPreview.HasAnyPoints)
        {
            return autoPreview;
        }

        if (!autoPreview.HasAnyPoints)
        {
            autoPreview = this.TryBuildAutoRoutePreview(npc, ruleKey, anchor.LocationName, anchor.Tile, destinationLocation, destinationTile, logWarnings);
            if (autoPreview.HasAnyPoints)
            {
                if (manualRoute.Count <= 1)
                {
                    return autoPreview;
                }

                if (isCrossLocation)
                {
                    if (logWarnings)
                    {
                        this.monitor.Log(
                            $"规则 {ruleKey} 的跨图站点手工路径首段无法自动接续：{anchor.LocationName} ({anchor.Tile.X}, {anchor.Tile.Y}) -> {destinationLocation} ({destinationTile.X}, {destinationTile.Y})。已回退为纯自动跨图路径，并忽略目标地图上的手工 route。",
                            LogLevel.Warn);
                    }

                    return autoPreview;
                }

                CompiledRoutePreview manualOnlyPreview = new();
                manualOnlyPreview.AppendSegment(destinationLocation, manualRoute, isAutoGenerated: false);
                return manualOnlyPreview;
            }
        }

        if (isCrossLocation && !autoPreview.HasAnyPoints)
        {
            if (logWarnings)
            {
                this.monitor.Log(
                    $"规则 {ruleKey} 的跨图站点自动接续失败：{anchor.LocationName} ({anchor.Tile.X}, {anchor.Tile.Y}) -> {destinationLocation} ({destinationTile.X}, {destinationTile.Y})。已拒绝退回到纯坐标路线，避免 NPC 在错误地图按同名坐标移动。",
                    LogLevel.Warn);
            }

            return new CompiledRoutePreview();
        }

        CompiledRoutePreview preview = new();
        foreach (CompiledRoutePreviewSegment segment in autoPreview.Segments)
        {
            preview.AppendSegment(segment.LocationName, segment.Tiles.Select(tile => tile.ToPoint()).ToList(), segment.IsAutoGenerated);
        }

        preview.AppendSegment(destinationLocation, manualRoute, isAutoGenerated: false);
        if (!preview.HasAnyPoints)
        {
            preview.AppendSegment(destinationLocation, new List<Point> { destinationTile }, isAutoGenerated: false);
        }

        return preview;
    }

    private CompiledRoutePreview TryBuildAutoRoutePreview(NPC npc, string ruleKey, string startLocation, Point startTile, string endLocation, Point endTile, bool logWarnings)
    {
        try
        {
            return this.routeBridgeService.TryBuildRoutePreview(npc, startLocation, startTile, endLocation, endTile);
        }
        catch (Exception ex)
        {
            if (logWarnings)
            {
                this.monitor.Log(
                    $"为 {npc.Name} 计算规则 {ruleKey} 的自动接续路径失败：{startLocation} ({startTile.X}, {startTile.Y}) -> {endLocation} ({endTile.X}, {endTile.Y})。{ex.Message}",
                    LogLevel.Warn);
            }

            return new CompiledRoutePreview();
        }
    }

    private int GetEffectiveScheduleTime(IReadOnlyList<Point> routePoints, int declaredTime, ScheduleTimeMode timeMode, int previousEffectiveTime)
    {
        if (timeMode != ScheduleTimeMode.Arrival)
        {
            return declaredTime;
        }

        int distanceTraveled = 0;
        Point? lastPoint = null;
        foreach (Point point in routePoints)
        {
            if (lastPoint.HasValue && Math.Abs(lastPoint.Value.X - point.X) + Math.Abs(lastPoint.Value.Y - point.Y) == 1)
            {
                distanceTraveled += 64;
            }

            lastPoint = point;
        }

        int pixelDistance = distanceTraveled / 2;
        int ticksPerTenMinutes = Math.Max(1, Game1.realMilliSecondsPerGameTenMinutes / 1000 * 60);
        int travelTime = (int)Math.Round((float)pixelDistance / ticksPerTenMinutes) * 10;
        int departureTime = Utility.ConvertMinutesToTime(Utility.ConvertTimeToMinutes(declaredTime) - travelTime);
        return Math.Max(departureTime, previousEffectiveTime);
    }
}

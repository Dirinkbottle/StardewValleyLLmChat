using Microsoft.Xna.Framework;
using StardewMod.Models;
using StardewMod.Services.ScheduleRouting;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace StardewMod.Services;

internal sealed partial class NpcScheduleEditorService
{
    public CompiledRoutePreview BuildStopRoutePreview(string npcName, EditableScheduleRule rule, int stopIndex, EditableScheduleStop? stopOverride = null)
    {
        if (!Context.IsWorldReady || stopIndex < 0 || stopIndex >= rule.Stops.Count)
        {
            return new CompiledRoutePreview();
        }

        NPC npc = this.RequireNpc(npcName);
        EditableScheduleStop previewStop = stopOverride?.Clone() ?? rule.Stops[stopIndex].Clone();
        RouteAnchor anchor = this.GetAnchorForEditableStop(npc, rule, stopIndex);
        return this.BuildCompiledRoutePreview(npc, rule.RuleKey, anchor, previewStop, logWarnings: false);
    }

    private string ResolveFallbackRuleKey(NPC npc)
    {
        if (npc.TryLoadSchedule())
        {
            return npc.ScheduleKey ?? "spring";
        }

        return "spring";
    }

    private EditableScheduleRule BuildEditableFromRaw(string npcName, string ruleKey)
    {
        Dictionary<string, string> rawData = this.GetRawScheduleData(npcName);
        if (!rawData.TryGetValue(ruleKey, out string? rawScript))
        {
            rawScript = string.Empty;
        }

        NPC npc = this.RequireNpc(npcName);
        ResolvedRawSchedule resolved = this.ResolveRawSchedule(npc, ruleKey, rawScript);
        Dictionary<int, SchedulePathDescription> parsed = string.IsNullOrWhiteSpace(resolved.RawScript)
            ? new Dictionary<int, SchedulePathDescription>()
            : npc.parseMasterSchedule(resolved.ResolvedKey, resolved.RawScript) ?? new Dictionary<int, SchedulePathDescription>();
        RawScheduleSemantics semantics = this.ParseRawScheduleSemantics(npc, resolved.RawScript);
        List<EditableScheduleStop> stops = this.BuildEditableStops(parsed, semantics);

        if (stops.Count == 0)
        {
            stops.Add(this.CreateFallbackStop(npc));
        }

        return new EditableScheduleRule
        {
            NpcName = npcName,
            RuleKey = ruleKey,
            DisplayName = ScheduleRuleClassifier.GetDisplayName(ruleKey),
            Category = ScheduleRuleClassifier.GetCategory(ruleKey),
            PreviewText = ScheduleRuleClassifier.BuildPreview(rawScript),
            IsOverride = false,
            StartPoint = semantics.StartPoint.Clone(),
            Stops = stops
        };
    }

    private EditableScheduleRule BuildEditableFromOverride(string npcName, NpcScheduleOverrideData overrideData)
    {
        EditableScheduleStartPoint startPoint = this.DeserializeStartPoint(overrideData.StartPoint, npcName);
        List<EditableScheduleStop> stops = overrideData.Stops.Select(this.DeserializeStop).ToList();
        if (stops.Count == 0)
        {
            stops.Add(this.CreateFallbackStop(this.RequireNpc(npcName)));
        }

        return new EditableScheduleRule
        {
            NpcName = npcName,
            RuleKey = overrideData.RuleKey,
            DisplayName = ScheduleRuleClassifier.GetDisplayName(overrideData.RuleKey),
            Category = ScheduleRuleClassifier.GetCategory(overrideData.RuleKey),
            PreviewText = "已保存自定义路线",
            IsOverride = true,
            StartPoint = startPoint,
            Stops = stops
        };
    }

    private List<EditableScheduleStop> BuildEditableStops(Dictionary<int, SchedulePathDescription> parsed, RawScheduleSemantics semantics)
    {
        List<KeyValuePair<int, SchedulePathDescription>> parsedStops = parsed
            .OrderBy(pair => pair.Key)
            .ToList();
        List<EditableScheduleStop> stops = new();
        int totalCount = Math.Max(parsedStops.Count, semantics.Stops.Count);

        for (int i = 0; i < totalCount; i++)
        {
            RawScheduleStopSemantics? rawStop = i < semantics.Stops.Count ? semantics.Stops[i] : null;
            KeyValuePair<int, SchedulePathDescription>? parsedStop = i < parsedStops.Count ? parsedStops[i] : null;
            stops.Add(this.BuildEditableStop(parsedStop, rawStop));
        }

        return stops;
    }

    private EditableScheduleStop BuildEditableStop(KeyValuePair<int, SchedulePathDescription>? parsedStop, RawScheduleStopSemantics? rawStop)
    {
        List<TilePointData> routeTiles = parsedStop?.Value.route?
            .Select(point => new TilePointData(point))
            .ToList() ?? new List<TilePointData>();

        TilePointData fallbackTarget = rawStop is not null
            ? rawStop.TargetTile.Clone()
            : new TilePointData(parsedStop?.Value.targetTile ?? Point.Zero);
        TilePointData targetTile = routeTiles.Count > 0
            ? routeTiles[^1].Clone()
            : fallbackTarget;

        return new EditableScheduleStop
        {
            Time = ScheduleTimeHelper.NormalizeStopTime(rawStop?.Time ?? parsedStop?.Key ?? 700),
            TimeMode = rawStop?.TimeMode ?? ScheduleTimeMode.Departure,
            LocationName = rawStop?.LocationName
                ?? parsedStop?.Value.targetLocationName
                ?? string.Empty,
            FacingDirection = rawStop?.FacingDirection ?? parsedStop?.Value.facingDirection ?? 2,
            EndBehavior = rawStop?.EndBehavior
                ?? parsedStop?.Value.endOfRouteBehavior
                ?? string.Empty,
            EndMessage = rawStop?.EndMessage
                ?? parsedStop?.Value.endOfRouteMessage
                ?? string.Empty,
            TargetTile = targetTile,
            RouteTiles = routeTiles
        };
    }

    private EditableScheduleStop CreateFallbackStop(NPC? npc = null)
    {
        Point playerTile;
        string locationName;
        if (npc is not null)
        {
            playerTile = this.GetNpcDefaultTile(npc);
            locationName = npc.DefaultMap;
        }
        else
        {
            playerTile = Context.IsWorldReady ? Game1.player.TilePoint : Point.Zero;
            locationName = Context.IsWorldReady ? Game1.currentLocation?.NameOrUniqueName ?? "SeedShop" : "SeedShop";
        }

        return new EditableScheduleStop
        {
            Time = 700,
            TimeMode = ScheduleTimeMode.Departure,
            LocationName = locationName,
            FacingDirection = 2,
            TargetTile = new TilePointData(playerTile),
            RouteTiles = new List<TilePointData> { new(playerTile) }
        };
    }
}

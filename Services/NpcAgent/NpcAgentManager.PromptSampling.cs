using System.Text.Json;
using StardewMod.Models;
using StardewValley;
using StardewValley.GameData;
using StardewValley.TokenizableStrings;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private bool TryBuildPromptSnapshot(
        string npcName,
        EditableScheduleRule rule,
        int promptRound,
        string? otherNpcName,
        out NpcPromptRefreshState refreshState)
    {
        refreshState = new NpcPromptRefreshState();

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            return false;
        }

        Dictionary<string, string> basicProfile = this.memoryStore.GetOrCreateProfile(npc);
        NpcPersonalityProfile personalityProfile = this.personalityService.GetPersonalityProfile(npc);
        NpcAgentRuntimeSummary runtimeSummary = this.GetRuntimeSummary(npcName);
        NpcAgentPromptSnapshot snapshot = new()
        {
            NpcName = npc.Name,
            DisplayName = npc.displayName,
            PromptRound = Math.Max(1, promptRound),
            GameDate = this.BuildGameDateString(),
            TimeOfDay = Game1.timeOfDay,
            TimeText = Game1.getTimeOfDayString(Game1.timeOfDay),
            Metadata = this.BuildPromptMetadata(npc, runtimeSummary, otherNpcName),
            ScheduleSummary = this.scheduleEditorService.BuildRuleSummary(rule),
            ScheduleDetailJson = this.BuildPromptScheduleDetail(rule)
        };
        refreshState = new NpcPromptRefreshState
        {
            Snapshot = snapshot,
            RuntimeSummary = runtimeSummary,
            BasicProfile = basicProfile,
            PersonalityProfile = personalityProfile
        };

        return true;
    }

    private NpcPromptMetadata BuildPromptMetadata(NPC npc, NpcAgentRuntimeSummary runtimeSummary, string? otherNpcName)
    {
        return new NpcPromptMetadata
        {
            Temporal = this.BuildTemporalMetadata(),
            Weather = this.BuildWeatherMetadata(),
            Festival = this.BuildFestivalMetadata(npc),
            Npc = this.BuildObservedNpcMetadata(npc, runtimeSummary),
            Farmer = this.BuildVisibleFarmerMetadata(npc),
            OtherNpc = this.BuildVisibleOtherNpcMetadata(npc, otherNpcName),
            NearbyNpcs = this.BuildNearbyNpcMetadataList(npc),
            Relationship = new NpcRelationshipMetadata
            {
                FriendshipHearts = Game1.player.getFriendshipHeartLevelForNPC(npc.Name)
            }
        };
    }

    private NpcTemporalMetadata BuildTemporalMetadata()
    {
        return new NpcTemporalMetadata
        {
            DateText = this.BuildGameDateString(),
            Season = Game1.currentSeason,
            DayOfMonth = Game1.dayOfMonth,
            Year = Game1.year,
            DayOfWeek = Game1.Date.DayOfWeek.ToString(),
            TimeOfDay = Game1.timeOfDay,
            TimeText = Game1.getTimeOfDayString(Game1.timeOfDay),
            IsNight = Game1.timeOfDay >= 1900 || Game1.timeOfDay < 600
        };
    }

    private NpcWeatherMetadata BuildWeatherMetadata()
    {
        return new NpcWeatherMetadata
        {
            CurrentKind = DescribeCurrentWeather(),
            TomorrowKind = NormalizeWeatherToken(Game1.weatherForTomorrow),
            IsRaining = Game1.isRaining,
            IsSnowing = Game1.isSnowing,
            IsLightning = Game1.isLightning,
            IsDebrisWeather = Game1.isDebrisWeather,
            IsGreenRain = Game1.isGreenRain,
            WeatherIcon = Game1.weatherIcon
        };
    }

    private NpcFestivalMetadata BuildFestivalMetadata(NPC npc)
    {
        string? locationContextId = npc.currentLocation?.GetLocationContextId();
        string activeFestivalId = $"{Utility.getSeasonKey(Game1.season)}{Game1.dayOfMonth}";
        if (Utility.isFestivalDay(locationContextId) &&
            Event.tryToLoadFestivalData(activeFestivalId, out _, out Dictionary<string, string> festivalData, out string locationName, out int startTime, out int endTime))
        {
            return new NpcFestivalMetadata
            {
                HasFestivalToday = true,
                IsActiveFestivalDay = true,
                IsPassiveFestivalDay = false,
                IsFestivalOpenNow = Game1.timeOfDay >= startTime && Game1.timeOfDay <= endTime,
                FestivalType = "active",
                FestivalId = activeFestivalId,
                FestivalName = festivalData.TryGetValue("name", out string? festivalName) ? festivalName : activeFestivalId,
                FestivalLocationName = locationName ?? string.Empty,
                StartTime = startTime,
                EndTime = endTime,
                PassiveFestivalDayIndex = -1
            };
        }

        if (Utility.TryGetPassiveFestivalDataForDay(Game1.dayOfMonth, Game1.season, locationContextId, out string passiveFestivalId, out PassiveFestivalData passiveFestivalData))
        {
            return new NpcFestivalMetadata
            {
                HasFestivalToday = true,
                IsActiveFestivalDay = false,
                IsPassiveFestivalDay = true,
                IsFestivalOpenNow = Utility.IsPassiveFestivalOpen(passiveFestivalId),
                FestivalType = "passive",
                FestivalId = passiveFestivalId ?? string.Empty,
                FestivalName = string.IsNullOrWhiteSpace(passiveFestivalData.DisplayName)
                    ? passiveFestivalId ?? string.Empty
                    : TokenParser.ParseText(passiveFestivalData.DisplayName),
                FestivalLocationName = ResolvePassiveFestivalLocationName(passiveFestivalData),
                StartTime = passiveFestivalData.StartTime,
                EndTime = 2600,
                PassiveFestivalDayIndex = string.IsNullOrWhiteSpace(passiveFestivalId)
                    ? -1
                    : Utility.GetDayOfPassiveFestival(passiveFestivalId)
            };
        }

        return new NpcFestivalMetadata
        {
            HasFestivalToday = false,
            FestivalType = "none"
        };
    }

    private NpcObservedNpcMetadata BuildObservedNpcMetadata(NPC npc, NpcAgentRuntimeSummary runtimeSummary)
    {
        return new NpcObservedNpcMetadata
        {
            MapName = npc.currentLocation?.NameOrUniqueName ?? string.Empty,
            TileX = npc.TilePoint.X,
            TileY = npc.TilePoint.Y,
            FacingDirection = npc.FacingDirection,
            IsMoving = npc.isMoving(),
            IsEmoting = npc.isEmoting,
            CurrentEmoteId = npc.CurrentEmote,
            CurrentEmoteName = NpcEmoteCatalog.DescribeCurrent(npc.CurrentEmote),
            MoodHint = runtimeSummary.LiveState.MoodHint
        };
    }

    private NpcVisibleFarmerMetadata BuildVisibleFarmerMetadata(NPC npc)
    {
        Farmer farmer = Game1.player;
        FarmerPerceptionState perception = this.GetFarmerPerceptionState(npc);
        string farmerLocationName = farmer.currentLocation?.NameOrUniqueName ?? string.Empty;

        NpcVisibleFarmerMetadata metadata = new()
        {
            IsVisibleToNpc = perception.IsSameMap && perception.IsWithinPerceptionRadius,
            IsSameMap = perception.IsSameMap,
            IsWithinPerceptionRadius = perception.IsWithinPerceptionRadius,
            DistanceTiles = perception.DistanceTiles,
            PerceptionRadiusTiles = perception.RadiusTiles,
            VisibilityNote = perception.VisibilityNote
        };
        if (!metadata.IsVisibleToNpc)
        {
            return metadata;
        }

        metadata.MapName = farmerLocationName;
        metadata.TileX = farmer.TilePoint.X;
        metadata.TileY = farmer.TilePoint.Y;
        metadata.FacingDirection = farmer.FacingDirection;
        metadata.HeldObjectQualifiedItemId = farmer.ActiveObject?.QualifiedItemId ?? string.Empty;
        metadata.HeldObjectDisplayName = farmer.ActiveObject?.DisplayName ?? string.Empty;
        metadata.CurrentToolQualifiedItemId = farmer.CurrentTool?.QualifiedItemId ?? string.Empty;
        metadata.CurrentToolDisplayName = farmer.CurrentTool?.DisplayName ?? string.Empty;
        metadata.Stamina = farmer.Stamina;
        metadata.MaxStamina = farmer.MaxStamina;
        metadata.StatusEffects = farmer.buffs.AppliedBuffs.Values
            .OrderBy(buff => buff.displayName ?? buff.id, StringComparer.OrdinalIgnoreCase)
            .Select(buff => new NpcVisibleStatusEffectMetadata
            {
                Id = buff.id ?? string.Empty,
                DisplayName = buff.displayName ?? string.Empty,
                Description = buff.description ?? string.Empty,
                RemainingMilliseconds = buff.millisecondsDuration,
                Visible = buff.visible,
                HasStatEffects = buff.HasAnyEffects()
            })
            .ToList();
        return metadata;
    }

    private NpcVisibleOtherNpcMetadata BuildVisibleOtherNpcMetadata(NPC npc, string? otherNpcName)
    {
        string normalizedOtherNpcName = (otherNpcName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedOtherNpcName))
        {
            return new NpcVisibleOtherNpcMetadata
            {
                Exists = false,
                VisibilityNote = "no_other_npc_in_current_prompt"
            };
        }

        NPC? otherNpc = Game1.getCharacterFromName(normalizedOtherNpcName);
        if (otherNpc is null)
        {
            return new NpcVisibleOtherNpcMetadata
            {
                Exists = false,
                NpcName = normalizedOtherNpcName,
                VisibilityNote = "other_npc_not_loaded"
            };
        }

        string npcLocationName = npc.currentLocation?.NameOrUniqueName ?? string.Empty;
        string otherNpcLocationName = otherNpc.currentLocation?.NameOrUniqueName ?? string.Empty;
        bool isSameMap = !string.IsNullOrWhiteSpace(npcLocationName) &&
            string.Equals(npcLocationName, otherNpcLocationName, StringComparison.OrdinalIgnoreCase);
        double distanceTiles = ComputeTileDistance(
            npc.TilePoint.X,
            npc.TilePoint.Y,
            otherNpc.TilePoint.X,
            otherNpc.TilePoint.Y);
        int radiusTiles = this.GetNpcPerceptionRadiusTiles();
        NpcAgentRuntimeSummary otherRuntime = this.GetRuntimeSummary(otherNpc.Name);

        return new NpcVisibleOtherNpcMetadata
        {
            Exists = true,
            IsSameMap = isSameMap,
            IsWithinPerceptionRadius = isSameMap && distanceTiles <= radiusTiles,
            VisibilityNote = isSameMap
                ? "other_npc_visible_on_same_map"
                : "other_npc_not_visible_because_different_map",
            RelationshipNote = "visible_npc_to_npc_encounter_context",
            NpcName = otherNpc.Name,
            DisplayName = otherNpc.displayName,
            MapName = otherNpcLocationName,
            TileX = otherNpc.TilePoint.X,
            TileY = otherNpc.TilePoint.Y,
            FacingDirection = otherNpc.FacingDirection,
            IsMoving = otherNpc.isMoving(),
            IsEmoting = otherNpc.isEmoting,
            CurrentEmoteId = otherNpc.CurrentEmote,
            CurrentEmoteName = NpcEmoteCatalog.DescribeCurrent(otherNpc.CurrentEmote),
            MoodHint = otherRuntime.LiveState.MoodHint,
            DistanceTiles = Math.Round(distanceTiles, 2),
            PerceptionRadiusTiles = radiusTiles,
            BasicProfile = this.memoryStore.GetOrCreateProfile(otherNpc),
            PersonalityProfile = this.personalityService.GetPersonalityProfile(otherNpc)
        };
    }

    private List<NpcPerceptionNeighbor> BuildNearbyNpcMetadataList(NPC npc)
    {
        return this.GetNeighborhood(npc.Name).NearbyNpcs
            .Select(CloneNeighbor)
            .ToList();
    }

    private string BuildPromptScheduleDetail(EditableScheduleRule rule)
    {
        return JsonSerializer.Serialize(new
        {
            rule_key = rule.RuleKey,
            start_point = new
            {
                use_custom_start_point = rule.StartPoint.UseCustomStartPoint,
                location_name = rule.StartPoint.LocationName,
                x = rule.StartPoint.Tile.X,
                y = rule.StartPoint.Tile.Y,
                facing_direction = rule.StartPoint.FacingDirection
            },
            stops = rule.Stops
                .OrderBy(stop => stop.Time)
                .Select((stop, index) => new
                {
                    index,
                    time = stop.Time,
                    time_mode = stop.TimeMode.ToString(),
                    location_name = stop.LocationName,
                    target_x = stop.TargetTile.X,
                    target_y = stop.TargetTile.Y,
                    facing_direction = stop.FacingDirection,
                    end_behavior = stop.EndBehavior,
                    end_message = stop.EndMessage
                })
                .ToList()
        });
    }

    private static string DescribeCurrentWeather()
    {
        if (Game1.isGreenRain)
        {
            return "green_rain";
        }

        if (Game1.isLightning)
        {
            return "storm";
        }

        if (Game1.isSnowing)
        {
            return "snow";
        }

        if (Game1.isRaining)
        {
            return "rain";
        }

        if (Game1.isDebrisWeather)
        {
            return "debris";
        }

        return "clear";
    }

    private static string NormalizeWeatherToken(string rawWeather)
    {
        return rawWeather.Trim() switch
        {
            "Sun" => "clear",
            "Rain" => "rain",
            "Storm" => "storm",
            "Snow" => "snow",
            "Wind" => "debris",
            "Festival" => "festival",
            "GreenRain" => "green_rain",
            "Wedding" => "wedding",
            "" => "unknown",
            string other => other.ToLowerInvariant()
        };
    }

    private static string ResolvePassiveFestivalLocationName(PassiveFestivalData? passiveFestivalData)
    {
        if (passiveFestivalData?.MapReplacements is null || passiveFestivalData.MapReplacements.Count == 0)
        {
            return string.Empty;
        }

        return passiveFestivalData.MapReplacements.Values.FirstOrDefault() ?? string.Empty;
    }
}

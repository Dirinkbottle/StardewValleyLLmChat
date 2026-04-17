using Microsoft.Xna.Framework;
using StardewMod.Models;
using StardewValley;
using StardewValley.Network;

namespace StardewMod.Services;

internal sealed partial class NpcScheduleEditorService
{
    private ResolvedRawSchedule ResolveRawSchedule(NPC npc, string scheduleKey, string rawScript)
    {
        return this.ResolveRawSchedule(npc, scheduleKey, rawScript, new List<string>());
    }

    private ResolvedRawSchedule ResolveRawSchedule(NPC npc, string scheduleKey, string rawScript, List<string> visited)
    {
        if (visited.Any(key => string.Equals(key, scheduleKey, StringComparison.OrdinalIgnoreCase)))
        {
            return new ResolvedRawSchedule(scheduleKey, rawScript);
        }

        if (string.IsNullOrWhiteSpace(rawScript))
        {
            return new ResolvedRawSchedule(scheduleKey, string.Empty);
        }

        visited.Add(scheduleKey);
        string[] split = NPC.SplitScheduleCommands(rawScript);
        if (split.Length == 0)
        {
            return new ResolvedRawSchedule(scheduleKey, string.Empty);
        }

        int routesToSkip = 0;
        if (split[0].Contains("GOTO", StringComparison.OrdinalIgnoreCase))
        {
            string newKey = ArgUtility.SplitBySpaceAndGet(split[0], 1);
            if (newKey.Equals("season", StringComparison.OrdinalIgnoreCase))
            {
                newKey = Game1.currentSeason;
            }

            if (npc.getMasterScheduleRawData().TryGetValue(newKey, out string? redirectedScript))
            {
                return this.ResolveRawSchedule(npc, newKey, redirectedScript, visited);
            }

            if (npc.hasMasterScheduleEntry("spring"))
            {
                return this.ResolveRawSchedule(npc, "spring", npc.getMasterScheduleEntry("spring"), visited);
            }

            return new ResolvedRawSchedule(scheduleKey, rawScript);
        }

        if (split[0].Contains("NOT", StringComparison.OrdinalIgnoreCase))
        {
            string[] commandSplit = ArgUtility.SplitBySpace(split[0]);
            if (commandSplit.Length > 2 && commandSplit[1].Equals("friendship", StringComparison.OrdinalIgnoreCase))
            {
                bool conditionMet = false;
                for (int index = 2; index + 1 < commandSplit.Length; index += 2)
                {
                    string who = commandSplit[index];
                    if (!int.TryParse(commandSplit[index + 1], out int level))
                    {
                        continue;
                    }

                    foreach (Farmer farmer in Game1.getAllFarmers())
                    {
                        if (farmer.getFriendshipHeartLevelForNPC(who) >= level)
                        {
                            conditionMet = true;
                            break;
                        }
                    }

                    if (conditionMet)
                    {
                        break;
                    }
                }

                if (conditionMet && npc.hasMasterScheduleEntry("spring"))
                {
                    return this.ResolveRawSchedule(npc, "spring", npc.getMasterScheduleEntry("spring"), visited);
                }

                routesToSkip++;
            }
        }
        else if (split[0].Contains("MAIL", StringComparison.OrdinalIgnoreCase))
        {
            string[] commandSplit = ArgUtility.SplitBySpace(split[0]);
            string mailId = commandSplit.Length > 1 ? commandSplit[1] : string.Empty;
            bool hasMail = Game1.MasterPlayer.mailReceived.Contains(mailId) || NetWorldState.checkAnywhereForWorldStateID(mailId);
            routesToSkip = hasMail ? 2 : 1;
        }

        if (routesToSkip >= split.Length)
        {
            return new ResolvedRawSchedule(scheduleKey, string.Empty);
        }

        if (split[routesToSkip].Contains("GOTO", StringComparison.OrdinalIgnoreCase))
        {
            string newKey = ArgUtility.SplitBySpaceAndGet(split[routesToSkip], 1);
            if (newKey.Equals("no_schedule", StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedRawSchedule(scheduleKey, string.Empty);
            }

            if (newKey.Equals("season", StringComparison.OrdinalIgnoreCase))
            {
                newKey = Game1.currentSeason;
            }

            if (npc.hasMasterScheduleEntry(newKey))
            {
                return this.ResolveRawSchedule(npc, newKey, npc.getMasterScheduleEntry(newKey), visited);
            }

            if (npc.hasMasterScheduleEntry("spring"))
            {
                return this.ResolveRawSchedule(npc, "spring", npc.getMasterScheduleEntry("spring"), visited);
            }
        }

        return new ResolvedRawSchedule(scheduleKey, string.Join('/', split.Skip(routesToSkip)));
    }

    private RawScheduleSemantics ParseRawScheduleSemantics(NPC npc, string rawScript)
    {
        EditableScheduleStartPoint defaultStart = this.GetDefaultStartPoint(npc);
        RawScheduleSemantics semantics = new()
        {
            StartPoint = defaultStart.Clone()
        };

        if (string.IsNullOrWhiteSpace(rawScript))
        {
            return semantics;
        }

        string[] commands = NPC.SplitScheduleCommands(rawScript);
        string previousLocation = defaultStart.LocationName;
        string defaultMap = defaultStart.LocationName;
        int defaultX = defaultStart.Tile.X;
        int defaultY = defaultStart.Tile.Y;

        foreach (string command in commands)
        {
            string[] parts = ArgUtility.SplitBySpace(command);
            if (parts.Length == 0)
            {
                continue;
            }

            int index = 0;
            bool isArrivalTime = false;
            string timeString = parts[index];
            if (timeString.Length > 0 && timeString[0] == 'a')
            {
                isArrivalTime = true;
                timeString = timeString.Substring(1);
            }

            if (!int.TryParse(timeString, out int time))
            {
                continue;
            }

            index++;
            if (index >= parts.Length)
            {
                continue;
            }

            string location = parts[index];
            string? endBehavior = null;
            string? endMessage = null;
            int x = 0;
            int y = 0;
            int facingDirection = 2;

            if (location == "bed")
            {
                if (npc.isMarried())
                {
                    location = "BusStop";
                    x = 9;
                    y = 23;
                    facingDirection = 3;
                }
                else
                {
                    string? defaultSchedule = null;
                    if (npc.hasMasterScheduleEntry("default"))
                    {
                        defaultSchedule = npc.getMasterScheduleEntry("default");
                    }
                    else if (npc.hasMasterScheduleEntry("spring"))
                    {
                        defaultSchedule = npc.getMasterScheduleEntry("spring");
                    }

                    if (!string.IsNullOrWhiteSpace(defaultSchedule))
                    {
                        try
                        {
                            string[] lastScheduleSplit = ArgUtility.SplitBySpace(NPC.SplitScheduleCommands(defaultSchedule)[^1]);
                            location = lastScheduleSplit[1];
                            if (lastScheduleSplit.Length > 3 &&
                                int.TryParse(lastScheduleSplit[2], out int parsedX) &&
                                int.TryParse(lastScheduleSplit[3], out int parsedY))
                            {
                                x = parsedX;
                                y = parsedY;
                            }
                            else
                            {
                                location = defaultMap;
                                x = defaultX;
                                y = defaultY;
                            }
                        }
                        catch
                        {
                            location = defaultMap;
                            x = defaultX;
                            y = defaultY;
                        }
                    }
                    else
                    {
                        location = defaultMap;
                        x = defaultX;
                        y = defaultY;
                    }
                }

                index++;
            }
            else
            {
                if (int.TryParse(location, out _))
                {
                    location = previousLocation;
                    index--;
                }

                index++;
                if (index >= parts.Length || !int.TryParse(parts[index], out x))
                {
                    continue;
                }

                index++;
                if (index >= parts.Length || !int.TryParse(parts[index], out y))
                {
                    continue;
                }

                index++;
                if (index < parts.Length && int.TryParse(parts[index], out int parsedFacing))
                {
                    facingDirection = parsedFacing;
                    index++;
                }
            }

            if (index < parts.Length)
            {
                if (parts[index].Length > 0 && parts[index][0] == '"')
                {
                    endMessage = command.Substring(command.IndexOf('"')).Replace("\"", "");
                }
                else
                {
                    endBehavior = parts[index];
                    index++;
                    if (index < parts.Length && parts[index].Length > 0 && parts[index][0] == '"')
                    {
                        endMessage = command.Substring(command.IndexOf('"')).Replace("\"", "");
                    }
                }
            }

            if (time == 0)
            {
                semantics.StartPoint.UseCustomStartPoint = true;
                semantics.StartPoint.LocationName = location;
                semantics.StartPoint.Tile = new TilePointData(x, y);
                semantics.StartPoint.FacingDirection = facingDirection;
                defaultMap = location;
                defaultX = x;
                defaultY = y;
                previousLocation = location;
                continue;
            }

            RawScheduleStopSemantics stop = new()
            {
                Time = time,
                TimeMode = isArrivalTime ? ScheduleTimeMode.Arrival : ScheduleTimeMode.Departure,
                LocationName = location,
                FacingDirection = facingDirection,
                EndBehavior = endBehavior ?? string.Empty,
                EndMessage = endMessage ?? string.Empty,
                TargetTile = new TilePointData(x, y)
            };
            semantics.Stops.Add(stop);
            previousLocation = location;
        }

        return semantics;
    }

    private sealed class ResolvedRawSchedule
    {
        public ResolvedRawSchedule(string resolvedKey, string rawScript)
        {
            this.ResolvedKey = resolvedKey;
            this.RawScript = rawScript;
        }

        public string ResolvedKey { get; }

        public string RawScript { get; }
    }

    private sealed class RawScheduleSemantics
    {
        public EditableScheduleStartPoint StartPoint { get; set; } = new();

        public List<RawScheduleStopSemantics> Stops { get; } = new();
    }

    private sealed class RawScheduleStopSemantics
    {
        public int Time { get; set; }

        public ScheduleTimeMode TimeMode { get; set; }

        public string LocationName { get; set; } = string.Empty;

        public int FacingDirection { get; set; }

        public string EndBehavior { get; set; } = string.Empty;

        public string EndMessage { get; set; } = string.Empty;

        public TilePointData TargetTile { get; set; } = new();
    }

    private sealed class RouteAnchor
    {
        public RouteAnchor(string locationName, Point tile)
        {
            this.LocationName = locationName;
            this.Tile = tile;
        }

        public string LocationName { get; }

        public Point Tile { get; }
    }
}

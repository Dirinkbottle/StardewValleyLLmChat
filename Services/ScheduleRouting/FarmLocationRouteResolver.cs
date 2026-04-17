using StardewValley;
using StardewValley.Pathfinding;

namespace StardewMod.Services.ScheduleRouting;

/// <summary>
/// 只负责处理 Farm 作为起点或终点时的地图级桥接。
/// 不把 Farm 放回原版全局缓存，避免影响其他 NPC 的普通路由。
/// </summary>
internal sealed class FarmLocationRouteResolver
{
    public bool TryResolveRoute(string startLocation, string endLocation, Gender gender, out string[] route)
    {
        route = Array.Empty<string>();
        bool startIsFarm = IsFarm(startLocation);
        bool endIsFarm = IsFarm(endLocation);
        if (!startIsFarm && !endIsFarm)
        {
            return false;
        }

        List<string> farmNeighbors = this.GetFarmDirectTargets();
        if (farmNeighbors.Count == 0)
        {
            return false;
        }

        if (startIsFarm && endIsFarm)
        {
            route = new[] { "Farm", "Farm" };
            return true;
        }

        if (startIsFarm)
        {
            return this.TryResolveFromFarm(endLocation, gender, farmNeighbors, out route);
        }

        return this.TryResolveToFarm(startLocation, gender, farmNeighbors, out route);
    }

    private bool TryResolveFromFarm(string endLocation, Gender gender, IReadOnlyList<string> farmNeighbors, out string[] route)
    {
        route = Array.Empty<string>();
        string canonicalEnd = NormalizeTargetName(endLocation);
        if (farmNeighbors.Contains(canonicalEnd, StringComparer.OrdinalIgnoreCase))
        {
            route = new[] { "Farm", canonicalEnd };
            return true;
        }

        string[]? bestCandidate = null;
        foreach (string neighbor in farmNeighbors)
        {
            string[]? suffix = WarpPathfindingCache.GetLocationRoute(neighbor, canonicalEnd, gender);
            if (suffix is null || suffix.Length == 0)
            {
                continue;
            }

            string[] candidate = new[] { "Farm" }
                .Concat(suffix)
                .ToArray();
            if (IsBetterCandidate(candidate, bestCandidate))
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null)
        {
            return false;
        }

        route = bestCandidate;
        return true;
    }

    private bool TryResolveToFarm(string startLocation, Gender gender, IReadOnlyList<string> farmNeighbors, out string[] route)
    {
        route = Array.Empty<string>();
        string canonicalStart = NormalizeTargetName(startLocation);
        if (farmNeighbors.Contains(canonicalStart, StringComparer.OrdinalIgnoreCase))
        {
            route = new[] { canonicalStart, "Farm" };
            return true;
        }

        string[]? bestCandidate = null;
        foreach (string neighbor in farmNeighbors)
        {
            string[]? prefix = WarpPathfindingCache.GetLocationRoute(canonicalStart, neighbor, gender);
            if (prefix is null || prefix.Length == 0)
            {
                continue;
            }

            string[] candidate = prefix
                .Concat(new[] { "Farm" })
                .ToArray();
            if (IsBetterCandidate(candidate, bestCandidate))
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null)
        {
            return false;
        }

        route = bestCandidate;
        return true;
    }

    private List<string> GetFarmDirectTargets()
    {
        if (Game1.game1 is null || Game1.locations.Count == 0)
        {
            return new List<string>();
        }

        Farm? farm = Game1.getFarm();
        if (farm is null)
        {
            return new List<string>();
        }

        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (Warp warp in farm.warps)
        {
            string normalized = NormalizeTargetName(warp.TargetName);
            if (!string.IsNullOrWhiteSpace(normalized) && !IsFarm(normalized))
            {
                targets.Add(normalized);
            }
        }

        foreach (string targetName in farm.doors.Values)
        {
            string normalized = NormalizeTargetName(targetName);
            if (!string.IsNullOrWhiteSpace(normalized) && !IsFarm(normalized))
            {
                targets.Add(normalized);
            }
        }

        return targets.ToList();
    }

    private static bool IsBetterCandidate(string[] candidate, string[]? currentBest)
    {
        if (currentBest is null)
        {
            return true;
        }

        if (candidate.Length != currentBest.Length)
        {
            return candidate.Length < currentBest.Length;
        }

        return string.Join(">", candidate).Length < string.Join(">", currentBest).Length;
    }

    private static string NormalizeTargetName(string locationName)
    {
        return locationName switch
        {
            "BoatTunnel" => "IslandSouth",
            _ => locationName
        };
    }

    private static bool IsFarm(string locationName)
    {
        return string.Equals(locationName, "Farm", StringComparison.OrdinalIgnoreCase);
    }
}

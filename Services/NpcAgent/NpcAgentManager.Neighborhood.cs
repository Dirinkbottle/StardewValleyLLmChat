using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private void RefreshNpcPerceptionNeighborhoods()
    {
        this.perceptionNeighborhoods.Clear();
        if (!Context.IsWorldReady)
        {
            return;
        }

        foreach (NPC npc in Utility.getAllVillagers().Where(candidate => candidate.currentLocation is not null && !string.IsNullOrWhiteSpace(candidate.Name)))
        {
            NpcPerceptionNeighborhood neighborhood = this.BuildNeighborhoodForNpc(npc);
            this.perceptionNeighborhoods[npc.Name] = neighborhood;
        }
    }

    private NpcPerceptionNeighborhood GetNeighborhood(string npcName, bool preferLive = false)
    {
        if (!preferLive && this.perceptionNeighborhoods.TryGetValue(npcName, out NpcPerceptionNeighborhood? cachedNeighborhood))
        {
            return CloneNeighborhood(cachedNeighborhood);
        }

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            return new NpcPerceptionNeighborhood
            {
                OwnerNpcName = npcName,
                RadiusTiles = this.GetNpcPerceptionRadiusTiles()
            };
        }

        NpcPerceptionNeighborhood liveNeighborhood = this.BuildNeighborhoodForNpc(npc);
        this.perceptionNeighborhoods[npcName] = liveNeighborhood;
        return CloneNeighborhood(liveNeighborhood);
    }

    private NpcPerceptionNeighborhood BuildNeighborhoodForNpc(NPC ownerNpc)
    {
        string ownerLocationName = ownerNpc.currentLocation?.NameOrUniqueName ?? string.Empty;
        int radiusTiles = this.GetNpcPerceptionRadiusTiles();
        if (string.IsNullOrWhiteSpace(ownerLocationName))
        {
            return new NpcPerceptionNeighborhood
            {
                OwnerNpcName = ownerNpc.Name,
                RadiusTiles = radiusTiles
            };
        }

        List<NpcPerceptionNeighbor> nearbyNpcs = Utility.getAllVillagers()
            .Where(otherNpc =>
                otherNpc.IsVillager &&
                otherNpc.currentLocation is not null &&
                !string.IsNullOrWhiteSpace(otherNpc.Name) &&
                !string.Equals(otherNpc.Name, ownerNpc.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(otherNpc.currentLocation.NameOrUniqueName, ownerLocationName, StringComparison.OrdinalIgnoreCase))
            .Select(otherNpc =>
            {
                double distanceTiles = ComputeTileDistance(
                    ownerNpc.TilePoint.X,
                    ownerNpc.TilePoint.Y,
                    otherNpc.TilePoint.X,
                    otherNpc.TilePoint.Y);
                return new
                {
                    OtherNpc = otherNpc,
                    DistanceTiles = Math.Round(distanceTiles, 2)
                };
            })
            .Where(entry => entry.DistanceTiles <= radiusTiles)
            .OrderBy(entry => entry.DistanceTiles)
            .ThenBy(entry => entry.OtherNpc.displayName, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                NpcAgentSettings targetSettings = this.GetSettings(entry.OtherNpc.Name);
                bool providerUsable = this.IsProviderUsable(targetSettings);
                bool withinActiveWindow = this.IsWithinActiveWindow(targetSettings);
                return new NpcPerceptionNeighbor
                {
                    NpcName = entry.OtherNpc.Name,
                    DisplayName = entry.OtherNpc.displayName,
                    DistanceTiles = entry.DistanceTiles,
                    TileX = entry.OtherNpc.TilePoint.X,
                    TileY = entry.OtherNpc.TilePoint.Y,
                    FacingDirection = entry.OtherNpc.FacingDirection,
                    CanReceiveSyncSpeechNow = targetSettings.Enabled && providerUsable && withinActiveWindow,
                    IsMentionedCandidate = true
                };
            })
            .ToList();

        return new NpcPerceptionNeighborhood
        {
            OwnerNpcName = ownerNpc.Name,
            MapName = ownerLocationName,
            RadiusTiles = radiusTiles,
            NearbyNpcs = nearbyNpcs
        };
    }

    private static NpcPerceptionNeighborhood CloneNeighborhood(NpcPerceptionNeighborhood source)
    {
        return new NpcPerceptionNeighborhood
        {
            OwnerNpcName = source.OwnerNpcName,
            MapName = source.MapName,
            RadiusTiles = source.RadiusTiles,
            NearbyNpcs = source.NearbyNpcs.Select(CloneNeighbor).ToList()
        };
    }

    private static NpcPerceptionNeighbor CloneNeighbor(NpcPerceptionNeighbor source)
    {
        return new NpcPerceptionNeighbor
        {
            NpcName = source.NpcName,
            DisplayName = source.DisplayName,
            DistanceTiles = source.DistanceTiles,
            TileX = source.TileX,
            TileY = source.TileY,
            FacingDirection = source.FacingDirection,
            CanReceiveSyncSpeechNow = source.CanReceiveSyncSpeechNow,
            IsMentionedCandidate = source.IsMentionedCandidate
        };
    }
}

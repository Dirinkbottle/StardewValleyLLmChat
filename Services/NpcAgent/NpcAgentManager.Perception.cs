using Microsoft.Xna.Framework;
using StardewMod.Models;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private readonly record struct FarmerPerceptionState(
        bool IsSameMap,
        bool IsWithinPerceptionRadius,
        double DistanceTiles,
        int RadiusTiles,
        string VisibilityNote);

    private int GetNpcPerceptionRadiusTiles()
    {
        int radiusFromToml = this.configService.Current.Perception.NpcRadiusTiles;
        if (radiusFromToml >= 1)
        {
            return radiusFromToml;
        }

        return Math.Max(1, this.modConfig.NpcPerceptionRadiusTiles);
    }

    private static double ComputeTileDistance(int leftX, int leftY, int rightX, int rightY)
    {
        return Vector2.Distance(new Vector2(leftX, leftY), new Vector2(rightX, rightY));
    }

    private FarmerPerceptionState GetFarmerPerceptionState(NPC npc)
    {
        Farmer farmer = Game1.player;
        string npcLocationName = npc.currentLocation?.NameOrUniqueName ?? string.Empty;
        string farmerLocationName = farmer.currentLocation?.NameOrUniqueName ?? string.Empty;
        bool isSameMap = !string.IsNullOrWhiteSpace(npcLocationName) &&
            string.Equals(npcLocationName, farmerLocationName, StringComparison.OrdinalIgnoreCase);
        int radiusTiles = this.GetNpcPerceptionRadiusTiles();
        if (!isSameMap)
        {
            return new FarmerPerceptionState(
                IsSameMap: false,
                IsWithinPerceptionRadius: false,
                DistanceTiles: -1d,
                RadiusTiles: radiusTiles,
                VisibilityNote: "farmer_not_visible_to_npc_because_different_map");
        }

        double distanceTiles = ComputeTileDistance(
            npc.TilePoint.X,
            npc.TilePoint.Y,
            farmer.TilePoint.X,
            farmer.TilePoint.Y);
        bool isWithinPerceptionRadius = distanceTiles <= radiusTiles;
        return new FarmerPerceptionState(
            IsSameMap: true,
            IsWithinPerceptionRadius: isWithinPerceptionRadius,
            DistanceTiles: Math.Round(distanceTiles, 2),
            RadiusTiles: radiusTiles,
            VisibilityNote: isWithinPerceptionRadius
                ? "farmer_visible_within_perception_radius"
                : "farmer_not_visible_to_npc_because_outside_perception_radius");
    }

    private bool CanSpeakToFarmerNow(NPC npc, out string rejectionReason)
    {
        FarmerPerceptionState perception = this.GetFarmerPerceptionState(npc);
        if (!perception.IsSameMap)
        {
            rejectionReason = "农夫不在同一张地图，当前不能直接对白。";
            return false;
        }

        if (!perception.IsWithinPerceptionRadius)
        {
            rejectionReason = $"农夫当前在同图但超出感知半径 {perception.RadiusTiles} tile，不能远距离对白。";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }
}

using Microsoft.Xna.Framework.Graphics;
using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcScheduleEditorService
{
    public IReadOnlyList<NpcMenuEntry> GetEditableNpcs()
    {
        if (!Context.IsWorldReady)
        {
            return Array.Empty<NpcMenuEntry>();
        }

        Dictionary<string, NPC> villagersByName = Utility.getAllVillagers()
            .Where(npc => npc.IsVillager && !string.IsNullOrWhiteSpace(npc.Name))
            .GroupBy(npc => npc.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        HashSet<string> candidateNames = new(villagersByName.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string npcName in this.saveData.Npcs.Keys)
        {
            candidateNames.Add(npcName);
        }

        return candidateNames
            .Where(this.HasEditableScheduleSource)
            .Select(npcName =>
            {
                villagersByName.TryGetValue(npcName, out NPC? npc);
                npc ??= Game1.getCharacterFromName(npcName);
                return this.CreateNpcMenuEntry(npcName, npc);
            })
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RefreshAllOverriddenNpcSchedules()
    {
        foreach (string npcName in this.saveData.Npcs
                     .Where(pair => pair.Value is not null && pair.Value.Rules.Count > 0)
                     .Select(pair => pair.Key)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            this.TryRefreshNpcSchedule(npcName);
        }
    }

    private bool HasEditableScheduleSource(string npcName)
    {
        if (this.saveData.Npcs.TryGetValue(npcName, out NpcScheduleNpcData? npcData) &&
            npcData is not null &&
            npcData.Rules.Count > 0)
        {
            return true;
        }

        return this.TryGetRawScheduleData(npcName, out _);
    }

    private NpcMenuEntry CreateNpcMenuEntry(string npcName, NPC? npc)
    {
        string displayName = !string.IsNullOrWhiteSpace(npc?.displayName)
            ? npc.displayName
            : npcName;
        Texture2D portrait = npc?.Portrait
            ?? this.TryLoadNpcPortrait(npcName)
            ?? Game1.staminaRect;

        return new NpcMenuEntry
        {
            InternalName = npcName,
            DisplayName = displayName,
            Portrait = portrait
        };
    }

    private Texture2D? TryLoadNpcPortrait(string npcName)
    {
        try
        {
            return this.helper.GameContent.Load<Texture2D>($"Portraits/{npcName}");
        }
        catch (Exception)
        {
            return null;
        }
    }
}

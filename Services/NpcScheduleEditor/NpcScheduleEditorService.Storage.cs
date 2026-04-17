using StardewMod.Models;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcScheduleEditorService
{
    private Dictionary<string, string> GetRawScheduleData(string npcName)
    {
        return this.TryGetRawScheduleData(npcName, out Dictionary<string, string> rawData)
            ? rawData
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private bool TryGetRawScheduleData(string npcName, out Dictionary<string, string> rawData)
    {
        if (this.rawScheduleCache.TryGetValue(npcName, out Dictionary<string, string>? cached))
        {
            rawData = cached;
            return true;
        }

        if (this.missingRawScheduleNames.Contains(npcName))
        {
            rawData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        try
        {
            Dictionary<string, string> loaded = this.helper.GameContent.Load<Dictionary<string, string>>($"Characters/schedules/{npcName}");
            Dictionary<string, string> normalized = new(loaded, StringComparer.OrdinalIgnoreCase);
            this.rawScheduleCache[npcName] = normalized;
            rawData = normalized;
            return true;
        }
        catch (Exception)
        {
            this.missingRawScheduleNames.Add(npcName);
            rawData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    private NPC RequireNpc(string npcName)
    {
        return Game1.getCharacterFromName(npcName) ?? throw new InvalidOperationException($"未找到 NPC：{npcName}");
    }

    private NpcScheduleNpcData GetOrCreateNpcData(string npcName)
    {
        if (!this.saveData.Npcs.TryGetValue(npcName, out NpcScheduleNpcData? data))
        {
            data = new NpcScheduleNpcData();
            this.saveData.Npcs[npcName] = data;
        }

        return data;
    }

    private bool TryGetNpcData(string npcName, out NpcScheduleNpcData npcData)
    {
        if (this.saveData.Npcs.TryGetValue(npcName, out NpcScheduleNpcData? found) && found is not null)
        {
            npcData = found;
            return true;
        }

        npcData = null!;
        return false;
    }

    private bool TryGetOverride(string npcName, string ruleKey, out NpcScheduleOverrideData overrideData)
    {
        if (this.saveData.Npcs.TryGetValue(npcName, out NpcScheduleNpcData? npcData) &&
            npcData is not null &&
            npcData.Rules.TryGetValue(ruleKey, out NpcScheduleOverrideData? found) &&
            found is not null)
        {
            overrideData = found;
            return true;
        }

        overrideData = null!;
        return false;
    }
}

using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    public void EnableAllNpcAgentsAlwaysOn()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        string defaultProvider = this.router.GetUsableProviderNames().FirstOrDefault() ?? string.Empty;
        int changedCount = 0;
        foreach (string npcName in Utility.getAllVillagers()
                     .Where(npc => npc.IsVillager && !string.IsNullOrWhiteSpace(npc.Name))
                     .Select(npc => npc.Name)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            NpcAgentSettings settings = this.GetOrCreateMutableSettings(npcName);
            bool changed = !settings.Enabled ||
                !settings.IsAlwaysOnAllWeek() ||
                string.IsNullOrWhiteSpace(settings.ProviderName);

            settings.Enabled = true;
            if (string.IsNullOrWhiteSpace(settings.ProviderName))
            {
                settings.ProviderName = defaultProvider;
            }

            settings.DayWindows = NpcAgentSettings.CreateAlwaysOnDayWindows();
            this.NormalizeSettings(settings);
            if (changed)
            {
                changedCount++;
            }
        }

        this.helper.Data.WriteSaveData(SaveDataKey, this.saveData);
        this.logger.Info("Config", $"批量启用全部 NPC LLM，全天生效 npc_count={changedCount} default_provider={defaultProvider}");
    }

    public void DisableAllNpcAgents()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        int changedCount = 0;
        foreach ((string npcName, NpcAgentSettings settings) in this.saveData.Npcs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList())
        {
            this.NormalizeSettings(settings);
            if (settings.Enabled)
            {
                changedCount++;
            }

            settings.Enabled = false;
            if (this.states.TryGetValue(npcName, out NpcAgentRuntimeState? state))
            {
                state.IsWithinActiveWindow = false;
                state.WaitingForPlayerResponse = false;
                state.PausePeriodicUntilConversationSettles = false;
                state.AwaitingConversationDialogueClose = false;
                state.IdleStatusOverride = "disabled";
                this.ReleaseAllSyncPairsForNpc(npcName, preserveCooldown: false);
                this.ClearRuntimeOverride(
                    state,
                    restoreBaseline: true,
                    cancellationReason: NpcRequestCancellationReason.AgentDisabled,
                    reason: "disabled");
            }
        }

        this.syncPairStates.Clear();
        this.helper.Data.WriteSaveData(SaveDataKey, this.saveData);
        this.logger.Info("Config", $"批量关闭全部 NPC LLM npc_count={changedCount}");
    }

    private NpcAgentSettings GetOrCreateMutableSettings(string npcName)
    {
        if (!this.saveData.Npcs.TryGetValue(npcName, out NpcAgentSettings? settings))
        {
            settings = new NpcAgentSettings
            {
                ProviderName = this.router.GetUsableProviderNames().FirstOrDefault() ?? string.Empty
            };
            this.saveData.Npcs[npcName] = settings;
        }

        this.NormalizeSettings(settings);
        return settings;
    }
}

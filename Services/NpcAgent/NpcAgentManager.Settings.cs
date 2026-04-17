using Microsoft.Xna.Framework.Graphics;
using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    public IReadOnlyList<NpcAgentMenuEntry> GetNpcMenuEntries()
    {
        if (!Context.IsWorldReady)
        {
            return Array.Empty<NpcAgentMenuEntry>();
        }

        return Utility.getAllVillagers()
            .Where(npc => npc.IsVillager && !string.IsNullOrWhiteSpace(npc.Name))
            .OrderBy(npc => npc.displayName, StringComparer.OrdinalIgnoreCase)
            .Select(npc =>
            {
                NpcAgentSettings settings = this.GetSettings(npc.Name);
                return new NpcAgentMenuEntry
                {
                    InternalName = npc.Name,
                    DisplayName = npc.displayName,
                    Portrait = npc.Portrait,
                    Enabled = settings.Enabled,
                    ProviderName = settings.ProviderName,
                    IsWithinActiveWindow = this.IsWithinActiveWindow(settings),
                    ActiveWindowSummary = this.GetTodayWindowSummary(settings)
                };
            })
            .ToList();
    }

    public NpcAgentSettings GetSettings(string npcName)
    {
        if (!this.saveData.Npcs.TryGetValue(npcName, out NpcAgentSettings? settings))
        {
            settings = new NpcAgentSettings
            {
                ProviderName = this.router.GetUsableProviderNames().FirstOrDefault() ?? string.Empty
            };
            this.NormalizeSettings(settings);
            this.saveData.Npcs[npcName] = settings;
            this.helper.Data.WriteSaveData(SaveDataKey, this.saveData);
        }

        this.NormalizeSettings(settings);
        return settings.Clone();
    }

    public void SaveSettings(string npcName, NpcAgentSettings settings)
    {
        this.NormalizeSettings(settings);
        NpcAgentSettings normalized = settings.Clone();
        if (this.saveData.Npcs.TryGetValue(npcName, out NpcAgentSettings? existingSettings) &&
            AreSettingsEquivalent(existingSettings, normalized))
        {
            return;
        }

        this.saveData.Npcs[npcName] = normalized;
        this.helper.Data.WriteSaveData(SaveDataKey, this.saveData);
        this.logger.Info("Config", $"保存 NPC 设置 enabled={normalized.Enabled} provider={normalized.ProviderName} interval={normalized.PeriodicIntervalSeconds}s", npcName);

        if (!normalized.Enabled && this.states.TryGetValue(npcName, out NpcAgentRuntimeState? state))
        {
            state.IdleStatusOverride = "disabled";
            this.ReleaseAllSyncPairsForNpc(npcName, preserveCooldown: false);
            this.ClearRuntimeOverride(
                state,
                restoreBaseline: true,
                cancellationReason: NpcRequestCancellationReason.AgentDisabled,
                reason: "disabled");
        }
    }

    public IReadOnlyList<string> GetProviderNames()
    {
        return this.router.GetUsableProviderNames();
    }

    public IReadOnlyList<string> GetConfigErrors()
    {
        return this.configService.ValidationErrors;
    }

    private string GetTodayWindowSummary(NpcAgentSettings settings)
    {
        if (settings.IsAlwaysOnAllWeek())
        {
            return "全天开启";
        }

        string dayName = Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth);
        if (!settings.DayWindows.TryGetValue(dayName, out List<AgentTimeWindow>? windows) || windows.Count == 0)
        {
            return "今天未启用";
        }

        return string.Join(" | ", windows.OrderBy(window => window.StartTime).Select(window => window.ToString()));
    }

    private bool IsWithinActiveWindow(NpcAgentSettings settings)
    {
        string dayName = Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth);
        if (!settings.DayWindows.TryGetValue(dayName, out List<AgentTimeWindow>? windows))
        {
            return false;
        }

        return windows.Any(window => window.Contains(Game1.timeOfDay));
    }

    private static bool AreSettingsEquivalent(NpcAgentSettings left, NpcAgentSettings right)
    {
        if (left.Enabled != right.Enabled ||
            !string.Equals(left.ProviderName, right.ProviderName, StringComparison.OrdinalIgnoreCase) ||
            left.PeriodicIntervalSeconds != right.PeriodicIntervalSeconds ||
            left.AllowBehaviorControl != right.AllowBehaviorControl ||
            left.AllowSpeech != right.AllowSpeech ||
            left.AllowScheduleControl != right.AllowScheduleControl ||
            !string.Equals(left.BaselineScheduleKeyHint, right.BaselineScheduleKeyHint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
        {
            List<AgentTimeWindow> leftWindows = left.DayWindows.TryGetValue(day, out List<AgentTimeWindow>? leftValue)
                ? leftValue
                : new List<AgentTimeWindow>();
            List<AgentTimeWindow> rightWindows = right.DayWindows.TryGetValue(day, out List<AgentTimeWindow>? rightValue)
                ? rightValue
                : new List<AgentTimeWindow>();
            if (leftWindows.Count != rightWindows.Count)
            {
                return false;
            }

            for (int i = 0; i < leftWindows.Count; i++)
            {
                if (leftWindows[i].StartTime != rightWindows[i].StartTime ||
                    leftWindows[i].EndTime != rightWindows[i].EndTime)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void NormalizeSettings(NpcAgentSettings settings)
    {
        settings.PeriodicIntervalSeconds = Math.Clamp(settings.PeriodicIntervalSeconds, 10, 600);
        settings.DayWindows ??= NpcAgentSettings.CreateDefaultDayWindows();
        foreach (string day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
        {
            if (!settings.DayWindows.ContainsKey(day))
            {
                settings.DayWindows[day] = new List<AgentTimeWindow>();
            }

            settings.DayWindows[day] = settings.DayWindows[day]
                .Select(window => new AgentTimeWindow
                {
                    StartTime = Math.Clamp(window.StartTime, 600, 2600),
                    EndTime = Math.Clamp(window.EndTime, 610, 2600)
                })
                .Where(window => window.EndTime > window.StartTime)
                .OrderBy(window => window.StartTime)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(settings.ProviderName))
        {
            settings.ProviderName = this.router.GetUsableProviderNames().FirstOrDefault() ?? string.Empty;
            this.logger.Debug("Config", $"自动选择默认 provider={settings.ProviderName}", null);
        }
    }

    private NpcAgentRuntimeState GetOrCreateState(string npcName)
    {
        if (!this.states.TryGetValue(npcName, out NpcAgentRuntimeState? state))
        {
            state = new NpcAgentRuntimeState
            {
                NpcName = npcName
            };
            this.states[npcName] = state;
        }

        return state;
    }
}

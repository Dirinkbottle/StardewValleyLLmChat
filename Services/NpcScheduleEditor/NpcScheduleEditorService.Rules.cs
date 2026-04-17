using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcScheduleEditorService
{
    /// <summary>
    /// Harmony 后置调用入口。
    /// </summary>
    public void ApplyPostfixOverride(NPC npc)
    {
        if (!Context.IsWorldReady || npc is null)
        {
            return;
        }

        if (!this.TryGetNpcData(npc.Name, out NpcScheduleNpcData npcData) || npcData.Rules.Count == 0)
        {
            return;
        }

        this.TryApplyOverrideForKey(npc, npc.ScheduleKey);
    }

    public IReadOnlyList<ScheduleRuleSummary> GetRuleSummaries(string npcName)
    {
        Dictionary<string, string> rawData = this.GetRawScheduleData(npcName);
        HashSet<string> allKeys = new(rawData.Keys, StringComparer.OrdinalIgnoreCase);

        if (this.TryGetNpcData(npcName, out NpcScheduleNpcData npcData))
        {
            foreach (string key in npcData.Rules.Keys)
            {
                allKeys.Add(key);
            }
        }

        return allKeys
            .OrderBy(key => ScheduleRuleClassifier.GetCategory(key))
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                EditableScheduleRule runtimeRule = null!;
                string revisionId = string.Empty;
                bool hasRuntimePatch = NpcAgentManager.Instance?.TryGetActivePatchRule(npcName, key, out runtimeRule, out revisionId) == true;
                EditableScheduleRule rule = hasRuntimePatch
                    ? runtimeRule
                    : this.GetEditableRule(npcName, key);
                return new ScheduleRuleSummary
                {
                    RuleKey = key,
                    DisplayName = ScheduleRuleClassifier.GetDisplayName(key),
                    Category = ScheduleRuleClassifier.GetCategory(key),
                    HasOverride = this.HasOverride(npcName, key),
                    HasRuntimePatch = hasRuntimePatch,
                    RuntimePatchRevisionId = hasRuntimePatch ? revisionId : string.Empty,
                    StopCount = rule.Stops.Count,
                    PreviewText = hasRuntimePatch ? "当前优先显示运行时 patch" : rule.PreviewText
                };
            })
            .ToList();
    }

    public EditableScheduleRule GetEditableRule(string npcName, string ruleKey)
    {
        if (this.TryGetOverride(npcName, ruleKey, out NpcScheduleOverrideData overrideData))
        {
            return this.BuildEditableFromOverride(npcName, overrideData);
        }

        return this.BuildEditableFromRaw(npcName, ruleKey);
    }

    public EditableScheduleRule GetPreferredEditableRuleForMenu(string npcName, string ruleKey, out string sourceLabel, out bool isRuntimePatch, out string revisionId)
    {
        if (NpcAgentManager.Instance?.TryGetActivePatchRule(npcName, ruleKey, out EditableScheduleRule runtimeRule, out string runtimePatchRevisionId) == true)
        {
            string shortRevision = runtimePatchRevisionId.Length > 8 ? runtimePatchRevisionId[..8] : runtimePatchRevisionId;
            sourceLabel = $"当前显示：运行时 patch 优先（rev {shortRevision}）";
            isRuntimePatch = true;
            revisionId = runtimePatchRevisionId;
            return runtimeRule;
        }

        EditableScheduleRule persistedRule = this.GetEditableRule(npcName, ruleKey).Clone();
        sourceLabel = persistedRule.IsOverride
            ? "当前显示：存档覆盖规则"
            : "当前显示：原版规则";
        isRuntimePatch = false;
        revisionId = string.Empty;
        return persistedRule;
    }

    public void SaveRule(string npcName, EditableScheduleRule rule)
    {
        rule.NormalizeBeforeSave();

        NpcScheduleNpcData npcData = this.GetOrCreateNpcData(npcName);
        npcData.Rules[rule.RuleKey] = new NpcScheduleOverrideData
        {
            RuleKey = rule.RuleKey,
            StartPoint = this.SerializeStartPoint(rule.StartPoint),
            Stops = rule.Stops.Select(this.SerializeStop).ToList()
        };

        this.helper.Data.WriteSaveData(SaveDataKey, this.saveData);
        this.monitor.Log($"已保存 {npcName} 的日程规则 {rule.RuleKey}。", LogLevel.Info);
        this.TryRefreshNpcSchedule(npcName);
    }

    public bool TryApplyRuleToRuntimePatch(string npcName, EditableScheduleRule rule, out string revisionId, out string error)
    {
        revisionId = string.Empty;
        error = "当前没有可编辑的运行时 patch。";
        if (NpcAgentManager.Instance is null)
        {
            return false;
        }

        return NpcAgentManager.Instance.TryUpdateActivePatchRule(npcName, rule.RuleKey, rule, out revisionId, out error);
    }

    public bool TryDiscardRuntimePatch(string npcName, string ruleKey, out string error)
    {
        error = "当前没有可清除的运行时 patch。";
        if (NpcAgentManager.Instance is null)
        {
            return false;
        }

        return NpcAgentManager.Instance.TryDiscardActivePatch(npcName, ruleKey, out error);
    }

    public void RemoveRule(string npcName, string ruleKey)
    {
        if (!this.TryGetNpcData(npcName, out NpcScheduleNpcData npcData))
        {
            return;
        }

        if (!npcData.Rules.Remove(ruleKey))
        {
            return;
        }

        if (npcData.Rules.Count == 0)
        {
            this.saveData.Npcs.Remove(npcName);
        }

        this.helper.Data.WriteSaveData(SaveDataKey, this.saveData);
        this.monitor.Log($"已将 {npcName} 的规则 {ruleKey} 重置为原版。", LogLevel.Info);
        this.TryRefreshNpcSchedule(npcName);
    }

    public bool HasOverride(string npcName, string ruleKey)
    {
        return this.TryGetOverride(npcName, ruleKey, out _);
    }

    public bool TryRefreshNpcSchedule(string npcName)
    {
        if (!Context.IsWorldReady)
        {
            return false;
        }

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            return false;
        }

        npc.InvalidateMasterSchedule();
        npc.queuedSchedulePaths.Clear();
        npc.lastAttemptedSchedule = -1;

        npc.TryLoadSchedule();
        npc.checkSchedule(Game1.timeOfDay);
        return true;
    }

    /// <summary>
    /// 读取当前规则键对应的可编辑规则，可被 AI runtime 当作基线规则。
    /// </summary>
    public EditableScheduleRule GetCurrentEditableRule(string npcName, string? ruleKey = null)
    {
        NPC npc = this.RequireNpc(npcName);
        string resolvedKey = string.IsNullOrWhiteSpace(ruleKey)
            ? npc.ScheduleKey ?? this.ResolveFallbackRuleKey(npc)
            : ruleKey;

        return this.GetEditableRule(npcName, resolvedKey).Clone();
    }

    public EditableScheduleStartPoint GetDefaultStartPointPreview(string npcName)
    {
        return this.GetDefaultStartPoint(this.RequireNpc(npcName));
    }

    /// <summary>
    /// 当前可编辑规则的简短文本摘要，给提示词和调试面板使用。
    /// </summary>
    public string BuildRuleSummary(EditableScheduleRule rule)
    {
        List<string> lines = new();
        if (rule.StartPoint.UseCustomStartPoint)
        {
            lines.Add($"0 {rule.StartPoint.LocationName} ({rule.StartPoint.Tile.X}, {rule.StartPoint.Tile.Y}) 朝向 {rule.StartPoint.FacingDirection}");
        }
        else
        {
            lines.Add($"日初出生点使用原版默认：{rule.StartPoint.LocationName} ({rule.StartPoint.Tile.X}, {rule.StartPoint.Tile.Y})");
        }

        foreach (EditableScheduleStop stop in rule.Stops.OrderBy(stop => stop.Time))
        {
            string timeText = stop.TimeMode == ScheduleTimeMode.Arrival ? $"a{stop.Time}" : stop.Time.ToString();
            string endText = string.IsNullOrWhiteSpace(stop.EndBehavior) ? string.Empty : $" 行为={stop.EndBehavior}";
            string messageText = string.IsNullOrWhiteSpace(stop.EndMessage) ? string.Empty : $" 说话=\"{stop.EndMessage}\"";
            lines.Add($"{timeText} {stop.LocationName} ({stop.TargetTile.X}, {stop.TargetTile.Y}) 朝向 {stop.FacingDirection}{endText}{messageText}");
        }

        return string.Join('\n', lines);
    }
}

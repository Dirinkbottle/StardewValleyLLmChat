using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    public void ApplyPostfixOverride(NPC npc)
    {
        if (!Context.IsWorldReady || npc is null)
        {
            return;
        }

        if (!this.states.TryGetValue(npc.Name, out NpcAgentRuntimeState? state) ||
            !state.IsWithinActiveWindow ||
            state.ActivePatch?.Rule is null)
        {
            return;
        }

        this.scheduleEditorService.TryApplyLiveRule(npc, state.ActivePatch.Rule, preserveCurrentMovement: true);
    }

    public bool TryGetActivePatchRule(string npcName, string? ruleKey, out EditableScheduleRule rule, out string revisionId)
    {
        rule = null!;
        revisionId = string.Empty;
        if (!this.states.TryGetValue(npcName, out NpcAgentRuntimeState? state) || state.ActivePatch?.Rule is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ruleKey) &&
            !string.Equals(state.ActivePatch.Rule.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rule = state.ActivePatch.Rule.Clone();
        revisionId = state.ActivePatch.RevisionId;
        return true;
    }

    public bool TryUpdateActivePatchRule(string npcName, string ruleKey, EditableScheduleRule rule, out string revisionId, out string error)
    {
        revisionId = string.Empty;
        error = string.Empty;
        if (!Context.IsWorldReady)
        {
            error = "当前不在存档内，无法修改运行时 patch。";
            return false;
        }

        if (!this.states.TryGetValue(npcName, out NpcAgentRuntimeState? state) || state.ActivePatch?.Rule is null)
        {
            error = "当前 NPC 没有活动中的运行时 patch。";
            return false;
        }

        if (!string.Equals(state.ActivePatch.Rule.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase))
        {
            error = $"当前活动 patch 不属于规则 {ruleKey}。";
            return false;
        }

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            error = "目标 NPC 未加载，无法修改运行时 patch。";
            return false;
        }

        EditableScheduleRule normalizedRule = rule.Clone();
        normalizedRule.NormalizeBeforeSave();
        if (!this.scheduleEditorService.TryApplyLiveRule(npc, normalizedRule, preserveCurrentMovement: false))
        {
            error = "运行时 patch 应用失败。";
            return false;
        }

        RuntimeSchedulePatch updatedPatch = new()
        {
            RevisionId = Guid.NewGuid().ToString("N"),
            Reason = "editor_runtime_patch_update",
            ApplyFromTime = Game1.timeOfDay,
            Rule = normalizedRule,
            ExpiresAtWindowEnd = state.ActivePatch.ExpiresAtWindowEnd,
            DiffSummary = this.scheduleEditorService.BuildRuleSummary(normalizedRule)
        };

        state.ActivePatch = updatedPatch;
        state.LastPatchSummary = updatedPatch.DiffSummary;
        state.LastRejectionReason = string.Empty;
        state.PushDebugLine($"编辑器更新运行时 patch：{updatedPatch.RevisionId}");
        this.logger.Info("Patch", $"编辑器更新运行时 patch revision={updatedPatch.RevisionId}", npcName);
        this.LogRuleSummary("Patch", $"编辑器应用后的当前 patch revision={updatedPatch.RevisionId}", npcName, updatedPatch.Rule);
        revisionId = updatedPatch.RevisionId;
        return true;
    }

    public bool TryDiscardActivePatch(string npcName, string ruleKey, out string error)
    {
        error = string.Empty;
        if (!Context.IsWorldReady)
        {
            error = "当前不在存档内，无法丢弃运行时 patch。";
            return false;
        }

        if (!this.states.TryGetValue(npcName, out NpcAgentRuntimeState? state) || state.ActivePatch?.Rule is null)
        {
            error = "当前 NPC 没有活动中的运行时 patch。";
            return false;
        }

        if (!string.Equals(state.ActivePatch.Rule.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase))
        {
            error = $"当前活动 patch 不属于规则 {ruleKey}。";
            return false;
        }

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            error = "目标 NPC 未加载，无法丢弃运行时 patch。";
            return false;
        }

        EditableScheduleRule fallbackRule = this.scheduleEditorService.GetEditableRule(npcName, ruleKey);
        if (!this.scheduleEditorService.TryApplyLiveRule(npc, fallbackRule, preserveCurrentMovement: false))
        {
            error = "恢复普通规则失败。";
            return false;
        }

        state.ActivePatch = null;
        state.LastPatchSummary = string.Empty;
        state.LastRejectionReason = string.Empty;
        state.PushDebugLine($"编辑器丢弃运行时 patch：{ruleKey}");
        this.logger.Info("Patch", $"编辑器丢弃运行时 patch rule_key={ruleKey}", npcName);
        return true;
    }

    private void ClearRuntimeOverride(
        NpcAgentRuntimeState state,
        bool restoreBaseline,
        bool logChange = true,
        NpcRequestCancellationReason cancellationReason = NpcRequestCancellationReason.RuntimeReset,
        string reason = "runtime_reset")
    {
        this.QueueRuntimeReset(state, restoreBaseline, logChange, reason);
        this.TryCancelActiveRequest(state, cancellationReason, reason);
        this.TryApplyQueuedRuntimeReset(state);
    }

    private bool TryCaptureBaseline(string npcName, NpcAgentRuntimeState state, NpcAgentSettings settings)
    {
        try
        {
            EditableScheduleRule rule = this.scheduleEditorService.GetCurrentEditableRule(npcName);
            state.BaselineRule = rule.Clone();
            state.BaselineScheduleKey = rule.RuleKey;
            settings.BaselineScheduleKeyHint = rule.RuleKey;
            this.helper.Data.WriteSaveData(SaveDataKey, this.saveData);
            this.logger.Info("Patch", $"采集基线成功 rule_key={rule.RuleKey}", npcName);
            return true;
        }
        catch (Exception ex)
        {
            state.PushDebugLine($"采集基线失败：{ex.Message}");
            this.logger.Warn("Patch", $"采集基线失败：{ex.Message}", npcName);
            return false;
        }
    }

    private EditableScheduleRule? TryGetCurrentRule(string npcName)
    {
        try
        {
            return this.scheduleEditorService.GetCurrentEditableRule(npcName);
        }
        catch
        {
            return null;
        }
    }

    private void LogRuleSummary(string area, string title, string npcName, EditableScheduleRule rule)
    {
        string summary = this.scheduleEditorService.BuildRuleSummary(rule);
        string[] lines = summary.Split('\n', StringSplitOptions.None);
        this.logger.Info(area, $"{title} summary_lines={lines.Length}", npcName);
        foreach (string line in lines)
        {
            this.logger.Info(area, $"  {line}", npcName);
        }
    }
}

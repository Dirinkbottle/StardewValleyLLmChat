using System.Text.Json;
using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private string ReplaceFutureSchedule(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 replace_future_schedule，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        ScheduleMutationPlan mutationPlan = this.ResolveScheduleMutationPlan(context, args, context.Snapshot.TimeOfDay);
        int applyFromTime = mutationPlan.EffectiveApplyFromTime;
        List<EditableScheduleStop> newStops = this
            .ApplyMinimumTimeToStops(ReadStops(args.GetProperty("stops")), applyFromTime)
            .Select(stop => stop.Clone())
            .ToList();
        if (newStops.Count == 0)
        {
            return Serialize(new { ok = false, error = "replace_future_schedule 需要至少一个 stop。" });
        }

        List<EditableScheduleStop> pastStops = context.WorkingRule.Stops
            .Where(stop => stop.Time < applyFromTime)
            .Select(stop => stop.Clone())
            .ToList();
        pastStops.AddRange(newStops);
        context.WorkingRule.Stops = pastStops;
        context.WorkingRule.NormalizeBeforeSave();
        context.ScheduleTouched = true;
        context.ApplyFromTime = applyFromTime;
        context.PatchReason = this.MergePatchReason(
            ReadString(args, "reason", $"{context.TriggerEvent.EventType} 触发的未来行程替换"),
            mutationPlan);
        this.logger.Info("Tool", $"replace_future_schedule 成功，apply_from={applyFromTime} stops={newStops.Count}", context.NpcName);
        this.LogScheduleIntent(context, "replace_future_schedule", applyFromTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = applyFromTime,
            guard_applied = mutationPlan.GuardApplied,
            guard_message = mutationPlan.GuardMessage,
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private string InsertScheduleStops(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 insert_schedule_stops，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        ScheduleMutationPlan mutationPlan = this.ResolveScheduleMutationPlan(context, args, context.Snapshot.TimeOfDay);
        int applyFromTime = mutationPlan.EffectiveApplyFromTime;
        List<EditableScheduleStop> stops = this
            .ApplyMinimumTimeToStops(ReadStops(args.GetProperty("stops")), applyFromTime)
            .Select(stop => stop.Clone())
            .ToList();
        if (stops.Count == 0)
        {
            return Serialize(new { ok = false, error = "insert_schedule_stops 需要至少一个 stop。" });
        }

        int requestedInsertBeforeIndex = ReadInt(args, "insert_before_index", context.WorkingRule.Stops.Count);
        int effectiveInsertBeforeIndex = this.ResolveInsertBeforeIndex(
            context.WorkingRule,
            applyFromTime,
            requestedInsertBeforeIndex,
            out bool indexGuardApplied,
            out string indexGuardMessage);
        context.WorkingRule.Stops.InsertRange(effectiveInsertBeforeIndex, stops);
        context.WorkingRule.NormalizeBeforeSave();
        context.ScheduleTouched = true;
        context.ApplyFromTime = Math.Min(context.ApplyFromTime ?? applyFromTime, applyFromTime);
        context.PatchReason = this.MergePatchReason(
            ReadString(args, "reason", $"{context.TriggerEvent.EventType} 插入站点"),
            mutationPlan,
            indexGuardMessage);
        this.logger.Info(
            "Tool",
            $"insert_schedule_stops 成功，apply_from={applyFromTime} insert_before={effectiveInsertBeforeIndex} stop_count={stops.Count}",
            context.NpcName);
        this.LogScheduleIntent(context, "insert_schedule_stops", applyFromTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = applyFromTime,
            effective_insert_before_index = effectiveInsertBeforeIndex,
            guard_applied = mutationPlan.GuardApplied || indexGuardApplied,
            guard_message = CombineGuardMessages(mutationPlan.GuardMessage, indexGuardMessage),
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private string UpdateScheduleStops(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 update_schedule_stops，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        if (!TryGetProperty(args, "updates", out JsonElement updatesElement) || updatesElement.ValueKind != JsonValueKind.Array)
        {
            return Serialize(new { ok = false, error = "updates 不能为空。" });
        }

        ScheduleMutationPlan mutationPlan = this.ResolveScheduleMutationPlan(context, args, context.Snapshot.TimeOfDay);
        int applyFromTime = mutationPlan.EffectiveApplyFromTime;
        List<int> updatedIndexes = new();
        foreach (JsonElement update in updatesElement.EnumerateArray())
        {
            int index = ReadInt(update, "index", -1);
            if (index < 0 || index >= context.WorkingRule.Stops.Count)
            {
                this.logger.Warn("Tool", $"update_schedule_stops 索引越界 index={index}", context.NpcName);
                return Serialize(new { ok = false, error = $"站点索引越界：{index}" });
            }

            EditableScheduleStop existingStop = context.WorkingRule.Stops[index];
            if (existingStop.Time < applyFromTime)
            {
                return Serialize(new
                {
                    ok = false,
                    error = $"站点 index={index} 的 declared time={existingStop.Time} 早于允许改写时间 {applyFromTime}，请改后续站点或明确允许打断当前 schedule。"
                });
            }

            if (!TryGetProperty(update, "changes", out JsonElement changesElement) || changesElement.ValueKind != JsonValueKind.Object)
            {
                return Serialize(new { ok = false, error = $"index={index} 缺少 changes。" });
            }

            EditableScheduleStop patchedStop = this.ApplyStopChanges(existingStop, changesElement, applyFromTime);
            context.WorkingRule.Stops[index] = patchedStop;
            updatedIndexes.Add(index);
        }

        if (updatedIndexes.Count == 0)
        {
            return Serialize(new { ok = false, error = "没有可更新的站点。" });
        }

        context.WorkingRule.NormalizeBeforeSave();
        context.ScheduleTouched = true;
        context.ApplyFromTime = Math.Min(context.ApplyFromTime ?? applyFromTime, applyFromTime);
        context.PatchReason = this.MergePatchReason(
            ReadString(args, "reason", $"{context.TriggerEvent.EventType} 修改站点"),
            mutationPlan);
        this.logger.Info(
            "Tool",
            $"update_schedule_stops 成功，apply_from={applyFromTime} indexes={string.Join(",", updatedIndexes)}",
            context.NpcName);
        this.LogScheduleIntent(context, "update_schedule_stops", applyFromTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = applyFromTime,
            updated_indexes = updatedIndexes,
            guard_applied = mutationPlan.GuardApplied,
            guard_message = mutationPlan.GuardMessage,
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private string RemoveScheduleStops(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 remove_schedule_stops，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        ScheduleMutationPlan mutationPlan = this.ResolveScheduleMutationPlan(context, args, context.Snapshot.TimeOfDay);
        int applyFromTime = mutationPlan.EffectiveApplyFromTime;
        int[] indexes = ReadIntArray(args, "indexes")
            .Distinct()
            .OrderByDescending(index => index)
            .ToArray();
        if (indexes.Length == 0)
        {
            return Serialize(new { ok = false, error = "indexes 不能为空。" });
        }

        foreach (int index in indexes)
        {
            if (index < 0 || index >= context.WorkingRule.Stops.Count)
            {
                this.logger.Warn("Tool", $"remove_schedule_stops 索引越界 index={index}", context.NpcName);
                return Serialize(new { ok = false, error = $"站点索引越界：{index}" });
            }

            EditableScheduleStop stop = context.WorkingRule.Stops[index];
            if (stop.Time < applyFromTime)
            {
                return Serialize(new
                {
                    ok = false,
                    error = $"站点 index={index} 的 declared time={stop.Time} 早于允许改写时间 {applyFromTime}，请改后续站点或明确允许打断当前 schedule。"
                });
            }
        }

        if (indexes.Length >= context.WorkingRule.Stops.Count)
        {
            return Serialize(new { ok = false, error = "remove_schedule_stops 不能把整天站点删空；整日重写请改用 replace_entire_schedule。" });
        }

        foreach (int index in indexes)
        {
            context.WorkingRule.Stops.RemoveAt(index);
        }

        context.WorkingRule.NormalizeBeforeSave();
        context.ScheduleTouched = true;
        context.ApplyFromTime = Math.Min(context.ApplyFromTime ?? applyFromTime, applyFromTime);
        context.PatchReason = this.MergePatchReason(
            ReadString(args, "reason", $"{context.TriggerEvent.EventType} 删除站点"),
            mutationPlan);
        this.logger.Info(
            "Tool",
            $"remove_schedule_stops 成功，apply_from={applyFromTime} indexes={string.Join(",", indexes.Reverse())}",
            context.NpcName);
        this.LogScheduleIntent(context, "remove_schedule_stops", applyFromTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = applyFromTime,
            removed_indexes = indexes.OrderBy(index => index).ToArray(),
            guard_applied = mutationPlan.GuardApplied,
            guard_message = mutationPlan.GuardMessage,
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private string ReplaceEntireSchedule(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 replace_entire_schedule，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        bool allowInterruptCurrentSchedule = ReadBool(args, "allow_interrupt_current_schedule", false);
        if (!allowInterruptCurrentSchedule && context.RuntimeSummary.ScheduleState.CurrentExecutionProtected)
        {
            return Serialize(new
            {
                ok = false,
                error = "replace_entire_schedule 会覆盖当前执行段；只有在玩家明确要求整天重写时，才设置 allow_interrupt_current_schedule=true。"
            });
        }

        if (!TryGetProperty(args, "stops", out JsonElement stopsElement) || stopsElement.ValueKind != JsonValueKind.Array)
        {
            return Serialize(new { ok = false, error = "stops 不能为空。" });
        }

        List<EditableScheduleStop> stops = this
            .ApplyMinimumTimeToStops(ReadStops(stopsElement), ScheduleTimeHelper.EarliestStopTime)
            .Select(stop => stop.Clone())
            .ToList();
        if (stops.Count == 0)
        {
            return Serialize(new { ok = false, error = "replace_entire_schedule 需要至少一个 stop。" });
        }

        EditableScheduleRule rewrittenRule = context.WorkingRule.Clone();
        if (TryGetProperty(args, "start_point", out JsonElement startPointElement) && startPointElement.ValueKind == JsonValueKind.Object)
        {
            rewrittenRule.StartPoint = ReadStartPoint(startPointElement, rewrittenRule.StartPoint);
        }

        rewrittenRule.Stops = stops;
        rewrittenRule.NormalizeBeforeSave();
        context.WorkingRule = rewrittenRule;
        context.ScheduleTouched = true;
        context.ApplyFromTime = ScheduleTimeHelper.EarliestStopTime;
        context.PatchReason = ReadString(args, "reason", $"{context.TriggerEvent.EventType} 整日重写 schedule");
        this.logger.Info("Tool", "replace_entire_schedule 成功，整天站点已全部替换。", context.NpcName);
        this.LogScheduleIntent(context, "replace_entire_schedule", ScheduleTimeHelper.EarliestStopTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = ScheduleTimeHelper.EarliestStopTime,
            guard_applied = false,
            guard_message = string.Empty,
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private string AppendFutureStop(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 append_future_stop，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        ScheduleMutationPlan mutationPlan = this.ResolveScheduleMutationPlan(context, args, context.Snapshot.TimeOfDay);
        int applyFromTime = mutationPlan.EffectiveApplyFromTime;
        EditableScheduleStop stop = this.ApplyMinimumTimeToStop(ReadStop(args.GetProperty("stop")), applyFromTime);
        List<EditableScheduleStop> merged = context.WorkingRule.Stops.Select(existing => existing.Clone()).ToList();
        merged.Add(stop);
        context.WorkingRule.Stops = merged;
        context.WorkingRule.NormalizeBeforeSave();
        context.ScheduleTouched = true;
        context.ApplyFromTime = Math.Min(context.ApplyFromTime ?? applyFromTime, applyFromTime);
        context.PatchReason = this.MergePatchReason($"{context.TriggerEvent.EventType} 追加未来站点", mutationPlan);
        this.logger.Info("Tool", $"append_future_stop 成功，apply_from={applyFromTime} total_stops={context.WorkingRule.Stops.Count}", context.NpcName);
        this.LogScheduleIntent(context, "append_future_stop", applyFromTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = applyFromTime,
            guard_applied = mutationPlan.GuardApplied,
            guard_message = mutationPlan.GuardMessage,
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private string UpdateFutureStop(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 update_future_stop，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        ScheduleMutationPlan mutationPlan = this.ResolveScheduleMutationPlan(context, args, context.Snapshot.TimeOfDay);
        int applyFromTime = mutationPlan.EffectiveApplyFromTime;
        int futureIndex = ReadInt(args, "index", 0);
        List<int> actualIndexes = context.WorkingRule.Stops
            .Select((stop, index) => new { stop, index })
            .Where(pair => pair.stop.Time >= applyFromTime)
            .Select(pair => pair.index)
            .ToList();

        if (futureIndex < 0 || futureIndex >= actualIndexes.Count)
        {
            this.logger.Warn("Tool", $"update_future_stop 索引越界 future_index={futureIndex}", context.NpcName);
            return Serialize(new { ok = false, error = $"未来站点索引越界：{futureIndex}" });
        }

        context.WorkingRule.Stops[actualIndexes[futureIndex]] = this.ApplyMinimumTimeToStop(ReadStop(args.GetProperty("stop")), applyFromTime);
        context.WorkingRule.NormalizeBeforeSave();
        context.ScheduleTouched = true;
        context.ApplyFromTime = Math.Min(context.ApplyFromTime ?? applyFromTime, applyFromTime);
        context.PatchReason = this.MergePatchReason($"{context.TriggerEvent.EventType} 修改未来站点", mutationPlan);
        this.logger.Info("Tool", $"update_future_stop 成功，apply_from={applyFromTime} future_index={futureIndex}", context.NpcName);
        this.LogScheduleIntent(context, "update_future_stop", applyFromTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = applyFromTime,
            guard_applied = mutationPlan.GuardApplied,
            guard_message = mutationPlan.GuardMessage,
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private string RemoveFutureStop(NpcToolExecutionContext context, JsonElement args)
    {
        if (!context.AllowScheduleControl)
        {
            this.logger.Warn("Tool", "拒绝 remove_future_stop，当前 NPC 禁止 AI 修改日程。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 修改日程。" });
        }

        ScheduleMutationPlan mutationPlan = this.ResolveScheduleMutationPlan(context, args, context.Snapshot.TimeOfDay);
        int applyFromTime = mutationPlan.EffectiveApplyFromTime;
        int futureIndex = ReadInt(args, "index", 0);
        List<int> actualIndexes = context.WorkingRule.Stops
            .Select((stop, index) => new { stop, index })
            .Where(pair => pair.stop.Time >= applyFromTime)
            .Select(pair => pair.index)
            .ToList();

        if (futureIndex < 0 || futureIndex >= actualIndexes.Count)
        {
            this.logger.Warn("Tool", $"remove_future_stop 索引越界 future_index={futureIndex}", context.NpcName);
            return Serialize(new { ok = false, error = $"未来站点索引越界：{futureIndex}" });
        }

        context.WorkingRule.Stops.RemoveAt(actualIndexes[futureIndex]);
        context.WorkingRule.NormalizeBeforeSave();
        context.ScheduleTouched = true;
        context.ApplyFromTime = Math.Min(context.ApplyFromTime ?? applyFromTime, applyFromTime);
        context.PatchReason = this.MergePatchReason($"{context.TriggerEvent.EventType} 删除未来站点", mutationPlan);
        this.logger.Info("Tool", $"remove_future_stop 成功，apply_from={applyFromTime} future_index={futureIndex}", context.NpcName);
        this.LogScheduleIntent(context, "remove_future_stop", applyFromTime);

        return Serialize(new
        {
            ok = true,
            effective_apply_from_time = applyFromTime,
            guard_applied = mutationPlan.GuardApplied,
            guard_message = mutationPlan.GuardMessage,
            summary = DescribeRule(context.WorkingRule)
        });
    }

    private object DescribeWorkingSchedule(NpcToolExecutionContext context)
    {
        string source = string.Equals(context.RuntimeSummary.ScheduleState.Source, "runtime_patch", StringComparison.OrdinalIgnoreCase)
            ? "patch"
            : "normal";
        int safeMutationTime = context.RuntimeSummary.ScheduleState.SafeMutationTime;
        return new
        {
            source,
            source_detail = context.RuntimeSummary.ScheduleState.Source,
            patch_revision_id = context.RuntimeSummary.PatchRevisionId,
            context.WorkingRule.RuleKey,
            safe_mutation_time = safeMutationTime,
            current_execution_protected = context.RuntimeSummary.ScheduleState.CurrentExecutionProtected,
            mutation_guidance = context.RuntimeSummary.ScheduleState.MutationGuidance,
            start_point = new
            {
                context.WorkingRule.StartPoint.UseCustomStartPoint,
                context.WorkingRule.StartPoint.LocationName,
                x = context.WorkingRule.StartPoint.Tile.X,
                y = context.WorkingRule.StartPoint.Tile.Y,
                context.WorkingRule.StartPoint.FacingDirection
            },
            stops = context.WorkingRule.Stops.Select((stop, index) => new
            {
                index,
                stop.Time,
                time_mode = stop.TimeMode.ToString(),
                stop.LocationName,
                target_x = stop.TargetTile.X,
                target_y = stop.TargetTile.Y,
                stop.FacingDirection,
                stop.EndBehavior,
                stop.EndMessage,
                route_mode = this.HasExplicitManualRoute(stop) ? "manual" : "auto",
                route_tile_count = stop.RouteTiles.Count,
                route_tiles = stop.RouteTiles.Select(tile => new { x = tile.X, y = tile.Y }).ToList(),
                mutable_without_interrupt = stop.Time >= safeMutationTime
            }).ToList()
        };
    }

    private void LogScheduleIntent(NpcToolExecutionContext context, string operation, int applyFromTime)
    {
        string summary = this.scheduleEditorService.BuildRuleSummary(context.WorkingRule);
        string[] lines = summary.Split('\n', StringSplitOptions.None);
        this.logger.Info(
            "Tool",
            $"LLM 计划修改日程 operation={operation} apply_from={applyFromTime} reason={this.logger.Summarize(context.PatchReason, 160)} summary_lines={lines.Length}",
            context.NpcName);
        foreach (string line in lines)
        {
            this.logger.Info("Tool", $"  {line}", context.NpcName);
        }
    }

    private ScheduleMutationPlan ResolveScheduleMutationPlan(NpcToolExecutionContext context, JsonElement args, int fallbackApplyFromTime)
    {
        int requestedApplyFromTime = ReadInt(args, "apply_from_time", fallbackApplyFromTime);
        int effectiveApplyFromTime = Math.Max(requestedApplyFromTime, context.Snapshot.TimeOfDay);
        bool guardApplied = effectiveApplyFromTime != requestedApplyFromTime;
        string guardMessage = guardApplied
            ? $"apply_from_time 已提升到当前时间 {effectiveApplyFromTime}，避免回写过去时段。"
            : string.Empty;

        bool allowInterruptCurrentSchedule = ReadBool(args, "allow_interrupt_current_schedule", false);
        int safeMutationTime = context.RuntimeSummary.ScheduleState.SafeMutationTime;
        if (!allowInterruptCurrentSchedule &&
            context.RuntimeSummary.ScheduleState.CurrentExecutionProtected &&
            safeMutationTime > 0 &&
            effectiveApplyFromTime < safeMutationTime)
        {
            effectiveApplyFromTime = safeMutationTime;
            guardApplied = true;
            guardMessage = context.RuntimeSummary.ScheduleState.MutationGuidance;
        }

        if (guardApplied)
        {
            this.logger.Warn("Tool", $"schedule 改动已被安全边界重写 apply_from={effectiveApplyFromTime} message={guardMessage}", context.NpcName);
        }

        return new ScheduleMutationPlan(requestedApplyFromTime, effectiveApplyFromTime, guardApplied, guardMessage);
    }

    private string MergePatchReason(string baseReason, ScheduleMutationPlan mutationPlan, string extraGuardMessage = "")
    {
        string combinedGuardMessage = CombineGuardMessages(mutationPlan.GuardMessage, extraGuardMessage);
        if (!mutationPlan.GuardApplied && string.IsNullOrWhiteSpace(extraGuardMessage))
        {
            return baseReason;
        }

        if (string.IsNullOrWhiteSpace(combinedGuardMessage))
        {
            return baseReason;
        }

        return $"{baseReason}；{combinedGuardMessage}";
    }

    private static string CombineGuardMessages(params string[] messages)
    {
        return string.Join(
            "；",
            messages
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private int ResolveInsertBeforeIndex(
        EditableScheduleRule rule,
        int applyFromTime,
        int requestedInsertBeforeIndex,
        out bool guardApplied,
        out string guardMessage)
    {
        int clampedIndex = Math.Clamp(requestedInsertBeforeIndex, 0, rule.Stops.Count);
        int firstMutableIndex = rule.Stops.FindIndex(stop => stop.Time >= applyFromTime);
        if (firstMutableIndex < 0)
        {
            firstMutableIndex = rule.Stops.Count;
        }

        if (clampedIndex < firstMutableIndex)
        {
            guardApplied = true;
            guardMessage = $"insert_before_index 已提升到 {firstMutableIndex}，避免插入到不可改写的过去/当前段之前。";
            return firstMutableIndex;
        }

        guardApplied = clampedIndex != requestedInsertBeforeIndex;
        guardMessage = guardApplied
            ? $"insert_before_index 已调整为 {clampedIndex}。"
            : string.Empty;
        return clampedIndex;
    }

    private EditableScheduleStop ApplyStopChanges(EditableScheduleStop existingStop, JsonElement changesElement, int applyFromTime)
    {
        EditableScheduleStop updatedStop = existingStop.Clone();
        bool changedLocation = false;
        bool changedTarget = false;

        if (TryGetProperty(changesElement, "time", out _))
        {
            updatedStop.Time = ReadInt(changesElement, "time", updatedStop.Time);
        }

        string timeMode = ReadString(changesElement, "time_mode");
        if (!string.IsNullOrWhiteSpace(timeMode))
        {
            updatedStop.TimeMode = string.Equals(timeMode, "arrival", StringComparison.OrdinalIgnoreCase)
                ? ScheduleTimeMode.Arrival
                : ScheduleTimeMode.Departure;
        }

        string locationName = ReadString(changesElement, "location_name");
        if (!string.IsNullOrWhiteSpace(locationName))
        {
            updatedStop.LocationName = locationName;
            changedLocation = true;
        }

        if (TryGetProperty(changesElement, "target_x", out _))
        {
            updatedStop.TargetTile.X = ReadInt(changesElement, "target_x", updatedStop.TargetTile.X);
            changedTarget = true;
        }

        if (TryGetProperty(changesElement, "target_y", out _))
        {
            updatedStop.TargetTile.Y = ReadInt(changesElement, "target_y", updatedStop.TargetTile.Y);
            changedTarget = true;
        }

        if (TryGetProperty(changesElement, "facing_direction", out _))
        {
            updatedStop.FacingDirection = ReadInt(changesElement, "facing_direction", updatedStop.FacingDirection);
        }

        if (ReadBool(changesElement, "clear_end_behavior", false))
        {
            updatedStop.EndBehavior = string.Empty;
        }
        else if (TryGetProperty(changesElement, "end_behavior", out _))
        {
            updatedStop.EndBehavior = ReadString(changesElement, "end_behavior");
        }

        if (ReadBool(changesElement, "clear_end_message", false))
        {
            updatedStop.EndMessage = string.Empty;
        }
        else if (TryGetProperty(changesElement, "end_message", out _))
        {
            updatedStop.EndMessage = ReadString(changesElement, "end_message");
        }

        string routeMode = ReadString(changesElement, "route_mode", "keep").Trim().ToLowerInvariant();
        bool hasRouteTilesPatch = TryGetProperty(changesElement, "route_tiles", out JsonElement routeTilesElement) &&
                                  routeTilesElement.ValueKind == JsonValueKind.Array;
        if (hasRouteTilesPatch)
        {
            updatedStop.RouteTiles = ReadTilePoints(routeTilesElement);
            if (updatedStop.RouteTiles.Count > 0)
            {
                updatedStop.TargetTile = updatedStop.RouteTiles[^1].Clone();
                changedTarget = false;
            }
        }

        if (string.Equals(routeMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            updatedStop.RouteTiles = new List<TilePointData> { updatedStop.TargetTile.Clone() };
        }
        else if (string.Equals(routeMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            if (updatedStop.RouteTiles.Count == 0)
            {
                updatedStop.RouteTiles.Add(updatedStop.TargetTile.Clone());
            }
        }
        else if (hasRouteTilesPatch)
        {
            if (updatedStop.RouteTiles.Count == 0)
            {
                updatedStop.RouteTiles.Add(updatedStop.TargetTile.Clone());
            }
        }
        else if (changedLocation || changedTarget)
        {
            updatedStop.RouteTiles = new List<TilePointData> { updatedStop.TargetTile.Clone() };
        }

        return this.ApplyMinimumTimeToStop(updatedStop, applyFromTime);
    }

    private EditableScheduleStartPoint ReadStartPoint(JsonElement element, EditableScheduleStartPoint fallback)
    {
        EditableScheduleStartPoint startPoint = fallback.Clone();
        if (TryGetProperty(element, "use_custom_start_point", out _))
        {
            startPoint.UseCustomStartPoint = ReadBool(element, "use_custom_start_point", startPoint.UseCustomStartPoint);
        }

        string locationName = ReadString(element, "location_name");
        if (!string.IsNullOrWhiteSpace(locationName))
        {
            startPoint.LocationName = locationName;
        }

        if (TryGetProperty(element, "tile_x", out _))
        {
            startPoint.Tile.X = ReadInt(element, "tile_x", startPoint.Tile.X);
        }

        if (TryGetProperty(element, "tile_y", out _))
        {
            startPoint.Tile.Y = ReadInt(element, "tile_y", startPoint.Tile.Y);
        }

        if (TryGetProperty(element, "facing_direction", out _))
        {
            startPoint.FacingDirection = ReadInt(element, "facing_direction", startPoint.FacingDirection);
        }

        return startPoint;
    }

    private List<EditableScheduleStop> ApplyMinimumTimeToStops(IEnumerable<EditableScheduleStop> stops, int minimumTime)
    {
        return stops.Select(stop => this.ApplyMinimumTimeToStop(stop, minimumTime)).ToList();
    }

    private EditableScheduleStop ApplyMinimumTimeToStop(EditableScheduleStop stop, int minimumTime)
    {
        EditableScheduleStop adjusted = stop.Clone();
        adjusted.Time = Math.Max(ScheduleTimeHelper.NormalizeStopTime(adjusted.Time), minimumTime);
        if (adjusted.RouteTiles.Count == 0)
        {
            adjusted.RouteTiles.Add(adjusted.TargetTile.Clone());
        }

        adjusted.TargetTile = adjusted.RouteTiles[^1].Clone();
        return adjusted;
    }

    private bool HasExplicitManualRoute(EditableScheduleStop stop)
    {
        return stop.RouteTiles.Count > 1;
    }

    private static EditableScheduleStop ReadStop(JsonElement element)
    {
        string timeMode = ReadString(element, "time_mode", "departure");
        TilePointData targetTile = new(ReadInt(element, "target_x", 0), ReadInt(element, "target_y", 0));
        string routeMode = ReadString(element, "route_mode", "auto").Trim().ToLowerInvariant();
        List<TilePointData> routeTiles = TryGetProperty(element, "route_tiles", out JsonElement routeTilesElement) &&
                                         routeTilesElement.ValueKind == JsonValueKind.Array
            ? ReadTilePoints(routeTilesElement)
            : new List<TilePointData>();
        if (!string.Equals(routeMode, "manual", StringComparison.OrdinalIgnoreCase) || routeTiles.Count == 0)
        {
            routeTiles = new List<TilePointData> { targetTile.Clone() };
        }

        return new EditableScheduleStop
        {
            Time = ReadInt(element, "time", 700),
            TimeMode = string.Equals(timeMode, "arrival", StringComparison.OrdinalIgnoreCase)
                ? ScheduleTimeMode.Arrival
                : ScheduleTimeMode.Departure,
            LocationName = ReadString(element, "location_name"),
            TargetTile = routeTiles[^1].Clone(),
            FacingDirection = ReadInt(element, "facing_direction", 2),
            EndBehavior = ReadString(element, "end_behavior"),
            EndMessage = ReadString(element, "end_message"),
            RouteTiles = routeTiles
        };
    }

    private static List<EditableScheduleStop> ReadStops(JsonElement element)
    {
        List<EditableScheduleStop> stops = new();
        if (element.ValueKind != JsonValueKind.Array)
        {
            return stops;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            stops.Add(ReadStop(item));
        }

        return stops;
    }

    private static List<TilePointData> ReadTilePoints(JsonElement element)
    {
        List<TilePointData> tiles = new();
        if (element.ValueKind != JsonValueKind.Array)
        {
            return tiles;
        }

        foreach (JsonElement tileElement in element.EnumerateArray())
        {
            if (tileElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            tiles.Add(new TilePointData(
                ReadInt(tileElement, "x", 0),
                ReadInt(tileElement, "y", 0)));
        }

        return tiles;
    }

    private static object DescribeRule(EditableScheduleRule rule)
    {
        return new
        {
            rule.RuleKey,
            start_point = new
            {
                rule.StartPoint.UseCustomStartPoint,
                rule.StartPoint.LocationName,
                rule.StartPoint.Tile.X,
                rule.StartPoint.Tile.Y,
                rule.StartPoint.FacingDirection
            },
            stops = rule.Stops.Select((stop, index) => new
            {
                index,
                stop.Time,
                time_mode = stop.TimeMode.ToString(),
                stop.LocationName,
                target_x = stop.TargetTile.X,
                target_y = stop.TargetTile.Y,
                stop.FacingDirection,
                stop.EndBehavior,
                stop.EndMessage,
                route_tile_count = stop.RouteTiles.Count,
                route_tiles = stop.RouteTiles.Select(tile => new { x = tile.X, y = tile.Y }).ToList()
            }).ToList()
        };
    }
}

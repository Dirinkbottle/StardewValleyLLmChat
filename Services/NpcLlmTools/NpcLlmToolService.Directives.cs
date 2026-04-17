using System.Text.Json;
using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private string EnqueueImmediateAction(NpcToolExecutionContext context, string sourceToolName, JsonElement args)
    {
        if (!context.AllowBehaviorControl)
        {
            this.logger.Warn("Tool", "拒绝 enqueue_immediate_action，当前 NPC 禁止 AI 行为控制。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 执行为控制。" });
        }

        if (!TryReadRequiredBool(args, "broadcast_to_nearby_npcs", out bool broadcastToNearbyNpcs))
        {
            return Serialize(new { ok = false, error = "broadcast_to_nearby_npcs 必填，且必须是 boolean。" });
        }

        JsonElement actionElement = args.GetProperty("action");
        NpcActionRequestType type = Enum.TryParse(ReadString(actionElement, "type"), ignoreCase: true, out NpcActionRequestType parsed)
            ? parsed
            : NpcActionRequestType.PauseAndWait;
        if (type == NpcActionRequestType.PlayRouteAnimation || type == NpcActionRequestType.SpeakToNpc)
        {
            return Serialize(new { ok = false, error = "PlayRouteAnimation 请改用 enqueue_route_animation；SpeakToNpc 请改用 say_to_npc。" });
        }

        string emoteName = NpcEmoteCatalog.Normalize(ReadString(actionElement, "emote_name"));
        if (type == NpcActionRequestType.DoEmote && string.IsNullOrWhiteSpace(emoteName))
        {
            return Serialize(new { ok = false, error = "DoEmote 需要受控 emote_name，例如 happy / heart / angry / question。" });
        }

        string requestReason = ReadString(args, "reason", ReadString(actionElement, "reason"));
        if (type == NpcActionRequestType.SpeakToPlayer &&
            !context.Snapshot.Metadata.Farmer.IsVisibleToNpc)
        {
            return Serialize(new
            {
                ok = false,
                error = $"当前玩家不在 NPC 可感知范围内：{context.Snapshot.Metadata.Farmer.VisibilityNote}"
            });
        }

        NpcActionRequest actionRequest = new()
        {
            Type = type,
            DispatchMode = NpcActionDispatchPolicy.GetDefaultMode(type),
            TargetLocationName = ReadString(actionElement, "target_location_name"),
            TargetTile = new TilePointData(ReadInt(actionElement, "target_x", 0), ReadInt(actionElement, "target_y", 0)),
            FacingDirection = ReadInt(actionElement, "facing_direction", 2),
            EmoteName = emoteName,
            Message = ReadString(actionElement, "message"),
            EndBehavior = ReadString(actionElement, "end_behavior"),
            BroadcastToNearbyNpcs = broadcastToNearbyNpcs,
            BroadcastSummaryHint = ReadString(actionElement, "message", requestReason),
            DurationMilliseconds = ReadInt(actionElement, "duration_milliseconds", 3000),
            Reason = requestReason
        };

        context.EnqueueActionRequest(sourceToolName, actionRequest);
        this.logger.Info("Tool", $"加入动作请求 type={actionRequest.Type} dispatch={actionRequest.DispatchMode} emote={actionRequest.EmoteName} target={actionRequest.TargetLocationName} tile={actionRequest.TargetTile.X},{actionRequest.TargetTile.Y}", context.NpcName);
        return Serialize(new { ok = true, action = actionRequest.Type.ToString(), dispatch_mode = actionRequest.DispatchMode.ToString(), emote_name = actionRequest.EmoteName });
    }

    private string EnqueueEmoteSequence(NpcToolExecutionContext context, string sourceToolName, JsonElement args)
    {
        if (!context.AllowBehaviorControl)
        {
            this.logger.Warn("Tool", "拒绝 enqueue_emote_sequence，当前 NPC 禁止 AI 行为控制。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 执行为控制。" });
        }

        if (!TryReadRequiredBool(args, "broadcast_to_nearby_npcs", out bool broadcastToNearbyNpcs))
        {
            return Serialize(new { ok = false, error = "broadcast_to_nearby_npcs 必填，且必须是 boolean。" });
        }

        if (!TryGetProperty(args, "sequence", out JsonElement sequenceElement) || sequenceElement.ValueKind != JsonValueKind.Array)
        {
            return Serialize(new { ok = false, error = "sequence 不能为空。" });
        }

        string sequenceBroadcastCorrelationId = broadcastToNearbyNpcs
            ? context.TriggerEvent.BroadcastContext?.CorrelationId ?? Guid.NewGuid().ToString("N")
            : string.Empty;
        List<object> acceptedSteps = new();
        foreach (JsonElement step in sequenceElement.EnumerateArray())
        {
            string emoteName = NpcEmoteCatalog.Normalize(ReadString(step, "emote_name"));
            if (string.IsNullOrWhiteSpace(emoteName))
            {
                continue;
            }

            int repeat = Math.Clamp(ReadInt(step, "repeat", 1), 1, 20);
            int duration = Math.Clamp(ReadInt(step, "duration_milliseconds", emoteName == NpcEmoteCatalog.Pause ? 900 : 1200), 300, 5000);
            string stepReason = ReadString(step, "reason", ReadString(args, "reason"));

            for (int i = 0; i < repeat; i++)
            {
                if (string.Equals(emoteName, NpcEmoteCatalog.Pause, StringComparison.OrdinalIgnoreCase))
                {
                    context.EnqueueActionRequest(sourceToolName, new NpcActionRequest
                    {
                        Type = NpcActionRequestType.PauseAndWait,
                        DispatchMode = NpcActionDispatchMode.ImmediateFeedback,
                        BroadcastToNearbyNpcs = broadcastToNearbyNpcs,
                        BroadcastSummaryHint = string.IsNullOrWhiteSpace(stepReason) ? "pause" : stepReason,
                        BroadcastCorrelationId = sequenceBroadcastCorrelationId,
                        DurationMilliseconds = duration,
                        Reason = string.IsNullOrWhiteSpace(stepReason) ? $"表情序列 pause {i + 1}/{repeat}" : stepReason
                    });
                }
                else
                {
                    context.EnqueueActionRequest(sourceToolName, new NpcActionRequest
                    {
                        Type = NpcActionRequestType.DoEmote,
                        DispatchMode = NpcActionDispatchMode.ImmediateFeedback,
                        EmoteName = emoteName,
                        BroadcastToNearbyNpcs = broadcastToNearbyNpcs,
                        BroadcastSummaryHint = emoteName,
                        BroadcastCorrelationId = sequenceBroadcastCorrelationId,
                        DurationMilliseconds = duration,
                        Reason = string.IsNullOrWhiteSpace(stepReason) ? $"表情序列 {emoteName} {i + 1}/{repeat}" : stepReason
                    });
                }
            }

            acceptedSteps.Add(new
            {
                emote_name = emoteName,
                repeat,
                duration_milliseconds = duration
            });
        }

        if (acceptedSteps.Count == 0)
        {
            return Serialize(new { ok = false, error = "没有可用的表情步骤。" });
        }

        this.logger.Info("Tool", $"加入表情序列 steps={acceptedSteps.Count} deferred_actions={context.DeferredActionRequests.Count} immediate_feedback={context.ImmediateFeedbackCount}", context.NpcName);
        return Serialize(new
        {
            ok = true,
            sequence = acceptedSteps
        });
    }

    private string EnqueueSpeech(NpcToolExecutionContext context, string sourceToolName, JsonElement args)
    {
        if (!context.AllowSpeech)
        {
            this.logger.Warn("Tool", "拒绝 npc_say_to_player，当前 NPC 禁止 AI 说话。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 说话。" });
        }

        if (!TryReadRequiredBool(args, "broadcast_to_nearby_npcs", out bool broadcastToNearbyNpcs))
        {
            return Serialize(new { ok = false, error = "broadcast_to_nearby_npcs 必填，且必须是 boolean。" });
        }

        string message = ReadString(args, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            this.logger.Warn("Tool", "拒绝 npc_say_to_player，message 为空。", context.NpcName);
            return Serialize(new { ok = false, error = "message 不能为空。" });
        }

        if (!context.Snapshot.Metadata.Farmer.IsVisibleToNpc)
        {
            this.logger.Warn("Tool", $"拒绝 npc_say_to_player，当前玩家不可见 visibility={context.Snapshot.Metadata.Farmer.VisibilityNote}", context.NpcName);
            return Serialize(new
            {
                ok = false,
                error = $"当前玩家不在 NPC 可感知范围内：{context.Snapshot.Metadata.Farmer.VisibilityNote}"
            });
        }

        context.EnqueueActionRequest(sourceToolName, new NpcActionRequest
        {
            Type = NpcActionRequestType.SpeakToPlayer,
            DispatchMode = NpcActionDispatchMode.ImmediateFeedback,
            Message = message,
            BroadcastToNearbyNpcs = broadcastToNearbyNpcs,
            BroadcastSummaryHint = message,
            Reason = ReadString(args, "reason")
        });

        this.logger.Info("Tool", $"加入对话回复：{this.logger.Summarize(message, 120)}", context.NpcName);
        return Serialize(new { ok = true, message });
    }
}

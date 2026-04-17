using System.Text.Json;
using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private string EnqueueNpcSpeech(NpcToolExecutionContext context, string sourceToolName, JsonElement args)
    {
        if (!context.AllowNpcSpeech)
        {
            this.logger.Warn("Tool", "拒绝 say_to_npc，当前事件不允许 NPC-NPC 对话。", context.NpcName);
            return Serialize(new { ok = false, error = "当前事件不允许 NPC-NPC 对话。" });
        }

        if (!TryReadRequiredBool(args, "broadcast_to_nearby_npcs", out bool broadcastToNearbyNpcs))
        {
            return Serialize(new { ok = false, error = "broadcast_to_nearby_npcs 必填，且必须是 boolean。" });
        }

        string targetNpcName = ReadString(args, "target_npc_name");
        string message = ReadString(args, "message");
        if (string.IsNullOrWhiteSpace(targetNpcName) || string.IsNullOrWhiteSpace(message))
        {
            return Serialize(new { ok = false, error = "target_npc_name 和 message 都不能为空。" });
        }

        NpcSyncTargetValidationResult validation = context.ValidateNpcSpeechTarget?.Invoke(targetNpcName)
            ?? new NpcSyncTargetValidationResult
            {
                Ok = false,
                Error = "当前上下文不支持 NPC-NPC 对话校验。"
            };
        if (!validation.Ok)
        {
            this.logger.Warn("Tool", $"拒绝 say_to_npc target={targetNpcName} error={validation.Error}", context.NpcName);
            return Serialize(new { ok = false, error = validation.Error });
        }

        context.EnqueueActionRequest(sourceToolName, new NpcActionRequest
        {
            Type = NpcActionRequestType.SpeakToNpc,
            DispatchMode = NpcActionDispatchMode.ImmediateFeedback,
            TargetNpcName = validation.TargetNpcName,
            Message = message,
            SyncPairKey = context.TriggerEvent.SyncPairKey,
            BroadcastToNearbyNpcs = broadcastToNearbyNpcs,
            BroadcastSummaryHint = message,
            Reason = ReadString(args, "reason")
        });

        this.logger.Info("Tool", $"加入 NPC-NPC 对话 target={validation.TargetNpcName} message={this.logger.Summarize(message, 120)}", context.NpcName);
        return Serialize(new
        {
            ok = true,
            target_npc_name = validation.TargetNpcName,
            target_display_name = validation.TargetDisplayName,
            map_name = validation.MapName,
            distance_tiles = validation.DistanceTiles,
            message
        });
    }
}

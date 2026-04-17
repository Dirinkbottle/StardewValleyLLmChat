using System.Text.Json;
using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private string EnqueueRouteAnimation(NpcToolExecutionContext context, string sourceToolName, JsonElement args)
    {
        if (!context.AllowBehaviorControl)
        {
            this.logger.Warn("Tool", "拒绝 enqueue_route_animation，当前 NPC 禁止 AI 行为控制。", context.NpcName);
            return Serialize(new { ok = false, error = "当前 NPC 禁止 AI 执行为控制。" });
        }

        if (!TryReadRequiredBool(args, "broadcast_to_nearby_npcs", out bool broadcastToNearbyNpcs))
        {
            return Serialize(new { ok = false, error = "broadcast_to_nearby_npcs 必填，且必须是 boolean。" });
        }

        string animationName = ReadString(args, "animation_name");
        if (!NpcRouteAnimationCatalog.TryResolve(animationName, out string resolvedAnimationName))
        {
            return Serialize(new
            {
                ok = false,
                error = $"未知或不受控的 animation_name：{animationName}",
                available = NpcRouteAnimationCatalog.GetControlledNames()
            });
        }

        NpcActionRequest actionRequest = new()
        {
            Type = NpcActionRequestType.PlayRouteAnimation,
            DispatchMode = NpcActionDispatchMode.ImmediateFeedback,
            AnimationName = resolvedAnimationName,
            BroadcastToNearbyNpcs = broadcastToNearbyNpcs,
            BroadcastSummaryHint = resolvedAnimationName,
            DurationMilliseconds = ReadInt(args, "duration_milliseconds", 1400),
            Reason = ReadString(args, "reason")
        };

        context.EnqueueActionRequest(sourceToolName, actionRequest);
        this.logger.Info("Tool", $"加入原版动画请求 animation={resolvedAnimationName}", context.NpcName);
        return Serialize(new
        {
            ok = true,
            animation_name = resolvedAnimationName,
            dispatch_mode = actionRequest.DispatchMode.ToString()
        });
    }
}

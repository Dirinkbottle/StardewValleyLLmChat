using System.Text.Json;
using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private string IgnoreCurrentBroadcast(NpcToolExecutionContext context, JsonElement args)
    {
        if (context.TriggerEvent.BroadcastContext is null)
        {
            return Serialize(new
            {
                ok = false,
                error = "当前事件不是广播事件，不能调用 ignore_current_broadcast。"
            });
        }

        bool ignored = context.TryIgnoreCurrentBroadcast();
        string reason = ReadString(args, "reason");
        if (!ignored)
        {
            return Serialize(new
            {
                ok = false,
                error = "当前广播上下文无效，无法忽略。"
            });
        }

        this.logger.Info("Tool", $"显式忽略当前广播 correlation={context.TriggerEvent.BroadcastContext.CorrelationId} reason={this.logger.Summarize(reason, 120)}", context.NpcName);
        return Serialize(new
        {
            ok = true,
            correlation_id = context.TriggerEvent.BroadcastContext.CorrelationId,
            ignored = true
        });
    }
}

using System.Text.Json;
using StardewMod.Models;
using StardewMod.Services.Memory;

namespace StardewMod.Services;

/// <summary>
/// 内嵌式 MCP 风格工具层。协议走 provider 原生 tool calling，但工具语义完全由本地控制。
/// </summary>
internal sealed partial class NpcLlmToolService
{
    private readonly NpcScheduleEditorService scheduleEditorService;
    private readonly NpcLlmFactStore factStore;
    private readonly NpcLlmConsoleLogger logger;

    public NpcLlmToolService(NpcScheduleEditorService scheduleEditorService, NpcLlmFactStore factStore, NpcLlmConsoleLogger logger)
    {
        this.scheduleEditorService = scheduleEditorService;
        this.factStore = factStore;
        this.logger = logger;
    }

    public IReadOnlyList<AiToolDefinition> GetToolDefinitions(NpcToolAccessProfile profile)
    {
        return this.BuildToolDefinitions()
            .Where(tool => IsToolAllowedForProfile(tool.Name, profile))
            .ToList();
    }

    public async Task<string> ExecuteAsync(NpcToolExecutionContext context, AiToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (!IsToolAllowedForProfile(invocation.Name, context.ToolAccessProfile))
        {
            string rejected = Serialize(new
            {
                ok = false,
                error = $"当前事件类型不允许调用工具：{invocation.Name}"
            });
            this.logger.Warn("Tool", $"拒绝 {invocation.Name}，tool_profile={context.ToolAccessProfile}", context.NpcName);
            return rejected;
        }

        if (context.IgnoreCurrentBroadcastInvoked &&
            !string.Equals(invocation.Name, "ignore_current_broadcast", StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(new
            {
                ok = false,
                error = "当前广播已被显式忽略，不能继续基于该广播执行新的工具。"
            });
        }

        using JsonDocument argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(invocation.ArgumentsJson) ? "{}" : invocation.ArgumentsJson);
        JsonElement args = argumentsDocument.RootElement;
        AiToolDefinition toolDefinition = this.GetRequiredToolDefinition(invocation.Name);
        string toolReason = this.ExtractToolReason(invocation.Name, args);
        context.RecordToolCall(this.FormatToolCallSummary(invocation.Name, toolDefinition, toolReason));
        this.logger.Info("Tool", $"开始执行 {invocation.Name}", context.NpcName);
        this.logger.Debug("Tool", $"args={this.logger.Summarize(invocation.ArgumentsJson, 400)}", context.NpcName);
        this.logger.Debug("Tool", $"tool_kind={toolDefinition.ToolKind} dispatch_policy={toolDefinition.DispatchPolicy}", context.NpcName);
        this.logger.Debug("Tool", $"parallel_call_description={this.logger.Summarize(string.IsNullOrWhiteSpace(toolDefinition.ParallelCallDescription) ? "<none>" : toolDefinition.ParallelCallDescription, 260)} allow_local_parallel={toolDefinition.AllowLocalParallelExecution}", context.NpcName);
        this.logger.Debug("Tool", $"reason={this.logger.Summarize(string.IsNullOrWhiteSpace(toolReason) ? "<missing>" : toolReason, 220)}", context.NpcName);

        string result = invocation.Name switch
        {
            "get_npc_profile" => Serialize(new
            {
                ok = true,
                basic_profile = context.BasicProfile,
                personality_profile = DescribePersonalityProfile(context.PersonalityProfile),
                personality_source = context.PersonalityProfile.Source.ToString().ToLowerInvariant()
            }),
            "get_recent_memories" => Serialize(new
            {
                ok = true,
                memories = this.GetRecentMemories(context, args)
            }),
            "search_memories" => Serialize(new
            {
                ok = true,
                memories = await this.SearchMemoriesAsync(context, args, cancellationToken)
            }),
            "memory_update" => this.UpdateMemoryFact(context, args),
            "get_today_schedule" => Serialize(new
            {
                ok = true,
                schedule = this.DescribeWorkingSchedule(context)
            }),
            "replace_future_schedule" => this.ReplaceFutureSchedule(context, args),
            "insert_schedule_stops" => this.InsertScheduleStops(context, args),
            "update_schedule_stops" => this.UpdateScheduleStops(context, args),
            "remove_schedule_stops" => this.RemoveScheduleStops(context, args),
            "replace_entire_schedule" => this.ReplaceEntireSchedule(context, args),
            "append_future_stop" => this.AppendFutureStop(context, args),
            "update_future_stop" => this.UpdateFutureStop(context, args),
            "remove_future_stop" => this.RemoveFutureStop(context, args),
            "enqueue_immediate_action" => this.EnqueueImmediateAction(context, invocation.Name, args),
            "enqueue_emote_sequence" => this.EnqueueEmoteSequence(context, invocation.Name, args),
            "enqueue_route_animation" => this.EnqueueRouteAnimation(context, invocation.Name, args),
            "npc_say_to_player" => this.EnqueueSpeech(context, invocation.Name, args),
            "say_to_npc" => this.EnqueueNpcSpeech(context, invocation.Name, args),
            "ignore_current_broadcast" => this.IgnoreCurrentBroadcast(context, args),
            "get_runtime_state" => Serialize(new
            {
                ok = true,
                runtime = this.GetLiveRuntimeSummary(context)
            }),
            _ => Serialize(new
            {
                ok = false,
                error = $"未知工具：{invocation.Name}"
            })
        };

        this.logger.Debug("Tool", $"结果={this.logger.Summarize(result, 420)}", context.NpcName);
        return result;
    }

    private AiToolDefinition GetRequiredToolDefinition(string toolName)
    {
        AiToolDefinition? definition = this.BuildToolDefinitions()
            .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (definition is not null)
        {
            return definition;
        }

        throw new InvalidOperationException($"未注册的 tool definition：{toolName}");
    }

    private string ExtractToolReason(string toolName, JsonElement args)
    {
        string topLevelReason = ReadString(args, "reason").Trim();
        if (!string.IsNullOrWhiteSpace(topLevelReason))
        {
            return topLevelReason;
        }

        if (string.Equals(toolName, "enqueue_immediate_action", StringComparison.OrdinalIgnoreCase) &&
            TryGetProperty(args, "action", out JsonElement actionElement))
        {
            return ReadString(actionElement, "reason").Trim();
        }

        return string.Empty;
    }

    private string FormatToolCallSummary(string toolName, AiToolDefinition definition, string reason)
    {
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "<missing>" : reason;
        return $"{toolName} [{definition.ToolKind}/{definition.DispatchPolicy}] reason={normalizedReason}";
    }

    private NpcAgentRuntimeSummary GetLiveRuntimeSummary(NpcToolExecutionContext context)
    {
        context.RefreshLiveSampling(Math.Max(1, context.Snapshot.PromptRound));
        return context.RuntimeSummary;
    }

    private static object DescribePersonalityProfile(NpcPersonalityProfile profile)
    {
        return new
        {
            name = profile.Name,
            gender = profile.Gender,
            speech_style = profile.SpeechStyle,
            work_style = profile.WorkStyle,
            entertainment_style = profile.EntertainmentStyle,
            hobbies = profile.Hobbies,
            dislikes = profile.Dislikes,
            likes = profile.Likes,
            secrets = profile.Secrets,
            thinking_style = profile.ThinkingStyle,
            raw_markdown = profile.RawMarkdown,
            sections = profile.Sections.Select(section => new
            {
                key = section.Key,
                title = section.Title,
                content = section.Content,
                recognized = section.Recognized
            }).ToList()
        };
    }

    private static bool IsToolAllowedForProfile(string toolName, NpcToolAccessProfile profile)
    {
        return profile switch
        {
            NpcToolAccessProfile.Maintenance => toolName switch
            {
                "get_npc_profile" => true,
                "get_recent_memories" => true,
                "search_memories" => true,
                "memory_update" => true,
                "get_runtime_state" => true,
                _ => false
            },
            NpcToolAccessProfile.NpcSync => toolName switch
            {
                "get_npc_profile" => true,
                "get_recent_memories" => true,
                "search_memories" => true,
                "memory_update" => true,
                "get_runtime_state" => true,
                "say_to_npc" => true,
                "enqueue_route_animation" => true,
                _ => false
            },
            NpcToolAccessProfile.Ambient => toolName switch
            {
                "get_npc_profile" => true,
                "get_recent_memories" => true,
                "search_memories" => true,
                "memory_update" => true,
                "get_today_schedule" => true,
                "get_runtime_state" => true,
                _ => false
            },
            NpcToolAccessProfile.Reactive => toolName switch
            {
                "get_npc_profile" => true,
                "get_recent_memories" => true,
                "search_memories" => true,
                "memory_update" => true,
                "get_today_schedule" => true,
                "get_runtime_state" => true,
                "enqueue_immediate_action" => true,
                "enqueue_emote_sequence" => true,
                "enqueue_route_animation" => true,
                "npc_say_to_player" => true,
                _ => false
            },
            NpcToolAccessProfile.Broadcast => toolName switch
            {
                "get_npc_profile" => true,
                "get_recent_memories" => true,
                "search_memories" => true,
                "memory_update" => true,
                "get_runtime_state" => true,
                "npc_say_to_player" => true,
                "say_to_npc" => true,
                "enqueue_immediate_action" => true,
                "enqueue_emote_sequence" => true,
                "enqueue_route_animation" => true,
                "ignore_current_broadcast" => true,
                _ => false
            },
            _ => true
        };
    }
}

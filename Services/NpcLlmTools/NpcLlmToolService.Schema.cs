using System.Text.Json;
using System.Text.Json.Nodes;
using StardewMod.Models;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private static AiToolDefinition CreateTool(
        string name,
        string description,
        NpcToolKind toolKind,
        NpcToolDispatchPolicy dispatchPolicy,
        object schema,
        string parallelCallDescription = "",
        bool allowLocalParallelExecution = false,
        bool supportsNpcBroadcast = false)
    {
        return new AiToolDefinition
        {
            Name = name,
            Description = description,
            ToolKind = toolKind,
            DispatchPolicy = dispatchPolicy,
            ParallelCallDescription = parallelCallDescription,
            AllowLocalParallelExecution = allowLocalParallelExecution,
            SupportsNpcBroadcast = supportsNpcBroadcast,
            InputSchema = BuildToolSchema(schema, supportsNpcBroadcast)
        };
    }

    private IReadOnlyList<AiToolDefinition> BuildToolDefinitions()
    {
        return new List<AiToolDefinition>
        {
            CreateTool("get_npc_profile", "读取当前 NPC 的基础资料与人格档案。会返回 basic_profile、personality_profile 和 personality_source。", NpcToolKind.Query, NpcToolDispatchPolicy.None, new { type = "object", properties = new { reason = new { type = "string" } }, additionalProperties = false }, "可与其它只读查询同轮并发。适用：同时查人格档案和记忆；同时查资料和最近互动；也可与一句不依赖结果的过渡对白同轮共发。", true),
            CreateTool("get_recent_memories", "读取最近的互动与日常记忆。", NpcToolKind.Query, NpcToolDispatchPolicy.None, new
            {
                type = "object",
                properties = new
                {
                    limit = new { type = "integer", minimum = 1, maximum = 20 },
                    event_types = new { type = "array", items = new { type = "string" } },
                    reason = new { type = "string" }
                }
            }, "可与 search_memories、get_npc_profile 这类只读查询同轮并发。适用：同时查最近互动和语义检索；同时查最近礼物与最近玩家偏好；也可与一句“让我想想…”的过渡对白同轮共发。", true),
            CreateTool("search_memories", "按语义检索记忆。", NpcToolKind.Query, NpcToolDispatchPolicy.None, new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string" },
                    top_k = new { type = "integer", minimum = 1, maximum = 10 },
                    event_types = new { type = "array", items = new { type = "string" } },
                    reason = new { type = "string" }
                },
                required = new[] { "query" }
            }, "可与其它只读查询同轮并发，但不要与 memory_update 或 schedule 修改并行落地。适用：同轮查“礼物记录”和“最近互动”；同轮查不同关键词；也可与一句不依赖结果的过渡对白同轮共发。", true),
            CreateTool("memory_update", "新增、覆盖或删除结构化事实记忆。适合记录长期偏好、当天状态和被玩家纠正后的明确信息。", NpcToolKind.Mutation, NpcToolDispatchPolicy.Immediate, new
            {
                type = "object",
                properties = new
                {
                    operation = new { type = "string", @enum = new[] { "upsert", "remove" } },
                    key = new { type = "string" },
                    scope = new { type = "string", @enum = new[] { "persistent", "today" } },
                    category = new { type = "string" },
                    summary = new { type = "string" },
                    value = new { type = "string" },
                    reason = new { type = "string" }
                },
                required = new[] { "operation", "key", "scope" }
            }, "不要与其它 mutation 同轮并发，也不要和依赖其结果的查询同轮发出。适用：在拿到足够证据后单独 upsert；纠正旧事实时单独覆盖；day_idle 中单独整理 facts。"),
            CreateTool("get_today_schedule", "读取当前今天的完整 schedule，返回当前生效 source=patch|normal、每个 stop 的绝对 index，以及当前安全改写边界。", NpcToolKind.Query, NpcToolDispatchPolicy.None, new { type = "object", properties = new { reason = new { type = "string" } }, additionalProperties = false }, "通常单独调用更稳妥；如果只是补一个索引信息，可与 get_runtime_state 同轮共发，但不建议和 schedule mutation 同轮并发。"),
            CreateTool("replace_future_schedule", "从某个时刻开始整体替换未来行程。只有在你明确要丢弃该时刻之后的旧站点并整体重排时才使用。", NpcToolKind.Mutation, NpcToolDispatchPolicy.Deferred, new
            {
                type = "object",
                properties = new
                {
                    apply_from_time = new { type = "integer" },
                    allow_interrupt_current_schedule = new { type = "boolean" },
                    reason = new { type = "string" },
                    stops = new
                    {
                        type = "array",
                        items = BuildStopSchema()
                    }
                },
                required = new[] { "apply_from_time", "stops" }
            }, "不要与其它 schedule mutation 同轮并发。适用：确认要整体替换 future tail 后单独调用；不要一边 replace 一边 update；不要与 memory_update 混在同一批。"),
            CreateTool("insert_schedule_stops", "在当前 working schedule 中某个绝对 index 前插入一个或多个站点。后续未删除站点会保留。index 使用 get_today_schedule 返回的绝对 index。", NpcToolKind.Mutation, NpcToolDispatchPolicy.Deferred, new
            {
                type = "object",
                properties = new
                {
                    apply_from_time = new { type = "integer" },
                    allow_interrupt_current_schedule = new { type = "boolean" },
                    insert_before_index = new { type = "integer", minimum = 0 },
                    reason = new { type = "string" },
                    stops = new
                    {
                        type = "array",
                        items = BuildStopSchema()
                    }
                },
                required = new[] { "apply_from_time", "stops" }
            }, "不要与其它 schedule mutation 同轮并发。适用：插入未来一站时单独调用；先读 schedule 再 insert；不要同时 insert+remove 去拼一次复杂改写。"),
            CreateTool("update_schedule_stops", "按绝对 index 修改一个或多个既有站点。changes 只填要改的字段，未提供的字段保持原值。", NpcToolKind.Mutation, NpcToolDispatchPolicy.Deferred, new
            {
                type = "object",
                properties = new
                {
                    apply_from_time = new { type = "integer" },
                    allow_interrupt_current_schedule = new { type = "boolean" },
                    reason = new { type = "string" },
                    updates = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                index = new { type = "integer", minimum = 0 },
                                changes = BuildStopPatchSchema()
                            },
                            required = new[] { "index", "changes" }
                        }
                    }
                },
                required = new[] { "apply_from_time", "updates" }
            }, "不要与其它 schedule mutation 同轮并发。适用：只改若干字段时单独调用；同一批 update 内可改多个 stop；不要同时再发 replace_future_schedule。"),
            CreateTool("remove_schedule_stops", "按绝对 index 删除一个或多个既有站点。只删除指定项，其余后续站点会保留。", NpcToolKind.Mutation, NpcToolDispatchPolicy.Deferred, new
            {
                type = "object",
                properties = new
                {
                    apply_from_time = new { type = "integer" },
                    allow_interrupt_current_schedule = new { type = "boolean" },
                    reason = new { type = "string" },
                    indexes = new { type = "array", items = new { type = "integer", minimum = 0 } }
                },
                required = new[] { "apply_from_time", "indexes" }
            }, "不要与其它 schedule mutation 同轮并发。适用：单独删除若干站点；不要同轮再 insert 去抵消；先读索引再 remove。"),
            CreateTool("replace_entire_schedule", "清空当前 working schedule 的全部站点，并用一份全新的整日 schedule 替换。仅在玩家明确要求整天重写时使用。", NpcToolKind.Mutation, NpcToolDispatchPolicy.Deferred, new
            {
                type = "object",
                properties = new
                {
                    allow_interrupt_current_schedule = new { type = "boolean" },
                    reason = new { type = "string" },
                    start_point = BuildStartPointSchema(),
                    stops = new
                    {
                        type = "array",
                        items = BuildStopSchema()
                    }
                },
                required = new[] { "stops" }
            }, "不要与其它 schedule mutation 同轮并发。适用：玩家明确要求整天重写时单独调用；不要和 update/insert/remove 混发；先确认需求再 replace_entire_schedule。"),
            CreateTool("enqueue_immediate_action", "提交一个动作事件请求。本地会按动作类型自动分流到即时反馈链或请求结束后的延迟执行链。若 broadcast_to_nearby_npcs=true，则动作真正落地时会广播给同图且仍在感知半径内的其它 NPC。", NpcToolKind.ActionRequest, NpcToolDispatchPolicy.Mixed, new
            {
                type = "object",
                properties = new
                {
                    reason = new { type = "string" },
                    action = BuildDirectiveSchema()
                },
                required = new[] { "action" }
            }, "可以与不依赖其结果的查询同轮共发，但本地会按时序落地。适用：一边开始查记忆一边先做一个表情；先朝向玩家再查资料；不要同轮堆太多互相冲突的动作。", supportsNpcBroadcast: true),
            CreateTool("enqueue_emote_sequence", "提交一个表情序列事件请求。本地会等待上一条表情/停顿完成，再继续下一条。若 broadcast_to_nearby_npcs=true，则每一步真正执行时都会广播给附近 NPC。", NpcToolKind.ActionRequest, NpcToolDispatchPolicy.Immediate, new
            {
                type = "object",
                properties = new
                {
                    sequence = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                emote_name = BuildEmoteNameSchema(includePause: true),
                                repeat = new { type = "integer", minimum = 1, maximum = 20 },
                                duration_milliseconds = new { type = "integer", minimum = 300, maximum = 5000 },
                                reason = new { type = "string" }
                            },
                            required = new[] { "emote_name" }
                        }
                    },
                    reason = new { type = "string" }
                },
                required = new[] { "sequence" }
            }, "可以与不依赖其结果的查询同轮共发，但不要与其它复杂动作序列同轮堆叠。适用：先给出一个思考表情序列再查记忆；先 pause 再查资料；不要同时发多个相互覆盖的序列。", supportsNpcBroadcast: true),
            CreateTool("enqueue_route_animation", "提交一个受控原版路由动画请求。只接受本地目录中的 animation_name；简单表情优先用 enqueue_immediate_action 或 enqueue_emote_sequence，原版精灵动画优先用本工具。若 broadcast_to_nearby_npcs=true，则动画真正执行时会广播给附近 NPC。", NpcToolKind.ActionRequest, NpcToolDispatchPolicy.Immediate, new
            {
                type = "object",
                properties = new
                {
                    animation_name = BuildRouteAnimationNameSchema(),
                    duration_milliseconds = new { type = "integer", minimum = 300, maximum = 8000 },
                    reason = new { type = "string" }
                },
                required = new[] { "animation_name" }
            }, "可以与不依赖其结果的只读查询同轮共发，用于给长任务补一个即时现场动作。适用：查记忆时先播一个思考/惊讶动画；先做原版动作再继续查资料；不要与多个互斥动画同轮混发。", supportsNpcBroadcast: true),
            CreateTool("npc_say_to_player", "提交一个对话事件请求，让 NPC 通过对话框回复玩家。若 broadcast_to_nearby_npcs=true，则对白真正弹出时会广播给附近 NPC。若已说完且没有别的必要动作，应立刻结束本轮，不要再继续无关查询.", NpcToolKind.ActionRequest, NpcToolDispatchPolicy.Immediate, new
            {
                type = "object",
                properties = new
                {
                    message = new { type = "string" },
                    reason = new { type = "string" }
                },
                required = new[] { "message" }
            }, "可以与不依赖其结果的只读查询同轮共发，常用于长任务先给玩家一句过渡反馈。适用：同轮先说“让我想想…”再查记忆；先简短确认再查资料；最终答案应在下一轮基于查询结果再说。", supportsNpcBroadcast: true),
            CreateTool("say_to_npc", "让当前 NPC 对同地图且仍在感知范围内的另一名 NPC 说一句话。若 broadcast_to_nearby_npcs=true，则真正落地时会作为公开可见事件广播给附近 NPC；不会再强制触发旧式 reply 链路。", NpcToolKind.ActionRequest, NpcToolDispatchPolicy.Immediate, new
            {
                type = "object",
                properties = new
                {
                    target_npc_name = new { type = "string" },
                    message = new { type = "string" },
                    reason = new { type = "string" }
                },
                required = new[] { "target_npc_name", "message" }
            }, "可以与不依赖其结果的只读查询同轮共发，但本地会按时序落地。适用：先打招呼同时查对方相关记忆；先做一句短回应同时查最近互动；不要同轮连续发多句互相冲突的 NPC-NPC 对话。", supportsNpcBroadcast: true),
            CreateTool("get_runtime_state", "读取当前 NPC agent 的运行时状态。", NpcToolKind.Query, NpcToolDispatchPolicy.None, new { type = "object", properties = new { reason = new { type = "string" } }, additionalProperties = false }, "通常单独调用更稳妥；可与 get_today_schedule 同轮共发补运行态，但不建议和 mutation 同轮并发。"),
            CreateTool("ignore_current_broadcast", "显式忽略当前收到的广播事件。只在广播事件里可用；调用后本 NPC 不再沿本次广播链继续回应或扩散。", NpcToolKind.ActionRequest, NpcToolDispatchPolicy.Immediate, new
            {
                type = "object",
                properties = new
                {
                    reason = new { type = "string" }
                },
                additionalProperties = false
            })
        };
    }

    private static JsonElement BuildToolSchema(object schema, bool supportsNpcBroadcast)
    {
        JsonNode schemaNode = JsonNode.Parse(JsonSerializer.Serialize(schema))
            ?? throw new InvalidOperationException("无法构建工具 schema。");
        if (supportsNpcBroadcast)
        {
            JsonObject root = schemaNode.AsObject();
            JsonObject properties = root["properties"]?.AsObject() ?? new JsonObject();
            properties["broadcast_to_nearby_npcs"] = new JsonObject
            {
                ["type"] = "boolean"
            };
            root["properties"] = properties;

            JsonArray required = root["required"] as JsonArray ?? new JsonArray();
            if (!required.Any(node => string.Equals(node?.GetValue<string>(), "broadcast_to_nearby_npcs", StringComparison.OrdinalIgnoreCase)))
            {
                required.Add("broadcast_to_nearby_npcs");
            }

            root["required"] = required;
        }

        return JsonSerializer.SerializeToElement(schemaNode);
    }

    private static object BuildStopSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                time = new { type = "integer" },
                time_mode = new { type = "string", @enum = new[] { "departure", "arrival" } },
                location_name = new { type = "string" },
                target_x = new { type = "integer" },
                target_y = new { type = "integer" },
                facing_direction = new { type = "integer" },
                end_behavior = new { type = "string" },
                end_message = new { type = "string" },
                route_mode = new { type = "string", @enum = new[] { "auto", "manual" } },
                route_tiles = new
                {
                    type = "array",
                    items = BuildTileSchema()
                }
            },
            required = new[] { "time", "location_name", "target_x", "target_y" }
        };
    }

    private static object BuildStopPatchSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                time = new { type = "integer" },
                time_mode = new { type = "string", @enum = new[] { "departure", "arrival" } },
                location_name = new { type = "string" },
                target_x = new { type = "integer" },
                target_y = new { type = "integer" },
                facing_direction = new { type = "integer" },
                end_behavior = new { type = "string" },
                end_message = new { type = "string" },
                clear_end_behavior = new { type = "boolean" },
                clear_end_message = new { type = "boolean" },
                route_mode = new { type = "string", @enum = new[] { "keep", "auto", "manual" } },
                route_tiles = new
                {
                    type = "array",
                    items = BuildTileSchema()
                }
            }
        };
    }

    private static object BuildStartPointSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                use_custom_start_point = new { type = "boolean" },
                location_name = new { type = "string" },
                tile_x = new { type = "integer" },
                tile_y = new { type = "integer" },
                facing_direction = new { type = "integer" }
            }
        };
    }

    private static object BuildTileSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                x = new { type = "integer" },
                y = new { type = "integer" }
            },
            required = new[] { "x", "y" }
        };
    }

    private static object BuildDirectiveSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                type = new { type = "string", @enum = new[] { "MoveToTile", "SpeakToPlayer", "DoEmote", "FacePlayer", "PlayEndBehavior", "PauseAndWait" } },
                target_location_name = new { type = "string" },
                target_x = new { type = "integer" },
                target_y = new { type = "integer" },
                facing_direction = new { type = "integer" },
                emote_name = BuildEmoteNameSchema(includePause: false),
                message = new { type = "string" },
                end_behavior = new { type = "string" },
                duration_milliseconds = new { type = "integer" },
                reason = new { type = "string" }
            },
            required = new[] { "type" }
        };
    }

    private static object BuildRouteAnimationNameSchema()
    {
        IReadOnlyList<string> names = NpcRouteAnimationCatalog.GetControlledNames();
        return new
        {
            type = "string",
            @enum = names.ToArray()
        };
    }

    private static object BuildEmoteNameSchema(bool includePause)
    {
        IReadOnlyList<string> values = includePause
            ? NpcEmoteCatalog.ControlledNamesWithPause
            : NpcEmoteCatalog.ControlledNames;
        return new { type = "string", @enum = values.ToArray() };
    }

    private static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value);
    }
}

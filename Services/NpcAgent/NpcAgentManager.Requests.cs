using System.Diagnostics;
using System.Text.Json;
using StardewMod.Models;
using StardewValley;
using StardewModdingAPI;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private const int MaxPendingEventsPerNpc = 16;

    private static bool IsNonInterruptingSystemEventType(string? eventType)
    {
        return string.Equals(eventType, "day_idle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventType, "day_started", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasQueuedOrActiveLlmWork(NpcAgentRuntimeState state)
    {
        return state.ActiveRequest is not null ||
            state.Queues.HasQueuedWork ||
            state.WaitingForPlayerResponse;
    }

    private void EnqueueEvent(string npcName, NpcAgentEvent agentEvent, bool interruptInflight)
    {
        NpcAgentRuntimeState state = this.GetOrCreateState(npcName);
        bool isNonInterruptingSystemEvent = IsNonInterruptingSystemEventType(agentEvent.EventType);
        bool effectiveInterruptInflight = (interruptInflight || IsForegroundEventType(agentEvent.EventType)) && !isNonInterruptingSystemEvent;
        NpcAgentEvent? replayEvent = null;

        if (isNonInterruptingSystemEvent &&
            state.Queues.AnyPendingEvent(existingEvent => string.Equals(existingEvent.EventType, agentEvent.EventType, StringComparison.OrdinalIgnoreCase)))
        {
            this.logger.Debug("Event", $"后台系统事件 {agentEvent.EventType} 已在队列中，跳过重复入队。", npcName);
            return;
        }

        if (IsAmbientObservationEventType(agentEvent.EventType))
        {
            int removedCount = state.Queues.RemovePendingEvents(existingEvent =>
                string.Equals(existingEvent.EventType, agentEvent.EventType, StringComparison.OrdinalIgnoreCase));
            if (removedCount > 0)
            {
                this.logger.Debug("Event", $"事件 {agentEvent.EventType} 替换了 {removedCount} 个旧的后台观察事件。", npcName);
            }
        }

        if (string.Equals(agentEvent.EventType, "player_prompt", StringComparison.OrdinalIgnoreCase))
        {
            int removedCount = state.Queues.RemovePendingEvents(existingEvent =>
                string.Equals(existingEvent.EventType, "player_prompt", StringComparison.OrdinalIgnoreCase));
            if (removedCount > 0)
            {
                this.logger.Info("Event", $"新玩家输入替换了 {removedCount} 个旧的待处理玩家输入。", npcName);
            }
        }

        if (effectiveInterruptInflight &&
            this.HasInflightRequest(state) &&
            !this.IsInflightCancellationPending(state))
        {
            replayEvent = this.PrepareInterruptedRequestReplay(npcName, state, agentEvent.EventType);
            if (this.TryCancelActiveRequest(state, NpcRequestCancellationReason.ReplacedByHigherPriorityEvent, agentEvent.EventType))
            {
                this.logger.Warn("Event", $"新事件 {agentEvent.EventType} 中断旧请求。", npcName);
            }
        }

        if (replayEvent is not null)
        {
            state.Queues.EnqueuePendingEvent(replayEvent, prepend: true);
            this.logger.Info("Event", $"被 {agentEvent.EventType} 打断的 {replayEvent.EventType} 已重新插回队头 pending={state.Queues.PendingEventCount}", npcName);
        }

        state.Queues.EnqueuePendingEvent(agentEvent, prepend: effectiveInterruptInflight);
        this.EnforcePendingEventLimit(npcName, state);
        this.logger.Info("Event", $"入队 event={agentEvent.EventType} pending={state.Queues.PendingEventCount}", npcName);
    }

    private static bool IsForegroundEventType(string? eventType)
    {
        return string.Equals(eventType, "player_prompt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventType, "gift_received", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPendingEventPriority(NpcAgentEvent agentEvent)
    {
        if (string.Equals(agentEvent.EventType, "player_prompt", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (string.Equals(agentEvent.EventType, "gift_received", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (IsNpcSyncEventType(agentEvent.EventType))
        {
            return 2;
        }

        if (IsDayIdleEventType(agentEvent.EventType))
        {
            return 1;
        }

        return IsAmbientObservationEventType(agentEvent.EventType) || IsBroadcastEventType(agentEvent.EventType)
            ? 0
            : 1;
    }

    private void EnforcePendingEventLimit(string npcName, NpcAgentRuntimeState state)
    {
        while (state.Queues.PendingEventCount > MaxPendingEventsPerNpc)
        {
            List<NpcAgentEvent> queuedEvents = state.Queues.EnumeratePendingEvents().ToList();
            if (queuedEvents.Count == 0)
            {
                return;
            }

            int lowestPriority = queuedEvents.Min(GetPendingEventPriority);
            if (!state.Queues.TryRemoveLastPendingEvent(
                    queuedEvent => GetPendingEventPriority(queuedEvent) == lowestPriority,
                    out NpcAgentEvent? droppedEvent) ||
                droppedEvent is null)
            {
                return;
            }

            state.DroppedPendingEventCount++;
            state.LastDroppedEventType = droppedEvent.EventType;
            state.PushDebugLine($"队列限流：丢弃 {droppedEvent.EventType}。");
            this.logger.Warn(
                "Event",
                $"待处理事件超过上限 {MaxPendingEventsPerNpc}，丢弃低优先级事件 event={droppedEvent.EventType} priority={lowestPriority} dropped_total={state.DroppedPendingEventCount}",
                npcName);
        }
    }

    private void StartNextRequest(string npcName, NpcAgentRuntimeState state, NpcAgentSettings settings)
    {
        if (state.PendingRuntimeReset is not null ||
            !state.Queues.TryPeekPendingEvent(out NpcAgentEvent? agentEvent) ||
            agentEvent is null)
        {
            return;
        }

        if (!this.TryBuildSnapshot(
            npcName,
            state,
            settings,
            agentEvent,
            out NpcPromptRefreshState? promptState,
            out EditableScheduleRule? rule,
            out NpcAgentRuntimeSummary? runtimeSummary))
        {
            return;
        }

        state.Queues.TryDequeuePendingEvent(out agentEvent);
        if (agentEvent is null)
        {
            return;
        }

        state.LastTrigger = agentEvent.EventType;
        state.IdleStatusOverride = string.Empty;
        string requestId = Guid.NewGuid().ToString("N");
        CancellationTokenSource cancellation = new();
        NpcActiveRequestRuntime activeRequest = new()
        {
            RequestId = requestId,
            TriggerEvent = CloneAgentEvent(agentEvent),
            SyncPairKey = agentEvent.SyncPairKey,
            OtherNpcName = agentEvent.OtherNpcName,
            Cancellation = cancellation
        };
        state.ActiveRequest = activeRequest;
        state.PushDebugLine($"开始请求：{agentEvent.EventType}");
        this.logger.Info("Agent", $"开始请求 trigger={agentEvent.EventType} provider={settings.ProviderName}", npcName);

        activeRequest.Task = this.RunAgentRequestAsync(
            promptState,
            rule,
            runtimeSummary,
            agentEvent,
            settings,
            requestId,
            feedbackEvent => this.PublishImmediateFeedbackEvent(state, feedbackEvent),
            cancellation.Token);
    }

    private async Task<AgentRequestResult> RunAgentRequestAsync(
        NpcPromptRefreshState promptState,
        EditableScheduleRule rule,
        NpcAgentRuntimeSummary runtimeSummary,
        NpcAgentEvent agentEvent,
        NpcAgentSettings settings,
        string requestId,
        Func<NpcImmediateFeedbackEvent, bool> publishImmediateFeedback,
        CancellationToken cancellationToken)
    {
        NpcAgentPromptSnapshot snapshot = promptState.Snapshot;
        Dictionary<string, string> basicProfile = promptState.BasicProfile;
        NpcPersonalityProfile personalityProfile = promptState.PersonalityProfile;
        bool isDayIdle = this.IsDayIdleEvent(agentEvent);
        bool isNpcSync = this.IsNpcSyncEvent(agentEvent);
        bool isAmbientObservation = this.IsAmbientObservationEvent(agentEvent);
        NpcToolAccessProfile toolAccessProfile = this.GetToolAccessProfile(agentEvent);
        Stopwatch stopwatch = Stopwatch.StartNew();
        this.logger.Debug("Agent", $"开始构造上下文 trigger={agentEvent.EventType} schedule_rule={rule.RuleKey}", snapshot.NpcName);
        List<MemoryRecord> automaticMemories = new();
        List<MemoryRecord> todayEvents = new();
        if (isDayIdle)
        {
            todayEvents = this.memoryStore.GetMemoriesForGameDate(snapshot.NpcName, snapshot.GameDate, maxCount: 60);
            this.logger.Info("Memory", $"day_idle 今日事件视图 count={todayEvents.Count}", snapshot.NpcName);
        }
        else if (!isAmbientObservation)
        {
            automaticMemories = await this.memoryStore.SearchMemoriesAsync(
                snapshot.NpcName,
                string.Join(' ', new[]
                {
                    agentEvent.PlayerAction,
                    agentEvent.DialogueExcerpt,
                    agentEvent.GiftItem,
                    agentEvent.CurrentScheduleSummary,
                    agentEvent.OtherNpcName,
                    agentEvent.OtherNpcMessage
                }),
                5,
                null,
                cancellationToken);
            this.logger.Info("Memory", $"自动检索记忆命中 {automaticMemories.Count} 条。", snapshot.NpcName);
        }
        else
        {
            this.logger.Debug("Memory", $"触发 {agentEvent.EventType}，跳过自动语义记忆检索。", snapshot.NpcName);
        }

        List<MemoryFactRecord> activeFacts = this.factStore.GetActiveFacts(snapshot.NpcName, snapshot.GameDate);

        NpcToolExecutionContext context = new()
        {
            RequestId = requestId,
            Snapshot = snapshot,
            NpcName = snapshot.NpcName,
            BasicProfile = basicProfile,
            PersonalityProfile = personalityProfile,
            WorkingRule = rule.Clone(),
            TriggerEvent = agentEvent,
            RuntimeSummary = runtimeSummary,
            MemoryStore = this.memoryStore,
            ActiveFacts = activeFacts,
            ToolAccessProfile = toolAccessProfile,
            BroadcastMaxHops = Math.Max(1, this.configService.Current.Broadcast.MaxHops),
            AllowBehaviorControl = !isDayIdle && !isAmbientObservation && settings.AllowBehaviorControl,
            AllowSpeech = !isDayIdle && !isAmbientObservation && settings.AllowSpeech,
            AllowNpcSpeech = settings.AllowSpeech && IsNpcSpeechAllowedForEventType(agentEvent.EventType),
            AllowScheduleControl = !isDayIdle &&
                settings.AllowScheduleControl &&
                IsScheduleControlAllowedForEventType(agentEvent.EventType),
            PublishImmediateFeedback = publishImmediateFeedback,
            ValidateNpcSpeechTarget = targetNpcName => this.ValidateNpcSyncSpeechTarget(snapshot.NpcName, targetNpcName)
        };
        context.RefreshSamplingContext = promptRound =>
        {
            if (this.TryBuildPromptSnapshot(snapshot.NpcName, context.WorkingRule, promptRound, agentEvent.OtherNpcName, out NpcPromptRefreshState refreshState))
            {
                return refreshState;
            }

            return new NpcPromptRefreshState
            {
                Snapshot = context.Snapshot,
                RuntimeSummary = context.RuntimeSummary,
                BasicProfile = context.BasicProfile,
                PersonalityProfile = context.PersonalityProfile
            };
        };
        context.RecordMemoryHits(automaticMemories.Select(memory => memory.Id));
        context.RecordMemoryHits(todayEvents.Select(memory => memory.Id));

        string userPrompt = this.BuildUserPrompt(agentEvent, automaticMemories, todayEvents, activeFacts);
        AiToolLoopResult loopResult = await this.router.RunToolLoopAsync(
            settings.ProviderName,
            async (promptRound, _) =>
            {
                context.RefreshLiveSampling(promptRound);
                context.ActiveFacts = this.factStore.GetActiveFacts(context.NpcName, context.Snapshot.GameDate);
                string roundSystemPrompt = this.BuildSystemPrompt(
                    agentEvent,
                    context.Snapshot,
                    context.BasicProfile,
                    context.PersonalityProfile,
                    context.ActiveFacts,
                    automaticMemories,
                    todayEvents,
                    context.RuntimeSummary);
                this.logger.Debug("Prompt", $"round={promptRound} system_chars={roundSystemPrompt.Length} user_chars={userPrompt.Length}", snapshot.NpcName);
                return await Task.FromResult(roundSystemPrompt);
            },
            userPrompt,
            this.toolService.GetToolDefinitions(toolAccessProfile),
            (invocation, token) => this.toolService.ExecuteAsync(context, invocation, token),
            cancellationToken);

        stopwatch.Stop();
        AgentRequestResult result = new()
        {
            NpcName = snapshot.NpcName,
            Trigger = agentEvent.EventType,
            ResponseSummary = loopResult.LastAssistantText,
            ToolCalls = context.ToolCalls.ToList(),
            MemoryHits = context.MemoryHits.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            IgnoredBroadcastCorrelationIds = context.IgnoredBroadcastCorrelationIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequestedSpeech = context.RequestedSpeech,
            RequestedNpcSpeech = context.RequestedNpcSpeech,
            ImmediateFeedbackCount = context.ImmediateFeedbackCount,
            OtherNpcName = agentEvent.OtherNpcName,
            SyncPairKey = agentEvent.SyncPairKey,
            Duration = stopwatch.Elapsed
        };

        if (!isDayIdle && !isNpcSync && context.ScheduleTouched)
        {
            result.Patch = new RuntimeSchedulePatch
            {
                RevisionId = Guid.NewGuid().ToString("N"),
                Reason = string.IsNullOrWhiteSpace(context.PatchReason) ? agentEvent.EventType : context.PatchReason,
                ApplyFromTime = context.ApplyFromTime ?? agentEvent.TimeOfDay,
                Rule = context.WorkingRule.Clone(),
                ExpiresAtWindowEnd = true,
                DiffSummary = this.scheduleEditorService.BuildRuleSummary(context.WorkingRule)
            };
        }

        result.DeferredActionRequests.AddRange(context.DeferredActionRequests);
        this.logger.Info(
            "Agent",
            $"请求完成 trigger={agentEvent.EventType} tool_calls={result.ToolCalls.Count} patch={(result.Patch is null ? "no" : "yes")} deferred_actions={result.DeferredActionRequests.Count} immediate_feedback={result.ImmediateFeedbackCount}",
            snapshot.NpcName);
        return result;
    }

    private void ProcessCompletedRequestIfNeeded(string npcName, NpcAgentRuntimeState state)
    {
        if (state.ActiveRequest?.Task is not Task<AgentRequestResult> task || !task.IsCompleted)
        {
            return;
        }

        NpcActiveRequestRuntime activeRequest = state.ActiveRequest;
        this.FlushImmediateFeedbackForCompletedRequest(npcName, state, activeRequest);
        try
        {
            AgentRequestResult result = task.GetAwaiter().GetResult();
            state.LastRequestFinishedAt = DateTimeOffset.UtcNow;
            state.LastRequestDuration = $"{result.Duration.TotalMilliseconds:0} ms";
            this.logger.Info("Agent", $"请求落地 trigger={result.Trigger} duration={state.LastRequestDuration}", npcName);
            foreach (string toolCall in result.ToolCalls)
            {
                state.PushToolCall(toolCall);
            }

            if (IsDayIdleEventType(result.Trigger))
            {
                this.MarkDayIdleCompletedIfNeeded(npcName);
            }

            foreach (string correlationId in result.IgnoredBroadcastCorrelationIds)
            {
                this.RegisterIgnoredBroadcastCorrelation(npcName, correlationId);
            }

            this.OnSyncRequestCompleted(result);

            if (result.ToolCalls.Count > 0)
            {
                this.logger.Info("Tool", $"本轮工具调用摘要 count={result.ToolCalls.Count}", npcName);
                for (int i = 0; i < result.ToolCalls.Count; i++)
                {
                    this.logger.Info("Tool", $"  {i + 1}. {result.ToolCalls[i]}", npcName);
                }
            }

            if (result.Patch is not null)
            {
                NPC? npc = Game1.getCharacterFromName(npcName);
                if (npc is null)
                {
                    state.LastRejectionReason = "目标 NPC 未加载，无法应用 patch。";
                    result.RejectionReason = state.LastRejectionReason;
                    this.logger.Warn("Patch", state.LastRejectionReason, npcName);
                }
                else
                {
                    this.LogRuleSummary(
                        "Patch",
                        $"LLM 计划应用 revision={result.Patch.RevisionId} apply_from={result.Patch.ApplyFromTime} reason={this.logger.Summarize(result.Patch.Reason, 160)}",
                        npcName,
                        result.Patch.Rule);
                    if (this.scheduleEditorService.TryApplyLiveRule(npc, result.Patch.Rule, preserveCurrentMovement: true))
                    {
                        state.ActivePatch = result.Patch;
                        state.LastPatchSummary = result.Patch.DiffSummary;
                        state.LastRejectionReason = string.Empty;
                        state.PushDebugLine($"应用 patch 成功：{result.Patch.RevisionId}");
                        this.logger.Info("Patch", $"应用成功 revision={result.Patch.RevisionId}", npcName);
                        this.LogRuleSummary("Patch", $"应用后的当前规则 revision={result.Patch.RevisionId}", npcName, result.Patch.Rule);
                    }
                    else
                    {
                        state.LastRejectionReason = "本地 schedule 编译或应用失败，已保留旧 patch。";
                        result.RejectionReason = state.LastRejectionReason;
                        state.PushDebugLine(state.LastRejectionReason);
                        this.logger.Warn("Patch", state.LastRejectionReason, npcName);
                    }
                }
            }

            foreach (NpcActionRequest actionRequest in result.DeferredActionRequests)
            {
                this.RouteCommittedActionRequest(npcName, state, actionRequest, fromImmediateFeedback: false);
            }

            if (string.Equals(result.Trigger, "player_prompt", StringComparison.OrdinalIgnoreCase) &&
                !result.RequestedSpeech &&
                state.Queues.PendingSpeechCount == 0 &&
                TryBuildFallbackSpeech(result.ResponseSummary, out string fallbackMessage))
            {
                this.RouteCommittedActionRequest(
                    npcName,
                    state,
                    new NpcActionRequest
                    {
                        Type = NpcActionRequestType.SpeakToPlayer,
                        DispatchMode = NpcActionDispatchMode.ImmediateFeedback,
                        Message = fallbackMessage,
                        SourceToolName = "assistant_text_fallback",
                        Reason = "模型返回了可显示的纯文本，但没有调用 npc_say_to_player。"
                    },
                    fromImmediateFeedback: false,
                    sourceToolName: "assistant_text_fallback");
                result.RequestedSpeech = true;
                state.PushDebugLine("模型未调用对白工具，已把纯文本结果转成游戏内对白。");
                this.logger.Info("Speech", "已将模型纯文本结果转成兜底对白。", npcName);
            }

            if (string.Equals(result.Trigger, "player_prompt", StringComparison.OrdinalIgnoreCase) &&
                !result.RequestedSpeech &&
                state.Queues.PendingSpeechCount == 0)
            {
                state.WaitingForPlayerResponse = false;
            }

            this.memoryStore.AppendDebugRecord(
                npcName,
                new NpcAgentDebugRecord
                {
                    NpcName = npcName,
                    Trigger = result.Trigger,
                    RequestSummary = $"耗时 {state.LastRequestDuration}",
                    ResponseSummary = result.ResponseSummary,
                    ToolCalls = result.ToolCalls,
                    PatchSummary = result.Patch?.DiffSummary ?? string.Empty,
                    RejectionReason = result.RejectionReason
                });
        }
        catch (OperationCanceledException)
        {
            state.PushDebugLine($"请求被取消：{activeRequest.BuildStatusText()}");
            this.logger.Warn("Agent", $"请求被取消 reason={activeRequest.BuildStatusText()}", npcName);
            this.OnSyncRequestCancelled(activeRequest);
            if (state.WaitingForPlayerResponse && state.Queues.PendingEventCount == 0 && state.Queues.PendingSpeechCount == 0)
            {
                state.WaitingForPlayerResponse = false;
            }
        }
        catch (Exception ex)
        {
            state.LastRejectionReason = ex.Message;
            state.PushDebugLine($"请求失败：{ex.Message}");
            this.logger.Error("Agent", $"请求失败：{ex.Message}", npcName);
            this.monitor.Log($"NPC Agent 请求失败：{npcName} -> {ex}", LogLevel.Warn);
            this.OnSyncRequestFailed(activeRequest);
            if (state.WaitingForPlayerResponse && state.Queues.PendingSpeechCount == 0)
            {
                state.WaitingForPlayerResponse = false;
            }
        }
        finally
        {
            this.FinalizeActiveRequest(state);
        }
    }

    private bool TryBuildSnapshot(
        string npcName,
        NpcAgentRuntimeState state,
        NpcAgentSettings settings,
        NpcAgentEvent agentEvent,
        out NpcPromptRefreshState promptState,
        out EditableScheduleRule rule,
        out NpcAgentRuntimeSummary runtimeSummary)
    {
        promptState = null!;
        rule = null!;
        runtimeSummary = null!;

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            return false;
        }

        rule = state.ActivePatch?.Rule?.Clone()
            ?? state.BaselineRule?.Clone()
            ?? this.scheduleEditorService.GetCurrentEditableRule(npcName);

        if (!this.TryBuildPromptSnapshot(npcName, rule, promptRound: 1, agentEvent.OtherNpcName, out promptState))
        {
            return false;
        }

        runtimeSummary = promptState.RuntimeSummary;
        return true;
    }

    private string BuildSystemPrompt(
        NpcAgentEvent agentEvent,
        NpcAgentPromptSnapshot snapshot,
        Dictionary<string, string> basicProfile,
        NpcPersonalityProfile personalityProfile,
        IReadOnlyList<MemoryFactRecord> activeFacts,
        IReadOnlyList<MemoryRecord> automaticMemories,
        IReadOnlyList<MemoryRecord> todayEvents,
        NpcAgentRuntimeSummary runtimeSummary)
    {
        List<string> basicProfileLines = basicProfile
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}: {pair.Value}")
            .ToList();
        string personalitySource = personalityProfile.Source.ToString().ToLowerInvariant();
        string personalityMarkdown = string.IsNullOrWhiteSpace(personalityProfile.RawMarkdown)
            ? string.Join(
                "\n",
                personalityProfile.Sections.Select(section => $"## {section.Title}\n{section.Content}".Trim()))
            : personalityProfile.RawMarkdown.Trim();
        List<string> factLines = activeFacts.Count == 0
            ? new List<string> { "无结构化事实。" }
            : activeFacts.Select(fact => $"- [{NpcMemoryFactScopes.Normalize(fact.Scope)}] {fact.Key}: {fact.Summary}").ToList();
        List<string> memoryLines = automaticMemories.Count == 0
            ? new List<string> { "无自动检索记忆命中。" }
            : automaticMemories.Select(memory => $"- [{memory.EventType}] {memory.Text}").ToList();
        List<string> todayEventLines = todayEvents.Count == 0
            ? new List<string> { "今天尚无可用事件记录。" }
            : todayEvents.Select(memory =>
            {
                string timeText = memory.Metadata.TryGetValue("time", out string? timeValue) && int.TryParse(timeValue, out int recordTime)
                    ? Game1.getTimeOfDayString(recordTime)
                    : memory.Timestamp.ToLocalTime().ToString("HH:mm:ss");
                return $"- [{timeText}] [{memory.EventType}] {memory.Text}";
            }).ToList();

        if (this.IsDayIdleEvent(agentEvent))
        {
            return string.Join(
                "\n",
                new[]
                {
                    "# Role: 你是 Stardew Valley 中的 NPC 行为代理。",
                    "本轮触发事件是 day_idle，这是系统维护任务，不是对玩家说话，也不是现场行为决策。",
                    "本轮唯一目标：根据今天已经发生的事件、对话、互动和现有 facts，把值得沉淀下来的结论整理为结构化事实。",
                    "人格档案是高权重输入，会影响你如何理解偏好、态度、关系和是否值得多查一轮；但它不能覆盖本轮的系统限制。",
                    "本轮严禁制造世界副作用：不要对白，不要动作，不要 schedule 修改。本地也会拒绝这些工具调用并记录。",
                    "你可以使用 memory_update 新增、覆盖或删除 facts；默认应尽量一次完成，不要把 day_idle 变成多轮信息探索。",
                    "不要做记忆压缩，不要删除 events.jsonl 原始事件；本轮只整理 facts。",
                    "事实整理规则：长期偏好、稳定关系、持续习惯 -> persistent；仅当天有效的情绪、状态、临时意图 -> today。",
                    "如果今天的事件明确纠正了旧认知，优先沿用原有 key 覆盖旧 fact，而不是再造一个相近 key。",
                    "除非今天事件视图确实缺关键上下文，否则不要继续跑记忆检索；默认以这份今日事件视图和 active facts 为准。",
                    $"NPC: {snapshot.DisplayName} ({snapshot.NpcName})",
                    $"当前采样轮次: 第 {snapshot.PromptRound} 轮",
                    $"游戏时间: {snapshot.GameDate} {snapshot.TimeText}",
                    $"人格来源: {personalitySource}",
                    "NPC 人格档案（高权重）：",
                    personalityMarkdown,
                    "基础资料：",
                    string.Join('\n', basicProfileLines),
                    "当前采样环境元数据：",
                    JsonSerializer.Serialize(snapshot.Metadata),
                    "当前 active facts：",
                    string.Join('\n', factLines),
                    "今日事件视图（按时间顺序）：",
                    string.Join('\n', todayEventLines)
                });
        }

        if (this.IsAmbientObservationEvent(agentEvent))
        {
            return string.Join(
                "\n",
                new[]
                {
                    "# Role: 你是 Stardew Valley 中的 NPC 行为代理。",
                    $"本轮触发事件是 {agentEvent.EventType}，这是后台观察轮，不是面对玩家的对话，不是 NPC-NPC 同步轮，也不是 schedule 编辑任务。",
                    "本轮目标只有两个：读取当前状态，或在确有必要时整理/修正 facts。",
                    "本轮严禁主动制造现场副作用：不要对白，不要调用 say_to_npc，不要动作，不要移动，不要 schedule 修改。",
                    "本地开放的工具只包含查询、facts 更新、schedule/runtime 只读查看；动作、对白和 schedule mutation 都不会开放。",
                    "当前 prompt 已经直接给出时间、地点、附近角色、working schedule 和运行时状态。默认不需要额外查记忆。",
                    "除非当前 prompt 明显缺关键上下文，否则不要继续查询；若必须查，也尽量控制在 1 次以内。",
                    "如果当前没有值得沉淀的新事实，直接结束，不要为了让自己看起来忙碌而空转。",
                    $"NPC: {snapshot.DisplayName} ({snapshot.NpcName})",
                    $"当前采样轮次: 第 {snapshot.PromptRound} 轮",
                    $"游戏时间: {snapshot.GameDate} {snapshot.TimeText}",
                    $"人格来源: {personalitySource}",
                    "NPC 人格档案（高权重）：",
                    personalityMarkdown,
                    "基础资料：",
                    string.Join('\n', basicProfileLines),
                    "当前可见环境元数据：",
                    JsonSerializer.Serialize(snapshot.Metadata),
                    "结构化事实记忆：",
                    string.Join('\n', factLines),
                    "当前 working schedule：",
                    snapshot.ScheduleSummary,
                    "当前 working schedule（结构化明细）：",
                    snapshot.ScheduleDetailJson,
                    "当前运行时状态：",
                    JsonSerializer.Serialize(runtimeSummary),
                    "自动检索记忆：",
                    string.Join('\n', memoryLines)
            });
        }

        if (IsBroadcastEventType(agentEvent.EventType) && agentEvent.BroadcastContext is not null)
        {
            return this.BuildBroadcastSystemPrompt(
                agentEvent,
                snapshot,
                basicProfileLines,
                personalitySource,
                personalityMarkdown,
                factLines,
                memoryLines,
                runtimeSummary);
        }

        if (this.IsNpcSyncEvent(agentEvent))
        {
            return this.BuildNpcSyncSystemPrompt(
                agentEvent,
                snapshot,
                basicProfileLines,
                personalitySource,
                personalityMarkdown,
                factLines,
                memoryLines,
                runtimeSummary);
        }

        List<string> nearbyNpcLines = snapshot.Metadata.NearbyNpcs.Count == 0
            ? new List<string> { "- 当前感知半径内没有其它可见 NPC。" }
            : snapshot.Metadata.NearbyNpcs.Select(nearbyNpc =>
                $"- {nearbyNpc.DisplayName}（{nearbyNpc.NpcName}） distance={nearbyNpc.DistanceTiles} tile=({nearbyNpc.TileX},{nearbyNpc.TileY}) facing={nearbyNpc.FacingDirection} sync_available={nearbyNpc.CanReceiveSyncSpeechNow} mentioned_candidate={nearbyNpc.IsMentionedCandidate}").ToList();

        return string.Join(
            "\n",
            new[]
            {
                "# Role: 你是 Stardew Valley 中的 NPC 行为代理。",
                "你不能直接生成底层 route 点栈，只能通过工具读取和修改规范化 schedule 与即时动作。",
                "未来行程走 schedule patch，短时反馈与动作走动作请求。",
                "每一轮 tool loop 开始前，系统都会重新采样当前游戏状态、NPC 状态、天气、节日、可见农夫信息、working schedule 与运行时状态。",
                "如果新采样结果与你上一轮推断、旧记忆或 baseline 习惯冲突，一律以当前轮采样结果为准。",
                "当前 working schedule 摘要与结构化明细，是你在本次请求里继续决策和继续修改的唯一行程基准。",
                "如果你刚刚已经调用过 schedule 修改工具，下面的 working schedule 已经反映这些最新变更；不要再按旧摘要继续推。",
                "你已经直接拿到了当前完整时间表；简单任务不要为了补齐上下文而习惯性多查工具。",
                "只有 player_prompt 这类玩家明确当场提出的请求，才会开放完整的 schedule mutation 权限；其它事件不要把自己误判成 schedule 任务。",
                "如果玩家刚刚明确清空、缩短或改写今天行程，后续 periodic_tick 不要擅自恢复旧日程，除非玩家再次要求或工具结果明确支持。",
                "runtime state 是游戏此刻真实运行态的重新采样，包含 NPC 当前是否在走 schedule、当前/下一站、当前表情、对话栈、controller 状态与安全改写时间。",
                "除非玩家明确要求立即打断，否则不要轻易删除、覆盖或改写当前正在执行的 schedule 段；优先从 runtime state 给出的 SafeMutationTime 之后修改。",
                "get_today_schedule 会返回当前 working schedule 的 source=patch|normal、每个 stop 的绝对 index，以及当前安全改写边界。",
                "所有 schedule 修改工具都基于这个当前 working schedule，不需要你自己把 normal 和 patch 再手动合并。",
                "默认优先使用最小改动工具：update_schedule_stops、insert_schedule_stops、remove_schedule_stops。",
                "update_schedule_stops 可一次修改一个或多个既有 stop；changes 里只填写要改的字段，未提供的字段保持原值。",
                "如果只是同一目的地下微调时间、切换 departure/arrival、改朝向、改结束行为或结束对话，优先用 update_schedule_stops，不要整段 replace。",
                "insert_schedule_stops 用于插入一个或多个新 stop；后续未删除站点会保留，不需要把后面的整段 schedule 重写出来。",
                "remove_schedule_stops 只删除指定 index，对其余后续站点保持保留。",
                "replace_future_schedule 只在你明确要丢弃 apply_from_time 之后的旧 future tail，并从那里整体重排后续所有站点时使用。",
                "replace_entire_schedule 只在玩家明确要求整天完全重写时使用；否则不要清空整天。",
                "如果只是改终点或地图，优先只提供 location_name + target_x + target_y，不要手写 route_tiles；本地会自动切回自动推路，并用新的上一站终点重算这一段以及后续段的实际起点。",
                "只有你明确想控制手工采样路径时，才提供 route_tiles。",
                "本地自动调整规则如下：",
                "1. 所有 stop 时间都会限制在 06:00 到次日 02:00。",
                "2. 全部 stop 会按时间重新排序。",
                "3. 相邻 stop 至少间隔 10 分钟；不足会被自动顺延。",
                "4. 每一段真实起点自动取上一站终点，第一段自动取日初出生点。",
                "5. 如果 stop 使用 arrival 语义，编译时会按实际路径长度换算成真实出发时间。",
                "6. 如果你只改终点而不提供 route_tiles，本地会优先让算法自动推路。",
                "除非玩家明确要求中断，否则不要改 SafeMutationTime 之前的站点；如果工具返回 guard_message，说明本地已经替你把操作改到了更安全的位置。",
                "如果触发事件是 player_prompt，且你要以对话框的形式回复玩家，必须调用 npc_say_to_player。",
                "你在实时游戏里工作，必须权衡延迟与完整性。一次额外的工具调用，再加上一轮新的模型思考，通常就可能额外消耗大约 20 到 30 分钟游戏内时间。",
                "因此，先判断这次任务是否真的缺关键信息：如果当前 prompt 里已经给了足够的时间表、运行时状态和相关记忆，就直接决策，不要为“更完整”而继续查询。",
                "简单任务的默认策略：能 0 次额外查询就不要查；必须查时尽量控制在 1 次；只有在缺少关键字段、索引或冲突信息时才继续下一轮。",
                "一轮 assistant 回复可以同时发出多个工具调用；如果这些调用互不依赖彼此结果，优先同轮发出，不要人为拆成多轮。",
                "工具说明里带有 parallel_call_description，会告诉你这个工具是否适合同轮组合、何时适合，以及常见例子。",
                "最常见的高效模式有三类：",
                "1. 长任务先给过渡反馈：如果玩家在眼前且你还需要查记忆，可以同轮先调用 npc_say_to_player 说一句“让我想想…”之类的短反馈，再同时发 1 到 2 个独立记忆查询。",
                "2. 独立记忆并查：如果你要分别查“最近互动”和“语义相关旧记忆”，可以同轮同时发 get_recent_memories 与 search_memories，或发两个不同 query 的 search_memories。",
                "3. 轻量动作陪衬：如果只是为了表现思考/惊讶，可以同轮发一个轻量动作请求，再发独立查询；但动作本身不要依赖查询结果。",
                "不要为了凑多工具而多工具；只有在多个调用彼此独立、能减少下一轮等待时，才值得同轮组合。",
                "每次调用工具都必须带一条简短的 reason，明确说明你为什么现在必须调用它；如果说不出 reason，通常说明这次调用不值得。",
                "尤其是记忆查询：只有当当前任务确实依赖玩家长期偏好、当天状态、纠正历史或人物关系时才调用。纯时间表修改、简单确认、直接动作，不要先习惯性查记忆。",
                "如果 Farmer.IsVisibleToNpc=false，说明农夫要么不在同一张地图，要么虽然同图但超出了感知半径；此时不要编造农夫的朝向、手持物、体力状态或当前工具，也不要调用 npc_say_to_player。",
                "Metadata.NearbyNpcs 会直接列出你当前感知半径内看得到的其它 NPC，以及他们现在能否接收 say_to_npc。",
                "say_to_npc 只会在 player_prompt 或 npc_sync 这类明确需要 NPC-NPC 对话的事件里开放；如果当前事件不是这两类，就不要尝试。",
                "如果玩家明确提到另一名 NPC，或直接要求你“调用 say_to_npc 给某某”，优先先看 NearbyNpcs：",
                "1. 如果目标 NPC 在 NearbyNpcs 里且 sync_available=true ， 并且你要和那位NPC进行对话，默认优先直接调用 say_to_npc，不要先去查无关记忆，更不要擅自改自己的 schedule。",
                "2. 如果目标 NPC 不在 NearbyNpcs 里，说明对方此刻不在你身边或不在感知范围内，不要假装你已经和对方说上话。",
                "玩家让你对旁边 NPC 开口时，`say_to_npc` 才是首选工具；不要把这种请求误判成 schedule 任务、找人任务或自我移动任务，除非玩家明确要求你走过去。",
                "动作类 tool 只负责提交事件请求，本地会自动分流时序；你不需要手工模拟队列调度。",
                "默认分流规则是：对白、表情、朝向玩家、短暂停顿优先走即时反馈；移动和 end behavior 默认延迟到本轮请求结束后再提交。",
                "如果要做表情，不要传裸 emote_id；单个表情用 enqueue_immediate_action + emote_name，连续表情优先用 enqueue_emote_sequence。",
                "如果要播放原版路由动画或精灵动作，优先用 enqueue_route_animation，并且只能传受控 animation_name，不要拼接未知 key。",
                "如果你调用 npc_say_to_player 是为了最终答案，而且没有别的必要动作，请立刻结束本轮，不要继续无关工具查询，否则会拖慢对白真正弹出。",
                "如果这句 npc_say_to_player 只是一个与查询结果无关的过渡反馈，那么它可以和独立查询同轮共发；等下一轮拿到结果后，再决定是否给最终答案。",
                "纯 assistant 文本不会自动显示给玩家；只有 npc_say_to_player 才会弹出游戏内对话框。",
                "你有一层结构化事实记忆：persistent 表示长期事实，today 表示仅当天有效；如果两者冲突，today 优先。",
                "当玩家表达长期偏好、今天临时状态、纠正旧认知或要求你记住某件事时，应优先调用 memory_update。",
                "同一条事实被修正时，继续使用同一个 key 覆盖；只在当天生效的信息用 scope=today。",
                "如果不需要改动，可以只查询信息并结束，不要捏造工具结果。",
                "NPC 人格档案是高权重输入，可以明显影响你的说话方式、做事方式、配合度、是否值得继续多跑工具轮次，以及是否选择简短拒绝。",
                "但人格不能绕过系统规则、工具权限、本地安全约束，也不能让你在该回复时不回复。",
                $"NPC: {snapshot.DisplayName} ({snapshot.NpcName})",
                $"当前采样轮次: 第 {snapshot.PromptRound} 轮",
                $"游戏时间: {snapshot.GameDate} {snapshot.TimeText}",
                $"人格来源: {personalitySource}",
                "NPC 人格档案（高权重）：",
                personalityMarkdown,
                "基础资料：",
                string.Join('\n', basicProfileLines),
                "附近可见 NPC 简表：",
                string.Join('\n', nearbyNpcLines),
                "当前可见环境元数据：",
                JsonSerializer.Serialize(snapshot.Metadata),
                "结构化事实记忆：",
                string.Join('\n', factLines),
                "当前 working schedule：",
                snapshot.ScheduleSummary,
                "当前 working schedule（结构化明细）：",
                snapshot.ScheduleDetailJson,
                "当前运行时状态：",
                JsonSerializer.Serialize(runtimeSummary),
                "自动检索记忆：",
                string.Join('\n', memoryLines)
            });
    }

    private string BuildUserPrompt(
        NpcAgentEvent agentEvent,
        IReadOnlyList<MemoryRecord> memories,
        IReadOnlyList<MemoryRecord> todayEvents,
        IReadOnlyList<MemoryFactRecord> activeFacts)
    {
        if (this.IsDayIdleEvent(agentEvent))
        {
            List<string> eventLines = todayEvents.Count == 0
                ? new List<string> { "- 今天尚无事件记录。" }
                : todayEvents.Select(memory =>
                {
                    string timeText = memory.Metadata.TryGetValue("time", out string? timeValue) && int.TryParse(timeValue, out int recordTime)
                        ? Game1.getTimeOfDayString(recordTime)
                        : memory.Timestamp.ToLocalTime().ToString("HH:mm:ss");
                    return $"- [{timeText}] [{memory.EventType}] {memory.Text}";
                }).ToList();
            List<string> factLines = activeFacts.Count == 0
                ? new List<string> { "- 当前没有 active facts。" }
                : activeFacts.Select(fact => $"- [{NpcMemoryFactScopes.Normalize(fact.Scope)}] {fact.Key}: {fact.Summary}").ToList();

            return string.Join(
                "\n",
                new[]
                {
                    "触发事件：day_idle",
                    $"时间：{agentEvent.GameDate} {Game1.getTimeOfDayString(agentEvent.TimeOfDay)}",
                    "这是系统维护任务。请只整理 facts，不要对白、动作或 schedule 修改。",
                    $"当前 active facts 数量：{activeFacts.Count}",
                    "当前 active facts：",
                    string.Join('\n', factLines),
                    $"今日事件数量：{todayEvents.Count}",
                    "今日事件视图：",
                    string.Join('\n', eventLines),
                    "默认应在一轮内完成。只有在今日事件确实缺关键上下文时，才考虑额外查询。"
                });
        }

        if (this.IsAmbientObservationEvent(agentEvent))
        {
            return string.Join(
                "\n",
                new[]
                {
                    $"触发事件：{agentEvent.EventType}",
                    $"时间：{agentEvent.GameDate} {Game1.getTimeOfDayString(agentEvent.TimeOfDay)}",
                    $"地点：{agentEvent.LocationName}",
                    "这是后台观察轮。不要对白、不要 say_to_npc、不要动作、不要 schedule 修改。",
                    "默认直接根据当前 prompt 判断是否需要整理 facts；若没有必要更新，就直接结束。"
            });
        }

        if (IsBroadcastEventType(agentEvent.EventType) && agentEvent.BroadcastContext is not null)
        {
            return this.BuildBroadcastUserPrompt(agentEvent);
        }

        if (this.IsNpcSyncEvent(agentEvent))
        {
            return this.BuildNpcSyncUserPrompt(agentEvent);
        }

        return string.Join(
            "\n",
            new[]
            {
                $"触发事件：{agentEvent.EventType}",
                $"时间：{agentEvent.GameDate} {Game1.getTimeOfDayString(agentEvent.TimeOfDay)}",
                $"地点：{agentEvent.LocationName}",
                $"玩家动作：{agentEvent.PlayerAction}",
                $"对话摘录：{agentEvent.DialogueExcerpt}",
                $"礼物：{agentEvent.GiftItem}",
                $"当前 schedule 摘要：{agentEvent.CurrentScheduleSummary}",
                $"已自动附带 {memories.Count} 条相关记忆，可继续用工具补充查询。",
                "系统会在每一轮 tool loop 前重采样时间、天气、节日、地图与可见角色信息；优先使用本轮采样直接决策。",
                "注意：每增加一次工具查询并进入新一轮 tool loop，通常就意味着额外的明显延迟。"
            });
    }

    private NpcAgentEvent BuildEvent(
        string npcName,
        string eventType,
        string playerAction,
        string dialogueExcerpt,
        string giftItem,
        int friendshipDelta,
        NpcAgentSettings settings)
    {
        EditableScheduleRule? rule = this.states.TryGetValue(npcName, out NpcAgentRuntimeState? state)
            ? state.ActivePatch?.Rule?.Clone() ?? state.BaselineRule?.Clone()
            : null;
        rule ??= this.TryGetCurrentRule(npcName);
        NPC? npc = Context.IsWorldReady ? Game1.getCharacterFromName(npcName) : null;

        return new NpcAgentEvent
        {
            EventType = eventType,
            NpcName = npcName,
            GameDate = this.BuildGameDateString(),
            TimeOfDay = Game1.timeOfDay,
            LocationName = npc?.currentLocation?.NameOrUniqueName ?? Game1.currentLocation?.NameOrUniqueName ?? string.Empty,
            PlayerAction = playerAction,
            DialogueExcerpt = dialogueExcerpt,
            GiftItem = giftItem,
            FriendshipDelta = friendshipDelta,
            CurrentScheduleSummary = rule is null ? string.Empty : this.scheduleEditorService.BuildRuleSummary(rule),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider"] = settings.ProviderName
            }
        };
    }

    private string BuildGameDateString()
    {
        return $"Year {Game1.year} {Game1.currentSeason} {Game1.dayOfMonth}";
    }

    private static bool TryBuildFallbackSpeech(string assistantText, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return false;
        }

        string normalized = assistantText.Replace("\r", string.Empty).Trim();
        if (normalized.StartsWith("```", StringComparison.Ordinal) ||
            normalized.StartsWith("{", StringComparison.Ordinal) ||
            normalized.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        string[] lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            return false;
        }

        message = string.Join("\n", lines).Trim().Trim('"', '\'', '“', '”', '‘', '’');
        return !string.IsNullOrWhiteSpace(message);
    }
}

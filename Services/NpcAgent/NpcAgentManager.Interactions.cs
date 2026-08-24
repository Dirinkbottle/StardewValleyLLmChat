using StardewMod.Models;
using StardewMod.Ui;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    public bool TrySubmitConsolePrompt(string npcName, string text, out string error)
    {
        error = string.Empty;
        if (!Context.IsWorldReady)
        {
            error = "当前不在存档内。";
            return false;
        }

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            error = $"未找到 NPC：{npcName}";
            return false;
        }

        this.SubmitPlayerPrompt(npc, text);
        return true;
    }

    public bool TryInterceptConversation(NPC npc, Farmer who, GameLocation location, out IClickableMenu? promptMenu)
    {
        promptMenu = null;
        if (!Context.IsWorldReady ||
            npc is null ||
            who is null ||
            !who.IsLocalPlayer ||
            who.ActiveObject is not null ||
            Game1.eventUp ||
            Game1.currentLocation?.currentEvent is not null)
        {
            return false;
        }

        NpcAgentSettings settings = this.GetSettings(npc.Name);
        if (!settings.Enabled ||
            !settings.AllowSpeech ||
            !this.IsProviderUsable(settings) ||
            !this.IsWithinActiveWindow(settings))
        {
            return false;
        }

        this.logger.Info("Interact", $"拦截右键交互，打开自定义输入框 location={location.NameOrUniqueName}", npc.Name);
        promptMenu = new NpcChatPromptMenu(this, npc);
        return true;
    }

    public void SubmitPlayerPrompt(NPC npc, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        NpcAgentSettings settings = this.GetSettings(npc.Name);
        NpcAgentRuntimeState state = this.GetOrCreateState(npc.Name);
        state.Queues.ClearSpeech();
        state.WaitingForPlayerResponse = true;
        state.PausePeriodicUntilConversationSettles = true;
        state.AwaitingConversationDialogueClose = false;
        state.LastPeriodicTriggeredAt = DateTimeOffset.UtcNow;
        state.PushDebugLine($"收到玩家输入：{text}");
        this.logger.Info("Interact", $"收到玩家输入：{this.logger.Summarize(text, 140)}", npc.Name);

        Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["location"] = Game1.currentLocation?.NameOrUniqueName ?? string.Empty,
            ["time"] = Game1.timeOfDay.ToString()
        };
        MemoryRecord record = this.memoryStore.AppendEventRecord(npc.Name, "player_prompt", text, metadata);
        _ = this.memoryStore.TryEmbedRecordAsync(npc.Name, record, CancellationToken.None);

        this.EnqueueEvent(
            npc.Name,
            this.BuildEvent(npc.Name, "player_prompt", text, text, string.Empty, 0, settings),
            interruptInflight: true);
    }

    public void NotifyGiftReceived(NPC npc, Farmer giver, SObject? gift)
    {
        if (!Context.IsWorldReady || npc is null || giver is null)
        {
            return;
        }

        NpcAgentSettings settings = this.GetSettings(npc.Name);
        if (!settings.Enabled || !this.IsWithinActiveWindow(settings))
        {
            return;
        }

        string giftName = gift?.DisplayName ?? string.Empty;
        this.logger.Info("Interact", $"收到礼物 giver={giver.Name} gift={giftName}", npc.Name);
        MemoryRecord record = this.memoryStore.AppendEventRecord(
            npc.Name,
            "gift_received",
            $"{giver.Name} 给了 {npc.displayName} 礼物：{giftName}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gift"] = giftName
            });
        _ = this.memoryStore.TryEmbedRecordAsync(npc.Name, record, CancellationToken.None);

        this.EnqueueEvent(
            npc.Name,
            this.BuildEvent(npc.Name, "gift_received", "玩家送礼", string.Empty, giftName, 0, settings),
            interruptInflight: true);
        this.QueueGiftReceivedBroadcast(npc, giver, gift);
    }

    private void ShowPendingSpeechIfPossible(string npcName, NpcAgentRuntimeState state)
    {
        if (state.Queues.PendingSpeechCount == 0)
        {
            return;
        }

        if (Game1.activeClickableMenu is NpcChatPromptMenu)
        {
            Game1.exitActiveMenu();
        }

        if (Game1.activeClickableMenu is not null)
        {
            return;
        }

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            state.Queues.ClearSpeech();
            state.WaitingForPlayerResponse = false;
            return;
        }

        if (!this.CanSpeakToFarmerNow(npc, out string rejectionReason))
        {
            int droppedCount = state.Queues.ClearSpeech();
            state.WaitingForPlayerResponse = false;
            state.AwaitingConversationDialogueClose = false;
            state.PushDebugLine($"取消对白：{rejectionReason}");
            this.logger.Warn("Speech", $"取消排队对白，原因={rejectionReason} dropped_queue={droppedCount}", npcName);
            return;
        }

        if (!state.Queues.TryDequeueSpeech(out NpcActionRequest? speechAction) || speechAction is null)
        {
            return;
        }

        string message = speechAction.Message;
        this.logger.Info("Speech", $"弹出对话：{this.logger.Summarize(message, 140)} remaining_queue={state.Queues.PendingSpeechCount}", npcName);
        Game1.DrawDialogue(new Dialogue(npc, null, message));
        state.AwaitingConversationDialogueClose = true;
        state.WaitingForPlayerResponse = false;

        MemoryRecord record = this.memoryStore.AppendEventRecord(
            npcName,
            "npc_reply",
            message,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["time"] = Game1.timeOfDay.ToString()
            });
        _ = this.memoryStore.TryEmbedRecordAsync(npcName, record, CancellationToken.None);
        this.QueueActionBroadcast(npc, speechAction);
    }

    private void RefreshConversationPeriodicLock(string npcName, NpcAgentRuntimeState state)
    {
        if (!state.PausePeriodicUntilConversationSettles)
        {
            return;
        }

        if (state.AwaitingConversationDialogueClose)
        {
            if (Game1.activeClickableMenu is null)
            {
                state.AwaitingConversationDialogueClose = false;
            }
            else
            {
                return;
            }
        }

        if (this.HasInflightRequest(state) ||
            state.Queues.HasQueuedWork ||
            state.WaitingForPlayerResponse)
        {
            return;
        }

        state.PausePeriodicUntilConversationSettles = false;
        state.LastPeriodicTriggeredAt = DateTimeOffset.UtcNow;
        state.PushDebugLine("玩家对话链已结束，恢复周期轮询。");
        this.logger.Debug("Event", "玩家对话链已完全结束，重新允许 periodic_tick。", npcName);
    }
}

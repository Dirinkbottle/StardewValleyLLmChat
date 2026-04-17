using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private void ExecuteNextRealtimeAction(string npcName, NpcAgentRuntimeState state)
    {
        this.ExecuteNextActionFromQueue(npcName, state, isRealtimeQueue: true, "实时动作");
    }

    private void ExecuteNextDeferredAction(string npcName, NpcAgentRuntimeState state)
    {
        this.ExecuteNextActionFromQueue(npcName, state, isRealtimeQueue: false, "延迟动作");
    }

    private void ExecuteNextActionFromQueue(string npcName, NpcAgentRuntimeState state, bool isRealtimeQueue, string queueLabel)
    {
        bool hasAction = isRealtimeQueue
            ? state.Queues.TryPeekRealtimeAction(out NpcActionRequest? actionRequest)
            : state.Queues.TryPeekDeferredAction(out actionRequest);
        if (!hasAction || actionRequest is null)
        {
            state.ActiveActionSummary = string.Empty;
            return;
        }

        NPC? npc = Game1.getCharacterFromName(npcName);
        if (npc is null)
        {
            if (isRealtimeQueue)
            {
                state.Queues.ClearRealtimeActions();
            }
            else
            {
                state.Queues.ClearDeferredActions();
            }

            state.ActiveActionSummary = string.Empty;
            return;
        }

        if (!this.CanExecuteActionRequest(npc, state, actionRequest))
        {
            return;
        }

        if (isRealtimeQueue)
        {
            state.Queues.TryDequeueRealtimeAction(out actionRequest);
        }
        else
        {
            state.Queues.TryDequeueDeferredAction(out actionRequest);
        }

        if (actionRequest is null)
        {
            state.ActiveActionSummary = string.Empty;
            return;
        }

        state.ActiveActionSummary = this.BuildActionSummary(actionRequest);
        this.logger.Info("Action", $"开始执行 {queueLabel} {actionRequest.Type}", npcName);
        bool actionApplied = false;
        switch (actionRequest.Type)
        {
            case NpcActionRequestType.MoveToTile:
                this.ApplyMoveDirective(npc, actionRequest);
                state.NextActionNotBeforeUtc = DateTimeOffset.UtcNow.AddMilliseconds(150);
                actionApplied = true;
                break;
            case NpcActionRequestType.DoEmote:
                if (NpcEmoteCatalog.TryGetGameEmoteId(actionRequest.EmoteName, out int emoteId))
                {
                    npc.doEmote(emoteId);
                    state.NextActionNotBeforeUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(850, actionRequest.DurationMilliseconds));
                    actionApplied = true;
                }
                else
                {
                    this.logger.Warn("Action", $"未知受控表情：{actionRequest.EmoteName}", npcName);
                }

                break;
            case NpcActionRequestType.FacePlayer:
                int faceDuration = Math.Max(500, actionRequest.DurationMilliseconds);
                npc.faceTowardFarmerForPeriod(faceDuration, 4, faceAway: false, Game1.player);
                state.NextActionNotBeforeUtc = DateTimeOffset.UtcNow.AddMilliseconds(faceDuration);
                actionApplied = true;
                break;
            case NpcActionRequestType.PlayEndBehavior:
                this.ApplyEndBehaviorDirective(npc, actionRequest);
                state.NextActionNotBeforeUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(700, actionRequest.DurationMilliseconds));
                actionApplied = true;
                break;
            case NpcActionRequestType.PauseAndWait:
                this.ApplyPauseDirective(npc, actionRequest);
                state.NextActionNotBeforeUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(500, actionRequest.DurationMilliseconds));
                actionApplied = true;
                break;
            case NpcActionRequestType.PlayRouteAnimation:
                this.ApplyRouteAnimationDirective(npc, actionRequest);
                state.NextActionNotBeforeUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(450, actionRequest.DurationMilliseconds));
                actionApplied = true;
                break;
            case NpcActionRequestType.SpeakToNpc:
                this.ApplyNpcSyncSpeechDirective(npc, actionRequest);
                state.NextActionNotBeforeUtc = DateTimeOffset.UtcNow.AddMilliseconds(280);
                actionApplied = true;
                break;
        }

        state.PushDebugLine($"执行动作：{this.BuildActionSummary(actionRequest)}");
        if (actionApplied)
        {
            this.QueueActionBroadcast(npc, actionRequest);
        }
    }

    private bool CanExecuteActionRequest(NPC npc, NpcAgentRuntimeState state, NpcActionRequest actionRequest)
    {
        if (DateTimeOffset.UtcNow < state.NextActionNotBeforeUtc)
        {
            return false;
        }

        if (npc.temporaryController is not null)
        {
            return false;
        }

        if (actionRequest.Type == NpcActionRequestType.DoEmote && npc.isEmoting)
        {
            return false;
        }

        if (actionRequest.Type == NpcActionRequestType.PauseAndWait && npc.movementPause > 0)
        {
            return false;
        }

        if (actionRequest.Type == NpcActionRequestType.PlayRouteAnimation &&
            (npc.doingEndOfRouteAnimation.Value || npc.goingToDoEndOfRouteAnimation.Value))
        {
            return false;
        }

        return true;
    }

    private string BuildActionSummary(NpcActionRequest actionRequest)
    {
        return actionRequest.Type switch
        {
            NpcActionRequestType.DoEmote when !string.IsNullOrWhiteSpace(actionRequest.EmoteName) => $"DoEmote:{actionRequest.EmoteName}",
            NpcActionRequestType.MoveToTile => $"MoveToTile:{actionRequest.TargetLocationName}({actionRequest.TargetTile.X},{actionRequest.TargetTile.Y})",
            NpcActionRequestType.PlayEndBehavior when !string.IsNullOrWhiteSpace(actionRequest.EndBehavior) => $"PlayEndBehavior:{actionRequest.EndBehavior}",
            NpcActionRequestType.PlayRouteAnimation when !string.IsNullOrWhiteSpace(actionRequest.AnimationName) => $"PlayRouteAnimation:{actionRequest.AnimationName}",
            NpcActionRequestType.SpeakToNpc when !string.IsNullOrWhiteSpace(actionRequest.TargetNpcName) => $"SpeakToNpc:{actionRequest.TargetNpcName}",
            _ => actionRequest.Type.ToString()
        };
    }

    private void ApplyMoveDirective(NPC npc, NpcActionRequest actionRequest)
    {
        string targetLocation = string.IsNullOrWhiteSpace(actionRequest.TargetLocationName)
            ? npc.currentLocation?.NameOrUniqueName ?? npc.DefaultMap
            : actionRequest.TargetLocationName;

        try
        {
            SchedulePathDescription description = npc.pathfindToNextScheduleLocation(
                npc.ScheduleKey ?? "runtime_agent",
                npc.currentLocation?.NameOrUniqueName ?? npc.DefaultMap,
                npc.TilePoint.X,
                npc.TilePoint.Y,
                targetLocation,
                actionRequest.TargetTile.X,
                actionRequest.TargetTile.Y,
                actionRequest.FacingDirection,
                null,
                null);

            if (description.route is null || description.route.Count == 0)
            {
                npc.faceDirection(actionRequest.FacingDirection);
                this.logger.Warn("Action", $"移动指令无可用路径，改为仅朝向 {actionRequest.FacingDirection}。", npc.Name);
                return;
            }

            npc.temporaryController = new PathFindController(description.route, npc, Utility.getGameLocationOfCharacter(npc))
            {
                finalFacingDirection = actionRequest.FacingDirection
            };
        }
        catch (Exception ex)
        {
            this.logger.Warn("Action", $"移动动作失败：{ex.Message}", npc.Name);
            this.monitor.Log($"动作移动 {npc.Name} 到 {targetLocation} ({actionRequest.TargetTile.X}, {actionRequest.TargetTile.Y}) 失败：{ex.Message}", LogLevel.Warn);
        }
    }

    private void ApplyPauseDirective(NPC npc, NpcActionRequest actionRequest)
    {
        int duration = Math.Max(500, actionRequest.DurationMilliseconds);
        npc.Halt();
        npc.movementPause = Math.Max(npc.movementPause, duration);
        if (Game1.player.currentLocation == npc.currentLocation)
        {
            npc.faceTowardFarmerForPeriod(duration, 4, faceAway: false, Game1.player);
        }
        else
        {
            npc.faceDirection(actionRequest.FacingDirection);
        }
    }

    private void ApplyEndBehaviorDirective(NPC npc, NpcActionRequest actionRequest)
    {
        if (string.IsNullOrWhiteSpace(actionRequest.EndBehavior))
        {
            return;
        }

        try
        {
            npc.controller = null;
            npc.temporaryController = null;
            npc.Halt();
            npc.StartActivityRouteEndBehavior(actionRequest.EndBehavior, null);
            this.logger.Info("Action", $"调用原版动画/路由行为 end_behavior={actionRequest.EndBehavior}", npc.Name);
        }
        catch (Exception ex)
        {
            this.logger.Warn("Action", $"播放原版动画失败 end_behavior={actionRequest.EndBehavior} error={ex.Message}", npc.Name);
        }
    }

    private void ApplyRouteAnimationDirective(NPC npc, NpcActionRequest actionRequest)
    {
        if (string.IsNullOrWhiteSpace(actionRequest.AnimationName))
        {
            return;
        }

        try
        {
            npc.controller = null;
            npc.temporaryController = null;
            npc.Halt();
            npc.StartActivityRouteEndBehavior(actionRequest.AnimationName, null);
            this.logger.Info("Action", $"调用受控原版动画 animation={actionRequest.AnimationName}", npc.Name);
        }
        catch (Exception ex)
        {
            this.logger.Warn("Action", $"播放受控原版动画失败 animation={actionRequest.AnimationName} error={ex.Message}", npc.Name);
        }
    }
}

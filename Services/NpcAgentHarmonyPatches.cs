using HarmonyLib;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewMod.Services;

/// <summary>
/// LLM Agent 的 Harmony 钩子，覆盖玩家交互与送礼后的同步事件触发。
/// </summary>
internal static class NpcAgentHarmonyPatches
{
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(NPC), nameof(NPC.checkAction), new[] { typeof(Farmer), typeof(GameLocation) }),
            prefix: new HarmonyMethod(typeof(NpcAgentHarmonyPatches), nameof(BeforeCheckAction)));

        harmony.Patch(
            AccessTools.Method(typeof(NPC), nameof(NPC.receiveGift)),
            postfix: new HarmonyMethod(typeof(NpcAgentHarmonyPatches), nameof(AfterReceiveGift)));
    }

    private static bool BeforeCheckAction(NPC __instance, Farmer who, GameLocation l, ref bool __result)
    {
        if (NpcAgentManager.Instance?.TryInterceptConversation(__instance, who, l, out IClickableMenu? promptMenu) == true &&
            promptMenu is not null)
        {
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = promptMenu;
            __result = true;
            return false;
        }

        return true;
    }

    private static void AfterReceiveGift(NPC __instance, SObject o, Farmer giver)
    {
        NpcAgentManager.Instance?.NotifyGiftReceived(__instance, giver, o);
    }
}

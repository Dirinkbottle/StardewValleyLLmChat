using HarmonyLib;
using StardewValley;

namespace StardewMod.Services;

/// <summary>
/// 用 Harmony 在原版选出当天日程后，替换为模组保存的自定义路线。
/// </summary>
internal static class NpcScheduleHarmonyPatches
{
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(NPC), nameof(NPC.TryLoadSchedule), Type.EmptyTypes),
            postfix: new HarmonyMethod(typeof(NpcScheduleHarmonyPatches), nameof(AfterTryLoadSchedule)));
    }

    private static void AfterTryLoadSchedule(NPC __instance)
    {
        NpcScheduleEditorService.Instance?.ApplyPostfixOverride(__instance);
        NpcAgentManager.Instance?.ApplyPostfixOverride(__instance);
    }
}

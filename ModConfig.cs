using StardewModdingAPI;

namespace StardewMod;

/// <summary>
/// 模组配置。只有 UI 与兼容回退项保留在 config.json。
/// </summary>
public sealed class ModConfig
{
    /// <summary>
    /// 打开模组菜单的快捷键。
    /// </summary>
    public SButton OpenMenuKey { get; set; } = SButton.L;

    /// <summary>
    /// 整个模组菜单树共用的界面倍率档位。
    /// </summary>
    public int MenuScaleIndex { get; set; } = 1;

    /// <summary>
    /// 旧版感知半径回退值。主配置已迁移到 mod.toml 的 [perception].npc_radius_tiles。
    /// </summary>
    public int NpcPerceptionRadiusTiles { get; set; } = 100;
}

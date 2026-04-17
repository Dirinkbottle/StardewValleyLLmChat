using Microsoft.Xna.Framework.Graphics;

namespace StardewMod.Ui;

/// <summary>
/// 标记当前菜单属于本模组，方便统一开关。
/// </summary>
internal interface IModMenu
{
}

/// <summary>
/// 可以在世界层绘制额外覆盖信息的菜单。
/// </summary>
internal interface IWorldOverlayMenu : IModMenu
{
    void DrawWorldOverlay(SpriteBatch spriteBatch);
}

using Microsoft.Xna.Framework.Graphics;
using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Services;

internal sealed partial class NpcScheduleEditorService
{
    private Ui.RouteDrawingMenu? GetLiveRouteDrawingOverlay()
    {
        if (this.activeRouteDrawingOverlay is null)
        {
            return null;
        }

        if (Game1.activeClickableMenu is not null &&
            Game1.activeClickableMenu is not Ui.RouteDrawingMenu &&
            !ReferenceEquals(Game1.activeClickableMenu, this.activeRouteDrawingOverlay))
        {
            this.activeRouteDrawingOverlay = null;
            return null;
        }

        return this.activeRouteDrawingOverlay;
    }

    /// <summary>
    /// 在世界层绘制采样路径或当前选中路径预览。
    /// </summary>
    public void DrawWorldOverlay(SpriteBatch spriteBatch)
    {
        this.GetLiveRouteDrawingOverlay()?.DrawWorldOverlay(spriteBatch);

        if (Game1.activeClickableMenu is Ui.IWorldOverlayMenu overlayMenu)
        {
            overlayMenu.DrawWorldOverlay(spriteBatch);
        }
    }

    public void DrawHudOverlay(SpriteBatch spriteBatch)
    {
        this.GetLiveRouteDrawingOverlay()?.DrawHudOverlay(spriteBatch);
    }

    public void OpenRouteDrawingOverlay(string npcName, EditableScheduleRule rule, int stopIndex, Ui.NpcScheduleEditorMenu returnMenu)
    {
        this.activeRouteDrawingOverlay = new Ui.RouteDrawingMenu(this, npcName, rule, stopIndex, returnMenu);
        Game1.activeClickableMenu = null;
    }

    public void CloseRouteDrawingOverlay(IClickableMenu? nextMenu = null)
    {
        this.activeRouteDrawingOverlay = null;
        Game1.activeClickableMenu = nextMenu;
    }

    public bool IsRouteDrawingOverlayActive()
    {
        return this.GetLiveRouteDrawingOverlay() is not null;
    }

    public bool TryHandleRouteDrawingOverlayInput(SButton button)
    {
        Ui.RouteDrawingMenu? overlay = this.GetLiveRouteDrawingOverlay();
        if (overlay is null)
        {
            return false;
        }

        if (button == SButton.Escape)
        {
            this.helper.Input.Suppress(button);
            overlay.receiveKeyPress(Microsoft.Xna.Framework.Input.Keys.Escape);
            return true;
        }

        if (button == SButton.MouseLeft)
        {
            this.helper.Input.Suppress(button);
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            if (overlay.IsPointInsideOverlay(mouseX, mouseY))
            {
                overlay.receiveLeftClick(mouseX, mouseY);
            }

            return true;
        }

        if (button == SButton.MouseRight)
        {
            this.helper.Input.Suppress(button);
            return true;
        }

        return false;
    }
}

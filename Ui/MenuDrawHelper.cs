using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewMod.Models;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 菜单绘制工具，统一保持视觉风格。
/// </summary>
internal static class MenuDrawHelper
{
    private const int MenuMargin = 32;
    private const int CardPadding = 24;

    public static void DrawBackground(IClickableMenu menu, SpriteBatch spriteBatch)
    {
        spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.72f);
        IClickableMenu.drawTextureBox(spriteBatch, menu.xPositionOnScreen, menu.yPositionOnScreen, menu.width, menu.height, Color.White);
    }

    public static void ResizeMenu(IClickableMenu menu, int preferredWidth, int preferredHeight, int minWidth, int minHeight)
    {
        menu.width = Math.Clamp(preferredWidth, minWidth, Math.Max(minWidth, Game1.uiViewport.Width - MenuMargin * 2));
        menu.height = Math.Clamp(preferredHeight, minHeight, Math.Max(minHeight, Game1.uiViewport.Height - MenuMargin * 2));
        menu.xPositionOnScreen = (Game1.uiViewport.Width - menu.width) / 2;
        menu.yPositionOnScreen = (Game1.uiViewport.Height - menu.height) / 2;
    }

    public static void DrawHeader(IClickableMenu menu, SpriteBatch spriteBatch, string title, string subtitle)
    {
        Vector2 titlePosition = new(menu.xPositionOnScreen + 48, menu.yPositionOnScreen + 36);
        Vector2 subtitlePosition = new(menu.xPositionOnScreen + 48, menu.yPositionOnScreen + 84);
        int subtitleWidth = menu.width - 96;

        spriteBatch.DrawString(Game1.dialogueFont, title, titlePosition, Game1.textColor);
        DrawWrappedText(spriteBatch, Game1.smallFont, subtitle, subtitlePosition, new Color(80, 90, 110), subtitleWidth);
    }

    public static int MeasureHeaderHeight(IClickableMenu menu, string subtitle)
    {
        return Game1.dialogueFont.LineSpacing
               + 14
               + MeasureWrappedHeight(Game1.smallFont, subtitle, menu.width - 96);
    }

    public static void DrawButtonBackground(SpriteBatch spriteBatch, Rectangle bounds, bool selected = false, bool accent = false)
    {
        Color cardColor = selected
            ? new Color(236, 244, 255)
            : accent
                ? new Color(245, 240, 225)
                : new Color(247, 244, 238);

        IClickableMenu.drawTextureBox(
            spriteBatch,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            cardColor,
            1f,
            drawShadow: true,
            0.87f);
    }

    public static void DrawCard(SpriteBatch spriteBatch, MenuActionButton button, bool selected = false, bool accent = false)
    {
        Color titleColor = selected ? new Color(26, 72, 128) : Game1.textColor;
        Color descriptionColor = new(92, 99, 116);
        Color footerColor = new(109, 92, 74);
        int badgeReserveWidth = string.IsNullOrWhiteSpace(button.BadgeText) ? 0 : 112;
        int textWidth = Math.Max(80, button.Bounds.Width - CardPadding * 2 - badgeReserveWidth);

        DrawButtonBackground(spriteBatch, button.Bounds, selected, accent);
        DrawCardBadge(spriteBatch, button);

        Vector2 textPosition = new(button.Bounds.X + CardPadding, button.Bounds.Y + 18);
        textPosition.Y += DrawWrappedText(spriteBatch, Game1.smallFont, button.Title, textPosition, titleColor, textWidth);
        textPosition.Y += 8f;
        textPosition.Y += DrawWrappedText(spriteBatch, Game1.smallFont, button.Description, textPosition, descriptionColor, textWidth);

        if (!string.IsNullOrWhiteSpace(button.Footer))
        {
            textPosition.Y += 8f;
            DrawWrappedText(spriteBatch, Game1.smallFont, button.Footer, textPosition, footerColor, textWidth);
        }
    }

    public static int MeasureCardHeight(MenuActionButton button, int width, int minHeight = 56)
    {
        int badgeReserveWidth = string.IsNullOrWhiteSpace(button.BadgeText) ? 0 : 112;
        int textWidth = Math.Max(80, width - CardPadding * 2 - badgeReserveWidth);
        int height = 18;
        height += MeasureWrappedHeight(Game1.smallFont, button.Title, textWidth);
        height += 8;
        height += MeasureWrappedHeight(Game1.smallFont, button.Description, textWidth);
        if (!string.IsNullOrWhiteSpace(button.Footer))
        {
            height += 8;
            height += MeasureWrappedHeight(Game1.smallFont, button.Footer, textWidth);
        }

        height += 18;
        return Math.Max(minHeight, height);
    }

    private static void DrawCardBadge(SpriteBatch spriteBatch, MenuActionButton button)
    {
        if (string.IsNullOrWhiteSpace(button.BadgeText))
        {
            return;
        }

        string badgeText = button.BadgeText.Trim();
        Vector2 textSize = Game1.smallFont.MeasureString(badgeText);
        int badgeWidth = Math.Max(60, (int)Math.Ceiling(textSize.X) + 24);
        int badgeHeight = 28;
        Rectangle badgeBounds = new(
            button.Bounds.Right - CardPadding - badgeWidth,
            button.Bounds.Y + 16,
            badgeWidth,
            badgeHeight);
        Color badgeColor = button.BadgeAccent
            ? new Color(222, 125, 44)
            : new Color(102, 121, 152);

        spriteBatch.Draw(Game1.staminaRect, badgeBounds, badgeColor * 0.92f);
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(badgeBounds.X, badgeBounds.Y, badgeBounds.Width, 2),
            Color.White * 0.35f);
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(badgeBounds.X, badgeBounds.Bottom - 2, badgeBounds.Width, 2),
            Color.White * 0.35f);
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(badgeBounds.X, badgeBounds.Y, 2, badgeBounds.Height),
            Color.White * 0.35f);
        spriteBatch.Draw(
            Game1.staminaRect,
            new Rectangle(badgeBounds.Right - 2, badgeBounds.Y, 2, badgeBounds.Height),
            Color.White * 0.35f);

        Vector2 textPosition = new(
            badgeBounds.X + (badgeBounds.Width - textSize.X) / 2f,
            badgeBounds.Y + (badgeBounds.Height - textSize.Y) / 2f - 1f);
        spriteBatch.DrawString(Game1.smallFont, badgeText, textPosition, Color.White);
    }

    public static void DrawTilePath(SpriteBatch spriteBatch, IReadOnlyList<TilePointData> tiles)
    {
        DrawTilePath(
            spriteBatch,
            tiles,
            new Color(220, 48, 48),
            new Color(220, 32, 32),
            0.38f,
            0.65f);
    }

    public static void DrawTilePath(
        SpriteBatch spriteBatch,
        IReadOnlyList<TilePointData> tiles,
        Color bodyColor,
        Color finalColor,
        float bodyOpacity,
        float finalOpacity)
    {
        if (Game1.currentLocation is null)
        {
            return;
        }

        for (int i = 0; i < tiles.Count; i++)
        {
            TilePointData tile = tiles[i];
            Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64, tile.Y * 64));
            Rectangle screenRect = new((int)screenPos.X, (int)screenPos.Y, 64, 64);
            Color fill = i == tiles.Count - 1
                ? finalColor * finalOpacity
                : bodyColor * bodyOpacity;

            spriteBatch.Draw(Game1.staminaRect, screenRect, fill);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.X, screenRect.Y, screenRect.Width, 2), Color.White * 0.35f);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.X, screenRect.Bottom - 2, screenRect.Width, 2), Color.White * 0.35f);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.X, screenRect.Y, 2, screenRect.Height), Color.White * 0.35f);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.Right - 2, screenRect.Y, 2, screenRect.Height), Color.White * 0.35f);
        }
    }

    public static void DrawTileMarker(SpriteBatch spriteBatch, TilePointData tile, Color fillColor)
    {
        if (Game1.currentLocation is null)
        {
            return;
        }

        Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64, tile.Y * 64));
        Rectangle screenRect = new((int)screenPos.X, (int)screenPos.Y, 64, 64);
        spriteBatch.Draw(Game1.staminaRect, screenRect, fillColor);
        spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.X, screenRect.Y, screenRect.Width, 2), Color.White * 0.45f);
        spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.X, screenRect.Bottom - 2, screenRect.Width, 2), Color.White * 0.45f);
        spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.X, screenRect.Y, 2, screenRect.Height), Color.White * 0.45f);
        spriteBatch.Draw(Game1.staminaRect, new Rectangle(screenRect.Right - 2, screenRect.Y, 2, screenRect.Height), Color.White * 0.45f);
    }

    public static void DrawThinkingBubble(SpriteBatch spriteBatch, Character character)
    {
        double totalMs = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        int frameIndex = Character.pauseEmote + (int)(totalMs / 240d) % 4;
        Rectangle source = new(
            frameIndex * 16 % Game1.emoteSpriteSheet.Width,
            frameIndex * 16 / Game1.emoteSpriteSheet.Width * 16,
            16,
            16);
        Vector2 drawPosition = character.getLocalPosition(Game1.viewport);
        drawPosition.Y -= 112f + (float)Math.Sin(totalMs / 220d) * 3f;
        drawPosition.X += (float)(character.GetSpriteWidthForPositioning() * 4) / 2f - 32f;
        float pulse = 0.94f + (float)Math.Sin(totalMs / 300d) * 0.06f;
        float layerDepth = Math.Min(0.99999f, (float)character.StandingPixel.Y / 10000f + 0.002f);
        spriteBatch.Draw(Game1.emoteSpriteSheet, drawPosition, source, Color.White * 0.92f, 0f, Vector2.Zero, 4f * pulse, SpriteEffects.None, layerDepth);
    }

    public static string FormatLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
        {
            return "未设置";
        }

        GameLocation? location = Game1.getLocationFromName(locationName);
        return location?.DisplayName ?? locationName;
    }

    public static List<string> WrapText(SpriteFont font, string text, int maxWidth)
    {
        List<string> lines = new();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        foreach (string paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            if (string.IsNullOrEmpty(paragraph))
            {
                lines.Add(string.Empty);
                continue;
            }

            string current = string.Empty;
            foreach (char character in paragraph)
            {
                string candidate = current + character;
                if (current.Length > 0 && font.MeasureString(candidate).X > maxWidth)
                {
                    lines.Add(current.TrimEnd());
                    current = character.ToString();
                }
                else
                {
                    current = candidate;
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current.TrimEnd());
            }
        }

        return lines;
    }

    public static int MeasureWrappedHeight(SpriteFont font, string text, int maxWidth)
    {
        return WrapText(font, text, maxWidth).Count * font.LineSpacing;
    }

    public static float DrawWrappedText(SpriteBatch spriteBatch, SpriteFont font, string text, Vector2 position, Color color, int maxWidth)
    {
        List<string> lines = WrapText(font, text, maxWidth);
        float y = position.Y;

        foreach (string line in lines)
        {
            spriteBatch.DrawString(font, line, new Vector2(position.X, y), color);
            y += font.LineSpacing;
        }

        return lines.Count * font.LineSpacing;
    }
}

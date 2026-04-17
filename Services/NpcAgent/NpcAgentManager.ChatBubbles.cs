using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewMod.Models;
using StardewMod.Ui;
using StardewValley;

namespace StardewMod.Services;

internal sealed partial class NpcAgentManager
{
    private const int ChatBubbleFrameWidth = 140;
    private const int ChatBubbleFrameHeight = 90;

    private void ShowNpcChatBubble(string sourceNpcName, string targetNpcName, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        NpcAgentRuntimeState state = this.GetOrCreateState(sourceNpcName);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int durationMilliseconds = Math.Clamp(1900 + text.Trim().Length * 45, 2200, 6200);
        state.ActiveChatBubble = new NpcChatBubbleDisplayState
        {
            SourceNpcName = sourceNpcName,
            TargetNpcName = targetNpcName,
            Text = text.Trim(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMilliseconds(durationMilliseconds),
            DurationMilliseconds = durationMilliseconds
        };
    }

    private void PruneExpiredChatBubbles()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (NpcAgentRuntimeState state in this.states.Values)
        {
            if (state.ActiveChatBubble?.IsExpired(now) == true)
            {
                state.ActiveChatBubble = null;
            }
        }
    }

    private bool HasVisibleChatBubble(NpcAgentRuntimeState state)
    {
        if (state.ActiveChatBubble is null)
        {
            return false;
        }

        if (state.ActiveChatBubble.IsExpired(DateTimeOffset.UtcNow))
        {
            state.ActiveChatBubble = null;
            return false;
        }

        return true;
    }

    private void DrawNpcChatBubbles(SpriteBatch spriteBatch)
    {
        Texture2D? texture = this.GetChatBubbleTexture();
        if (texture is null)
        {
            return;
        }

        foreach ((string npcName, NpcAgentRuntimeState state) in this.states)
        {
            if (!this.HasVisibleChatBubble(state))
            {
                continue;
            }

            NPC? npc = Game1.getCharacterFromName(npcName);
            if (npc is null || npc.currentLocation != Game1.currentLocation)
            {
                continue;
            }

            this.DrawNpcChatBubble(spriteBatch, texture, npc, state.ActiveChatBubble!);
        }
    }

    private void DrawNpcChatBubble(SpriteBatch spriteBatch, Texture2D texture, NPC npc, NpcChatBubbleDisplayState bubble)
    {
        const int maxTextWidth = 220;
        const int minBubbleWidth = 110;
        const int maxBubbleWidth = 300;
        const int minBubbleHeight = 68;
        const int maxBubbleHeight = 168;
        const int textPaddingX = 20;
        const int textPaddingY = 16;

        List<string> lines = MenuDrawHelper.WrapText(Game1.smallFont, bubble.Text, maxTextWidth);
        float widestLine = lines.Count == 0
            ? 0f
            : lines.Max(line => Game1.smallFont.MeasureString(line).X);
        int textHeight = Math.Max(Game1.smallFont.LineSpacing, lines.Count * Game1.smallFont.LineSpacing);
        int bubbleWidth = Math.Clamp((int)Math.Ceiling(widestLine) + textPaddingX * 2, minBubbleWidth, maxBubbleWidth);
        int bubbleHeight = Math.Clamp(textHeight + textPaddingY * 2 + 6, minBubbleHeight, maxBubbleHeight);

        int frameIndex = bubbleWidth switch
        {
            <= 150 => 0,
            <= 220 => 1,
            _ => 2
        };
        Rectangle sourceRect = new(frameIndex * ChatBubbleFrameWidth, 0, ChatBubbleFrameWidth, ChatBubbleFrameHeight);

        double totalMs = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        Vector2 npcPosition = npc.getLocalPosition(Game1.viewport);
        Vector2 bubblePosition = new(
            npcPosition.X + (npc.GetSpriteWidthForPositioning() * 4f - bubbleWidth) / 2f,
            npcPosition.Y - 144f - bubbleHeight + (float)Math.Sin(totalMs / 300d) * 2.5f);
        Rectangle bubbleBounds = new((int)bubblePosition.X, (int)bubblePosition.Y, bubbleWidth, bubbleHeight);
        float layerDepth = Math.Min(0.99999f, (float)npc.StandingPixel.Y / 10000f + 0.0035f);

        spriteBatch.Draw(texture, bubbleBounds, sourceRect, Color.White * 0.97f, 0f, Vector2.Zero, SpriteEffects.None, layerDepth);

        Vector2 textPosition = new(bubbleBounds.X + textPaddingX, bubbleBounds.Y + textPaddingY - 2);
        foreach (string line in lines)
        {
            Vector2 shadowPosition = textPosition + new Vector2(1f, 1f);
            spriteBatch.DrawString(Game1.smallFont, line, shadowPosition, Color.Black * 0.28f, 0f, Vector2.Zero, 1f, SpriteEffects.None, layerDepth + 0.0001f);
            spriteBatch.DrawString(Game1.smallFont, line, textPosition, new Color(55, 49, 64), 0f, Vector2.Zero, 1f, SpriteEffects.None, layerDepth + 0.0002f);
            textPosition.Y += Game1.smallFont.LineSpacing;
        }
    }

    private Texture2D? GetChatBubbleTexture()
    {
        if (this.chatBubbleTexture is not null)
        {
            return this.chatBubbleTexture;
        }

        try
        {
            this.chatBubbleTexture = this.helper.ModContent.Load<Texture2D>("image/chatbox/chatbox-sheet.png");
        }
        catch (Exception ex)
        {
            this.logger.Warn("ChatBubble", $"加载聊天气泡贴图失败：{ex.Message}", null);
        }

        return this.chatBubbleTexture;
    }
}

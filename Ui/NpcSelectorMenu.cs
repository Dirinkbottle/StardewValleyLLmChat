using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewMod.Models;
using StardewMod.Services;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// NPC 选择页，展示当前可编辑路径的全部 NPC。
/// </summary>
internal sealed class NpcSelectorMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 860;
    private const int PreferredPanelHeight = 520;
    private const int MinPanelWidth = 680;
    private const int MinPanelHeight = 420;
    private const int PortraitSize = 96;
    private const string SubtitleText = "选择要修改路径的角色。列表会自动适应文本高度，滚轮可查看全部可编辑 NPC。";
    private readonly NpcScheduleEditorService scheduleService;
    private readonly Func<IClickableMenu> backFactory;
    private readonly List<NpcMenuEntry> entries;
    private readonly List<MenuActionButton> npcButtons = new();
    private readonly List<int> visibleEntryIndexes = new();
    private readonly MenuActionButton backButton = new("返回上一级", "回到模组主菜单。");
    private int scrollIndex;

    public NpcSelectorMenu(NpcScheduleEditorService scheduleService, Func<IClickableMenu> backFactory)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.scheduleService = scheduleService;
        this.backFactory = backFactory;
        this.entries = this.scheduleService.GetEditableNpcs().ToList();
        this.Recenter();
        this.Relayout();
        this.initializeUpperRightCloseButton();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        this.Recenter();
        this.Relayout();
        this.initializeUpperRightCloseButton();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            Game1.playSound("bigDeSelect");
            Game1.exitActiveMenu();
            return;
        }

        if (this.backButton.Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            Game1.activeClickableMenu = this.backFactory();
            return;
        }

        for (int i = 0; i < this.npcButtons.Count; i++)
        {
            if (!this.npcButtons[i].Contains(x, y))
            {
                continue;
            }

            NpcMenuEntry entry = this.entries[this.visibleEntryIndexes[i]];
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = new NpcScheduleListMenu(
                this.scheduleService,
                entry.InternalName,
                entry.DisplayName,
                () => new NpcSelectorMenu(this.scheduleService, this.backFactory));
            return;
        }
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.upperRightCloseButton?.tryHover(x, y, 0.5f);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            Game1.playSound("bigDeSelect");
            Game1.activeClickableMenu = this.backFactory();
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (direction > 0)
        {
            this.scrollIndex = Math.Max(0, this.scrollIndex - 1);
        }
        else if (direction < 0)
        {
            this.scrollIndex = Math.Min(Math.Max(0, this.entries.Count - 1), this.scrollIndex + 1);
        }

        this.Relayout();
    }

    public override void draw(SpriteBatch b)
    {
        MenuDrawHelper.DrawBackground(this, b);
        MenuDrawHelper.DrawHeader(this, b, "NPC 路线编辑", SubtitleText);

        for (int i = 0; i < this.npcButtons.Count; i++)
        {
            this.DrawNpcEntryCard(b, this.entries[this.visibleEntryIndexes[i]], this.npcButtons[i]);
        }

        MenuDrawHelper.DrawCard(b, this.backButton);
        if (this.entries.Count == 0)
        {
            Vector2 emptyPosition = new(this.xPositionOnScreen + 48, this.yPositionOnScreen + 148);
            MenuDrawHelper.DrawWrappedText(
                b,
                Game1.smallFont,
                "当前没有检测到可编辑路径的 NPC。只有存在原版 schedule 资源，或已经保存过覆盖规则的 NPC，才会出现在这里。",
                emptyPosition,
                new Color(92, 99, 116),
                this.width - 96);
        }
        else if (this.visibleEntryIndexes.Count > 0)
        {
            string pageText = $"显示 {this.visibleEntryIndexes[0] + 1}-{this.visibleEntryIndexes[^1] + 1} / {this.entries.Count}";
            b.DrawString(Game1.smallFont, pageText, new Vector2(this.backButton.Bounds.Right + 18, this.backButton.Bounds.Y + 12), new Color(92, 99, 116));
        }

        base.draw(b);
        this.drawMouse(b);
    }

    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight);
    }

    private void Relayout()
    {
        this.npcButtons.Clear();
        this.visibleEntryIndexes.Clear();
        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(this, SubtitleText);
        int cardX = this.xPositionOnScreen + 42;
        int cardY = this.yPositionOnScreen + 36 + headerHeight + 24;
        int cardWidth = this.width - 84;
        int cardGap = 14;
        int footerY = this.yPositionOnScreen + this.height - 84;
        int listBottom = footerY - 36;

        if (this.scrollIndex >= this.entries.Count)
        {
            this.scrollIndex = Math.Max(0, this.entries.Count - 1);
        }

        for (int i = this.scrollIndex; i < this.entries.Count; i++)
        {
            MenuActionButton button = this.CreateNpcButton(this.entries[i]);
            int cardHeight = this.MeasureNpcCardHeight(button, cardWidth);
            if (cardY + cardHeight > listBottom && this.npcButtons.Count > 0)
            {
                break;
            }

            button.SetBounds(new Rectangle(cardX, cardY, cardWidth, cardHeight));
            this.npcButtons.Add(button);
            this.visibleEntryIndexes.Add(i);
            cardY += cardHeight + cardGap;
        }

        this.backButton.SetBounds(new Rectangle(this.xPositionOnScreen + 42, footerY, 220, 52));
    }

    private MenuActionButton CreateNpcButton(NpcMenuEntry entry)
    {
        return new MenuActionButton(
            entry.DisplayName,
            $"内部名：{entry.InternalName}",
            "点击进入该 NPC 的规则列表并开始编辑路径。");
    }

    private int MeasureNpcCardHeight(MenuActionButton button, int width)
    {
        int textWidth = Math.Max(180, width - PortraitSize - 70);
        int textHeight = 18;
        textHeight += MenuDrawHelper.MeasureWrappedHeight(Game1.smallFont, button.Title, textWidth);
        textHeight += 8;
        textHeight += MenuDrawHelper.MeasureWrappedHeight(Game1.smallFont, button.Description, textWidth);
        if (!string.IsNullOrWhiteSpace(button.Footer))
        {
            textHeight += 8;
            textHeight += MenuDrawHelper.MeasureWrappedHeight(Game1.smallFont, button.Footer, textWidth);
        }

        textHeight += 18;
        return Math.Max(124, Math.Max(textHeight, PortraitSize + 28));
    }

    private void DrawNpcEntryCard(SpriteBatch spriteBatch, NpcMenuEntry entry, MenuActionButton button)
    {
        MenuDrawHelper.DrawButtonBackground(spriteBatch, button.Bounds, accent: true);

        int portraitSize = Math.Min(PortraitSize, button.Bounds.Height - 28);
        Rectangle portraitRect = new(button.Bounds.X + 18, button.Bounds.Y + 14, portraitSize, portraitSize);
        spriteBatch.Draw(Game1.staminaRect, portraitRect, new Color(236, 230, 219));

        Rectangle? sourceRect = entry.Portrait.Width >= 64 && entry.Portrait.Height >= 64
            ? new Rectangle(0, 0, 64, 64)
            : null;
        spriteBatch.Draw(entry.Portrait, portraitRect, sourceRect, Color.White);

        if (sourceRect is null)
        {
            Vector2 placeholderSize = Game1.smallFont.MeasureString("NPC");
            Vector2 placeholderPosition = new(
                portraitRect.X + (portraitRect.Width - placeholderSize.X) / 2f,
                portraitRect.Y + (portraitRect.Height - placeholderSize.Y) / 2f);
            spriteBatch.DrawString(Game1.smallFont, "NPC", placeholderPosition, new Color(109, 92, 74));
        }

        int textWidth = Math.Max(180, button.Bounds.Width - portraitSize - 70);
        Vector2 textPosition = new(button.Bounds.X + portraitSize + 36, button.Bounds.Y + 18);
        textPosition.Y += MenuDrawHelper.DrawWrappedText(spriteBatch, Game1.smallFont, button.Title, textPosition, Game1.textColor, textWidth);
        textPosition.Y += 8f;
        textPosition.Y += MenuDrawHelper.DrawWrappedText(spriteBatch, Game1.smallFont, button.Description, textPosition, new Color(92, 99, 116), textWidth);

        if (!string.IsNullOrWhiteSpace(button.Footer))
        {
            textPosition.Y += 8f;
            MenuDrawHelper.DrawWrappedText(spriteBatch, Game1.smallFont, button.Footer, textPosition, new Color(109, 92, 74), textWidth);
        }
    }
}

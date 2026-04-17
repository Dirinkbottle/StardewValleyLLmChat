using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewMod.Models;
using StardewMod.Services;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// NPC LLM 的角色列表页。
/// </summary>
internal sealed class NpcLlmNpcListMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 980;
    private const int PreferredPanelHeight = 760;
    private const int MinPanelWidth = 760;
    private const int MinPanelHeight = 560;
    private const int AbsoluteMinWidth = 620;
    private const int AbsoluteMinHeight = 460;
    private readonly NpcAgentManager agentManager;
    private readonly Func<IClickableMenu> backFactory;
    private readonly MenuActionButton backButton = new("返回主菜单", "回到模组主菜单。");
    private readonly MenuActionButton enableAllButton = new("全部开启(全天)", "启用全部 villager 的 LLM，并把每周时间窗直接设为全天 06:00-26:00。");
    private readonly MenuActionButton disableAllButton = new("全部关闭", "关闭全部 villager 的 LLM，并停止新的周期/同步请求。");
    private readonly List<NpcAgentMenuEntry> entries;
    private readonly List<MenuActionButton> npcButtons = new();
    private readonly List<int> visibleIndexes = new();
    private int scrollIndex;

    public NpcLlmNpcListMenu(NpcAgentManager agentManager, Func<IClickableMenu> backFactory)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.agentManager = agentManager;
        this.backFactory = backFactory;
        this.entries = this.agentManager.GetNpcMenuEntries().ToList();
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

        if (this.enableAllButton.Contains(x, y))
        {
            this.agentManager.EnableAllNpcAgentsAlwaysOn();
            this.ReloadEntries();
            Game1.playSound("smallSelect");
            this.Relayout();
            return;
        }

        if (this.disableAllButton.Contains(x, y))
        {
            this.agentManager.DisableAllNpcAgents();
            this.ReloadEntries();
            Game1.playSound("bigDeSelect");
            this.Relayout();
            return;
        }

        for (int i = 0; i < this.npcButtons.Count; i++)
        {
            if (!this.npcButtons[i].Contains(x, y))
            {
                continue;
            }

            NpcAgentMenuEntry entry = this.entries[this.visibleIndexes[i]];
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = new NpcLlmSettingsMenu(
                this.agentManager,
                entry.InternalName,
                entry.DisplayName,
                () => new NpcLlmNpcListMenu(this.agentManager, this.backFactory));
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
        string subtitle = "为每个 villager 单独启用 LLM、选择 provider、设置时间窗，并查看最近的运行时调试信息。顶部快捷按钮可一键把所有 NPC 改成全天候开启。";
        MenuDrawHelper.DrawBackground(this, b);
        MenuDrawHelper.DrawHeader(this, b, "NPC LLM", subtitle);

        bool allEnabled = this.entries.Count > 0 && this.entries.All(entry => entry.Enabled);
        MenuDrawHelper.DrawCard(b, this.enableAllButton, accent: allEnabled);
        MenuDrawHelper.DrawCard(b, this.disableAllButton, accent: !allEnabled);

        for (int i = 0; i < this.npcButtons.Count; i++)
        {
            NpcAgentMenuEntry entry = this.entries[this.visibleIndexes[i]];
            MenuActionButton button = this.npcButtons[i];
            MenuDrawHelper.DrawButtonBackground(b, button.Bounds, accent: entry.Enabled);

            int portraitSize = Math.Min(96, button.Bounds.Height - 24);
            Rectangle portraitRect = new(button.Bounds.X + 18, button.Bounds.Y + 14, portraitSize, portraitSize);
            int textWidth = button.Bounds.Width - portraitSize - 64;
            b.Draw(entry.Portrait, portraitRect, new Rectangle(0, 0, 64, 64), Color.White);

            Vector2 textPosition = new(button.Bounds.X + portraitSize + 34, button.Bounds.Y + 18);
            textPosition.Y += MenuDrawHelper.DrawWrappedText(b, Game1.smallFont, entry.DisplayName, textPosition, Game1.textColor, textWidth);
            textPosition.Y += 8f;
            textPosition.Y += MenuDrawHelper.DrawWrappedText(
                b,
                Game1.smallFont,
                $"状态：{(entry.Enabled ? "已启用" : "未启用")} | Provider：{(string.IsNullOrWhiteSpace(entry.ProviderName) ? "未配置" : entry.ProviderName)}",
                textPosition,
                new Color(92, 99, 116),
                textWidth);
            textPosition.Y += 6f;
            MenuDrawHelper.DrawWrappedText(
                b,
                Game1.smallFont,
                $"今天时间窗：{entry.ActiveWindowSummary} | 当前{(entry.IsWithinActiveWindow ? "正在生效" : "未生效")}",
                textPosition,
                new Color(109, 92, 74),
                textWidth);
        }

        MenuDrawHelper.DrawCard(b, this.backButton);
        base.draw(b);
        this.drawMouse(b);
    }

    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight, AbsoluteMinWidth, AbsoluteMinHeight);
    }

    private void ReloadEntries()
    {
        this.entries.Clear();
        this.entries.AddRange(this.agentManager.GetNpcMenuEntries());
        this.scrollIndex = Math.Clamp(this.scrollIndex, 0, Math.Max(0, this.entries.Count - 1));
    }

    private void Relayout()
    {
        this.npcButtons.Clear();
        this.visibleIndexes.Clear();
        string subtitle = "为每个 villager 单独启用 LLM、选择 provider、设置时间窗，并查看最近的运行时调试信息。顶部快捷按钮可一键把所有 NPC 改成全天候开启。";
        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(this, subtitle);
        int rowX = this.xPositionOnScreen + 42;
        int rowY = this.yPositionOnScreen + 36 + headerHeight + 24;
        int rowWidth = this.width - 84;
        int rowGap = 14;
        int footerY = this.yPositionOnScreen + this.height - 84;
        int listBottom = footerY - 44;
        int bulkGap = 12;
        int bulkWidth = Math.Max(180, (rowWidth - bulkGap) / 2);
        int bulkHeight = Math.Max(
            MenuDrawHelper.MeasureCardHeight(this.enableAllButton, bulkWidth, 82),
            MenuDrawHelper.MeasureCardHeight(this.disableAllButton, bulkWidth, 82));

        this.enableAllButton.SetBounds(new Rectangle(rowX, rowY, bulkWidth, bulkHeight));
        this.disableAllButton.SetBounds(new Rectangle(rowX + rowWidth - bulkWidth, rowY, bulkWidth, bulkHeight));
        rowY += bulkHeight + 16;

        for (int i = this.scrollIndex; i < this.entries.Count; i++)
        {
            NpcAgentMenuEntry entry = this.entries[i];
            MenuActionButton button = new(
                entry.DisplayName,
                $"状态：{(entry.Enabled ? "已启用" : "未启用")} | Provider：{(string.IsNullOrWhiteSpace(entry.ProviderName) ? "未配置" : entry.ProviderName)}",
                $"今天时间窗：{entry.ActiveWindowSummary}");
            int rowHeight = Math.Max(116, MenuDrawHelper.MeasureCardHeight(button, rowWidth, 116));
            if (rowY + rowHeight > listBottom && this.npcButtons.Count > 0)
            {
                break;
            }

            button.SetBounds(new Rectangle(rowX, rowY, rowWidth, rowHeight));
            this.npcButtons.Add(button);
            this.visibleIndexes.Add(i);
            rowY += rowHeight + rowGap;
        }

        this.backButton.SetBounds(new Rectangle(this.xPositionOnScreen + 42, footerY, 220, 52));
    }
}

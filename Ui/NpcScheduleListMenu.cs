using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewMod.Models;
using StardewMod.Services;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 展示某个 NPC 的全部可编辑规则键。
/// </summary>
internal sealed class NpcScheduleListMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 980;
    private const int PreferredPanelHeight = 760;
    private const int MinPanelWidth = 760;
    private const int MinPanelHeight = 560;
    private readonly NpcScheduleEditorService scheduleService;
    private readonly string npcName;
    private readonly string npcDisplayName;
    private readonly Func<IClickableMenu> backFactory;
    private readonly MenuActionButton backButton = new("返回角色列表", "回到 NPC 选择页面。");
    private readonly List<ScheduleRuleSummary> ruleSummaries;
    private readonly List<MenuActionButton> ruleButtons = new();
    private readonly List<int> visibleRuleIndexes = new();
    private int scrollIndex;
    private string subtitleText = string.Empty;

    public NpcScheduleListMenu(NpcScheduleEditorService scheduleService, string npcName, string npcDisplayName, Func<IClickableMenu> backFactory)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.scheduleService = scheduleService;
        this.npcName = npcName;
        this.npcDisplayName = npcDisplayName;
        this.backFactory = backFactory;
        this.ruleSummaries = this.scheduleService.GetRuleSummaries(npcName).ToList();
        this.subtitleText = "点击任意规则键进入编辑。带 [Patch] 的规则当前会优先显示并编辑运行时 patch，其余则编辑普通规则。滚轮可滚动列表。";
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

        for (int i = 0; i < this.ruleButtons.Count; i++)
        {
            if (!this.ruleButtons[i].Contains(x, y))
            {
                continue;
            }

            ScheduleRuleSummary summary = this.ruleSummaries[this.visibleRuleIndexes[i]];
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = new NpcScheduleEditorMenu(
                this.scheduleService,
                this.npcName,
                this.npcDisplayName,
                summary.RuleKey,
                () => new NpcScheduleListMenu(this.scheduleService, this.npcName, this.npcDisplayName, this.backFactory));
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

    public override void draw(SpriteBatch b)
    {
        MenuDrawHelper.DrawBackground(this, b);
        MenuDrawHelper.DrawHeader(this, b, $"{this.npcDisplayName} 的规则列表", this.subtitleText);

        for (int i = 0; i < this.ruleButtons.Count; i++)
        {
            int summaryIndex = this.visibleRuleIndexes[i];
            ScheduleRuleSummary summary = this.ruleSummaries[summaryIndex];
            MenuDrawHelper.DrawCard(b, this.ruleButtons[i], accent: summary.HasRuntimePatch || summary.HasOverride);
        }

        MenuDrawHelper.DrawCard(b, this.backButton);
        if (this.ruleSummaries.Count > 0 && this.visibleRuleIndexes.Count > 0)
        {
            string pageText = $"显示 {this.visibleRuleIndexes[0] + 1}-{this.visibleRuleIndexes[^1] + 1} / {this.ruleSummaries.Count}";
            b.DrawString(Game1.smallFont, pageText, new Vector2(this.backButton.Bounds.Right + 18, this.backButton.Bounds.Y + 12), new Color(92, 99, 116));
        }

        base.draw(b);
        this.drawMouse(b);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (direction > 0)
        {
            this.scrollIndex = Math.Max(0, this.scrollIndex - 1);
        }
        else if (direction < 0)
        {
            this.scrollIndex = Math.Min(Math.Max(0, this.ruleSummaries.Count - 1), this.scrollIndex + 1);
        }

        this.Relayout();
    }

    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight);
    }

    private void Relayout()
    {
        this.ruleButtons.Clear();
        this.visibleRuleIndexes.Clear();
        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(this, this.subtitleText);
        int rowX = this.xPositionOnScreen + 42;
        int rowY = this.yPositionOnScreen + 36 + headerHeight + 28;
        int rowWidth = this.width - 84;
        int rowGap = 14;
        int listBottom = this.yPositionOnScreen + this.height - 104;

        if (this.scrollIndex >= this.ruleSummaries.Count)
        {
            this.scrollIndex = Math.Max(0, this.ruleSummaries.Count - 1);
        }

        for (int i = this.scrollIndex; i < this.ruleSummaries.Count; i++)
        {
            ScheduleRuleSummary summary = this.ruleSummaries[i];
            string shortRevision = summary.RuntimePatchRevisionId.Length > 8 ? summary.RuntimePatchRevisionId[..8] : summary.RuntimePatchRevisionId;
            string sourceText = summary.HasRuntimePatch
                ? $"运行时 Patch rev {shortRevision}"
                : summary.HasOverride
                    ? "存档覆盖"
                    : "原版";
            MenuActionButton button = new(
                summary.HasRuntimePatch ? $"[Patch] {summary.DisplayName}" : summary.DisplayName,
                $"{summary.RuleKey} | {summary.PreviewText}",
                $"{summary.Category} | {sourceText} | {summary.StopCount} 段");
            int rowHeight = MenuDrawHelper.MeasureCardHeight(button, rowWidth, 96);
            if (rowY + rowHeight > listBottom && this.ruleButtons.Count > 0)
            {
                break;
            }

            button.SetBounds(new Rectangle(rowX, rowY, rowWidth, rowHeight));
            this.ruleButtons.Add(button);
            this.visibleRuleIndexes.Add(i);
            rowY += rowHeight + rowGap;
        }

        this.backButton.SetBounds(new Rectangle(this.xPositionOnScreen + 42, this.yPositionOnScreen + this.height - 84, 220, 52));
    }
}

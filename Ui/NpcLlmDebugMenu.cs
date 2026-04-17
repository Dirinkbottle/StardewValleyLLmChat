using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewMod.Models;
using StardewMod.Services;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 查看某个 NPC 最近的 AI 调试摘要。
/// </summary>
internal sealed class NpcLlmDebugMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 980;
    private const int PreferredPanelHeight = 760;
    private const int MinPanelWidth = 760;
    private const int MinPanelHeight = 560;
    private const int AbsoluteMinWidth = 620;
    private const int AbsoluteMinHeight = 460;
    private readonly NpcAgentManager agentManager;
    private readonly string npcName;
    private readonly string npcDisplayName;
    private readonly Func<IClickableMenu> backFactory;
    private readonly MenuActionButton backButton = new("返回设置", "回到该 NPC 的设置页。");
    private int scrollLineIndex;

    public NpcLlmDebugMenu(NpcAgentManager agentManager, string npcName, string npcDisplayName, Func<IClickableMenu> backFactory)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.agentManager = agentManager;
        this.npcName = npcName;
        this.npcDisplayName = npcDisplayName;
        this.backFactory = backFactory;
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
        if (this.upperRightCloseButton?.containsPoint(x, y) == true || this.backButton.Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            Game1.activeClickableMenu = this.backFactory();
            return;
        }
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

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.upperRightCloseButton?.tryHover(x, y, 0.5f);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (direction > 0)
        {
            this.scrollLineIndex = Math.Max(0, this.scrollLineIndex - 2);
        }
        else if (direction < 0)
        {
            this.scrollLineIndex += 2;
        }
    }

    public override void draw(SpriteBatch b)
    {
        string subtitle = $"查看 {this.npcDisplayName} 最近一次 AI 请求、tool 调用、patch 摘要和配置问题。";
        NpcAgentRuntimeSummary runtime = this.agentManager.GetRuntimeSummary(this.npcName);
        IReadOnlyList<string> configErrors = this.agentManager.GetConfigErrors();
        MenuDrawHelper.DrawBackground(this, b);
        MenuDrawHelper.DrawHeader(this, b, $"{this.npcDisplayName} 的调试面板", subtitle);

        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(this, subtitle);
        int footerY = this.yPositionOnScreen + this.height - 84;
        Rectangle contentBox = new(this.xPositionOnScreen + 42, this.yPositionOnScreen + 36 + headerHeight + 24, this.width - 84, footerY - (this.yPositionOnScreen + 36 + headerHeight + 24) - 46);
        IClickableMenu.drawTextureBox(b, contentBox.X, contentBox.Y, contentBox.Width, contentBox.Height, new Color(250, 248, 240));

        int textWidth = contentBox.Width - 48;
        Vector2 textPos = new(contentBox.X + 24, contentBox.Y + 20);
        List<string> rawLines = new()
        {
            $"Provider：{runtime.ProviderName}",
            $"时间窗：{(runtime.IsWithinActiveWindow ? "当前生效" : "当前未生效")}",
            $"基线规则：{runtime.BaselineScheduleKey}",
            $"Patch 版本：{(string.IsNullOrWhiteSpace(runtime.PatchRevisionId) ? "无" : runtime.PatchRevisionId)}",
            $"Inflight：{runtime.InflightStatus}",
            $"最近触发：{runtime.LastTrigger}",
            $"最近耗时：{runtime.LastRequestDuration}",
            $"最近 Patch 摘要：{(string.IsNullOrWhiteSpace(runtime.LastPatchSummary) ? "无" : runtime.LastPatchSummary)}",
            $"最近拒绝：{(string.IsNullOrWhiteSpace(runtime.LastRejectionReason) ? "无" : runtime.LastRejectionReason)}",
            "最近 tool 调用：",
            runtime.RecentToolCalls.Count == 0 ? "无" : string.Join(" | ", runtime.RecentToolCalls),
            "最近运行日志：",
            runtime.RecentDebugLines.Count == 0 ? "无" : string.Join('\n', runtime.RecentDebugLines),
            "mod.toml 配置问题：",
            configErrors.Count == 0 ? "无" : string.Join('\n', configErrors),
            $"配置文件路径：{this.agentManager.ConfigPath}"
        };

        List<string> displayLines = new();
        foreach (string line in rawLines)
        {
            foreach (string wrappedLine in MenuDrawHelper.WrapText(Game1.smallFont, line, textWidth))
            {
                displayLines.Add(wrappedLine);
            }

            displayLines.Add(string.Empty);
        }

        MenuDrawHelper.DrawCard(b, this.backButton);
        int lineHeight = Game1.smallFont.LineSpacing + 2;
        int maxVisibleLines = Math.Max(1, (contentBox.Height - 40) / lineHeight);
        int maxOffset = Math.Max(0, displayLines.Count - maxVisibleLines);
        this.scrollLineIndex = Math.Clamp(this.scrollLineIndex, 0, maxOffset);

        for (int i = this.scrollLineIndex; i < displayLines.Count && i < this.scrollLineIndex + maxVisibleLines; i++)
        {
            b.DrawString(Game1.smallFont, displayLines[i], textPos, new Color(72, 72, 84));
            textPos.Y += lineHeight;
        }

        Utility.drawTextWithShadow(
            b,
            "滚轮可滚动日志",
            Game1.smallFont,
            new Vector2(this.backButton.Bounds.Right + 18, this.backButton.Bounds.Y + 12),
            new Color(84, 92, 110));
        base.draw(b);
        this.drawMouse(b);
    }

    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight, AbsoluteMinWidth, AbsoluteMinHeight);
    }

    private void Relayout()
    {
        int footerY = this.yPositionOnScreen + this.height - 84;
        this.backButton.SetBounds(new Rectangle(this.xPositionOnScreen + 42, footerY, 220, 52));
    }
}

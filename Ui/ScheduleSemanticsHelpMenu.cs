using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 展示原版 NPC 日程语义的说明页。
/// </summary>
internal sealed class ScheduleSemanticsHelpMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 920;
    private const int PreferredPanelHeight = 760;
    private const int MinPanelWidth = 700;
    private const int MinPanelHeight = 520;
    private readonly IClickableMenu returnMenu;
    private readonly MenuActionButton backButton = new("返回编辑器", "回到当前规则编辑页。");

    public ScheduleSemanticsHelpMenu(IClickableMenu returnMenu)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.returnMenu = returnMenu;
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
            Game1.activeClickableMenu = this.returnMenu;
            return;
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            Game1.playSound("bigDeSelect");
            Game1.activeClickableMenu = this.returnMenu;
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.upperRightCloseButton?.tryHover(x, y, 0.5f);
    }

    public override void draw(SpriteBatch b)
    {
        MenuDrawHelper.DrawBackground(this, b);
        const string subtitle = "这页解释原版 schedule 的几个关键语义，避免把“出发时间”和“出生点”混在一起。";
        MenuDrawHelper.DrawHeader(this, b, "日程语义说明", subtitle);

        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(this, subtitle);
        Rectangle contentBox = new(this.xPositionOnScreen + 42, this.yPositionOnScreen + 36 + headerHeight + 24, this.width - 84, this.height - headerHeight - 196);
        IClickableMenu.drawTextureBox(b, contentBox.X, contentBox.Y, contentBox.Width, contentBox.Height, new Color(250, 248, 240));

        int textWidth = contentBox.Width - 48;
        Vector2 textPos = new(contentBox.X + 24, contentBox.Y + 22);
        List<string> lines = new()
        {
            "1. 普通时间，例如 700：",
            "表示 NPC 在 7:00 开始走这段路，不表示 7:00 瞬移到终点。",
            "2. 到达时间，例如 a700：",
            "表示希望 7:00 到达，系统会按路线长度反推出更早的出发时间。",
            "3. 日初出生点，例如 0 Town 20 30：",
            "表示这一天加载日程时，NPC 先被放到这里，再开始跑后面的站点。",
            "4. 当前模组的自动接续：",
            "你可以只在目标场景画最后一段红线路径，保存时会先自动从真正起点接到你画线的第一个 tile。",
            "5. 蓝色 tile：",
            "编辑器和采样页里蓝色 tile 表示当前段真正的起点。第一段通常来自日初出生点，后续段来自上一站终点。",
            "6. 路线限制：",
            "跨场景要有合法 warp 链；目标 tile 和中间 tile 也必须是 NPC 可走的格子。"
        };

        foreach (string line in lines)
        {
            textPos.Y += MenuDrawHelper.DrawWrappedText(b, Game1.smallFont, line, textPos, new Color(72, 72, 84), textWidth);
            textPos.Y += 10f;
        }

        MenuDrawHelper.DrawCard(b, this.backButton);
        base.draw(b);
        this.drawMouse(b);
    }

    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight);
    }

    private void Relayout()
    {
        this.backButton.SetBounds(new Rectangle(this.xPositionOnScreen + 42, this.yPositionOnScreen + this.height - 84, 220, 52));
    }
}

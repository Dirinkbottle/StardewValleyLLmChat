using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 模组主菜单，提供主要功能入口和全局界面倍率控制。
/// </summary>
internal sealed class ModMainMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 920;
    private const int PreferredPanelHeight = 620;
    private const int MinPanelWidth = 700;
    private const int MinPanelHeight = 460;
    private const int AbsoluteMinWidth = 620;
    private const int AbsoluteMinHeight = 420;
    private readonly ModConfig config;
    private readonly Action saveAction;
    private readonly MenuActionButton npcRouteButton = new("NPC 路线编辑", "打开角色列表，进入全部可编辑 NPC 的日程与路径编辑器。");
    private readonly MenuActionButton npcLlmButton = new("NPC LLM", "打开 AI 菜单，为各个村民配置 provider、时间窗和调试面板。");
    private readonly MenuActionButton shrinkButton = new("缩小界面", "让整个模组菜单树更紧凑。");
    private readonly MenuActionButton growButton = new("放大界面", "让整个模组菜单树更宽更高。");
    private readonly Func<IClickableMenu> openNpcEditorMenu;
    private readonly Func<IClickableMenu> openNpcLlmMenu;
    private readonly string subtitleText;

    public ModMainMenu(ModConfig config, Action saveAction, Func<IClickableMenu> openNpcEditorMenu, Func<IClickableMenu> openNpcLlmMenu)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.config = config;
        this.saveAction = saveAction;
        this.openNpcEditorMenu = openNpcEditorMenu;
        this.openNpcLlmMenu = openNpcLlmMenu;
        string menuKeyLabel = config.OpenMenuKey.ToString().ToUpperInvariant();
        this.subtitleText = $"按 {menuKeyLabel} 打开或关闭此菜单。倍率按钮会影响整个模组菜单树。";

        this.Recenter();
        this.Relayout();
        this.initializeUpperRightCloseButton();
    }

    /// <inheritdoc />
    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        this.Recenter();
        this.Relayout();
        this.initializeUpperRightCloseButton();
    }

    /// <inheritdoc />
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            Game1.playSound("bigDeSelect");
            Game1.exitActiveMenu();
            return;
        }

        if (this.npcRouteButton.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = this.openNpcEditorMenu();
            return;
        }

        if (this.npcLlmButton.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = this.openNpcLlmMenu();
            return;
        }

        if (this.shrinkButton.Contains(x, y))
        {
            this.ChangeScale(grow: false);
            return;
        }

        if (this.growButton.Contains(x, y))
        {
            this.ChangeScale(grow: true);
            return;
        }

        base.receiveLeftClick(x, y, playSound);
    }

    /// <inheritdoc />
    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.upperRightCloseButton?.tryHover(x, y, 0.5f);
    }

    /// <inheritdoc />
    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            Game1.playSound("bigDeSelect");
            Game1.exitActiveMenu();
            return;
        }

        base.receiveKeyPress(key);
    }

    /// <inheritdoc />
    public override void draw(SpriteBatch b)
    {
        MenuDrawHelper.DrawBackground(this, b);
        MenuDrawHelper.DrawHeader(this, b, "模组菜单", this.subtitleText);

        MenuDrawHelper.DrawCard(b, this.npcRouteButton, accent: true);
        MenuDrawHelper.DrawCard(b, this.npcLlmButton, accent: true);
        MenuDrawHelper.DrawCard(b, this.shrinkButton);
        MenuDrawHelper.DrawCard(b, this.growButton);

        Utility.drawTextWithShadow(
            b,
            $"当前界面倍率：{ModMenuLayoutState.ScaleLabel} | 会持久化并影响整个模组菜单树",
            Game1.smallFont,
            new Vector2(this.xPositionOnScreen + 48, this.yPositionOnScreen + this.height - 118),
            new Color(84, 92, 110));

        base.draw(b);
        this.drawMouse(b);
    }

    /// <summary>
    /// 让菜单始终居中显示。
    /// </summary>
    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight, AbsoluteMinWidth, AbsoluteMinHeight);
    }

    /// <summary>
    /// 统一计算按钮与说明文本的位置。
    /// </summary>
    private void Relayout()
    {
        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(this, this.subtitleText);
        int contentX = this.xPositionOnScreen + 48;
        int contentWidth = this.width - 96;
        int currentY = this.yPositionOnScreen + 36 + headerHeight + 28;
        int buttonHeight = Math.Max(
            MenuDrawHelper.MeasureCardHeight(this.npcRouteButton, contentWidth, 90),
            MenuDrawHelper.MeasureCardHeight(this.npcLlmButton, contentWidth, 90));

        this.npcRouteButton.SetBounds(new Rectangle(contentX, currentY, contentWidth, buttonHeight));
        currentY += buttonHeight + 14;
        this.npcLlmButton.SetBounds(new Rectangle(contentX, currentY, contentWidth, buttonHeight));

        int footerY = this.yPositionOnScreen + this.height - 84;
        this.growButton.SetBounds(new Rectangle(this.xPositionOnScreen + this.width - 48 - 150, footerY, 150, 52));
        this.shrinkButton.SetBounds(new Rectangle(this.growButton.Bounds.X - 12 - 150, footerY, 150, 52));
    }

    private void ChangeScale(bool grow)
    {
        bool changed = grow
            ? ModMenuLayoutState.TryGrow()
            : ModMenuLayoutState.TryShrink();
        if (!changed)
        {
            Game1.playSound("cancel");
            return;
        }

        this.config.MenuScaleIndex = ModMenuLayoutState.ScaleIndex;
        this.saveAction();
        Game1.playSound("smallSelect");
        this.Recenter();
        this.Relayout();
        this.initializeUpperRightCloseButton();
    }
}

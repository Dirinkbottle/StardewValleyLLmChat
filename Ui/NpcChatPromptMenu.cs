using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewMod.Services;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 底部聊天输入框。玩家给启用 LLM 的 NPC 发消息后，manager 会发起同步请求。
/// </summary>
internal sealed class NpcChatPromptMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 900;
    private const int PreferredPanelHeight = 252;
    private const int MinPanelWidth = 560;
    private const int MinPanelHeight = 220;
    private const int AbsoluteMinWidth = 420;
    private const int AbsoluteMinHeight = 200;
    private readonly NpcAgentManager agentManager;
    private readonly NPC npc;
    private readonly MenuActionButton cancelButton = new("取消", "关闭输入框，不发送。");
    private readonly MenuActionButton sendButton;
    private readonly TextBox inputBox;

    public NpcChatPromptMenu(NpcAgentManager agentManager, NPC npc)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: false)
    {
        this.agentManager = agentManager;
        this.npc = npc;
        this.sendButton = new($"对 {npc.displayName} 说", "回车或点击按钮发送。");
        this.inputBox = new TextBox(null, null, Game1.smallFont, Game1.textColor)
        {
            Width = this.width - 72,
            Height = 48,
            Text = string.Empty,
            TitleText = $"对 {npc.displayName} 说"
        };
        this.inputBox.OnEnterPressed += this.OnEnterPressed;
        this.Recenter();
        this.Relayout();
        this.inputBox.SelectMe();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        this.Recenter();
        this.Relayout();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.cancelButton.Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            Game1.exitActiveMenu();
            return;
        }

        if (this.sendButton.Contains(x, y))
        {
            this.Submit();
            return;
        }

        if (new Rectangle(this.inputBox.X, this.inputBox.Y, this.inputBox.Width, this.inputBox.Height).Contains(x, y))
        {
            this.inputBox.SelectMe();
        }
        else
        {
            this.inputBox.Selected = false;
        }
    }

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

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.18f);
        Rectangle panelBounds = new(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height);
        Rectangle inputBounds = new(this.inputBox.X - 10, this.inputBox.Y - 8, this.inputBox.Width + 20, this.inputBox.Height + 16);
        MenuDrawHelper.DrawButtonBackground(b, panelBounds, accent: true);
        IClickableMenu.drawTextureBox(b, inputBounds.X, inputBounds.Y, inputBounds.Width, inputBounds.Height, new Color(252, 250, 244));

        Vector2 titlePos = new(this.xPositionOnScreen + 36, this.yPositionOnScreen + 22);
        Vector2 descPos = new(this.xPositionOnScreen + 36, this.yPositionOnScreen + 52);
        b.DrawString(Game1.smallFont, $"{this.npc.displayName} 的 LLM 对话", titlePos, Game1.textColor);
        MenuDrawHelper.DrawWrappedText(
            b,
            Game1.smallFont,
            "发送后会立刻关闭输入框，NPC 会先原地等待，再通过原版对话框回话。控制台可用 npc_llm_prompt / npc_llm_state / npc_llm_schedule 调试。",
            descPos,
            new Color(80, 90, 110),
            this.width - 72);

        this.inputBox.Draw(b);
        MenuDrawHelper.DrawCard(b, this.cancelButton);
        MenuDrawHelper.DrawCard(b, this.sendButton, accent: true);
        this.drawMouse(b);
    }

    private void OnEnterPressed(TextBox sender)
    {
        this.Submit();
    }

    private void Submit()
    {
        string text = this.inputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            Game1.playSound("cancel");
            return;
        }

        this.agentManager.SubmitPlayerPrompt(this.npc, text);
        Game1.playSound("smallSelect");
        Game1.exitActiveMenu();
    }

    private void Recenter()
    {
        ModMenuLayoutState.ResizeBottomAnchored(
            this,
            PreferredPanelWidth,
            PreferredPanelHeight,
            MinPanelWidth,
            MinPanelHeight,
            AbsoluteMinWidth,
            AbsoluteMinHeight,
            28);
    }

    private void Relayout()
    {
        this.inputBox.X = this.xPositionOnScreen + 36;
        this.inputBox.Y = this.yPositionOnScreen + 118;
        this.inputBox.Width = this.width - 72;
        this.inputBox.Height = 52;

        int buttonWidth = (this.width - 84) / 2;
        this.cancelButton.SetBounds(new Rectangle(this.xPositionOnScreen + 36, this.yPositionOnScreen + this.height - 62, buttonWidth, 44));
        this.sendButton.SetBounds(new Rectangle(this.xPositionOnScreen + 48 + buttonWidth, this.yPositionOnScreen + this.height - 62, buttonWidth, 44));
    }
}

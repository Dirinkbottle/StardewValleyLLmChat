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
    private const int PreferredPanelHeight = 340;
    private const int MinPanelWidth = 560;
    private const int MinPanelHeight = 300;
    private const int AbsoluteMinWidth = 420;
    private const int AbsoluteMinHeight = 260;
    private const string DescriptionText = "发送后输入框会关闭；NPC 可以结合记忆、附近角色和当天行程回答。正在处理旧消息时，新消息会自动优先排队。";
    private static readonly string[] SuggestedPrompts =
    {
        "今天发生了什么让你印象最深？",
        "你最近还记得我们之间哪些有趣的事？",
        "看看附近的人，你现在最想和谁聊两句？",
        "如果今天的行程可以小改一下，你最想去哪里？"
    };
    private readonly NpcAgentManager agentManager;
    private readonly NPC npc;
    private readonly MenuActionButton cancelButton = new("取消", "关闭输入框，不发送。");
    private readonly MenuActionButton inspirationButton = new("来点灵感", "填入一条可继续修改的话题。");
    private readonly MenuActionButton sendButton;
    private readonly TextBox inputBox;
    private int suggestedPromptIndex = -1;

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

        if (this.inspirationButton.Contains(x, y))
        {
            this.FillSuggestedPrompt();
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

    public override void cleanupBeforeExit()
    {
        this.inputBox.Selected = false;
        this.inputBox.OnEnterPressed -= this.OnEnterPressed;
        base.cleanupBeforeExit();
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
            DescriptionText,
            descPos,
            new Color(80, 90, 110),
            this.width - 72);

        this.inputBox.Draw(b);
        var runtime = this.agentManager.GetRuntimeSummary(this.npc.Name);
        string queueHint = runtime.ConversationState.WaitingForPlayerResponse ||
            runtime.InflightStatus.StartsWith("running:", StringComparison.OrdinalIgnoreCase) ||
            runtime.InflightStatus.StartsWith("cancelling:", StringComparison.OrdinalIgnoreCase)
            ? $"{this.npc.displayName} 正在思考；发送后会优先处理这条新消息。"
            : $"{this.npc.displayName} 现在有空，可以直接聊。";
        int queueHintWidth = this.width - 72;
        int queueHintY = this.inputBox.Y + this.inputBox.Height + 10;
        int queueHintHeight = MenuDrawHelper.MeasureWrappedHeight(Game1.smallFont, queueHint, queueHintWidth);
        if (queueHintY + queueHintHeight <= this.cancelButton.Bounds.Y - 6)
        {
            MenuDrawHelper.DrawWrappedText(
                b,
                Game1.smallFont,
                queueHint,
                new Vector2(this.xPositionOnScreen + 36, queueHintY),
                new Color(109, 92, 74),
                queueHintWidth);
        }
        MenuDrawHelper.DrawCard(b, this.cancelButton);
        MenuDrawHelper.DrawCard(b, this.inspirationButton);
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

    private void FillSuggestedPrompt()
    {
        this.suggestedPromptIndex = (this.suggestedPromptIndex + 1) % SuggestedPrompts.Length;
        this.inputBox.Text = SuggestedPrompts[this.suggestedPromptIndex];
        this.inputBox.SelectMe();
        Game1.playSound("smallSelect");
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
        int descriptionHeight = MenuDrawHelper.MeasureWrappedHeight(Game1.smallFont, DescriptionText, this.width - 72);
        this.inputBox.X = this.xPositionOnScreen + 36;
        this.inputBox.Y = this.yPositionOnScreen + 52 + descriptionHeight + 16;
        this.inputBox.Width = this.width - 72;
        this.inputBox.Height = 52;

        int buttonGap = 10;
        int buttonWidth = Math.Max(80, (this.width - 72 - buttonGap * 2) / 3);
        this.cancelButton.SetBounds(new Rectangle(this.xPositionOnScreen + 36, this.yPositionOnScreen + this.height - 62, buttonWidth, 44));
        this.inspirationButton.SetBounds(new Rectangle(this.cancelButton.Bounds.Right + buttonGap, this.cancelButton.Bounds.Y, buttonWidth, 44));
        this.sendButton.SetBounds(new Rectangle(this.inspirationButton.Bounds.Right + buttonGap, this.cancelButton.Bounds.Y, buttonWidth, 44));
    }
}

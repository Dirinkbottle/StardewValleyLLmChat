using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewMod.Models;
using StardewMod.Services;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 单个 NPC 的 LLM 设置页。
/// </summary>
internal sealed class NpcLlmSettingsMenu : IClickableMenu, IModMenu
{
    private const int PreferredPanelWidth = 1140;
    private const int PreferredPanelHeight = 780;
    private const int MinPanelWidth = 860;
    private const int MinPanelHeight = 620;
    private const int AbsoluteMinWidth = 680;
    private const int AbsoluteMinHeight = 500;
    private static readonly string[] DayKeys = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    private static readonly Dictionary<string, string> DayLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = "周一",
        ["Tue"] = "周二",
        ["Wed"] = "周三",
        ["Thu"] = "周四",
        ["Fri"] = "周五",
        ["Sat"] = "周六",
        ["Sun"] = "周日"
    };

    private readonly NpcAgentManager agentManager;
    private readonly string npcName;
    private readonly string npcDisplayName;
    private readonly Func<IClickableMenu> backFactory;
    private readonly MenuActionButton backButton = new("返回列表", "回到 NPC LLM 列表。");
    private readonly MenuActionButton debugButton = new("查看调试", "打开最近的 AI 运行摘要。");
    private MenuActionButton toggleEnabledButton = new("LLM 开关", "启用或关闭当前 NPC 的 LLM。");
    private MenuActionButton providerButton = new("Provider", "从 mod.toml 里已配置的 provider 轮换切换。");
    private MenuActionButton behaviorButton = new("行为控制", "允许或禁止移动、动作、表情。");
    private MenuActionButton speechButton = new("对话控制", "允许或禁止通过 NPC 对话框回复玩家。");
    private MenuActionButton scheduleButton = new("日程控制", "允许或禁止修改未来 schedule。");
    private MenuActionButton periodMinusButton = new("周期 -5 秒", "减少周期轮询间隔。");
    private MenuActionButton periodPlusButton = new("周期 +5 秒", "增加周期轮询间隔。");
    private readonly MenuActionButton addWindowButton = new("新增时间窗", "给当前星期新增一个时间窗。");
    private readonly MenuActionButton removeWindowButton = new("删除时间窗", "删除当前选中的时间窗。");
    private readonly MenuActionButton startMinusButton = new("开始 -10", "开始时间减少 10 分钟。");
    private readonly MenuActionButton startPlusButton = new("开始 +10", "开始时间增加 10 分钟。");
    private readonly MenuActionButton endMinusButton = new("结束 -10", "结束时间减少 10 分钟。");
    private readonly MenuActionButton endPlusButton = new("结束 +10", "结束时间增加 10 分钟。");
    private readonly List<MenuActionButton> dayButtons = new();
    private readonly List<MenuActionButton> windowButtons = new();
    private readonly List<MenuActionButton> primaryButtons = new();
    private NpcAgentSettings settings;
    private string selectedDay = "Mon";
    private int selectedWindowIndex;
    private Rectangle inspectorBounds;

    public NpcLlmSettingsMenu(NpcAgentManager agentManager, string npcName, string npcDisplayName, Func<IClickableMenu> backFactory)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.agentManager = agentManager;
        this.npcName = npcName;
        this.npcDisplayName = npcDisplayName;
        this.backFactory = backFactory;
        this.settings = this.agentManager.GetSettings(npcName);

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

        if (this.debugButton.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = new NpcLlmDebugMenu(
                this.agentManager,
                this.npcName,
                this.npcDisplayName,
                () => new NpcLlmSettingsMenu(this.agentManager, this.npcName, this.npcDisplayName, this.backFactory));
            return;
        }

        if (this.toggleEnabledButton.Contains(x, y))
        {
            this.settings.Enabled = !this.settings.Enabled;
            this.CommitSettings();
            return;
        }

        if (this.providerButton.Contains(x, y))
        {
            this.CycleProvider();
            return;
        }

        if (this.behaviorButton.Contains(x, y))
        {
            this.settings.AllowBehaviorControl = !this.settings.AllowBehaviorControl;
            this.CommitSettings();
            return;
        }

        if (this.speechButton.Contains(x, y))
        {
            this.settings.AllowSpeech = !this.settings.AllowSpeech;
            this.CommitSettings();
            return;
        }

        if (this.scheduleButton.Contains(x, y))
        {
            this.settings.AllowScheduleControl = !this.settings.AllowScheduleControl;
            this.CommitSettings();
            return;
        }

        if (this.periodMinusButton.Contains(x, y))
        {
            this.settings.PeriodicIntervalSeconds = Math.Max(10, this.settings.PeriodicIntervalSeconds - 5);
            this.CommitSettings();
            return;
        }

        if (this.periodPlusButton.Contains(x, y))
        {
            this.settings.PeriodicIntervalSeconds = Math.Min(600, this.settings.PeriodicIntervalSeconds + 5);
            this.CommitSettings();
            return;
        }

        for (int i = 0; i < this.dayButtons.Count; i++)
        {
            if (this.dayButtons[i].Contains(x, y))
            {
                this.selectedDay = DayKeys[i];
                this.selectedWindowIndex = 0;
                this.Relayout();
                return;
            }
        }

        for (int i = 0; i < this.windowButtons.Count; i++)
        {
            if (this.windowButtons[i].Contains(x, y))
            {
                this.selectedWindowIndex = i;
                this.Relayout();
                return;
            }
        }

        if (this.addWindowButton.Contains(x, y))
        {
            this.AddWindow();
            return;
        }

        if (this.removeWindowButton.Contains(x, y))
        {
            this.RemoveWindow();
            return;
        }

        if (this.startMinusButton.Contains(x, y))
        {
            this.ChangeSelectedWindow(-10, 0);
            return;
        }

        if (this.startPlusButton.Contains(x, y))
        {
            this.ChangeSelectedWindow(10, 0);
            return;
        }

        if (this.endMinusButton.Contains(x, y))
        {
            this.ChangeSelectedWindow(0, -10);
            return;
        }

        if (this.endPlusButton.Contains(x, y))
        {
            this.ChangeSelectedWindow(0, 10);
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
        string subtitle = $"管理 {this.npcDisplayName} 的 provider、权限、周期轮询和每周时间窗。URL/Token 文本配置请编辑 {Path.GetFileName(this.agentManager.ConfigPath)}。";
        MenuDrawHelper.DrawBackground(this, b);
        MenuDrawHelper.DrawHeader(this, b, $"{this.npcDisplayName} 的 LLM 设置", subtitle);

        foreach (MenuActionButton button in this.primaryButtons)
        {
            bool accent = button == this.toggleEnabledButton && this.settings.Enabled;
            MenuDrawHelper.DrawCard(b, button, accent: accent);
        }

        for (int i = 0; i < this.dayButtons.Count; i++)
        {
            bool selected = DayKeys[i] == this.selectedDay;
            MenuDrawHelper.DrawCard(b, this.dayButtons[i], selected: selected, accent: selected);
        }

        for (int i = 0; i < this.windowButtons.Count; i++)
        {
            bool selected = i == this.selectedWindowIndex;
            MenuDrawHelper.DrawCard(b, this.windowButtons[i], selected: selected, accent: selected);
        }

        foreach (MenuActionButton button in new[]
                 {
                     this.addWindowButton,
                     this.removeWindowButton,
                     this.startMinusButton,
                     this.startPlusButton,
                     this.endMinusButton,
                     this.endPlusButton
                 })
        {
            MenuDrawHelper.DrawCard(b, button);
        }

        this.DrawInspector(b);

        base.draw(b);
        this.drawMouse(b);
    }

    private void DrawInspector(SpriteBatch b)
    {
        if (this.inspectorBounds.Width <= 0 || this.inspectorBounds.Height <= 0)
        {
            return;
        }

        NpcAgentRuntimeSummary runtime = this.agentManager.GetRuntimeSummary(this.npcName);
        IClickableMenu.drawTextureBox(b, this.inspectorBounds.X, this.inspectorBounds.Y, this.inspectorBounds.Width, this.inspectorBounds.Height, new Color(250, 248, 240));

        Vector2 textPos = new(this.inspectorBounds.X + 18, this.inspectorBounds.Y + 18);
        int textWidth = this.inspectorBounds.Width - 36;
        List<string> lines = new()
        {
            $"Provider：{(string.IsNullOrWhiteSpace(this.settings.ProviderName) ? "未配置" : this.settings.ProviderName)}",
            $"周期：{this.settings.PeriodicIntervalSeconds} 秒",
            $"行为控制：{(this.settings.AllowBehaviorControl ? "开" : "关")}",
            $"对话控制：{(this.settings.AllowSpeech ? "开" : "关")}",
            $"日程控制：{(this.settings.AllowScheduleControl ? "开" : "关")}",
            $"当前天：{DayLabels[this.selectedDay]}",
            $"时间窗：{this.GetDaySummary(this.selectedDay)}",
            $"运行状态：{runtime.InflightStatus}",
            $"基线规则：{runtime.BaselineScheduleKey}",
            $"Patch：{(string.IsNullOrWhiteSpace(runtime.PatchRevisionId) ? "无" : runtime.PatchRevisionId)}",
            $"最近触发：{(string.IsNullOrWhiteSpace(runtime.LastTrigger) ? "无" : runtime.LastTrigger)}",
            $"最近耗时：{(string.IsNullOrWhiteSpace(runtime.LastRequestDuration) ? "无" : runtime.LastRequestDuration)}",
            $"最近拒绝：{(string.IsNullOrWhiteSpace(runtime.LastRejectionReason) ? "无" : runtime.LastRejectionReason)}"
        };

        foreach (string line in lines)
        {
            float height = MenuDrawHelper.DrawWrappedText(b, Game1.smallFont, line, textPos, new Color(72, 72, 84), textWidth);
            textPos.Y += height + 8f;
            if (textPos.Y >= this.inspectorBounds.Bottom - 18)
            {
                break;
            }
        }
    }

    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight, AbsoluteMinWidth, AbsoluteMinHeight);
    }

    private void Relayout()
    {
        this.primaryButtons.Clear();
        this.dayButtons.Clear();
        this.windowButtons.Clear();
        this.inspectorBounds = Rectangle.Empty;

        this.toggleEnabledButton = new MenuActionButton(
            this.settings.Enabled ? "LLM：已启用" : "LLM：已关闭",
            "点击切换当前 NPC 的 LLM 开关。");
        this.providerButton = new MenuActionButton(
            "Provider",
            string.IsNullOrWhiteSpace(this.settings.ProviderName) ? "未配置 provider" : this.settings.ProviderName);
        this.behaviorButton = new MenuActionButton(
            this.settings.AllowBehaviorControl ? "行为控制：开" : "行为控制：关",
            "是否允许即时动作、移动、表情和动画。");
        this.speechButton = new MenuActionButton(
            this.settings.AllowSpeech ? "对话控制：开" : "对话控制：关",
            "是否允许通过 NPC 对话框回复玩家。");
        this.scheduleButton = new MenuActionButton(
            this.settings.AllowScheduleControl ? "日程控制：开" : "日程控制：关",
            "是否允许修改未来 schedule。");
        this.periodMinusButton = new MenuActionButton("周期 -5 秒", $"当前：{this.settings.PeriodicIntervalSeconds} 秒");
        this.periodPlusButton = new MenuActionButton("周期 +5 秒", $"当前：{this.settings.PeriodicIntervalSeconds} 秒");

        string subtitle = $"管理 {this.npcDisplayName} 的 provider、权限、周期轮询和每周时间窗。URL/Token 文本配置请编辑 {Path.GetFileName(this.agentManager.ConfigPath)}。";
        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(this, subtitle);
        int contentX = this.xPositionOnScreen + 36;
        int contentY = this.yPositionOnScreen + 36 + headerHeight + 18;
        int contentWidth = this.width - 72;
        int footerY = this.yPositionOnScreen + this.height - 84;
        int contentBottom = footerY - 46;

        bool wideLayout = contentWidth >= 980 && this.height >= 720;
        if (wideLayout)
        {
            this.LayoutWide(contentX, contentY, contentWidth, contentBottom);
        }
        else
        {
            this.LayoutCompact(contentX, contentY, contentWidth, contentBottom);
        }
    }

    private void LayoutWide(int contentX, int contentY, int contentWidth, int contentBottom)
    {
        int inspectorWidth = Math.Min(320, Math.Max(260, contentWidth / 4));
        int leftAreaWidth = contentWidth - inspectorWidth - 18;
        int primaryWidth = Math.Min(300, Math.Max(240, leftAreaWidth / 3));
        int scheduleWidth = Math.Max(260, leftAreaWidth - primaryWidth - 18);
        int primaryBottom = this.LayoutPrimaryButtons(contentX, contentY, primaryWidth, 1, 58, 10);

        int scheduleX = contentX + primaryWidth + 18;
        int dayColumns = scheduleWidth >= 520 ? 4 : 3;
        int dayBottom = this.LayoutDayButtons(scheduleX, contentY, scheduleWidth, dayColumns);
        int toolColumns = scheduleWidth >= 420 ? 2 : 1;
        int toolTop = this.LayoutToolButtons(scheduleX, contentBottom, scheduleWidth, toolColumns);
        int listBottom = toolTop - 12;

        this.LayoutWindowButtons(scheduleX, dayBottom + 12, scheduleWidth, listBottom);
        this.inspectorBounds = new Rectangle(contentX + leftAreaWidth + 18, contentY, inspectorWidth, Math.Max(160, contentBottom - contentY));

        if (primaryBottom > contentBottom)
        {
            this.inspectorBounds = Rectangle.Empty;
        }
    }

    private void LayoutCompact(int contentX, int contentY, int contentWidth, int contentBottom)
    {
        int primaryColumns = contentWidth >= 760 ? 2 : 1;
        int primaryBottom = this.LayoutPrimaryButtons(contentX, contentY, contentWidth, primaryColumns, 54, 10);
        int dayColumns = contentWidth >= 760 ? 4 : (contentWidth >= 560 ? 3 : 2);
        int dayBottom = this.LayoutDayButtons(contentX, primaryBottom + 12, contentWidth, dayColumns);
        int toolColumns = contentWidth >= 620 ? 2 : 1;
        MenuActionButton[] toolButtons = this.GetToolButtons();
        int[] toolHeights = this.MeasureButtonGridHeights(toolButtons, contentWidth, toolColumns, 46, 10);
        int toolTotalHeight = toolHeights.Sum() + Math.Max(0, toolHeights.Length - 1) * 10;
        int desiredInspectorHeight = Math.Clamp((int)(this.height * 0.24f), 170, 240);
        int availableInspectorHeight = contentBottom - (dayBottom + 12 + toolTotalHeight + 12);
        int inspectorHeight = availableInspectorHeight >= 120
            ? Math.Min(desiredInspectorHeight, availableInspectorHeight)
            : 0;

        int toolBottom = inspectorHeight > 0
            ? contentBottom - inspectorHeight - 12
            : contentBottom;
        int toolTop = this.LayoutToolButtons(contentX, toolBottom, contentWidth, toolColumns);
        this.inspectorBounds = inspectorHeight > 0
            ? new Rectangle(contentX, contentBottom - inspectorHeight, contentWidth, inspectorHeight)
            : Rectangle.Empty;
        this.LayoutWindowButtons(contentX, dayBottom + 12, contentWidth, toolTop - 12);
    }

    private int LayoutPrimaryButtons(int x, int y, int totalWidth, int columns, int minHeight, int gap)
    {
        MenuActionButton[] buttons =
        {
            this.toggleEnabledButton,
            this.providerButton,
            this.behaviorButton,
            this.speechButton,
            this.scheduleButton,
            this.periodMinusButton,
            this.periodPlusButton,
            this.debugButton,
            this.backButton
        };

        this.primaryButtons.AddRange(buttons);
        return this.LayoutButtonGrid(buttons, x, y, totalWidth, columns, gap, gap, minHeight);
    }

    private int LayoutDayButtons(int x, int y, int totalWidth, int columns)
    {
        int buttonGap = 10;
        int buttonWidth = columns <= 1
            ? totalWidth
            : Math.Max(88, (totalWidth - buttonGap * (columns - 1)) / columns);
        int currentY = y;

        for (int i = 0; i < DayKeys.Length; i++)
        {
            MenuActionButton button = new(DayLabels[DayKeys[i]], "点击编辑这一天的时间窗。");
            int row = i / columns;
            int column = i % columns;
            button.SetBounds(new Rectangle(x + column * (buttonWidth + buttonGap), currentY + row * 58, buttonWidth, 48));
            this.dayButtons.Add(button);
        }

        int rows = (int)Math.Ceiling(DayKeys.Length / (float)columns);
        return currentY + rows * 58 - 10;
    }

    private void LayoutWindowButtons(int x, int y, int totalWidth, int bottomY)
    {
        if (bottomY <= y)
        {
            return;
        }

        List<AgentTimeWindow> windows = this.settings.DayWindows[this.selectedDay];
        int currentY = y;
        for (int i = 0; i < windows.Count; i++)
        {
            MenuActionButton button = new(
                $"{DayLabels[this.selectedDay]} 时间窗 {i + 1}",
                $"{Game1.getTimeOfDayString(windows[i].StartTime)} - {Game1.getTimeOfDayString(windows[i].EndTime)}");
            int rowHeight = MenuDrawHelper.MeasureCardHeight(button, totalWidth, 58);
            if (currentY + rowHeight > bottomY && this.windowButtons.Count > 0)
            {
                break;
            }

            button.SetBounds(new Rectangle(x, currentY, totalWidth, rowHeight));
            this.windowButtons.Add(button);
            currentY += rowHeight + 10;
        }
    }

    private int LayoutToolButtons(int x, int bottomY, int totalWidth, int columns)
    {
        MenuActionButton[] buttons = this.GetToolButtons();

        int[] rowHeights = this.MeasureButtonGridHeights(buttons, totalWidth, columns, 46, 10);
        int totalHeight = rowHeights.Sum() + Math.Max(0, rowHeights.Length - 1) * 10;
        int topY = bottomY - totalHeight;
        this.LayoutButtonGrid(buttons, x, topY, totalWidth, columns, 10, 10, 46);
        return topY;
    }

    private MenuActionButton[] GetToolButtons()
    {
        return new[]
        {
            this.addWindowButton,
            this.removeWindowButton,
            this.startMinusButton,
            this.startPlusButton,
            this.endMinusButton,
            this.endPlusButton
        };
    }

    private int[] MeasureButtonGridHeights(IReadOnlyList<MenuActionButton> buttons, int totalWidth, int columns, int minHeight, int horizontalGap)
    {
        int buttonWidth = columns <= 1
            ? totalWidth
            : Math.Max(80, (totalWidth - horizontalGap * (columns - 1)) / columns);
        List<int> rowHeights = new();
        for (int index = 0; index < buttons.Count; index += columns)
        {
            int rowCount = Math.Min(columns, buttons.Count - index);
            int rowHeight = 0;
            for (int column = 0; column < rowCount; column++)
            {
                rowHeight = Math.Max(rowHeight, MenuDrawHelper.MeasureCardHeight(buttons[index + column], buttonWidth, minHeight));
            }

            rowHeights.Add(rowHeight);
        }

        return rowHeights.ToArray();
    }

    private int LayoutButtonGrid(IReadOnlyList<MenuActionButton> buttons, int x, int y, int totalWidth, int columns, int horizontalGap, int verticalGap, int minHeight)
    {
        int buttonWidth = columns <= 1
            ? totalWidth
            : Math.Max(80, (totalWidth - horizontalGap * (columns - 1)) / columns);
        int currentY = y;

        for (int index = 0; index < buttons.Count; index += columns)
        {
            int rowCount = Math.Min(columns, buttons.Count - index);
            int rowHeight = 0;
            for (int column = 0; column < rowCount; column++)
            {
                rowHeight = Math.Max(rowHeight, MenuDrawHelper.MeasureCardHeight(buttons[index + column], buttonWidth, minHeight));
            }

            for (int column = 0; column < rowCount; column++)
            {
                buttons[index + column].SetBounds(new Rectangle(x + column * (buttonWidth + horizontalGap), currentY, buttonWidth, rowHeight));
            }

            currentY += rowHeight + verticalGap;
        }

        return currentY - verticalGap;
    }

    private void AddWindow()
    {
        this.settings.DayWindows[this.selectedDay].Add(new AgentTimeWindow
        {
            StartTime = 800,
            EndTime = 1200
        });
        this.selectedWindowIndex = this.settings.DayWindows[this.selectedDay].Count - 1;
        this.CommitSettings();
    }

    private void RemoveWindow()
    {
        List<AgentTimeWindow> windows = this.settings.DayWindows[this.selectedDay];
        if (windows.Count == 0 || this.selectedWindowIndex < 0 || this.selectedWindowIndex >= windows.Count)
        {
            return;
        }

        windows.RemoveAt(this.selectedWindowIndex);
        this.selectedWindowIndex = Math.Clamp(this.selectedWindowIndex, 0, Math.Max(0, windows.Count - 1));
        this.CommitSettings();
    }

    private void ChangeSelectedWindow(int startDelta, int endDelta)
    {
        List<AgentTimeWindow> windows = this.settings.DayWindows[this.selectedDay];
        if (windows.Count == 0 || this.selectedWindowIndex < 0 || this.selectedWindowIndex >= windows.Count)
        {
            return;
        }

        AgentTimeWindow window = windows[this.selectedWindowIndex];
        window.StartTime = Math.Clamp(Utility.ModifyTime(window.StartTime, startDelta), 600, 2550);
        window.EndTime = Math.Clamp(Utility.ModifyTime(window.EndTime, endDelta), 610, 2600);
        if (window.EndTime <= window.StartTime)
        {
            window.EndTime = Utility.ModifyTime(window.StartTime, 10);
        }

        this.CommitSettings();
    }

    private void CycleProvider()
    {
        List<string> providers = this.agentManager.GetProviderNames().ToList();
        if (providers.Count == 0)
        {
            this.settings.ProviderName = string.Empty;
            this.CommitSettings();
            return;
        }

        int currentIndex = providers.FindIndex(name => string.Equals(name, this.settings.ProviderName, StringComparison.OrdinalIgnoreCase));
        currentIndex = (currentIndex + 1 + providers.Count) % providers.Count;
        this.settings.ProviderName = providers[currentIndex];
        this.CommitSettings();
    }

    private void CommitSettings()
    {
        this.agentManager.SaveSettings(this.npcName, this.settings);
        this.settings = this.agentManager.GetSettings(this.npcName);
        Game1.playSound("drumkit6");
        this.Relayout();
    }

    private string GetDaySummary(string dayKey)
    {
        List<AgentTimeWindow> windows = this.settings.DayWindows[dayKey];
        if (windows.Count == 0)
        {
            return "未设置";
        }

        return string.Join(" | ", windows.Select(window => window.ToString()));
    }
}

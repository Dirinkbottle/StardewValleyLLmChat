using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewMod.Models;
using StardewMod.Services;
using StardewMod.Services.ScheduleRouting;
using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 单条规则的详细编辑页，可修改时间、朝向、站点与自绘路线。
/// </summary>
internal sealed class NpcScheduleEditorMenu : IClickableMenu, IWorldOverlayMenu
{
    private const int PreferredPanelWidth = 1220;
    private const int PreferredPanelHeight = 840;
    private const int MinPanelWidth = 840;
    private const int MinPanelHeight = 640;
    private readonly NpcScheduleEditorService scheduleService;
    private readonly string npcName;
    private readonly string npcDisplayName;
    private readonly string ruleKey;
    private readonly Func<IClickableMenu> backFactory;
    private readonly MenuActionButton backButton = new("返回规则列表", "回到规则列表。");
    private MenuActionButton saveButton = new("保存覆盖", "保存当前规则。");
    private MenuActionButton resetButton = new("恢复原版", "删除当前覆盖。");
    private readonly MenuActionButton addStopButton = new("新增站点", "插入一个站点。");
    private readonly MenuActionButton deleteStopButton = new("删除站点", "删除当前站点。");
    private readonly MenuActionButton timeMinusButton = new("时间 -10", "提前 10 分钟。");
    private readonly MenuActionButton timePlusButton = new("时间 +10", "延后 10 分钟。");
    private MenuActionButton timeModeButton = new("切换时间语义", "切到出发或到达。");
    private MenuActionButton facingButton = new("旋转朝向", "循环朝向。");
    private readonly MenuActionButton setStopToPlayerButton = new("站点=当前位置", "当前站点改成玩家位置。");
    private readonly MenuActionButton drawRouteButton = new("开始绘制路线", "采样当前地图路线。");
    private MenuActionButton startModeButton = new("切换出生点模式", "切到默认或自定义。");
    private MenuActionButton setStartToPlayerButton = new("出生点=当前位置", "起点改成玩家位置。");
    private MenuActionButton rotateStartFacingButton = new("出生点朝向", "循环起点朝向。");
    private MenuActionButton clearBehaviorButton = new("清空结束行为", "清掉结束动作。");
    private MenuActionButton clearMessageButton = new("清空结束对话", "清掉结束文本。");
    private readonly MenuActionButton stopToolsButton = new("站点工具", "时间、终点、绘制。");
    private readonly MenuActionButton startToolsButton = new("起点工具", "出生点和起点朝向。");
    private readonly MenuActionButton helpButton = new("语义说明", "查看 schedule 说明。");
    private readonly List<MenuActionButton> stopButtons = new();
    private readonly List<int> visibleStopIndexes = new();
    private EditableScheduleRule rule = new();
    private int selectedStopIndex;
    private int stopScrollIndex;
    private Rectangle stopListBounds;
    private Rectangle inspectorBox;
    private EditorSection currentSection = EditorSection.Stop;
    private string displaySourceLabel = string.Empty;
    private bool displayingRuntimePatch;
    private string runtimePatchRevisionId = string.Empty;
    private CompiledRoutePreview selectedStopRoutePreview = new();
    private bool selectedStopRoutePreviewDirty = true;

    public NpcScheduleEditorMenu(NpcScheduleEditorService scheduleService, string npcName, string npcDisplayName, string ruleKey, Func<IClickableMenu> backFactory)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.scheduleService = scheduleService;
        this.npcName = npcName;
        this.npcDisplayName = npcDisplayName;
        this.ruleKey = ruleKey;
        this.backFactory = backFactory;
        this.LoadRuleForDisplay(preferRuntimePatch: true);
        this.selectedStopIndex = 0;
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

        if (this.stopToolsButton.Contains(x, y))
        {
            this.currentSection = EditorSection.Stop;
            Game1.playSound("smallSelect");
            this.Relayout();
            return;
        }

        if (this.startToolsButton.Contains(x, y))
        {
            this.currentSection = EditorSection.Start;
            Game1.playSound("smallSelect");
            this.Relayout();
            return;
        }

        if (this.helpButton.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = new ScheduleSemanticsHelpMenu(this);
            return;
        }

        if (this.saveButton.Contains(x, y))
        {
            if (this.displayingRuntimePatch)
            {
                if (this.scheduleService.TryApplyRuleToRuntimePatch(this.npcName, this.rule, out _, out string runtimeError))
                {
                    this.LoadRuleForDisplay(preferRuntimePatch: true);
                    this.selectedStopIndex = Math.Min(this.selectedStopIndex, this.rule.Stops.Count - 1);
                    this.Relayout();
                    Game1.playSound("money");
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage(runtimeError, HUDMessage.error_type));
                    Game1.playSound("cancel");
                }
            }
            else
            {
                this.scheduleService.SaveRule(this.npcName, this.rule);
                this.LoadRuleForDisplay(preferRuntimePatch: false);
                this.selectedStopIndex = Math.Min(this.selectedStopIndex, this.rule.Stops.Count - 1);
                this.Relayout();
                Game1.playSound("money");
            }
            return;
        }

        if (this.resetButton.Contains(x, y))
        {
            if (this.displayingRuntimePatch)
            {
                if (this.scheduleService.TryDiscardRuntimePatch(this.npcName, this.ruleKey, out string runtimeError))
                {
                    this.LoadRuleForDisplay(preferRuntimePatch: true);
                    this.selectedStopIndex = 0;
                    this.Relayout();
                    Game1.playSound("trashcan");
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage(runtimeError, HUDMessage.error_type));
                    Game1.playSound("cancel");
                }
            }
            else
            {
                this.scheduleService.RemoveRule(this.npcName, this.ruleKey);
                this.LoadRuleForDisplay(preferRuntimePatch: false);
                this.selectedStopIndex = 0;
                this.Relayout();
                Game1.playSound("trashcan");
            }
            return;
        }

        if (this.currentSection == EditorSection.Stop)
        {
            if (this.addStopButton.Contains(x, y))
            {
                this.AddStop();
                Game1.playSound("smallSelect");
                return;
            }

            if (this.deleteStopButton.Contains(x, y))
            {
                this.DeleteSelectedStop();
                Game1.playSound("trashcan");
                return;
            }

            if (this.timeMinusButton.Contains(x, y))
            {
                this.AdjustSelectedTime(-10);
                Game1.playSound("drumkit6");
                return;
            }

            if (this.timePlusButton.Contains(x, y))
            {
                this.AdjustSelectedTime(10);
                Game1.playSound("drumkit6");
                return;
            }

            if (this.timeModeButton.Contains(x, y))
            {
                this.ToggleSelectedTimeMode();
                Game1.playSound("drumkit6");
                return;
            }

            if (this.facingButton.Contains(x, y))
            {
                EditableScheduleStop? stop = this.GetSelectedStop();
                if (stop is not null)
                {
                    stop.FacingDirection = (stop.FacingDirection + 1) % 4;
                    this.Relayout();
                    Game1.playSound("drumkit6");
                }
                return;
            }

            if (this.setStopToPlayerButton.Contains(x, y))
            {
                this.SetSelectedStopToPlayerLocation();
                Game1.playSound("smallSelect");
                return;
            }

            if (this.drawRouteButton.Contains(x, y))
            {
                if (this.GetSelectedStop() is not null)
                {
                    Game1.playSound("smallSelect");
                    this.scheduleService.OpenRouteDrawingOverlay(this.npcName, this.rule, this.selectedStopIndex, this);
                }
                return;
            }
        }

        if (this.currentSection == EditorSection.Start)
        {
            if (this.startModeButton.Contains(x, y))
            {
                this.ToggleStartPointMode();
                Game1.playSound("drumkit6");
                return;
            }

            if (this.setStartToPlayerButton.Contains(x, y))
            {
                this.SetStartPointToPlayerLocation();
                Game1.playSound("smallSelect");
                return;
            }

            if (this.rotateStartFacingButton.Contains(x, y))
            {
                if (!this.rule.StartPoint.UseCustomStartPoint)
                {
                    Game1.playSound("cancel");
                    return;
                }

                this.rule.StartPoint.FacingDirection = (this.rule.StartPoint.FacingDirection + 1) % 4;
                this.Relayout();
                Game1.playSound("drumkit6");
                return;
            }

            if (this.clearBehaviorButton.Contains(x, y))
            {
                EditableScheduleStop? stop = this.GetSelectedStop();
                if (stop is not null)
                {
                    stop.EndBehavior = string.Empty;
                    Game1.playSound("trashcan");
                }

                return;
            }

            if (this.clearMessageButton.Contains(x, y))
            {
                EditableScheduleStop? stop = this.GetSelectedStop();
                if (stop is not null)
                {
                    stop.EndMessage = string.Empty;
                    Game1.playSound("trashcan");
                }

                return;
            }
        }

        for (int i = 0; i < this.stopButtons.Count; i++)
        {
            if (!this.stopButtons[i].Contains(x, y))
            {
                continue;
            }

            this.selectedStopIndex = this.visibleStopIndexes[i];
            Game1.playSound("smallSelect");
            this.Relayout();
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
        int mouseX = Game1.getMouseX();
        int mouseY = Game1.getMouseY();
        if (!this.stopListBounds.Contains(mouseX, mouseY))
        {
            return;
        }

        if (direction > 0)
        {
            this.stopScrollIndex = Math.Max(0, this.stopScrollIndex - 1);
        }
        else if (direction < 0)
        {
            this.stopScrollIndex = Math.Min(Math.Max(0, this.rule.Stops.Count - 1), this.stopScrollIndex + 1);
        }

        this.Relayout();
    }

    public override void draw(SpriteBatch b)
    {
        MenuDrawHelper.DrawBackground(this, b);
        MenuDrawHelper.DrawHeader(
            this,
            b,
            this.displayingRuntimePatch
                ? $"{this.npcDisplayName} - {this.rule.DisplayName} [Patch]"
                : $"{this.npcDisplayName} - {this.rule.DisplayName}",
            $"规则键：{this.rule.RuleKey} | 分类：{this.rule.Category} | {this.displaySourceLabel} | 左侧列表支持滚轮滚动，蓝色 tile 是当前段实际起点，黄色是自动接续段");

        for (int i = 0; i < this.stopButtons.Count; i++)
        {
            MenuDrawHelper.DrawCard(b, this.stopButtons[i], selected: this.visibleStopIndexes[i] == this.selectedStopIndex);
        }

        MenuDrawHelper.DrawCard(b, this.backButton);
        MenuDrawHelper.DrawCard(b, this.stopToolsButton, selected: this.currentSection == EditorSection.Stop, accent: this.currentSection != EditorSection.Stop);
        MenuDrawHelper.DrawCard(b, this.startToolsButton, selected: this.currentSection == EditorSection.Start, accent: this.currentSection != EditorSection.Start);
        MenuDrawHelper.DrawCard(b, this.helpButton, accent: true);
        foreach (MenuActionButton actionButton in this.GetVisibleActionButtons())
        {
            bool accent = ReferenceEquals(actionButton, this.saveButton) || ReferenceEquals(actionButton, this.drawRouteButton);
            MenuDrawHelper.DrawCard(b, actionButton, accent: accent);
        }

        this.DrawInspector(b);

        base.draw(b);
        this.drawMouse(b);
    }

    public void DrawWorldOverlay(SpriteBatch spriteBatch)
    {
        EditableScheduleStop? stop = this.GetSelectedStop();
        if (stop is null || Game1.currentLocation is null)
        {
            return;
        }

        RouteAnchorView anchor = this.GetSelectedAnchor();
        if (this.IsCurrentLocationMatch(anchor.LocationName))
        {
            MenuDrawHelper.DrawTileMarker(spriteBatch, anchor.Tile, new Color(36, 132, 220) * 0.48f);
        }

        CompiledRoutePreview preview = this.GetSelectedStopRoutePreview();
        foreach (CompiledRoutePreviewSegment segment in preview.Segments.Where(segment => segment.IsAutoGenerated && this.IsCurrentLocationMatch(segment.LocationName)))
        {
            MenuDrawHelper.DrawTilePath(
                spriteBatch,
                segment.Tiles,
                new Color(245, 188, 24),
                new Color(252, 220, 76),
                0.30f,
                0.56f);
        }

        foreach (CompiledRoutePreviewSegment segment in preview.Segments.Where(segment => !segment.IsAutoGenerated && this.IsCurrentLocationMatch(segment.LocationName)))
        {
            MenuDrawHelper.DrawTilePath(spriteBatch, segment.Tiles);
        }
    }

    public void OnRouteUpdated()
    {
        EditableScheduleStop? stop = this.GetSelectedStop();
        if (stop is not null && stop.RouteTiles.Count > 0)
        {
            stop.TargetTile = stop.RouteTiles[^1].Clone();
        }

        this.Relayout();
    }

    private void Recenter()
    {
        ModMenuLayoutState.Resize(this, PreferredPanelWidth, PreferredPanelHeight, MinPanelWidth, MinPanelHeight);
    }

    private void Relayout()
    {
        this.stopButtons.Clear();
        this.visibleStopIndexes.Clear();
        this.RefreshDynamicButtons();
        int margin = 42;
        int columnGap = 24;
        int headerHeight = MenuDrawHelper.MeasureHeaderHeight(
            this,
            $"规则键：{this.rule.RuleKey} | 分类：{this.rule.Category} | {this.displaySourceLabel} | 左侧列表支持滚轮滚动，蓝色 tile 是当前段实际起点，黄色是自动接续段");
        this.selectedStopRoutePreviewDirty = true;
        int topY = this.yPositionOnScreen + 36 + headerHeight + 26;
        int footerY = this.yPositionOnScreen + this.height - 84;
        int contentWidth = this.width - margin * 2;
        int leftWidth = Math.Clamp((int)(contentWidth * 0.47f), 320, Math.Max(320, contentWidth - 300));
        int rightWidth = Math.Max(260, contentWidth - leftWidth - columnGap);
        int leftX = this.xPositionOnScreen + margin;
        int rightX = leftX + leftWidth + columnGap;
        int listY = topY;
        int listBottom = footerY - 16;
        int listGap = 12;

        if (this.stopScrollIndex >= this.rule.Stops.Count)
        {
            this.stopScrollIndex = Math.Max(0, this.rule.Stops.Count - 1);
        }

        for (int i = this.stopScrollIndex; i < this.rule.Stops.Count; i++)
        {
            EditableScheduleStop stop = this.rule.Stops[i];
            MenuActionButton button = new(
                $"{Game1.getTimeOfDayString(stop.Time)} [{this.GetTimeModeShortText(stop.TimeMode)}] -> {MenuDrawHelper.FormatLocation(stop.LocationName)}",
                $"终点 ({stop.TargetTile.X}, {stop.TargetTile.Y}) | 朝向 {this.GetFacingText(stop.FacingDirection)}",
                $"路点 {stop.RouteTiles.Count} 个 | 起点 {MenuDrawHelper.FormatLocation(this.GetAnchorForStop(i).LocationName)}");
            button.BadgeText = this.displayingRuntimePatch ? "patch" : "normal";
            button.BadgeAccent = this.displayingRuntimePatch;
            int rowHeight = MenuDrawHelper.MeasureCardHeight(button, leftWidth, 96);
            if (listY + rowHeight > listBottom && this.stopButtons.Count > 0)
            {
                break;
            }

            button.SetBounds(new Rectangle(leftX, listY, leftWidth, rowHeight));
            this.stopButtons.Add(button);
            this.visibleStopIndexes.Add(i);
            listY += rowHeight + listGap;
        }

        this.stopListBounds = new Rectangle(leftX, topY, leftWidth, Math.Max(1, listBottom - topY));
        this.backButton.SetBounds(new Rectangle(leftX, footerY, 220, 52));

        int toolY = this.LayoutButtonGrid(new List<MenuActionButton>
        {
            this.stopToolsButton,
            this.startToolsButton,
            this.helpButton
        }, rightX, topY, rightWidth, 8, 12);
        toolY = this.LayoutButtonGrid(this.GetVisibleActionButtons(), rightX, toolY, rightWidth, 8, 12);

        this.inspectorBox = new Rectangle(rightX, toolY + 4, rightWidth, Math.Max(96, footerY - toolY - 4));
    }

    private void DrawInspector(SpriteBatch b)
    {
        EditableScheduleStop? stop = this.GetSelectedStop();
        if (stop is null)
        {
            return;
        }

        IClickableMenu.drawTextureBox(b, this.inspectorBox.X, this.inspectorBox.Y, this.inspectorBox.Width, this.inspectorBox.Height, new Color(250, 248, 240));

        Vector2 textPos = new(this.inspectorBox.X + 24, this.inspectorBox.Y + 22);
        int textWidth = this.inspectorBox.Width - 48;
        RouteAnchorView anchor = this.GetSelectedAnchor();
        EditableScheduleStartPoint effectiveStartPoint = this.GetEffectiveStartPoint();
        CompiledRoutePreview preview = this.GetSelectedStopRoutePreview();
        int autoTileCount = preview.Segments.Where(segment => segment.IsAutoGenerated).Sum(segment => segment.Tiles.Count);
        int manualTileCount = preview.Segments.Where(segment => !segment.IsAutoGenerated).Sum(segment => segment.Tiles.Count);
        List<string> lines = new()
        {
            this.displaySourceLabel,
            $"当前站点：{this.selectedStopIndex + 1} / {this.rule.Stops.Count}",
            $"时间：{Game1.getTimeOfDayString(stop.Time)} | 语义：{this.GetTimeModeText(stop.TimeMode)}",
            $"目标地图：{MenuDrawHelper.FormatLocation(stop.LocationName)}",
            $"终点 Tile：({stop.TargetTile.X}, {stop.TargetTile.Y})",
            $"当前段实际起点：{MenuDrawHelper.FormatLocation(anchor.LocationName)} ({anchor.Tile.X}, {anchor.Tile.Y})",
            $"日初出生点：{this.GetStartModeText()} | {MenuDrawHelper.FormatLocation(effectiveStartPoint.LocationName)} ({effectiveStartPoint.Tile.X}, {effectiveStartPoint.Tile.Y}) | 朝向 {this.GetFacingText(effectiveStartPoint.FacingDirection)}",
            $"朝向：{this.GetFacingText(stop.FacingDirection)}",
            $"结束行为：{(string.IsNullOrWhiteSpace(stop.EndBehavior) ? "无" : stop.EndBehavior)}",
            $"对话消息：{(string.IsNullOrWhiteSpace(stop.EndMessage) ? "无" : stop.EndMessage)}",
            $"路线预览：自动 {autoTileCount} tile | 手工 {manualTileCount} tile",
            $"路线采样点：{stop.RouteTiles.Count} 个",
            $"原始预览：{this.rule.PreviewText}",
            "提示：",
            "1. 点击左侧时间点切换当前编辑对象。",
            "2. 蓝色 tile 是当前段的真正起点，不一定等于你正在画线的场景。",
            "3. 黄色是自动接续段，红色是手工段；两者叠起来才是 NPC 真正会走的完整路线。",
            "4. 如果要让 NPC 一早就出现在别处，需要改“日初出生点”，不是只改 7:00 的第一站。 "
        };

        foreach (string line in lines)
        {
            int height = MenuDrawHelper.MeasureWrappedHeight(Game1.smallFont, line, textWidth);
            if (textPos.Y + height > this.inspectorBox.Bottom - 18)
            {
                break;
            }

            textPos.Y += MenuDrawHelper.DrawWrappedText(b, Game1.smallFont, line, textPos, new Color(72, 72, 84), textWidth);
            textPos.Y += 6f;
        }
    }

    private EditableScheduleStop? GetSelectedStop()
    {
        if (this.selectedStopIndex < 0 || this.selectedStopIndex >= this.rule.Stops.Count)
        {
            return null;
        }

        return this.rule.Stops[this.selectedStopIndex];
    }

    private void LoadRuleForDisplay(bool preferRuntimePatch)
    {
        this.rule = preferRuntimePatch
            ? this.scheduleService.GetPreferredEditableRuleForMenu(this.npcName, this.ruleKey, out this.displaySourceLabel, out this.displayingRuntimePatch, out this.runtimePatchRevisionId)
            : this.scheduleService.GetEditableRule(this.npcName, this.ruleKey).Clone();

        if (!preferRuntimePatch)
        {
            this.displayingRuntimePatch = false;
            this.runtimePatchRevisionId = string.Empty;
        }

        if (!this.rule.StartPoint.UseCustomStartPoint)
        {
            this.rule.StartPoint = this.scheduleService.GetDefaultStartPointPreview(this.npcName);
        }

        if (!preferRuntimePatch)
        {
            this.displaySourceLabel = this.rule.IsOverride
                ? "当前显示：存档覆盖规则"
                : "当前显示：原版规则";
        }
    }

    private void AddStop()
    {
        EditableScheduleStop? basis = this.GetSelectedStop() ?? this.rule.Stops.LastOrDefault();
        Point fallbackTile = Game1.player?.TilePoint ?? Point.Zero;
        string fallbackLocation = Game1.currentLocation?.NameOrUniqueName ?? "SeedShop";

        EditableScheduleStop newStop = new()
        {
            Time = ScheduleTimeHelper.AddMinutes(basis?.Time ?? 700, 60),
            TimeMode = basis?.TimeMode ?? ScheduleTimeMode.Departure,
            LocationName = basis?.LocationName ?? fallbackLocation,
            FacingDirection = basis?.FacingDirection ?? 2,
            EndBehavior = basis?.EndBehavior ?? string.Empty,
            EndMessage = basis?.EndMessage ?? string.Empty,
            TargetTile = basis?.TargetTile.Clone() ?? new TilePointData(fallbackTile),
            RouteTiles = basis?.RouteTiles.Select(tile => tile.Clone()).ToList() ?? new List<TilePointData> { new(fallbackTile) }
        };

        int insertIndex = Math.Clamp(this.selectedStopIndex + 1, 0, this.rule.Stops.Count);
        this.rule.Stops.Insert(insertIndex, newStop);
        this.NormalizeRuleAndKeepSelection(newStop);
        this.Relayout();
    }

    private void DeleteSelectedStop()
    {
        if (this.rule.Stops.Count <= 1 || this.selectedStopIndex < 0 || this.selectedStopIndex >= this.rule.Stops.Count)
        {
            return;
        }

        this.rule.Stops.RemoveAt(this.selectedStopIndex);
        this.selectedStopIndex = Math.Clamp(this.selectedStopIndex, 0, this.rule.Stops.Count - 1);
        this.rule.NormalizeBeforeSave();
        this.Relayout();
    }

    private void AdjustSelectedTime(int deltaMinutes)
    {
        EditableScheduleStop? stop = this.GetSelectedStop();
        if (stop is null)
        {
            return;
        }

        stop.Time = ScheduleTimeHelper.AddMinutes(stop.Time, deltaMinutes);
        this.NormalizeRuleAndKeepSelection(stop);
        this.Relayout();
    }

    private void ToggleSelectedTimeMode()
    {
        EditableScheduleStop? stop = this.GetSelectedStop();
        if (stop is null)
        {
            return;
        }

        stop.TimeMode = stop.TimeMode == ScheduleTimeMode.Departure
            ? ScheduleTimeMode.Arrival
            : ScheduleTimeMode.Departure;
        this.Relayout();
    }

    private void SetSelectedStopToPlayerLocation()
    {
        EditableScheduleStop? stop = this.GetSelectedStop();
        if (stop is null || Game1.currentLocation is null)
        {
            return;
        }

        Point tile = Game1.player.TilePoint;
        stop.LocationName = Game1.currentLocation.NameOrUniqueName;
        stop.TargetTile = new TilePointData(tile);
        stop.RouteTiles = new List<TilePointData> { new(tile) };
        this.Relayout();
    }

    private void ToggleStartPointMode()
    {
        this.rule.StartPoint.UseCustomStartPoint = !this.rule.StartPoint.UseCustomStartPoint;
        if (!this.rule.StartPoint.UseCustomStartPoint)
        {
            this.rule.StartPoint = this.scheduleService.GetDefaultStartPointPreview(this.npcName);
        }

        this.Relayout();
    }

    private void SetStartPointToPlayerLocation()
    {
        if (Game1.currentLocation is null)
        {
            return;
        }

        this.rule.StartPoint.UseCustomStartPoint = true;
        this.rule.StartPoint.LocationName = Game1.currentLocation.NameOrUniqueName;
        this.rule.StartPoint.Tile = new TilePointData(Game1.player.TilePoint);
        this.Relayout();
    }

    private string GetFacingText(int direction)
    {
        return direction switch
        {
            0 => "上",
            1 => "右",
            2 => "下",
            3 => "左",
            _ => direction.ToString()
        };
    }

    private string GetTimeModeText(ScheduleTimeMode timeMode)
    {
        return timeMode == ScheduleTimeMode.Arrival ? "到达时间" : "出发时间";
    }

    private string GetTimeModeShortText(ScheduleTimeMode timeMode)
    {
        return timeMode == ScheduleTimeMode.Arrival ? "到达" : "出发";
    }

    private string GetStartModeText()
    {
        return this.rule.StartPoint.UseCustomStartPoint ? "自定义出生点" : "原版默认出生点";
    }

    private EditableScheduleStartPoint GetEffectiveStartPoint()
    {
        return this.rule.StartPoint.UseCustomStartPoint
            ? this.rule.StartPoint
            : this.scheduleService.GetDefaultStartPointPreview(this.npcName);
    }

    private RouteAnchorView GetSelectedAnchor()
    {
        EditableScheduleStartPoint effectiveStartPoint = this.GetEffectiveStartPoint();
        if (this.selectedStopIndex <= 0)
        {
            return new RouteAnchorView(effectiveStartPoint.LocationName, effectiveStartPoint.Tile.Clone());
        }

        EditableScheduleStop previousStop = this.rule.Stops[this.selectedStopIndex - 1];
        return new RouteAnchorView(previousStop.LocationName, previousStop.TargetTile.Clone());
    }

    private RouteAnchorView GetAnchorForStop(int stopIndex)
    {
        EditableScheduleStartPoint effectiveStartPoint = this.GetEffectiveStartPoint();
        if (stopIndex <= 0)
        {
            return new RouteAnchorView(effectiveStartPoint.LocationName, effectiveStartPoint.Tile.Clone());
        }

        EditableScheduleStop previousStop = this.rule.Stops[stopIndex - 1];
        return new RouteAnchorView(previousStop.LocationName, previousStop.TargetTile.Clone());
    }

    private bool IsCurrentLocationMatch(string locationName)
    {
        if (Game1.currentLocation is null || string.IsNullOrWhiteSpace(locationName))
        {
            return false;
        }

        return string.Equals(locationName, Game1.currentLocation.NameOrUniqueName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(locationName, Game1.currentLocation.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshDynamicButtons()
    {
        EditableScheduleStop? selectedStop = this.GetSelectedStop();
        EditableScheduleStartPoint effectiveStartPoint = this.GetEffectiveStartPoint();
        string shortRevision = this.runtimePatchRevisionId.Length > 8 ? this.runtimePatchRevisionId[..8] : this.runtimePatchRevisionId;
        this.saveButton = new MenuActionButton(
            this.displayingRuntimePatch ? "应用到当前 Patch" : "保存覆盖",
            this.displayingRuntimePatch
                ? $"当前正在编辑运行时 patch（rev {shortRevision}），保存会直接改当前 patch。"
                : "保存当前规则到存档覆盖。");
        this.resetButton = new MenuActionButton(
            this.displayingRuntimePatch ? "丢弃当前 Patch" : "恢复原版",
            this.displayingRuntimePatch
                ? "移除当前运行时 patch，并切回普通规则显示。"
                : "删除当前覆盖，恢复到普通规则。");
        this.timeModeButton = new MenuActionButton(
            "切换时间语义",
            $"当前：{this.GetTimeModeText(selectedStop?.TimeMode ?? ScheduleTimeMode.Departure)}");
        this.facingButton = new MenuActionButton(
            "旋转朝向",
            $"当前站点朝向：{this.GetFacingText(selectedStop?.FacingDirection ?? 2)}");
        this.startModeButton = new MenuActionButton(
            this.rule.StartPoint.UseCustomStartPoint ? "出生点：自定义" : "出生点：原版默认",
            this.rule.StartPoint.UseCustomStartPoint ? "点击切回原版默认出生点。" : "点击切到自定义出生点。");
        this.setStartToPlayerButton = new MenuActionButton(
            "出生点=当前位置",
            "设置后会自动切到自定义出生点。");
        this.rotateStartFacingButton = new MenuActionButton(
            "出生点朝向",
            this.rule.StartPoint.UseCustomStartPoint
                ? $"当前自定义朝向：{this.GetFacingText(this.rule.StartPoint.FacingDirection)}"
                : $"当前由原版决定：{this.GetFacingText(effectiveStartPoint.FacingDirection)}");
        EditableScheduleStop? stopForCleanup = this.GetSelectedStop();
        this.clearBehaviorButton = new MenuActionButton(
            "清空结束行为",
            string.IsNullOrWhiteSpace(stopForCleanup?.EndBehavior) ? "当前站点没有结束行为。" : $"当前：{stopForCleanup!.EndBehavior}");
        this.clearMessageButton = new MenuActionButton(
            "清空结束对话",
            string.IsNullOrWhiteSpace(stopForCleanup?.EndMessage) ? "当前站点没有结束对话。" : $"当前：{stopForCleanup!.EndMessage}");
    }

    private void NormalizeRuleAndKeepSelection(EditableScheduleStop? focusedStop)
    {
        this.rule.NormalizeBeforeSave();
        if (focusedStop is null)
        {
            this.selectedStopIndex = Math.Clamp(this.selectedStopIndex, 0, Math.Max(0, this.rule.Stops.Count - 1));
            return;
        }

        int newIndex = this.rule.Stops.FindIndex(stop => ReferenceEquals(stop, focusedStop));
        this.selectedStopIndex = newIndex >= 0
            ? newIndex
            : Math.Clamp(this.selectedStopIndex, 0, Math.Max(0, this.rule.Stops.Count - 1));
    }

    private CompiledRoutePreview GetSelectedStopRoutePreview()
    {
        if (!this.selectedStopRoutePreviewDirty)
        {
            return this.selectedStopRoutePreview;
        }

        this.selectedStopRoutePreview = this.scheduleService.BuildStopRoutePreview(this.npcName, this.rule, this.selectedStopIndex);
        this.selectedStopRoutePreviewDirty = false;
        return this.selectedStopRoutePreview;
    }

    private List<MenuActionButton> GetVisibleActionButtons()
    {
        return this.currentSection switch
        {
            EditorSection.Start => new List<MenuActionButton>
            {
                this.saveButton,
                this.resetButton,
                this.startModeButton,
                this.setStartToPlayerButton,
                this.rotateStartFacingButton,
                this.clearBehaviorButton,
                this.clearMessageButton
            },
            _ => new List<MenuActionButton>
            {
                this.saveButton,
                this.resetButton,
                this.addStopButton,
                this.deleteStopButton,
                this.timeMinusButton,
                this.timePlusButton,
                this.timeModeButton,
                this.facingButton,
                this.setStopToPlayerButton,
                this.drawRouteButton
            }
        };
    }

    private int LayoutButtonGrid(IReadOnlyList<MenuActionButton> buttons, int x, int y, int totalWidth, int rowGap, int columnGap)
    {
        int columns = totalWidth >= 520 ? 3 : totalWidth >= 360 ? 2 : 1;
        int buttonWidth = columns == 1
            ? totalWidth
            : (totalWidth - columnGap * (columns - 1)) / columns;

        for (int index = 0; index < buttons.Count;)
        {
            int count = Math.Min(columns, buttons.Count - index);
            int rowHeight = 0;
            for (int column = 0; column < count; column++)
            {
                rowHeight = Math.Max(rowHeight, MenuDrawHelper.MeasureCardHeight(buttons[index + column], buttonWidth, 52));
            }

            for (int column = 0; column < count; column++)
            {
                int buttonX = x + column * (buttonWidth + columnGap);
                buttons[index + column].SetBounds(new Rectangle(buttonX, y, buttonWidth, rowHeight));
            }

            y += rowHeight + rowGap;
            index += count;
        }

        return y;
    }

    private sealed class RouteAnchorView
    {
        public RouteAnchorView(string locationName, TilePointData tile)
        {
            this.LocationName = locationName;
            this.Tile = tile;
        }

        public string LocationName { get; }

        public TilePointData Tile { get; }
    }

    private enum EditorSection
    {
        Stop = 0,
        Start = 1
    }
}

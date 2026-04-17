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
/// 世界叠加层绘制模式。玩家按住鼠标左键即可记录 tile 路线，按住鼠标右键可擦除当前经过的采样 tile。
/// </summary>
internal sealed class RouteDrawingMenu : IClickableMenu, IWorldOverlayMenu
{
    private const int PreferredPanelWidth = 920;
    private const int PreferredPanelHeight = 240;
    private const int MinPanelWidth = 640;
    private const int MinPanelHeight = 220;
    private readonly NpcScheduleEditorService scheduleService;
    private readonly string npcName;
    private readonly EditableScheduleRule rule;
    private readonly int stopIndex;
    private readonly NpcScheduleEditorMenu returnMenu;
    private readonly MenuActionButton saveButton = new("保存采样", "将当前采样路线写回当前站点。");
    private readonly MenuActionButton clearButton = new("清空采样", "清空当前已采集的 tile。");
    private readonly MenuActionButton cancelButton = new("取消返回", "放弃本次采样并回到编辑页。");
    private readonly List<TilePointData> sampledTiles;
    private CompiledRoutePreview draftPreview = new();
    private bool draftPreviewDirty = true;
    private bool sampledTilesOverrideActive;
    private bool isSampling;
    private bool isErasing;
    private Point? lastEraseCursorTile;
    private Point lastViewportSize = Point.Zero;

    public RouteDrawingMenu(NpcScheduleEditorService scheduleService, string npcName, EditableScheduleRule rule, int stopIndex, NpcScheduleEditorMenu returnMenu)
        : base(0, 0, PreferredPanelWidth, PreferredPanelHeight, showUpperRightCloseButton: true)
    {
        this.scheduleService = scheduleService;
        this.npcName = npcName;
        this.rule = rule;
        this.stopIndex = stopIndex;
        this.returnMenu = returnMenu;
        this.sampledTiles = rule.Stops[stopIndex].RouteTiles.Select(tile => tile.Clone()).ToList();

        this.ResizeAndPosition();
        this.Relayout();
        this.initializeUpperRightCloseButton();
        this.lastViewportSize = new Point(Game1.uiViewport.Width, Game1.uiViewport.Height);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true || this.cancelButton.Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            this.scheduleService.CloseRouteDrawingOverlay(this.returnMenu);
            return;
        }

        if (this.clearButton.Contains(x, y))
        {
            this.sampledTiles.Clear();
            this.sampledTilesOverrideActive = true;
            this.draftPreviewDirty = true;
            Game1.playSound("trashcan");
            return;
        }

        if (this.saveButton.Contains(x, y))
        {
            this.CommitRoute();
            Game1.playSound("money");
            this.scheduleService.CloseRouteDrawingOverlay(this.returnMenu);
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
            this.scheduleService.CloseRouteDrawingOverlay(this.returnMenu);
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void draw(SpriteBatch b)
    {
        this.SyncViewportLayoutIfNeeded();
        this.DrawOverlayPanel(b, drawCursor: true);
    }

    public void DrawHudOverlay(SpriteBatch b)
    {
        this.SyncViewportLayoutIfNeeded();
        this.DrawOverlayPanel(b, drawCursor: false);
    }

    public void UpdateOverlayInteraction()
    {
        this.SyncViewportLayoutIfNeeded();
        this.performHoverAction(Game1.getMouseX(), Game1.getMouseY());
    }

    public bool IsPointInsideOverlay(int x, int y)
    {
        Rectangle menuBounds = new(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height);
        return menuBounds.Contains(x, y);
    }

    private void DrawOverlayPanel(SpriteBatch b, bool drawCursor)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.12f);
        IClickableMenu.drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

        Vector2 titlePos = new(this.xPositionOnScreen + 36, this.yPositionOnScreen + 24);
        Vector2 subtitlePos = new(this.xPositionOnScreen + 36, this.yPositionOnScreen + 68);
        Vector2 statusPos = new(this.xPositionOnScreen + 36, this.yPositionOnScreen + 108);
        int textWidth = this.width - 72;

        b.DrawString(Game1.dialogueFont, "路径采样模式", titlePos, Game1.textColor);
        MenuDrawHelper.DrawWrappedText(b, Game1.smallFont, "在世界中可以自由移动和切图；按住鼠标左键拖动即可绘制路线，按住鼠标右键可擦除经过的采样 tile。蓝色 tile 是系统自动接续的起点，黄色是自动接续段，红色是你当前采样的手工段。", subtitlePos, new Color(80, 90, 110), textWidth);
        MenuDrawHelper.DrawWrappedText(
            b,
            Game1.smallFont,
            $"当前站点：{this.stopIndex + 1} | 地图：{MenuDrawHelper.FormatLocation(Game1.currentLocation?.NameOrUniqueName ?? "未知")} | 已采样 {this.sampledTiles.Count} 个 tile | 可边走边看完整路线，也可切图查看当前地图上的段",
            statusPos,
            new Color(92, 99, 116),
            textWidth);

        MenuDrawHelper.DrawCard(b, this.saveButton, accent: true);
        MenuDrawHelper.DrawCard(b, this.clearButton);
        MenuDrawHelper.DrawCard(b, this.cancelButton);

        base.draw(b);
        if (drawCursor)
        {
            this.drawMouse(b);
        }
    }

    public void DrawWorldOverlay(SpriteBatch spriteBatch)
    {
        RouteAnchorView anchor = this.GetAnchor();
        if (this.IsCurrentLocationMatch(anchor.LocationName))
        {
            MenuDrawHelper.DrawTileMarker(spriteBatch, anchor.Tile, new Color(36, 132, 220) * 0.48f);
        }

        CompiledRoutePreview preview = this.GetDraftPreview();
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

    /// <summary>
    /// 由服务在每帧调用，负责实时采样鼠标经过的 tile。
    /// </summary>
    public void CaptureCursorTileIfNeeded()
    {
        if (Game1.currentLocation?.Map is null)
        {
            return;
        }

        MouseState state = Mouse.GetState();
        bool isDrawMode = state.LeftButton == ButtonState.Pressed && state.RightButton != ButtonState.Pressed;
        bool isEraseMode = state.RightButton == ButtonState.Pressed && state.LeftButton != ButtonState.Pressed;
        if (!isDrawMode)
        {
            this.isSampling = false;
        }

        if (!isEraseMode)
        {
            this.isErasing = false;
            this.lastEraseCursorTile = null;
        }

        if (!isDrawMode && !isEraseMode)
        {
            return;
        }

        int mouseX = Game1.getMouseX();
        int mouseY = Game1.getMouseY();
        Rectangle menuBounds = new(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height);
        if (menuBounds.Contains(mouseX, mouseY))
        {
            return;
        }

        Point tile = Game1.currentCursorTile.ToPoint();
        if (!this.IsValidTile(tile))
        {
            return;
        }

        if (isDrawMode)
        {
            if (!this.isSampling)
            {
                this.isSampling = true;
                this.TryAddTile(tile);
                return;
            }

            this.AddOrthogonalPath(tile);
            return;
        }

        if (!this.isErasing)
        {
            this.isErasing = true;
            this.TryRemoveTile(tile);
            this.lastEraseCursorTile = tile;
            return;
        }

        this.RemoveOrthogonalPath(this.lastEraseCursorTile ?? tile, tile);
        this.lastEraseCursorTile = tile;
    }

    private void CommitRoute()
    {
        if (this.sampledTiles.Count == 0)
        {
            return;
        }

        EditableScheduleStop stop = this.rule.Stops[this.stopIndex];
        stop.LocationName = Game1.currentLocation?.NameOrUniqueName ?? stop.LocationName;
        stop.RouteTiles.Clear();
        stop.RouteTiles.AddRange(this.sampledTiles.Select(tile => tile.Clone()));
        stop.TargetTile = stop.RouteTiles[^1].Clone();
        this.returnMenu.OnRouteUpdated();
    }

    private void Relayout()
    {
        int buttonY = this.yPositionOnScreen + this.height - 72;
        int buttonX = this.xPositionOnScreen + 36;
        int contentWidth = this.width - 72;
        bool threeColumns = contentWidth >= 720;
        bool twoColumns = !threeColumns && contentWidth >= 480;

        if (threeColumns)
        {
            int buttonWidth = (contentWidth - 24) / 3;
            this.saveButton.SetBounds(new Rectangle(buttonX, buttonY, buttonWidth, 50));
            this.clearButton.SetBounds(new Rectangle(buttonX + buttonWidth + 12, buttonY, buttonWidth, 50));
            this.cancelButton.SetBounds(new Rectangle(buttonX + (buttonWidth + 12) * 2, buttonY, buttonWidth, 50));
            return;
        }

        if (twoColumns)
        {
            int buttonWidth = (contentWidth - 12) / 2;
            this.saveButton.SetBounds(new Rectangle(buttonX, buttonY - 62, buttonWidth, 50));
            this.clearButton.SetBounds(new Rectangle(buttonX + buttonWidth + 12, buttonY - 62, buttonWidth, 50));
            this.cancelButton.SetBounds(new Rectangle(buttonX, buttonY, contentWidth, 50));
            return;
        }

        this.saveButton.SetBounds(new Rectangle(buttonX, buttonY - 124, contentWidth, 50));
        this.clearButton.SetBounds(new Rectangle(buttonX, buttonY - 62, contentWidth, 50));
        this.cancelButton.SetBounds(new Rectangle(buttonX, buttonY, contentWidth, 50));
    }

    private void ResizeAndPosition()
    {
        int scaledPreferredWidth = ModMenuLayoutState.ScalePreferred(PreferredPanelWidth);
        int scaledMinWidth = ModMenuLayoutState.ScaleMinimum(MinPanelWidth, MinPanelWidth);
        int maxWidth = Math.Max(scaledMinWidth, Game1.uiViewport.Width - 64);
        this.width = Math.Clamp(scaledPreferredWidth, scaledMinWidth, maxWidth);
        int contentWidth = this.width - 72;
        int desiredHeight = PreferredPanelHeight;
        if (contentWidth < 480)
        {
            desiredHeight = 380;
        }
        else if (contentWidth < 720)
        {
            desiredHeight = 300;
        }

        int scaledPreferredHeight = ModMenuLayoutState.ScalePreferred(desiredHeight);
        int scaledMinHeight = ModMenuLayoutState.ScaleMinimum(MinPanelHeight, MinPanelHeight);
        int maxHeight = Math.Max(scaledMinHeight, Game1.uiViewport.Height - 64);
        this.height = Math.Clamp(scaledPreferredHeight, scaledMinHeight, maxHeight);
        this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
        this.yPositionOnScreen = 28;
    }

    private void SyncViewportLayoutIfNeeded()
    {
        Point viewportSize = new(Game1.uiViewport.Width, Game1.uiViewport.Height);
        if (viewportSize == this.lastViewportSize)
        {
            return;
        }

        this.lastViewportSize = viewportSize;
        this.ResizeAndPosition();
        this.Relayout();
        this.initializeUpperRightCloseButton();
    }

    private bool IsValidTile(Point tile)
    {
        if (Game1.currentLocation?.Map is null)
        {
            return false;
        }

        int maxX = Game1.currentLocation.Map.DisplayWidth / 64;
        int maxY = Game1.currentLocation.Map.DisplayHeight / 64;
        return tile.X >= 0 && tile.Y >= 0 && tile.X < maxX && tile.Y < maxY;
    }

    private void AddOrthogonalPath(Point targetTile)
    {
        if (this.sampledTiles.Count == 0)
        {
            this.TryAddTile(targetTile);
            return;
        }

        Point current = this.sampledTiles[^1].ToPoint();
        while (current.X != targetTile.X)
        {
            current.X += Math.Sign(targetTile.X - current.X);
            this.TryAddTile(current);
        }

        while (current.Y != targetTile.Y)
        {
            current.Y += Math.Sign(targetTile.Y - current.Y);
            this.TryAddTile(current);
        }
    }

    private void RemoveOrthogonalPath(Point startTile, Point targetTile)
    {
        Point current = startTile;
        this.TryRemoveTile(current);
        while (current.X != targetTile.X)
        {
            current.X += Math.Sign(targetTile.X - current.X);
            this.TryRemoveTile(current);
        }

        while (current.Y != targetTile.Y)
        {
            current.Y += Math.Sign(targetTile.Y - current.Y);
            this.TryRemoveTile(current);
        }
    }

    private void TryAddTile(Point tile)
    {
        if (this.sampledTiles.Count > 0)
        {
            TilePointData last = this.sampledTiles[^1];
            if (last.X == tile.X && last.Y == tile.Y)
            {
                return;
            }
        }

        this.sampledTiles.Add(new TilePointData(tile));
        this.sampledTilesOverrideActive = true;
        this.draftPreviewDirty = true;
    }

    private void TryRemoveTile(Point tile)
    {
        for (int i = this.sampledTiles.Count - 1; i >= 0; i--)
        {
            TilePointData candidate = this.sampledTiles[i];
            if (candidate.X != tile.X || candidate.Y != tile.Y)
            {
                continue;
            }

            this.sampledTiles.RemoveAt(i);
            this.sampledTilesOverrideActive = true;
            this.draftPreviewDirty = true;
            return;
        }
    }

    private RouteAnchorView GetAnchor()
    {
        if (this.stopIndex <= 0)
        {
            return new RouteAnchorView(this.rule.StartPoint.LocationName, this.rule.StartPoint.Tile.Clone());
        }

        EditableScheduleStop previousStop = this.rule.Stops[this.stopIndex - 1];
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

    private CompiledRoutePreview GetDraftPreview()
    {
        if (!this.draftPreviewDirty)
        {
            return this.draftPreview;
        }

        EditableScheduleStop previewStop = this.rule.Stops[this.stopIndex].Clone();
        if (this.sampledTilesOverrideActive)
        {
            previewStop.LocationName = Game1.currentLocation?.NameOrUniqueName ?? previewStop.LocationName;
            previewStop.RouteTiles = this.sampledTiles.Select(tile => tile.Clone()).ToList();
            if (previewStop.RouteTiles.Count > 0)
            {
                previewStop.TargetTile = previewStop.RouteTiles[^1].Clone();
            }
        }

        this.draftPreview = this.scheduleService.BuildStopRoutePreview(this.npcName, this.rule, this.stopIndex, previewStop);
        this.draftPreviewDirty = false;
        return this.draftPreview;
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
}

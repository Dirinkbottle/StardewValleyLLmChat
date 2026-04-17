using Microsoft.Xna.Framework.Input;
using StardewMod.Models;
using StardewMod.Services.ScheduleRouting;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

/// <summary>
/// 负责 NPC 路线编辑器的数据读取、保存和运行时日程替换。
/// </summary>
internal sealed partial class NpcScheduleEditorService
{
    private const string SaveDataKey = "npc-route-editor-data";
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly NpcScheduleRouteBridgeService routeBridgeService;
    private readonly HashSet<string> missingRawScheduleNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> rawScheduleCache = new(StringComparer.OrdinalIgnoreCase);
    private Ui.RouteDrawingMenu? activeRouteDrawingOverlay;
    private NpcRouteEditorSaveData saveData = new();

    public NpcScheduleEditorService(IModHelper helper, IMonitor monitor, NpcScheduleRouteBridgeService routeBridgeService)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.routeBridgeService = routeBridgeService;
        Instance = this;
    }

    public static NpcScheduleEditorService? Instance { get; private set; }

    /// <summary>
    /// 进入存档后读取上次保存的数据，并对当天已保存覆盖的 NPC 日程执行刷新。
    /// </summary>
    public void LoadFromSave()
    {
        this.saveData = this.helper.Data.ReadSaveData<NpcRouteEditorSaveData>(SaveDataKey) ?? new NpcRouteEditorSaveData();
        this.RefreshAllOverriddenNpcSchedules();
    }

    /// <summary>
    /// 回标题时清空内存数据，避免串档。
    /// </summary>
    public void ClearForTitle()
    {
        this.activeRouteDrawingOverlay = null;
        this.saveData = new NpcRouteEditorSaveData();
        this.missingRawScheduleNames.Clear();
        this.rawScheduleCache.Clear();
    }

    /// <summary>
    /// 新的一天重新尝试应用当天规则。
    /// </summary>
    public void OnDayStarted()
    {
        this.RefreshAllOverriddenNpcSchedules();
    }

    /// <summary>
    /// 驱动世界叠加层类菜单的采样逻辑。
    /// </summary>
    public void Update()
    {
        Ui.RouteDrawingMenu? drawMenu = this.activeRouteDrawingOverlay;
        if (drawMenu is null && Game1.activeClickableMenu is Ui.RouteDrawingMenu activeMenu)
        {
            drawMenu = activeMenu;
        }

        if (drawMenu is not null)
        {
            drawMenu.UpdateOverlayInteraction();
            drawMenu.CaptureCursorTileIfNeeded();
        }
    }
}

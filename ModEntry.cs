using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewMod.Services;
using StardewMod.Services.Memory;
using StardewMod.Services.ScheduleRouting;
using StardewMod.Ui;

namespace StardewMod;

/// <summary>
/// 模组入口，负责初始化配置、事件和菜单。
/// </summary>
public sealed class ModEntry : Mod
{
    private ModConfig config = null!;
    private NpcScheduleEditorService scheduleEditorService = null!;
    private NpcScheduleRouteBridgeService scheduleRouteBridgeService = null!;
    private NpcLlmConfigService llmConfigService = null!;
    private NpcLlmConsoleLogger llmLogger = null!;
    private NpcLlmRouter llmRouter = null!;
    private NpcLlmMemoryStore llmMemoryStore = null!;
    private NpcLlmFactStore llmFactStore = null!;
    private NpcPersonalityService npcPersonalityService = null!;
    private NpcLlmToolService llmToolService = null!;
    private NpcAgentManager npcAgentManager = null!;
    private Harmony harmony = null!;

    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        ModMenuLayoutState.Initialize(this.config.MenuScaleIndex);
        this.config.MenuScaleIndex = ModMenuLayoutState.ScaleIndex;
        this.scheduleRouteBridgeService = new NpcScheduleRouteBridgeService(new FarmLocationRouteResolver());
        this.scheduleEditorService = new NpcScheduleEditorService(helper, this.Monitor, this.scheduleRouteBridgeService);
        this.llmConfigService = new NpcLlmConfigService(helper, this.Monitor);
        this.llmLogger = new NpcLlmConsoleLogger(this.Monitor, () => this.llmConfigService.Current.Router.EnableVerboseDebug);
        this.llmConfigService.LoadOrCreate();
        this.llmRouter = new NpcLlmRouter(this.llmConfigService, this.Monitor, this.llmLogger);
        this.llmMemoryStore = new NpcLlmMemoryStore(helper, this.Monitor, this.llmConfigService, this.llmRouter, this.llmLogger);
        this.llmFactStore = new NpcLlmFactStore(helper, this.llmLogger);
        this.npcPersonalityService = new NpcPersonalityService(helper, this.llmLogger);
        this.llmToolService = new NpcLlmToolService(this.scheduleEditorService, this.llmFactStore, this.llmLogger);
        this.npcAgentManager = new NpcAgentManager(
            this.config,
            helper,
            this.Monitor,
            this.scheduleEditorService,
            this.llmConfigService,
            this.llmRouter,
            this.llmMemoryStore,
            this.llmFactStore,
            this.npcPersonalityService,
            this.llmToolService,
            this.llmLogger);
        this.harmony = new Harmony(this.ModManifest.UniqueID);

        NpcScheduleHarmonyPatches.Apply(this.harmony);
        NpcAgentHarmonyPatches.Apply(this.harmony);

        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.DayEnding += this.OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Display.RenderedHud += this.OnRenderedHud;
        helper.ConsoleCommands.Add("npc_llm_state", "打印 NPC LLM 运行时状态。用法: npc_llm_state <NPC内部名>", this.OnNpcLlmStateCommand);
        helper.ConsoleCommands.Add("npc_llm_schedule", "打印 NPC 当前生效日程。用法: npc_llm_schedule <NPC内部名>", this.OnNpcLlmScheduleCommand);
        helper.ConsoleCommands.Add("npc_llm_prompt", "直接向 NPC 注入一句测试对话。用法: npc_llm_prompt <NPC内部名> <文本>", this.OnNpcLlmPromptCommand);
    }

    /// <summary>
    /// 统一处理每帧逻辑，把入口保持得尽量薄。
    /// </summary>
    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.scheduleEditorService.Update();
        this.npcAgentManager.Update();
    }

    /// <summary>
    /// 存档读完后加载自定义路线数据，并尝试刷新当天已保存覆盖的 NPC 日程。
    /// </summary>
    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.llmLogger.Info("Lifecycle", "SaveLoaded -> 开始加载路线编辑与 NPC Agent 数据。");
        this.scheduleEditorService.LoadFromSave();
        this.npcAgentManager.LoadFromSave();
    }

    /// <summary>
    /// 新的一天重新尝试应用当天规则。
    /// </summary>
    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.llmLogger.Info("Lifecycle", "DayStarted -> 刷新 NPC 路线与 LLM Agent。");
        this.scheduleEditorService.OnDayStarted();
        this.npcAgentManager.OnDayStarted();
    }

    /// <summary>
    /// 日终时把当天行程和互动写入 NPC 记忆库。
    /// </summary>
    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        this.llmLogger.Info("Lifecycle", "DayEnding -> 准备写入 NPC Agent 记忆。");
        this.npcAgentManager.OnDayEnding();
    }

    /// <summary>
    /// 回标题时清空当前存档上下文。
    /// </summary>
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.llmLogger.Info("Lifecycle", "ReturnedToTitle -> 清理 NPC Agent 运行时。");
        this.scheduleEditorService.ClearForTitle();
        this.npcAgentManager.ClearForTitle();
    }

    /// <summary>
    /// 在世界层绘制路线采样和预览。
    /// </summary>
    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        this.scheduleEditorService.DrawWorldOverlay(e.SpriteBatch);
        this.npcAgentManager.DrawWorldOverlay(e.SpriteBatch);
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        this.scheduleEditorService.DrawHudOverlay(e.SpriteBatch);
    }

    /// <summary>
    /// 处理按键开关和菜单打开逻辑。
    /// </summary>
    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        if (this.scheduleEditorService.TryHandleRouteDrawingOverlayInput(e.Button))
        {
            return;
        }

        if (this.scheduleEditorService.IsRouteDrawingOverlayActive())
        {
            if (e.Button == this.config.OpenMenuKey)
            {
                this.Helper.Input.Suppress(e.Button);
            }

            return;
        }

        if (e.Button != this.config.OpenMenuKey)
        {
            return;
        }

        if (Game1.activeClickableMenu is IModMenu)
        {
            Game1.exitActiveMenu();
            return;
        }

        // 避免覆盖游戏自己的菜单，降低与原版/其他模组冲突的概率。
        if (Game1.activeClickableMenu is not null)
        {
            return;
        }

        Game1.activeClickableMenu = this.CreateMainMenu();
    }

    /// <summary>
    /// 持久化配置，并打印当前状态，方便排查问题。
    /// </summary>
    private void SaveConfig()
    {
        this.config.MenuScaleIndex = ModMenuLayoutState.ScaleIndex;
        this.Helper.WriteConfig(this.config);
        this.Monitor.Log(
            $"设置已保存：菜单倍率={ModMenuLayoutState.ScaleLabel}，快捷键={this.config.OpenMenuKey}。",
            LogLevel.Info);
    }

    private void OnNpcLlmStateCommand(string command, string[] args)
    {
        if (args.Length < 1)
        {
            this.Monitor.Log("用法: npc_llm_state <NPC内部名>", LogLevel.Warn);
            return;
        }

        foreach (string line in this.npcAgentManager.BuildConsoleStateReport(args[0]))
        {
            this.Monitor.Log($"[NPC LLM][Console] {line}", LogLevel.Info);
        }
    }

    private void OnNpcLlmScheduleCommand(string command, string[] args)
    {
        if (args.Length < 1)
        {
            this.Monitor.Log("用法: npc_llm_schedule <NPC内部名>", LogLevel.Warn);
            return;
        }

        foreach (string line in this.npcAgentManager.BuildConsoleScheduleReport(args[0]))
        {
            this.Monitor.Log($"[NPC LLM][Console] {line}", LogLevel.Info);
        }
    }

    private void OnNpcLlmPromptCommand(string command, string[] args)
    {
        if (args.Length < 2)
        {
            this.Monitor.Log("用法: npc_llm_prompt <NPC内部名> <文本>", LogLevel.Warn);
            return;
        }

        string npcName = args[0];
        string text = string.Join(' ', args.Skip(1));
        if (!this.npcAgentManager.TrySubmitConsolePrompt(npcName, text, out string error))
        {
            this.Monitor.Log($"[NPC LLM][Console] {error}", LogLevel.Warn);
            return;
        }

        this.Monitor.Log($"[NPC LLM][Console] 已注入测试对话 npc={npcName} text={text}", LogLevel.Info);
    }

    /// <summary>
    /// 统一构造主菜单，方便子菜单回跳时复用。
    /// </summary>
    private IClickableMenu CreateMainMenu()
    {
        return new ModMainMenu(
            this.config,
            this.SaveConfig,
            () => new NpcSelectorMenu(this.scheduleEditorService, this.CreateMainMenu),
            () => new NpcLlmNpcListMenu(this.npcAgentManager, this.CreateMainMenu));
    }
}

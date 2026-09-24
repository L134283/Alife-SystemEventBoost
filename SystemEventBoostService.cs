using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.SystemEvent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Alife.Plugin.SystemEventBoost;

[Module(
    "主动事件增强版",
    "定时任务、报点模式、工作模式与游戏/睡眠/峰谷/勿扰/撒娇等陪伴模式，外加对话窗口倒计时挂件；并实现官方 ISystemEventService（QQ 群聊/私聊消息直接重置报点倒计时）。详细说明见设置面板内的「插件说明」。",
    url: "https://github.com/L134283/Alife-SystemEventBoost",
    defaultCategory: "Doro的妙妙工具",
    editorUI: typeof(SystemEventBoostServiceUI),
    globalUI: typeof(CountdownOverlayWidget),
    launchOrder: 200)]
public class SystemEventBoostService(
    XmlFunctionCaller functionService,
    Interactor<SystemEventBoostService> interactor,
    ConfigurationSystem configurationSystem,
    ILogger<SystemEventBoostService> logger) :
    ChatBehaviour,
    IConfigurable<SystemEventBoostServiceConfig>,
    ISystemEventService
{
    SystemEventBoostServiceConfig _configuration = null!;
    public SystemEventBoostServiceConfig Configuration
    {
        get => _configuration;
        set
        {
            SystemEventBoostServiceConfig old = _configuration;
            _configuration = value;
            // UI 面板保存配置后,框架直接替换 Configuration 对象(热应用),但模块实例不会重建、
            // OnStart 不会重跑,nextActivityTime 等调度状态仍停留在旧配置计算的时刻:
            // 挂件倒计时与实际间隔不符,且要等旧报点时刻到达才恢复(期间反复改配置也一直不对)。
            // 这里检测调度相关配置的变化,立即按新配置重新对齐下次活跃时间。
            //
            // 注意不要加 IsStarted 守卫:角色激活时模块先以默认配置跑完 OnAwake/OnStart(并可能已排出
            // 第一个周期的时刻),框架随后才把磁盘里的真实配置热应用进来,而此刻 IsStarted 往往还没置位。
            // 若跳过重排,激活后的第一个周期就会沿用"默认配置算出的间隔/峰谷抑制状态"(要等下一个 tick
            // 才纠正,表现为刚激活时挂件间隔与配置不符)。ResetScheduling 只改时刻、无副作用,提前执行安全。
            // (若框架是「就地更新配置字段」而非替换对象,setter 不会被调用,由 TickActivity 的配置指纹校验兜底)
            if (old != null && old != value && SchedulingConfigChanged(old, value))
            {
                try
                {
                    ResetScheduling();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "配置变更后重置调度失败");
                }
            }
        }
    }

    #region 运行时状态

    DateTime nextActivityTime;        // 下次自主活跃报点时间（周期/游戏/撒娇共用调度）
    DateTime lastScheduleTime;        // 当前报点间隔的起点（挂件进度环用；仅周期类路径记录）
    DateTime? awakeReminderTime;      // Awake 定点报时时间
    string awakeReminderRemark = "";
    int continuousTimerCount;         // 连续触发次数（官方翻倍机制）
    bool forceNextPoke;               // 主人手动催促：下次报点绕过高峰抑制
    DateTime? sleepUntil;             // 睡眠结束时间（null=未设置倒计时）
    bool sleepWaitForUser;            // 睡眠等待主人消息唤醒
    bool groupSilenceFlag;            // 本次Chat是否被睡眠静默占位改写（防误判唤醒）
    bool wasSleeping;                 // 上一次检测的睡眠状态（用于睡眠结束时的唤醒提示）
    DateTime lastUserInteractionTime; // 最近真实互动时间（撒娇智能节流）
    string? scheduleFingerprint;      // 当前排期所用的「调度相关配置」指纹（见 BuildScheduleFingerprint）

    // ===== 框架更新循环 =====
    // 4.5.3/4.5.4 曾用「自有周期循环 + 停摆看门狗」绕过框架的一个缺陷：
    // ChatActivity.StartTimer 边遍历 container.Instances 边 await 各模块 UpdateAsync，
    // 而插件加载/卸载、角色配置变更会在别的线程就地增删该集合，撞上就抛 Collection was modified
    // 并被吞掉，该角色的更新循环永久停止且永不重启（报点停摆、重载插件后挂件不回来、其它模块失去 OnStart）。
    // Alife.Client 4.5.0 已把容器遍历与增删用 containerLock 串行化（ChatActivity.StartTimer / OnModulesLoaded /
    // OnModulesUnloaded / OnCharacterChangedAsync 全部加锁），根因消除，故 4.6.0 撤掉自有循环、
    // 周期工作回归框架的 OnUpdate；只保留"长时间收不到更新回调"的只读告警（见 LastTickTime）。
    DateTime lastTickTime;            // 框架更新循环最后一次调用本模块 OnUpdate 的时间（挂件据此告警）

    // ===== 倒计时重置来源（接管 ISystemEventService 后由 QQ 插件驱动）=====
    DateTime lastTimerResetTime;      // 最近一次重置倒计时的时刻（双路径去重用）
    string lastResetSource = "";      // 最近一次重置倒计时的来源描述（供 UI / 挂件显示）

    // ===== 工作模式运行时状态 =====
    WorkPhase workPhase;              // 工作模式阶段
    string workTask = "";             // 当前工作任务描述
    List<WorkStep> workSteps = [];    // 步骤队列
    int workCurrentIndex = -1;        // 当前执行中的步骤索引
    DateTime workPhaseStartTime;      // 当前阶段开始时间（用于超时判断）
    DateTime workStepStartTime;       // 当前步骤开始时间（用于单步超时判断）
    StringBuilder workPlanBuffer = new(); // 收集 AI 输出的计划文本
    readonly HashSet<Guid> workSessionTaskIds = []; // 本次工作会话期间创建的一次性任务（可选在结束时清理）

    bool IsSleeping => sleepWaitForUser || (sleepUntil != null && sleepUntil > DateTime.Now);
    bool IsGameModeActive => Configuration.MasterGameMode && Configuration.GameModeEnabled;
    bool IsCuteModeActive => Configuration.MasterCuteMode && Configuration.CuteModeEnabled;
    bool IsDndActive => Configuration.MasterDndMode && Configuration.DndModeEnabled;
    bool IsWorkModeActive => Configuration.MasterWorkMode
        && workPhase is WorkPhase.Planning or WorkPhase.Executing or WorkPhase.Paused;

    #endregion

    #region UI 展示数据（供配置面板实时显示）

    /// <summary>距下次自主活跃报点的剩余时间（供 UI 倒计时显示）</summary>
    public DateTime NextActivityTime => nextActivityTime;

    /// <summary>是否处于睡眠（供 UI 显示）</summary>
    public bool IsSleepingNow => IsSleeping;

    /// <summary>睡眠结束时间（null=等待主人消息唤醒）</summary>
    public DateTime? SleepEndTime => sleepUntil;

    /// <summary>是否等待主人消息唤醒</summary>
    public bool SleepAwaitingUser => sleepWaitForUser;

    /// <summary>工作模式阶段</summary>
    public WorkPhase CurrentWorkPhase => workPhase;

    /// <summary>当前工作步骤（1 基；0=未开始）</summary>
    public int CurrentWorkStep => workCurrentIndex >= 0 ? workCurrentIndex + 1 : 0;

    /// <summary>工作步骤总数</summary>
    public int TotalWorkSteps => workSteps.Count;

    /// <summary>当前工作任务</summary>
    public string CurrentWorkTask => workTask;

    /// <summary>Awake 定点报时时间</summary>
    public DateTime? AwakeTime => awakeReminderTime;

    /// <summary>距最近一次框架更新回调的秒数（供 UI / 挂件检测更新循环停摆）</summary>
    public double TickAgeSeconds => (DateTime.Now - lastTickTime).TotalSeconds;

    /// <summary>当前报点间隔已走过的百分比（0-100；睡眠/工作等无间隔概念的场景返回 0）</summary>
    public double IntervalProgressPercent
    {
        get
        {
            double total = (nextActivityTime - lastScheduleTime).TotalMilliseconds;
            if (total <= 0)
                return 0;
            return Math.Clamp((DateTime.Now - lastScheduleTime).TotalMilliseconds / total * 100, 0, 100);
        }
    }

    /// <summary>最近一次倒计时重置的来源描述（如「群聊/私聊消息（QQ插件）」「主人对话」）</summary>
    public string LastResetSource => lastResetSource;

    /// <summary>最近一次倒计时重置的时刻</summary>
    public DateTime? LastResetTime => lastTimerResetTime == default ? null : lastTimerResetTime;

    /// <summary>当前调度状态描述（供 UI 显示报点类型/抑制原因）</summary>
    public string ActivityStatus => ClassifyActivity().Text;

    /// <summary>角色名（倒计时挂件注册键）</summary>
    public string CharacterName => Character?.Name ?? "";

    /// <summary>挂件状态码：period/sleep/work/game/cute/dnd/peak（决定挂件配色）</summary>
    public string OverlayStateCode => ClassifyActivity().Code;

    /// <summary>模式优先级判定（ActivityStatus 与 OverlayStateCode 的统一来源，避免两份逻辑改一处漏一处）</summary>
    (string Code, string Text) ClassifyActivity()
    {
        //框架更新循环长时间没有回调本模块：周期工作已停摆。
        //（4.5.0 已从根上修复容器并发导致的循环中断，这里只作只读告警，不再自己起循环兜底）
        if (TickAgeSeconds > 30)
            return ("stall", "框架更新循环已停止·请停用后重新激活");
        if (IsWorkModeActive)
            return ("work", $"工作模式·{workPhase}");
        if (IsSleeping)
            return ("sleep", sleepWaitForUser ? "睡眠中·等待主人消息" : "睡眠中·倒计时中");
        if (Configuration.MasterPeakMode && Configuration.PeakModeEnabled)
        {
            if (IsPeakHour(DateTime.Now))
                return ("peak", "高峰时段·自主活跃已暂停");
            //节假日豁免：峰谷不生效（谷价），状态按正常周期报点展示，仅文案提示
            if (Configuration.PeakHolidayExempt && TodayHolidayName is string holiday)
                return ("period", $"节假日（{holiday}）·峰谷已豁免");
        }
        if (IsGameModeActive)
            return ("game", "游戏陪伴");
        if (IsCuteModeActive && !IsDndActive)
            return ("cute", "撒娇");
        if (IsDndActive)
            return ("dnd", "勿扰");
        return ("period", "周期报点");
    }

    /// <summary>挂件『催一下』：立即触发一次自主活跃（用户主动操作优先于各模式压制）</summary>
    public void TriggerActivityNow()
    {
        if (IsSleeping)
        {
            WakeUp(silent: false);
            Console.WriteLine($"[主动事件增强] 挂件催一下：唤醒睡眠角色 [{CharacterName}]");
            return;
        }

        if (IsWorkModeActive)
        {
            //工作模式下不抢调度，改为催促当前步骤进度
            interactor.Poke("(主人在挂件上点了「催促进度」：请汇报当前步骤进展，或尽快完成当前步骤后继续)");
            Console.WriteLine($"[主动事件增强] 挂件催一下：[{CharacterName}] 工作模式中，已发送催促");
            return;
        }

        //高峰抑制场景：置绕过标记（仅当当前确实被抑制，避免标记残留到之后的高峰期造成计划外报点）
        if (Configuration.MasterPeakMode && Configuration.PeakModeEnabled && IsPeakHour(DateTime.Now))
            forceNextPoke = true;
        nextActivityTime = DateTime.Now;
        lastScheduleTime = DateTime.Now;
        Console.WriteLine($"[主动事件增强] 挂件催一下：[{CharacterName}] 下次自主活跃已置为现在");
    }

    /// <summary>设置该角色的倒计时胶囊显示/隐藏（挂件详情卡与设置页开关共用同一状态，立即生效并落盘）</summary>
    public void SetOverlayVisible(bool visible)
    {
        if (Configuration.ShowCountdownOverlay == visible)
            return;
        Configuration.ShowCountdownOverlay = visible;
        SaveConfig();
        Console.WriteLine($"[主动事件增强] 挂件：[{CharacterName}] 倒计时胶囊已{(visible ? "显示" : "隐藏")}");
    }

    /// <summary>设置胶囊自由位置（null=归位编组）。随角色配置持久化，重启 Alife 保留。</summary>
    public void SetOverlayPillPosition(double? x, double? y)
    {
        Configuration.OverlayPillX = x;
        Configuration.OverlayPillY = y;
        SaveConfig();
        Console.WriteLine($"[主动事件增强] 挂件位置已保存：[{CharacterName}] " + (x == null ? "归位" : $"({x:0},{y:0})"));
    }

    /// <summary>设置胶囊显示模式：true=显示下次活跃时刻，false=显示剩余倒计时。随角色配置持久化。</summary>
    public void SetOverlayClockMode(bool clockMode)
    {
        if (Configuration.OverlayClockMode == clockMode)
            return;
        Configuration.OverlayClockMode = clockMode;
        SaveConfig();
    }

    /// <summary>挂件上点击胶囊：在「剩余倒计时 / 时刻」之间切换</summary>
    public void ToggleOverlayClockMode() => SetOverlayClockMode(Configuration.OverlayClockMode == false);

    /// <summary>构建挂件展示快照（时间用 epoch 毫秒，挂件端只做差值，不受时钟同步影响）</summary>
    public OverlayCharSnapshot BuildOverlaySnapshot() => new()
    {
        Name = CharacterName,
        StateCode = OverlayStateCode,
        StateText = ActivityStatus,
        NextMs = new DateTimeOffset(NextActivityTime).ToUnixTimeMilliseconds(),
        IntervalStartMs = new DateTimeOffset(lastScheduleTime).ToUnixTimeMilliseconds(),
        IntervalEndMs = new DateTimeOffset(NextActivityTime).ToUnixTimeMilliseconds(),
        SleepEndMs = SleepEndTime == null ? null : new DateTimeOffset(SleepEndTime.Value).ToUnixTimeMilliseconds(),
        SleepAwaitingUser = SleepAwaitingUser,
        WorkStep = CurrentWorkStep,
        WorkTotal = TotalWorkSteps,
        WorkTask = CurrentWorkTask,
        AwakeMs = AwakeTime == null ? null : new DateTimeOffset(AwakeTime.Value).ToUnixTimeMilliseconds(),
        TickAgeSeconds = TickAgeSeconds,
        ShowRing = Configuration.OverlayShowRing,
        ShowCharName = Configuration.OverlayShowCharName,
        ClockMode = Configuration.OverlayClockMode,
        ScalePercent = Configuration.OverlayScalePercent,
        OpacityPercent = Configuration.OverlayOpacityPercent,
        AvoidOtherWidgets = Configuration.OverlayAvoidOtherWidgets,
        FreeX = Configuration.OverlayPillX,
        FreeY = Configuration.OverlayPillY,
    };

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        ChatBot.ChatSent += OnChatSent;
        ChatBot.ChatSend += OnChatSend;
        ChatBot.ChatReceived += OnChatReceived;
        ChatBot.ChatFinishedAsync += OnChatFinishedAsync;

        //挂件注册放在唤醒阶段（OnAwake 早于 OnStart，且重载插件后能立刻重新注册）
        lastUserInteractionTime = DateTime.Now;
        nextActivityTime = DateTime.Now;
        lastScheduleTime = DateTime.Now;
        lastTickTime = DateTime.Now;
        CountdownOverlayManager.Register(this);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        lastUserInteractionTime = DateTime.Now;
        nextActivityTime = DateTime.Now;
        lastScheduleTime = DateTime.Now;
        lastTickTime = DateTime.Now;
        awakeReminderTime = null;

        //按模式总开关过滤可用函数：关闭的模式不暴露给 AI
        var enabledFunctions = new XmlHandler(this).Functions
            .Where(f => IsModeFunctionAllowed(f.Name))
            .ToList();

        //核心硬规则（常时注入）：总纲 + 触发词速查，按总开关过滤
        StringBuilder hard = new();
        hard.AppendLine("你拥有「主动事件增强」能力：可自主创建定时任务、切换报点模式、调整活跃节奏、开关陪伴模式，并进入「工作模式」像专业 Agent 一样执行任务。");
        hard.AppendLine("当主人用自然语言表达需求时，主动调用本服务对应函数响应。触发词速查（仅列已开启模式）：");
        if (Configuration.MasterGameMode)
            hard.AppendLine("- 「进入游戏模式/陪我打游戏」→ 游戏陪伴");
        if (Configuration.MasterSleepMode)
            hard.AppendLine("- 「我要睡觉了/晚安/明天X点叫我」→ 睡眠");
        if (Configuration.MasterDndMode)
            hard.AppendLine("- 「别吵我/勿扰/安静点」→ 勿扰");
        if (Configuration.MasterCuteMode)
            hard.AppendLine("- 「开启撒娇」→ 撒娇");
        if (Configuration.MasterWorkMode)
            hard.AppendLine("- 明确工作任务（写代码/改文件/跑脚本/查资料/多步任务）→ 进入工作模式 <EnterWorkMode/>：先 <plan> 计划，每步 <WorkStepDone/> 推进，全部完成再汇报。不要跳过工作模式零散执行。");
        string hardRules = hard.ToString();

        //详细规则：只放「函数文档里没有的全局行为规则」，按总开关过滤，避免与函数 Description 重复
        StringBuilder detail = new();
        if (Configuration.MasterScheduledTask)
            detail.AppendLine("- 定时任务：循环任务按「每天/每周几 + 时分」触发；临时任务按「N分钟后」触发一次，触发后由系统自动删除，不需要你手动清理");
        if (Configuration.MasterWorkMode || Configuration.MasterSleepMode || Configuration.MasterDndMode || Configuration.MasterPeakMode || Configuration.MasterGameMode || Configuration.MasterCuteMode)
            detail.AppendLine("- 模式优先级：工作模式 > 睡眠 > 勿扰 > 峰谷 > 游戏陪伴 > 撒娇；工作/睡眠期间不进行其他主动报点");
        if (Configuration.MasterSleepMode)
            detail.AppendLine("- 睡眠：倒计时结束自动恢复；开启「群聊静默」后睡眠期间群聊消息被替换为占位，AI 不回复、不打扰");
        if (Configuration.MasterDndMode)
            detail.AppendLine("- 勿扰：自主活动但禁止 speak/qchat 等打扰标签，可按「允许做的事」清单自娱自乐");
        if (Configuration.MasterPeakMode)
            detail.AppendLine("- 峰谷：高峰时段（默认北京时间 9-12/14-18、周一至周五）自动暂停自主活跃，空闲时段恢复；"
                + "法定节假日（如国庆/春节假期）全天豁免、不抑制自主活跃（此时按谷价计费更划算）");
        if (Configuration.MasterReportMode)
            detail.AppendLine("- 报点模式：可创建多个自定义报点模式；每个模式可单独开启「独立活跃机制」使用自己的报点间隔，切换模式即切换报点节奏与提示词");
        if (Configuration.MasterCuteMode)
            detail.AppendLine("- 撒娇：长时间无互动会自动拉长活跃间隔，避免频繁空转烧 token");
        if (Configuration.MasterWorkMode)
            detail.AppendLine("- 工作模式：逐步执行时配合 <python/> <process/> <file/> <browser/> <smartwebsearch/> <skill/> <AlifeMcp/> 完成实际任务；卡住时用 <SkipWorkStep/> 跳过");
        string detailedRules = detail.ToString();

        string implicitNote = Configuration.ImplicitInjection
            ? "\n- 隐式注入已开启：需要调用本服务函数前，先调用 <systemeventboost/> 加载完整函数说明。"
            : "";

        XmlHandler xmlHandler = new("SystemEventBoost") {
            Description = "主动事件增强：定时任务、报点模式切换、游戏陪伴/睡眠/勿扰/撒娇等自主活动控制。",
            Explanation = Configuration.ImplicitInjection ? detailedRules : null,
            Functions = enabledFunctions,
        };
        functionService.RegisterHandler(xmlHandler,
            Configuration.ImplicitInjection ? DocumentMode.Implicit : DocumentMode.Explicit,
            DestroyCancellationToken);

        //注册 plan 占位标签：仅工作模式开启时注册，避免 XmlFunctionCaller 报"环境中没有<plan/>"。
        if (Configuration.MasterWorkMode)
        {
            XmlHandler planHandler = new("SystemEventBoostPlan");
            planHandler.Functions.Add(new XmlFunction {
                Name = "plan",
                Mode = FunctionMode.Content,
                ContentName = "Content",
                Invoker = (_, _) => Task.CompletedTask,
            });
            functionService.RegisterHandlerWithoutDocument(planHandler, DestroyCancellationToken);
        }

        if (Configuration.ImplicitInjection)
            interactor.Prompt(hardRules + implicitNote);
        else
            interactor.Prompt(hardRules + detailedRules);

        //发送系统启动消息（继承官方行为）
        {
            OccupationMarker? occupationMarker = ChatBot.LanguageModel.GetThinkingRequester().Rent("程序启动");

            if (ChatBot.ChatHistory.All(content => content.Role != AuthorRole.Assistant))
            {
                interactor.ChatAsync("""
                                     角色已激活：
                                     这是你第一次苏醒，初来乍到这个陌生环境，学习利用上下文中的工具了解这个世界。
                                     此外最重要的一件事，就是现在用上你丰富的能力，先向用户华丽的打个招呼吧！
                                     """)
                    .ContinueWith(_ => occupationMarker?.Dispose());
            }
            else
            {
                interactor.ChatAsync($"程序已重启。{Configuration.StartPrompt}")
                    .ContinueWith(_ => occupationMarker?.Dispose());
            }
        }

        //对话面板倒计时挂件已在 OnAwake 注册（不依赖框架更新循环）

        //节假日表在线校准（可选开关，默认关闭；失败静默忽略，不影响离线使用）
        TryAutoFetchHolidays();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 框架每秒回调：周期工作（报点 / 定时任务 / 睡眠 / Awake / 工作模式）都在这里推进。
    /// 4.6.0 起不再自建循环：Alife.Client 4.5.0 已把容器遍历与增删串行化，循环中断的根因消除；
    /// 这里只记录时间戳，供挂件/配置页在长时间收不到回调时给出只读告警。
    /// </summary>
    protected override Task OnUpdate()
    {
        lastTickTime = DateTime.Now;
        TickWorkMode();
        TickAwakeReminder();
        TickScheduledTasks();
        TickActivity();
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        ChatBot.ChatSent -= OnChatSent;
        ChatBot.ChatSend -= OnChatSend;
        ChatBot.ChatReceived -= OnChatReceived;
        ChatBot.ChatFinishedAsync -= OnChatFinishedAsync;

        CountdownOverlayManager.Unregister(this);

        await interactor.ChatAsync($"程序关闭中。{Configuration.DestroyPrompt}");
    }

    #endregion

    #region 系统事件接口（替代官方「主动事件」插件）

    /// <summary>
    /// Alife.Function.SystemEvent 的 <see cref="ISystemEventService"/> 实现：
    /// QQ 插件（v4.4.0+）在收到群聊消息、以及非主人的私聊消息时调用它，用于重置周期报点倒计时。
    /// 语义与官方实现一致（清零连续触发次数并重新计时），另外：
    /// - 睡眠中不重置（避免群消息打断睡眠，与官方"睡眠绝对压制"的定位一致）；
    /// - 遵循本插件的「群聊消息重置倒计时」开关（关闭时群聊/他人私聊不打断自主活跃节奏）；
    /// - 与 <see cref="OnChatSent"/> 的文本兜底识别共享 800ms 去重窗口，两条路径不会互相叠加。
    /// </summary>
    public void ResetTimer()
    {
        if (IsSleeping)
            return;
        if (Configuration.ResetCountdownOnGroupMessage == false)
            return;
        if (lastTimerResetTime != default && (DateTime.Now - lastTimerResetTime).TotalMilliseconds < 800)
            return;   //QQ 插件事件与文本兜底识别会先后各触发一次，这里去重

        lastTimerResetTime = DateTime.Now;
        lastResetSource = "群聊/私聊消息（QQ 插件）";
        continuousTimerCount = 0;
        lastUserInteractionTime = DateTime.Now;
        NextActivity();
    }

    #endregion

    #region 事件处理

    void OnChatSent(string message)
    {
        bool isSilencedGroup = groupSilenceFlag;
        groupSilenceFlag = false;

        bool isPoke = message.Contains(ChatBot.PokeMessageTag);
        bool isGroup = IsGroupPokeMessage(message);

        //系统报点 / 被睡眠静默的群聊消息：不视为真实互动
        if (isPoke && !isGroup)
            return;
        if (isSilencedGroup)
            return;

        //群聊消息：是否重置周期报点由开关控制（默认开启；睡眠中不重置，避免群消息打断睡眠）。
        //注意：QQ 插件 v4.4.0 起会通过 ISystemEventService.ResetTimer 直接通知本插件，
        //这里的文本识别只作为"QQ 插件版本较旧 / 消息来自其它渠道"时的兜底，两条路径由 800ms 窗口去重。
        if (isGroup)
        {
            ResetTimer();
            return;
        }

        //真实用户消息（主人对话）：重置周期报点 + 记录互动 + 唤醒睡眠
        continuousTimerCount = 0;
        lastUserInteractionTime = DateTime.Now;
        lastResetSource = "主人对话";
        lastTimerResetTime = DateTime.Now;
        NextActivity();
        WakeUp(silent: true);
    }

    string OnChatSend(string message)
    {
        //睡眠模式：将群聊消息改写为静默占位，强约束 AI 不回复
        if (IsSleeping && Configuration.SleepSilentGroup && IsGroupPokeMessage(message))
        {
            groupSilenceFlag = true;
            return Configuration.SleepGroupSilencePrompt;
        }
        return message;
    }

    void OnChatReceived(string content)
    {
        //工作模式规划阶段：收集 AI 输出的计划文本
        if (workPhase == WorkPhase.Planning)
        {
            workPlanBuffer.Append(content);
            //提前检测到 </plan> 立即解析，不必等对话结束
            if (workPlanBuffer.ToString().Contains("</plan>", StringComparison.OrdinalIgnoreCase))
            {
                TryParsePlan(workPlanBuffer.ToString());
                workPlanBuffer.Clear();
            }
        }
    }

    Task OnChatFinishedAsync(ChatContext chatContext)
    {
        //对话结束：若仍在规划阶段，尝试解析（兼容未使用 </plan> 标签的情况）
        if (workPhase == WorkPhase.Planning)
        {
            TryParsePlan(workPlanBuffer.ToString());
            workPlanBuffer.Clear();
        }
        return Task.CompletedTask;
    }

    static bool IsGroupPokeMessage(string message)
        => message.Contains(ChatBot.PokeMessageTag) && message.Contains("[群聊消息(");

    #endregion

    #region 工作模式调度

    void TickWorkMode()
    {
        switch (workPhase)
        {
            case WorkPhase.Planning:
                //规划超时：提醒 AI 重新输出计划
                if ((DateTime.Now - workPhaseStartTime).TotalSeconds > Configuration.WorkPlanTimeoutSeconds)
                {
                    workPhaseStartTime = DateTime.Now;
                    interactor.Poke("(工作模式：尚未解析到有效计划。请重新用 <plan> 包裹的编号列表输出你的执行计划，如：<plan>1. 分析需求\n2. 编写代码\n3. 运行验证\n4. 汇报结果</plan>)");
                }
                break;

            case WorkPhase.Executing:
                if (workCurrentIndex < 0 || workCurrentIndex >= workSteps.Count)
                {
                    FinishWorkMode(autoReport: Configuration.WorkAutoReport);
                    return;
                }
                //单步超时：提醒 AI 报告进度或跳过
                if ((DateTime.Now - workStepStartTime).TotalSeconds > Configuration.WorkStepTimeoutSeconds)
                {
                    workStepStartTime = DateTime.Now;
                    var step = workSteps[workCurrentIndex];
                    interactor.Poke($"(工作模式提醒：步骤{step.Index}/{workSteps.Count}「{step.Description}」已执行较长时间。若已完成请调用 <{nameof(WorkStepDone)}/> 继续下一步；若卡住请说明进度或调用 <{nameof(SkipWorkStep)}/> 跳过)");
                }
                break;
        }
    }

    /// <summary>进入工作模式：设定任务，进入规划阶段</summary>
    void EnterWorkModeInternal(string task)
    {
        workTask = task.Trim();
        workSteps.Clear();
        workCurrentIndex = -1;
        workPlanBuffer.Clear();
        workSessionTaskIds.Clear();
        workPhase = WorkPhase.Planning;
        workPhaseStartTime = DateTime.Now;
        nextActivityTime = DateTime.Now.AddSeconds(10);

        Console.WriteLine($"[工作模式] 进入工作模式，任务：{workTask}");

        interactor.Poke($"""
            (工作模式已启动。任务：{workTask})

            请先输出你的执行计划，要求：
            - 用 <plan> 标签包裹，每行一个步骤，编号列表，例如：
            <plan>
            1. 分析任务需求，明确目标
            2. 拆解为可执行的子任务并选择合适工具
            3. 逐步执行并验证结果
            4. 汇总汇报完成情况
            </plan>
            - 步骤要具体、可执行、数量适中（{Configuration.WorkMaxSteps} 步以内）
            - 输出计划后系统会自动引导你逐步执行，每步完成后调用 <{nameof(WorkStepDone)}/> 进入下一步

            可用工具：{Configuration.WorkInjectedTools}
            """);
    }

    /// <summary>解析 AI 输出的计划，成功后进入执行阶段</summary>
    void TryParsePlan(string output)
    {
        if (workPhase != WorkPhase.Planning)
            return;

        List<string> steps = ParsePlan(output);
        if (steps.Count == 0)
        {
            //没解析到步骤，保持规划阶段等下一轮（超时由 TickWorkMode 处理）
            workPlanBuffer.Clear();
            return;
        }

        //限制最大步骤数
        if (steps.Count > Configuration.WorkMaxSteps)
            steps = steps.Take(Configuration.WorkMaxSteps).ToList();

        workSteps = steps.Select((desc, i) => new WorkStep { Index = i + 1, Description = desc }).ToList();
        workCurrentIndex = -1;
        workPhase = WorkPhase.Executing;
        workPhaseStartTime = DateTime.Now;

        Console.WriteLine($"[工作模式] 计划解析成功，共 {workSteps.Count} 步：{string.Join(" | ", workSteps.Select(s => s.Description))}");

        interactor.Poke($"(已解析计划，共 {workSteps.Count} 步，开始逐步执行)");
        AdvanceWorkStep();
    }

    /// <summary>把 AI 输出的自然语言计划解析为步骤列表</summary>
    static List<string> ParsePlan(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var result = new List<string>();
        string body = output;

        //优先提取 <plan>...</plan> 块
        int planStart = body.IndexOf("<plan>", StringComparison.OrdinalIgnoreCase);
        int planEnd = body.IndexOf("</plan>", StringComparison.OrdinalIgnoreCase);
        if (planStart >= 0 && planEnd > planStart)
            body = body.Substring(planStart + 6, planEnd - planStart - 6);

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            //只把以编号/项目符号开头的行视为步骤，忽略 AI 的引导语等普通文本
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+[\.、\)\]\-]|[>\-•*])\s*(.*)$");
            if (!m.Success)
                continue;
            string cleaned = m.Groups[2].Value.Trim();
            if (cleaned.Length > 0 && cleaned != "</plan>" && cleaned != "<plan>")
                result.Add(cleaned);
        }
        return result;
    }

    /// <summary>推进到下一步（完成当前步后调用）</summary>
    void AdvanceWorkStep()
    {
        if (workPhase != WorkPhase.Executing)
            return;

        //找下一个未完成的步骤
        int next = -1;
        for (int i = workCurrentIndex + 1; i < workSteps.Count; i++)
        {
            if (workSteps[i].Status == WorkStepStatus.Pending)
            {
                next = i;
                break;
            }
        }

        if (next == -1)
        {
            FinishWorkMode(autoReport: Configuration.WorkAutoReport);
            return;
        }

        workCurrentIndex = next;
        var step = workSteps[next];
        step.Status = WorkStepStatus.Executing;
        step.StartedAt = DateTime.Now;
        workStepStartTime = DateTime.Now;
        nextActivityTime = DateTime.Now.AddSeconds(5);

        Console.WriteLine($"[工作模式] 步骤 {step.Index}/{workSteps.Count}：{step.Description}");

        interactor.Poke($"""
            [工作模式] 步骤 {step.Index}/{workSteps.Count}：{step.Description}

            (请执行此步骤。完成本步后调用 <{nameof(WorkStepDone)}/> 进入下一步；若该步骤无法完成可调用 <{nameof(SkipWorkStep)}/> 跳过。可用工具：{Configuration.WorkInjectedTools})
            """);
    }

    /// <summary>AI 标记当前步骤完成</summary>
    void CompleteCurrentStep()
    {
        if (workPhase != WorkPhase.Executing)
            return;
        if (workCurrentIndex >= 0 && workCurrentIndex < workSteps.Count)
            workSteps[workCurrentIndex].Status = WorkStepStatus.Completed;
        AdvanceWorkStep();
    }

    /// <summary>跳过当前步骤</summary>
    void SkipCurrentStep()
    {
        if (workPhase != WorkPhase.Executing)
            return;
        if (workCurrentIndex >= 0 && workCurrentIndex < workSteps.Count)
            workSteps[workCurrentIndex].Status = WorkStepStatus.Skipped;
        AdvanceWorkStep();
    }

    /// <summary>全部步骤完成/中止，退出工作模式</summary>
    void FinishWorkMode(bool autoReport)
    {
        if (workPhase == WorkPhase.None)
            return;

        int total = workSteps.Count;
        int done = workSteps.Count(s => s.Status == WorkStepStatus.Completed);
        int skipped = workSteps.Count(s => s.Status == WorkStepStatus.Skipped);
        string finishedTask = workTask;
        workPhase = WorkPhase.None;
        workSteps.Clear();
        workCurrentIndex = -1;
        nextActivityTime = DateTime.Now.AddSeconds(5);

        //可选兜底：清理本次工作会话期间创建、且尚未触发的一次性任务（避免残留僵尸任务）
        int cleaned = CleanWorkSessionTasks();

        Console.WriteLine($"[工作模式] 结束，完成 {done} 步，跳过 {skipped} 步，清理会话临时任务 {cleaned} 条");

        if (autoReport)
            interactor.Poke($"(工作模式结束：任务「{finishedTask}」已完成 {done}/{total} 步"
                + $"{(cleaned > 0 ? $"，并清理了 {cleaned} 条本次会话创建的未触发临时任务" : "")}。请向主人简要汇报成果与遗留事项)");
    }

    /// <summary>
    /// 结束工作会话时清理会话内创建的一次性任务（需开启「结束时清理会话临时任务」开关），返回清理条数。
    /// 无论开关是否开启都会清空会话记录，避免影响下一次工作会话。
    /// </summary>
    int CleanWorkSessionTasks()
    {
        if (workSessionTaskIds.Count == 0)
            return 0;

        int removed = 0;
        if (Configuration.CleanWorkSessionTasksOnExit)
        {
            removed = Configuration.ScheduledTasks.RemoveAll(t => workSessionTaskIds.Contains(t.Id));
            if (removed > 0)
                SaveConfig();
        }
        workSessionTaskIds.Clear();
        return removed;
    }

    #endregion

    #region 定时任务调度

    void TickAwakeReminder()
    {
        if (awakeReminderTime != null && DateTime.Now >= awakeReminderTime)
        {
            string remark = awakeReminderRemark;
            awakeReminderTime = null;
            awakeReminderRemark = "";
            if (!IsSleeping)
                interactor.Poke($"AWake报点：{remark}");
        }
    }

    void TickScheduledTasks()
    {
        if (Configuration.MasterScheduledTask == false)
            return; //定时任务总开关关闭，不触发任何任务

        bool dirty = false;
        List<ScheduledTask>? firedOneTime = null; //一次性任务触发后直接从列表移除（不再留禁用僵尸条目）
        foreach (ScheduledTask task in Configuration.ScheduledTasks)
        {
            if (task.Enabled == false)
                continue;
            if (IsSleeping)
                continue; //睡眠绝对压制：不触发任何定时任务

            bool due = false;
            if (task.Type == ScheduledTaskType.Recurring)
            {
                //按"当日计划时刻已到且今天未触发"判断（非精确分钟匹配）：
                //卡顿/重启错过触发分钟时，30 分钟宽限期内补触发；超宽限视为已过，防止晚启动意外补触发
                DateTime now = DateTime.Now;
                string todayKey = now.ToString("yyyyMMdd");
                if (task.LastTriggerDate != todayKey && task.IsDayMatched(now.DayOfWeek))
                {
                    DateTime scheduled = now.Date.AddHours(task.Hour).AddMinutes(task.Minute);
                    if (now >= scheduled && now < scheduled.AddMinutes(30))
                    {
                        task.LastTriggerDate = todayKey;
                        due = true;
                        dirty = true;
                    }
                    else if (now >= scheduled.AddMinutes(30))
                    {
                        task.LastTriggerDate = todayKey; //已超宽限：标记今日已过，避免反复判断与补触发
                        dirty = true;
                    }
                }
            }
            else if (task.TriggerTimeUtc != null && DateTime.UtcNow >= task.TriggerTimeUtc.Value)
            {
                (firedOneTime ??= []).Add(task); //先收集，循环结束后统一移除（遍历中不能改动集合）
                due = true;
                dirty = true;
            }

            if (due)
                interactor.Poke(task.Type == ScheduledTaskType.OneTime
                    ? $"[定时任务:{task.Name}] {task.Message}\n(该临时任务已触发完成，已自动从任务列表移除，无需再手动删除)"
                    : $"[定时任务:{task.Name}] {task.Message}");
        }

        //一次性任务：触发即删除（含工作会话记录），避免列表里堆积永远看不到、也删不掉的僵尸条目
        if (firedOneTime != null)
        {
            foreach (ScheduledTask fired in firedOneTime)
            {
                Configuration.ScheduledTasks.Remove(fired);
                workSessionTaskIds.Remove(fired.Id);
            }
        }

        //触发状态落盘：循环任务的防重标记与一次性任务的删除跨重启生效
        if (dirty)
            SaveConfig();
    }

    #endregion

    #region 自主活跃调度（周期/游戏/撒娇/勿扰/峰谷叠加）

    void TickActivity()
    {
        //配置指纹校验：框架既可能整体替换 Configuration 对象（走 setter 热应用），
        //也可能「就地更新配置对象的字段」（不走 setter）——后者会让已排定的下次活跃时刻停留在旧参数上，
        //典型表现是角色刚激活时第一个周期仍按默认配置的间隔/峰谷抑制状态走。每个 tick 比对指纹即可立即纠正。
        if (scheduleFingerprint != BuildScheduleFingerprint())
            ResetScheduling();

        //工作模式：最高优先级，专注任务执行，不进行其他主动报点（由 TickWorkMode 推进步骤）
        if (IsWorkModeActive)
        {
            nextActivityTime = DateTime.Now.AddSeconds(5);
            return;
        }

        //睡眠：绝对压制，优先于一切时间检查（睡眠期间持续轮询刷新，确保倒计时结束后立即恢复）
        if (IsSleeping)
        {
            wasSleeping = true;
            nextActivityTime = DateTime.Now.AddSeconds(10);
            return;
        }

        if (DateTime.Now < nextActivityTime)
            return;

        //睡眠刚结束：发送睡醒提示，恢复正常活动
        if (wasSleeping)
        {
            wasSleeping = false;
            interactor.Poke("(已睡醒，恢复正常自主活动)");
        }

        (TimeSpan interval, string? text) = ComposeActivity();

        if (string.IsNullOrEmpty(text))
        {
            nextActivityTime = DateTime.Now.Add(interval); //高峰抑制等：不Poke，稍后重查
            lastScheduleTime = DateTime.Now;
            return;
        }

        if (functionService.IsIdle == false)
        {
            nextActivityTime = DateTime.Now.Add(interval); //与AI活动碰撞，延迟重试
            lastScheduleTime = DateTime.Now;
            return;
        }

        interactor.Poke(text);
        continuousTimerCount++;
        //用递增后的连续次数重新合成下次间隔：
        //报点 N 完成后，下一次报点直接按「已连续触发 N 次」翻倍，而不是等 N+1 次报点才翻倍
        //(旧逻辑用递增前的 count 设置 next，导致报点事件间隔恒为基础间隔，翻倍延迟一次报点)
        (TimeSpan nextInterval, _) = ComposeActivity();
        nextActivityTime = DateTime.Now.Add(nextInterval);
        lastScheduleTime = DateTime.Now;
    }

    /// <summary>按优先级叠加各模式，合成当前自主活跃的间隔与提示文本</summary>
    (TimeSpan Interval, string? Text) ComposeActivity()
    {
        // ---- 峰谷模式：高峰时段停止自主活跃（主人手动催促时绕过一次） ----
        bool peakActive = Configuration.MasterPeakMode && Configuration.PeakModeEnabled && IsPeakHour(DateTime.Now);
        if (peakActive && forceNextPoke)
            forceNextPoke = false;
        else if (peakActive && (Configuration.PeakSuppressGameMode || IsGameModeActive == false))
            return (TimeSpan.FromMinutes(5), null); //自主活跃暂停，5分钟后重查
        //不抑制游戏陪伴时，继续按游戏模式逻辑

        // ---- 游戏陪伴模式：固定间隔 ----
        if (IsGameModeActive)
        {
            int gameInterval = Math.Max(10, Configuration.GamePokeIntervalSeconds);
            string gameText = Configuration.GamePrompt;
            if (IsDndActive)
                gameText += "\n" + GetDndText();
            return (TimeSpan.FromSeconds(gameInterval), gameText);
        }

        // ---- 周期报点（继承官方算法；独立活跃机制的自定义模式使用自己的一组参数） ----
        (int actInterval, int actOffset, _, int actMaxRetry) = GetActivityParams();
        int interval = GetNextInterval(continuousTimerCount, Random.Shared.Next(-actOffset, actOffset));

        StringBuilder sb = new();
        sb.Append("系统周期报点。");
        sb.AppendLine(GetActiveReportPrompt());
        if (continuousTimerCount >= actMaxRetry)
            sb.Append($"(系统周期报点已达最大间隔时间，如果你想重新活跃一段时间，请使用<{nameof(Awake)}>来重置周期报点)");

        // ---- 撒娇模式：压缩间隔 + 促粘人（勿扰开启时被覆盖） ----
        if (IsCuteModeActive && IsDndActive == false)
        {
            int cuteInterval = Math.Max(20, Configuration.CuteMinIntervalSeconds);
            //智能节流：一段时间无真实互动则自动拉长间隔，避免高频空转烧token
            if ((DateTime.Now - lastUserInteractionTime).TotalSeconds > Configuration.CuteIdleThrottleSeconds)
                cuteInterval = Math.Max(cuteInterval, Configuration.CuteMinIntervalSeconds * 3);

            interval = Math.Min(interval, cuteInterval);
            sb.AppendLine(Configuration.CutePrompt);
        }

        // ---- 勿扰模式：自主活动但不打扰主人 ----
        if (IsDndActive)
        {
            interval = Math.Max(interval, actInterval); //勿扰下保持克制间隔
            sb.AppendLine(GetDndText());
        }

        return (TimeSpan.FromSeconds(Math.Max(1, interval)), sb.ToString());
    }

    /// <summary>
    /// 当前生效的周期报点提示词：优先使用激活的自定义报点模式，否则回退官方默认 UpdatePrompt。
    /// </summary>
    string? GetActiveReportPrompt()
    {
        string? prompt = GetActiveReportMode()?.Prompt;
        return string.IsNullOrWhiteSpace(prompt) ? Configuration.UpdatePrompt : prompt;
    }

    /// <summary>当前激活的自定义报点模式（未激活/已停用/不存在时为 null）</summary>
    CustomReportMode? GetActiveReportMode() =>
        string.IsNullOrWhiteSpace(Configuration.ActiveReportModeName)
            ? null
            : Configuration.CustomReportModes.FirstOrDefault(
                m => m.Enabled && string.Equals(m.Name, Configuration.ActiveReportModeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 当前生效的活跃机制参数：激活的自定义报点模式若开启「独立活跃机制」则用该模式自己的一组参数，
    /// 否则沿用全局活跃机制（间隔/随机偏移/倍数/最大翻倍次数）。
    /// </summary>
    (int Interval, int Offset, int Multiplier, int MaxRetry) GetActivityParams()
    {
        CustomReportMode? mode = GetActiveReportMode();
        if (mode is { IndependentActivity: true })
        {
            return (Math.Max(10, mode.IntervalSeconds),
                Math.Max(0, mode.RandomOffsetSeconds),
                Math.Max(1, mode.IntervalMultiplier),
                Math.Clamp(mode.MaxRetryCount, 0, 20));
        }
        return (Math.Max(10, Configuration.UpdateInterval),
            Math.Max(0, Configuration.UpdateRandomOffset),
            Math.Max(1, Configuration.UpdateIntervalMultiplier),
            Math.Clamp(Configuration.UpdateMaxRetryCount, 0, 20));
    }

    /// <summary>当前生效的活跃机制描述（供 UI / AI 展示）</summary>
    public string ActivityParamsText
    {
        get
        {
            (int interval, int offset, int multiplier, int maxRetry) = GetActivityParams();
            CustomReportMode? mode = GetActiveReportMode();
            string scope = mode is { IndependentActivity: true } ? $"「{mode.Name}」独立" : "全局";
            return $"{scope} {interval}s±{offset} ×{multiplier}^{maxRetry}";
        }
    }

    int GetNextInterval(int layer, int shake)
    {
        (int baseInterval, _, int multiplier, int maxRetry) = GetActivityParams();
        int pow = (int)MathF.Pow(multiplier, MathF.Min(layer, maxRetry));
        return Math.Max(1, (baseInterval + shake) * pow);
    }

    void NextActivity()
    {
        var (_, offset, _, _) = GetActivityParams();
        nextActivityTime = DateTime.Now.AddSeconds(
            GetNextInterval(continuousTimerCount, Random.Shared.Next(-offset, offset)));
        lastScheduleTime = DateTime.Now;
    }

    /// <summary>切换报点模式后重置报点节奏，并立即按新模式（可能含独立活跃机制）重排下次活跃时刻</summary>
    void ApplyReportModeChange()
    {
        continuousTimerCount = 0;
        NextActivity();
    }

    /// <summary>是否处于高峰时段（按北京时间 UTC+8 判断星期与小时，不依赖系统时区）</summary>
    bool IsPeakHour(DateTime now)
    {
        DateTime bj = now.ToUniversalTime().AddHours(8);
        //节假日豁免：法定节假日（如国庆/春节假期）DeepSeek 按谷价计费，全天不抑制自主活跃
        if (Configuration.PeakHolidayExempt && GetHolidayName(bj.Date) != null)
            return false;
        //星期过滤：未开启的星期任何时段都不算高峰（默认周一至周五；调休补班的周末不特殊处理）
        if ((Configuration.PeakDayBits & (1 << (int)bj.DayOfWeek)) == 0)
            return false;
        return Configuration.PeakHours.Any(range => range.Contains(bj.Hour));
    }

    /// <summary>北京时间对应日期命中的节假日名称（未命中为 null）</summary>
    string? GetHolidayName(DateTime bjDate) =>
        Configuration.PeakHolidays?.FirstOrDefault(r => r.Contains(bjDate))?.Name;

    /// <summary>北京时间今天命中的节假日名称（供 UI 显示，未命中为 null）</summary>
    public string? TodayHolidayName => GetHolidayName(DateTime.UtcNow.AddHours(8));

    /// <summary>峰谷当前是否因节假日而豁免（供 UI 显示）</summary>
    public bool IsPeakExemptByHoliday =>
        Configuration.MasterPeakMode && Configuration.PeakModeEnabled
        && Configuration.PeakHolidayExempt && TodayHolidayName != null;

    #endregion

    #region AI函数：模式开关

    [XmlFunction(FunctionMode.OneShot)]
    [Description("进入游戏陪伴模式：以固定间隔主动查看屏幕游戏画面并陪伴主人。当主人说「进入游戏模式/陪我打游戏/游戏陪伴」时使用。")]
    public void EnterGameMode([Description("Poke间隔（秒），默认60秒")] int intervalSeconds = 60)
    {
        Configuration.GameModeEnabled = true;
        Configuration.GamePokeIntervalSeconds = Math.Max(10, intervalSeconds);
        SaveConfig();
        nextActivityTime = DateTime.Now.AddSeconds(Math.Max(10, intervalSeconds));
        lastScheduleTime = DateTime.Now;
        interactor.Poke($"(已进入游戏陪伴模式，将每 {Configuration.GamePokeIntervalSeconds} 秒主动查看一次游戏画面陪伴主人)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("退出游戏陪伴模式。")]
    public void ExitGameMode()
    {
        Configuration.GameModeEnabled = false;
        SaveConfig();
        NextActivity();
        interactor.Poke("(已退出游戏陪伴模式，恢复常规活动节奏)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("进入睡眠模式：设定时间内不再主动活动。支持指定唤醒时间、指定时长、或等待主人发消息唤醒。当主人说「我要睡觉了/晚安/明天X点叫我」时使用。")]
    public void GoSleep(
        [Description("唤醒时间（ISO-8601格式，如2026-08-18T09:00:00），提供后以此为准")] DateTime? wakeupTime = null,
        [Description("睡眠时长（分钟），wakeupTime为空时生效，默认480分钟(8小时)")] int? durationMinutes = null,
        [Description("true=一直睡到主人发消息才唤醒（前两者均空时生效）")] bool waitForUser = false)
    {
        DateTime? wake = wakeupTime;
        if (wake == null && durationMinutes != null)
            wake = DateTime.Now.AddMinutes(durationMinutes.Value);
        if (wake == null && waitForUser == false)
            wake = DateTime.Now.AddHours(Configuration.SleepDefaultHours)
                .AddMinutes(Configuration.SleepDefaultMinutes);

        sleepWaitForUser = waitForUser;
        sleepUntil = waitForUser ? null : wake;
        nextActivityTime = DateTime.Now.AddSeconds(10); //睡眠期间由TickActivity持续轮询刷新

        string desc = waitForUser
            ? "将保持安静，直到主人发来消息才恢复"
            : $"将在 {sleepUntil:yyyy-MM-dd HH:mm} 醒来";
        interactor.Poke($"(已进入睡眠模式，{desc}。睡眠期间不会主动打扰，群聊消息也会静默处理)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("立即唤醒，恢复正常自主活动。")]
    public void WakeUp()
    {
        WakeUp(silent: false);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("开启勿扰模式：自主活动但不打扰主人（禁止使用speak/qchat等打扰标签）。当主人说「别吵我/勿扰/安静点」时使用。")]
    public void EnableDoNotDisturb([Description("自定义允许AI自主做的事，逗号分隔。留空用默认预设")] string? actions = null)
    {
        Configuration.DndModeEnabled = true;
        if (string.IsNullOrWhiteSpace(actions) == false)
            Configuration.DndAllowedActions = actions.Trim();
        SaveConfig();
        interactor.Poke($"(已开启勿扰模式。你可以自主做这些事：{Configuration.DndAllowedActions}，但绝对不要打扰主人)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("关闭勿扰模式。")]
    public void DisableDoNotDisturb()
    {
        Configuration.DndModeEnabled = false;
        SaveConfig();
        NextActivity();
        interactor.Poke("(已关闭勿扰模式，可以正常打扰主人了)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("开启撒娇模式：更粘人主动找主人，活跃间隔压缩到最短。")]
    public void EnableCuteMode([Description("最短活跃间隔（秒），不得低于20")] int? minIntervalSeconds = null)
    {
        Configuration.CuteModeEnabled = true;
        if (minIntervalSeconds != null)
            Configuration.CuteMinIntervalSeconds = Math.Max(20, minIntervalSeconds.Value);
        SaveConfig();
        nextActivityTime = DateTime.Now.AddSeconds(Configuration.CuteMinIntervalSeconds);
        lastScheduleTime = DateTime.Now;
        interactor.Poke($"(已开启撒娇模式，将更主动地粘着主人！最短活跃间隔 {Configuration.CuteMinIntervalSeconds} 秒)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("关闭撒娇模式。")]
    public void DisableCuteMode()
    {
        Configuration.CuteModeEnabled = false;
        SaveConfig();
        NextActivity();
        interactor.Poke("(已关闭撒娇模式，恢复正常活跃节奏)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换DeepSeek峰谷模式：高峰时段（默认北京时间9-12点、14-18点，周一至周五；法定节假日自动豁免）自动停止自主活跃以节省资源。")]
    public void SetPeakMode(bool enabled)
    {
        Configuration.PeakModeEnabled = enabled;
        SaveConfig();
        NextActivity();
        interactor.Poke(enabled ? "(已开启峰谷模式，高峰时段将停止自主活跃)" : "(已关闭峰谷模式，任何时段都可以自主活跃)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("自主调整你的活跃间隔（周期报点的基础间隔，秒），最短20秒。若当前激活的自定义报点模式开启了独立活跃机制，则调整的是该模式自己的间隔。")]
    public void SetActiveInterval([Description("间隔秒数，不得低于20")] int intervalSeconds)
    {
        int seconds = Math.Max(20, intervalSeconds);
        CustomReportMode? mode = GetActiveReportMode();
        if (mode is { IndependentActivity: true })
        {
            mode.IntervalSeconds = seconds;
            SaveConfig();
            ApplyReportModeChange();
            interactor.Poke($"(已将报点模式「{mode.Name}」的独立活跃间隔调整为 {seconds} 秒)");
            return;
        }
        Configuration.UpdateInterval = seconds;
        SaveConfig();
        NextActivity();
        interactor.Poke($"(已将活跃间隔调整为 {seconds} 秒)");
    }

    #endregion

    #region AI函数：报点模式

    [XmlFunction(FunctionMode.OneShot)]
    [Description("创建自定义报点模式：用你自定义的提示词替换周期报点文本；不传间隔参数时沿用全局活跃机制，传入间隔参数则该模式拥有独立的报点节奏。创建后可切换到该模式。")]
    public void CreateReportMode(
        [Description("模式名称，如「学习模式」「新闻模式」")] string name,
        [Description("周期报点提示词，到点后引导你做什么，如「如果你手头没重要的事，就去看看新闻或学习点新东西，保持安静」")] string prompt,
        [Description("可选：该模式的独立报点间隔（秒，最短10秒）。传了即为该模式开启独立活跃机制；不传则沿用全局活跃机制")] int? intervalSeconds = null,
        [Description("可选：独立活跃机制的随机偏移（秒），默认沿用全局")] int? randomOffsetSeconds = null,
        [Description("可选：独立活跃机制的间隔倍数，默认沿用全局")] int? intervalMultiplier = null,
        [Description("可选：独立活跃机制的最大翻倍次数，默认沿用全局")] int? maxRetryCount = null)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            interactor.Poke("(创建报点模式失败：名称不能为空)");
            return;
        }
        if (Configuration.CustomReportModes.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            interactor.Poke($"(已存在同名报点模式「{name}」，如需修改请先删除，或用<{nameof(SetReportModeActivity)}>调整其活跃机制)");
            return;
        }

        CustomReportMode mode = new() { Name = name, Prompt = prompt.Trim() };
        bool independent = intervalSeconds is > 0
            || randomOffsetSeconds != null || intervalMultiplier != null || maxRetryCount != null;
        if (independent)
        {
            mode.IndependentActivity = true;
            mode.IntervalSeconds = Math.Max(10, intervalSeconds ?? Configuration.UpdateInterval);
            mode.RandomOffsetSeconds = Math.Max(0, randomOffsetSeconds ?? Configuration.UpdateRandomOffset);
            mode.IntervalMultiplier = Math.Max(1, intervalMultiplier ?? Configuration.UpdateIntervalMultiplier);
            mode.MaxRetryCount = Math.Clamp(maxRetryCount ?? Configuration.UpdateMaxRetryCount, 0, 20);
        }

        Configuration.CustomReportModes.Add(mode);
        SaveConfig();
        interactor.Poke(independent
            ? $"(已创建报点模式「{name}」，独立活跃机制：{mode.ActivityText}。当前仍使用原报点，需要时可用<{nameof(SwitchReportMode)}>切换)"
            : $"(已创建报点模式「{name}」，沿用全局活跃机制。当前仍使用原报点，需要时可用<{nameof(SwitchReportMode)}>切换)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("调整已有自定义报点模式的活跃机制（是否独立计时及其间隔参数）。每个模式的报点时间互相独立。")]
    public void SetReportModeActivity(
        [Description("报点模式名称")] string name,
        [Description("是否启用独立活跃机制：true=该模式按自己的间隔独立计时；false=沿用全局活跃机制")] bool independent,
        [Description("基础间隔（秒），最短10秒，独立时生效")] int intervalSeconds = 90,
        [Description("随机偏移（秒）")] int randomOffsetSeconds = 30,
        [Description("间隔倍数")] int intervalMultiplier = 3,
        [Description("最大翻倍次数")] int maxRetryCount = 4)
    {
        name = name.Trim();
        CustomReportMode? mode = Configuration.CustomReportModes.FirstOrDefault(
            m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (mode == null)
        {
            interactor.Poke($"(未找到报点模式「{name}」，可先用<{nameof(CreateReportMode)}>创建)");
            return;
        }

        mode.IndependentActivity = independent;
        if (independent)
        {
            mode.IntervalSeconds = Math.Max(10, intervalSeconds);
            mode.RandomOffsetSeconds = Math.Max(0, randomOffsetSeconds);
            mode.IntervalMultiplier = Math.Max(1, intervalMultiplier);
            mode.MaxRetryCount = Math.Clamp(maxRetryCount, 0, 20);
        }
        SaveConfig();
        //若调整的正是当前激活的模式，立即按新节奏重排下次活跃
        if (string.Equals(Configuration.ActiveReportModeName, mode.Name, StringComparison.OrdinalIgnoreCase))
            ApplyReportModeChange();

        interactor.Poke(independent
            ? $"(已将报点模式「{mode.Name}」设为独立活跃机制：{mode.ActivityText})"
            : $"(已将报点模式「{mode.Name}」改为沿用全局活跃机制)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换周期报点模式：切换到指定自定义模式，或传「默认/官方」切回默认官方报点。提示词随之改变；若目标模式开启了独立活跃机制，报点节奏也随该模式切换。")]
    public void SwitchReportMode([Description("报点模式名称；传「默认」「官方」或留空则切回默认官方报点")] string modeName = "")
    {
        modeName = modeName.Trim();
        if (string.IsNullOrWhiteSpace(modeName)
            || string.Equals(modeName, "默认", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modeName, "官方", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.ActiveReportModeName = null;
            SaveConfig();
            ApplyReportModeChange();
            interactor.Poke($"(已切换为默认官方报点，活跃机制：{ActivityParamsText})");
            return;
        }

        CustomReportMode? mode = Configuration.CustomReportModes.FirstOrDefault(
            m => m.Enabled && string.Equals(m.Name, modeName, StringComparison.OrdinalIgnoreCase));
        if (mode == null)
        {
            interactor.Poke($"(未找到启用的报点模式「{modeName}」，可先使用<{nameof(CreateReportMode)}>创建)");
            return;
        }
        Configuration.ActiveReportModeName = mode.Name;
        SaveConfig();
        ApplyReportModeChange();
        interactor.Poke($"(已切换报点模式为「{mode.Name}」，活跃机制：{ActivityParamsText}：{mode.Prompt})");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出所有报点模式（含默认官方与自定义）、各自的活跃机制，并标注当前激活的模式。")]
    public void ListReportModes()
    {
        StringBuilder sb = new();
        string globalText = $"全局 {Math.Max(10, Configuration.UpdateInterval)}s±{Math.Max(0, Configuration.UpdateRandomOffset)}"
            + $" ×{Math.Max(1, Configuration.UpdateIntervalMultiplier)}^{Math.Clamp(Configuration.UpdateMaxRetryCount, 0, 20)}";
        bool activeIsDefault = string.IsNullOrWhiteSpace(Configuration.ActiveReportModeName)
            || Configuration.CustomReportModes.All(m => !string.Equals(m.Name, Configuration.ActiveReportModeName, StringComparison.OrdinalIgnoreCase));
        sb.AppendLine(activeIsDefault ? $"● 默认官方报点（当前，{globalText}）" : $"○ 默认官方报点（{globalText}）");
        foreach (CustomReportMode mode in Configuration.CustomReportModes)
        {
            bool active = string.Equals(mode.Name, Configuration.ActiveReportModeName, StringComparison.OrdinalIgnoreCase);
            string activity = mode.IndependentActivity ? $"，{mode.ActivityText}" : "，沿用全局";
            sb.AppendLine($"{(active ? "●" : "○")} {mode.Name}{activity}{(mode.Enabled ? "" : "（已停用）")}：{mode.Prompt}");
        }
        interactor.Poke(sb.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除自定义报点模式；若删除的是当前激活模式，将自动回退默认官方报点。")]
    public void RemoveReportMode([Description("要删除的报点模式名称")] string name)
    {
        name = name.Trim();
        CustomReportMode? mode = Configuration.CustomReportModes.FirstOrDefault(
            m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (mode == null)
        {
            interactor.Poke($"(未找到报点模式「{name}」)");
            return;
        }
        Configuration.CustomReportModes.Remove(mode);
        bool wasActive = string.Equals(Configuration.ActiveReportModeName, mode.Name, StringComparison.OrdinalIgnoreCase);
        if (wasActive)
            Configuration.ActiveReportModeName = null;
        SaveConfig();
        if (wasActive)
            ApplyReportModeChange();
        else
            NextActivity();
        interactor.Poke($"(已删除报点模式「{mode.Name}」{(wasActive ? "，报点已回退默认官方" : "")})");
    }

    #endregion

    #region AI函数：定时任务

    [XmlFunction(FunctionMode.OneShot)]
    [Description("创建一条循环定时任务：每天（或指定星期几）在固定时分触发，让AI提醒主人或自主做某事。")]
    public void CreateScheduledTask(
        [Description("任务名称，如「早安问候」")] string name,
        [Description("触发时要求AI做的事，如「跟主人说早安，要精神一点」")] string message,
        [Description("触发小时（0-23）")] int hour,
        [Description("触发分钟（0-59）")] int minute,
        [Description("星期几（1=周一...7=周日，多个用英文逗号分隔如\"1,3,5\"，留空=每天）")] string? daysOfWeek = null)
    {
        int bits = ParseDayBits(daysOfWeek);
        Configuration.ScheduledTasks.Add(new ScheduledTask {
            Name = name,
            Message = message,
            Type = ScheduledTaskType.Recurring,
            Hour = hour,
            Minute = minute,
            RecurringDayBits = bits,
        });
        SaveConfig();
        string daysText = bits == 0 ? "每天" : DayBitsText(bits);
        interactor.Poke($"(已创建循环任务「{name}」：{daysText} {hour:00}:{minute:00} 触发。触发内容：{message})");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("创建一条临时定时任务：N分钟（或N小时后）触发一次，提醒或让AI自主做某事；触发后任务会自动删除，无需手动清理。")]
    public void CreateOneTimeTask(
        [Description("任务名称")] string name,
        [Description("触发时要求AI做的事")] string message,
        [Description("多少分钟后触发（如30=30分钟后，120=2小时后）")] int afterMinutes)
    {
        afterMinutes = Math.Max(1, afterMinutes);
        ScheduledTask task = new()
        {
            Name = name,
            Message = message,
            Type = ScheduledTaskType.OneTime,
            TriggerTimeUtc = DateTime.UtcNow.AddMinutes(afterMinutes),
        };
        Configuration.ScheduledTasks.Add(task);
        //记录到当前工作会话：便于工作模式结束时（可选开关）一并清理未触发的临时任务
        if (IsWorkModeActive)
            workSessionTaskIds.Add(task.Id);
        SaveConfig();
        interactor.Poke($"(已创建临时任务「{name}」：{afterMinutes} 分钟后触发，触发后自动删除。触发内容：{message})");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除一条定时任务（按名称删除同名任务）。")]
    public void RemoveScheduledTask([Description("要删除的任务名称")] string name)
    {
        name = name.Trim();
        int removed = Configuration.ScheduledTasks.RemoveAll(t => t.Name == name);
        if (removed == 0)
        {
            interactor.Poke($"(未找到名为「{name}」的定时任务)");
            return;
        }
        SaveConfig();
        interactor.Poke($"(已删除 {removed} 条名为「{name}」的定时任务)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出当前所有定时任务。")]
    public void ListScheduledTasks()
    {
        var tasks = Configuration.ScheduledTasks.Where(t => t.Enabled).ToList();
        if (tasks.Count == 0)
        {
            interactor.Poke("(当前没有任何定时任务)");
            return;
        }
        interactor.Poke("当前定时任务：\n" + string.Join("\n", tasks.Select(t => $"- {t.Display} | {t.Message}")));
    }

    #endregion

    #region AI函数：官方基础机制

    [XmlFunction(FunctionMode.OneShot)]
    [Description("设置定点报时并重置报点节奏")]
    public void Awake([Description("报时时间")] DateTime time, [Description("备注")] string remark = "")
    {
        awakeReminderTime = time;
        awakeReminderRemark = remark;
        continuousTimerCount = 0;
        NextActivity();
        interactor.Poke($"已在 {time} 设置事件");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("等待几秒再继续")]
    public async Task Await([Description("等待秒数")] int second)
    {
        if (second > 60)
            throw new Exception($"不支持等待超过60秒，长时间等待请使用<{nameof(Awake)}>模拟");

        await Task.Delay(second * 1000);
        interactor.Poke("AWait已完成");
    }

    #endregion

    #region AI函数：工作模式

    [XmlFunction(FunctionMode.OneShot)]
    [Description("进入工作模式：当主人下达具体工作任务（写代码/改文件/跑脚本/查资料等）时使用。工作模式下会先列计划再逐步执行，像专业 Agent 一样完成任务。")]
    public void EnterWorkMode([Description("工作任务描述，如「帮我写一个Python脚本统计文件夹里的文件数量」")] string task)
    {
        if (Configuration.WorkModeEnabled == false || Configuration.MasterWorkMode == false)
        {
            interactor.Poke("(工作模式已被配置禁用，无法进入)");
            return;
        }
        if (IsWorkModeActive)
        {
            interactor.Poke("(已处于工作模式中，如需重新规划请先调用 <AbortWorkMode/> 中止后再进入)");
            return;
        }
        if (string.IsNullOrWhiteSpace(task))
        {
            interactor.Poke("(请先说明你要处理的任务内容)");
            return;
        }
        EnterWorkModeInternal(task);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("标记当前工作步骤已完成，系统自动推进到下一步。工作模式下每完成一步必须调用。")]
    public void WorkStepDone()
    {
        if (workPhase != WorkPhase.Executing)
        {
            interactor.Poke("(当前不在工作模式执行阶段，无需标记步骤完成)");
            return;
        }
        CompleteCurrentStep();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("跳过当前无法完成的工作步骤，系统自动推进到下一步。")]
    public void SkipWorkStep()
    {
        if (workPhase != WorkPhase.Executing)
        {
            interactor.Poke("(当前不在工作模式执行阶段，无需跳过)");
            return;
        }
        SkipCurrentStep();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看当前工作模式的计划与步骤进度。")]
    public void ListWorkPlan()
    {
        if (workPhase == WorkPhase.None)
        {
            interactor.Poke("(当前未在工作模式中)");
            return;
        }
        StringBuilder sb = new();
        sb.AppendLine($"工作模式 - 任务：{workTask} 阶段：{workPhase}");
        foreach (WorkStep step in workSteps)
        {
            string mark = step.Status switch
            {
                WorkStepStatus.Completed => "✔",
                WorkStepStatus.Executing => "▶",
                WorkStepStatus.Skipped => "⏭",
                WorkStepStatus.Failed => "✘",
                _ => "○",
            };
            sb.AppendLine($"{mark} 步骤{step.Index}: {step.Description}");
        }
        interactor.Poke(sb.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("暂停工作模式：暂停步骤推进，等待恢复。")]
    public void PauseWorkMode()
    {
        if (workPhase == WorkPhase.Executing)
        {
            workPhase = WorkPhase.Paused;
            interactor.Poke("(工作模式已暂停，可随时用 <ResumeWorkMode/> 恢复)");
        }
        else
        {
            interactor.Poke("(工作模式当前无法暂停)");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("恢复已暂停的工作模式，继续推进步骤。")]
    public void ResumeWorkMode()
    {
        if (workPhase == WorkPhase.Paused)
        {
            workPhase = WorkPhase.Executing;
            workStepStartTime = DateTime.Now;
            interactor.Poke("(工作模式已恢复，继续当前步骤)");
        }
        else
        {
            interactor.Poke("(工作模式未被暂停)");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("中止当前工作模式：放弃剩余步骤，立即结束。")]
    public void AbortWorkMode()
    {
        if (workPhase == WorkPhase.None)
        {
            interactor.Poke("(当前未在工作模式中)");
            return;
        }
        FinishWorkMode(autoReport: false);
        interactor.Poke("(工作模式已中止，剩余步骤放弃。如需继续可重新发起任务)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("手动跳转到指定工作步骤（从 1 开始）。用于纠正计划执行顺序。")]
    public void SetWorkStep([Description("要跳转到的步骤序号（1=第一步）")] int stepIndex)
    {
        if (workPhase != WorkPhase.Executing)
        {
            interactor.Poke("(当前不在工作模式执行阶段)");
            return;
        }
        if (stepIndex < 1 || stepIndex > workSteps.Count)
        {
            interactor.Poke($"(步骤序号无效，当前共 {workSteps.Count} 步)");
            return;
        }
        //先把正在执行的旧步骤标记为跳过，避免状态残留
        if (workCurrentIndex >= 0 && workCurrentIndex < workSteps.Count && workSteps[workCurrentIndex].Status == WorkStepStatus.Executing)
            workSteps[workCurrentIndex].Status = WorkStepStatus.Skipped;

        workCurrentIndex = stepIndex - 1;
        workSteps[workCurrentIndex].Status = WorkStepStatus.Executing;
        workSteps[workCurrentIndex].StartedAt = DateTime.Now;
        workStepStartTime = DateTime.Now;
        interactor.Poke($"(已跳转到步骤{stepIndex}：{workSteps[workCurrentIndex].Description})");
    }

    #endregion

    #region 内部工具

    void WakeUp(bool silent)
    {
        if (IsSleeping == false)
            return;
        sleepUntil = null;
        sleepWaitForUser = false;
        nextActivityTime = DateTime.Now.AddSeconds(5);
        if (silent == false)
            interactor.Poke("(已唤醒，恢复正常自主活动)");
    }

    /// <summary>
    /// 判断某个 XmlFunction 是否因模式总开关而允许暴露给 AI。
    /// 函数名与模式的映射表：关闭的模式，其全部函数都不会注册。
    /// </summary>
    bool IsModeFunctionAllowed(string functionName)
    {
        string name = functionName.ToLowerInvariant();
        return name switch
        {
            "entergamemode" or "exitgamemode" => Configuration.MasterGameMode,
            "gosleep" or "wakeup" => Configuration.MasterSleepMode,
            "enabledonotdisturb" or "disabledonotdisturb" => Configuration.MasterDndMode,
            "enablecutemode" or "disablecutemode" => Configuration.MasterCuteMode,
            "setpeakmode" => Configuration.MasterPeakMode,
            "enterworkmode" or "workstepdone" or "skipworkstep" or "listworkplan"
                or "pauseworkmode" or "resumeworkmode" or "abortworkmode" or "setworkstep" => Configuration.MasterWorkMode,
            "createreportmode" or "switchreportmode" or "listreportmodes" or "removereportmode"
                or "setreportmodeactivity" => Configuration.MasterReportMode,
            "createscheduledtask" or "createonetimetask" or "removescheduledtask" or "listscheduledtasks" => Configuration.MasterScheduledTask,
            "setactiveinterval" => Configuration.MasterInterval,
            "awake" or "await" => Configuration.MasterAwake,
            _ => true,
        };
    }

    /// <summary>勿扰模式报点文本：默认约束 + 自定义「可做的事」</summary>
    string GetDndText()
    {
        string text = Configuration.DndPokeText;
        if (string.IsNullOrWhiteSpace(Configuration.DndAllowedActions) == false)
            text += $"\n(你可以自主做这些事：{Configuration.DndAllowedActions})";
        return text;
    }

    /// <summary>解析星期字符串（1=周一...7=周日）为位图；空=每天(0)</summary>
    static int ParseDayBits(string? daysOfWeek)
    {
        if (string.IsNullOrWhiteSpace(daysOfWeek))
            return 0;
        int bits = 0;
        foreach (string part in daysOfWeek.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int day) && day >= 1 && day <= 7)
                bits |= 1 << (day % 7); //7=周日→bit0, 1-6→bit1-6，与DayOfWeek一致
        }
        return bits;
    }

    static string DayBitsText(int bits)
    {
        string[] names = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
        var days = Enumerable.Range(0, 7).Where(i => (bits & (1 << i)) != 0).Select(i => names[i]);
        return string.Join("、", days);
    }

    /// <summary>
    /// 配置热应用后按新配置重新对齐调度状态:
    /// 重置连续触发计数并按当前模式用新配置计算下次活跃时刻,
    /// 让挂件倒计时立即与新间隔同步(而非等旧报点时刻到达)。
    /// </summary>
    void ResetScheduling()
    {
        //记录本次排期所依据的配置（TickActivity 每 tick 比对，发现配置被就地改动就重排）
        scheduleFingerprint = BuildScheduleFingerprint();
        continuousTimerCount = 0;
        if (IsSleeping)
        {
            nextActivityTime = DateTime.Now.AddSeconds(10); //睡眠期间由 TickActivity 持续轮询刷新
            lastScheduleTime = DateTime.Now;
            return;
        }
        if (IsWorkModeActive)
        {
            nextActivityTime = DateTime.Now.AddSeconds(5);
            lastScheduleTime = DateTime.Now;
            return;
        }
        (TimeSpan interval, _) = ComposeActivity();
        nextActivityTime = DateTime.Now.Add(interval);
        lastScheduleTime = DateTime.Now;
        Console.WriteLine($"[主动事件增强] 配置变更，调度已按新配置对齐：[{CharacterName}] 下次活跃 {interval.TotalSeconds:0} 秒后");
    }

    /// <summary>判断两次配置对象是否在"影响调度/挂件时间"的字段上有差异(避免无关配置改动触发重新调度)</summary>
    static bool SchedulingConfigChanged(SystemEventBoostServiceConfig a, SystemEventBoostServiceConfig b)
    {
        return a.UpdateInterval != b.UpdateInterval
            || a.UpdateRandomOffset != b.UpdateRandomOffset
            || a.UpdateIntervalMultiplier != b.UpdateIntervalMultiplier
            || a.UpdateMaxRetryCount != b.UpdateMaxRetryCount
            || !string.Equals(a.ActiveReportModeName, b.ActiveReportModeName, StringComparison.Ordinal)
            || ReportModesChanged(a.CustomReportModes, b.CustomReportModes)
            || a.MasterGameMode != b.MasterGameMode || a.GameModeEnabled != b.GameModeEnabled
            || a.GamePokeIntervalSeconds != b.GamePokeIntervalSeconds
            || a.MasterCuteMode != b.MasterCuteMode || a.CuteModeEnabled != b.CuteModeEnabled
            || a.CuteMinIntervalSeconds != b.CuteMinIntervalSeconds
            || a.MasterDndMode != b.MasterDndMode || a.DndModeEnabled != b.DndModeEnabled
            || a.MasterPeakMode != b.MasterPeakMode || a.PeakModeEnabled != b.PeakModeEnabled
            || a.PeakSuppressGameMode != b.PeakSuppressGameMode
            || a.PeakDayBits != b.PeakDayBits
            || a.PeakHolidayExempt != b.PeakHolidayExempt
            || PeakHoursChanged(a.PeakHours, b.PeakHours)
            || HolidaysChanged(a.PeakHolidays, b.PeakHolidays);
    }

    /// <summary>
    /// 当前「调度相关配置」的指纹（字符串）。凡是会改变下次活跃时刻或报点节奏的配置都纳入，
    /// 用于检测框架「就地更新配置字段」（不重新赋值 Configuration 属性、setter 不被调用）导致的排期脱节。
    /// </summary>
    string BuildScheduleFingerprint()
    {
        (int interval, int offset, int multiplier, int maxRetry) = GetActivityParams();
        CustomReportMode? mode = GetActiveReportMode();
        StringBuilder sb = new();
        sb.Append(interval).Append(',').Append(offset).Append(',').Append(multiplier).Append(',').Append(maxRetry).Append(';')
          .Append(GetActiveReportPrompt()).Append(';')
          .Append(mode?.Name).Append(',').Append(mode?.IndependentActivity).Append(';')
          .Append(Configuration.MasterGameMode).Append(',').Append(Configuration.GameModeEnabled).Append(',')
          .Append(Configuration.GamePokeIntervalSeconds).Append(',')
          .Append(Configuration.GamePrompt).Append(';')
          .Append(Configuration.MasterCuteMode).Append(',').Append(Configuration.CuteModeEnabled).Append(',')
          .Append(Configuration.CuteMinIntervalSeconds).Append(',').Append(Configuration.CuteIdleThrottleSeconds).Append(',')
          .Append(Configuration.CutePrompt).Append(';')
          .Append(Configuration.MasterDndMode).Append(',').Append(Configuration.DndModeEnabled).Append(',')
          .Append(Configuration.DndPokeText).Append(',').Append(Configuration.DndAllowedActions).Append(';')
          .Append(Configuration.MasterPeakMode).Append(',').Append(Configuration.PeakModeEnabled).Append(',')
          .Append(Configuration.PeakSuppressGameMode).Append(',').Append(Configuration.PeakDayBits).Append(',')
          .Append(Configuration.PeakHolidayExempt).Append(';');
        foreach (TimeRange range in Configuration.PeakHours ?? [])
            sb.Append(range.StartHour).Append('-').Append(range.EndHour).Append(',');
        sb.Append(';');
        foreach (HolidayRange holiday in Configuration.PeakHolidays ?? [])
            sb.Append(holiday.Start.Ticks).Append('-').Append(holiday.End.Ticks).Append(',');
        return sb.ToString();
    }

    /// <summary>自定义报点模式列表是否变化（含各自独立活跃机制的开关与参数）</summary>
    static bool ReportModesChanged(List<CustomReportMode>? a, List<CustomReportMode>? b)
    {
        if (a == null || b == null)
            return a != b;
        if (a.Count != b.Count)
            return true;
        for (int i = 0; i < a.Count; i++)
        {
            CustomReportMode x = a[i];
            CustomReportMode y = b[i];
            if (x.Name != y.Name || x.Enabled != y.Enabled
                || x.IndependentActivity != y.IndependentActivity
                || x.IntervalSeconds != y.IntervalSeconds
                || x.RandomOffsetSeconds != y.RandomOffsetSeconds
                || x.IntervalMultiplier != y.IntervalMultiplier
                || x.MaxRetryCount != y.MaxRetryCount)
                return true;
        }
        return false;
    }

    /// <summary>节假日表是否变化（影响峰谷豁免判断）</summary>
    static bool HolidaysChanged(List<HolidayRange>? a, List<HolidayRange>? b)
    {
        if (a == null || b == null)
            return a != b;
        if (a.Count != b.Count)
            return true;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Start != b[i].Start || a[i].End != b[i].End)
                return true;
        }
        return false;
    }

    static bool PeakHoursChanged(List<TimeRange>? a, List<TimeRange>? b)
    {
        if (a == null || b == null)
            return a != b;
        if (a.Count != b.Count)
            return true;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].StartHour != b[i].StartHour || a[i].EndHour != b[i].EndHour)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 节假日在线校准（可选，默认关闭）：
    /// 开启后每个自然年首次启动联网拉取一次当年节假日；请求失败静默忽略，继续使用本地内置表 + 手工表。
    /// </summary>
    void TryAutoFetchHolidays()
    {
        if (Configuration.PeakHolidayAutoFetch == false)
            return;
        int year = DateTime.UtcNow.AddHours(8).Year;
        if (Configuration.PeakHolidayFetchedYear >= year)
            return;
        _ = Task.Run(() => FetchHolidaysAsync(year));
    }

    /// <summary>
    /// 联网获取指定年份的中国法定节假日并合并进节假日表（UI 的「在线校准」按钮也会调用）。
    /// 合并策略：替换原本由内置表/在线来源写入的条目，保留用户手工添加的条目。返回是否成功。
    /// </summary>
    public async Task<bool> FetchHolidaysAsync(int year)
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Alife-SystemEventBoost/4.6.0");
            string json = await client.GetStringAsync($"https://timor.tech/api/holiday/year/{year}");
            Dictionary<string, HolidayRange> fetched = ParseHolidayJson(json);
            if (fetched.Count == 0)
            {
                Console.WriteLine($"[主动事件增强] 节假日在线校准：{year} 年未获取到数据（可能官方安排尚未发布）");
                return false;
            }

            List<HolidayRange> merged = (Configuration.PeakHolidays ?? []).Where(h => h.Builtin == false).ToList();
            merged.AddRange(fetched.Values);
            merged.Sort((x, y) => x.Start.CompareTo(y.Start));
            Configuration.PeakHolidays = merged;
            Configuration.PeakHolidayFetchedYear = year;
            SaveConfig();
            Console.WriteLine($"[主动事件增强] 节假日在线校准完成：{year} 年共 {fetched.Count} 段节假日");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "节假日在线校准失败（已忽略，继续使用本地节假日表）");
            return false;
        }
    }

    /// <summary>
    /// 解析 timor.tech 节假日接口返回的 JSON，合并为「区间」列表。
    /// 只取 holiday=true 的放假日，调休补班日按需求直接忽略（无视调休）。
    /// </summary>
    static Dictionary<string, HolidayRange> ParseHolidayJson(string json)
    {
        Dictionary<string, HolidayRange> result = [];
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("holiday", out JsonElement holiday) == false
            || holiday.ValueKind != JsonValueKind.Object)
            return result;

        List<(string Name, DateTime Date)> days = [];
        foreach (JsonProperty prop in holiday.EnumerateObject())
        {
            JsonElement item = prop.Value;
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (item.TryGetProperty("holiday", out JsonElement holidayFlag) == false || holidayFlag.ValueKind != JsonValueKind.True)
                continue;
            string name = item.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "节假日" : "节假日";
            string? dateText = item.TryGetProperty("date", out JsonElement dateElement) ? dateElement.GetString() : prop.Name;
            if (dateText == null || DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) == false)
                continue;
            days.Add((name, date.Date));
        }

        //连续（且同名）的放假日合并为一条区间，便于 UI 展示与编辑
        foreach (IGrouping<string, (string Name, DateTime Date)> group in days.GroupBy(d => d.Name))
        {
            List<DateTime> sorted = group.Select(d => d.Date).OrderBy(d => d).ToList();
            DateTime start = sorted[0];
            DateTime prev = sorted[0];
            for (int i = 1; i < sorted.Count; i++)
            {
                DateTime cur = sorted[i];
                if ((cur - prev).TotalDays > 1)
                {
                    result[$"{group.Key}-{start:yyyyMMdd}"] = new HolidayRange { Start = start, End = prev, Name = group.Key, Builtin = true };
                    start = cur;
                }
                prev = cur;
            }
            result[$"{group.Key}-{start:yyyyMMdd}"] = new HolidayRange { Start = start, End = prev, Name = group.Key, Builtin = true };
        }
        return result;
    }

    void SaveConfig()
    {
        try
        {
            configurationSystem.SetConfiguration(
                typeof(SystemEventBoostService),
                Configuration,
                Character?.StorageKey ?? "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存 SystemEventBoost 配置失败");
        }
    }

    #endregion
}

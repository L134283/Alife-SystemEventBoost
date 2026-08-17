using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Alife.Plugin.SystemEventBoost;

[Module(
    "主动事件增强版",
    """
    以官方主动事件机制为基础进行增强，让 AI 的自主活动更聪明、更懂主人：
    - 定时任务：循环任务（每天/每周几）+ 临时任务（N 分钟后/后触发），让 AI 定时做任何事
    - 游戏陪伴模式：固定间隔查看屏幕游戏画面，给予鼓励与建议
    - 睡眠模式：设定时间内不再主动活动，直到主人发消息或倒计时结束
    - DeepSeek 峰谷模式：高峰时段自动停止自主活跃，节省资源
    - 勿扰模式：AI 自主活动但不打扰主人（禁止 speak/qchat 标签）
    - 撒娇模式：更粘人主动找主人，可联动桌宠吸引注意
    全部模式可叠加同时生效，且可由 AI 通过自然语言自主开启/关闭。
    """,
    defaultCategory: "Doro的妙妙工具",
    editorUI: typeof(SystemEventBoostServiceUI),
    launchOrder: 200)]
public class SystemEventBoostService(
    XmlFunctionCaller functionService,
    Interactor<SystemEventBoostService> interactor,
    ConfigurationSystem configurationSystem,
    ILogger<SystemEventBoostService> logger) :
    ChatBehaviour,
    IConfigurable<SystemEventBoostServiceConfig>
{
    public SystemEventBoostServiceConfig Configuration { get; set; } = null!;

    #region 运行时状态

    DateTime nextActivityTime;        // 下次自主活跃报点时间（周期/游戏/撒娇共用调度）
    DateTime? awakeReminderTime;      // Awake 定点报时时间
    string awakeReminderRemark = "";
    int continuousTimerCount;         // 连续触发次数（官方翻倍机制）
    DateTime? sleepUntil;             // 睡眠结束时间（null=未设置倒计时）
    bool sleepWaitForUser;            // 睡眠等待主人消息唤醒
    bool groupSilenceFlag;            // 本次Chat是否被睡眠静默占位改写（防误判唤醒）
    bool wasSleeping;                 // 上一次检测的睡眠状态（用于睡眠结束时的唤醒提示）
    DateTime lastUserInteractionTime; // 最近真实互动时间（撒娇智能节流）

    bool IsSleeping => sleepWaitForUser || (sleepUntil != null && sleepUntil > DateTime.Now);
    bool IsGameModeActive => Configuration.GameModeEnabled;
    bool IsCuteModeActive => Configuration.CuteModeEnabled;
    bool IsDndActive => Configuration.DndModeEnabled;

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        ChatBot.ChatSent += OnChatSent;
        ChatBot.ChatSend += OnChatSend;
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        lastUserInteractionTime = DateTime.Now;
        nextActivityTime = DateTime.Now;
        awakeReminderTime = null;

        //核心硬规则：无论隐式/显式都常时注入，保证 AI 知道本服务存在与触发场景
        string hardRules = """
            你拥有「主动事件增强」能力：可自主创建定时任务、切换报点模式、调整活跃节奏、开关各种陪伴模式。
            当主人用自然语言说出「进入游戏模式/陪我打游戏」「我要睡觉了/晚安/明天X点叫我」「别吵我/勿扰/安静点」「开启勿扰」「开启撒娇」等话时，
            应当主动调用本服务的对应函数响应，让主人感到贴心。
            """;

        //详细规则：显式模式常时注入；隐式模式移入 handler.Explanation，随 <systemeventboost/> 按需加载（省 token）
        string detailedRules = """
            - 创建循环或临时的定时任务（早晚问候、定时提醒、让自己定时做某事）
            - 进入游戏陪伴模式：固定间隔主动用深度视觉查看屏幕游戏画面，鼓励或建议主人
            - 进入睡眠模式：设定唤醒时间或等主人发消息后再恢复活跃
            - 开启勿扰模式：自主活动但不打扰主人；或开启撒娇模式：更粘人主动找主人
            - 控制峰谷模式（高峰时段停止自主活跃）以及自己的活跃间隔
            - 报点模式：可创建/切换自定义报点提示词（如「学习模式」「新闻模式」），报点间隔算法不变
            """;

        string implicitNote = Configuration.ImplicitInjection
            ? "\n- 隐式注入已开启：需要切换模式/创建定时任务/调整报点/主动活动时，先调用 <systemeventboost/> 加载完整函数说明与调用方式，再按文档调用对应函数。"
            : "";

        XmlHandler xmlHandler = new("SystemEventBoost") {
            Description = "主动事件增强：定时任务、报点模式切换、游戏陪伴/睡眠/勿扰/撒娇等自主活动控制。",
            Explanation = Configuration.ImplicitInjection ? detailedRules : null,
            Functions = new XmlHandler(this).Functions,
        };
        functionService.RegisterHandler(xmlHandler,
            Configuration.ImplicitInjection ? DocumentMode.Implicit : DocumentMode.Explicit,
            DestroyCancellationToken);

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

        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        if (UpdateContext.FrameCount % (int)(1 / UpdateContext.ExpectedDeltaTime) != 0)
            return Task.CompletedTask;

        TickAwakeReminder();
        TickScheduledTasks();
        TickActivity();
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        ChatBot.ChatSent -= OnChatSent;
        ChatBot.ChatSend -= OnChatSend;

        await interactor.ChatAsync($"程序关闭中。{Configuration.DestroyPrompt}");
    }

    #endregion

    #region 事件处理

    void OnChatSent(string message)
    {
        bool isSilencedGroup = groupSilenceFlag;
        groupSilenceFlag = false;

        if (message.Contains(ChatBot.PokeMessageTag) || isSilencedGroup)
            return; //系统报点/被静默的群聊消息，不视为真实互动

        //真实用户消息（主人对话）：重置周期报点 + 记录互动 + 唤醒睡眠
        continuousTimerCount = 0;
        lastUserInteractionTime = DateTime.Now;
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

    static bool IsGroupPokeMessage(string message)
        => message.Contains(ChatBot.PokeMessageTag) && message.Contains("[群聊消息(");

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
        foreach (ScheduledTask task in Configuration.ScheduledTasks)
        {
            if (task.Enabled == false)
                continue;
            if (IsSleeping)
                continue; //睡眠绝对压制：不触发任何定时任务

            bool due = false;
            if (task.Type == ScheduledTaskType.Recurring)
            {
                DateTime now = DateTime.Now;
                if (now.Hour == task.Hour && now.Minute == task.Minute && task.IsDayMatched(now.DayOfWeek))
                {
                    string todayKey = now.ToString("yyyyMMdd");
                    if (task.LastTriggerDate != todayKey)
                    {
                        task.LastTriggerDate = todayKey;
                        due = true;
                    }
                }
            }
            else if (task.TriggerTimeUtc != null && DateTime.UtcNow >= task.TriggerTimeUtc.Value)
            {
                task.Enabled = false; //一次性任务触发后自动禁用
                due = true;
            }

            if (due)
                interactor.Poke($"[定时任务:{task.Name}] {task.Message}");
        }
    }

    #endregion

    #region 自主活跃调度（周期/游戏/撒娇/勿扰/峰谷叠加）

    void TickActivity()
    {
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
            return;
        }

        if (functionService.IsIdle == false)
        {
            nextActivityTime = DateTime.Now.Add(interval); //与AI活动碰撞，延迟重试
            return;
        }

        interactor.Poke(text);
        continuousTimerCount++;
        nextActivityTime = DateTime.Now.Add(interval);
    }

    /// <summary>按优先级叠加各模式，合成当前自主活跃的间隔与提示文本</summary>
    (TimeSpan Interval, string? Text) ComposeActivity()
    {
        // ---- 峰谷模式：高峰时段停止自主活跃 ----
        if (Configuration.PeakModeEnabled && IsPeakHour(DateTime.Now))
        {
            if (Configuration.PeakSuppressGameMode || IsGameModeActive == false)
                return (TimeSpan.FromMinutes(5), null); //自主活跃暂停，5分钟后重查
            //不抑制游戏陪伴时，继续按游戏模式逻辑
        }

        // ---- 游戏陪伴模式：固定间隔 ----
        if (IsGameModeActive)
        {
            int gameInterval = Math.Max(10, Configuration.GamePokeIntervalSeconds);
            string gameText = Configuration.GamePrompt;
            if (IsDndActive)
                gameText += "\n" + GetDndText();
            return (TimeSpan.FromSeconds(gameInterval), gameText);
        }

        // ---- 周期报点（继承官方算法） ----
        int interval = GetNextInterval(continuousTimerCount,
            Random.Shared.Next(-Configuration.UpdateRandomOffset, Configuration.UpdateRandomOffset));

        StringBuilder sb = new();
        sb.Append("系统周期报点。");
        sb.AppendLine(GetActiveReportPrompt());
        if (continuousTimerCount >= Configuration.UpdateMaxRetryCount)
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
            interval = Math.Max(interval, Configuration.UpdateInterval); //勿扰下保持克制间隔
            sb.AppendLine(GetDndText());
        }

        return (TimeSpan.FromSeconds(Math.Max(1, interval)), sb.ToString());
    }

    /// <summary>
    /// 当前生效的周期报点提示词：优先使用激活的自定义报点模式，否则回退官方默认 UpdatePrompt。
    /// </summary>
    string GetActiveReportPrompt()
    {
        if (string.IsNullOrWhiteSpace(Configuration.ActiveReportModeName))
            return Configuration.UpdatePrompt;

        CustomReportMode? mode = Configuration.CustomReportModes.FirstOrDefault(
            m => m.Enabled && string.Equals(m.Name, Configuration.ActiveReportModeName, StringComparison.OrdinalIgnoreCase));
        if (mode == null || string.IsNullOrWhiteSpace(mode.Prompt))
            return Configuration.UpdatePrompt;
        return mode.Prompt;
    }

    int GetNextInterval(int layer, int shake)
    {
        int baseInterval = Configuration.UpdateInterval + shake;
        int multiplier = (int)MathF.Pow(
            Configuration.UpdateIntervalMultiplier,
            MathF.Min(layer, Configuration.UpdateMaxRetryCount));
        return Math.Max(1, baseInterval * multiplier);
    }

    void NextActivity()
    {
        nextActivityTime = DateTime.Now.AddSeconds(GetNextInterval(continuousTimerCount,
            Random.Shared.Next(-Configuration.UpdateRandomOffset, Configuration.UpdateRandomOffset)));
    }

    /// <summary>是否处于高峰时段（固定按北京时间 UTC+8 判断，不依赖系统时区）</summary>
    bool IsPeakHour(DateTime now)
    {
        int bjHour = now.ToUniversalTime().AddHours(8).Hour;
        return Configuration.PeakHours.Any(range => range.Contains(bjHour));
    }

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
    [Description("切换DeepSeek峰谷模式：高峰时段（默认北京时间9-12点、14-18点）自动停止自主活跃以节省资源。")]
    public void SetPeakMode(bool enabled)
    {
        Configuration.PeakModeEnabled = enabled;
        SaveConfig();
        NextActivity();
        interactor.Poke(enabled ? "(已开启峰谷模式，高峰时段将停止自主活跃)" : "(已关闭峰谷模式，任何时段都可以自主活跃)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("自主调整你的活跃间隔（周期报点的基础间隔，秒）。最短20秒。")]
    public void SetActiveInterval([Description("间隔秒数，不得低于20")] int intervalSeconds)
    {
        Configuration.UpdateInterval = Math.Max(20, intervalSeconds);
        SaveConfig();
        NextActivity();
        interactor.Poke($"(已将活跃间隔调整为 {Configuration.UpdateInterval} 秒)");
    }

    #endregion

    #region AI函数：报点模式

    [XmlFunction(FunctionMode.OneShot)]
    [Description("创建自定义报点模式：用你自定义的提示词替换周期报点文本，报点间隔算法与官方完全一致。创建后可切换到该模式。")]
    public void CreateReportMode(
        [Description("模式名称，如「学习模式」「新闻模式」")] string name,
        [Description("周期报点提示词，到点后引导你做什么，如「如果你手头没重要的事，就去看看新闻或学习点新东西，保持安静」")] string prompt)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            interactor.Poke("(创建报点模式失败：名称不能为空)");
            return;
        }
        if (Configuration.CustomReportModes.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            interactor.Poke($"(已存在同名报点模式「{name}」，如需修改请先删除)");
            return;
        }
        Configuration.CustomReportModes.Add(new CustomReportMode { Name = name, Prompt = prompt.Trim() });
        SaveConfig();
        interactor.Poke($"(已创建报点模式「{name}」，当前仍使用原报点，需要时可用<{nameof(SwitchReportMode)}>切换)");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换周期报点模式：切换到指定自定义模式，或传「默认/官方」切回默认官方报点。报点逻辑（间隔算法）不变，仅提示词改变。")]
    public void SwitchReportMode([Description("报点模式名称；传「默认」「官方」或留空则切回默认官方报点")] string modeName = "")
    {
        modeName = modeName.Trim();
        if (string.IsNullOrWhiteSpace(modeName)
            || string.Equals(modeName, "默认", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modeName, "官方", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.ActiveReportModeName = null;
            SaveConfig();
            NextActivity();
            interactor.Poke("(已切换为默认官方报点)");
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
        NextActivity();
        interactor.Poke($"(已切换报点模式为「{mode.Name}」：{mode.Prompt})");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出所有报点模式（含默认官方与自定义），并标注当前激活的模式。")]
    public void ListReportModes()
    {
        StringBuilder sb = new();
        bool activeIsDefault = string.IsNullOrWhiteSpace(Configuration.ActiveReportModeName)
            || Configuration.CustomReportModes.All(m => !string.Equals(m.Name, Configuration.ActiveReportModeName, StringComparison.OrdinalIgnoreCase));
        sb.AppendLine(activeIsDefault ? "● 默认官方报点（当前）" : "○ 默认官方报点");
        foreach (CustomReportMode mode in Configuration.CustomReportModes)
        {
            bool active = string.Equals(mode.Name, Configuration.ActiveReportModeName, StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"{(active ? "●" : "○")} {mode.Name}{(mode.Enabled ? "" : "（已停用）")}：{mode.Prompt}");
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
        if (string.Equals(Configuration.ActiveReportModeName, mode.Name, StringComparison.OrdinalIgnoreCase))
            Configuration.ActiveReportModeName = null;
        SaveConfig();
        NextActivity();
        interactor.Poke($"(已删除报点模式「{mode.Name}」{(Configuration.ActiveReportModeName == null && mode.Name != "默认官方报点" ? "，报点已回退默认官方" : "")})");
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
    [Description("创建一条临时定时任务：N分钟（或N小时后）触发一次，提醒或让AI自主做某事。")]
    public void CreateOneTimeTask(
        [Description("任务名称")] string name,
        [Description("触发时要求AI做的事")] string message,
        [Description("多少分钟后触发（如30=30分钟后，120=2小时后）")] int afterMinutes)
    {
        afterMinutes = Math.Max(1, afterMinutes);
        Configuration.ScheduledTasks.Add(new ScheduledTask {
            Name = name,
            Message = message,
            Type = ScheduledTaskType.OneTime,
            TriggerTimeUtc = DateTime.UtcNow.AddMinutes(afterMinutes),
        });
        SaveConfig();
        interactor.Poke($"(已创建临时任务「{name}」：{afterMinutes} 分钟后触发。触发内容：{message})");
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
    [Description("创建一个定点报时，同时重置系统周期报点（使自己可以持续活跃一段时间）")]
    public void Awake([Description("格式为ISO-8601")] DateTime time, string remark = "")
    {
        awakeReminderTime = time;
        awakeReminderRemark = remark;
        continuousTimerCount = 0;
        NextActivity();
        interactor.Poke($"已在 {time} 设置事件");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("让自己等待几秒再继续（通常仅用于主动追问或等待外部进程，因为内部工具通常支持回调，所以不需要使用）")]
    public async Task Await(int second)
    {
        if (second > 60)
            throw new Exception($"不支持等待超过60秒，长时间等待请使用<{nameof(Awake)}>模拟");

        await Task.Delay(second * 1000);
        interactor.Poke("AWait已完成");
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

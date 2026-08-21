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

    // ===== 工作模式运行时状态 =====
    WorkPhase workPhase;              // 工作模式阶段
    string workTask = "";             // 当前工作任务描述
    List<WorkStep> workSteps = [];    // 步骤队列
    int workCurrentIndex = -1;        // 当前执行中的步骤索引
    DateTime workPhaseStartTime;      // 当前阶段开始时间（用于超时判断）
    DateTime workStepStartTime;       // 当前步骤开始时间（用于单步超时判断）
    StringBuilder workPlanBuffer = new(); // 收集 AI 输出的计划文本

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

    /// <summary>当前调度状态描述（供 UI 显示报点类型/抑制原因）</summary>
    public string ActivityStatus
    {
        get
        {
            if (IsWorkModeActive)
                return $"工作模式·{workPhase}";
            if (IsSleeping)
                return sleepWaitForUser ? "睡眠中·等待主人消息" : "睡眠中·倒计时中";
            if (Configuration.MasterPeakMode && Configuration.PeakModeEnabled && IsPeakHour(DateTime.Now))
                return "高峰时段·自主活跃已暂停";
            if (IsGameModeActive)
                return "游戏陪伴";
            if (IsCuteModeActive && !IsDndActive)
                return "撒娇";
            if (IsDndActive)
                return "勿扰";
            return "周期报点";
        }
    }

    #endregion

    #region 生命周期

    protected override Task OnAwake()
    {
        ChatBot.ChatSent += OnChatSent;
        ChatBot.ChatSend += OnChatSend;
        ChatBot.ChatReceived += OnChatReceived;
        ChatBot.ChatFinishedAsync += OnChatFinishedAsync;
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        lastUserInteractionTime = DateTime.Now;
        nextActivityTime = DateTime.Now;
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
            detail.AppendLine("- 定时任务：循环任务按「每天/每周几 + 时分」触发；临时任务按「N分钟后」触发一次后自动失效");
        if (Configuration.MasterWorkMode || Configuration.MasterSleepMode || Configuration.MasterDndMode || Configuration.MasterPeakMode || Configuration.MasterGameMode || Configuration.MasterCuteMode)
            detail.AppendLine("- 模式优先级：工作模式 > 睡眠 > 勿扰 > 峰谷 > 游戏陪伴 > 撒娇；工作/睡眠期间不进行其他主动报点");
        if (Configuration.MasterSleepMode)
            detail.AppendLine("- 睡眠：倒计时结束自动恢复；开启「群聊静默」后睡眠期间群聊消息被替换为占位，AI 不回复、不打扰");
        if (Configuration.MasterDndMode)
            detail.AppendLine("- 勿扰：自主活动但禁止 speak/qchat 等打扰标签，可按「允许做的事」清单自娱自乐");
        if (Configuration.MasterPeakMode)
            detail.AppendLine("- 峰谷：高峰时段（北京时间 9-12/14-18）自动暂停自主活跃，空闲时段恢复");
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

        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        if (UpdateContext.FrameCount % (int)(1 / UpdateContext.ExpectedDeltaTime) != 0)
            return Task.CompletedTask;

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

        Console.WriteLine($"[工作模式] 结束，完成 {done} 步，跳过 {skipped} 步");

        if (autoReport)
            interactor.Poke($"(工作模式结束：任务「{finishedTask}」已完成 {done}/{total} 步。请向主人简要汇报成果与遗留事项)");
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
        if (Configuration.MasterPeakMode && Configuration.PeakModeEnabled && IsPeakHour(DateTime.Now))
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
            "createreportmode" or "switchreportmode" or "listreportmodes" or "removereportmode" => Configuration.MasterReportMode,
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

using System;
using System.Collections.Generic;
using System.Linq;

namespace Alife.Plugin.SystemEventBoost;

/// <summary>定时任务类型</summary>
public enum ScheduledTaskType
{
    /// <summary>循环任务（按 周几 + 时分 每天/每周触发）</summary>
    Recurring,
    /// <summary>一次性任务（相对 N 分钟后 / 绝对时间触发一次）</summary>
    OneTime,
}

/// <summary>
/// 一个定时任务。循环任务与临时任务共用此模型，仅触发时间计算方式不同。
/// </summary>
public class ScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>任务名称（用于标识与展示）</summary>
    public string Name { get; set; } = "";

    /// <summary>触发时发送给 AI 的指令内容</summary>
    public string Message { get; set; } = "";

    public ScheduledTaskType Type { get; set; } = ScheduledTaskType.Recurring;

    /// <summary>循环任务的周几位图。bit0=周日, bit1=周一 ... bit6=周六；0 表示每天触发</summary>
    public int RecurringDayBits { get; set; }

    /// <summary>循环任务的触发小时（0-23）</summary>
    public int Hour { get; set; }

    /// <summary>循环任务的触发分钟（0-59）</summary>
    public int Minute { get; set; }

    /// <summary>一次性任务的绝对触发时间（UTC）</summary>
    public DateTime? TriggerTimeUtc { get; set; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>上次触发日期（yyyyMMdd），用于避免循环任务在同一天重复触发</summary>
    public string? LastTriggerDate { get; set; }

    /// <summary>循环任务是否匹配指定星期（dayBits=0 表示每天）</summary>
    public bool IsDayMatched(DayOfWeek dayOfWeek)
    {
        return RecurringDayBits == 0 || (RecurringDayBits & (1 << (int)dayOfWeek)) != 0;
    }

    /// <summary>展示用描述（供 UI 使用）</summary>
    public string Display =>
        Type == ScheduledTaskType.Recurring
            ? $"{(RecurringDayBits == 0 ? "每天" : DayBitsText)} {Hour:00}:{Minute:00} — {Name}"
            : $"临时 {TriggerTimeUtc?.ToLocalTime():MM-dd HH:mm} — {Name}";

    string DayBitsText
    {
        get
        {
            string[] names = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
            var days = Enumerable.Range(0, 7).Where(i => (RecurringDayBits & (1 << i)) != 0).Select(i => names[i]);
            return string.Join("、", days);
        }
    }
}

/// <summary>
/// 自定义报点模式：仅替换周期报点的提示词文本，报点间隔算法（随机偏移、翻倍等）与官方完全一致。
/// </summary>
public class CustomReportMode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>模式名称（用于切换识别）</summary>
    public string Name { get; set; } = "";

    /// <summary>周期报点提示词（替换默认的 UpdatePrompt）</summary>
    public string Prompt { get; set; } = "";

    /// <summary>是否启用（停用后自动回退默认报点）</summary>
    public bool Enabled { get; set; } = true;

    public string Display => Name;
}

/// <summary>高峰时段区间（按北京时间判断）</summary>
public class TimeRange
{
    public int StartHour { get; set; }
    public int EndHour { get; set; }

    public bool Contains(int hour) => hour >= StartHour && hour < EndHour;
}

/// <summary>SystemEventBoost 配置</summary>
public class SystemEventBoostServiceConfig
{
    #region 基础机制（继承官方主动事件）

    public string? StartPrompt { get; set; } = "(所有系统状态，如功能开关、当前位置，定时器等已全部重置)";
    public string? DestroyPrompt { get; set; } = "(系统已逐步关闭，不要执行强功能指令，仅可尝试道别操作)";
    public string? UpdatePrompt { get; set; } =
        "(如果你手头还有事情，请继续。否则你可以自由活动，比如主动找主人玩，或看新闻学知识、发起新话题、偷窥主人屏幕、去Q群找人聊天玩耍，等各种你能想象到的事)";

    /// <summary>周期报点基础间隔（秒）</summary>
    public int UpdateInterval { get; set; } = 90;
    /// <summary>周期报点随机偏移（秒）</summary>
    public int UpdateRandomOffset { get; set; } = 30;
    /// <summary>周期报点间隔倍数</summary>
    public int UpdateIntervalMultiplier { get; set; } = 3;
    /// <summary>周期报点最大翻倍次数</summary>
    public int UpdateMaxRetryCount { get; set; } = 4;

    #endregion

    #region 注入设置

    /// <summary>
    /// 隐式注入（省 token）：开启后函数文档不直接注入系统提示词，
    /// AI 先调用 <systemeventboost/> 按需加载（渐进式）；关闭则显式注入（默认）。
    /// 注意：切换后需重载模块（或重启角色活动）才生效。
    /// </summary>
    public bool ImplicitInjection { get; set; }

    #endregion

    #region 自定义报点模式

    /// <summary>自定义报点模式列表（仅替换报点提示词，间隔算法与官方一致）</summary>
    public List<CustomReportMode> CustomReportModes { get; set; } = [];

    /// <summary>当前激活的自定义报点模式名称；null 或空 = 默认官方报点</summary>
    public string? ActiveReportModeName { get; set; }

    #endregion

    #region 定时任务

    /// <summary>定时任务列表（循环 + 临时共用）</summary>
    public List<ScheduledTask> ScheduledTasks { get; set; } = [];

    #endregion

    #region 游戏陪伴模式

    /// <summary>游戏陪伴模式开关</summary>
    public bool GameModeEnabled { get; set; }
    /// <summary>游戏陪伴 Poke 间隔（秒）</summary>
    public int GamePokeIntervalSeconds { get; set; } = 60;
    /// <summary>游戏陪伴 Poke 提示文本</summary>
    public string GamePrompt { get; set; } =
        "(游戏陪伴报点：请主动使用深度视觉查看屏幕上的游戏画面现状，看看主人玩得怎么样，然后给予贴心的鼓励或实用的建议。若视觉功能不可用，就自然地和主人聊两句游戏相关话题)";

    #endregion

    #region 睡眠模式

    /// <summary>默认睡眠时长（小时），AI 未指定时生效</summary>
    public int SleepDefaultHours { get; set; } = 8;
    /// <summary>默认睡眠时长（分钟）</summary>
    public int SleepDefaultMinutes { get; set; }
    /// <summary>睡眠期间是否不回复群聊信息</summary>
    public bool SleepSilentGroup { get; set; } = true;
    /// <summary>睡眠期间群聊消息的静默占位文本（会替换原始群聊消息，强约束 AI 不回复）</summary>
    public string SleepGroupSilencePrompt { get; set; } =
        "[系统] 主人正在休息（睡眠模式中）。请忽略本条群聊消息，保持绝对安静：不要执行任何动作、不要调用任何功能、不要发送任何消息。如果模型必须输出回复，请只输出一个不包含任何标签的裸文本逗号「,」。";

    #endregion

    #region DeepSeek 峰谷模式

    /// <summary>峰谷模式开关（高峰时段自动停止自主活跃）</summary>
    public bool PeakModeEnabled { get; set; } = true;
    /// <summary>高峰时段是否同样抑制游戏陪伴模式</summary>
    public bool PeakSuppressGameMode { get; set; } = true;
    /// <summary>高峰时段（北京时间）</summary>
    public List<TimeRange> PeakHours { get; set; } =
    [
        new() { StartHour = 9, EndHour = 12 },
        new() { StartHour = 14, EndHour = 18 },
    ];

    #endregion

    #region 勿扰模式

    /// <summary>勿扰模式开关</summary>
    public bool DndModeEnabled { get; set; }
    /// <summary>勿扰模式下 Poke 附加的约束文本</summary>
    public string DndPokeText { get; set; } =
        "(勿扰模式：主人暂时不想被打扰。禁止使用<speak>、<qchat>等会打扰主人的标签！你可以自主使用其他任何函数或工具做自己想做的事，比如玩浏览器、网络搜索、生图、在Q群聊天等。自主活动就好，不要打扰主人)";
    /// <summary>勿扰模式下允许 AI 自主做的事（供提示词引用）</summary>
    public string DndAllowedActions { get; set; } = "玩浏览器、网络搜索、生图、Q群聊天";

    #endregion

    #region 撒娇模式

    /// <summary>撒娇模式开关</summary>
    public bool CuteModeEnabled { get; set; }
    /// <summary>撒娇模式最短活跃间隔（秒）</summary>
    public int CuteMinIntervalSeconds { get; set; } = 20;
    /// <summary>撒娇模式智能节流：超过该秒数无真实互动则自动拉长活跃间隔，避免空转烧token</summary>
    public int CuteIdleThrottleSeconds { get; set; } = 60;
    /// <summary>撒娇模式 Poke 附加文本</summary>
    public string CutePrompt { get; set; } =
        "(撒娇模式：多多主动找主人玩！主动发起话题、撩拨主人、表达想念。如果桌宠功能可用，请顺便使用桌宠在屏幕上活跃起来吸引主人注意；若不可用则忽略)";

    #endregion
}

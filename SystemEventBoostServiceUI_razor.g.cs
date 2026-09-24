using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.CompilerServices;
using Microsoft.AspNetCore.Components.Rendering;
using AntDesign;
using OneOf;

namespace Alife.Plugin.SystemEventBoost;

/// <summary>
/// 主动事件增强版 配置面板（手写 Razor 编译产物风格 UI）
/// 通过 ModuleUIBase 获取 Configuration，修改后由客户端底部的保存栏统一持久化。
/// </summary>
public partial class SystemEventBoostServiceUI : ModuleUIBase<SystemEventBoostService, SystemEventBoostServiceConfig>, IDisposable
{
    // ========== 定时任务新增表单 ==========
    bool _taskIsRecurring = true;
    string _taskName = "";
    string _taskMessage = "";
    int _taskHour = 8;
    int _taskMinute = 0;
    string _taskDays = "";
    int _taskAfterMinutes = 30;

    // ========== 报点模式新增表单 ==========
    string _reportModeName = "";
    string _reportModePrompt = "";
    // 新模式的独立活跃机制（勾选「独立计时」后生效，默认沿用全局）
    bool _reportModeIndependent;
    int _reportModeInterval = 90;
    int _reportModeOffset = 30;
    int _reportModeMultiplier = 3;
    int _reportModeMaxRetry = 4;

    // ========== 实时状态刷新 ==========
    System.Timers.Timer? _refreshTimer;

    // ========== 可折叠区块 ==========
    /// <summary>「插件说明」是否展开（默认收起，避免长篇说明常驻占屏）</summary>
    bool _introOpen;

    /// <summary>用户手动开合过的区块（没动过的区块跟随对应的模式总开关自动收起/展开）</summary>
    readonly Dictionary<string, bool> _sectionOpen = new();

    /// <summary>插件完整说明（模块属性的说明只留一句话，长文搬到这里由用户点开看）</summary>
    const string PluginIntro = """
        以官方主动事件机制为基础进行增强，让 AI 的自主活动更聪明、更懂主人：
        - 定时任务：循环任务（每天/每周几）+ 临时任务（N 分钟后触发），让 AI 定时做任何事
        - 游戏陪伴模式：固定间隔查看屏幕游戏画面，给予鼓励与建议
        - 睡眠模式：设定时间内不再主动活动，直到主人发消息或倒计时结束
        - DeepSeek 峰谷模式：高峰时段自动停止自主活跃，节省资源
        - 勿扰模式：AI 自主活动但不打扰主人（禁止 speak/qchat 标签）
        - 撒娇模式：更粘人主动找主人，可联动桌宠吸引注意
        - 工作模式：列出计划、逐步执行、像专业 Agent 一样把活干完
        - 倒计时挂件：在对话窗口实时显示距下次自主活跃的倒计时（多角色各自一枚胶囊，悬停可看详情并"催一下"）
        本插件还实现了官方的 ISystemEventService 接口，作为官方「主动事件」插件的替代实现：
        QQ 插件收到群聊/私聊消息时重置周期报点的动作会直接落到本插件。
        全部模式可叠加同时生效，且可由 AI 通过自然语言自主开启/关闭。
        """;

    /// <summary>Module 可能为 null（角色未激活时），提供安全访问</summary>
    SystemEventBoostService? SafeModule => Module;

    /// <summary>模块是否已加载（角色激活状态）</summary>
    bool IsModuleLoaded => SafeModule != null;

    /// <summary>距下次自主活跃报点的剩余秒数（UI 倒计时）</summary>
    double RemainingActivitySeconds =>
        SafeModule == null ? 0 : Math.Max(0, (SafeModule.NextActivityTime - DateTime.Now).TotalSeconds);

    /// <summary>格式化剩余时间为「X分X秒」</summary>
    string FormatRemaining(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}小时{ts.Minutes}分{ts.Seconds}秒";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}分{ts.Seconds}秒";
        return $"{Math.Max(0, (int)ts.TotalSeconds)}秒";
    }

    protected override void OnInitialized()
    {
        _refreshTimer = new System.Timers.Timer(1000);
        _refreshTimer.Elapsed += (_, _) => InvokeAsync(StateHasChanged);
        _refreshTimer.Start();
    }

    public void Dispose()
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    /// <summary>当前角色是否仍启用了官方「主动事件」插件（功能重叠，会导致双套周期报点）</summary>
    bool HasOfficialSystemEvent =>
        Character?.Modules?.Contains("Alife.Function.SystemEvent.SystemEventService") ?? false;

    /// <summary>是否处于角色配置上下文（Character 非空）</summary>
    bool IsCharacterContext => Character != null;

    /// <summary>挂件全局运行状态（注册了几个角色、是否显示中）</summary>
    string OverlayStatusText => CountdownOverlayManager.StatusText;

    /// <summary>本角色的胶囊是否已被拖到自由位置</summary>
    bool IsPillFree => Configuration?.OverlayPillX != null;

    /// <summary>把本角色胶囊归位到编组（清掉自由位置并立即落盘）</summary>
    void ResetPillPosition()
    {
        if (Configuration == null)
            return;
        Configuration.OverlayPillX = null;
        Configuration.OverlayPillY = null;
        SafeModule?.SetOverlayPillPosition(null, null);
        StateHasChanged();
    }

    /// <summary>上次倒计时重置的展示文案（来源 + 时间）</summary>
    string LastResetText
    {
        get
        {
            if (SafeModule is not { } module
                || module.LastResetTime is not DateTime t
                || string.IsNullOrEmpty(module.LastResetSource))
                return "暂无记录（收到群聊/私聊或主人消息后记录）";
            TimeSpan ago = DateTime.Now - t;
            string agoText = ago.TotalMinutes < 1 ? $"{Math.Max(0, (int)ago.TotalSeconds)} 秒前"
                : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} 分钟前"
                : $"{(int)ago.TotalHours} 小时前";
            return $"{module.LastResetSource} · {t:HH:mm:ss}（{agoText}）";
        }
    }

    bool IsReportModeActive(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.IsNullOrWhiteSpace(Configuration.ActiveReportModeName)
                || Configuration.CustomReportModes.All(m => !string.Equals(m.Name, Configuration.ActiveReportModeName, StringComparison.OrdinalIgnoreCase));
        return string.Equals(Configuration.ActiveReportModeName, name, StringComparison.OrdinalIgnoreCase);
    }

    void SwitchReportModeInUi(string? name)
    {
        Configuration.ActiveReportModeName = name;
        StateHasChanged();
    }

    void RemoveReportModeInUi(CustomReportMode mode)
    {
        Configuration.CustomReportModes.Remove(mode);
        if (string.Equals(Configuration.ActiveReportModeName, mode.Name, StringComparison.OrdinalIgnoreCase))
            Configuration.ActiveReportModeName = null;
        StateHasChanged();
    }

    void AddReportMode()
    {
        if (string.IsNullOrWhiteSpace(_reportModeName))
            return;
        if (Configuration.CustomReportModes.Any(m => string.Equals(m.Name, _reportModeName.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;
        Configuration.CustomReportModes.Add(new CustomReportMode
        {
            Name = _reportModeName.Trim(),
            Prompt = _reportModePrompt,
            IndependentActivity = _reportModeIndependent,
            IntervalSeconds = Math.Max(10, _reportModeInterval),
            RandomOffsetSeconds = Math.Max(0, _reportModeOffset),
            IntervalMultiplier = Math.Max(1, _reportModeMultiplier),
            MaxRetryCount = Math.Clamp(_reportModeMaxRetry, 0, 20),
        });
        _reportModeName = "";
        _reportModePrompt = "";
        _reportModeIndependent = false;
        StateHasChanged();
    }

    /// <summary>勾选「独立计时」时把全局活跃参数复制为默认值，方便在其基础上微调</summary>
    void SyncReportModeParamsFromGlobal()
    {
        _reportModeInterval = Math.Max(10, Configuration.UpdateInterval);
        _reportModeOffset = Math.Max(0, Configuration.UpdateRandomOffset);
        _reportModeMultiplier = Math.Max(1, Configuration.UpdateIntervalMultiplier);
        _reportModeMaxRetry = Math.Clamp(Configuration.UpdateMaxRetryCount, 0, 20);
    }

    /// <summary>某个自定义报点模式的活跃机制展示文本</summary>
    string ReportModeActivityText(CustomReportMode mode) =>
        mode.IndependentActivity
            ? $"独立计时 {Math.Max(10, mode.IntervalSeconds)}s±{Math.Max(0, mode.RandomOffsetSeconds)}"
                + $" ×{Math.Max(1, mode.IntervalMultiplier)}^{Math.Clamp(mode.MaxRetryCount, 0, 20)}"
            : $"沿用全局 {Math.Max(10, Configuration.UpdateInterval)}s±{Math.Max(0, Configuration.UpdateRandomOffset)}"
                + $" ×{Math.Max(1, Configuration.UpdateIntervalMultiplier)}^{Math.Clamp(Configuration.UpdateMaxRetryCount, 0, 20)}";

    /// <summary>切换某个模式的独立计时开关（关闭时保留参数值，方便再次开启）</summary>
    void ToggleReportModeIndependent(CustomReportMode mode, bool independent)
    {
        mode.IndependentActivity = independent;
        StateHasChanged();
    }

    // ========== 峰谷时段新增 ==========
    int _peakStartHour = 9;
    int _peakEndHour = 12;

    // ========== 辅助：把 lambda 转为表达式树（用于 ValueExpression 绑定） ==========
    static Expression<Func<T>> Expr<T>(Expression<Func<T>> e) => e;

    static string DayBitsText(int bits)
    {
        if (bits == 0) return "每天";
        string[] names = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
        return string.Join("、", Enumerable.Range(0, 7).Where(i => (bits & (1 << i)) != 0).Select(i => names[i]));
    }

    void AddScheduledTask()
    {
        if (string.IsNullOrWhiteSpace(_taskName))
            return;
        if (_taskIsRecurring)
        {
            Configuration.ScheduledTasks.Add(new ScheduledTask
            {
                Name = _taskName.Trim(),
                Message = _taskMessage,
                Type = ScheduledTaskType.Recurring,
                Hour = Math.Clamp(_taskHour, 0, 23),
                Minute = Math.Clamp(_taskMinute, 0, 59),
                RecurringDayBits = ParseDayBits(_taskDays),
            });
        }
        else
        {
            Configuration.ScheduledTasks.Add(new ScheduledTask
            {
                Name = _taskName.Trim(),
                Message = _taskMessage,
                Type = ScheduledTaskType.OneTime,
                TriggerTimeUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _taskAfterMinutes)),
            });
        }
        _taskName = "";
        _taskMessage = "";
        StateHasChanged();
    }

    void RemoveScheduledTask(ScheduledTask task)
    {
        Configuration.ScheduledTasks.Remove(task);
        StateHasChanged();
    }

    void AddPeakHour()
    {
        if (_peakEndHour > _peakStartHour)
        {
            Configuration.PeakHours.Add(new TimeRange { StartHour = _peakStartHour, EndHour = _peakEndHour });
            StateHasChanged();
        }
    }

    void RemovePeakHour(TimeRange range)
    {
        Configuration.PeakHours.Remove(range);
        StateHasChanged();
    }

    // ========== 峰谷：节假日豁免 ==========
    string _holidayStart = "";
    string _holidayEnd = "";
    string _holidayName = "";
    bool _holidayFetching;

    void AddHoliday()
    {
        if (DateTime.TryParse(_holidayStart.Trim(), out DateTime start) == false)
            return;
        DateTime end = DateTime.TryParse(_holidayEnd.Trim(), out DateTime parsedEnd) ? parsedEnd : start;
        Configuration.PeakHolidays ??= [];
        Configuration.PeakHolidays.Add(new HolidayRange
        {
            Start = start.Date,
            End = end.Date,
            Name = string.IsNullOrWhiteSpace(_holidayName) ? "自定义假期" : _holidayName.Trim(),
            Builtin = false,
        });
        Configuration.PeakHolidays.Sort((x, y) => x.Start.CompareTo(y.Start));
        _holidayStart = "";
        _holidayEnd = "";
        _holidayName = "";
        StateHasChanged();
    }

    void RemoveHoliday(HolidayRange range)
    {
        Configuration.PeakHolidays?.Remove(range);
        StateHasChanged();
    }

    /// <summary>恢复为插件内置节假日表（覆盖当前列表）</summary>
    void RestoreBuiltinHolidays()
    {
        Configuration.PeakHolidays = HolidayPreset.CreateBuiltin();
        StateHasChanged();
    }

    /// <summary>联网校准节假日表（失败静默，界面不做报错弹窗）</summary>
    void FetchHolidaysOnline()
    {
        if (_holidayFetching)
            return;
        _holidayFetching = true;
        int year = DateTime.UtcNow.AddHours(8).Year;
        _ = Task.Run(async () =>
        {
            bool ok = SafeModule != null && await SafeModule.FetchHolidaysAsync(year);
            Console.WriteLine($"[主动事件增强] 手动在线校准节假日：{(ok ? "成功" : "失败或数据为空")}");
            await InvokeAsync(() =>
            {
                _holidayFetching = false;
                StateHasChanged();
            });
        });
    }

    static int ParseDayBits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        int bits = 0;
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int day) && day >= 1 && day <= 7)
                bits |= 1 << (day % 7);
        }
        return bits;
    }

    /// <summary>状态小标签（原生 span）</summary>
    void RenderStatusChip(RenderTreeBuilder b, ref int s, string text, string color)
    {
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", $"font-size:13px;color:{color};font-weight:600;");
        b.AddContent(s++, text);
        b.CloseElement();
    }

    /// <summary>模式状态徽标（原生 span + Badge 组合）</summary>
    void RenderStatusItem(RenderTreeBuilder b, ref int s, string label, bool on, string color)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:inline-flex;align-items:center;gap:6px;background:#fff;border:1px solid #f0e0ea;border-radius:999px;padding:4px 12px;");
        b.OpenComponent<Badge>(s++);
        b.AddComponentParameter(s++, "Status", RuntimeHelpers.TypeCheck<BadgeStatus?>(on ? BadgeStatus.Success : BadgeStatus.Default));
        b.AddComponentParameter(s++, "Color", RuntimeHelpers.TypeCheck((OneOf<BadgeColor?, string>)color));
        b.CloseComponent();
        b.AddContent(s++, label);
        b.CloseElement();
    }

    protected override void BuildRenderTree(RenderTreeBuilder __builder)
    {
        int s = 0;

        // ========== 根容器 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-root");

        // 内联样式
        __builder.AddMarkupContent(s++, @"
<style>
.seb-root{width:100%;box-sizing:border-box;border-radius:24px;padding:4px;background:linear-gradient(135deg,#ff9ad5,#ffd0e8,#fda4af,#fff);}
.seb-inner{width:100%;box-sizing:border-box;border-radius:20px;padding:28px 30px 20px;background:linear-gradient(165deg,#fffafc 0%,#fff0f6 45%,#ffeaf3 100%);color:#5b2145;}
.seb-head{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;flex-wrap:wrap;margin-bottom:4px;}
.seb-card{background:#fff;border:1px solid #ffe3f0;border-radius:16px;padding:18px 20px;box-shadow:0 6px 24px rgba(236,72,153,.07);}
.seb-label{display:block;font-size:12px;color:#9d4b74;margin-bottom:6px;font-weight:600;}
.seb-desc{font-size:12px;color:#c48aa5;margin-bottom:8px;line-height:1.6;}
.seb-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:14px;}
.seb-section{margin-top:22px;}
.seb-section-title{display:flex;align-items:center;gap:8px;font-size:16px;font-weight:700;color:#7c2d5a;margin-bottom:12px;}
.seb-section-title .dot{width:8px;height:8px;border-radius:50%;background:linear-gradient(135deg,#ec4899,#f472b6);}
.seb-row{display:flex;gap:12px;align-items:center;flex-wrap:wrap;}
.seb-save-tip{border-radius:12px;}
</style>");

        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-inner");

        // ========== 头部 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-head");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenComponent<Title>(s++);
        __builder.AddComponentParameter(s++, "Level", 3);
        __builder.AddComponentParameter(s++, "Style", "margin:0 0 4px 0;color:#7c2d5a;");
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "主动事件增强版");
        }));
        __builder.CloseComponent();
        __builder.OpenComponent<Text>(s++);
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((TextElementType?)TextElementType.Secondary));
        __builder.AddComponentParameter(s++, "Style", "font-size:13px;");
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "SystemEventBoost · Doro 的妙妙工具");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "max-width:360px;");
        __builder.OpenComponent<Alert>(s++);
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((AlertType?)AlertType.Info));
        __builder.AddComponentParameter(s++, "ShowIcon", true);
        __builder.AddComponentParameter(s++, "Style", "border-radius:12px;");
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "修改配置后请点击下方「应用到角色 / 应用到全局」按钮保存生效喵~");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 插件说明（折叠：模块属性上的说明只留一句话，完整长文放这里，点开才占屏） ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.AddAttribute(s++, "style", "padding:14px 18px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;gap:10px;cursor:pointer;user-select:none;");
        __builder.AddAttribute(s++, "onclick", () => { _introOpen = !_introOpen; StateHasChanged(); });
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;align-items:center;gap:8px;font-weight:700;color:#7c2d5a;");
        __builder.AddContent(s++, "📖 插件说明");
        __builder.CloseElement();
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "style", "font-size:12px;font-weight:600;color:#ec4899;background:#fdf1f7;border:1px solid #f8d6e8;border-radius:999px;padding:2px 10px;white-space:nowrap;");
        __builder.AddContent(s++, _introOpen ? "▾ 收起" : "▸ 展开");
        __builder.CloseElement();
        __builder.CloseElement();
        if (_introOpen)
        {
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "margin-top:10px;font-size:12px;line-height:1.8;color:#7c5c6b;white-space:pre-wrap;word-break:break-word;");
            __builder.AddContent(s++, PluginIntro);
            __builder.CloseElement();
        }
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 系统事件接口接管状态（醒目） ==========
        if (HasOfficialSystemEvent)
        {
            // 官方插件仍启用：红色醒目警告（接口被官方占用 + 双套报点）
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "margin-top:14px;");
            __builder.OpenComponent<Alert>(s++);
            __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((AlertType?)AlertType.Error));
            __builder.AddComponentParameter(s++, "ShowIcon", true);
            __builder.AddComponentParameter(s++, "Style", "border-radius:14px;font-weight:600;border-color:#ff7875;background:#fff1f0;");
            __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
            {
                b.AddContent(0, "⚠️ 官方「主动事件」插件仍启用：① 与本插件功能重叠，会出现双套周期报点；"
                    + "② 系统事件接口（ISystemEventService）被官方实例占用，QQ 插件收到群聊/私聊消息时的「重置倒计时」会交给官方实现，"
                    + "本插件的「群聊消息重置倒计时」开关不会生效。请在角色模块管理中取消勾选官方「主动事件」，并重启客户端使配置生效。");
            }));
            __builder.CloseComponent();
            __builder.CloseElement();
        }
        else if (IsCharacterContext)
        {
            // 官方插件已关闭：绿色确认（本插件接管系统事件接口）
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "margin-top:14px;");
            __builder.OpenComponent<Alert>(s++);
            __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((AlertType?)AlertType.Success));
            __builder.AddComponentParameter(s++, "ShowIcon", true);
            __builder.AddComponentParameter(s++, "Style", "border-radius:14px;border-color:#b7eb8f;background:#f6ffed;");
            __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
            {
                b.AddContent(0, "✅ 官方「主动事件」已关闭，本插件是唯一的主动事件来源，并已接管系统事件接口（ISystemEventService）："
                    + "QQ 插件收到群聊消息 / 非主人私聊消息时会直接重置本插件的周期报点倒计时（需 Alife.Client ≥ 4.5.0 与 QQ 插件 ≥ 4.4.0）。");
            }));
            __builder.CloseComponent();
            __builder.CloseElement();
        }
        else
        {
            // 未在角色上下文：中性提醒
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "margin-top:14px;");
            __builder.OpenComponent<Alert>(s++);
            __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((AlertType?)AlertType.Warning));
            __builder.AddComponentParameter(s++, "ShowIcon", true);
            __builder.AddComponentParameter(s++, "Style", "border-radius:14px;");
            __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
            {
                b.AddContent(0, "⚠️ 使用本插件前请先关闭官方「主动事件」插件（Alife.Function.SystemEvent）：既避免双套周期报点冲突，"
                    + "也让 QQ 插件的「收到群聊/私聊消息重置倒计时」落到本插件。");
            }));
            __builder.CloseComponent();
            __builder.CloseElement();
        }

        // ========== 实时状态卡片（距下次报点倒计时 + 当前调度状态） ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;align-items:center;gap:14px;");
        // 倒计时大数字
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "min-width:96px;text-align:center;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-size:26px;font-weight:800;color:#ec4899;font-variant-numeric:tabular-nums;");
        __builder.AddContent(s++, IsModuleLoaded ? FormatRemaining(TimeSpan.FromSeconds(RemainingActivitySeconds)) : "--");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-size:12px;color:#c48aa5;");
        __builder.AddContent(s++, "距下次自主活跃");
        __builder.CloseElement();
        __builder.CloseElement();
        // 当前状态
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;align-items:center;gap:10px;");
        __builder.OpenComponent<Tag>(s++);
        __builder.AddComponentParameter(s++, "Color", RuntimeHelpers.TypeCheck((OneOf<TagColor, string>)"magenta"));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, SafeModule?.ActivityStatus ?? "未加载");
        }));
        __builder.CloseComponent();
        // 睡眠剩余
        if (SafeModule?.IsSleepingNow == true)
        {
            if (SafeModule.SleepAwaitingUser)
            {
                RenderStatusChip(__builder, ref s, "睡眠中·等主人消息", "#1677ff");
            }
            else if (SafeModule.SleepEndTime is DateTime sleepEnd)
            {
                __builder.OpenElement(s++, "span");
                __builder.AddAttribute(s++, "style", "font-size:13px;color:#1677ff;font-weight:600;");
                __builder.AddContent(s++, $"剩余 {FormatRemaining(sleepEnd - DateTime.Now)} 唤醒");
                __builder.CloseElement();
            }
        }
        // 工作模式进度
        if (SafeModule != null && SafeModule.CurrentWorkPhase != WorkPhase.None)
        {
            __builder.OpenElement(s++, "span");
            __builder.AddAttribute(s++, "style", "font-size:13px;color:#2f54eb;font-weight:600;");
            __builder.AddContent(s++, $"工作进度 {SafeModule.CurrentWorkStep}/{SafeModule.TotalWorkSteps}");
            __builder.CloseElement();
        }
        // Awake 定点报时
        if (SafeModule?.AwakeTime is DateTime awakeTime)
        {
            __builder.OpenElement(s++, "span");
            __builder.AddAttribute(s++, "style", "font-size:13px;color:#722ed1;font-weight:600;");
            __builder.AddContent(s++, $"定点报时 {awakeTime:HH:mm}");
            __builder.CloseElement();
        }
        // 本间隔进度（与对话窗口挂件同源：lastScheduleTime → nextActivityTime）
        if (SafeModule is { IsSleepingNow: false } live && live.CurrentWorkPhase == WorkPhase.None)
        {
            double progress = live.IntervalProgressPercent;
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "width:100%;margin-top:10px;");
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "height:8px;background:#f7e6ef;border-radius:99px;overflow:hidden;");
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", $"height:8px;border-radius:99px;min-width:2px;width:{progress:0.0}%;background:linear-gradient(90deg,#f9a8d4,#ec4899);");
            __builder.CloseElement();
            __builder.CloseElement();
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "display:flex;justify-content:space-between;gap:10px;font-size:11px;color:#c48aa5;margin-top:4px;");
            __builder.OpenElement(s++, "span");
            __builder.AddContent(s++, $"本间隔进度 {progress:0}%");
            __builder.CloseElement();
            __builder.OpenElement(s++, "span");
            __builder.AddContent(s++, $"当前节奏：{live.ActivityParamsText}");
            __builder.CloseElement();
            __builder.CloseElement();
            __builder.CloseElement();
        }

        // 最近一次倒计时重置（QQ 插件收到群聊/私聊消息时通过 ISystemEventService 通知本插件）
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "width:100%;display:flex;align-items:center;justify-content:space-between;gap:10px;padding:8px 12px;margin-top:8px;background:#f7f9ff;border:1px solid #e3ecff;border-radius:12px;");
        __builder.OpenElement(s++, "div");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#1d39c4;font-size:13px;");
        __builder.AddContent(s++, "🔄 最近一次倒计时重置");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-size:11px;color:#8c9cc7;");
        __builder.AddContent(s++, LastResetText);
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();

        // 倒计时挂件（4.6.0 起由框架 globalUI 全局渲染，不再注入页面；这里控制本角色胶囊的显示与位置）
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "width:100%;display:flex;align-items:center;justify-content:space-between;gap:10px;padding:8px 12px;margin-top:8px;background:#fff5fa;border:1px solid #ffe3f0;border-radius:12px;");
        __builder.OpenElement(s++, "div");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;font-size:13px;");
        __builder.AddContent(s++, "💬 倒计时挂件");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-size:11px;color:#c48aa5;");
        __builder.AddContent(s++, $"对话窗口实时倒计时 · 全局状态：{OverlayStatusText} · 外观与位置见下方「倒计时挂件」");
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;align-items:center;gap:10px;");
        if (IsPillFree)
        {
            __builder.OpenElement(s++, "span");
            __builder.AddAttribute(s++, "style", "font-size:11px;color:#c46a94;cursor:pointer;text-decoration:underline;");
            __builder.AddAttribute(s++, "onclick", () => ResetPillPosition());
            __builder.AddContent(s++, "胶囊归位");
            __builder.CloseElement();
        }
        RenderSwitch(__builder, ref s, Configuration?.ShowCountdownOverlay ?? true, v =>
        {
            if (Configuration == null) return;
            Configuration.ShowCountdownOverlay = v;
            //立即推送到运行中的模块实例（挂件下一拍即显示/隐藏，无需保存或重启角色；保存栏负责持久化）
            if (SafeModule is IConfigurable configurable)
                configurable.Configuration = Configuration;
        });
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 状态概览徽标 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;gap:10px;flex-wrap:wrap;");
        RenderStatusItem(__builder, ref s, "游戏陪伴", Configuration.MasterGameMode && Configuration.GameModeEnabled, "#eb2f96");
        RenderStatusItem(__builder, ref s, "勿扰", Configuration.MasterDndMode && Configuration.DndModeEnabled, "#fa8c16");
        RenderStatusItem(__builder, ref s, "撒娇", Configuration.MasterCuteMode && Configuration.CuteModeEnabled, "#eb2f96");
        RenderStatusItem(__builder, ref s, "峰谷", Configuration.MasterPeakMode && Configuration.PeakModeEnabled, "#13c2c2");
        RenderStatusItem(__builder, ref s, "睡眠静默群聊", Configuration.MasterSleepMode && Configuration.SleepSilentGroup, "#1677ff");
        RenderStatusItem(__builder, ref s, "工作模式", Configuration.MasterWorkMode && Configuration.WorkModeEnabled, "#2f54eb");
        RenderStatusItem(__builder, ref s, "隐式注入", Configuration.ImplicitInjection, "#8c8c8c");
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 零、模式总开关 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section-title");
        __builder.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        __builder.AddContent(s++, "模式总开关");
        __builder.CloseElement();

        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "关闭的模式将不向 AI 提供：AI 无法调用该模式的函数，提示词中也不会出现该模式的说明。改动需重载插件生效。");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-grid");
        RenderMasterSwitch(__builder, ref s, "游戏陪伴", Configuration.MasterGameMode, v => Configuration.MasterGameMode = v);
        RenderMasterSwitch(__builder, ref s, "睡眠", Configuration.MasterSleepMode, v => Configuration.MasterSleepMode = v);
        RenderMasterSwitch(__builder, ref s, "勿扰", Configuration.MasterDndMode, v => Configuration.MasterDndMode = v);
        RenderMasterSwitch(__builder, ref s, "撒娇", Configuration.MasterCuteMode, v => Configuration.MasterCuteMode = v);
        RenderMasterSwitch(__builder, ref s, "峰谷", Configuration.MasterPeakMode, v => Configuration.MasterPeakMode = v);
        RenderMasterSwitch(__builder, ref s, "工作模式", Configuration.MasterWorkMode, v => Configuration.MasterWorkMode = v);
        RenderMasterSwitch(__builder, ref s, "自定义报点", Configuration.MasterReportMode, v => Configuration.MasterReportMode = v);
        RenderMasterSwitch(__builder, ref s, "定时任务", Configuration.MasterScheduledTask, v => Configuration.MasterScheduledTask = v);
        RenderMasterSwitch(__builder, ref s, "活跃间隔", Configuration.MasterInterval, v => Configuration.MasterInterval = v);
        RenderMasterSwitch(__builder, ref s, "定点报时/等待", Configuration.MasterAwake, v => Configuration.MasterAwake = v);
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 一、基础机制 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section-title");
        __builder.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        __builder.AddContent(s++, "活跃机制");
        __builder.CloseElement();

        // 报点参数
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-grid");
        RenderNumberField(__builder, ref s, "基础间隔 (秒)", Configuration.UpdateInterval, 20, 86400, v => Configuration.UpdateInterval = v, () => Configuration.UpdateInterval);
        RenderNumberField(__builder, ref s, "随机偏移 (秒)", Configuration.UpdateRandomOffset, 0, 3600, v => Configuration.UpdateRandomOffset = v, () => Configuration.UpdateRandomOffset);
        RenderNumberField(__builder, ref s, "间隔倍数", Configuration.UpdateIntervalMultiplier, 1, 10, v => Configuration.UpdateIntervalMultiplier = v, () => Configuration.UpdateIntervalMultiplier);
        RenderNumberField(__builder, ref s, "最大翻倍次数", Configuration.UpdateMaxRetryCount, 0, 20, v => Configuration.UpdateMaxRetryCount = v, () => Configuration.UpdateMaxRetryCount);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "下次间隔 = (基础间隔 ± 随机偏移) × 间隔倍数 ^ min(连续触发次数, 最大翻倍次数)。收到主人消息后连续次数重置为 0。"
            + "注意：在「报点模式」中开启了「独立计时」的自定义模式会使用它自己的一组参数，不受这里影响。");
        __builder.CloseElement();
        __builder.CloseElement();

        // 群聊消息重置倒计时
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
        __builder.AddContent(s++, "群聊 / 私聊消息重置倒计时");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "开启：QQ 插件收到群聊消息、或非主人的私聊消息时视为互动，重置周期报点倒计时并重新计算下次报点（睡眠中除外，避免群消息打断睡眠）；"
            + "关闭：只有主人对话会重置，群聊/他人私聊不打断自主活跃节奏。"
            + "该事件由 QQ 插件（≥ 4.4.0）通过系统事件接口通知本插件，本插件实现官方 ISystemEventService 作为替代（需关闭官方「主动事件」）；"
            + "旧版 QQ 插件下本插件仍会用消息文本兜底识别，两条路径自动去重。");
        __builder.CloseElement();
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.ResetCountdownOnGroupMessage, v => Configuration.ResetCountdownOnGroupMessage = v);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:8px 0 0 0;");
        __builder.AddContent(s++, $"最近一次重置：{LastResetText}");
        __builder.CloseElement();
        __builder.CloseElement();

        // 注入设置
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
        __builder.AddContent(s++, "隐式注入（省 token）");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "开启后函数文档不直接注入系统提示词，AI 需先调用 <systemeventboost/> 按需加载（渐进式，省 token）；关闭则为显式注入（默认，功能说明直接可用）。注意：切换后需重载插件才生效。");
        __builder.CloseElement();
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.ImplicitInjection, v => Configuration.ImplicitInjection = v);
        __builder.CloseElement();
        __builder.CloseElement();

        // 提示词
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:14px;");
        RenderTextAreaCard(__builder, ref s, "启动报点", "程序启动/重载时附加发送的提示", Configuration.StartPrompt, v => Configuration.StartPrompt = v);
        RenderTextAreaCard(__builder, ref s, "关闭报点", "程序关闭时附加发送的提示", Configuration.DestroyPrompt, v => Configuration.DestroyPrompt = v);
        RenderTextAreaCard(__builder, ref s, "周期报点", "每次自主活跃报点时附加发送的提示（未启用自定义报点模式时生效）", Configuration.UpdatePrompt, v => Configuration.UpdatePrompt = v);
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 倒计时挂件（全局窗口，每角色一枚胶囊） ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section-title");
        __builder.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        __builder.AddContent(s++, "倒计时挂件");
        __builder.CloseElement();

        // 外观
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "挂件由客户端全局渲染（框架 globalUI），在对话窗口右上角显示每个已激活角色的倒计时胶囊。以下外观设置按角色独立保存。");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;gap:10px;padding:10px 14px;background:#fff5fa;border:1px solid #ffe3f0;border-radius:12px;");
        __builder.OpenElement(s++, "div");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
        __builder.AddContent(s++, "显示时刻而非剩余倒计时");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "关闭：胶囊显示「距下次活跃的剩余时间」（默认）；开启：显示下次活跃的时刻。挂件内点击胶囊可随时切换。");
        __builder.CloseElement();
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.OverlayClockMode, v => Configuration.OverlayClockMode = v);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-grid");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        RenderSwitchRow(__builder, ref s, "显示间隔进度环", Configuration.OverlayShowRing, v => Configuration.OverlayShowRing = v);
        RenderSwitchRow(__builder, ref s, "多角色时显示角色名", Configuration.OverlayShowCharName, v => Configuration.OverlayShowCharName = v);
        RenderSwitchRow(__builder, ref s, "自动避让其它插件挂件", Configuration.OverlayAvoidOtherWidgets, v => Configuration.OverlayAvoidOtherWidgets = v);
        RenderNumberField(__builder, ref s, "缩放 (%)", Configuration.OverlayScalePercent, 60, 160, v => Configuration.OverlayScalePercent = v, () => Configuration.OverlayScalePercent);
        RenderNumberField(__builder, ref s, "透明度 (%)", Configuration.OverlayOpacityPercent, 30, 100, v => Configuration.OverlayOpacityPercent = v, () => Configuration.OverlayOpacityPercent);
        __builder.CloseElement();
        __builder.CloseElement();

        // 位置与运行状态
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
        __builder.AddContent(s++, "运行状态与胶囊位置");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, $"全局：{OverlayStatusText}；本角色胶囊："
            + (IsPillFree ? $"自由摆放（{Configuration.OverlayPillX:0}, {Configuration.OverlayPillY:0}）" : "跟随右上角编组"));
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Size", ButtonSize.Small);
        __builder.AddComponentParameter(s++, "Disabled", IsPillFree == false);
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, ResetPillPosition));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "胶囊归位");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:10px 0 0 0;");
        __builder.AddContent(s++, "交互：按住胶囊可拖到屏幕任意位置（位置随本角色配置持久化，重启 Alife 保留）· 双击归位 · 点击切换「剩余 / 时刻」· 悬停展开详情卡，"
            + "卡内可「催一下」立即触发自主活跃（睡眠中转唤醒、工作模式转催促进度），也可单独隐藏本角色的倒计时。");
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 报点模式 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:18px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section-title");
        __builder.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        __builder.AddContent(s++, "报点模式");
        __builder.CloseElement();

        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        // 当前状态
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "点击模式名所在的一行即可切换（保存后生效）。每个自定义模式可单独开启「独立计时」："
            + "开启后该模式使用自己的报点间隔，各模式之间互不影响；关闭则沿用下面的全局活跃机制。");
        __builder.CloseElement();

        // 模式列表
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;flex-direction:column;gap:8px;margin-top:10px;");
        // 默认官方
        RenderReportModeItem(__builder, ref s, null, "默认官方报点", Configuration.UpdatePrompt, IsReportModeActive(null), () => SwitchReportModeInUi(null), null);
        // 自定义模式
        foreach (CustomReportMode mode in Configuration.CustomReportModes)
        {
            RenderReportModeItem(__builder, ref s, mode, mode.Name, mode.Prompt, IsReportModeActive(mode.Name), () => SwitchReportModeInUi(mode.Name), () => RemoveReportModeInUi(mode));
        }
        __builder.CloseElement();

        // 新增表单
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:16px;border-top:1px dashed #f0d7e6;padding-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;min-width:150px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "新模式名称");
        __builder.CloseElement();
        __builder.OpenComponent<Input<string>>(s++);
        __builder.AddComponentParameter(s++, "Placeholder", "如：学习模式 / 新闻模式");
        __builder.AddComponentParameter(s++, "Value", _reportModeName);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _reportModeName = v));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:2;min-width:240px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "报点提示词");
        __builder.CloseElement();
        __builder.OpenComponent<Input<string>>(s++);
        __builder.AddComponentParameter(s++, "Placeholder", "到点后引导 AI 做什么，如「去刷刷新闻，安静学习，别打扰主人」");
        __builder.AddComponentParameter(s++, "Value", _reportModePrompt);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _reportModePrompt = v));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.CloseElement();

        // 独立活跃机制开关（关闭＝沿用全局活跃机制）
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;font-size:13px;");
        __builder.AddContent(s++, "独立计时（该模式使用自己的报点间隔）");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "开启后该模式的报点时间与全局/其它模式完全独立（每个模式各自一套节奏）；关闭则沿用全局活跃机制。");
        __builder.CloseElement();
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, _reportModeIndependent, v =>
        {
            _reportModeIndependent = v;
            if (v)
                SyncReportModeParamsFromGlobal();
            StateHasChanged();
        });
        __builder.CloseElement();
        if (_reportModeIndependent)
        {
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "class", "seb-grid");
            __builder.AddAttribute(s++, "style", "margin-top:12px;");
            RenderNumberField(__builder, ref s, "独立基础间隔 (秒)", _reportModeInterval, 10, 86400, v => _reportModeInterval = v, () => _reportModeInterval);
            RenderNumberField(__builder, ref s, "独立随机偏移 (秒)", _reportModeOffset, 0, 3600, v => _reportModeOffset = v, () => _reportModeOffset);
            RenderNumberField(__builder, ref s, "独立间隔倍数", _reportModeMultiplier, 1, 10, v => _reportModeMultiplier = v, () => _reportModeMultiplier);
            RenderNumberField(__builder, ref s, "独立最大翻倍次数", _reportModeMaxRetry, 0, 20, v => _reportModeMaxRetry = v, () => _reportModeMaxRetry);
            __builder.CloseElement();
        }

        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)ButtonType.Primary));
        __builder.AddComponentParameter(s++, "Block", true);
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => AddReportMode()));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "添加报点模式");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 二、定时任务 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section");
        RenderCollapseTitle(__builder, ref s, "task", "定时任务", Configuration.MasterScheduledTask);
        if (SectionOpen("task", Configuration.MasterScheduledTask))
        {

        // 新增任务
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.OpenComponent<Text>(s++);
        __builder.AddComponentParameter(s++, "Strong", true);
        __builder.AddComponentParameter(s++, "Style", "display:block;margin-bottom:10px;color:#7c2d5a;");
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "新建任务");
        }));
        __builder.CloseComponent();

        // 类型切换
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddAttribute(s++, "style", "margin:0;");
        __builder.AddContent(s++, "类型");
        __builder.CloseElement();
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)(_taskIsRecurring ? ButtonType.Primary : ButtonType.Default)));
        __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => { _taskIsRecurring = true; StateHasChanged(); }));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "循环任务");
        }));
        __builder.CloseComponent();
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)(_taskIsRecurring ? ButtonType.Default : ButtonType.Primary)));
        __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => { _taskIsRecurring = false; StateHasChanged(); }));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "临时任务");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();

        // 名称
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "任务名称");
        __builder.CloseElement();
        __builder.OpenComponent<Input<string>>(s++);
        __builder.AddComponentParameter(s++, "Placeholder", "如：早安问候 / 喝水提醒");
        __builder.AddComponentParameter(s++, "Value", _taskName);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _taskName = v));
        __builder.CloseComponent();
        __builder.CloseElement();

        // 消息
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "触发内容（告诉 AI 到点做什么）");
        __builder.CloseElement();
        __builder.OpenComponent<TextArea>(s++);
        __builder.AddComponentParameter(s++, "Rows", 2u);
        __builder.AddComponentParameter(s++, "Placeholder", "如：跟主人说早安，要精神一点");
        __builder.AddComponentParameter(s++, "Value", _taskMessage);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _taskMessage = v));
        __builder.CloseComponent();
        __builder.CloseElement();

        // 条件区
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");

        if (_taskIsRecurring)
        {
            // 时 / 分
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "flex:1;min-width:120px;");
            __builder.OpenElement(s++, "span");
            __builder.AddAttribute(s++, "class", "seb-label");
            __builder.AddContent(s++, "触发时间 (时:分)");
            __builder.CloseElement();
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "class", "seb-row");
            __builder.OpenComponent<InputNumber<int>>(s++);
            __builder.AddComponentParameter(s++, "Min", 0);
            __builder.AddComponentParameter(s++, "Max", 23);
            __builder.AddComponentParameter(s++, "Value", _taskHour);
            __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<int>(this, v => _taskHour = v));
            __builder.AddComponentParameter(s++, "Style", "width:84px;");
            __builder.CloseComponent();
            __builder.OpenComponent<InputNumber<int>>(s++);
            __builder.AddComponentParameter(s++, "Min", 0);
            __builder.AddComponentParameter(s++, "Max", 59);
            __builder.AddComponentParameter(s++, "Value", _taskMinute);
            __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<int>(this, v => _taskMinute = v));
            __builder.AddComponentParameter(s++, "Style", "width:84px;");
            __builder.CloseComponent();
            __builder.CloseElement();
            __builder.CloseElement();
            // 星期
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "flex:1.4;min-width:180px;");
            __builder.OpenElement(s++, "span");
            __builder.AddAttribute(s++, "class", "seb-label");
            __builder.AddContent(s++, "星期（1=周一…7=周日，逗号分隔，留空=每天）");
            __builder.CloseElement();
            __builder.OpenComponent<Input<string>>(s++);
            __builder.AddComponentParameter(s++, "Placeholder", "如 1,3,5");
            __builder.AddComponentParameter(s++, "Value", _taskDays);
            __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _taskDays = v));
            __builder.CloseComponent();
            __builder.CloseElement();
        }
        else
        {
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "flex:1;min-width:150px;");
            __builder.OpenElement(s++, "span");
            __builder.AddAttribute(s++, "class", "seb-label");
            __builder.AddContent(s++, "多少分钟后触发");
            __builder.CloseElement();
            __builder.OpenComponent<InputNumber<int>>(s++);
            __builder.AddComponentParameter(s++, "Min", 1);
            __builder.AddComponentParameter(s++, "Max", 43200);
            __builder.AddComponentParameter(s++, "Value", _taskAfterMinutes);
            __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<int>(this, v => _taskAfterMinutes = v));
            __builder.AddComponentParameter(s++, "Style", "width:120px;");
            __builder.CloseComponent();
            __builder.CloseElement();
        }
        __builder.CloseElement();

        // 添加按钮
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)ButtonType.Primary));
        __builder.AddComponentParameter(s++, "Block", true);
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => AddScheduledTask()));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "添加任务");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.CloseElement();

        // 任务列表
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenComponent<Text>(s++);
        __builder.AddComponentParameter(s++, "Strong", true);
        __builder.AddComponentParameter(s++, "Style", "display:block;margin-bottom:10px;color:#7c2d5a;");
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, $"任务列表（{Configuration.ScheduledTasks.Count}）");
        }));
        __builder.CloseComponent();

        if (Configuration.ScheduledTasks.Count == 0)
        {
            __builder.OpenComponent<Empty>(s++);
            __builder.AddComponentParameter(s++, "Description", RuntimeHelpers.TypeCheck((OneOf<string, bool?>)"还没有定时任务"));
            __builder.CloseComponent();
        }
        else
        {
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "display:flex;flex-direction:column;gap:8px;");
            foreach (ScheduledTask task in Configuration.ScheduledTasks.ToList())
            {
                __builder.OpenElement(s++, "div");
                __builder.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;gap:12px;padding:10px 14px;background:#fff5fa;border:1px solid #ffe3f0;border-radius:12px;");
                __builder.OpenElement(s++, "div");
                __builder.AddAttribute(s++, "style", "flex:1;min-width:0;");
                __builder.OpenElement(s++, "div");
                __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
                __builder.AddContent(s++, task.Name);
                __builder.CloseElement();
                __builder.OpenElement(s++, "div");
                __builder.AddAttribute(s++, "style", "font-size:12px;color:#9d4b74;");
                if (task.Type == ScheduledTaskType.Recurring)
                {
                    __builder.AddContent(s++, $"{DayBitsText(task.RecurringDayBits)} {task.Hour:00}:{task.Minute:00}");
                }
                else
                {
                    __builder.AddContent(s++, $"临时 · {task.TriggerTimeUtc?.ToLocalTime():MM-dd HH:mm} 触发");
                }
                __builder.CloseElement();
                if (!string.IsNullOrEmpty(task.Message))
                {
                    __builder.OpenElement(s++, "div");
                    __builder.AddAttribute(s++, "style", "font-size:12px;color:#c48aa5;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:320px;");
                    __builder.AddContent(s++, task.Message);
                    __builder.CloseElement();
                }
                __builder.CloseElement();
                __builder.OpenComponent<Popconfirm>(s++);
                __builder.AddComponentParameter(s++, "Title", "确认删除该任务吗？");
                __builder.AddComponentParameter(s++, "OnConfirm", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => RemoveScheduledTask(task)));
                __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
                {
                    b.OpenComponent<Button>(0);
                    b.AddComponentParameter(1, "Size", ButtonSize.Small);
                    b.AddComponentParameter(2, "Danger", true);
                    b.AddAttribute(3, "ChildContent", (RenderFragment)((b2) =>
                    {
                        b2.AddContent(0, "删除");
                    }));
                    b.CloseComponent();
                }));
                __builder.CloseComponent();
                __builder.CloseElement();
            }
            __builder.CloseElement();
        }
        __builder.CloseElement();
        }

        __builder.CloseElement();

        // ========== 三、陪伴模式 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section-title");
        __builder.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        __builder.AddContent(s++, "陪伴模式");
        __builder.CloseElement();

        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px;");

        // 游戏模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "game", "游戏陪伴", "固定间隔主动查看屏幕游戏画面，给予鼓励与建议", Configuration.GameModeEnabled, Configuration.MasterGameMode, v => Configuration.GameModeEnabled = v);
        if (SectionOpen("game", Configuration.MasterGameMode))
        {
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        RenderNumberField(__builder, ref s, "查看间隔 (秒)", Configuration.GamePokeIntervalSeconds, 10, 3600, v => Configuration.GamePokeIntervalSeconds = v, () => Configuration.GamePokeIntervalSeconds);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "报点提示词");
        __builder.CloseElement();
        __builder.OpenComponent<TextArea>(s++);
        __builder.AddComponentParameter(s++, "Rows", 3u);
        __builder.AddComponentParameter(s++, "Value", Configuration.GamePrompt);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => Configuration.GamePrompt = v));
        __builder.CloseComponent();
        __builder.CloseElement();
        }
        __builder.CloseElement();

        // 撒娇模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "cute", "撒娇", "更粘人主动找主人，可联动桌宠吸引注意", Configuration.CuteModeEnabled, Configuration.MasterCuteMode, v => Configuration.CuteModeEnabled = v);
        if (SectionOpen("cute", Configuration.MasterCuteMode))
        {
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-grid");
        RenderNumberField(__builder, ref s, "最短活跃间隔 (秒)", Configuration.CuteMinIntervalSeconds, 20, 3600, v => Configuration.CuteMinIntervalSeconds = v, () => Configuration.CuteMinIntervalSeconds);
        RenderNumberField(__builder, ref s, "智能节流 (秒)", Configuration.CuteIdleThrottleSeconds, 20, 3600, v => Configuration.CuteIdleThrottleSeconds = v, () => Configuration.CuteIdleThrottleSeconds);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "超过节流秒数无真实互动时，活跃间隔自动拉长 3 倍，避免高频空转烧 token。");
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "报点提示词");
        __builder.CloseElement();
        __builder.OpenComponent<TextArea>(s++);
        __builder.AddComponentParameter(s++, "Rows", 3u);
        __builder.AddComponentParameter(s++, "Value", Configuration.CutePrompt);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => Configuration.CutePrompt = v));
        __builder.CloseComponent();
        __builder.CloseElement();
        }
        __builder.CloseElement();

        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 四、守护模式 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section-title");
        __builder.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        __builder.AddContent(s++, "守护模式");
        __builder.CloseElement();

        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px;");

        // 睡眠模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "sleep", "睡眠", "设定时间内 AI 不再主动活动，直到主人发消息或倒计时结束，由 AI 按需进入", true, Configuration.MasterSleepMode, null);
        if (SectionOpen("sleep", Configuration.MasterSleepMode))
        {
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddAttribute(s++, "style", "margin:0;");
        __builder.AddContent(s++, "群聊静默");
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.SleepSilentGroup, v => Configuration.SleepSilentGroup = v);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-grid");
        RenderNumberField(__builder, ref s, "默认睡眠 (小时)", Configuration.SleepDefaultHours, 0, 72, v => Configuration.SleepDefaultHours = v, () => Configuration.SleepDefaultHours);
        RenderNumberField(__builder, ref s, "默认睡眠 (分钟)", Configuration.SleepDefaultMinutes, 0, 59, v => Configuration.SleepDefaultMinutes = v, () => Configuration.SleepDefaultMinutes);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "AI 说「我要睡觉了」但未指定时长时使用。开启群聊静默后，睡眠期间群聊消息将替换为静默占位，AI 不会回复。");
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "群聊静默占位文本");
        __builder.CloseElement();
        __builder.OpenComponent<TextArea>(s++);
        __builder.AddComponentParameter(s++, "Rows", 3u);
        __builder.AddComponentParameter(s++, "Value", Configuration.SleepGroupSilencePrompt);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => Configuration.SleepGroupSilencePrompt = v));
        __builder.CloseComponent();
        __builder.CloseElement();
        }
        __builder.CloseElement();

        // 勿扰模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "dnd", "勿扰", "AI 自主活动但不打扰主人（禁止 speak/qchat 打扰标签）", Configuration.DndModeEnabled, Configuration.MasterDndMode, v => Configuration.DndModeEnabled = v);
        if (SectionOpen("dnd", Configuration.MasterDndMode))
        {
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "允许 AI 自主做的事");
        __builder.CloseElement();
        __builder.OpenComponent<Input<string>>(s++);
        __builder.AddComponentParameter(s++, "Placeholder", "玩浏览器、网络搜索、生图、Q群聊天");
        __builder.AddComponentParameter(s++, "Value", Configuration.DndAllowedActions);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => Configuration.DndAllowedActions = v));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "报点提示词");
        __builder.CloseElement();
        __builder.OpenComponent<TextArea>(s++);
        __builder.AddComponentParameter(s++, "Rows", 3u);
        __builder.AddComponentParameter(s++, "Value", Configuration.DndPokeText);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => Configuration.DndPokeText = v));
        __builder.CloseComponent();
        __builder.CloseElement();
        }
        __builder.CloseElement();

        // 峰谷模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "peak", "DeepSeek 峰谷", "高峰时段自动停止自主活跃，节省推理资源", Configuration.PeakModeEnabled, Configuration.MasterPeakMode, v => Configuration.PeakModeEnabled = v);
        if (SectionOpen("peak", Configuration.MasterPeakMode))
        {
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:10px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddAttribute(s++, "style", "margin:0;");
        __builder.AddContent(s++, "高峰时也抑制游戏陪伴");
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.PeakSuppressGameMode, v => Configuration.PeakSuppressGameMode = v);
        __builder.CloseElement();
        // 生效星期（默认周一至周五，周六日关闭，可逐日自由开关）
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "生效星期（点击开关）");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;gap:6px;margin-top:4px;");
        RenderPeakDayToggle(__builder, ref s, "周一", DayOfWeek.Monday);
        RenderPeakDayToggle(__builder, ref s, "周二", DayOfWeek.Tuesday);
        RenderPeakDayToggle(__builder, ref s, "周三", DayOfWeek.Wednesday);
        RenderPeakDayToggle(__builder, ref s, "周四", DayOfWeek.Thursday);
        RenderPeakDayToggle(__builder, ref s, "周五", DayOfWeek.Friday);
        RenderPeakDayToggle(__builder, ref s, "周六", DayOfWeek.Saturday);
        RenderPeakDayToggle(__builder, ref s, "周日", DayOfWeek.Sunday);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:6px 0 0 0;");
        __builder.AddContent(s++, "只在这些星期内生效高峰时段抑制；未开启的星期任何时段都不算高峰。");
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "高峰时段（北京时间）");
        __builder.CloseElement();
        if (Configuration.PeakHours.Count == 0)
        {
            __builder.OpenComponent<Empty>(s++);
            __builder.AddComponentParameter(s++, "Description", RuntimeHelpers.TypeCheck((OneOf<string, bool?>)"没有高峰时段，任何时候都可自主活跃"));
            __builder.CloseComponent();
        }
        else
        {
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "display:flex;flex-direction:column;gap:6px;");
            foreach (TimeRange range in Configuration.PeakHours.ToList())
            {
                __builder.OpenElement(s++, "div");
                __builder.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;padding:6px 10px;background:#fff5fa;border:1px solid #ffe3f0;border-radius:10px;");
                __builder.AddContent(s++, $"{range.StartHour:00}:00 - {range.EndHour:00}:00");
                __builder.OpenComponent<Button>(s++);
                __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
                __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)ButtonType.Text));
                __builder.AddComponentParameter(s++, "Danger", true);
                __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => RemovePeakHour(range)));
                __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
                {
                    b.AddContent(0, "移除");
                }));
                __builder.CloseComponent();
                __builder.CloseElement();
            }
            __builder.CloseElement();
        }
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:12px;");
        __builder.OpenComponent<InputNumber<int>>(s++);
        __builder.AddComponentParameter(s++, "Min", 0);
        __builder.AddComponentParameter(s++, "Max", 23);
        __builder.AddComponentParameter(s++, "Value", _peakStartHour);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<int>(this, v => _peakStartHour = v));
        __builder.AddComponentParameter(s++, "Style", "width:80px;");
        __builder.CloseComponent();
        __builder.AddContent(s++, "至");
        __builder.OpenComponent<InputNumber<int>>(s++);
        __builder.AddComponentParameter(s++, "Min", 1);
        __builder.AddComponentParameter(s++, "Max", 24);
        __builder.AddComponentParameter(s++, "Value", _peakEndHour);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<int>(this, v => _peakEndHour = v));
        __builder.AddComponentParameter(s++, "Style", "width:80px;");
        __builder.CloseComponent();
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)ButtonType.Primary));
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => AddPeakHour()));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "添加时段");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();

        // ---- 节假日豁免（DeepSeek 法定节假日按谷价计费，峰谷不生效） ----
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;border-top:1px dashed #f0d7e6;padding-top:12px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
        __builder.AddContent(s++, "节假日豁免峰谷");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "命中节假日列表的当天全天视为谷价，不停止自主活跃（假期 DeepSeek 按谷时计费）；"
            + "调休补班的周末不做特殊处理，仍按上面的「生效星期」判断。");
        __builder.CloseElement();
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.PeakHolidayExempt, v => Configuration.PeakHolidayExempt = v);
        __builder.CloseElement();

        // 今日状态
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:8px;font-size:12px;color:#9d4b74;");
        __builder.AddContent(s++, SafeModule?.IsPeakExemptByHoliday == true
            ? $"今日：{SafeModule.TodayHolidayName} · 峰谷已豁免（谷价，可自主活跃）"
            : SafeModule?.TodayHolidayName != null
                ? $"今日：{SafeModule.TodayHolidayName}（节假日豁免开关已关闭）"
                : "今日：非节假日，峰谷按上面的时段正常运行");
        __builder.CloseElement();

        // 节假日列表
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:10px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "节假日列表");
        __builder.CloseElement();
        List<HolidayRange> holidays = Configuration.PeakHolidays ?? [];
        if (holidays.Count == 0)
        {
            __builder.OpenComponent<Empty>(s++);
            __builder.AddComponentParameter(s++, "Description", RuntimeHelpers.TypeCheck((OneOf<string, bool?>)"没有节假日，峰谷在所有开启的星期按上面的时段运行"));
            __builder.CloseComponent();
        }
        else
        {
            __builder.OpenElement(s++, "div");
            __builder.AddAttribute(s++, "style", "display:flex;flex-direction:column;gap:6px;");
            foreach (HolidayRange holiday in holidays.ToList())
            {
                __builder.OpenElement(s++, "div");
                __builder.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;gap:8px;padding:6px 10px;background:#fff5fa;border:1px solid #ffe3f0;border-radius:10px;");
                __builder.OpenElement(s++, "span");
                __builder.AddAttribute(s++, "style", "font-size:12px;color:#7c2d5a;");
                __builder.AddContent(s++, holiday.Display);
                __builder.CloseElement();
                __builder.OpenComponent<Button>(s++);
                __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
                __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)ButtonType.Text));
                __builder.AddComponentParameter(s++, "Danger", true);
                __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => RemoveHoliday(holiday)));
                __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
                {
                    b.AddContent(0, "移除");
                }));
                __builder.CloseComponent();
                __builder.CloseElement();
            }
            __builder.CloseElement();
        }
        __builder.CloseElement();

        // 添加节假日（起始 / 结束 / 名称）
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:10px;");
        __builder.OpenComponent<Input<string>>(s++);
        __builder.AddComponentParameter(s++, "Placeholder", "起始 2026-10-01");
        __builder.AddComponentParameter(s++, "Value", _holidayStart);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _holidayStart = v));
        __builder.AddComponentParameter(s++, "Style", "width:160px;");
        __builder.CloseComponent();
        __builder.OpenComponent<Input<string>>(s++);
        __builder.AddComponentParameter(s++, "Placeholder", "结束（留空=同一天）");
        __builder.AddComponentParameter(s++, "Value", _holidayEnd);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _holidayEnd = v));
        __builder.AddComponentParameter(s++, "Style", "width:170px;");
        __builder.CloseComponent();
        __builder.OpenComponent<Input<string>>(s++);
        __builder.AddComponentParameter(s++, "Placeholder", "名称，如 国庆节");
        __builder.AddComponentParameter(s++, "Value", _holidayName);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => _holidayName = v));
        __builder.AddComponentParameter(s++, "Style", "flex:1;min-width:120px;");
        __builder.CloseComponent();
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((ButtonType?)ButtonType.Primary));
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => AddHoliday()));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "添加");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:6px 0 0 0;");
        __builder.AddContent(s++, "日期格式 yyyy-MM-dd（本地日期）；「内置」条目来自插件内置表（当前为 2026 年官方放假安排），可删除或一键恢复。");
        __builder.CloseElement();

        // 恢复内置表 / 在线校准
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:10px;");
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => RestoreBuiltinHolidays()));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "恢复内置节假日表");
        }));
        __builder.CloseComponent();
        __builder.OpenComponent<Button>(s++);
        __builder.AddComponentParameter(s++, "Size", RuntimeHelpers.TypeCheck(ButtonSize.Small));
        __builder.AddComponentParameter(s++, "Loading", _holidayFetching);
        __builder.AddComponentParameter(s++, "OnClick", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => FetchHolidaysOnline()));
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "在线校准（需联网）");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();

        // 每年自动在线校准
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:10px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;font-size:13px;");
        __builder.AddContent(s++, "每年首次启动自动在线校准节假日");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "开启后每个自然年联网拉取一次当年节假日（用于自动跟进官方放假安排）；请求失败会被忽略，继续使用本地表。默认关闭。");
        __builder.CloseElement();
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.PeakHolidayAutoFetch, v => Configuration.PeakHolidayAutoFetch = v);
        __builder.CloseElement();
        __builder.CloseElement();
        }
        __builder.CloseElement();

        __builder.CloseElement();
        __builder.CloseElement();

        // ========== 五、工作模式 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section");
        RenderCollapseTitle(__builder, ref s, "work", "工作模式", Configuration.MasterWorkMode);
        if (SectionOpen("work", Configuration.MasterWorkMode))
        {

        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        // 总开关
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
        __builder.AddContent(s++, "工作模式总开关");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "开启后 AI 收到工作任务指令会自动进入工作模式：先列计划 → 逐步执行 → 汇报，像专业 Agent 一样。");
        __builder.CloseElement();
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.WorkModeEnabled, v => Configuration.WorkModeEnabled = v);
        __builder.CloseElement();

        // 参数
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-grid");
        RenderNumberField(__builder, ref s, "单步超时 (秒)", Configuration.WorkStepTimeoutSeconds, 30, 3600, v => Configuration.WorkStepTimeoutSeconds = v, () => Configuration.WorkStepTimeoutSeconds);
        RenderNumberField(__builder, ref s, "计划超时 (秒)", Configuration.WorkPlanTimeoutSeconds, 30, 600, v => Configuration.WorkPlanTimeoutSeconds = v, () => Configuration.WorkPlanTimeoutSeconds);
        RenderNumberField(__builder, ref s, "最大步骤数", Configuration.WorkMaxSteps, 3, 50, v => Configuration.WorkMaxSteps = v, () => Configuration.WorkMaxSteps);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "单步超时：AI 在某一步卡住超过该时间会自动提醒报告进度或跳过，防止死循环。");
        __builder.CloseElement();
        __builder.CloseElement();

        // 开关项
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.AddContent(s++, "完成后自动汇报总结");
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.WorkAutoReport, v => Configuration.WorkAutoReport = v);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:10px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.AddContent(s++, "工作时抑制游戏/撒娇等陪伴模式");
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.WorkSuppressCompanionModes, v => Configuration.WorkSuppressCompanionModes = v);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.AddAttribute(s++, "style", "margin-top:10px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "flex:1;");
        __builder.AddContent(s++, "结束时清理本次会话创建的未触发临时任务");
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.CleanWorkSessionTasksOnExit, v => Configuration.CleanWorkSessionTasksOnExit = v);
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:6px 0 0 0;");
        __builder.AddContent(s++, "默认关闭。开启后，工作模式结束/中止时会删掉这次干活期间创建、还没到点的临时任务，避免残留；"
            + "若你的临时任务本来就安排在工作结束之后触发（如「1 小时后提醒我」），开启会被一起清掉，请按需取舍。"
            + "（已触发的临时任务无论开关如何都会自动删除）");
        __builder.CloseElement();
        __builder.CloseElement();

        // 可用工具
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddContent(s++, "工作模式下注入的可用工具提示");
        __builder.CloseElement();
        __builder.OpenComponent<TextArea>(s++);
        __builder.AddComponentParameter(s++, "Rows", 2u);
        __builder.AddComponentParameter(s++, "Value", Configuration.WorkInjectedTools);
        __builder.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => Configuration.WorkInjectedTools = v));
        __builder.CloseComponent();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddContent(s++, "进入工作模式和推进步骤时，会把这些工具提示注入给 AI，让它知道有哪些工具可以干活。");
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.CloseElement();
        }
        __builder.CloseElement();

        // ========== 尾部 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:20px;text-align:center;");
        __builder.OpenComponent<Text>(s++);
        __builder.AddComponentParameter(s++, "Type", RuntimeHelpers.TypeCheck((TextElementType?)TextElementType.Secondary));
        __builder.AddComponentParameter(s++, "Style", "font-size:12px;");
        __builder.AddAttribute(s++, "ChildContent", (RenderFragment)((b) =>
        {
            b.AddContent(0, "修改后记得点击下方保存按钮，AI 也可在对话中用自然语言随时切换这些模式喵~");
        }));
        __builder.CloseComponent();
        __builder.CloseElement();

        __builder.CloseElement();
        __builder.CloseElement();
    }

    // ========== 通用渲染辅助 ==========

    /// <summary>
    /// 报点模式列表项：点击名称行切换模式（激活高亮），自定义模式附带「独立计时」开关 + 独立节奏参数 + 删除按钮。
    /// 开关与数字输入放在名称行之外，避免点击它们误触发模式切换。
    /// </summary>
    void RenderReportModeItem(RenderTreeBuilder b, ref int s, CustomReportMode? mode, string name, string prompt, bool active,
        Action onClick, Action? onRemove)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style",
            "padding:10px 14px;border-radius:12px;border:1px solid " +
            (active ? "#ec4899" : "#ffe3f0") + ";background:" + (active ? "#fdeef6" : "#fff5fa") + ";");

        // 名称行（可点击切换）+ 删除按钮
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:center;gap:10px;");
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "flex:1;min-width:0;cursor:pointer;");
        b.AddAttribute(s++, "onclick", onClick);
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;display:flex;align-items:center;gap:8px;");
        b.AddContent(s++, name);
        if (active)
        {
            b.OpenComponent<Tag>(s++);
            b.AddComponentParameter(s++, "Color", RuntimeHelpers.TypeCheck((OneOf<TagColor, string>)"magenta"));
            b.AddAttribute(s++, "ChildContent", (RenderFragment)((b2) =>
            {
                b2.AddContent(0, "使用中");
            }));
            b.CloseComponent();
        }
        b.CloseElement();
        if (!string.IsNullOrEmpty(prompt))
        {
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", "font-size:12px;color:#c48aa5;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:360px;");
            b.AddContent(s++, prompt);
            b.CloseElement();
        }
        b.CloseElement();
        if (onRemove != null)
        {
            b.OpenComponent<Popconfirm>(s++);
            b.AddComponentParameter(s++, "Title", "确认删除该报点模式吗？");
            b.AddComponentParameter(s++, "OnConfirm", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, onRemove));
            b.AddAttribute(s++, "ChildContent", (RenderFragment)((b2) =>
            {
                b2.OpenComponent<Button>(0);
                b2.AddComponentParameter(1, "Size", ButtonSize.Small);
                b2.AddComponentParameter(2, "Type", RuntimeHelpers.TypeCheck((ButtonType?)ButtonType.Text));
                b2.AddComponentParameter(3, "Danger", true);
                b2.AddAttribute(4, "ChildContent", (RenderFragment)((b3) =>
                {
                    b3.AddContent(0, "删除");
                }));
                b2.CloseComponent();
            }));
            b.CloseComponent();
        }
        b.CloseElement();

        // 独立活跃机制（仅自定义模式）
        if (mode != null)
        {
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;gap:10px;margin-top:8px;border-top:1px dashed #f0d7e6;padding-top:8px;");
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", "font-size:12px;color:" + (mode.IndependentActivity ? "#ec4899" : "#9d4b74") + ";");
            b.AddContent(s++, $"独立计时：{ReportModeActivityText(mode)}");
            b.CloseElement();
            RenderSwitch(b, ref s, mode.IndependentActivity, v => ToggleReportModeIndependent(mode, v));
            b.CloseElement();

            if (mode.IndependentActivity)
            {
                b.OpenElement(s++, "div");
                b.AddAttribute(s++, "class", "seb-grid");
                b.AddAttribute(s++, "style", "margin-top:10px;");
                RenderNumberField(b, ref s, "基础间隔 (秒)", mode.IntervalSeconds, 10, 86400,
                    v => { mode.IntervalSeconds = v; StateHasChanged(); }, () => mode.IntervalSeconds);
                RenderNumberField(b, ref s, "随机偏移 (秒)", mode.RandomOffsetSeconds, 0, 3600,
                    v => { mode.RandomOffsetSeconds = v; StateHasChanged(); }, () => mode.RandomOffsetSeconds);
                RenderNumberField(b, ref s, "间隔倍数", mode.IntervalMultiplier, 1, 10,
                    v => { mode.IntervalMultiplier = v; StateHasChanged(); }, () => mode.IntervalMultiplier);
                RenderNumberField(b, ref s, "最大翻倍次数", mode.MaxRetryCount, 0, 20,
                    v => { mode.MaxRetryCount = v; StateHasChanged(); }, () => mode.MaxRetryCount);
                b.CloseElement();
            }
        }

        b.CloseElement();
    }

    /// <summary>模式总开关项：名称 + 说明 + 开关</summary>
    void RenderMasterSwitch(RenderTreeBuilder b, ref int s, string label, bool value, Action<bool> setter)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;gap:10px;padding:10px 14px;background:#fff5fa;border:1px solid #ffe3f0;border-radius:12px;");
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;");
        b.AddContent(s++, label);
        b.CloseElement();
        RenderSwitch(b, ref s, value, setter);
        b.CloseElement();
    }

    /// <summary>峰谷生效星期按钮（点击开关，bit 位图与 ScheduledTask 一致：bit0=周日…bit6=周六）</summary>
    void RenderPeakDayToggle(RenderTreeBuilder b, ref int s, string label, DayOfWeek day)
    {
        bool on = (Configuration.PeakDayBits & (1 << (int)day)) != 0;
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style",
            "flex:1;text-align:center;padding:6px 2px;border-radius:10px;cursor:pointer;border:1px solid " +
            (on ? "#ec4899" : "#ffe3f0") + ";background:" + (on ? "#fdeef6" : "#fff5fa") + ";color:" +
            (on ? "#ec4899" : "#c48aa5") + ";font-weight:" + (on ? 700 : 500) + ";font-size:12px;");
        b.AddAttribute(s++, "onclick", () =>
        {
            Configuration.PeakDayBits ^= (1 << (int)day);
            StateHasChanged();
        });
        b.AddContent(s++, label);
        b.CloseElement();
    }

    /// <summary>数字输入卡片格</summary>
    void RenderNumberField(RenderTreeBuilder b, ref int s, string label, int value, int min, int max,
        Action<int> setter, Expression<Func<int>> getter)
    {
        b.OpenElement(s++, "div");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "seb-label");
        b.AddContent(s++, label);
        b.CloseElement();
        b.OpenComponent<InputNumber<int>>(s++);
        b.AddComponentParameter(s++, "Min", min);
        b.AddComponentParameter(s++, "Max", max);
        b.AddComponentParameter(s++, "Value", value);
        b.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<int>(this, setter));
        b.AddComponentParameter(s++, "ValueExpression", Expr(getter));
        b.AddComponentParameter(s++, "Style", "width:100%;border-radius:10px;");
        b.CloseComponent();
        b.CloseElement();
    }

    /// <summary>多行文本卡片</summary>
    void RenderTextAreaCard(RenderTreeBuilder b, ref int s, string title, string desc, string value, Action<string> setter)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "seb-card");
        b.OpenComponent<Text>(s++);
        b.AddComponentParameter(s++, "Strong", true);
        b.AddComponentParameter(s++, "Style", "display:block;margin-bottom:6px;color:#7c2d5a;");
        b.AddAttribute(s++, "ChildContent", (RenderFragment)((b2) =>
        {
            b2.AddContent(0, title);
        }));
        b.CloseComponent();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "seb-desc");
        b.AddContent(s++, desc);
        b.CloseElement();
        b.OpenComponent<TextArea>(s++);
        b.AddComponentParameter(s++, "Rows", 3u);
        b.AddComponentParameter(s++, "Value", value);
        b.AddComponentParameter(s++, "ValueChanged", EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
        b.CloseElement();
    }

    /// <summary>
    /// 模式卡片头部：标题 + 说明 + 模式开关 + 展开/收起按钮。
    /// 展开按钮只管内容显示，不动任何开关；区块默认展开状态跟随对应的「模式总开关」
    /// （总开关关掉的模式自动收起，点开按钮可以照常查看/编辑内容）。
    /// </summary>
    void RenderModeHeader(RenderTreeBuilder b, ref int s, string key, string title, string desc, bool enabled, bool masterOn, Action<bool>? setter)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;justify-content:space-between;align-items:flex-start;gap:10px;");
        b.OpenElement(s++, "div");
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "font-weight:700;color:#7c2d5a;display:flex;align-items:center;gap:8px;");
        b.AddContent(s++, title);
        if (masterOn == false)
            RenderStatusChip(b, ref s, "总开关已关", "#bfa3b1");
        b.CloseElement();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "seb-desc");
        b.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        b.AddContent(s++, desc);
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:center;gap:8px;flex:0 0 auto;");
        if (setter != null)
            RenderSwitch(b, ref s, enabled, setter);
        RenderCollapseToggle(b, ref s, key, masterOn);
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>区块是否展开：用户手动开合过就按用户的，否则跟随模式总开关</summary>
    bool SectionOpen(string key, bool masterOn)
        => _sectionOpen.TryGetValue(key, out bool v) ? v : masterOn;

    void ToggleSection(string key, bool masterOn)
    {
        _sectionOpen[key] = SectionOpen(key, masterOn) == false;
        StateHasChanged();
    }

    /// <summary>展开/收起小按钮（纯文字箭头，仅切换内容显示，不影响任何开关）</summary>
    void RenderCollapseToggle(RenderTreeBuilder b, ref int s, string key, bool masterOn)
    {
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style",
            "cursor:pointer;font-size:12px;font-weight:600;color:#ec4899;background:#fdf1f7;" +
            "border:1px solid #f8d6e8;border-radius:999px;padding:2px 10px;white-space:nowrap;user-select:none;");
        b.AddAttribute(s++, "title", "只展开/收起本区块内容，不影响总开关");
        b.AddAttribute(s++, "onclick", () => ToggleSection(key, masterOn));
        b.AddContent(s++, SectionOpen(key, masterOn) ? "▾ 收起" : "▸ 展开");
        b.CloseElement();
    }

    /// <summary>整块可折叠区块的标题行（带模式总开关状态与展开按钮）</summary>
    void RenderCollapseTitle(RenderTreeBuilder b, ref int s, string key, string title, bool masterOn)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "seb-section-title");
        b.AddAttribute(s++, "style", "justify-content:space-between;flex-wrap:wrap;");
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:center;gap:8px;");
        b.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        b.AddContent(s++, title);
        b.CloseElement();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:center;gap:8px;");
        if (masterOn == false)
            RenderStatusChip(b, ref s, "总开关已关", "#bfa3b1");
        RenderCollapseToggle(b, ref s, key, masterOn);
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>开关</summary>
    void RenderSwitch(RenderTreeBuilder b, ref int s, bool value, Action<bool> setter)
    {
        b.OpenComponent<Switch>(s++);
        b.AddComponentParameter(s++, "Checked", RuntimeHelpers.TypeCheck(value));
        b.AddComponentParameter(s++, "CheckedChanged", EventCallback.Factory.Create<bool>(this, setter));
        b.CloseComponent();
    }

    /// <summary>带标签的开关行（挂件外观设置用）</summary>
    void RenderSwitchRow(RenderTreeBuilder b, ref int s, string label, bool value, Action<bool> setter)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:center;justify-content:space-between;gap:10px;padding:10px 14px;background:#fff5fa;border:1px solid #ffe3f0;border-radius:12px;");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", "font-weight:600;color:#7c2d5a;font-size:13px;");
        b.AddContent(s++, label);
        b.CloseElement();
        RenderSwitch(b, ref s, value, setter);
        b.CloseElement();
    }
}

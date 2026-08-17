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
public partial class SystemEventBoostServiceUI : ModuleUIBase<SystemEventBoostService, SystemEventBoostServiceConfig>
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
        });
        _reportModeName = "";
        _reportModePrompt = "";
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

        // ========== 状态概览 ==========
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "margin-top:14px;");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;gap:10px;flex-wrap:wrap;");
        RenderStatusItem(__builder, ref s, $"周期报点 {Configuration.UpdateInterval}s", true, "#722ed1");
        RenderStatusItem(__builder, ref s, "游戏陪伴", Configuration.GameModeEnabled, "#eb2f96");
        RenderStatusItem(__builder, ref s, "勿扰", Configuration.DndModeEnabled, "#fa8c16");
        RenderStatusItem(__builder, ref s, "撒娇", Configuration.CuteModeEnabled, "#eb2f96");
        RenderStatusItem(__builder, ref s, "峰谷", Configuration.PeakModeEnabled, "#13c2c2");
        RenderStatusItem(__builder, ref s, "睡眠静默群聊", Configuration.SleepSilentGroup, "#1677ff");
        RenderStatusItem(__builder, ref s, "隐式注入", Configuration.ImplicitInjection, "#8c8c8c");
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
        __builder.AddContent(s++, "下次间隔 = (基础间隔 ± 随机偏移) × 间隔倍数 ^ min(连续触发次数, 最大翻倍次数)。收到主人消息后连续次数重置为 0。");
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
        __builder.AddContent(s++, "点击下面的模式即可即时切换（保存后生效）。切换只改变报点提示词，报点间隔算法与官方完全一致。");
        __builder.CloseElement();

        // 模式列表
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;flex-direction:column;gap:8px;margin-top:10px;");
        // 默认官方
        RenderReportModeItem(__builder, ref s, "默认官方报点", Configuration.UpdatePrompt, IsReportModeActive(null), () => SwitchReportModeInUi(null), null);
        // 自定义模式
        foreach (CustomReportMode mode in Configuration.CustomReportModes)
        {
            RenderReportModeItem(__builder, ref s, mode.Name, mode.Prompt, IsReportModeActive(mode.Name), () => SwitchReportModeInUi(mode.Name), () => RemoveReportModeInUi(mode));
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
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-section-title");
        __builder.AddMarkupContent(s++, "<span class=\"dot\"></span>");
        __builder.AddContent(s++, "定时任务");
        __builder.CloseElement();

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
        RenderModeHeader(__builder, ref s, "游戏陪伴", "固定间隔主动查看屏幕游戏画面，给予鼓励与建议", Configuration.GameModeEnabled, v => Configuration.GameModeEnabled = v);
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
        __builder.CloseElement();

        // 撒娇模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "撒娇", "更粘人主动找主人，可联动桌宠吸引注意", Configuration.CuteModeEnabled, v => Configuration.CuteModeEnabled = v);
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
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "display:flex;justify-content:space-between;align-items:flex-start;gap:10px;");
        __builder.OpenElement(s++, "div");
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "style", "font-weight:700;color:#7c2d5a;");
        __builder.AddContent(s++, "睡眠");
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-desc");
        __builder.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        __builder.AddContent(s++, "设定时间内 AI 不再主动活动，直到主人发消息或倒计时结束");
        __builder.CloseElement();
        __builder.CloseElement();
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-row");
        __builder.OpenElement(s++, "span");
        __builder.AddAttribute(s++, "class", "seb-label");
        __builder.AddAttribute(s++, "style", "margin:0;");
        __builder.AddContent(s++, "群聊静默");
        __builder.CloseElement();
        RenderSwitch(__builder, ref s, Configuration.SleepSilentGroup, v => Configuration.SleepSilentGroup = v);
        __builder.CloseElement();
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
        __builder.CloseElement();

        // 勿扰模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "勿扰", "AI 自主活动但不打扰主人（禁止 speak/qchat 打扰标签）", Configuration.DndModeEnabled, v => Configuration.DndModeEnabled = v);
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
        __builder.CloseElement();

        // 峰谷模式
        __builder.OpenElement(s++, "div");
        __builder.AddAttribute(s++, "class", "seb-card");
        RenderModeHeader(__builder, ref s, "DeepSeek 峰谷", "高峰时段自动停止自主活跃，节省推理资源", Configuration.PeakModeEnabled, v => Configuration.PeakModeEnabled = v);
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
        __builder.CloseElement();

        __builder.CloseElement();
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

    /// <summary>报点模式列表项：点击切换，激活高亮，可带删除按钮</summary>
    void RenderReportModeItem(RenderTreeBuilder b, ref int s, string name, string prompt, bool active,
        Action onClick, Action? onRemove)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style",
            "display:flex;align-items:center;gap:10px;padding:10px 14px;border-radius:12px;cursor:pointer;border:1px solid " +
            (active ? "#ec4899" : "#ffe3f0") + ";background:" + (active ? "#fdeef6" : "#fff5fa") + ";");
        b.AddAttribute(s++, "onclick", onClick);
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "flex:1;min-width:0;");
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

    /// <summary>模式卡片头部：标题 + 说明 + 开关</summary>
    void RenderModeHeader(RenderTreeBuilder b, ref int s, string title, string desc, bool enabled, Action<bool> setter)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;justify-content:space-between;align-items:flex-start;gap:10px;");
        b.OpenElement(s++, "div");
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "font-weight:700;color:#7c2d5a;");
        b.AddContent(s++, title);
        b.CloseElement();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "seb-desc");
        b.AddAttribute(s++, "style", "margin:4px 0 0 0;");
        b.AddContent(s++, desc);
        b.CloseElement();
        b.CloseElement();
        RenderSwitch(b, ref s, enabled, setter);
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
}

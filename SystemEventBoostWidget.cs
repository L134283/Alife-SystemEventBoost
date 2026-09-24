using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Alife.Plugin.SystemEventBoost;

/// <summary>
/// 对话窗口倒计时挂件（框架 4.5.0 的 globalUI 全局渲染项，<see cref="ModuleAttribute.GlobalUI"/>）。
///
/// 与 4.5.x 的旧实现相比彻底换掉了「ElectronNET 注入 JS + 本地 HTTP 服务 + 端口/令牌/心跳」那套：
/// - 数据直接来自 <see cref="CountdownOverlayManager"/> 里注册的模块实例，没有序列化/网络往返；
/// - 渲染由 Blazor 负责，没有 Shadow DOM 手工重建、没有 fetch 失败自愈、没有 IPC 应答竞争；
/// - JS 仅在「拖拽时量一次胶囊矩形/视口」和「探测其它插件挂件做避让」时使用，全部 try/catch 兜底，
///   JS 不可用时挂件照常显示，只是不做边界钳制与避让。
///
/// 每个角色一枚胶囊（颜色随调度状态），悬停展开详情卡（下次活跃时刻/间隔进度/睡眠剩余/工作进度/定点报时），
/// 卡内可「催一下」立即触发活跃；胶囊可按住拖到任意位置（位置随角色配置持久化，重启 Alife 保留），
/// 双击归位；点击胶囊在「剩余倒计时 / 时刻」之间切换。
/// </summary>
public class CountdownOverlayWidget : ComponentBase, IDisposable
{
    [Inject]
    IJSRuntime Js { get; set; } = null!;

    // ---- 渲染状态 ----
    List<OverlayCharSnapshot> _snaps = [];
    string? _openName;              // 详情卡当前展开的角色
    string? _pokeResult;            // 「催一下」的临时反馈文案
    string? _pokeResultName;
    Timer? _timer;
    Timer? _pokeClearTimer;

    // ---- 拖拽状态 ----
    string? _dragName;
    bool _dragMoved;                        // 是否已越过拖拽阈值（此时才脱离编组跟着鼠标走）
    bool _dragPose;                         // 是否已拿到胶囊起始矩形（编组内胶囊的坐标只有浏览器知道）
    double _dragStartX, _dragStartY;        // 按下时的鼠标位置
    double _dragLastX, _dragLastY;          // 最近一次鼠标位置
    double? _dragSrcX, _dragSrcY;           // 自由胶囊在配置里的起始位置（无需问浏览器）
    double _dragX, _dragY;                  // 胶囊左上角（视口坐标）
    double _dragOffX, _dragOffY;            // 抓取点相对胶囊左上角的偏移
    double _dragW, _dragH;
    readonly Dictionary<string, ElementReference> _pillRefs = new();

    // ---- JS 辅助（视口 / 胶囊矩形 / 锚点与避让探测）----
    bool _jsReady;
    DateTime _jsLastTry = DateTime.MinValue;   // 失败后 30 秒再试一次（页面导航/脚本被清后能自愈）
    double _viewW, _viewH;
    double _anchorRight, _anchorTop, _anchorH; // 『展开思考』开关的位置（0=未探测到，退回窗口右上角）
    double _avoidRight = double.NaN;           // 其它插件注入挂件（TokenStats）的右边缘
    double _avoidTop = double.NaN;
    DateTime _lastProbeTime = DateTime.MinValue;
    int _tickIntervalMs = 250;

    // ---- 详情卡关合 ----
    Timer? _closeTimer;

    const double GroupMargin = 16;   // 编组距窗口右上角边距（探不到锚点时用）
    const double GroupGap = 5;       // 胶囊间距
    const double PillH = 30;         // 胶囊基准高度
    const double AnchorGap = 8;      // 编组距障碍物（『展开思考』/其它插件挂件）右侧的间隙
    const double PillNeedWidth = 130;// 估算的胶囊宽度（判断右侧放不放得下）

    /// <summary>拖拽中的胶囊（已越过阈值且拿到起始矩形）才脱离编组绝对定位；
    /// 未拿到矩形前保持原样，避免"按下瞬间闪到别处"</summary>
    bool Dragging => _dragName != null && _dragMoved && _dragPose;

    protected override void OnInitialized()
    {
        _timer = new Timer(_ => OnTick(), null, 250, 250);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _pokeClearTimer?.Dispose();
        _pokeClearTimer = null;
        _closeTimer?.Dispose();
        _closeTimer = null;
    }

    async void OnTick()
    {
        try
        {
            //每 2 秒探测一次锚点/避让/视口（窗口缩放、切换到别的页面、其它挂件出现都靠这个跟上）
            if (DateTime.Now - _lastProbeTime > TimeSpan.FromSeconds(2))
            {
                _lastProbeTime = DateTime.Now;
                await RefreshChromeAsync();
            }
            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            //组件已销毁 / 电路已断开：忽略
        }
    }

    /// <summary>有胶囊时 250ms 刷新一次让倒计时平滑跳动，空闲时降到 1s（网络与渲染开销都随之降下来）</summary>
    void AdjustTickRate()
    {
        if (_timer == null)
            return;
        int want = _snaps.Count > 0 ? 250 : 1000;
        if (want == _tickIntervalMs)
            return;
        _tickIntervalMs = want;
        _timer.Change(want, want);
    }

    #region 渲染

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        try
        {
            _snaps = CountdownOverlayManager.Snapshot();
        }
        catch
        {
            _snaps = [];
        }
        AdjustTickRate();

        if (_openName != null && _snaps.Any(c => c.Name == _openName) == false)
            _openName = null;

        if (_snaps.Count == 0)
            return;

        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        bool multi = _snaps.Count > 1;
        int s = 0;

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "id", "seb-ov-root");   //固定 id：便于日志/DevTools 排查挂件是否挂在页面上
        b.AddAttribute(s++, "style",
            "position:absolute;inset:0;pointer-events:none;overflow:hidden;" +
            "font-family:\"Segoe UI\",system-ui,\"Microsoft YaHei\",sans-serif;");
        // 拖拽靠根节点的冒泡事件（子元素 pointer-events:auto 时事件照常冒泡到 pointer-events:none 的祖先）：
        // 被拖拽的胶囊始终在光标下方，若把 mousemove 挂在捕获层上会被胶囊自己挡住。
        b.AddAttribute(s++, "onmousemove", EventCallback.Factory.Create<MouseEventArgs>(this, OnDragMove));
        b.AddAttribute(s++, "onmouseup", EventCallback.Factory.Create<MouseEventArgs>(this, OnDragUp));
        b.AddMarkupContent(s++, WidgetCss);

        // 编组：优先锚定『展开思考』开关右侧（探不到才退回窗口右上角），并在其它插件挂件占位时整体让开
        List<OverlayCharSnapshot> grouped = _snaps.Where(c => IsFreePill(c) == false).ToList();
        if (grouped.Count > 0)
        {
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", GroupStyle(grouped[0]));
            foreach (OverlayCharSnapshot c in grouped)
                RenderPill(b, ref s, c, multi, now, free: false);
            b.CloseElement();
        }

        // 自由定位的胶囊（含正在拖拽的那个）：直接挂在根节点下，按视口坐标绝对定位
        foreach (OverlayCharSnapshot c in _snaps.Where(IsFreePill))
            RenderPill(b, ref s, c, multi, now, free: true);

        b.CloseElement();
    }

    /// <summary>是否为「脱离编组、按视口坐标绝对定位」的胶囊（自带自由位置，或正在被拖拽）</summary>
    bool IsFreePill(OverlayCharSnapshot c) => c.FreeX != null || (Dragging && c.Name == _dragName);

    /// <summary>
    /// 编组定位。取「障碍物右边缘」（『展开思考』开关 + 其它插件注入的挂件）作为起点，
    /// 从它右边 <see cref="AnchorGap"/> 像素处**向右排**——按左边缘定位，胶囊绝不会压住开关；
    /// 右侧放不下时挪到开关下方（右对齐窗口）；探不到锚点（不在对话页）则退回窗口右上角。
    /// </summary>
    string GroupStyle(OverlayCharSnapshot first)
    {
        double scale = ScalePercentOf(first) / 100.0;
        string transform = $"transform:scale({scale:0.###});";

        double obstacle = _anchorRight;
        if (double.IsNaN(_avoidRight) == false && _avoidRight > obstacle)
            obstacle = _avoidRight;   //其它插件的挂件更靠右时以它为准（本挂件让到它右边）

        double need = PillNeedWidth * scale;
        if (obstacle > 0 && _viewW > 0 && obstacle + AnchorGap + need <= _viewW - 4)
            return "position:absolute;pointer-events:none;display:flex;flex-direction:column;align-items:flex-start;" +
                $"gap:{GroupGap}px;left:{obstacle + AnchorGap:0.#}px;top:{Math.Max(4, _anchorTop - 4):0.#}px;" +
                transform + "transform-origin:top left;";

        if (_anchorRight > 0)
            //右侧放不下：挪到开关下方，右对齐窗口（不压开关）
            return "position:absolute;pointer-events:none;display:flex;flex-direction:column;align-items:flex-end;" +
                $"gap:{GroupGap}px;right:{GroupMargin}px;top:{Math.Max(4, _anchorTop + _anchorH + 6):0.#}px;" +
                transform + "transform-origin:top right;";

        //探不到锚点：退回窗口右上角
        return "position:absolute;pointer-events:none;display:flex;flex-direction:column;align-items:flex-end;" +
            $"gap:{GroupGap}px;right:{GroupMargin}px;top:{GroupMargin}px;" +
            transform + "transform-origin:top right;";
    }

    double ScalePercentOf(OverlayCharSnapshot c) => Math.Clamp(c.ScalePercent, 60, 160);

    int OpacityPercentOf(OverlayCharSnapshot c) => Math.Clamp(c.OpacityPercent, 30, 100);

    void RenderPill(RenderTreeBuilder b, ref int s, OverlayCharSnapshot c, bool multi, long now, bool free)
    {
        bool isDragTarget = _dragName == c.Name && Dragging;   // 已越过阈值且拿到起始矩形，才按拖拽位置渲染
        bool isOpen = _openName == c.Name && isDragTarget == false;
        double scale = ScalePercentOf(c) / 100.0;
        string col = ColorOf(c.StateCode);
        long? target = TargetOf(c);
        bool hot = target != null && c.ClockMode == false && target.Value - now < 60_000;
        bool stale = c.TickAgeSeconds > 30;

        bool absolute = free || isDragTarget;
        // 拖拽中跟随鼠标（_dragX/_dragY），否则用配置里保存的自由位置
        double px = isDragTarget ? _dragX : FreeXOf(c);
        double py = isDragTarget ? _dragY : FreeYOf(c);
        string wrapStyle = (absolute
            ? $"position:absolute;pointer-events:none;left:{px:0}px;top:{py:0}px;" +
              $"transform:scale({scale:0.###});transform-origin:top left;" + (isDragTarget ? "z-index:3;" : "")
            : "position:relative;pointer-events:none;")
            + $"opacity:{OpacityPercentOf(c)}%;";

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", wrapStyle);
        b.AddAttribute(s++, "onmouseenter", EventCallback.Factory.Create<MouseEventArgs>(this, () => OpenCard(c.Name)));
        b.AddAttribute(s++, "onmouseleave", EventCallback.Factory.Create<MouseEventArgs>(this, () => ScheduleCloseCard(c.Name)));

        // ---- 胶囊本体 ----
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "sebOvPill" + (hot ? " hot" : "") + (isDragTarget && _dragMoved ? " drag" : ""));
        b.AddAttribute(s++, "style",
            $"display:flex;align-items:center;gap:6px;height:{PillH:0}px;padding:0 11px 0 7px;box-sizing:border-box;" +
            "background:rgba(255,255,255,.94);border:1.5px solid " + col + ";border-radius:999px;" +
            $"box-shadow:0 2px 10px {col}44;cursor:grab;pointer-events:auto;user-select:none;" +
            (hot ? "animation:sebOvPulse 1s ease-in-out infinite;" : ""));
        b.AddAttribute(s++, "title", $"{c.Name} · {c.StateText}"
            + (target == null ? " · 等待主人消息唤醒" : " · " + (c.ClockMode ? "点击切换为剩余倒计时" : "点击切换为时刻显示"))
            + " · 按住可拖动"
            + (c.FreeX != null ? " · 双击归位" : ""));
        b.AddAttribute(s++, "onmousedown", EventCallback.Factory.Create<MouseEventArgs>(this, e => _ = OnPillDownAsync(c, e)));
        b.AddAttribute(s++, "ondblclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => OnPillDoubleClick(c)));
        //注意：元素引用捕获必须放在所有属性之后——Blazor 规定属性帧只能紧跟 Element/Component 帧，
        //若把 AddElementReferenceCapture 插在属性中间，后续 AddAttribute 会抛
        //"Attributes may only be added immediately after frames of type Element or Component"。
        b.AddElementReferenceCapture(s++, r => _pillRefs[c.Name] = r);

        if (c.ShowRing && c.StateCode is not ("sleep" or "work" or "peak"))
            b.AddMarkupContent(s++, RingSvg(c, now, col));

        if (multi && c.ShowCharName)
        {
            b.OpenElement(s++, "span");
            b.AddAttribute(s++, "style", "font-size:10px;font-weight:600;color:#8a8f9c;max-width:56px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;pointer-events:none;");
            b.AddContent(s++, c.Name);
            b.CloseElement();
        }

        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", $"font-size:{(c.ClockMode ? 12 : 15)}px;font-weight:800;letter-spacing:.4px;font-variant-numeric:tabular-nums;line-height:1;color:{(hot ? "#ef4444" : col)};pointer-events:none;");
        b.AddContent(s++, NumTextOf(c, now));
        b.CloseElement();
        b.CloseElement();

        if (stale)
        {
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", "position:absolute;left:-2px;top:-2px;width:9px;height:9px;border-radius:50%;background:#dc2626;box-shadow:0 0 6px rgba(220,38,38,.7);");
            b.CloseElement();
        }

        // ---- 详情卡 ----
        if (isOpen)
        {
            //自由胶囊位置偏下时把卡片向上翻，避免跑出屏幕
            bool flipUp = absolute && _viewH > 0 && py + 60 > _viewH - 220;
            RenderCard(b, ref s, c, now, col, target, flipUp);
        }
        b.CloseElement();
    }

    void RenderCard(RenderTreeBuilder b, ref int s, OverlayCharSnapshot c, long now, string col, long? target, bool flipUp)
    {
        // 外层是"过渡桥"：用透明内边距把胶囊与卡片之间的空隙填满。
        // 否则鼠标从胶囊移向卡片时会穿过那段空隙 → 触发胶囊 wrapper 的 mouseleave → 卡片被收起，根本点不到。
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style",
            "position:absolute;right:0;width:248px;box-sizing:border-box;pointer-events:auto;" +
            (flipUp
                ? "bottom:100%;padding-bottom:8px;"
                : "top:100%;padding-top:8px;"));

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style",
            "width:100%;box-sizing:border-box;" +
            "background:#fffdf9;border:1px solid #f0d6e4;border-radius:14px;padding:12px 14px;" +
            "box-shadow:0 12px 32px rgba(23,27,40,.18),0 2px 8px rgba(23,27,40,.08);");

        // 头部
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:center;gap:6px;margin-bottom:8px;");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", $"width:8px;height:8px;border-radius:50%;flex:0 0 auto;background:{col};box-shadow:0 0 6px {col}99;");
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", "font-size:12.5px;font-weight:700;color:#3a4051;max-width:96px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;");
        b.AddContent(s++, c.Name);
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", $"margin-left:auto;font-size:9.5px;font-weight:600;color:{col};background:{col}14;border:1px solid {col}44;border-radius:999px;padding:1px 8px;max-width:92px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;");
        b.AddContent(s++, c.StateText);
        b.CloseElement();
        b.CloseElement();

        // 大倒计时
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;align-items:baseline;gap:8px;margin-bottom:8px;");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", $"font-size:27px;font-weight:800;letter-spacing:.5px;font-variant-numeric:tabular-nums;line-height:1;color:{col};");
        b.AddContent(s++, target == null ? "—" : FmtRem(target.Value - now));
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", "font-size:10px;color:#9aa1b0;");
        b.AddContent(s++, target == null
            ? "睡眠中 · 等主人消息唤醒"
            : (c.StateCode == "sleep" ? "距睡醒 · " : "距下次自主活跃 · ") + FmtClock(target.Value));
        b.CloseElement();
        b.CloseElement();

        // 睡眠状态行
        if (c.StateCode == "sleep" && c.SleepAwaitingUser)
            RenderRow(b, ref s, "睡眠", "等主人消息唤醒");

        // 间隔进度
        if (c.StateCode is not ("sleep" or "work" or "peak") && c.IntervalEndMs > c.IntervalStartMs)
        {
            double p = Math.Clamp((now - c.IntervalStartMs) / (double)(c.IntervalEndMs - c.IntervalStartMs), 0, 1);
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", "height:6px;background:#f3e9ef;border-radius:99px;margin:7px 0 3px;overflow:hidden;");
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", $"height:6px;border-radius:99px;min-width:2px;width:{p * 100:0.0}%;background:linear-gradient(90deg,{col}99,{col});");
            b.CloseElement();
            b.CloseElement();
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "style", "display:flex;justify-content:space-between;font-size:9.5px;color:#b6bac4;margin-bottom:8px;");
            b.OpenElement(s++, "span");
            b.AddContent(s++, "本间隔进度");
            b.CloseElement();
            b.OpenElement(s++, "span");
            b.AddContent(s++, $"{p * 100:0}%");
            b.CloseElement();
            b.CloseElement();
        }

        if (c.StateCode == "work" && c.WorkTotal > 0)
        {
            RenderRow(b, ref s, "工作进度", $"{c.WorkStep} / {c.WorkTotal}");
            if (string.IsNullOrEmpty(c.WorkTask) == false)
                RenderRow(b, ref s, "任务", c.WorkTask);
        }

        if (c.AwakeMs != null)
            RenderRow(b, ref s, "定点报时", FmtClock(c.AwakeMs.Value));

        if (c.TickAgeSeconds > 30)
            RenderRow(b, ref s, "更新循环", $"已停摆 {c.TickAgeSeconds:0} 秒（请到角色页停用后重新激活）");

        // 「催一下」
        bool pokeDone = _pokeResultName == c.Name && _pokeResult != null;
        b.OpenElement(s++, "button");
        b.AddAttribute(s++, "style",
            "display:block;width:100%;border:1.5px solid " + (pokeDone ? "#34d399" : "#ec4899") + ";" +
            "background:" + (pokeDone ? "#edfbf5" : "#fdf1f7") + ";color:" + (pokeDone ? "#0e9f6e" : "#ec4899") + ";" +
            "border-radius:999px;font-size:12px;font-weight:700;padding:6px 0;cursor:pointer;font-family:inherit;");
        b.AddAttribute(s++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => OnPoke(c)));
        b.AddContent(s++, pokeDone ? _pokeResult! : (c.StateCode == "work" ? "催促进度" : (c.StateCode == "sleep" ? "唤醒并活跃一下" : "催一下活跃")));
        b.CloseElement();

        // 底部
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;justify-content:space-between;align-items:center;margin-top:8px;");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", "font-size:9.5px;color:#b6bac4;");
        b.AddContent(s++, c.FreeX != null ? "按住拖动 · 双击归位" : "按住胶囊可拖到任意位置");
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", "font-size:10px;color:#c46a94;cursor:pointer;text-decoration:underline;");
        b.AddAttribute(s++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => OnHide(c)));
        b.AddContent(s++, "隐藏此倒计时");
        b.CloseElement();
        b.CloseElement();

        b.CloseElement();   //卡片本体
        b.CloseElement();   //过渡桥
    }

    void RenderRow(RenderTreeBuilder b, ref int s, string key, string value)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;justify-content:space-between;align-items:baseline;background:#fbf7fa;border:1px solid #f4ecf1;border-radius:8px;padding:4px 9px;font-size:11px;margin-bottom:5px;gap:8px;");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", "color:#9aa1b0;font-size:10px;flex:0 0 auto;");
        b.AddContent(s++, key);
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "style", "font-variant-numeric:tabular-nums;font-weight:650;color:#3a4051;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;");
        b.AddAttribute(s++, "title", value);
        b.AddContent(s++, value);
        b.CloseElement();
        b.CloseElement();
    }

    #endregion

    #region 交互

    /// <summary>
    /// 按下胶囊：只做"预备"（记录鼠标起点），不移动任何东西。
    /// 偏移量一律以「按下时的鼠标位置」为基准计算：
    /// - 自由胶囊：配置里就有坐标，直接算偏移，无需问浏览器 → 立刻可用；
    /// - 编组内胶囊：坐标只有浏览器知道，先量一次矩形再算偏移（量取期间胶囊保持原位不动，避免闪跳）。
    /// 这样无论量取快慢，胶囊相对光标的位置都严格一致，不会出现"按一下就跳到别处"。
    /// </summary>
    async Task OnPillDownAsync(OverlayCharSnapshot c, MouseEventArgs e)
    {
        if (e.Button != 0 || _dragName != null)
            return;

        _dragName = c.Name;
        _dragMoved = false;
        _dragPose = false;
        _dragStartX = _dragLastX = e.ClientX;
        _dragStartY = _dragLastY = e.ClientY;
        _dragSrcX = c.FreeX;
        _dragSrcY = c.FreeY;
        _dragW = 90;
        _dragH = PillH * ScalePercentOf(c) / 100.0;
        _dragX = 0;
        _dragY = 0;
        _dragOffX = 0;
        _dragOffY = 0;
        CloseCard(0);   //按下即收起详情卡（拖动期间不显示）
        StateHasChanged();

        if (_dragSrcX != null && _dragSrcY != null)
        {
            //自由胶囊：位置已知，直接得到偏移（拖拽即刻可用，且与当前视觉位置完全一致）
            _dragX = _dragSrcX.Value;
            _dragY = _dragSrcY.Value;
            _dragOffX = _dragStartX - _dragX;
            _dragOffY = _dragStartY - _dragY;
            _dragPose = true;
            await RefreshChromeAsync();
            return;
        }

        //编组内胶囊：量一次真实矩形与视口（失败则让胶囊落在光标中心下方，至少还能继续拖）
        double left = _dragStartX - _dragW / 2;
        double top = _dragStartY - _dragH / 2;
        await EnsureJsAsync();
        if (_jsReady && _pillRefs.TryGetValue(c.Name, out ElementReference el))
        {
            try
            {
                double[] rect = await Js.InvokeAsync<double[]>("__sebOvRect", el);
                if (rect.Length == 4 && rect[2] > 1)
                {
                    left = rect[0];
                    top = rect[1];
                    _dragW = rect[2];
                    _dragH = rect[3];
                }
            }
            catch { }
        }
        await RefreshChromeAsync();
        if (_dragName != c.Name)
            return;   //量取期间已经松手

        _dragX = left;
        _dragY = top;
        _dragOffX = _dragStartX - left;
        _dragOffY = _dragStartY - top;
        _dragPose = true;
        if (_dragMoved)
            ApplyDragPosition();   //把量取期间攒下的位移补上
    }

    void OnDragMove(MouseEventArgs e)
    {
        if (_dragName == null)
            return;

        _dragLastX = e.ClientX;
        _dragLastY = e.ClientY;

        //拖到窗口外再松手时收不到 mouseup，用「当前没有任何按键按下」兜底收尾
        if (_dragMoved && e.Buttons == 0)
        {
            OnDragUp(e);
            return;
        }

        if (_dragMoved == false)
        {
            double dx = e.ClientX - _dragStartX;
            double dy = e.ClientY - _dragStartY;
            if (dx * dx + dy * dy < 16)
                return;   // 未超过阈值：仍视作点击
            _dragMoved = true;
            CloseCard(0);
        }

        ApplyDragPosition();
    }

    /// <summary>按最近一次鼠标位置摆放胶囊（起始矩形未就绪时不动，避免闪跳）</summary>
    void ApplyDragPosition()
    {
        if (_dragPose == false)
            return;

        double x = _dragLastX - _dragOffX;
        double y = _dragLastY - _dragOffY;
        if (_viewW > 0 && _viewH > 0)
        {
            x = Math.Clamp(x, 2, Math.Max(2, _viewW - _dragW - 2));
            y = Math.Clamp(y, 2, Math.Max(2, _viewH - _dragH - 2));
        }
        _dragX = x;
        _dragY = y;
        StateHasChanged();
    }

    void OnDragUp(MouseEventArgs e)
    {
        string? name = _dragName;
        if (name == null)
            return;

        bool moved = _dragMoved && _dragPose;
        double x = _dragX;
        double y = _dragY;
        _dragName = null;
        _dragMoved = false;
        _dragPose = false;

        SystemEventBoostService? target = CountdownOverlayManager.Find(name);
        if (target != null)
        {
            if (moved)
                target.SetOverlayPillPosition(x, y);
            else
                target.ToggleOverlayClockMode();
        }
        StateHasChanged();
    }

    void OnPillDoubleClick(OverlayCharSnapshot c)
    {
        if (c.FreeX == null)
            return;
        CountdownOverlayManager.Find(c.Name)?.SetOverlayPillPosition(null, null);
        StateHasChanged();
    }

    void OnPoke(OverlayCharSnapshot c)
    {
        SystemEventBoostService? target = CountdownOverlayManager.Find(c.Name);
        if (target == null)
        {
            ShowPokeResult(c.Name, "角色未找到");
            return;
        }
        try
        {
            target.TriggerActivityNow();
            ShowPokeResult(c.Name, c.StateCode == "work" ? "已催促 ✓" : (c.StateCode == "sleep" ? "已唤醒 ✓" : "已发送 ✓"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[主动事件增强] 挂件催一下失败：{ex.Message}");
            ShowPokeResult(c.Name, "失败");
        }
        StateHasChanged();
    }

    void ShowPokeResult(string name, string text)
    {
        _pokeResultName = name;
        _pokeResult = text;
        _pokeClearTimer?.Dispose();
        _pokeClearTimer = new Timer(_ =>
        {
            try
            {
                _pokeResult = null;
                _pokeResultName = null;
                _ = InvokeAsync(StateHasChanged);
            }
            catch { }
        }, null, 1500, Timeout.Infinite);
    }

    void OnHide(OverlayCharSnapshot c)
    {
        CountdownOverlayManager.Find(c.Name)?.SetOverlayVisible(false);
        _openName = null;
        StateHasChanged();
    }

    #endregion

    #region 详情卡开合

    void OpenCard(string name)
    {
        _closeTimer?.Dispose();
        _closeTimer = null;
        if (_openName == name)
            return;
        _openName = name;
        StateHasChanged();
    }

    /// <summary>
    /// 延迟收起：鼠标从胶囊移向卡片时会经过一小段空隙（或经过一次重渲染），
    /// 立刻收起会导致"鼠标往下想点卡片，卡片就没了"。这里给 260ms 宽限，期间移进卡片（wrapper 的子元素）
    /// 会触发 again 的 mouseenter 把计时器取消掉。
    /// </summary>
    void ScheduleCloseCard(string name)
    {
        if (_dragName != null)
            return;
        _closeTimer?.Dispose();
        _closeTimer = new Timer(_ =>
        {
            try
            {
                if (_openName == name && _dragName == null)
                {
                    _openName = null;
                    _ = InvokeAsync(StateHasChanged);
                }
            }
            catch { }
        }, null, 260, Timeout.Infinite);
    }

    void CloseCard(int delayMs)
    {
        _closeTimer?.Dispose();
        _closeTimer = null;
        if (delayMs <= 0)
        {
            _openName = null;
            return;
        }
        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            try
            {
                _openName = null;
                _ = InvokeAsync(StateHasChanged);
            }
            catch { }
        });
    }

    #endregion

    #region JS 辅助（全部可失败：失败只是失去边界钳制与避让）

    async Task EnsureJsAsync()
    {
        if (_jsReady)
            return;
        if ((DateTime.Now - _jsLastTry).TotalSeconds < 30)
            return;   //失败后 30 秒再试：页面导航、脚本被清掉之后能自愈，又不会每拍都去撞失败路径
        _jsLastTry = DateTime.Now;
        try
        {
            // __sebOvProbe → [锚点右边缘, 锚点顶边, 锚点高, 避让挂件右边缘, 避让挂件顶边, 视口宽, 视口高]
            await Js.InvokeVoidAsync("eval",
                "window.__sebOvRect=function(e){var r=e.getBoundingClientRect();return [r.left,r.top,r.width,r.height]};" +
                "window.__sebOvProbe=function(){var o=[0,0,0,0,0,0,0];try{" +
                "var els=document.querySelectorAll('span,div,label,p');" +
                "for(var i=0;i<els.length;i++){var el=els[i];" +
                "if(el.children.length>0)continue;" +
                "if((el.textContent||'').trim().indexOf('展开思考')<0)continue;" +
                "var pe=el.parentElement,s=pe?pe.querySelector('.ant-switch'):null;" +
                "var x=(s&&s.getBoundingClientRect().width>0)?s:el;" +
                "if(x.getClientRects().length<1)continue;" +
                "var r=x.getBoundingClientRect();" +
                "if(r.width<5||r.height<5||r.top<0)continue;" +
                "o[0]=r.right;o[1]=r.top;o[2]=r.height;break}" +
                "var t=document.getElementById('tstats-root');" +
                "if(t){var tr=t.getBoundingClientRect();if(tr.width>0&&tr.top<220){o[3]=tr.right;o[4]=tr.top}}" +
                "}catch(e){}o[5]=window.innerWidth;o[6]=window.innerHeight;return o};void 0");
            _jsReady = true;
        }
        catch (Exception ex)
        {
            _jsReady = false;
            Console.WriteLine($"[主动事件增强] 挂件 JS 辅助不可用（不影响显示）：{ex.Message}");
        }
    }

    /// <summary>
    /// 探测『展开思考』锚点、其它插件注入挂件的右边缘与视口尺寸。
    /// 编组以「障碍物右边缘 + 间隙」为起点向右排（见 <see cref="GroupStyle"/>），
    /// 因此避让其它插件挂件是往**右**让，而不是压过去或往左挤。
    /// 只有开启了避让开关的角色才去探测避让挂件；JS 不可用时全部退回默认值。
    /// </summary>
    async Task RefreshChromeAsync()
    {
        try
        {
            await EnsureJsAsync();
            if (_jsReady == false)
                return;
            double[] p = await Js.InvokeAsync<double[]>("__sebOvProbe");
            if (p.Length < 7)
                return;
            if (p[0] > 0)
            {
                //探到锚点才更新：切到别的页面（锚点不在）时保持原地，不要跳回窗口右上角
                _anchorRight = p[0];
                _anchorTop = p[1];
                _anchorH = p[2];
            }
            bool avoidOn = _snaps.Count > 0 && _snaps.Any(c => c.AvoidOtherWidgets);
            _avoidRight = avoidOn && p[3] > 0 ? p[3] : double.NaN;
            _avoidTop = avoidOn && p[3] > 0 ? p[4] : double.NaN;
            if (p[5] > 0 && p[6] > 0)
            {
                _viewW = p[5];
                _viewH = p[6];
            }
        }
        catch { }
    }

    #endregion

    #region 展示工具

    static double FreeXOf(OverlayCharSnapshot c) => c.FreeX ?? 0;
    static double FreeYOf(OverlayCharSnapshot c) => c.FreeY ?? 0;

    static string ColorOf(string code) => code switch
    {
        "sleep" => "#8b5cf6",
        "work" => "#3b82f6",
        "game" => "#f59e0b",
        "cute" => "#f472b6",
        "dnd" => "#6b7280",
        "peak" => "#9ca3af",
        _ => "#ec4899",
    };

    /// <summary>倒计时目标：睡眠显示睡醒时刻（等主人唤醒时为 null），其余显示下次自主活跃</summary>
    static long? TargetOf(OverlayCharSnapshot c)
        => c.StateCode == "sleep" ? (c.SleepAwaitingUser ? null : c.SleepEndMs) : c.NextMs;

    static string NumTextOf(OverlayCharSnapshot c, long now)
    {
        long? t = TargetOf(c);
        if (t == null)
            return "等主人";
        return c.ClockMode ? FmtClock(t.Value) : FmtRem(t.Value - now);
    }

    static string FmtRem(long ms)
    {
        long s = Math.Max(0, ms) / 1000;
        if (s >= 86400)
            return $"{s / 86400}天{s % 86400 / 3600}时";
        long h = s / 3600, m = s % 3600 / 60, ss = s % 60;
        return h > 0 ? $"{h}:{m:00}:{ss:00}" : $"{m:00}:{ss:00}";
    }

    static string FmtClock(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("HH:mm:ss");

    /// <summary>间隔进度环（纯 SVG 字符串，随渲染重算）</summary>
    static string RingSvg(OverlayCharSnapshot c, long now, string col)
    {
        if (c.IntervalEndMs <= c.IntervalStartMs)
            return "";
        double p = Math.Clamp((now - c.IntervalStartMs) / (double)(c.IntervalEndMs - c.IntervalStartMs), 0, 1);
        double circumference = 2 * Math.PI * 7.5;
        return "<svg width=\"19\" height=\"19\" viewBox=\"0 0 19 19\" style=\"display:block;pointer-events:none\">" +
            "<circle cx=\"9.5\" cy=\"9.5\" r=\"7.5\" fill=\"none\" stroke=\"#f3e2ec\" stroke-width=\"2.4\"/>" +
            $"<circle cx=\"9.5\" cy=\"9.5\" r=\"7.5\" fill=\"none\" stroke=\"{col}\" stroke-width=\"2.4\" stroke-linecap=\"round\"" +
            $" stroke-dasharray=\"{(circumference * p):0.0} {circumference:0.0}\" transform=\"rotate(-90 9.5 9.5)\"/>" +
            "</svg>";
    }

    const string WidgetCss =
        "<style>" +
        "@keyframes sebOvPulse{50%{transform:translateY(-1px) scale(1.04)}}" +
        ".sebOvPill.drag{cursor:grabbing;opacity:.92;box-shadow:0 10px 26px rgba(23,27,40,.3)!important}" +
        ".sebOvPill button{font-family:inherit}" +
        "</style>";

    #endregion
}

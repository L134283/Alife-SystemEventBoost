using System;
using System.Collections.Generic;
using System.Linq;

namespace Alife.Plugin.SystemEventBoost;

/// <summary>单个角色的挂件展示快照（由模块实例实时生成，供全局挂件组件读取）</summary>
public readonly record struct OverlayCharSnapshot
{
    public required string Name { get; init; }
    /// <summary>状态码：period/sleep/work/game/cute/dnd/peak（决定挂件配色）</summary>
    public required string StateCode { get; init; }
    /// <summary>状态文本（与配置页 ActivityStatus 一致）</summary>
    public required string StateText { get; init; }
    /// <summary>下次自主活跃时刻（epoch 毫秒）</summary>
    public required long NextMs { get; init; }
    /// <summary>当前报点间隔起点（进度环用；无意义的模式为 0）</summary>
    public long IntervalStartMs { get; init; }
    /// <summary>当前报点间隔终点（进度环用）</summary>
    public long IntervalEndMs { get; init; }
    /// <summary>睡眠结束时刻（null=无睡眠倒计时）</summary>
    public long? SleepEndMs { get; init; }
    /// <summary>睡眠中等待主人消息唤醒</summary>
    public bool SleepAwaitingUser { get; init; }
    public int WorkStep { get; init; }
    public int WorkTotal { get; init; }
    public required string WorkTask { get; init; }
    /// <summary>定点报时时刻（null=未设置）</summary>
    public long? AwakeMs { get; init; }
    /// <summary>距最近一次框架更新回调的秒数（用于检测更新循环停摆）</summary>
    public double TickAgeSeconds { get; init; }
    /// <summary>配置开关</summary>
    public bool ShowRing { get; init; }
    public bool ShowCharName { get; init; }
    public bool ClockMode { get; init; }
    public int ScalePercent { get; init; }
    public int OpacityPercent { get; init; }
    public bool AvoidOtherWidgets { get; init; }
    /// <summary>胶囊自由位置 X（null=跟随编组锚定；随配置持久化，重启 Alife 保留）</summary>
    public double? FreeX { get; init; }
    /// <summary>胶囊自由位置 Y（null=跟随编组锚定）</summary>
    public double? FreeY { get; init; }
}

/// <summary>
/// 倒计时挂件的角色注册表（静态）。
/// 4.6.0 起挂件改为框架 globalUI 全局 Blazor 组件渲染（见 <see cref="CountdownOverlayWidget"/>），
/// 本类只保留"角色名 → 模块实例"的注册与快照生成：
/// 不再有本地 HTTP 服务、端口、令牌、心跳与 ElectronNET 注入注入，故障面大幅缩小。
/// </summary>
static class CountdownOverlayManager
{
    static void Log(string msg) => Console.WriteLine($"[主动事件增强] {msg}");

    // 角色名 → 模块实例（挂件渲染线程读，模块生命周期写）
    static readonly object sync = new();
    static readonly Dictionary<string, SystemEventBoostService> instances = new();

    /// <summary>注册模块实例（OnAwake 调用）。</summary>
    public static void Register(SystemEventBoostService instance)
    {
        string name = instance.CharacterName;
        if (name.Length == 0)
        {
            Log("挂件注册失败：角色名为空");
            return;
        }

        lock (sync)
            instances[name] = instance;
        Log($"倒计时挂件已注册角色 [{name}]（{StatusText}）");
    }

    /// <summary>注销模块实例（OnDestroy 调用）。</summary>
    public static void Unregister(SystemEventBoostService instance)
    {
        bool removed = false;
        lock (sync)
        {
            //按实例匹配移除（热重载时新实例可能已覆盖同名键）
            foreach (string key in instances
                         .Where(kv => ReferenceEquals(kv.Value, instance))
                         .Select(kv => kv.Key).ToList())
            {
                instances.Remove(key);
                removed = true;
            }
        }
        if (removed)
            Log($"倒计时挂件已注销角色 [{instance.CharacterName}]（{StatusText}）");
    }

    /// <summary>按角色名取模块实例（挂件交互：催一下/隐藏/拖动落点/切换显示模式）</summary>
    public static SystemEventBoostService? Find(string name)
    {
        lock (sync)
            return instances.GetValueOrDefault(name);
    }

    /// <summary>当前可见胶囊快照（按角色名排序；仅包含开启了挂件开关的角色）</summary>
    public static List<OverlayCharSnapshot> Snapshot()
    {
        List<OverlayCharSnapshot> snaps;
        lock (sync)
        {
            snaps = [];
            foreach (SystemEventBoostService instance in instances.Values)
            {
                try
                {
                    if (instance.Configuration.ShowCountdownOverlay == false)
                        continue;
                    snaps.Add(instance.BuildOverlaySnapshot());
                }
                catch (Exception ex)
                {
                    Log($"生成挂件快照失败（角色 [{instance.CharacterName}]）：{ex.Message}");
                }
            }
        }
        snaps.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return snaps;
    }

    /// <summary>挂件是否已被任意角色开启显示</summary>
    public static bool HasAnyVisible
    {
        get
        {
            lock (sync)
                return instances.Values.Any(i => i.Configuration.ShowCountdownOverlay);
        }
    }

    /// <summary>挂件运行状态（供配置面板显示）</summary>
    public static string StatusText
    {
        get
        {
            int total, visible;
            lock (sync)
            {
                total = instances.Count;
                visible = instances.Values.Count(i => i.Configuration.ShowCountdownOverlay);
            }
            if (total == 0)
                return "未运行（无已激活角色）";
            if (visible == 0)
                return $"已隐藏（{total} 个已激活角色均关闭了挂件开关）";
            return $"显示中 · {visible} 个角色";
        }
    }
}

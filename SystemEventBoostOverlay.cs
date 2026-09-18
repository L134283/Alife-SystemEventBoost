using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using ElectronNET.API;

namespace Alife.Plugin.SystemEventBoost;

/// <summary>单个角色的挂件展示快照（HTTP /state 数据源，全部时间用服务端 epoch 毫秒，JS 端只做差值）</summary>
public readonly record struct OverlayCharSnapshot
{
    public required string Name { get; init; }
    /// <summary>状态码：period/sleep/work/game/cute/dnd/peak（决定挂件配色）</summary>
    public required string StateCode { get; init; }
    /// <summary>状态文本（与配置页 ActivityStatus 一致）</summary>
    public required string StateText { get; init; }
    /// <summary>下次自主活跃时刻</summary>
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
    /// <summary>胶囊自由位置 X（null=编组锚定；随配置持久化，重启 Alife 保留）</summary>
    public double? FreeX { get; init; }
    /// <summary>胶囊自由位置 Y（null=编组锚定）</summary>
    public double? FreeY { get; init; }
}

/// <summary>
/// 对话面板倒计时挂件管理器（静态单例）：
/// - 多角色（多桌宠）共享一个本地 HTTP 服务与一个页面挂件，模块实例注册/注销（引用计数，最后一个注销时拆除）
/// - 通过 ElectronNET 向主窗口注入 JS：Shadow DOM 隔离样式，锚定『展开思考』开关附近，
///   并对其它插件的固定定位挂件（当前探测 TokenStats 的 #tstats-root）做包围盒避让
/// - 倒计时数字由 JS 用服务端时间戳本地每 200ms 平滑递减，/state 轮询 2s 一次仅用于同步状态
/// - 存活判定不依赖 ElectronNET 的 IPC 应答（应答事件名固定、全局单 handler，多插件并发会互相顶掉应答→先调用方
///   永久挂起：桌宠"卡在加载 live2d"与本插件"挂件消失"同一个根因），改由注入脚本带令牌回连 /ping 上报心跳
///   （心跳里带自检串）。心跳新鲜期间完全不占用 ElectronNET 桥；只有心跳缺失才注入一次（3s 冷却 + 无应答指数退避）
/// - 每个胶囊可按住拖到屏幕任意位置，位置随角色配置持久化（跨 Alife 重启保留），双击归位
/// - /poke 触发活跃需要页面令牌（防外部网页滥用烧 token）；/ping（心跳）、/place（挪位置）与 /vis（按角色显示/隐藏，与设置页开关同源）无危害保持开放
/// </summary>
static class CountdownOverlayManager
{
    static void Log(string msg) => Console.WriteLine($"[主动事件增强] {msg}");
    static void LogWarn(string msg) => Console.WriteLine($"[主动事件增强][警告] {msg}");

    // ---- 注册表：角色名 → 模块实例（HTTP 线程读、模块生命周期写）----
    static readonly object sync = new();
    static readonly Dictionary<string, SystemEventBoostService> instances = new();

    // ---- 本地 HTTP 服务（127.0.0.1，GET /state、GET /ping、POST /poke、POST /place）----
    static CancellationTokenSource? serverCts;
    static TcpListener? listener;
    static int actualPort;
    static int preferredPort;                 //配置的首选端口：漂移后定期尝试回迁
    static string authToken = "";             //每次启动服务生成：注入进页面做 /poke 鉴权，也用于校验心跳是不是本轮服务
    static DateTime lastServerAttempt = DateTime.MinValue;
    static DateTime lastReclaimAttempt = DateTime.MinValue;

    // ---- 挂件心跳（注入脚本带令牌回连 /ping）----
    // 绝不能用 ElectronNET 的 ExecuteJavaScriptAsync 应答来判断挂件存活：
    // 它的应答事件名固定（"webContents-executeJavaScript-completed"）、全局只保留一个 handler
    // （SocketIOConnection.Once → SocketIO.On 内部是"先 Remove 同名 key 再 Add"），且没有超时。
    // 于是任何两个并发的 ExecuteJavaScriptAsync 会互相顶掉应答：后注册者生效，先注册者的 TCS 永远不完成 →
    // 先调用方永久挂起。桌宠"卡在加载 live2d"与本插件"挂件消失"同一个根因（谁先谁倒霉，所以表现为随机）。
    // 因此改为：注入脚本主动带令牌回连本地服务上报心跳，心跳新鲜=挂件确实挂在该页面上，此时完全不碰 ElectronNET 桥。
    static DateTime lastPingTime = DateTime.MinValue;
    static string pingToken = "";             //最近一次心跳携带的令牌（须等于当前 authToken 才算存活）
    static string pingDiag = "";              //最近一次心跳携带的挂件自检串（胶囊数/显隐/失败次数等）
    static bool pingVisible = true;           //最近一次心跳时页面是否可见（隐藏页面定时器会被浏览器节流，需放宽心跳容忍时间）
    static DateTime lastInjectTime = DateTime.MinValue;   //最近一次注入尝试（注入冷却，避免连环注入白占 ElectronNET 桥）
    static DateTime nextInjectAllowedTime = DateTime.MinValue;  //允许注入的最早时刻（服务启动/回迁后先让开别人的激活窗口）
    static int ipcHangCount;                  //连续"注入未拿到应答"次数（指数退避：不与其它插件互抢应答）

    // ---- 注入状态 ----
    static BrowserWindow? mainWindow;
    static DateTime lastEnsureTime = DateTime.MinValue;
    static readonly SemaphoreSlim ipcLock = new(1, 1);   // ElectronNET 应答不带窗口Id，本插件内的 IPC 调用全局串行
    static string overlayState = "未启动";      //string 引用读写原子，跨线程最多读到上一拍状态，无功能影响

    /// <summary>IPC 调用结果：Locked=拿到串行锁；Completed=拿到应答；Hung=超时未应答（应答很可能被其它插件顶掉）</summary>
    readonly record struct IpcCallResult<T>(bool Locked, bool Completed, bool Hung, T? Value) where T : class;

    /// <summary>挂件心跳是否新鲜且来自本次服务实例（须在 sync 锁内调用）</summary>
    static bool OverlayAliveNoLock()
    {
        if (serverCts == null || actualPort == 0 || authToken.Length == 0)
            return false;
        if (string.Equals(pingToken, authToken, StringComparison.Ordinal) == false)
            return false;   //令牌不符=页面里还是上一轮注入的脚本（服务重启过/端口变了），视为不存活，重新注入
        //页面隐藏时（窗口最小化/被遮挡）浏览器会把定时器节流到约 1 次/分钟，心跳自然变稀：
        //此时放宽容忍时间，避免误判"挂件丢了"而反复重注入（白白占用 ElectronNET 桥）
        int toleranceSeconds = pingVisible ? 6 : 180;
        return (DateTime.Now - lastPingTime).TotalSeconds < toleranceSeconds;
    }

    /// <summary>挂件运行状态（供日志）：端口 + 心跳/注入结果 + 注册角色数</summary>
    public static string OverlayStatus
    {
        get
        {
            int count;
            string state;
            lock (sync)
            {
                count = instances.Count;
                state = OverlayAliveNoLock()
                    ? $"心跳正常{(pingDiag.Length > 0 ? "·" + pingDiag : "")}"
                    : $"注入[{overlayState}]";
            }
            if (count == 0)
                return "挂件未运行（无已激活角色）";
            if (serverCts == null)
                return $"挂件服务未启动（端口 {preferredPort} 被占用，稍后自动重试）";
            return $"挂件运行中 · 端口 {actualPort} · {state} · {count} 个角色";
        }
    }

    /// <summary>注册模块实例（OnStart 调用）。首个注册者启动 HTTP 服务并立即注入挂件。</summary>
    public static void Register(SystemEventBoostService instance)
    {
        string name = instance.CharacterName;
        if (name.Length == 0)
        {
            LogWarn("挂件注册失败：角色名为空");
            return;
        }

        lock (sync)
        {
            instances[name] = instance;
        }

        bool serverOk;
        lock (sync)
            serverOk = serverCts != null || StartServer(instance.Configuration.OverlayHttpPort);

        if (serverOk)
            _ = EnsureAsync(force: true);
        else
            LogWarn($"倒计时挂件：HTTP 服务启动失败（角色 [{name}] 已注册，端口可用后自动恢复）");
        Log($"倒计时挂件已注册角色 [{name}]（{OverlayStatus}）");
    }

    /// <summary>注销模块实例（OnDestroy 调用）。最后一个注销时拆除页面挂件并停止服务。</summary>
    public static void Unregister(SystemEventBoostService instance)
    {
        lock (sync)
        {
            //按实例匹配移除（热重载时新实例可能已覆盖同名键）
            foreach (KeyValuePair<string, SystemEventBoostService> kv in instances
                         .Where(kv => ReferenceEquals(kv.Value, instance)).ToList())
                instances.Remove(kv.Key);

            if (instances.Count > 0)
                return;

            //引用计数归零：拆挂件、停服务（IPC 可能已失效，失败无害：残留挂件会在下次注入时自清理）
            serverCts?.Cancel();
            serverCts = null;
            listener = null;
            overlayState = "已停止";
        }

        //这里故意不再通过 IPC 通知页面拆除挂件：
        //1) 少一次占用共享 ElectronNET 桥的调用（与桌宠等插件的并发调用会互相顶掉应答）；
        //2) "停用 A 立刻激活 B"时，异步发出的拆除消息可能晚于新挂件的注入到达，把刚注入的挂件删掉。
        //页面侧自己收尾：/state 拉不到（服务已停）连续失败后先隐藏，最终自删（见注入脚本）。
        Log("倒计时挂件：所有角色已停止，挂件与服务已停止（页面挂件将自行退出）");
    }

    /// <summary>
    /// 注入自愈（每实例 OnUpdate 调用；内部全局节流 ≥1.2s）。
    /// 页面导航/窗口重建后自动重新注入；配置开关实时生效（无需重启角色）。
    /// </summary>
    public static async Task EnsureAsync(bool force = false)
    {
        try
        {
            lock (sync)
                if (instances.Count == 0)
                    return;

            //配置开关实时检查：所有角色的开关都关闭则拆除整个挂件（任一开启即注入/保留）
            bool anyOn;
            lock (sync)
                anyOn = instances.Values.Any(i => i.Configuration.ShowCountdownOverlay);
            if (anyOn == false)
            {
                if (overlayState != "配置已关闭")
                {
                    //不发 IPC 拆除：/state 会返回空的角色列表，页面侧自己隐藏（任一角色重新开启即自动恢复）
                    overlayState = "配置已关闭";
                    Log("倒计时挂件：所有角色的开关均已关闭，页面挂件将自行隐藏（任一角色重新开启即恢复）");
                }
                return;
            }

            //服务自愈：启动失败过则每 30s 重试一次（端口可能已释放）
            if (serverCts == null)
            {
                lock (sync)
                {
                    if (serverCts == null && (DateTime.Now - lastServerAttempt).TotalSeconds > 30)
                    {
                        lastServerAttempt = DateTime.Now;
                        SystemEventBoostService? any = instances.Values.FirstOrDefault();
                        if (any != null)
                            StartServer(any.Configuration.OverlayHttpPort);
                    }
                    if (serverCts == null)
                    {
                        overlayState = "端口不可用";
                        return;
                    }
                }
            }

            //节流：正常 1.2s 检查一次；连续"注入未拿到应答"时指数退避（桥被挂起的调用占住时，密集重试只会互相抢应答）
            lock (sync)
            {
                int minIntervalMs = ipcHangCount > 0
                    ? Math.Min(1200 << Math.Min(ipcHangCount, 3), 15000)
                    : 1200;
                if (force == false && (DateTime.Now - lastEnsureTime).TotalMilliseconds < minIntervalMs)
                    return;
                lastEnsureTime = DateTime.Now;

                //心跳新鲜（且令牌/端口与当前服务一致）= 挂件确实挂在该页面上：本轮完全不碰 ElectronNET 桥，
                //把桥让给桌宠等其它使用方，从根上避免"并发 ExecuteJavaScriptAsync 互相顶掉应答"
                if (OverlayAliveNoLock())
                {
                    overlayState = "心跳正常";
                    return;
                }

                //注入冷却：注入后给心跳留出到达时间，避免连环注入
                if ((DateTime.Now - lastInjectTime).TotalSeconds < 3)
                {
                    overlayState = "等待挂件心跳";
                    return;
                }

                //服务刚启动/回迁时先让开别人的窗口（例如桌宠建窗 + 注入模块）再注入：
                //并发 ExecuteJavaScriptAsync 会互相顶掉应答，晚一点能显著降低撞车概率
                if (DateTime.Now < nextInjectAllowedTime)
                {
                    overlayState = "等待注入时机";
                    return;
                }
            }

            //端口回迁：曾因端口被占漂移到备用端口时，每 60s 尝试回首选端口（挂件凭端口标记自动重注入）
            if (actualPort != preferredPort && (DateTime.Now - lastReclaimAttempt).TotalSeconds > 60)
            {
                lastReclaimAttempt = DateTime.Now;
                TryRebindPreferredPort();
            }

            BrowserWindow? win;
            lock (sync)
                win = mainWindow;
            if (win == null)
            {
                try
                {
                    win = Electron.WindowManager.BrowserWindows.OrderBy(w => w.Id).FirstOrDefault();
                }
                catch { }
                if (win == null)
                {
                    overlayState = "无窗口";
                    return;
                }
                lock (sync)
                    mainWindow = win;
            }
            BrowserWindow main = win;

            //心跳缺失（页面刚导航/挂件被页面重建清掉/端口或令牌变了）：重新注入一次（注入脚本会先自清理残留）。
            //注入结果只作日志参考，不做控制流依据——应答可能被其它插件的同类调用顶掉（那时本调用会永久挂起），
            //注入是否成功一律由注入后的心跳说了算。
            int gap = 10;
            lock (sync)
            {
                SystemEventBoostService? any = instances.Values.FirstOrDefault();
                if (any != null)
                    gap = Math.Clamp(any.Configuration.OverlayGap, 0, 200);
                lastInjectTime = DateTime.Now;
            }
            string inject = OverlayJs
                .Replace("__PORT__", actualPort.ToString())
                .Replace("__GAP__", gap.ToString())
                .Replace("__TOKEN__", authToken);

            IpcCallResult<string> build = await IpcAsync(() => main.WebContents.ExecuteJavaScriptAsync<string>(inject));
            string reply = (build.Value ?? "").Trim().Trim('"');
            if (build.Locked == false)
                overlayState = "IPC忙";
            else if (build.Completed == false)
            {
                //没拿到应答（被顶掉/页面未响应）：退避重试，等心跳到自己会转正
                int hangs = Interlocked.Increment(ref ipcHangCount);
                overlayState = "注入未应答·将退避重试";
                if (hangs == 1)
                    LogWarn("倒计时挂件注入未拿到应答（ElectronNET 的 ExecuteJavaScriptAsync 应答事件全局单 handler，"
                        + "与桌宠等插件的并发调用会互相顶掉，也可能页面正忙），已转为退避重试");
            }
            else if (reply == "nopage")
                overlayState = "页面未就绪";
            else
            {
                Interlocked.Exchange(ref ipcHangCount, 0);
                overlayState = "已注入·等待心跳";
                Log($"倒计时挂件已注入对话面板（本地服务 http://127.0.0.1:{actualPort}/state）");
            }
        }
        catch (Exception ex)
        {
            overlayState = "error:" + ex.Message;
            LogWarn($"倒计时挂件注入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// IPC 串行执行（ElectronNET 应答不带窗口Id，本插件内的调用必须串行）。
    /// 两点关键：
    /// 1) 锁获取 5 秒超时：上一笔调用若卡住，跳过本轮而不是跟着卡死。
    /// 2) 单次调用 2.5 秒超时后【立即归还锁】——底层 ExecuteJavaScriptAsync 一旦永久挂起
    ///    （应答事件全局单 handler，被其它插件的并发调用顶掉就会永不返回），若继续持锁，
    ///    之后所有注入都只能拿到"IPC忙"，挂件被拆掉后再也回不来（多桌宠场景下的实际故障）。
    /// </summary>
    static async Task<IpcCallResult<T>> IpcAsync<T>(Func<Task<T>> call, int timeoutMs = 2500) where T : class
    {
        if (await ipcLock.WaitAsync(TimeSpan.FromSeconds(5)) == false)
            return new IpcCallResult<T>(Locked: false, Completed: false, Hung: false, Value: null);

        bool released = false;
        try
        {
            Task<T> task = call();
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
            {
                ipcLock.Release();
                released = true;
                return new IpcCallResult<T>(Locked: true, Completed: false, Hung: true, Value: null);
            }
            if (task.Status != TaskStatus.RanToCompletion)
                return new IpcCallResult<T>(Locked: true, Completed: false, Hung: false, Value: null);
            return new IpcCallResult<T>(Locked: true, Completed: true, Hung: false, Value: task.Result);
        }
        catch
        {
            return new IpcCallResult<T>(Locked: true, Completed: false, Hung: false, Value: null);
        }
        finally
        {
            if (released == false)
                ipcLock.Release();
        }
    }

    #region 本地 HTTP 服务

    /// <summary>启动 HTTP 服务（端口被占时向后扫描 20 个）。调用方须持有 sync 锁。</summary>
    static bool StartServer(int configPort)
    {
        preferredPort = Math.Clamp(configPort, 1, 65535);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int port = Math.Clamp(configPort + attempt, 1, 65535);
            TcpListener? candidate = null;
            try
            {
                candidate = new TcpListener(IPAddress.Loopback, port);
                candidate.Start(4);
                listener = candidate;
                actualPort = port;
                authToken = Guid.NewGuid().ToString("N");
                //服务刚起来先别急着注入：让开同一时刻可能正在建窗/注入模块的其它插件（例如桌宠）的窗口
                nextInjectAllowedTime = DateTime.Now.AddSeconds(5);
                serverCts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoopAsync(candidate, serverCts.Token));
                return true;
            }
            catch
            {
                try { candidate?.Stop(); } catch { }
                if (attempt == 19)
                    LogWarn($"倒计时挂件：端口 {configPort}~{port} 均不可用，挂件服务未开启（稍后自动重试）");
            }
        }
        return false;
    }

    /// <summary>曾漂移到备用端口时尝试回迁首选端口（须在 sync 锁内调用）。</summary>
    static void TryRebindPreferredPort()
    {
        TcpListener? candidate = null;
        try
        {
            candidate = new TcpListener(IPAddress.Loopback, preferredPort);
            candidate.Start(4);
            //新端口绑定成功：切换服务（令牌不变，页面挂件凭端口标记自动重注入）
            serverCts?.Cancel();
            listener = candidate;
            actualPort = preferredPort;
            nextInjectAllowedTime = DateTime.Now.AddSeconds(5);
            serverCts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(candidate, serverCts.Token));
            Log($"倒计时挂件服务已回迁首选端口 {preferredPort}（挂件将自动重连）");
        }
        catch
        {
            try { candidate?.Stop(); } catch { }
        }
    }

    static async Task AcceptLoopAsync(TcpListener server, CancellationToken ct)
    {
        try
        {
            while (ct.IsCancellationRequested == false)
            {
                TcpClient client = await server.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogWarn($"倒计时挂件服务循环退出：{ex.Message}"); }
        finally
        {
            try { server.Stop(); } catch { }
        }
    }

    static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                NetworkStream stream = client.GetStream();
                //循环读到 HTTP 头结束（\r\n\r\n）为止，容忍 TCP 分包；本服务无请求体
                byte[] buffer = new byte[8192];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                    if (read <= 0)
                        break;
                    total += read;
                    if (Encoding.UTF8.GetString(buffer, 0, total).Contains("\r\n\r\n"))
                        break;
                }
                if (total == 0)
                    return;
                string request = Encoding.UTF8.GetString(buffer, 0, total);
                string firstLine = request.Split('\r', '\n')[0];
                string[] parts = firstLine.Split(' ');
                string method = parts.Length > 0 ? parts[0] : "GET";
                string path = parts.Length > 1 ? parts[1] : "/";
                int queryIndex = path.IndexOf('?');
                string query = queryIndex >= 0 ? path[(queryIndex + 1)..] : "";
                if (queryIndex >= 0)
                    path = path[..queryIndex];

                if (path == "/ping" && method == "GET")
                {
                    //GET /ping?t=令牌&d=自检串 → 注入脚本的心跳（带令牌，无需鉴权也无危害）。
                    //令牌与当前服务一致且时间新鲜才算"挂件挂在该页面上"；令牌不符说明页面里还是上一轮注入的脚本
                    //（服务重启过/端口变了），判定为不存活并重新注入。
                    string pingT = QueryValue(query, "t") ?? "";
                    string pingV = QueryValue(query, "v") ?? "1";
                    string pingD = QueryValue(query, "d") ?? "";
                    lock (sync)
                    {
                        pingToken = pingT;
                        pingVisible = pingV != "0";
                        pingDiag = pingD;
                        lastPingTime = DateTime.Now;
                        if (string.Equals(pingT, authToken, StringComparison.Ordinal))
                            ipcHangCount = 0;   //心跳在=桥是通的，清掉注入退避
                    }
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes("{\"ok\":true}"), ct);
                }
                else if (path == "/state" && method == "GET")
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(BuildStateJson()), ct);
                else if (path == "/poke" && method == "POST")
                {
                    //POST /poke?name=角色名&t=页面令牌 → 立即触发一次自主活跃（令牌仅存在于注入页面，防外部网页滥用）
                    if (QueryValue(query, "t") != authToken)
                    {
                        await RespondAsync(stream, "403 Forbidden", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":false}"), ct);
                        return;
                    }
                    string? name = QueryValue(query, "name");

                    SystemEventBoostService? target = null;
                    if (name != null)
                        lock (sync)
                            instances.TryGetValue(name, out target);

                    if (target != null)
                    {
                        target.TriggerActivityNow();
                        await RespondAsync(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":true}"), ct);
                    }
                    else
                        await RespondAsync(stream, "404 Not Found", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":false}"), ct);
                }
                else if (path == "/vis" && method == "POST")
                {
                    //POST /vis?name=角色名&on=0|1 → 显示/隐藏该角色的胶囊（详情卡"隐藏此倒计时"用；
                    //与设置页开关同一状态源，仅改配置不烧 token，保持开放便于联调）
                    string? name = QueryValue(query, "name");
                    string? on = QueryValue(query, "on");
                    SystemEventBoostService? target = null;
                    if (name != null)
                        lock (sync)
                            instances.TryGetValue(name, out target);

                    if (target != null && (on == "0" || on == "1"))
                    {
                        target.SetOverlayVisible(on == "1");
                        await RespondAsync(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":true}"), ct);
                    }
                    else
                        await RespondAsync(stream, "404 Not Found", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":false}"), ct);
                }
                else if (path == "/place" && method == "POST")
                {
                    //POST /place?name=角色名[&x=..&y=..]：带坐标=记住胶囊自由位置；不带=归位（随角色配置持久化）
                    string? name = QueryValue(query, "name");
                    SystemEventBoostService? target = null;
                    if (name != null)
                        lock (sync)
                            instances.TryGetValue(name, out target);

                    if (target != null)
                    {
                        string? xs = QueryValue(query, "x");
                        string? ys = QueryValue(query, "y");
                        double? x = null, y = null;
                        if (xs != null && ys != null
                            && double.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out double px)
                            && double.TryParse(ys, NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
                        {
                            x = px;
                            y = py;
                        }
                        target.SetOverlayPillPosition(x, y);
                        await RespondAsync(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":true}"), ct);
                    }
                    else
                        await RespondAsync(stream, "404 Not Found", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":false}"), ct);
                }
                else
                    await RespondAsync(stream, "404 Not Found", "text/plain; charset=utf-8",
                        "404"u8.ToArray(), ct);
            }
        }
        catch { }
    }

    static string? QueryValue(string query, string key)
    {
        int i = query.IndexOf(key + "=", StringComparison.Ordinal);
        if (i < 0)
            return null;
        int start = i + key.Length + 1;
        int end = query.IndexOf('&', start);
        return Uri.UnescapeDataString(query[start..(end < 0 ? query.Length : end)]);
    }

    static async Task RespondAsync(NetworkStream stream, string status, string contentType, byte[] body, CancellationToken ct)
    {
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    static string BuildStateJson()
    {
        List<OverlayCharSnapshot> snaps;
        lock (sync)
            snaps = instances.Values
                .Where(i => i.Configuration.ShowCountdownOverlay)   //每角色开关控制自己的胶囊
                .Select(i => i.BuildOverlaySnapshot()).ToList();
        snaps.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        StringBuilder sb = new(1024);
        sb.Append("{\"ovs\":\"").Append(JsonEsc(overlayState))
          .Append("\",\"win\":").Append(mainWindow != null ? "true" : "false")
          .Append(",\"now\":").Append(DateTimeOffset.Now.ToUnixTimeMilliseconds()).Append(",\"chars\":[");
        for (int i = 0; i < snaps.Count; i++)
        {
            OverlayCharSnapshot s = snaps[i];
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"name\":\"").Append(JsonEsc(s.Name))
              .Append("\",\"code\":\"").Append(JsonEsc(s.StateCode))
              .Append("\",\"text\":\"").Append(JsonEsc(s.StateText))
              .Append("\",\"next\":").Append(s.NextMs)
              .Append(",\"ivs\":").Append(s.IntervalStartMs)
              .Append(",\"ive\":").Append(s.IntervalEndMs)
              .Append(",\"slp\":").Append(s.SleepEndMs.HasValue ? s.SleepEndMs.Value.ToString() : "null")
              .Append(",\"swu\":").Append(s.SleepAwaitingUser ? "true" : "false")
              .Append(",\"ws\":").Append(s.WorkStep)
              .Append(",\"wt\":").Append(s.WorkTotal)
              .Append(",\"wtk\":\"").Append(JsonEsc(s.WorkTask))
              .Append("\",\"awk\":").Append(s.AwakeMs.HasValue ? s.AwakeMs.Value.ToString() : "null")
              .Append(",\"fx\":").Append(s.FreeX.HasValue ? s.FreeX.Value.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"fy\":").Append(s.FreeY.HasValue ? s.FreeY.Value.ToString(CultureInfo.InvariantCulture) : "null")
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string JsonEsc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    #endregion

    #region 注入 JS

    // （已移除 ElectronNET IPC 探测与 IPC 拆除：挂件存活由注入脚本回连 /ping 心跳判定，
    //   拆除由页面侧在长时间连不上本地服务后自行完成，本插件除了"注入一次"外不再占用 ElectronNET 桥。）
    //   既彻底避开"并发 ExecuteJavaScriptAsync 互相顶掉应答导致永久挂起"，也把每次自愈探测的 IPC 调用降为 0。
    //   心跳携带自检串，日志/状态里仍能看到胶囊数、显隐、fetch 失败次数、矩形等诊断信息。）

    // 挂件注入脚本。要点：
    // - root id=seb-cd-root、Shadow DOM 隔离、全局 __sebCdTeardown 可幂等拆除（跨激活/换端口安全）
    // - 定位：锚定『展开思考』开关（DOM 顺序第一个）右侧；TokenStats 圆环占据该位时放它右侧；
    //   锚点消失（切换页面）保持原位；每秒重放 + resize + ResizeObserver 跟随
    // - 倒计时：/state 返回服务端 now，JS 以本地流逝时间插值每 200ms 刷新；睡眠中显示睡眠剩余
    // - 胶囊可拖动：按住移动超阈值脱离编组为独立 fixed 元素；位置随角色配置持久化（POST /place），
    //   /state 每轮带回服务端位置做同步；双击归位；拖动期间暂停轮询重建
    // - 详情卡：悬停胶囊展开（按角色名定位，角色消失自动关闭；方向自适应）；
    //   关闭用 document 级 mousemove 监测（仅算当前悬停胶囊+卡片并集，避免拖散后判定区域过大），
    //   鼠标移出窗口/窗口失焦兜底收起
    // - /poke 携带注入时下发的令牌
    const string OverlayJs = """
        (function(){
          var PORT=__PORT__,GAP=__GAP__,TOKEN='__TOKEN__';
          if(!document.body)return 'nopage';
          if(window.__sebCdTeardown){try{window.__sebCdTeardown()}catch(e){}}
          try{localStorage.removeItem('sebCdPos');localStorage.removeItem('sebCdHidden')}catch(e){}   //旧版本遗留（位置已迁服务端；隐藏已改为按角色配置）
          var root=document.createElement('div');
          root.id='seb-cd-root';
          root.dataset.sebp=String(PORT);
          var rs=root.style;rs.position='fixed';rs.zIndex='99999';rs.pointerEvents='none';rs.left='0';rs.top='0';
          var sh=root.attachShadow({mode:'open'});
          sh.innerHTML='<style>'+
          '*{margin:0;padding:0;box-sizing:border-box}'+
          '.wrap{font-family:"Segoe UI",system-ui,"Microsoft YaHei",sans-serif;display:flex;flex-direction:row;flex-wrap:wrap;gap:5px;max-width:300px;justify-content:flex-end}'+
          '.pill{display:flex;align-items:center;gap:6px;height:30px;padding:0 11px 0 7px;background:rgba(255,255,255,.94);border:1.5px solid #ec4899;border-radius:999px;box-shadow:0 2px 10px rgba(236,72,153,.28);cursor:grab;pointer-events:auto;user-select:none;transition:transform .15s,box-shadow .15s}'+
          '.pill:hover{transform:translateY(-1px);box-shadow:0 4px 14px rgba(236,72,153,.4)}'+
          '.pill svg{display:block;pointer-events:none}'+
          '.pill .num,.pill .nm{pointer-events:none}'+
          '.num{font-size:15px;font-weight:800;letter-spacing:.4px;font-variant-numeric:tabular-nums;line-height:1;color:#ec4899}'+
          '.nm{font-size:10px;font-weight:600;color:#8a8f9c;max-width:56px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}'+
          '.pill.hot .num{color:#ef4444}'+
          '.pill.hot{border-color:#ef4444;box-shadow:0 2px 12px rgba(239,68,68,.4);animation:sebPulse 1s ease-in-out infinite}'+
          '.pill.hold .num{font-size:12px}'+
          '@keyframes sebPulse{50%{transform:translateY(-1px) scale(1.04)}}'+
          '.pill.flash{animation:sebFlash .5s ease-out 2}'+
          '@keyframes sebFlash{50%{box-shadow:0 0 0 5px rgba(236,72,153,.25)}}'+
          '.pill.dragging{cursor:grabbing;opacity:.92;transform:scale(1.06);box-shadow:0 10px 26px rgba(23,27,40,.3)}'+
          '.card{position:absolute;width:248px;background:#fffdf9;border:1px solid #f0d6e4;border-radius:14px;box-shadow:0 12px 32px rgba(23,27,40,.18),0 2px 8px rgba(23,27,40,.08);padding:12px 14px;pointer-events:auto;opacity:0;transform:scale(.96);transform-origin:top right;transition:opacity .15s,transform .15s;visibility:hidden}'+
          '.wrap.on .card{opacity:1;transform:scale(1);visibility:visible}'+
          '.hd{display:flex;align-items:center;gap:6px;margin-bottom:8px}'+
          '.dot{width:8px;height:8px;border-radius:50%;background:#ec4899;box-shadow:0 0 6px rgba(236,72,153,.6);flex:0 0 auto}'+
          '.cname{font-size:12.5px;font-weight:700;color:#3a4051;max-width:96px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}'+
          '.stag{margin-left:auto;font-size:9.5px;color:#9d5b7d;background:#fdf1f7;border:1px solid #f6dcea;border-radius:999px;padding:1px 8px;font-weight:600;max-width:92px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}'+
          '.big{display:flex;align-items:baseline;gap:8px;margin-bottom:8px}'+
          '.big .t{font-size:27px;font-weight:800;letter-spacing:.5px;font-variant-numeric:tabular-nums;color:#ec4899;line-height:1}'+
          '.big .s{font-size:10px;color:#9aa1b0}'+
          '.row{display:flex;justify-content:space-between;align-items:baseline;background:#fbf7fa;border:1px solid #f4ecf1;border-radius:8px;padding:4px 9px;font-size:11px;margin-bottom:5px}'+
          '.row .k{color:#9aa1b0;font-size:10px}'+
          '.row .v{font-variant-numeric:tabular-nums;font-weight:650;color:#3a4051}'+
          '.barw{height:6px;background:#f3e9ef;border-radius:99px;margin:7px 0 3px;overflow:hidden}'+
          '.bar{height:6px;border-radius:99px;background:linear-gradient(90deg,#f472b6,#ec4899);min-width:2px;transition:width .3s}'+
          '.pct{display:flex;justify-content:space-between;font-size:9.5px;color:#b6bac4;margin-bottom:8px}'+
          '.btn{display:block;width:100%;border:1.5px solid #ec4899;background:#fdf1f7;color:#ec4899;border-radius:999px;font-size:12px;font-weight:700;padding:6px 0;cursor:pointer;font-family:inherit;transition:all .15s}'+
          '.btn:hover{background:#ec4899;color:#fff}'+
          '.btn.done{border-color:#34d399;background:#edfbf5;color:#0e9f6e}'+
          '.cfoot{display:flex;justify-content:space-between;align-items:center;margin-top:8px}'+
          '.chint{font-size:9.5px;color:#b6bac4}'+
          '.clnk{font-size:10px;color:#c46a94;cursor:pointer;text-decoration:underline}'+
          '.clnk:hover{color:#ec4899}'+
          '</style>'+
          '<div class="wrap"></div>';
          document.body.appendChild(root);
          var q=function(s){return sh.querySelector(s)},wrap=q('.wrap');
          var placeTimer=null,pollTimer=null,tickTimer=null,pingTimer=null,ro=null,fail=0;
          var state=null,fetchedAt=0,mode='rem',openName=null,hoverPill=null,zeroed={},lastSig='';
          var freePos={},dragging=false,suppressClick=null,pendingPlace=null,testTimers=[];
          var COLOR={period:'#ec4899',sleep:'#8b5cf6',work:'#3b82f6',game:'#f59e0b',cute:'#f472b6',dnd:'#6b7280',peak:'#9ca3af',stall:'#dc2626'};
          try{var m0=localStorage.getItem('sebCdMode');if(m0==='clk')mode='clk'}catch(e){}
          function p2(n){return String(n).padStart(2,'0')}
          function fmtRem(ms){
            ms=Math.max(0,ms);var s=Math.round(ms/1000);
            if(s>=86400)return Math.floor(s/86400)+'天'+Math.floor(s%86400/3600)+'时';
            var h=Math.floor(s/3600),m=Math.floor(s%3600/60),ss=s%60;
            return h>0?h+':'+p2(m)+':'+p2(ss):p2(m)+':'+p2(ss);
          }
          function fmtClk(ms){var d=new Date(ms);return p2(d.getHours())+':'+p2(d.getMinutes())+':'+p2(d.getSeconds())}
          function srvNow(){return state?state.now+(Date.now()-fetchedAt):Date.now()}
          function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')}
          function charByName(n){
            if(!state||!state.chars||!n)return null;
            for(var i=0;i<state.chars.length;i++)if(state.chars[i].name===n)return state.chars[i];
            return null;
          }
          //结构签名：状态类字段/位置变化才重建 DOM（数字由 tick 原位更新，避免悬停闪烁/打断交互）
          function sigOf(){
            if(!state||!state.chars)return '';
            return state.chars.map(function(c){return [c.name,c.code,c.text,c.next,c.ivs,c.ive,c.slp,c.swu,c.ws,c.wt,c.wtk,c.awk,c.fx,c.fy].join('~')}).join('|')+'#'+mode;
          }
          //每个角色的倒计时目标：睡眠中显示睡眠剩余，其余显示下次自主活跃
          function targetOf(c){
            if(c.code==='sleep')return c.slp&&!c.swu?c.slp:null;
            return c.next;
          }
          function ring(c){
            if(c.code==='sleep'||c.code==='work'||c.code==='peak'||!c.ivs||!c.ive||c.ive<=c.ivs)return '';
            var p=Math.min(1,Math.max(0,(srvNow()-c.ivs)/(c.ive-c.ivs)));
            var C=2*Math.PI*7.5;
            return '<svg width="19" height="19" viewBox="0 0 19 19">'+
              '<circle cx="9.5" cy="9.5" r="7.5" fill="none" stroke="#f3e2ec" stroke-width="2.4"/>'+
              '<circle cx="9.5" cy="9.5" r="7.5" fill="none" stroke="'+(COLOR[c.code]||COLOR.period)+'" stroke-width="2.4" stroke-linecap="round" stroke-dasharray="'+(C*p).toFixed(1)+' '+C.toFixed(1)+'" transform="rotate(-90 9.5 9.5)"/>'+
              '</svg>';
          }
          function numTextOf(c){
            var t=targetOf(c);
            if(c.code==='stall')return '停止';   //框架更新循环已停止：显示停止而不是 00:00
            if(t==null)return '等主人';
            if(mode==='clk')return fmtClk(t).slice(0,5);
            return fmtRem(t-srvNow());
          }
          function pillHtml(c,i,multi){
            var col=COLOR[c.code]||COLOR.period,t=targetOf(c);
            var cls='pill'+(t!=null&&mode==='rem'&&t-srvNow()<60000?' hot':'')+(t==null?' hold':'')+(freePos[c.name]?' free':'');
            return '<div class="'+cls+'" data-i="'+i+'" data-n="'+esc(c.name)+'" style="border-color:'+col+';box-shadow:0 2px 10px '+col+'44" title="'+esc(c.name+' · '+c.text+' · 按住拖动任意放置'+(freePos[c.name]?' · 双击归位':''))+'">'+
              ring(c)+(multi?'<span class="nm">'+esc(c.name)+'</span>':'')+
              '<span class="num" style="color:'+col+'">'+numTextOf(c)+'</span></div>';
          }
          function placePill(name,x,y){   //位置上报服务端（随角色配置持久化，重启 Alife 保留）
            fetch('http://127.0.0.1:'+PORT+'/place?name='+encodeURIComponent(name)+'&x='+Math.round(x)+'&y='+Math.round(y),{method:'POST'}).catch(function(){});
          }
          function clearPill(name){
            fetch('http://127.0.0.1:'+PORT+'/place?name='+encodeURIComponent(name),{method:'POST'}).catch(function(){});
          }
          function render(){
            if(!state||!state.chars||state.chars.length===0){wrap.style.display='none';return}
            wrap.style.display='';
            if(dragging)return;
            var sig=sigOf();
            if(sig===lastSig&&sh.querySelector('.pill'))return;   //结构未变：不重建
            lastSig=sig;
            //先移除旧的自由胶囊（编组内的随 wrap.innerHTML 一起换掉）
            sh.querySelectorAll('.pill.free').forEach(function(p){p.remove()});
            var multi=state.chars.length>1,html='';
            for(var i=0;i<state.chars.length;i++){
              var c=state.chars[i];
              if(freePos[c.name]){
                //自由胶囊：直接挂在 shadow root 下，fixed 定位脱离编组
                var tmp=document.createElement('div');
                tmp.innerHTML=pillHtml(c,i,multi);
                var el=tmp.firstChild;
                sh.appendChild(el);
                var x=Math.max(4,Math.min(freePos[c.name].x||4,innerWidth-el.offsetWidth-4));
                var y=Math.max(4,Math.min(freePos[c.name].y||4,innerHeight-el.offsetHeight-4));
                el.style.position='fixed';el.style.left=x+'px';el.style.top=y+'px';
              }else{
                html+=pillHtml(c,i,multi);
              }
            }
            wrap.innerHTML=html;
            bindPills();
            renderCard();
          }
          function activePill(){
            var pills=sh.querySelectorAll('.pill');
            for(var i=0;i<pills.length;i++)if(pills[i].dataset.n===openName)return pills[i];
            return null;
          }
          function bindPills(){
            sh.querySelectorAll('.pill').forEach(function(p){
              p.addEventListener('click',function(e){
                if(suppressClick===p.dataset.n){suppressClick=null;e.stopImmediatePropagation();return}
                mode=mode==='rem'?'clk':'rem';
                try{localStorage.setItem('sebCdMode',mode)}catch(e2){}
                render();tick();
              });
              p.addEventListener('mouseenter',function(){
                if(dragging)return;
                openName=p.dataset.n;hoverPill=p;wrap.classList.add('on');renderCard();
              });
              p.addEventListener('dblclick',function(){
                if(!p.classList.contains('free')||!p.dataset.n)return;
                var nm=p.dataset.n;
                delete freePos[nm];render();
                clearPill(nm);
              });
              bindDrag(p);
            });
          }
          //按住拖动：移动超 5px 判定为拖拽，脱离编组为独立 fixed 元素；松手上报位置（服务端持久化）
          function bindDrag(pill){
            pill.addEventListener('mousedown',function(e){
              if(e.button!==0)return;
              e.preventDefault();
              var name=pill.dataset.n,startX=e.clientX,startY=e.clientY,moved=false,offX=0,offY=0;
              function onMove(ev){
                var dx=ev.clientX-startX,dy=ev.clientY-startY;
                if(!moved&&dx*dx+dy*dy<25)return;
                if(!moved){
                  moved=true;dragging=true;
                  wrap.classList.remove('on');
                  var pr=pill.getBoundingClientRect();
                  if(pill.parentElement!==sh){
                    pill.classList.add('free');
                    sh.appendChild(pill);   //脱离编组（wrap 在 root 定位坐标系内，free 用 fixed 视口坐标）
                  }
                  pill.style.position='fixed';
                  offX=ev.clientX-pr.left;offY=ev.clientY-pr.top;
                  pill.style.left=Math.max(4,pr.left)+'px';pill.style.top=Math.max(4,pr.top)+'px';
                  pill.classList.add('dragging');
                }
                var x=Math.max(4,Math.min(ev.clientX-offX,innerWidth-pill.offsetWidth-4));
                var y=Math.max(4,Math.min(ev.clientY-offY,innerHeight-pill.offsetHeight-4));
                pill.style.left=x+'px';pill.style.top=y+'px';
              }
              function onUp(){
                document.removeEventListener('mousemove',onMove);
                document.removeEventListener('mouseup',onUp);
                if(moved){
                  pill.classList.remove('dragging');
                  var x=parseFloat(pill.style.left),y=parseFloat(pill.style.top);
                  freePos[name]={x:x,y:y};
                  suppressClick=name;dragging=false;pendingPlace=name;
                  placePill(name,x,y);
                  setTimeout(function(){if(pendingPlace===name)pendingPlace=null},5000);   //上报后短暂本地优先，超时恢复服务端同步
                  lastSig='';render();   //立即按新结构重排（数字/热态同步）
                }
              }
              document.addEventListener('mousemove',onMove);
              document.addEventListener('mouseup',onUp);
            });
          }
          function renderCard(){
            var old=sh.querySelector('.card');if(old)old.remove();
            if(!wrap.classList.contains('on'))return;
            var c=charByName(openName);
            if(!c){wrap.classList.remove('on');return}   //角色已停用：自动关卡片
            var col=COLOR[c.code]||COLOR.period,t=targetOf(c);
            var card=document.createElement('div');card.className='card';
            var h='<div class="hd"><span class="dot" style="background:'+col+';box-shadow:0 0 6px '+col+'99"></span>'+
              '<span class="cname">'+esc(c.name)+'</span><span class="stag" style="color:'+col+';background:'+col+'14;border-color:'+col+'44">'+esc(c.text)+'</span></div>';
            if(t==null){
              h+='<div class="big"><span class="t" style="color:'+col+'">—</span><span class="s">睡眠中 · 等主人消息唤醒</span></div>';
            }else{
              h+='<div class="big"><span class="t" style="color:'+col+'">'+fmtRem(t-srvNow())+'</span><span class="s">'+(c.code==='sleep'?'距睡醒':'距下次自主活跃')+' · '+fmtClk(t)+'</span></div>';
            }
            if(c.code==='sleep'&&c.slp&&c.swu)
              h+='<div class="row"><span class="k">睡眠</span><span class="v">等主人消息唤醒</span></div>';
            if(c.code!=='sleep'&&c.code!=='work'&&c.code!=='peak'&&c.ivs&&c.ive&&c.ive>c.ivs){
              var p=Math.min(1,Math.max(0,(srvNow()-c.ivs)/(c.ive-c.ivs)));
              h+='<div class="barw"><div class="bar" style="width:'+(p*100).toFixed(1)+'%;background:linear-gradient(90deg,'+col+'99,'+col+')"></div></div>'+
                 '<div class="pct"><span>本间隔进度</span><span>'+(p*100).toFixed(0)+'%</span></div>';
            }
            if(c.code==='work'&&c.wt>0)
              h+='<div class="row"><span class="k">工作进度</span><span class="v">'+c.ws+' / '+c.wt+'</span></div>'+
                 (c.wtk?'<div class="row"><span class="k">任务</span><span class="v" style="font-size:10px;max-width:130px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="'+esc(c.wtk)+'">'+esc(c.wtk)+'</span></div>':'');
            if(c.awk)
              h+='<div class="row"><span class="k">定点报时</span><span class="v">'+fmtClk(c.awk).slice(0,5)+'</span></div>';
            h+='<button class="btn" data-n="'+esc(c.name)+'">'+
              (c.code==='work'?'催促进度':(c.code==='sleep'?'唤醒并活跃一下':'催一下活跃'))+'</button>'+
              '<div class="cfoot"><span class="chint">按住胶囊可拖动 · 双击归位</span>'+
              '<a class="clnk" title="只隐藏本角色的倒计时胶囊；重新显示：到插件设置页打开「倒计时挂件」开关">隐藏此倒计时</a></div>';
            card.innerHTML=h;
            card.style.visibility='hidden';
            wrap.appendChild(card);
            //基准 = 当前悬停胶囊（含自由胶囊），否则编组整体；方向自适应：下方空间足则向下展开
            var rr=root.getBoundingClientRect();
            var ap=activePill();
            var br=ap?ap.getBoundingClientRect():rr;
            var ch=card.offsetHeight||200;
            if(innerHeight-br.bottom>=ch+10||innerHeight-br.bottom>=br.top){
              card.style.top=(br.bottom+6-rr.top)+'px';card.style.bottom='auto';
              card.style.transformOrigin='top right';
            }else{
              card.style.bottom=(rr.bottom-br.top+6)+'px';card.style.top='auto';
              card.style.transformOrigin='bottom right';
            }
            var left=Math.max(4,Math.min(br.right-card.offsetWidth,innerWidth-card.offsetWidth-4));
            card.style.left=(left-rr.left)+'px';card.style.right='auto';
            card.style.visibility='';
            card.querySelector('.btn').addEventListener('click',function(){
              var b=this;
              fetch('http://127.0.0.1:'+PORT+'/poke?name='+encodeURIComponent(b.dataset.n)+'&t='+TOKEN,{method:'POST'})
                .then(function(r){return r.json()})
                .then(function(d){
                  b.classList.add('done');b.textContent=d.ok?'已发送 ✓':'角色未找到';
                  setTimeout(function(){b.classList.remove('done');renderCard()},1500);
                })
                .catch(function(){b.textContent='发送失败';setTimeout(renderCard,1500)});
            });
            //隐藏此倒计时：只关本角色的开关（与设置页同一状态源，服务端落盘），下一轮 /state 生效
            var lnk=card.querySelector('.clnk');
            if(lnk)lnk.addEventListener('click',function(){
              lnk.textContent='已隐藏 ✓';
              fetch('http://127.0.0.1:'+PORT+'/vis?name='+encodeURIComponent(c.name)+'&on=0',{method:'POST'})
                .then(function(){wrap.classList.remove('on')})
                .catch(function(){lnk.textContent='发送失败'});
            });
          }
          // ---- 定位：锚定『展开思考』开关，避让 #tstats-root 等已存在挂件 ----
          function findSwitch(){
            //页面可能有多个『展开思考』（顶部导航/输入区各一），取 DOM 顺序第一个（顶部导航，挂件常驻位置）
            var els=document.querySelectorAll('span,div,label,p');
            for(var i=0;i<els.length;i++){var el=els[i];
              if(el.children.length>0)continue;
              if((el.textContent||'').trim().indexOf('展开思考')<0)continue;
              var p=el.parentElement,s=p?p.querySelector('.ant-switch'):null;
              var x=(s&&s.getBoundingClientRect().width>0)?s:el;
              if(x.getClientRects().length<1)continue;
              var r=x.getBoundingClientRect();
              if(r.width<5||r.height<5||r.top<0)continue;
              return{r:r.right,t:r.top,h:r.height};
            }
            return null;
          }
          function rectsOverlap(a,b,m){
            m=m||2;
            return a.left<b.right-m&&a.right>b.left+m&&a.top<b.bottom-m&&a.bottom>b.top+m;
          }
          function otherWidgets(){
            var list=[];
            var ts=document.getElementById('tstats-root');
            if(ts){var r=ts.getBoundingClientRect();if(r.width>0)list.push(r)}
            return list;
          }
          var lastPlaced=false;
          function place(){
            if(!document.body||root.style.display==='none')return;
            var w=root.offsetWidth,hh=root.offsetHeight;
            if(w<2)return;
            var sw=findSwitch(),cands=[],others=otherWidgets();
            if(sw){
              var ts=document.getElementById('tstats-root');
              var tsr=ts?ts.getBoundingClientRect():null;
              if(tsr&&tsr.width>0){
                cands.push({x:tsr.right+GAP,y:tsr.top+tsr.height/2-hh/2});
                cands.push({x:sw.r+GAP,y:sw.t+sw.h/2-hh/2-8});
              }else{
                cands.push({x:sw.r+GAP,y:sw.t+sw.h/2-hh/2});
              }
              cands.push({x:Math.max(8,sw.r-w-8),y:sw.t-hh-8});
            }else if(lastPlaced){
              //锚点消失（切换到其它页面）：保持原位，不跳到右下角；回到聊天页自动重新锚定
              return;
            }
            cands.push({x:innerWidth-w-12,y:innerHeight-hh-96});
            for(var i=0;i<cands.length;i++){
              var c=cands[i];
              c.x=Math.max(4,Math.min(c.x,innerWidth-w-4));
              c.y=Math.max(4,Math.min(c.y,innerHeight-hh-4));
              var rect={left:c.x,top:c.y,right:c.x+w,bottom:c.y+hh};
              var hit=false;
              for(var k=0;k<others.length;k++)if(rectsOverlap(rect,others[k])){hit=true;break}
              if(!hit){rs.left=c.x+'px';rs.top=c.y+'px';lastPlaced=true;return}
            }
            rs.left=cands[cands.length-1].x+'px';rs.top=cands[cands.length-1].y+'px';
            lastPlaced=true;
          }
          function tick(){
            if(!state||!state.chars)return;
            var dirty=false;
            sh.querySelectorAll('.pill').forEach(function(p){
              var i=parseInt(p.dataset.i||'-1',10);
              var c=state.chars[i];if(!c)return;
              var t=targetOf(c),el=p.querySelector('.num');if(!el)return;
              var col=COLOR[c.code]||COLOR.period;
              var txt=numTextOf(c);
              if(el.textContent!==txt){el.textContent=txt;dirty=true}
              var hot=t!=null&&mode==='rem'&&t-srvNow()<60000;
              p.classList.toggle('hot',hot);
              p.classList.toggle('hold',t==null);
              if(p.dataset.col!==col){
                p.dataset.col=col;
                p.style.borderColor=col;p.style.boxShadow='0 2px 10px '+col+'44';
                el.style.color=col;
                dirty=true;
              }
              //归零瞬间闪一下（此后 /state 会带回新间隔）
              var key=c.name+'|'+(c.code==='sleep'?'slp':'next');
              if(t!=null&&t-srvNow()<=0&&!zeroed[key]){zeroed[key]=true;p.classList.add('flash');setTimeout(function(){p.classList.remove('flash')},1100)}
              if(t!=null&&t-srvNow()>3000)zeroed[key]=false;
            });
            //详情卡打开时：原位更新卡内倒计时与进度条（不重建，避免打断按钮交互）
            var card=sh.querySelector('.card');
            if(card&&wrap.classList.contains('on')){
              var c0=charByName(openName);
              if(c0){
                var t0=targetOf(c0),bt=card.querySelector('.big .t'),bs=card.querySelector('.big .s'),bar=card.querySelector('.bar'),pp=card.querySelectorAll('.pct span');
                if(bt&&t0!=null)bt.textContent=fmtRem(t0-srvNow());
                if(bs&&t0!=null)bs.textContent=(c0.code==='sleep'?'距睡醒':'距下次自主活跃')+' · '+fmtClk(t0);
                if(bar&&c0.ivs&&c0.ive&&c0.ive>c0.ivs){
                  var pr=Math.min(1,Math.max(0,(srvNow()-c0.ivs)/(c0.ive-c0.ivs)));
                  bar.style.width=(pr*100).toFixed(1)+'%';
                  if(pp.length>1)pp[1].textContent=(pr*100).toFixed(0)+'%';
                }
              }
            }
            if(dirty)place();
          }
          //心跳：带本次注入的令牌回连本地服务，服务端据此判定"挂件确实挂在该页面上"。
          //不再依赖 ElectronNET 的 IPC 应答（该应答事件名固定且全局只有一个 handler，
          //多插件/多桌宠并发调用会互相顶掉、先调用方永久挂起），顺带上报自检串便于排错。
          function ping(){
            var d='';
            try{if(window.__sebCdDiag)d=window.__sebCdDiag()}catch(e){}
            var v=(document.visibilityState==='hidden')?'0':'1';   //隐藏页面定时器会被节流，服务端据此放宽心跳容忍
            fetch('http://127.0.0.1:'+PORT+'/ping?t='+TOKEN+'&v='+v+'&d='+encodeURIComponent(d),{cache:'no-store'}).catch(function(){});
          }
          function poll(){
            fetch('http://127.0.0.1:'+PORT+'/state',{cache:'no-store'})
              .then(function(r){return r.json()})
              .then(function(d){
                fail=0;state=d;fetchedAt=Date.now();
                //服务端是位置的权威来源（随角色配置持久化）；刚上报过的胶囊等下一轮再同步
                if(state.chars)state.chars.forEach(function(c){
                  if(c.name===pendingPlace)return;
                  if(c.fx!=null&&c.fy!=null)freePos[c.name]={x:c.fx,y:c.fy};
                  else delete freePos[c.name];
                });
                render();place();
              })
              //连续失败只隐藏挂件内容：绝不能隐藏 root——place() 会因 root.style.display==='none' 永久早退、
              //render() 也只恢复 wrap，挂件自此再也回不来（多桌宠并发/端口回迁时 fetch 短暂失败的真实故障）
              //长时间连不上本地服务（角色全部停用/插件卸载/端口换了）则自删，避免页面里留一个死挂件占位置
              .catch(function(){
                if(++fail>=3)wrap.style.display='none';
                if(fail>=15){try{window.__sebCdTeardown&&window.__sebCdTeardown()}catch(e){}}
              });
          }
          //卡片关闭：document 级 mousemove 监测，指针离开 当前悬停胶囊+卡片 并集 30px 即收起
          //(只算当前胶囊而非全部，避免胶囊被拖散在屏幕两端时判定区域过大永不关闭)
          function onDocMove(e){
            if(!wrap.classList.contains('on')||dragging)return;
            var base=(hoverPill&&hoverPill.isConnected)?hoverPill.getBoundingClientRect():root.getBoundingClientRect();
            var cd=sh.querySelector('.card');
            var l=base.left,t=base.top,rt=base.right,bm=base.bottom;
            if(cd){var cr=cd.getBoundingClientRect();l=Math.min(l,cr.left);t=Math.min(t,cr.top);rt=Math.max(rt,cr.right);bm=Math.max(bm,cr.bottom)}
            if(e.clientX<l-30||e.clientX>rt+30||e.clientY<t-30||e.clientY>bm+30){wrap.classList.remove('on');hoverPill=null}
          }
          //鼠标移出窗口（mouseout 且无 relatedTarget）或窗口失焦时也收起卡片：
          //此时不会再有 mousemove 事件，仅靠位移监测会偶发"移开后卡片不消失"
          function onDocOut(e){if(!e.relatedTarget&&!dragging){wrap.classList.remove('on');hoverPill=null}}
          function onWinBlur(){wrap.classList.remove('on');hoverPill=null}
          document.addEventListener('mousemove',onDocMove);
          document.addEventListener('mouseout',onDocOut);
          window.addEventListener('blur',onWinBlur);
          //卡片关闭只依赖上面三重兜底（位移/出窗口/失焦）。
          //不要用 wrap.mouseleave+延时器：胶囊→卡片之间必然离开 wrap 盒（卡片是绝对定位），
          //稍慢的移动就会被 160ms 延时器误杀（"想点按钮卡片就消失"的根因），且关闭后 visibility:hidden 无法再悬停唤回。
          poll();place();
          ping();
          pingTimer=setInterval(ping,2000);
          tickTimer=setInterval(tick,200);
          pollTimer=setInterval(poll,2000);
          placeTimer=setInterval(function(){if(root.style.display!=='none')place()},1000);
          addEventListener('resize',place);
          try{ro=new ResizeObserver(place);ro.observe(document.body)}catch(e){}
          window.__sebCdAlive=true;
          //注入后自检（仅卡片开合，兼作引导闪现；拖动/位置链路由 /place 服务端验证）
          testTimers.push(setTimeout(function(){wrap.classList.add('on');renderCard()},600));
          testTimers.push(setTimeout(function(){
            document.dispatchEvent(new MouseEvent('mousemove',{bubbles:true,clientX:8,clientY:innerHeight-8}));
            window.__sebCdCardTest=wrap.classList.contains('on')?'FAIL_still_open':'ok_closed';
          },1400));
          window.__sebCdDiag=function(){
            var r=root.getBoundingClientRect();
            var cd=sh.querySelector('.card'),cs='';
            if(cd){var cr=cd.getBoundingClientRect();cs=' card='+Math.round(cr.left)+','+Math.round(cr.top)+'x'+Math.round(cr.width)+','+Math.round(cr.height)}
            return 'pills='+sh.querySelectorAll('.pill').length+' free='+sh.querySelectorAll('.pill.free').length+' disp='+root.style.display+' fail='+fail+
              ' rect='+Math.round(r.left)+','+Math.round(r.top)+'x'+Math.round(r.width)+','+Math.round(r.height)+cs+
              ' cT='+(window.__sebCdCardTest||'-');
          };
          window.__sebCdTeardown=function(){
            window.__sebCdAlive=false;
            clearInterval(tickTimer);clearInterval(pollTimer);clearInterval(placeTimer);clearInterval(pingTimer);
            testTimers.forEach(function(t){clearTimeout(t)});
            removeEventListener('resize',place);
            document.removeEventListener('mousemove',onDocMove);
            document.removeEventListener('mouseout',onDocOut);
            window.removeEventListener('blur',onWinBlur);
            try{ro&&ro.disconnect()}catch(e){}
            root.remove();delete window.__sebCdTeardown;
          };
          var r0=root.getBoundingClientRect();
          return 'injected '+Math.round(r0.left)+','+Math.round(r0.top)+','+Math.round(r0.width)+','+Math.round(r0.height);
        })()
        """;

    #endregion
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LeishenCC.Core
{
    /// <summary>一条 Fn 事件的处理记录，供界面/日志排查用。</summary>
    public sealed class FnEventRecord
    {
        public DateTime Time;
        public int GapMs = -1;          // 距上一条事件的毫秒数；-1 = 首条
        public FnKeyKind Kind;
        public string Describe = "";
        public string FullHex = "";
        public bool Echo;               // 被判为同一次按键的重复通知
        public bool Simulated;          // 界面模拟按钮产生
        public string Source = "GMC";   // GMC = BIOS 私有事件流；WMI2 = Hotkey OSD 标准 ACPI 事件流
        public string Action = "";
        public string Ack = "";         // 0xFB00/0x0400 事件确认结果（仅 GMC 流）
    }

    /// <summary>
    /// Fn 组合键的服务端：监听 EC 上报的 GMC_WMIEvent，按原厂逻辑做出响应。
    /// 原厂是「守护进程收事件 → 广播 ControlCenterMessage → 主界面执行」，
    /// 这里是单进程，收完直接执行，行为保持一致。
    ///
    /// 健壮性（实测踩过的坑，见 README「Fn 组合键事件」）：
    ///   • WMI 订阅会被系统停掉（WMI 服务重启等），原来只订阅一次，停了就永远收不到事件；
    ///     现在监听 ManagementEventWatcher.Stopped + 30 秒看门狗，自动退避重订阅。
    ///   • EC 对同一次按键会连发两条相同负载，必须去重；但窗口太大（旧版 2500ms）会把真实的
    ///     第二次按键也吞掉，改为可配置的 700ms（HKCU\SOFTWARE\LeishenCC\FnEchoWindowMs）。
    ///   • 处理完必须回 0xFB00/0x0400 清事件位，否则 EC 会挂起该事件；即使处理抛异常也要清。
    /// </summary>
    public static class FnKeyService
    {
        private static readonly FnKeyWatcher Watcher = new FnKeyWatcher("GMC_WMIEvent");
        private static readonly Wmi2Watcher Wmi2 = new Wmi2Watcher();
        private static readonly object Sync = new object();
        private static readonly object EchoSync = new object();
        private static readonly object HistorySync = new object();
        private static readonly List<FnEventRecord> Events = new List<FnEventRecord>();
        private const int HistorySize = 12;

        // 自动重订阅
        private static Timer _watchdog;          // 30s 检查一次订阅是否还活着
        private static Timer _restartTimer;      // 退避重启（单次触发）
        private static volatile bool _desired;   // 用户是否希望监听
        private static int _restartAttempt;
        private static string _lastRestartReason = "";

        // 按键去重（EC 会把同一次按键连发两条）
        private static byte[] _lastPerfRaw;
        private static DateTime _lastPerfTime;
        private static DateTime _lastEventAt;
        private static int _lastGapMs = -1;

        public static bool Desired { get { return _desired; } }
        public static bool Running { get { return Watcher.Running; } }
        public static string LastError { get { return Watcher.LastError; } }

        /// <summary>
        /// 抓包模式（--fnprobe）：只解析/记录/确认事件，不执行任何硬件动作
        /// （不写背光、不动摄像头、不强冷、不切模式、不拉窗口），保证抓包不改变机器状态。
        /// </summary>
        public static bool DryRun { get; set; }

        public static string ClassName { get { return Watcher.ClassName; } }
        public static int StartCount { get { return Watcher.StartCount; } }
        public static DateTime StartedAt { get { return Watcher.StartedAt; } }
        public static FnKeyEvent LastEvent { get; private set; }
        public static DateTime LastTime { get; private set; }
        public static int EventCount { get; private set; }
        public static int EchoCount { get; private set; }
        public static int UnknownCount { get; private set; }
        public static int Wmi2EventCount { get; private set; }
        public static bool Wmi2Running { get { return Wmi2.Running; } }
        public static string Wmi2LastError { get { return Wmi2.LastError; } }
        public static Wmi2Event Wmi2LastEvent { get; private set; }
        public static int AckFailCount { get; private set; }      // 连续确认失败次数
        public static int AckFailTotal { get; private set; }
        public static int RestartCount { get; private set; }
        public static int LastGapMs { get { return _lastGapMs; } }
        public static string LastRestartReason { get { return _lastRestartReason; } }
        public static int EchoWindowMs { get { return Settings.FnEchoWindowMs; } }

        /// <summary>最近事件（最新在前），界面用来显示 Fn 诊断历史。</summary>
        public static FnEventRecord[] RecentEvents
        {
            get
            {
                lock (HistorySync)
                {
                    var arr = Events.ToArray();
                    Array.Reverse(arr);
                    return arr;
                }
            }
        }

        /// <summary>性能模式被 Fn 键切换（参数是新模式 0/1/2）。等价于 <see cref="PowerModeService.Changed"/>。</summary>
        public static event Action<int> PowerModeChanged;

        /// <summary>控制中心键：请求把主窗口拉到前台</summary>
        public static event Action ActivateRequested;

        /// <summary>任意 Fn 事件（含原始字节），界面用来显示最近一次按键</summary>
        public static event Action<FnKeyEvent> EventArrived;

        /// <summary>Hotkey OSD 的 WMI2Event（标准 ACPI 事件流），用于覆盖 GMC 流没有的热键</summary>
        public static event Action<Wmi2Event> Wmi2EventArrived;

        static FnKeyService()
        {
            // 统一从 PowerMode 服务转发，保证「Fn 键切换」和「界面切换」的界面反馈完全一致
            PowerModeService.Changed += m => PowerModeChanged?.Invoke(m);
            Watcher.Arrived += OnArrived;
            Watcher.Faulted += OnFaulted;
            Wmi2.Arrived += OnWmi2Arrived;
            Wmi2.Faulted += OnWmi2Faulted;
        }

        public static string StatusText
        {
            get
            {
                if (!_desired) return "已关闭";
                if (Running)
                {
                    return string.Format("监听中 · root\\wmi:{0} · 订阅于 {1} · 事件 {2} 条 · 去重窗口 {3}ms{4}{5}{6} · WMI2 {7}",
                        Watcher.ClassName, Watcher.StartedAt.ToString("HH:mm:ss"), EventCount,
                        EchoWindowMs,
                        EchoCount > 0 ? " · 回声 " + EchoCount : "",
                        UnknownCount > 0 ? " · 未识别 " + UnknownCount : "",
                        AckFailTotal > 0 ? " · 确认失败 " + AckFailTotal : "",
                        Wmi2Running ? "监听中 " + Wmi2EventCount + " 条" : "未启动" + (string.IsNullOrEmpty(Wmi2.LastError) ? "" : "：" + Wmi2.LastError));
                }
                return "未启动：" + (LastError ?? "未知原因") +
                       (RestartCount > 0 ? " · 已尝试重启 " + RestartCount + " 次" : "");
            }
        }

        public static bool Start()
        {
            lock (Sync)
            {
                _desired = true;
                bool ok = Watcher.Start();
                Hub.Write(ok
                    ? "Fn 组合键监听已启动（root\\wmi:" + Watcher.ClassName + "，去重窗口 " + Settings.FnEchoWindowMs + "ms）"
                    : "Fn 组合键监听启动失败: " + Watcher.LastError);

                // 第二路：Hotkey OSD 的 WMI2Event（标准 ACPI 事件流），与 GMC 流 GUID 不同、互不覆盖。
                // 它不需要 0x0400 确认，只做只读记录；失败不影响 GMC 主监听。
                bool ok2 = Wmi2.Start();
                Hub.Write(ok2
                    ? "Hotkey OSD WMI2Event 监听已启动（标准 ACPI 事件流，只读）"
                    : "WMI2Event 监听启动失败（不影响 GMC 流）: " + Wmi2.LastError);

                if (ok)
                {
                    _restartAttempt = 0;
                    StartWatchdogLocked();
                    // 启动时清掉可能残留的 2/3/4 事件位：若上一次是"确认失败 + 进程退出"，
                    // EC 会一直挂起该事件，表现为"换了新进程也收不到热键"，只能重启整机。
                    AckAllEvents("启动后清残位");
                }
                else
                {
                    ScheduleRestartLocked("启动失败", 3000);
                    StartWatchdogLocked();
                }
                return ok;
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                _desired = false;
                StopWatchdogLocked();
                Watcher.Stop();
                Wmi2.Stop();
                Hub.Write("Fn 组合键监听已停止");
            }
        }

        /// <summary>界面「重新订阅」按钮：手工重建订阅并清空去重状态（不重启程序即可恢复）。</summary>
        public static bool Restart(string reason)
        {
            lock (Sync)
            {
                _desired = true;
                _restartAttempt = 0;
                Watcher.Stop();
                Wmi2.Stop();
                ResetEchoState();
                bool ok = Watcher.Start();
                bool ok2 = Wmi2.Start();
                RestartCount++;
                Hub.Write((ok ? "Fn 监听已重新订阅" : "Fn 监听重新订阅失败") +
                          "（" + reason + "，root\\wmi:" + Watcher.ClassName + "）" +
                          (ok ? "" : " 错误：" + Watcher.LastError));
                Hub.Write(ok2 ? "WMI2Event 已重新订阅" : "WMI2Event 重新订阅失败（不影响 GMC 流）: " + Wmi2.LastError);
                if (ok) StartWatchdogLocked();
                else ScheduleRestartLocked(reason, 3000);
                return ok;
            }
        }

        // ---------- 自动重订阅 ----------

        private static void StartWatchdogLocked()
        {
            if (_watchdog == null)
                _watchdog = new Timer(WatchdogTick, null, 30000, 30000);
            else
                _watchdog.Change(30000, 30000);
        }

        private static void StopWatchdogLocked()
        {
            if (_watchdog != null) _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
            if (_restartTimer != null) _restartTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private static void WatchdogTick(object state)
        {
            lock (Sync)
            {
                if (!_desired) return;
                if (Watcher.Running && Wmi2.Running) return;
                ScheduleRestartLocked("看门狗：订阅已不在运行", 2000);
            }
        }

        private static void OnFaulted(string reason)
        {
            Hub.Write("Fn 事件订阅中断：" + reason);
            lock (Sync)
            {
                if (!_desired) return;
                ScheduleRestartLocked(reason, 2000);
            }
        }

        private static void OnWmi2Faulted(string reason)
        {
            Hub.Write("WMI2Event 订阅中断：" + reason);
            lock (Sync)
            {
                if (!_desired) return;
                ScheduleRestartLocked(reason, 2000);
            }
        }

        /// <summary>
        /// WMI2Event（标准 ACPI 事件流）。
        ///
        /// 本机实测（2026-10-19 21:04，`--fnprobe 180`）：**GMC 流 180 秒 0 条**，
        /// 全部 Fn+1 按键都从 WMI2 `0x30` 上报，data=b[30] 在 0/1 之间切换
        /// （OSD 语义：>0 = fan_max，0 = fan_normal）。也就是说 EC 停报 GMC 性能键时，
        /// WMI2 仍然工作 —— 这是"Fn+1 没反应"的正式解法。
        ///
        /// 0x30 按**状态**应用（SetBoost），不做 toggle；动作完成后更新历史。
        /// 其余事件 ID（触摸板/FnLock/麦克风/摄像头/使用模式）暂时只记录，
        /// 等 `--fnprobe` 拿到对应按键样本再逐个接动作。
        /// </summary>
        private static void OnWmi2Arrived(Wmi2Event ev)
        {
            Wmi2EventCount++;
            Wmi2LastEvent = ev;
            Hub.Write("OSD WMI2 事件: " + ev.Describe());

            string action = "WMI2 只读记录（动作映射待标注）";
            if (ev.EventId == 0x30)
            {
                bool fanMax = ev.Data > 0;
                if (DryRun)
                {
                    action = "抓包模式，未改强冷（data=" + ev.Data + " → " + (fanMax ? "max" : "normal") + "）";
                }
                else
                {
                    string r = FanControlService.SetBoost(fanMax);
                    action = "Fn+1 风扇键 → " + (fanMax ? "强冷开" : "强冷关") + "：" + r;
                    // 状态没变（EC 重复上报同值）就不刷日志，避免一次按键 60 条
                    if (r.IndexOf("状态一致", StringComparison.Ordinal) < 0)
                        Hub.Write("WMI2 风扇键: data=" + ev.Data + " → " + r);
                    Hub.RaiseUpdated();
                }
            }

            lock (HistorySync)
            {
                Events.Add(new FnEventRecord
                {
                    Time = DateTime.Now,
                    Kind = FnKeyKind.Unknown,
                    Describe = ev.Describe(),
                    FullHex = BitConverter.ToString(ev.Raw).Replace("-", " "),
                    Source = "WMI2",
                    Action = action,
                    Ack = "无需确认"
                });
                if (Events.Count > HistorySize) Events.RemoveAt(0);
            }
            Wmi2EventArrived?.Invoke(ev);
        }

        private static void ScheduleRestartLocked(string reason, int delayMs)
        {
            _lastRestartReason = reason;
            if (_restartTimer == null)
                _restartTimer = new Timer(RestartTick, null, delayMs, Timeout.Infinite);
            else
                _restartTimer.Change(delayMs, Timeout.Infinite);
        }

        private static void RestartTick(object state)
        {
            lock (Sync)
            {
                if (!_desired) return;
                if (Watcher.Running && Wmi2.Running) return;
                _restartAttempt++;
                bool ok1 = Watcher.Running || Watcher.Start();
                bool ok2 = Wmi2.Running || Wmi2.Start();
                bool ok = ok1 && ok2;
                RestartCount++;
                if (ok) _restartAttempt = 0;
                Hub.Write(string.Format("Fn 监听自动重启{0}（第 {1} 次，原因：{2}{3}{4}）",
                    ok ? "成功" : "失败", _restartAttempt > 0 ? _restartAttempt : 1,
                    _lastRestartReason, ok1 ? "" : "，GMC 错误：" + Watcher.LastError,
                    ok2 ? "" : "，WMI2 错误：" + Wmi2.LastError));
                if (!ok)
                {
                    int delay = Math.Min(30000, 2000 * Math.Max(1, _restartAttempt));
                    if (_restartTimer == null)
                        _restartTimer = new Timer(RestartTick, null, delay, Timeout.Infinite);
                    else
                        _restartTimer.Change(delay, Timeout.Infinite);
                }
            }
        }

        // ---------- 事件处理 ----------

        private static void OnArrived(FnKeyEvent ev)
        {
            var now = DateTime.Now;
            LastEvent = ev;
            LastTime = now;
            EventCount++;
            int gap = _lastEventAt == default(DateTime) ? -1 : (int)(now - _lastEventAt).TotalMilliseconds;
            _lastGapMs = gap;
            _lastEventAt = now;

            Hub.Write("Fn 键事件: " + ev.Describe() + (gap >= 0 ? "  距上条 " + gap + "ms" : ""));

            // 物理上不存在"按键 A 的回声夹在按键 B 之后"的合理情形：任何非性能键事件都重置回声状态
            if (ev.Kind != FnKeyKind.Performance) ResetEchoState();

            bool echo = false;
            string action = "";
            string ack;
            try
            {
                switch (ev.Kind)
                {
                    case FnKeyKind.Backlight:
                        if (DryRun) { action = "抓包模式，未写背光（亮度 " + Math.Max(0, Math.Min(15, ev.Brightness)) + "）"; break; }
                        ApplyBacklight(ev.Brightness);
                        action = "背光 → " + Math.Max(0, Math.Min(15, ev.Brightness));
                        break;

                    case FnKeyKind.Performance:
                    {
                        // EC 对同一次按键会连发两条相同负载。强冷是切换语义、模式键是循环语义，
                        // 两条就会互相抵消/多跳一档，所以必须按按键去重。
                        //
                        // 判据：全 32 字节相同 + 短窗（默认 600ms，覆盖实测 190~375ms 的背光回声上界）。
                        // 600ms~2500ms 的相同负载属于"灰区"：无法从协议上区分是延迟回声还是人第二次按键，
                        // 这里选择执行并记 WARN —— 吞掉真实按键是无反馈的，误执行一次用户能再按一次纠正。
                        int echoGap;
                        bool gray;
                        echo = IsEcho(ev.Raw, out echoGap, out gray);
                        if (echo)
                        {
                            EchoCount++;
                            action = "重复通知（间隔 " + echoGap + "ms < " + EchoWindowMs + "ms），忽略";
                            Hub.Write("性能键：" + action);
                        }
                        else
                        {
                            if (gray) Hub.Write("性能键：疑似回声但已按真实按键执行（间隔 " + echoGap + "ms），供后续校准窗口");
                            if (DryRun)
                                action = "抓包模式，未执行（键码 0x" + ev.KeyCode.ToString("X2") + "）";
                            else
                                action = HandlePerformanceKey(ev);
                        }
                        break;
                    }

                    case FnKeyKind.ControlCenter:
                        if (DryRun) { action = "抓包模式，未拉起窗口"; break; }
                        Hub.Write("控制中心键：拉起主窗口");
                        ActivateRequested?.Invoke();
                        action = "拉起主窗口";
                        break;

                    case FnKeyKind.Camera:
                        if (DryRun) { action = "抓包模式，未动摄像头（值 " + ev.CameraValue + "）"; break; }
                        action = HandleCameraKey(ev);
                        break;

                    case FnKeyKind.Hdmi:
                        Hub.Write("外接显示事件：重新读取显示状态");
                        Hub.ReadCaps();
                        // 原厂在 !SupportDds 且当前显示状态==3 时会自动切回混合模式；
                        // 本实现有意只刷新状态，不做显示模式写操作（从热键事件自动切显卡风险过高），
                        // 需要切换时请到「显卡与显示」页手动操作。见 README「Fn 组合键事件」。
                        action = "重读显示状态（不自动切显示模式）";
                        break;

                    case FnKeyKind.Adapter:
                        Hub.Write("电源适配器事件");
                        Hub.ReadCaps();
                        action = "重读电源状态";
                        break;

                    default:
                        UnknownCount++;
                        Hub.Write("未识别的 Fn 事件（全 32 字节）：" + ev.FullHex +
                                  "；按原厂习惯 2/3/4 位全量清一次做兜底");
                        AckAllEvents("未识别事件兜底");
                        action = "未识别，已兜底清位";
                        break;
                }
            }
            catch (Exception ex)
            {
                action = "异常：" + ex.Message;
                Hub.Write("Fn 事件处理异常: " + ex.Message);
            }
            finally
            {
                // 原厂 SetEventStatus：处理完必须回 0xFB00/0x0400 清对应位，否则 EC 会一直挂着。
                // 即使处理过程抛异常也要清，避免事件位泄漏导致后续热键停报。
                ack = AckEvent(ev);
            }

            AddHistory(ev, gap, echo, action, ack);
            EventArrived?.Invoke(ev);
        }

        /// <summary>摄像头键：对齐原厂判据（状态未变/不支持就不重复动作），但确认照旧总是发。</summary>
        private static string HandleCameraKey(FnKeyEvent ev)
        {
            int value = ev.CameraValue;
            if (value != 1 && value != 2)
            {
                Hub.Write("摄像头事件值未知: 0x" + value.ToString("X2"));
                return "摄像头值未知，忽略";
            }
            if (Hub.CameraStatus == value) return "摄像头状态未变（" + value + "），忽略";
            if (Hub.CameraStatus == 0) return "EC 报告不支持摄像头热键（状态 0），忽略";

            ToggleCamera(value);
            Hub.CameraStatus = value;      // 立即同步，别等 5 秒轮询
            Hub.RaiseUpdated();
            return "摄像头 → " + (value == 1 ? "开" : "关");
        }

        private static void AddHistory(FnKeyEvent ev, int gap, bool echo, string action, string ack)
        {
            var rec = new FnEventRecord
            {
                Time = DateTime.Now,
                GapMs = gap,
                Kind = ev.Kind,
                Describe = ev.Describe(),
                FullHex = ev.FullHex,
                Echo = echo,
                Simulated = ev.Simulated,
                Action = action,
                Ack = ack
            };
            lock (HistorySync)
            {
                Events.Add(rec);
                if (Events.Count > HistorySize) Events.RemoveAt(0);
            }
        }

        /// <summary>
        /// 确认一条事件（原厂 SetEventStatus：a2=性能键、a3=控制中心键、a4=摄像头键），
        /// 失败重试一次并连续失败计数；连续 3 次失败说明 EC 通道可能失灵，异步重建订阅。
        /// 返回值只用于日志/统计，不影响动作。
        /// </summary>
        private static string AckEvent(FnKeyEvent ev)
        {
            if (!Hub.WmiOk) return "EC 不可用";
            if (!ev.HasPerfBit && !ev.HasCcBit && !ev.HasCameraBit) return "无需确认";

            bool ok = Hub.Wmi.ClearEventBits(ev.HasPerfBit, ev.HasCcBit, ev.HasCameraBit);
            if (!ok)
            {
                Thread.Sleep(10);
                ok = Hub.Wmi.ClearEventBits(ev.HasPerfBit, ev.HasCcBit, ev.HasCameraBit);
            }

            if (ok)
            {
                AckFailCount = 0;
                return "确认 ok";
            }

            AckFailCount++;
            AckFailTotal++;
            Hub.Write("Fn 事件确认失败（连续 " + AckFailCount + " 次）：" + ev.Describe() +
                      "，错误：" + (Hub.Wmi.LastError ?? "未知"));
            if (AckFailCount >= 3)
            {
                AckFailCount = 0;
                lock (Sync) ScheduleRestartLocked("连续确认失败，重建订阅", 500);
            }
            return "确认失败";
        }

        /// <summary>顺序清 2/3/4 三个事件位（启动、唤醒、未识别事件时兜底）。</summary>
        public static bool AckAllEvents(string reason)
        {
            if (!Hub.WmiOk) return false;
            bool ok = Hub.Wmi.ClearAllEvents();
            Hub.Write("Fn 事件位清理（" + reason + "）：2/3/4 " + (ok ? "全部成功" : "失败，错误：" + (Hub.Wmi.LastError ?? "未知")));
            return ok;
        }

        /// <summary>
        /// 判断是不是同一次按键的第二条通知。分层算法：
        ///   • 全 32 字节相同且间隔 ≤ 短窗（<see cref="EchoWindowMs"/>，默认 600ms）→ 回声，丢弃；
        ///   • 全 32 字节相同且间隔 ≤ 2500ms（灰区）→ 执行但记 WARN（无法区分延迟回声与真实二次按键，
        ///     宁可多执行一次让用户能纠正，也不静默吞掉真实按键）；
        ///   • 其余（负载不同 / 超时）→ 真实按键，重置基线。
        /// 短窗取 600ms：实测背光键 13/13 次按键均为双发，间隔 190–375ms（中位 253ms），留 1.6 倍余量。
        /// 可在 HKCU\SOFTWARE\LeishenCC\FnEchoWindowMs 调整。
        /// </summary>
        private static bool IsEcho(byte[] raw, out int gapMs, out bool gray)
        {
            gray = false;
            gapMs = -1;
            int window = Settings.FnEchoWindowMs;
            lock (EchoSync)
            {
                var now = DateTime.Now;
                bool same = _lastPerfRaw != null && raw != null &&
                            raw.Length == _lastPerfRaw.Length && BytesEqual(raw, _lastPerfRaw);
                if (!same)
                {
                    _lastPerfRaw = raw == null ? null : (byte[])raw.Clone();
                    _lastPerfTime = now;
                    return false;
                }

                gapMs = (int)(now - _lastPerfTime).TotalMilliseconds;
                if (gapMs <= window) return true;          // 短窗：回声丢弃（不更新基线）
                gray = gapMs <= 2500;                      // 灰区：执行，但在调用方打 WARN
                _lastPerfRaw = (byte[])raw.Clone();
                _lastPerfTime = now;
                return false;
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static void ResetEchoState()
        {
            lock (EchoSync)
            {
                _lastPerfRaw = null;
                _lastPerfTime = default(DateTime);
            }
        }

        /// <summary>
        /// 性能键（GMC `EventDetail[7] != 0`）—— 本机实测（2026-10-19 21:51 标定）：
        ///   • **Fn+3** 只走这条 GMC 流（`b[7]=1`、`b[1]=0x07`），且**没有** WMI2 事件/OSD 图标；
        ///   • **Fn+1** 只走 WMI2 `0x30` 风扇状态流，GMC 流没有它。
        /// 所以这里不再按 `b[1]` 键码分流（Fn+3 的键码同样是 0x07，旧实现会把 Fn+3 误当强冷），
        /// 一律按原厂语义切性能模式；强冷只由 WMI2 0x30 驱动（见 OnWmi2Arrived）。
        /// 原厂在电池/Type-C 供电时忽略该键，这里只抑制动作、事件确认照发。
        /// </summary>
        private static string HandlePerformanceKey(FnKeyEvent ev)
        {
            // 原厂在电池供电（IsOnDCMode）与 Type-C 供电（IsTypeCAdapterIn）时直接忽略性能键，
            // 不切模式、不换电源计划。
            if (OnBattery())
            {
                Hub.Write("性能模式键 Fn+3：电池供电，按原厂语义忽略（不切模式/电源计划）");
                return "电池供电，忽略模式切换";
            }
            if (IsTypeCAdapterIn())
            {
                Hub.Write("性能模式键 Fn+3：Type-C 适配器供电，按原厂语义忽略（不切模式/电源计划）");
                return "Type-C 供电，忽略模式切换";
            }

            PowerModeService.Cycle("Fn+3");
            return "模式循环 → " + PowerModeService.Names[Math.Max(0, Math.Min(2, PowerModeService.Current))];
        }

        /// <summary>
        /// 原厂 DetectAdapterStatus：读 GET 0xFA00/0x0206 的 a4，==1 表示 Type-C 适配器接入。
        /// 原厂会 Sleep(100) 重试最多 5 次；热键路径上不能阻塞 WMI 线程 500ms，这里只重试 2 次。
        /// </summary>
        private static bool IsTypeCAdapterIn()
        {
            if (!Hub.WmiOk) return false;
            for (int i = 0; i < 2; i++)
            {
                Smi r;
                if (Hub.Wmi.Read(GmWmi.GET_FAN_CTRL, 0, out r)) return r.a4 == 1;
            }
            return false;
        }

        /// <summary>
        /// 性能模式键循环，完全照搬原厂：
        /// 当前 0(高性能) → 2(办公)，2 → 1(游戏)，1 → 0(高性能)。
        /// 实际下发见 <see cref="PowerModeService.Cycle"/>。
        /// </summary>
        public static int NextMode(int current) => PowerModeService.NextMode(current);

        private static void ApplyBacklight(int level)
        {
            if (!Hub.WmiOk) return;
            // EC 上报的就是 4 位亮度（原厂直接塞进 bit27..24），这里只做范围收敛
            int bright = Math.Max(0, Math.Min(15, level));
            int module = Settings.LedModule;
            var zones = LedData.ParseGroup(Settings.GetLedProfile(module));

            // 原厂 SetAlphaChangeData 只改键盘三个分区（LED1/2/3）
            for (int i = 0; i < 3; i++) zones[i].Brightness = bright;
            for (int i = 0; i < 3; i++) Hub.Wmi.SetLedZone(LedData.Zones[i].Zone, zones[i].Raw);

            Settings.SetLedProfile(module, LedData.Join(zones));
            Hub.Write("键盘背光档位 → " + bright);
        }

        private static void ToggleCamera(int value)
        {
            bool enable = value == 1;
            if (value != 1 && value != 2) { Hub.Write("摄像头事件值未知: 0x" + value.ToString("X2")); return; }
            string msg;
            bool ok = CameraService.SetEnabled(enable, out msg);
            Hub.Write("摄像头" + (enable ? "启用" : "禁用") + (ok ? "成功" : "失败") + " " + msg.Replace("\n", " ").Replace("\r", ""));
        }

        // ---------- 电源状态（原厂 IsOnDCMode） ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        private static bool OnBattery()
        {
            try
            {
                SYSTEM_POWER_STATUS s;
                if (GetSystemPowerStatus(out s)) return s.ACLineStatus == 0;
            }
            catch { }
            return false;
        }

        /// <summary>界面验证用：模拟一条 WMI2 `0x30` Fn+1 风扇状态事件（不影响真实 WMI）。</summary>
        public static void SimulateWmi2Fan(bool fanMax)
        {
            var raw = new byte[32];
            raw[0] = 0x30;
            raw[15] = 0x03;
            raw[30] = (byte)(fanMax ? 1 : 0);
            var ev = Wmi2Event.Parse(raw);
            if (ev != null) OnWmi2Arrived(ev);
        }

        /// <summary>不按键也能验证链路：直接按某种事件处理一次。</summary>
        public static void Simulate(FnKeyKind kind, int arg)        {
            var b = new byte[32];
            b[0] = (byte)(arg > 0 && arg < 256 ? arg : 0);
            switch (kind)
            {
                case FnKeyKind.Backlight: b[0] = (byte)arg; break;
                case FnKeyKind.Hdmi: b[4] = 1; break;
                case FnKeyKind.Adapter: b[5] = 159; break;
                case FnKeyKind.Camera: b[6] = (byte)arg; break;
                case FnKeyKind.Performance: b[7] = (byte)(arg > 0 ? arg : 1); break;
                case FnKeyKind.ControlCenter: b[8] = (byte)(arg > 0 ? arg : 1); break;
            }
            var ev = new FnKeyEvent { Raw = b, Kind = kind, Simulated = true };
            // 模拟按键不能触发"回声"判定（否则点一次按钮要等窗口过去才能再点）
            if (kind == FnKeyKind.Performance) ResetEchoState();
            OnArrived(ev);
        }
    }
}

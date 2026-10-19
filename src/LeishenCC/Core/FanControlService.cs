using System;
using System.Threading;

namespace LeishenCC.Core
{
    /// <summary>
    /// 常驻风扇下发服务。
    ///
    /// 原来的下发写在 FanView.OnUpdated 里，而该订阅随页面 Unloaded 取消——
    /// 打游戏时窗口在托盘、页面不在可视树，下发就停了，EC 会锁在最后一次的值上
    /// （2026-10-18 实测：50 分钟游戏转速恒定 2886 RPM，CPU 卡在 82℃ 温度墙）。
    ///
    /// 现在改成独立服务：只要 LeishenCC 在运行（含托盘），每 2 秒按「曲线 + 当前温度」下发。
    /// 与外部 `LeishenFanMcp --hold` 通过命名互斥量互斥，避免两处同时写 EC。
    /// </summary>
    public static class FanControlService
    {
        private const string OwnerName = @"Local\LeishenFanOwner";
        private const int IntervalMs = 2000;

        private static readonly object Sync = new object();
        private static Timer _timer;
        private static Mutex _owner;
        private static int _lastCpuIdx = -1, _lastGpuIdx = -1, _lastSysIdx = -1;
        private static int _failCount;

        // 强冷（Fn+1）：临时把三路风扇压到 100%，与"自定义曲线"互斥但不是同一个状态。
        // _boostOpenedManual 记录手动控制是强冷自己打开的，撤销时要交还 BIOS 而不是留在手动模式。
        private static bool _boost;
        private static bool _boostOpenedManual;
        private static DateTime _boostStart;

        // 上一次真正写进 EC 的占空比。原厂只在温度档位变化时才写 0x0205，
        // 我们原来每 2 秒无条件写一次，长期把 EC 的热键任务挤掉（实测性能键会静默）。
        // 现在改成"变化才写 + 5 分钟保活"，SMI 写频率从 0.5 次/秒降到接近 0。
        // 保活从 30s 延长到 300s：原厂从不保活，30s 一次仍然偏高，而外部重置 EC 的概率极低。
        private static int _sentCpu = -1, _sentGpu = -1, _sentSys = -1;
        private static DateTime _sentAt = DateTime.MinValue;
        private const int KeepAliveSeconds = 300;

        /// <summary>强冷最长持续分钟数，超时自动撤销（EC 停报按键时的安全网）。</summary>
        private const int BoostMaxMinutes = 20;

        public static bool Running { get; private set; }

        /// <summary>强冷（三路 100%）是否生效。</summary>
        public static bool BoostActive => _boost;

        /// <summary>是否被外部进程（MCP 的 --hold）接管中。</summary>
        public static bool OwnedByOther { get; private set; }

        public static string LastDelivery { get; private set; } = "尚未下发";

        public static string StatusText
        {
            get
            {
                if (OwnedByOther) return "由外部进程接管";
                if (_boost) return "强冷 · 三路 100%";
                if (!Running) return "未接管（BIOS 自动）";
                return "运行中 · " + LastDelivery;
            }
        }

        /// <summary>
        /// Fn+1 强冷开关：按下 → 三路 100%；再按一次 → 撤销。
        /// 撤销时按"强冷之前是谁在管"决定去向：本来就是自定义曲线 → 回到曲线；本来没接管 → 交还 BIOS。
        /// 这是硬件级兜底，不需要温度参与，所以走固定占空比。
        ///
        /// 另加 20 分钟上限：EC 偶尔会停掉性能键上报（实测），这时按第二下收不回来，
        /// 超时自动撤销避免风扇一直锁在 100%。
        /// </summary>
        public static string ToggleBoost()
        {
            lock (Sync)
            {
                if (!Hub.WmiOk) return "EC 接口不可用，无法强冷";
                return _boost ? CancelBoost("手动撤销") : StartBoost();
            }
        }

        /// <summary>
        /// 幂等设置强冷状态。Fn+1 在本机走 WMI2Event 0x30（风扇状态）上报，
        /// data 就是"当前风扇模式"（>0 = fan_max，0 = fan_normal），不是按键按下/抬起，
        /// 所以必须按状态应用而不是 toggle —— 否则连按两下会把一次状态变化翻两次。
        /// </summary>
        public static string SetBoost(bool on)
        {
            lock (Sync)
            {
                if (!Hub.WmiOk) return "EC 接口不可用，无法强冷";
                if (on == _boost) return on ? "强冷已开启（状态一致）" : "强冷已关闭（状态一致）";
                return on ? StartBoost() : CancelBoost("Fn+1 风扇键");
            }
        }

        private static string StartBoost()
        {
            bool needManual = !Running;
            if (needManual)
            {
                if (!AcquireOwner())
                {
                    OwnedByOther = true;
                    return "风扇已被外部进程接管，无法强冷";
                }
                if (!Hub.Wmi.SetFanControl(true))
                {
                    ReleaseOwner();
                    return "SetFanControl(true) 失败";
                }
                Running = true;
                _failCount = 0;
                if (_timer == null) _timer = new Timer(_ => SafeTick(), null, IntervalMs, IntervalMs);
                else _timer.Change(IntervalMs, IntervalMs);
            }

            _boost = true;
            _boostStart = DateTime.Now;
            _boostOpenedManual = needManual;
            ResetHysteresis();
            InvalidateDelivery();
            DeliverNow();          // 立即压到 100%，不等下一个周期
            return "强冷开启：三路 100%" + (needManual ? "（临时接管，撤销后交还 BIOS）" : "（撤销后回到自定义曲线）");
        }

        private static string CancelBoost(string reason)
        {
            _boost = false;
            if (_boostOpenedManual)
            {
                StopTimer();
                ReleaseToBios();
                Running = false;
                _boostOpenedManual = false;
                InvalidateDelivery();
                LastDelivery = DateTime.Now.ToString("HH:mm:ss") + " 已交还 BIOS";
                ReleaseOwner();
                return "强冷关闭（" + reason + "），已交还 BIOS 自动策略";
            }

            ResetHysteresis();
            InvalidateDelivery();
            DeliverNow();              // 回到自定义曲线的当前档位
            return "强冷关闭（" + reason + "），回到自定义曲线";
        }

        /// <summary>
        /// 交回 BIOS 自动风扇策略。光发 0xFB00/0x0206 a2=0 不够：EC 会保留最后一次
        /// 0x0205 的占空比（实测强冷撤销后风扇仍是 100%）。原厂 DisableUserDefineFan 的顺序是
        /// 先 SetFanControlStatus(0)（0x0206=0），再 SetFanSpeedWMI(255,255,255)（0x0205 三路 0xFF），
        /// 这里照抄：0xFF 是"交回 BIOS/自动"的哨兵值。
        /// </summary>
        private static bool ReleaseToBios()
        {
            bool a = Hub.Wmi.SetFanControl(false);
            bool b = Hub.Wmi.SetFanSpeed(255, 255, 255);
            if (!a || !b) Hub.Write("风扇交回 BIOS 失败：0206=" + (a ? "ok" : "fail") + "，0205=" + (b ? "ok" : "fail"));
            return a && b;
        }

        /// <summary>程序启动时按上次状态恢复。</summary>
        public static bool StartIfEnabled()
        {
            if (!Settings.FanControlEnabled) return false;
            return Enable(true);
        }

        /// <summary>开启/关闭自定义风扇控制。开启立即下发一次，之后每 2 秒一次。</summary>
        public static bool Enable(bool on)
        {
            lock (Sync)
            {
                if (!Hub.WmiOk)
                {
                    Hub.Write("风扇控制：EC 接口不可用");
                    return false;
                }

                // 手动开关自定义风扇 = 退出强冷状态，避免"开关是关的但风扇还锁在 100%"
                _boost = false;
                _boostOpenedManual = false;
                InvalidateDelivery();

                if (on)
                {
                    if (!AcquireOwner())
                    {
                        OwnedByOther = true;
                        Hub.Write("风扇控制：已被外部进程接管（LeishenFanMcp --hold），本程序让位");
                        return false;
                    }
                    if (!Hub.Wmi.SetFanControl(true))
                    {
                        ReleaseOwner();
                        Hub.Write("风扇控制：SetFanControl(true) 失败");
                        return false;
                    }

                    Settings.FanControlEnabled = true;
                    Running = true;
                    _failCount = 0;
                    ResetHysteresis();

                    if (_timer == null) _timer = new Timer(_ => SafeTick(), null, 0, IntervalMs);
                    else _timer.Change(0, IntervalMs);

                    Hub.Write("自定义风扇已开启（常驻下发，与页面无关）");
                    return true;
                }

                StopTimer();
                bool ok = Hub.WmiOk && ReleaseToBios();
                Settings.FanControlEnabled = false;
                Running = false;
                ReleaseOwner();
                LastDelivery = DateTime.Now.ToString("HH:mm:ss") + " 已交回 BIOS";
                Hub.Write("自定义风扇已关闭，交回 BIOS：" + (ok ? "成功" : "失败"));
                return ok;
            }
        }

        /// <summary>
        /// 性能模式切换后联动风扇档位。
        ///
        /// 档位与原厂三套自定义曲线一一对应：1=高性能、2=游戏、3=办公（FanControlSelect 0/1/2）。
        /// 自定义风扇运行时，EC 自己的模式风扇表会被本服务每 2 秒的下发覆盖，
        /// 所以这里必须换到对应档位的曲线并立即重发一次 —— 否则模式键看起来完全没效果。
        /// 返回给日志用的说明，没有变化时返回 null。
        /// </summary>
        public static string FollowPowerMode(int mode)
        {
            if (mode < 0 || mode > 2) return null;

            bool slotChanged = Settings.FanProfileIndex != mode;
            if (slotChanged) Settings.FanProfileIndex = mode;

            if (!Running)
                return slotChanged ? "风扇档位 → " + (mode + 1) + "（自定义风扇未开启）" : null;

            ResetHysteresis();
            DeliverNow();
            return "风扇档位 → " + (mode + 1) + "，已按新曲线下发";
        }

        /// <summary>换档位后清掉滞回记忆，否则降档要等温度掉到上一个断点以下。</summary>
        public static void ResetHysteresis()
        {
            _lastCpuIdx = _lastGpuIdx = _lastSysIdx = -1;
        }

        /// <summary>用户改了曲线/档位后立即生效一次。</summary>
        public static void DeliverNow()
        {
            lock (Sync)
            {
                if (!Running || !Hub.WmiOk) return;
                try
                {
                    // 强冷：不看温度，直接把三路压满。
                    if (_boost)
                    {
                        if ((DateTime.Now - _boostStart).TotalMinutes >= BoostMaxMinutes)
                        {
                            // EC 停报性能键时按第二下收不回来，这里兜底
                            Hub.Write("风扇控制：" + CancelBoost("超过 " + BoostMaxMinutes + " 分钟自动撤销"));
                            return;
                        }

                        int bSys = Hub.Support3Fan ? 100 : 255;
                        LastDelivery = string.Format("{0} 强冷 CPU 100% GPU 100% SYS {1}",
                            DateTime.Now.ToString("HH:mm:ss"), Hub.Support3Fan ? "100%" : "—");
                        if (!NeedWrite(100, 100, bSys)) return;      // 已经压满，别重复写 EC
                        bool okb = Hub.Wmi.SetFanSpeed(100, 100, bSys);
                        if (okb) MarkSent(100, 100, bSys);
                        _failCount = okb ? 0 : _failCount + 1;
                        CheckFailLimit();
                        return;
                    }

                    // 复用 Hub 的 EC 温度缓存，不要自己再发一遍 SMI（EC 通道与热键上报共享）
                    if (!Hub.EnsureThermal(3000)) return;
                    int ct = Hub.CpuTemp, gt = Hub.GpuTemp, st = Hub.SysTemp;

                    int slot = Math.Max(0, Math.Min(2, Settings.FanProfileIndex));
                    var pair = FanCurve.ParsePair(Settings.GetUserFan(slot));
                    var sysCurve = FanCurve.Parse(Settings.GetUserFanSys(slot));

                    int cpu = Hysteresis(pair.Cpu, ct, ref _lastCpuIdx);
                    int gpu = Hysteresis(pair.Gpu, gt, ref _lastGpuIdx);
                    int sys = Hub.Support3Fan ? Hysteresis(sysCurve, st, ref _lastSysIdx) : 255;

                    LastDelivery = string.Format("{0} · CPU {1}% GPU {2}% SYS {3} · {4}/{5}/{6}℃",
                        DateTime.Now.ToString("HH:mm:ss"), cpu, gpu,
                        Hub.Support3Fan ? sys.ToString() : "—", ct, gt, st);

                    // 档位没变就不写 —— 原厂也是这个策略，避免高频 SMI 写把 EC 的热键任务挤掉
                    if (!NeedWrite(cpu, gpu, sys)) return;

                    bool ok = Hub.Wmi.SetFanSpeed(cpu, gpu, sys);
                    if (ok) MarkSent(cpu, gpu, sys);
                    _failCount = ok ? 0 : _failCount + 1;

                    CheckFailLimit();
                }
                catch (Exception ex)
                {
                    Hub.Write("风扇下发异常：" + ex.Message);
                }
            }
        }

        /// <summary>占空比变了，或者距上次真正写入已超过 <see cref="KeepAliveSeconds"/> 秒。</summary>
        private static bool NeedWrite(int cpu, int gpu, int sys)
        {
            if (cpu != _sentCpu || gpu != _sentGpu || sys != _sentSys) return true;
            return (DateTime.Now - _sentAt).TotalSeconds >= KeepAliveSeconds;
        }

        private static void MarkSent(int cpu, int gpu, int sys)
        {
            _sentCpu = cpu; _sentGpu = gpu; _sentSys = sys;
            _sentAt = DateTime.Now;
        }

        /// <summary>交回 BIOS / 换档 / 换模式后必须丢掉"已写入"记忆，否则下一次会误判为无需下发。</summary>
        private static void InvalidateDelivery()
        {
            _sentCpu = _sentGpu = _sentSys = -1;
            _sentAt = DateTime.MinValue;
        }

        /// <summary>连续失败（EC 被别的进程抢走等）时自动交回，避免锁死在错误占空比。</summary>
        private static void CheckFailLimit()
        {
            if (_failCount < 5) return;
            Hub.Write("风扇控制：连续 5 次下发失败，自动交回 BIOS");
            StopTimer();
            ReleaseToBios();
            Settings.FanControlEnabled = false;
            Running = false;
            _boost = false;
            _boostOpenedManual = false;
            ReleaseOwner();
        }

        /// <summary>程序退出时调用：停止定时器并把风扇交回 BIOS，避免锁死在最后的占空比。</summary>
        public static void Shutdown()
        {
            lock (Sync)
            {
                StopTimer();
                if (Running && Hub.WmiOk)
                {
                    try { ReleaseToBios(); } catch { }
                }
                Settings.FanControlEnabled = false;
                Running = false;
                _boost = false;
                _boostOpenedManual = false;
                InvalidateDelivery();
                ReleaseOwner();
            }
        }

        private static void SafeTick()
        {
            if (!Running) return;
            try { DeliverNow(); } catch { }
        }

        private static void StopTimer()
        {
            if (_timer != null)
            {
                try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            }
        }

        /// <summary>
        /// 带滞回的档位取值：升温立即升档，降档需低于当前档下界 2℃。
        /// 原厂 PercentFor 是 10℃ 阶梯，断点附近抖动会让转速来回跳（2630↔3617 RPM）。
        /// </summary>
        private static int Hysteresis(FanCurve curve, int tempC, ref int lastIdx, int hyst = 2)
        {
            int idx = curve.IndexFor(tempC);
            if (lastIdx >= 0 && idx < lastIdx)
            {
                int boundary = FanCurve.Temps[lastIdx];
                if (tempC > boundary - hyst) idx = lastIdx;
            }
            lastIdx = idx;
            return curve.Percent[idx];
        }

        private static bool AcquireOwner()
        {
            if (_owner != null) return true;
            try
            {
                using (var existing = Mutex.OpenExisting(OwnerName))
                {
                    return false;      // 别人持有
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 不存在，创建并持有
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                _owner = new Mutex(true, OwnerName);
                OwnedByOther = false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void ReleaseOwner()
        {
            if (_owner == null) return;
            try { _owner.ReleaseMutex(); } catch { }
            try { _owner.Dispose(); } catch { }
            _owner = null;
        }
    }
}

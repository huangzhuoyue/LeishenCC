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

        public static bool Running { get; private set; }

        /// <summary>是否被外部进程（MCP 的 --hold）接管中。</summary>
        public static bool OwnedByOther { get; private set; }

        public static string LastDelivery { get; private set; } = "尚未下发";

        public static string StatusText
        {
            get
            {
                if (OwnedByOther) return "由外部进程接管";
                if (!Running) return "未接管（BIOS 自动）";
                return "运行中 · " + LastDelivery;
            }
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
                    _lastCpuIdx = _lastGpuIdx = _lastSysIdx = -1;

                    if (_timer == null) _timer = new Timer(_ => SafeTick(), null, 0, IntervalMs);
                    else _timer.Change(0, IntervalMs);

                    Hub.Write("自定义风扇已开启（常驻下发，与页面无关）");
                    return true;
                }

                StopTimer();
                bool ok = Hub.Wmi.SetFanControl(false);
                Settings.FanControlEnabled = false;
                Running = false;
                ReleaseOwner();
                LastDelivery = DateTime.Now.ToString("HH:mm:ss") + " 已交回 BIOS";
                Hub.Write("自定义风扇已关闭，交回 BIOS：" + (ok ? "成功" : "失败"));
                return ok;
            }
        }

        /// <summary>用户改了曲线/档位后立即生效一次。</summary>
        public static void DeliverNow()
        {
            lock (Sync)
            {
                if (!Running || !Hub.WmiOk) return;
                try
                {
                    int ct, gt, cf, gf, st, sf;
                    if (!Hub.Wmi.ReadThermal(out ct, out gt, out cf, out gf, out st, out sf)) return;

                    int slot = Math.Max(0, Math.Min(2, Settings.FanProfileIndex));
                    var pair = FanCurve.ParsePair(Settings.GetUserFan(slot));
                    var sysCurve = FanCurve.Parse(Settings.GetUserFanSys(slot));

                    int cpu = Hysteresis(pair.Cpu, ct, ref _lastCpuIdx);
                    int gpu = Hysteresis(pair.Gpu, gt, ref _lastGpuIdx);
                    int sys = Hub.Support3Fan ? Hysteresis(sysCurve, st, ref _lastSysIdx) : 255;

                    bool ok = Hub.Wmi.SetFanSpeed(cpu, gpu, sys);
                    _failCount = ok ? 0 : _failCount + 1;

                    LastDelivery = string.Format("{0} · CPU {1}% GPU {2}% SYS {3} · {4}/{5}/{6}℃",
                        DateTime.Now.ToString("HH:mm:ss"), cpu, gpu,
                        Hub.Support3Fan ? sys.ToString() : "—", ct, gt, st);

                    // 连续失败（EC 被别的进程抢走等）时自动交回，避免锁死在错误占空比
                    if (_failCount >= 5)
                    {
                        Hub.Write("风扇控制：连续 5 次下发失败，自动交回 BIOS");
                        StopTimer();
                        Hub.Wmi.SetFanControl(false);
                        Settings.FanControlEnabled = false;
                        Running = false;
                        ReleaseOwner();
                    }
                }
                catch (Exception ex)
                {
                    Hub.Write("风扇下发异常：" + ex.Message);
                }
            }
        }

        /// <summary>程序退出时调用：停止定时器并把风扇交回 BIOS，避免锁死在最后的占空比。</summary>
        public static void Shutdown()
        {
            lock (Sync)
            {
                StopTimer();
                if (Running && Hub.WmiOk)
                {
                    try { Hub.Wmi.SetFanControl(false); } catch { }
                }
                Settings.FanControlEnabled = false;
                Running = false;
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

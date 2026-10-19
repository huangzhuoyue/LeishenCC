using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace LeishenCC.Core
{
    /// <summary>硬件能力/状态快照，供各页面共享。</summary>
    public static class Hub
    {
        public static GmWmi Wmi = new GmWmi();
        public static Nvml Nvml = new Nvml();

        public static bool WmiOk;
        public static uint SmiVersion;
        public static uint SkuId;
        public static bool Support3Fan;
        public static bool SupportUserFan;
        public static bool SupportHsr;
        public static bool SupportDds;
        public static int CameraStatus;
        public static int AdapterStatus;
        public static uint DisplayMode;     // 1=混合 2=独显 3=UMA
        public static uint HsrStatus;
        public static int FanTableId;

        // 实时
        public static int CpuTemp, GpuTemp, SysTemp;
        public static int CpuFan, GpuFan, SysFan;
        public static double CpuLoad, RamPercent;
        public static ulong RamUsedMb, RamTotalMb;
        public static uint CpuMhz;
        public static int GpuLoad, GpuTempNvml, GpuClock, GpuMemClock;
        public static double GpuMemUsedMb, GpuMemTotalMb;

        // EC 温度/转速缓存时间：EC 的 SMI 通道是热键上报的共享资源，
        // 原厂守护进程只在 5s 定时器里读（约 0.4 次 SMI/s），重写版不能 1s 读一次。
        private static DateTime _thermalAt = DateTime.MinValue;
        private static DateTime _sysThermalAt = DateTime.MinValue;

        /// <summary>EC 温度/转速缓存的采样时间。</summary>
        public static DateTime ThermalAt { get { return _thermalAt; } }

        public static string CpuName = "", GpuName = "";

        /// <summary>采样历史，供概览页趋势图使用。</summary>
        public static readonly SampleHistory History = new SampleHistory();

        /// <summary>程序正在退出：为 true 时不再被「最小化到托盘」拦下关闭请求。</summary>
        public static bool AppExiting;

        /// <summary>慢速状态（每 5 秒）上次快照，用于检测外部改动。</summary>
        private static int _lastPowerMode = -1;
        private static int _lastDisplay = -1;
        private static int _lastFanCtrl = -1;
        private static int _lastCamera = -1;

        public static event Action Updated;
        public static void RaiseUpdated() => Updated?.Invoke();

        public static readonly ObservableCollection<string> Log = new ObservableCollection<string>();

        private static readonly object LogFileSync = new object();

        /// <summary>落盘日志：界面里的日志只在内存里，重启就没了，排查「按键没反应」这类问题必须看得到历史。</summary>
        public static readonly string LogFile =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "leishen.log");

        public static void Write(string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            try
            {
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Log.Add(line);
                    while (Log.Count > 400) Log.RemoveAt(0);
                }));
            }
            catch { }
            WriteFile(msg);
        }

        private static void WriteFile(string msg)
        {
            lock (LogFileSync)
            {
                try
                {
                    var fi = new System.IO.FileInfo(LogFile);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                        System.IO.File.Move(LogFile, LogFile + ".1", true);   // 超过 1MB 轮转一份，别无限长
                    System.IO.File.AppendAllText(LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n",
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }
        }

        public static void ReadCaps()
        {
            Smi r;
            if (Wmi.Read(GmWmi.GET_CAPS, 0, out r))
            {
                SmiVersion = r.a0;
                SkuId = r.a2;
                Support3Fan = r.a2 == 52 || r.a2 == 53;
                SupportUserFan = r.a0 >= 512;
                FanTableId = 0;
                Write($"能力查询: SMI版本={SmiVersion} SKU={SkuId} 三风扇={Support3Fan} 自定义风扇={SupportUserFan}");
            }
            if (Wmi.Read(GmWmi.GET_HW_STATUS, 0, out r))
            {
                AdapterStatus = (int)r.a3;
                SupportHsr = r.a4 == 2;
                CameraStatus = (int)r.a5;
                SupportDds = r.a6 == 2;
                Write($"硬件状态: 适配器={AdapterStatus} HSR={SupportHsr} 摄像头={CameraStatus} DDS={SupportDds}");
            }
            if (Wmi.Read(GmWmi.GET_DISPLAY, 0, out r))
            {
                DisplayMode = r.a2;
                Write($"显示输出: 当前={DisplayMode} HDMI={r.a3} 初始={r.a4}");
            }
            if (Wmi.Read(GmWmi.GET_HSR, 0, out r)) HsrStatus = r.a2;
            if (Wmi.Read(GmWmi.GET_FAN_TABLE, 0, out r)) FanTableId = (int)r.a2;
        }

        /// <summary>
        /// 保证 EC 温度/转速缓存不过期（超过 maxAgeMs 才真正发 SMI）。
        /// EC 的 SMI 通道与热键上报共享，实测高频读写会挤掉 EC 的热键任务（见 README），
        /// 所以界面 1s 刷新时复用缓存，EC 读数按 2~3s 一次。
        /// </summary>
        public static bool EnsureThermal(int maxAgeMs)
        {
            if (!WmiOk) return false;
            var now = DateTime.Now;
            if ((now - _thermalAt).TotalMilliseconds < maxAgeMs) return true;

            Smi r;
            if (!Wmi.Read(GmWmi.GET_THERMAL, 0, out r)) return false;
            CpuTemp = (int)(r.a2 & 0xFF);
            GpuTemp = (int)(r.a3 & 0xFF);
            CpuFan = (int)(r.a4 & 0xFFFF);
            GpuFan = (int)(r.a5 & 0xFFFF);
            _thermalAt = now;

            // SYS 温度/转速变化慢，10s 读一次即可（原厂 0x0207 也是独立一次 SMI）
            if ((now - _sysThermalAt).TotalMilliseconds >= 10000)
            {
                if (Wmi.Read(GmWmi.GET_SYS_THERMAL, 0, out r))
                {
                    SysTemp = (int)(r.a2 & 0xFF);
                    SysFan = (int)(r.a3 & 0xFFFF);
                    _sysThermalAt = now;
                }
            }
            return true;
        }

        public static void Poll()
        {
            if (!WmiOk) return;

            // EC 读数按 2.5s 一采；1s 界面刷新时其余指标（CPU/RAM/NVML）照常更新
            EnsureThermal(2500);

            CpuLoad = SystemStats.CpuPercent();
            double pct; ulong used, total;
            SystemStats.Memory(out pct, out used, out total);
            RamPercent = pct; RamUsedMb = used; RamTotalMb = total;
            CpuMhz = SystemStats.CpuCurrentMhz();

            if (Nvml != null && Nvml.Available)
            {
                GpuTempNvml = Nvml.Temperature();
                GpuLoad = Nvml.Usage();
                GpuClock = Nvml.CoreClock();
                GpuMemClock = Nvml.MemClock();
                Nvml.MemInfo(out GpuMemUsedMb, out GpuMemTotalMb);
            }

            History.Add(DateTime.Now, new[]
            {
                (double)CpuTemp, (double)GpuTemp, (double)SysTemp,
                CpuLoad, (double)GpuLoad, RamPercent,
                (double)CpuFan, (double)GpuFan, (double)SysFan
            });

            RaiseUpdated();
        }

        /// <summary>
        /// 慢速状态刷新（约每 5 秒一次），参考原厂 timer2(5s) + refresh_info 的做法：
        /// 重新读取能力/显示输出/风扇控制/Win 键等"可能被外部改动"的状态，
        /// 并与上次快照比对，发生变化时写日志，界面据此重画卡片。
        /// </summary>
        public static void RefreshStates()
        {
            if (!WmiOk) return;
            bool changed = false;
            Smi r;

            if (Wmi.Read(GmWmi.GET_DISPLAY, 0, out r))
            {
                // a2 正常只有 1/2/3；实测偶尔读回 33、48 这类脏值，不能让它污染状态和日志
                int disp = (int)r.a2;
                if (disp >= 1 && disp <= 3 && disp != _lastDisplay)
                {
                    if (_lastDisplay >= 0) { Write($"显示输出变化: {_lastDisplay} → {disp}"); changed = true; }
                    _lastDisplay = disp;
                    DisplayMode = r.a2;
                }
            }
            if (Wmi.Read(GmWmi.GET_FAN_CTRL, 0, out r))
            {
                // a2 不是稳定的 0/1：实测会读到 65(0x41) 这类值，只有 bit0 是"自定义风扇已开启"，
                // 直接比原值会刷出「自定义风扇状态变化: 1 → 65」这种假告警
                int fanCtrl = (int)(r.a2 & 1u);
                if (fanCtrl != _lastFanCtrl)
                {
                    if (_lastFanCtrl >= 0) { Write($"自定义风扇状态变化: {_lastFanCtrl} → {fanCtrl}"); changed = true; }
                    _lastFanCtrl = fanCtrl;
                }
            }
            if (Wmi.Read(GmWmi.GET_HW_STATUS, 0, out r))
            {
                // a5 正常只有 0/1/2；实测偶尔读回 1449 这类脏值，直接参与比对会刷假告警
                int cam = (int)r.a5;
                if (cam >= 0 && cam <= 2 && cam != _lastCamera)
                {
                    if (_lastCamera >= 0) { Write($"摄像头状态变化: {_lastCamera} → {cam}"); changed = true; }
                    _lastCamera = cam;
                    CameraStatus = cam;
                }
            }

            int mode = Settings.PowerMode;
            if (mode != _lastPowerMode)
            {
                if (_lastPowerMode >= 0)
                {
                    Write($"性能模式变化: {_lastPowerMode} → {mode}");
                    changed = true;
                    // 外部（原厂程序 / EC）改了模式时，自定义风扇也要跟着换档，否则又会被 2 秒周期覆盖
                    if (FanControlService.Running) FanControlService.FollowPowerMode(mode);
                }
                _lastPowerMode = mode;
            }

            if (changed) RaiseUpdated();
        }
    }
}

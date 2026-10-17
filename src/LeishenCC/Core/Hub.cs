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

        public static void Poll()
        {
            if (!WmiOk) return;
            int ct, gt, cf, gf, st, sf;
            if (Wmi.ReadThermal(out ct, out gt, out cf, out gf, out st, out sf))
            {
                CpuTemp = ct; GpuTemp = gt; CpuFan = cf; GpuFan = gf; SysTemp = st; SysFan = sf;
            }
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

            if (Wmi.Read(GmWmi.GET_DISPLAY, 0, out r) && (int)r.a2 != _lastDisplay)
            {
                if (_lastDisplay >= 0) { Write($"显示输出变化: {_lastDisplay} → {r.a2}"); changed = true; }
                _lastDisplay = (int)r.a2;
                DisplayMode = r.a2;
            }
            if (Wmi.Read(GmWmi.GET_FAN_CTRL, 0, out r) && (int)r.a2 != _lastFanCtrl)
            {
                if (_lastFanCtrl >= 0) { Write($"自定义风扇状态变化: {_lastFanCtrl} → {r.a2}"); changed = true; }
                _lastFanCtrl = (int)r.a2;
            }
            if (Wmi.Read(GmWmi.GET_HW_STATUS, 0, out r) && (int)r.a5 != _lastCamera)
            {
                if (_lastCamera >= 0) { Write($"摄像头状态变化: {_lastCamera} → {r.a5}"); changed = true; }
                _lastCamera = (int)r.a5;
                CameraStatus = (int)r.a5;
            }

            int mode = Settings.PowerMode;
            if (mode != _lastPowerMode)
            {
                if (_lastPowerMode >= 0) { Write($"性能模式变化: {_lastPowerMode} → {mode}"); changed = true; }
                _lastPowerMode = mode;
            }

            if (changed) RaiseUpdated();
        }
    }
}

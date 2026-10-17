using System;

namespace LeishenCC.Core
{
    /// <summary>
    /// Fn 组合键的服务端：监听 EC 上报的 GMC_WMIEvent，按原厂逻辑做出响应。
    /// 原厂是「守护进程收事件 → 广播 ControlCenterMessage → 主界面执行」，
    /// 这里是单进程，收完直接执行，行为保持一致。
    /// </summary>
    public static class FnKeyService
    {
        private static readonly FnKeyWatcher Watcher = new FnKeyWatcher();
        private static readonly object Sync = new object();

        public static bool Running => Watcher.Running;
        public static string LastError => Watcher.LastError;
        public static FnKeyEvent LastEvent { get; private set; }
        public static DateTime LastTime { get; private set; }

        /// <summary>性能模式被 Fn 键切换（参数是新模式 0/1/2）</summary>
        public static event Action<int> PowerModeChanged;

        /// <summary>控制中心键：请求把主窗口拉到前台</summary>
        public static event Action ActivateRequested;

        /// <summary>任意 Fn 事件（含原始字节），界面用来显示最近一次按键</summary>
        public static event Action<FnKeyEvent> EventArrived;

        public static bool Start()
        {
            lock (Sync)
            {
                if (Watcher.Running) return true;
                Watcher.Arrived -= OnArrived;
                Watcher.Arrived += OnArrived;
                bool ok = Watcher.Start();
                Hub.Write(ok ? "Fn 组合键监听已启动（root\\wmi:GMC_WMIEvent）" : "Fn 组合键监听启动失败: " + Watcher.LastError);
                return ok;
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                Watcher.Stop();
                Hub.Write("Fn 组合键监听已停止");
            }
        }

        private static void OnArrived(FnKeyEvent ev)
        {
            LastEvent = ev;
            LastTime = DateTime.Now;
            Hub.Write("Fn 键事件: " + ev.Describe());

            try
            {
                switch (ev.Kind)
                {
                    case FnKeyKind.Backlight:
                        ApplyBacklight(ev.Brightness);
                        break;

                    case FnKeyKind.Performance:
                        CyclePowerMode();
                        Hub.Wmi.ClearEvent(2);
                        break;

                    case FnKeyKind.ControlCenter:
                        Hub.Write("控制中心键：拉起主窗口");
                        Hub.Wmi.ClearEvent(3);
                        ActivateRequested?.Invoke();
                        break;

                    case FnKeyKind.Camera:
                        ToggleCamera(ev.CameraValue);
                        Hub.Wmi.ClearEvent(4);
                        break;

                    case FnKeyKind.Hdmi:
                        Hub.Write("外接显示事件：重新读取显示状态");
                        Hub.ReadCaps();
                        break;

                    case FnKeyKind.Adapter:
                        Hub.Write("电源适配器事件");
                        Hub.ReadCaps();
                        break;
                }
            }
            catch (Exception ex)
            {
                Hub.Write("Fn 事件处理异常: " + ex.Message);
            }

            EventArrived?.Invoke(ev);
        }

        /// <summary>
        /// 性能模式键循环，完全照搬原厂：
        /// 当前 0(高性能) → 2(办公)，2 → 1(游戏)，1 → 0(高性能)。
        /// </summary>
        public static int NextMode(int current)
        {
            return current != 0 ? current - 1 : 2;
        }

        private static void CyclePowerMode()
        {
            if (!Hub.WmiOk) { Hub.Write("EC 不可用，忽略性能模式键"); return; }

            int cur = Settings.PowerMode;
            int next = NextMode(cur);
            bool ok = Hub.Wmi.SetPowerMode(next);
            Settings.PowerMode = next;
            PowerPlanService.Apply(next);

            string[] names = { "高性能", "游戏", "办公 / 静音" };
            Hub.Write(string.Format("性能模式键: {0} → {1}（{2}）SMI={3}",
                names[Math.Max(0, Math.Min(2, cur))], names[next], next, ok ? "成功" : "失败"));
            PowerModeChanged?.Invoke(next);
            Hub.RaiseUpdated();     // 让概览页的「当前状态」同步刷新
        }

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

        /// <summary>不按键也能验证链路：直接按某种事件处理一次。</summary>
        public static void Simulate(FnKeyKind kind, int arg)
        {
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
            var ev = new FnKeyEvent { Raw = b, Kind = kind };
            OnArrived(ev);
        }
    }
}

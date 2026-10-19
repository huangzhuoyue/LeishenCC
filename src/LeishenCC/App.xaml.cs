using System;
using System.Threading;
using System.Windows;
using LeishenCC.Core;

namespace LeishenCC
{
    public partial class App : Application
    {
        /// <summary>截图/抓包等无界面模式：不接管风扇，避免影响硬件状态。</summary>
        public static bool Headless;

        /// <summary>Fn 抓包模式（--fnprobe）：停掉所有周期 SMI 轮询，避免干扰事件捕获。</summary>
        public static bool FnProbeMode;

        /// <summary>第二个实例用来通知已有实例把窗口叫到前台。</summary>
        public static EventWaitHandle ShowEvent;

        private static Mutex _instanceMutex;

        public static void ApplyTheme(bool dark)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(dark ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative)
            };
            var merged = Current.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = dict;
            else merged.Add(dict);
        }

        /// <summary>单实例：开机自启 + 手动双击同时发生时，只保留一个进程、一个托盘图标。</summary>
        private bool ClaimSingleInstance()
        {
            bool created;
            _instanceMutex = new Mutex(true, @"Local\LeishenCC.App", out created);
            if (created)
            {
                ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LeishenCC.Show");
                return true;
            }

            // 已有实例：让它把窗口显示出来，本进程静默退出
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(@"Local\LeishenCC.Show")) ev.Set();
            }
            catch { }
            return false;
        }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            this.DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    var log = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                    System.IO.File.AppendAllText(log, DateTime.Now + "\n" + ev.Exception + "\n---\n");
                }
                catch { }
                ev.Handled = true;
            };

            // --startup-enable / --startup-disable：不建窗口，直接接管或恢复开机自启，写 startup.log 后退出
            if (e.Args.Length > 0 && (e.Args[0] == "--startup-enable" || e.Args[0] == "--startup-disable"))
            {
                bool on = e.Args[0] == "--startup-enable";
                bool ok = on ? StartupTask.Enable() : StartupTask.Disable();
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e.Args[0] + "\n" +
                        "result = " + ok + "\n" +
                        (ok ? "" : "error  = " + StartupTask.LastError + "\n") +
                        "exe    = " + StartupTask.ExePath + "\n" +
                        "state  = " + StartupTask.Describe() + "\n",
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
                Hub.AppExiting = true;
                Shutdown();
                return;
            }

            ApplyTheme(Settings.DarkTheme);

            bool shot = e.Args.Length > 0 && e.Args[0] == "--shot";
            string shotDir = null;
            if (shot && e.Args.Length > 1) shotDir = e.Args[1];
            bool lightShot = shot && e.Args.Length > 2 && e.Args[2] == "light";
            if (lightShot) ApplyTheme(false);
            Headless = shot || (e.Args.Length > 0 && e.Args[0] == "--fnprobe");
            FnProbeMode = e.Args.Length > 0 && e.Args[0] == "--fnprobe";

            // --startup：由计划任务在登录时拉起，直接驻留托盘，不弹窗、不抢焦点
            bool startup = Array.IndexOf(e.Args, "--startup") >= 0;

            if (!Headless && !ClaimSingleInstance())
            {
                Shutdown();
                return;
            }

            Hub.WmiOk = Hub.Wmi.Connect();
            if (Hub.WmiOk)
            {
                Hub.ReadCaps();
                Hub.Nvml.Init();
                Hub.CpuName = SystemStats.CpuName();
                Hub.GpuName = SystemStats.GpuName();
            }

            // Fn 组合键（性能模式键 / 控制中心键 / 摄像头键 / 背光）监听
            // --fnprobe 由下面的抓包分支自己启动（先挂事件订阅再启动），避免启动两次
            if (Settings.FnKeyEnabled && !FnProbeMode) FnKeyService.Start();

            // 睡眠/唤醒后 WMI 订阅与缓存的 RW_GMWMI 实例都可能失效：重连 + 重建订阅 + 清残位
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += (s, ev) =>
                {
                    if (ev.Mode != Microsoft.Win32.PowerModes.Resume) return;
                    try
                    {
                        Hub.Wmi.Disconnect();
                        Hub.WmiOk = Hub.Wmi.Connect();
                        Hub.Write("系统唤醒：WMI 通道重连 " + (Hub.WmiOk ? "成功" : "失败：" + Hub.Wmi.LastError));
                        if (Settings.FnKeyEnabled) FnKeyService.Restart("系统唤醒");
                    }
                    catch (Exception ex) { Hub.Write("系统唤醒处理异常：" + ex.Message); }
                };
            }
            catch { }

            // 已接管开机自启时，若程序被移动到别处，用当前路径重建任务
            if (!Headless) StartupTask.RepairIfNeeded();

            var win = new MainWindow();
            MainWindow = win;
            if (Array.IndexOf(e.Args, "collapse") >= 0) win.SetNavCollapsed(true, false);

            if (startup && !Headless)
            {
                // 不调用 Show()：WPF 不会创建窗口句柄，等效于"启动即最小化到托盘"，
                // 不会闪一下窗口，也不抢焦点。托盘图标已由 MainWindow 构造函数建立。
                win.NotifyStarted();
            }
            else
            {
                win.Show();
            }

            // --fnprobe <秒数>：不弹窗，纯抓 Fn 键原始事件，写 fnprobe.log
            // 同时订阅 GMC_WMIEvent（原厂 ControlCenter 用）和 WMI2Event（厂商 Hotkey OSD 的
            // uw3.mof 定义），用于验证两个类名是否指向同一 ACPI 事件源、以及去重窗口是否合适。
            if (e.Args.Length > 0 && e.Args[0] == "--fnprobe")
            {
                int secs = 30;
                if (e.Args.Length > 1) int.TryParse(e.Args[1], out secs);
                var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fnprobe.log");
                var logLock = new object();
                Action<string> log = line =>
                {
                    lock (logLock)
                    {
                        try
                        {
                            System.IO.File.AppendAllText(logPath, line + "\r\n", new System.Text.UTF8Encoding(false));
                        }
                        catch { }
                    }
                };

                log("==== Fn 键抓包 " + DateTime.Now + "  时长 " + secs + "s ====");

                // 已有托盘实例在运行时，probe 会与它抢 GMC/WMI2 事件、抢风扇所有权（Local\LeishenFanOwner），
                // 实测会产生"已被外部进程接管"和两个实例各处理一次事件的假象。抓包必须独占。
                bool otherRunning;
                try
                {
                    using (var m = Mutex.OpenExisting(@"Local\LeishenCC.App")) otherRunning = true;
                }
                catch { otherRunning = false; }
                if (otherRunning)
                {
                    log("检测到已有 LeishenCC 实例在运行：probe 无法独占事件/风扇通道，已退出。请先退出控制中心（含托盘）再抓包。");
                    log("==== 结束 ====");
                    Hub.AppExiting = true;
                    Shutdown();
                    return;
                }

                log("环境: SMI=0x" + Hub.SmiVersion.ToString("X") + " (" + Hub.SmiVersion + ")  SKU=" + Hub.SkuId +
                    "  WMI连接=" + Hub.WmiOk + "  去重窗口=" + Settings.FnEchoWindowMs + "ms");
                log("事件流: root\\wmi:GMC_WMIEvent（BIOS 私有 GUID 74286D6E…）+ root\\wmi:WMI2Event（标准 ACPI GUID ABBC0F20…），两路独立");
                win.Hide();

                DateTime prev = default(DateTime);
                FnKeyService.EventArrived += ev =>
                {
                    var now = DateTime.Now;
                    int gap = prev == default(DateTime) ? -1 : (int)(now - prev).TotalMilliseconds;
                    prev = now;
                    var recent = FnKeyService.RecentEvents;
                    var rec = recent.Length > 0 ? recent[0] : null;
                    log(now.ToString("HH:mm:ss.fff") + "  [GMC] " + ev.Describe() +
                        (gap >= 0 ? "  间隔=" + gap + "ms" : "") +
                        (rec != null ? "  判定=" + (rec.Echo ? "回声丢弃" : "按键") +
                                       "  动作=" + rec.Action + "  " + rec.Ack : "") +
                        "  全32字节:" + ev.FullHex);
                };
                FnKeyService.Wmi2EventArrived += ev =>
                {
                    log(DateTime.Now.ToString("HH:mm:ss.fff") + "  [WMI2] " + ev.Describe() +
                        "  全32字节:" + BitConverter.ToString(ev.Raw).Replace("-", " "));
                    // 顺手读一次三路转速/温度，用来判断强冷/风扇状态事件是否真的生效
                    try
                    {
                        int ct, gt, cf, gf, st, sf;
                        if (Hub.WmiOk && Hub.Wmi.ReadThermal(out ct, out gt, out cf, out gf, out st, out sf))
                            log("        风扇 CPU=" + cf + " GPU=" + gf + " SYS=" + sf +
                                "  温度 CPU=" + ct + " GPU=" + gt + " SYS=" + st);
                    }
                    catch { }
                };

                // 抓包模式只观察，不执行任何硬件动作（不改背光/摄像头/风扇/模式）
                FnKeyService.DryRun = true;
                bool started = FnKeyService.Start();
                log("GMC_WMIEvent 监听启动=" + started + (started ? "" : "  错误=" + FnKeyService.LastError));                log("WMI2Event 监听启动=" + FnKeyService.Wmi2Running +
                    (FnKeyService.Wmi2Running ? "" : "  错误=" + FnKeyService.Wmi2LastError));

                var pt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                pt.Tick += (s, a) =>
                {
                    pt.Stop();
                    log("统计: GMC 事件 " + FnKeyService.EventCount + " 条，忽略回声 " + FnKeyService.EchoCount +
                        " 条，未识别 " + FnKeyService.UnknownCount + " 条，确认失败 " + FnKeyService.AckFailTotal +
                        " 次；WMI2 事件 " + FnKeyService.Wmi2EventCount + " 条；自动重启 " + FnKeyService.RestartCount +
                        " 次；去重窗口 " + FnKeyService.EchoWindowMs + "ms");
                    log("==== 结束 ====");
                    Hub.AppExiting = true;
                    Shutdown();
                };
                pt.Start();
                return;
            }

            if (shot && shotDir != null)
            {
                var timer = new System.Windows.Threading.DispatcherTimer();
                int step = 0;
                // 末尾再拍一次概览，让趋势图有几秒真实采样点
                var pages = new[] { "overview", "perf", "fan", "led", "display", "system", "diag", "overview" };
                timer.Interval = TimeSpan.FromMilliseconds(1200);
                timer.Tick += (s, a) =>
                {
                    try
                    {
                        if (step < pages.Length)
                        {
                            win.SelectPage(pages[step], animate: false);
                            win.UpdateLayout();
                            if (step == 0)
                                System.IO.File.WriteAllText(System.IO.Path.Combine(shotDir, "layout.txt"), win.DebugLayout());
                            win.CaptureTo(System.IO.Path.Combine(shotDir, pages[step] + ".png"));
                            step++;
                        }
                        else
                        {
                            timer.Stop();
                            Hub.AppExiting = true;
                            Application.Current.Shutdown();
                        }
                    }
                    catch (Exception ex)
                    {
                        Hub.Write("截图失败: " + ex.Message);
                        timer.Stop();
                        Hub.AppExiting = true;
                        Application.Current.Shutdown();
                    }
                };
                timer.Start();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { FnKeyService.Stop(); } catch { }
            Hub.Nvml?.Dispose();
            base.OnExit(e);
        }
    }
}

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

            ApplyTheme(Settings.DarkTheme);

            bool shot = e.Args.Length > 0 && e.Args[0] == "--shot";
            string shotDir = null;
            if (shot && e.Args.Length > 1) shotDir = e.Args[1];
            bool lightShot = shot && e.Args.Length > 2 && e.Args[2] == "light";
            if (lightShot) ApplyTheme(false);
            Headless = shot || (e.Args.Length > 0 && e.Args[0] == "--fnprobe");

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
            if (Settings.FnKeyEnabled) FnKeyService.Start();

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
            if (e.Args.Length > 0 && e.Args[0] == "--fnprobe")
            {
                int secs = 30;
                if (e.Args.Length > 1) int.TryParse(e.Args[1], out secs);
                var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fnprobe.log");
                System.IO.File.AppendAllText(logPath,
                    "==== Fn 键抓包 " + DateTime.Now + "  时长 " + secs + "s ====\n");
                win.Hide();
                bool started = FnKeyService.Start();
                System.IO.File.AppendAllText(logPath,
                    "监听启动=" + started + (started ? "" : (" 错误=" + FnKeyService.LastError)) +
                    "  WMI连接=" + Hub.WmiOk + "\n");
                FnKeyService.EventArrived += ev =>
                    System.IO.File.AppendAllText(logPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + ev.Describe() + "  全32字节:" +
                        BitConverter.ToString(ev.Raw) + "\n");
                var pt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                pt.Tick += (s, a) => { pt.Stop(); System.IO.File.AppendAllText(logPath, "==== 结束 ====\n"); Hub.AppExiting = true; Shutdown(); };
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
            Hub.Nvml?.Dispose();
            base.OnExit(e);
        }
    }
}

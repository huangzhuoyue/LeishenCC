using System;
using System.Windows;
using LeishenCC.Core;

namespace LeishenCC
{
    public partial class App : Application
    {
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

            var win = new MainWindow();
            MainWindow = win;
            if (Array.IndexOf(e.Args, "collapse") >= 0) win.SetNavCollapsed(true, false);
            win.Show();

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

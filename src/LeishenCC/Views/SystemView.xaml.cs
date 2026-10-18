using System;
using System.Windows;
using System.Windows.Controls;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    public partial class SystemView : UserControl
    {
        private bool _init = true;

        public SystemView()
        {
            InitializeComponent();

            WinKeySwitch.IsChecked = Settings.WinKeyBlocked;   // 开关打开 = 屏蔽
            TraySwitch.IsChecked = Settings.MinimizeToTray;
            FnSwitch.IsChecked = Settings.FnKeyEnabled;
            RefreshFn();
            InfoText.Text = string.Format(
                "CPU   {0}\nGPU   {1}\nSMI   0x{2:X} ({2})   ·   SKU {3}\n风扇 {4}   ·   自定义风扇 {5}\nHSR   {6}   ·   DDS {7}   ·   摄像头状态 {8}",
                Hub.CpuName, Hub.GpuName, Hub.SmiVersion, Hub.SkuId,
                Hub.Support3Fan ? "3 路" : "2 路",
                Hub.SupportUserFan ? "支持" : "不支持",
                Hub.SupportHsr ? "支持" : "不支持",
                Hub.SupportDds ? "支持" : "不支持",
                Hub.CameraStatus);
            RefreshCamera();
            RefreshStartup();

            _init = false;
            StartupSwitch.Checked += Startup_Toggled;
            StartupSwitch.Unchecked += Startup_Toggled;
            WinKeySwitch.Checked += WinKey_Toggled;
            WinKeySwitch.Unchecked += WinKey_Toggled;
            TraySwitch.Checked += Tray_Toggled;
            TraySwitch.Unchecked += Tray_Toggled;
            FnSwitch.Checked += Fn_Toggled;
            FnSwitch.Unchecked += Fn_Toggled;
            Loaded += (s, e) => { FnKeyService.EventArrived += OnFnEvent; RefreshFn(); };
            Unloaded += (s, e) => FnKeyService.EventArrived -= OnFnEvent;
        }

        private void OnFnEvent(FnKeyEvent ev)
        {
            Dispatcher.BeginInvoke(new Action(RefreshFn));
        }

        // ---------- 开机自启：计划任务接管，替代原厂 \ControlCenter\ControlCenter ----------

        private bool _syncingStartup;

        private void Startup_Toggled(object sender, RoutedEventArgs e)
        {
            if (_init || _syncingStartup) return;

            bool on = StartupSwitch.IsChecked == true;
            bool ok = on ? StartupTask.Enable() : StartupTask.Disable();

            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextMuted" : "Danger");
            if (ok)
            {
                Result.Text = on
                    ? "已接管开机自启：下次登录由重置版启动（托盘常驻），原版 ControlCenter 自启已停用。"
                    : "已恢复原版自启，重置版不再随登录启动。";
            }
            else
            {
                Result.Text = "操作失败：" + StartupTask.LastError;
            }
            Hub.Write("Startup " + (on ? "takeover" : "restore") + " -> " + ok +
                      (ok ? "" : " | " + StartupTask.LastError));

            RefreshStartup();
        }

        private void RefreshStartup()
        {
            _syncingStartup = true;
            bool active;
            try { active = StartupTask.Active; } catch { active = false; }
            StartupSwitch.IsChecked = active;
            _syncingStartup = false;

            var text = StartupTask.Describe();

            // 程序被移动/重新构建到别的目录时，任务里记录的路径会失效
            string reg = StartupTask.Active ? StartupTask.RegisteredExe : null;
            if (!string.IsNullOrEmpty(reg) &&
                !string.Equals(reg, StartupTask.ExePath, StringComparison.OrdinalIgnoreCase))
            {
                text += "\n注意：任务指向 " + reg + "，与当前程序位置不同 —— 关掉再打开本开关即可修正。";
            }
            StartupText.Text = text;
        }

        private void RefreshFn()
        {
            bool on = FnKeyService.Running;
            string head = on
                ? "监听中 · GMC_WMIEvent"
                : (Settings.FnKeyEnabled ? "未启动：" + (FnKeyService.LastError ?? "未知原因") : "已关闭");
            if (FnKeyService.LastEvent == null)
            {
                FnText.Text = head + "\n最近按键：暂无";
            }
            else
            {
                var ev = FnKeyService.LastEvent;
                FnText.Text = head + "\n最近按键：" + FnKeyService.LastTime.ToString("HH:mm:ss") + "  " + ev.Describe();
            }
        }

        private void Fn_Toggled(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            bool on = FnSwitch.IsChecked == true;
            Settings.FnKeyEnabled = on;
            if (on)
            {
                bool ok = FnKeyService.Start();
                Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextMuted" : "Danger");
                Result.Text = ok ? "Fn 组合键监听已启动。" : "启动失败：" + FnKeyService.LastError;
            }
            else
            {
                FnKeyService.Stop();
                Result.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
                Result.Text = "Fn 组合键监听已停止。";
            }
            RefreshFn();
        }

        private void FnPerf_Click(object sender, RoutedEventArgs e)
        {
            FnKeyService.Simulate(FnKeyKind.Performance, 1);
            RefreshFn();
        }

        private void FnCc_Click(object sender, RoutedEventArgs e)
        {
            FnKeyService.Simulate(FnKeyKind.ControlCenter, 1);
            RefreshFn();
        }

        private void FnCam_Click(object sender, RoutedEventArgs e)
        {
            bool enabled;
            CameraService.FindDeviceId(out enabled);
            FnKeyService.Simulate(FnKeyKind.Camera, enabled ? 2 : 1);
            RefreshCamera();
            RefreshFn();
        }

        private void FnLight_Click(object sender, RoutedEventArgs e)
        {
            var zones = LedData.ParseGroup(Settings.GetLedProfile(Settings.LedModule));
            int cur = zones[0].Brightness;
            FnKeyService.Simulate(FnKeyKind.Backlight, cur >= 15 ? 0 : cur + 5);
            RefreshFn();
        }

        private void RefreshCamera()
        {
            bool enabled;
            var id = CameraService.FindDeviceId(out enabled);
            if (id == null)
            {
                CameraText.Text = "未找到匹配 Camera.ini HWID 的摄像头设备。";
                BtnCamOn.IsEnabled = BtnCamOff.IsEnabled = false;
            }
            else
            {
                CameraText.Text = (enabled ? "当前：已启用" : "当前：已禁用") + "   ·   " + id;
                BtnCamOn.IsEnabled = !enabled;
                BtnCamOff.IsEnabled = enabled;
            }
        }

        private void WinKey_Toggled(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            bool blocked = WinKeySwitch.IsChecked == true;
            // SetWinKey 的参数是"是否启用 Win 键"，所以屏蔽时要传 false（a6=1）
            bool ok = Hub.WmiOk && Hub.Wmi.SetWinKey(!blocked);
            if (ok) Settings.WinKeyBlocked = blocked;
            Result.Text = ok
                ? (blocked ? "Windows 键已屏蔽（a6=1）。" : "Windows 键已恢复（a6=0）。")
                : "EC 写入失败。";
            Hub.Write("SetWinKey(enabled=" + !blocked + ") -> " + ok);
        }

        private void Tray_Toggled(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            Settings.MinimizeToTray = TraySwitch.IsChecked == true;
            Result.Text = Settings.MinimizeToTray ? "关闭窗口将最小化到托盘。" : "关闭窗口将直接退出程序。";
        }

        private void CamOn_Click(object sender, RoutedEventArgs e) => SetCamera(true);
        private void CamOff_Click(object sender, RoutedEventArgs e) => SetCamera(false);

        private void SetCamera(bool enable)
        {
            string msg;
            bool ok = CameraService.SetEnabled(enable, out msg);
            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextMuted" : "Danger");
            Result.Text = ok
                ? (enable ? "摄像头已启用。" : "摄像头已禁用。")
                : "操作失败：" + msg.Split('\n')[0];
            Hub.Write("Camera " + (enable ? "enable" : "disable") + " -> " + ok + " | " + msg.Replace("\r\n", " "));
            RefreshCamera();
        }

        private void ResetLed_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new[]
            {
                "61FF0080;61FF0080;61FF0080;61FF0080;61FF0080",
                "12FF0000;12FF0000;12FF0000;12FF0000;12FF0000",
                "1000FFFF;1000FFFF;10FF0000;1000FF00;100000FF",
                "1100FFFF;1100FFFF;1100FFFF;1100FFFF;1100FFFF"
            };
            for (int i = 0; i < 4; i++) Settings.SetLedProfile(i, defaults[i]);
            Settings.LedModule = 0;

            if (Hub.WmiOk)
            {
                foreach (var z in LedData.Zones)
                {
                    var d = LedData.ParseGroup(defaults[0])[Array.IndexOf(LedData.Zones, z)];
                    Hub.Wmi.SetLedZone(z.Zone, d.Raw);
                }
            }
            Result.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            Result.Text = "灯效档案已恢复默认。";
        }
    }
}

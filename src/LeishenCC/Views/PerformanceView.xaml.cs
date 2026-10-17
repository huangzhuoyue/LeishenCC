using System;
using System.Windows;
using System.Windows.Controls;
using LeishenCC.Core;
using Microsoft.Win32;

namespace LeishenCC.Views
{
    public partial class PerformanceView : UserControl
    {
        private bool _init = true;

        public PerformanceView()
        {
            InitializeComponent();

            int mode = Settings.PowerMode;
            if (mode == 0) ModeHigh.IsChecked = true;
            else if (mode == 1) ModeGame.IsChecked = true;
            else ModeOffice.IsChecked = true;

            PlanSwitch.IsChecked = true;
            PlanHint.Text = "游戏/高性能指向高性能计划，办公指向平衡计划；若检测到原厂电源方案则优先使用。";
            StartupSwitch.IsChecked = IsStartupEnabled();

            // Fn 性能模式键按下时同步选中状态；Loaded/Unloaded 成对订阅，
            // 保证切回本页时既能收到后续事件，也能立即按当前设置重画卡片。
            Loaded += (s, e) =>
            {
                FnKeyService.PowerModeChanged += OnExternalMode;
                SyncMode();
            };
            Unloaded += (s, e) => FnKeyService.PowerModeChanged -= OnExternalMode;

            _init = false;
        }

        /// <summary>按 Settings.PowerMode 重画三张模式卡片。</summary>
        private void SyncMode()
        {
            int mode = Settings.PowerMode;
            _init = true;
            if (mode == 0) ModeHigh.IsChecked = true;
            else if (mode == 1) ModeGame.IsChecked = true;
            else ModeOffice.IsChecked = true;
            _init = false;
        }

        private static readonly string[] Names = { "高性能", "游戏", "办公 / 静音" };

        private void OnExternalMode(int mode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _init = true;
                if (mode == 0) ModeHigh.IsChecked = true;
                else if (mode == 1) ModeGame.IsChecked = true;
                else ModeOffice.IsChecked = true;
                _init = false;
                Result.Foreground = (System.Windows.Media.Brush)FindResource("Success");
                Result.Text = string.Format("{0}：Fn 键切换到 {1}",
                    DateTime.Now.ToString("HH:mm:ss"), Names[Math.Max(0, Math.Min(2, mode))]);
            }));
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            int mode = int.Parse((string)rb.Tag);

            if (!Hub.WmiOk)
            {
                Result.Text = "EC 接口不可用，无法下发。";
                Result.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
                return;
            }

            bool ok = Hub.Wmi.SetPowerMode(mode);
            Settings.PowerMode = mode;
            Hub.Write(string.Format("SetPowerMode({0}) -> {1}", mode, ok ? "成功" : "失败"));

            string planMsg = "";
            if (PlanSwitch.IsChecked == true)
            {
                bool pok = PowerPlanService.Apply(mode);
                planMsg = pok ? "，Windows 电源计划已切换" : "，Windows 电源计划切换失败";
            }

            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Success" : "Danger");
            Result.Text = string.Format("{0}：{1}{2}（{3}）",
                DateTime.Now.ToString("HH:mm:ss"),
                new[] { "高性能", "游戏", "办公 / 静音" }[mode],
                ok ? " 已下发" : " 下发失败",
                planMsg.TrimStart('，'));
        }

        private static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
            {
                return k != null && k.GetValue("LeishenCC") != null;
            }
        }

        private void Startup_Changed(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            bool on = StartupSwitch.IsChecked == true;
            using (var k = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (k == null) return;
                if (on) k.SetValue("LeishenCC", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else k.DeleteValue("LeishenCC", false);
            }
        }
    }
}

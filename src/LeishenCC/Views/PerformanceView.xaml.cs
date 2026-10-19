using System;
using System.Windows;
using System.Windows.Controls;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    public partial class PerformanceView : UserControl
    {
        private bool _init = true;
        private bool _selfChange;      // 本次变化是不是本页点出来的

        public PerformanceView()
        {
            InitializeComponent();

            int mode = Settings.PowerMode;
            if (mode == 0) ModeHigh.IsChecked = true;
            else if (mode == 1) ModeGame.IsChecked = true;
            else ModeOffice.IsChecked = true;

            PlanSwitch.IsChecked = Settings.PlanFollowMode;
            PlanSwitch.Click += (s, e) => Settings.PlanFollowMode = PlanSwitch.IsChecked == true;
            PlanHint.Text = "游戏/高性能指向高性能计划，办公指向平衡计划；若检测到原厂电源方案则优先使用。Fn 键切换同样遵守此开关。";

            // 性能模式变化（Fn 键或本页）时同步选中状态；Loaded/Unloaded 成对订阅，
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

        private static readonly string[] Names = PowerModeService.Names;

        private void OnExternalMode(int mode)
        {
            bool self = _selfChange;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _init = true;
                if (mode == 0) ModeHigh.IsChecked = true;
                else if (mode == 1) ModeGame.IsChecked = true;
                else ModeOffice.IsChecked = true;
                _init = false;

                if (self) return;       // 本页点出来的，结果文案已经在 Mode_Checked 里写好了
                Result.Foreground = (System.Windows.Media.Brush)FindResource("Success");
                Result.Text = string.Format("{0}：已切换到 {1}（风扇档位同步为 {2}）",
                    DateTime.Now.ToString("HH:mm:ss"),
                    Names[Math.Max(0, Math.Min(2, mode))],
                    Math.Max(0, Math.Min(2, mode)) + 1);
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

            // 统一入口：SMI + 模式状态 + 电源计划 + 风扇档位联动（自定义风扇开着时也不会被覆盖）
            _selfChange = true;
            bool ok = PowerModeService.Apply(mode, "界面", PlanSwitch.IsChecked == true);
            _selfChange = false;

            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Success" : "Danger");
            Result.Text = string.Format("{0}：{1}{2}（风扇档位 {3}{4}）",
                DateTime.Now.ToString("HH:mm:ss"),
                Names[mode],
                ok ? " 已下发" : " 下发失败",
                mode + 1,
                PlanSwitch.IsChecked == true ? "，电源计划已联动" : "，未切换电源计划");
        }
    }
}

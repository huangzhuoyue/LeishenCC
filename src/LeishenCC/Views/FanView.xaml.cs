using System;
using System.Windows;
using System.Windows.Controls;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    public partial class FanView : UserControl
    {
        private readonly FanCurve[] _cpu = new FanCurve[3];
        private readonly FanCurve[] _gpu = new FanCurve[3];
        private readonly FanCurve[] _sys = new FanCurve[3];
        private int _slot;
        private int _sel;
        private bool _init = true;

        public FanView()
        {
            InitializeComponent();

            for (int i = 0; i < 3; i++)
            {
                var pair = FanCurve.ParsePair(Settings.GetUserFan(i));
                _cpu[i] = pair.Cpu;
                _gpu[i] = pair.Gpu;
                _sys[i] = FanCurve.Parse(Settings.GetUserFanSys(i));
            }

            _slot = Settings.FanProfileIndex;
            if (_slot == 0) Slot1.IsChecked = true;
            else if (_slot == 1) Slot2.IsChecked = true;
            else Slot3.IsChecked = true;

            FanCpu.IsChecked = true;
            FanSwitch.IsChecked = Settings.FanControlEnabled;

            if (!Hub.Support3Fan)
            {
                FanSys.IsEnabled = false;
                FanSys.Content = "系统风扇（本机不支持）";
            }
            if (!Hub.SupportUserFan)
            {
                FanSwitch.IsEnabled = false;
                FanHint.Text = "当前 BIOS/SMI 不支持自定义风扇曲线。";
            }
            else
            {
                FanHint.Text = "开启后本程序每 2 秒按 CPU/GPU/系统温度下发一次占空比；档位 1/2/3 对应高性能/游戏/办公，"
                             + "切性能模式（含 Fn 键）会自动换档并立即下发。退出程序前建议关闭，交回 BIOS。";
            }

            Editor.Changed += OnCurveChanged;
            Loaded += (s, e) =>
            {
                Hub.Updated += OnUpdated;
                FnKeyService.PowerModeChanged += OnPowerModeChanged;
                OnUpdated();
            };
            Unloaded += (s, e) =>
            {
                Hub.Updated -= OnUpdated;
                FnKeyService.PowerModeChanged -= OnPowerModeChanged;
            };

            _init = false;
            LoadSlot();
            FanSwitch.Checked += FanSwitch_Toggled;
            FanSwitch.Unchecked += FanSwitch_Toggled;
            OnUpdated();
        }

        private FanCurve Current
        {
            get
            {
                if (_sel == 0) return _cpu[_slot];
                if (_sel == 1) return _gpu[_slot];
                return _sys[_slot];
            }
        }

        private void LoadSlot()
        {
            Editor.SetCurve(Current.Clone());
            ValuesText.Text = Editor.Curve.ToString();
            Result.Text = "";
        }

        private void Slot_Checked(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            StoreCurrent();
            _slot = int.Parse((string)rb.Tag);
            Settings.FanProfileIndex = _slot;
            LoadSlot();
            // 手动换档也要立刻生效，别等下一个 2 秒周期；滞回记忆要清掉，否则降档会卡住
            FanControlService.ResetHysteresis();
            FanControlService.DeliverNow();
        }

        /// <summary>
        /// 性能模式变化（Fn 键或性能页）时，自定义风扇的档位必须跟着走：
        /// 否则本服务每 2 秒的下发会把模式切换的效果整个盖掉。
        /// 切走前先把当前编辑中的曲线落盘，避免未保存的改动丢失。
        /// </summary>
        private void OnPowerModeChanged(int mode)
        {
            if (mode < 0 || mode > 2 || mode == _slot) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (mode == _slot) return;
                SaveToRegistry();
                _slot = mode;
                _init = true;
                if (_slot == 0) Slot1.IsChecked = true;
                else if (_slot == 1) Slot2.IsChecked = true;
                else Slot3.IsChecked = true;
                _init = false;
                LoadSlot();
                Result.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
                Result.Text = "已按性能模式切到档位 " + (_slot + 1) + "（" + PowerModeService.Names[_slot] + "）。";
            }));
        }

        private void FanSel_Checked(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            StoreCurrent();
            _sel = int.Parse((string)rb.Tag);
            LoadSlot();
        }

        private void StoreCurrent()
        {
            if (Editor.Curve == null) return;
            var c = Editor.Curve.Clone();
            if (_sel == 0) _cpu[_slot] = c;
            else if (_sel == 1) _gpu[_slot] = c;
            else _sys[_slot] = c;
        }

        private void OnCurveChanged()
        {
            ValuesText.Text = Editor.Curve.ToString();
        }

        private void Persist()
        {
            SaveToRegistry();
            FanControlService.DeliverNow();   // 改完立即按新曲线下发一次
        }

        /// <summary>把当前档位的三条曲线写回注册表（不下发）。</summary>
        private void SaveToRegistry()
        {
            StoreCurrent();
            Settings.SetUserFan(_slot, _cpu[_slot].FormatPair(_gpu[_slot]));
            Settings.SetUserFanSys(_slot, _sys[_slot].ToString());
            Hub.Write(string.Format("保存档位 {0}: CPU={1} GPU={2} SYS={3}",
                _slot + 1, _cpu[_slot], _gpu[_slot], _sys[_slot]));
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            Persist();
            Result.Foreground = (System.Windows.Media.Brush)FindResource("Success");
            Result.Text = "已保存到注册表，并在风扇控制开启时于下个周期生效。";
        }

        /// <summary>
        /// 界面上的强冷开关。Fn+1 事件在某些 EC 状态下会停止上报（实测），
        /// 必须留一个不依赖按键的入口，否则风扇会卡在 100% 无法收回。
        /// </summary>
        private void BtnBoost_Click(object sender, RoutedEventArgs e)
        {
            string msg = FanControlService.ToggleBoost();
            Hub.Write("强冷按钮: " + msg);
            Result.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            Result.Text = msg;
            OnUpdated();
        }

        private void BtnDefault_Click(object sender, RoutedEventArgs e)
        {
            var d = DefaultCurve(Hub.FanTableId);
            if (_sel == 0) _cpu[_slot] = d.Cpu.Clone();
            else if (_sel == 1) _gpu[_slot] = d.Gpu.Clone();
            else _sys[_slot] = d.Sys.Clone();
            Editor.SetCurve(Current.Clone());
            ValuesText.Text = Editor.Curve.ToString();
            Persist();
            Result.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            Result.Text = "已恢复为该机型（风扇表 " + Hub.FanTableId + "）的默认曲线。";
        }

        /// <summary>原厂内建默认曲线，按 0xFA00/0x0205 返回的风扇表编号选择。</summary>
        public static (FanCurve Cpu, FanCurve Gpu, FanCurve Sys) DefaultCurve(int tableId)
        {
            string pair, sys;
            if (tableId == 4)
            {
                pair = "30,30,30,30,50,60,70,100;30,30,30,30,50,60,70,100";
                sys = "30,30,30,30,50,60,70,100";
            }
            else
            {
                pair = "0,0,0,0,40,50,60,70;0,0,0,0,30,40,50,70";
                sys = "0,0,0,0,30,40,50,70";
            }
            var p = FanCurve.ParsePair(pair);
            return (p.Cpu, p.Gpu, FanCurve.Parse(sys));
        }

        private void FanSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            bool on = FanSwitch.IsChecked == true;

            if (on)
            {
                var box = MessageBox.Show(
                    "自定义风扇会绕过 BIOS 的自动策略，极端曲线可能导致过热或噪音偏大。\n\n是否开启？",
                    "开启自定义风扇", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (box != MessageBoxResult.OK)
                {
                    _init = true; FanSwitch.IsChecked = false; _init = false;
                    return;
                }
            }

            Persist();
            bool ok = FanControlService.Enable(on);

            if (!ok && on && FanControlService.OwnedByOther)
            {
                Result.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
                Result.Text = "已有外部进程接管风扇（LeishenFanMcp --hold），本程序让位。";
                _init = true; FanSwitch.IsChecked = false; _init = false;
                return;
            }

            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextMuted" : "Danger");
            Result.Text = ok
                ? (on ? "自定义风扇已开启，常驻按曲线下发（与是否停留本页无关）。" : "自定义风扇已关闭，交回 BIOS 控制。")
                : "EC 写入失败，请确认以管理员身份运行。";
        }

        private void OnUpdated()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnUpdated));
                return;
            }
            int temp = _sel == 0 ? Hub.CpuTemp : _sel == 1 ? Hub.GpuTemp : Hub.SysTemp;
            int fan = _sel == 0 ? Hub.CpuFan : _sel == 1 ? Hub.GpuFan : Hub.SysFan;
            LiveText.Text = FanControlService.BoostActive
                ? string.Format("强冷中 · {0}°C · {1} RPM · 三路 100%", temp, fan)
                : string.Format("实时：{0}°C · {1} RPM · 目标 {2}%", temp, fan, Current.PercentFor(temp));
            Editor.SetLive(temp, Current.PercentFor(temp));

            // 下发由 FanControlService 常驻执行，这里只显示状态
            ControlStateText.Text = "控制状态：" + FanControlService.StatusText;
            BtnBoost.Content = FanControlService.BoostActive ? "退出强冷" : "强冷（三路 100%）";
            BtnBoost.IsEnabled = Hub.WmiOk;

            bool on = FanControlService.Running;
            if (FanSwitch.IsChecked != on && !FanControlService.OwnedByOther)
            {
                _init = true;
                FanSwitch.IsChecked = on;
                _init = false;
            }
        }
    }
}

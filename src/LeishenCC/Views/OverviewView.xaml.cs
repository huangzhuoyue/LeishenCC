using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    public partial class OverviewView : UserControl
    {
        private static readonly string[] ModeNames = { "高性能", "游戏", "办公 / 静音" };

        private readonly List<CheckBox> _checks = new List<CheckBox>();
        private int _windowSeconds = 60;
        private DateTime _lastChart = DateTime.MinValue;
        private bool _init = true;

        public OverviewView()
        {
            InitializeComponent();

            SubTitle.Text = Hub.CpuName + "   ·   " + Hub.GpuName;
            DeviceText.Text = string.Format("SMI 版本 {0}   ·   SKU {1}   ·   {2} 风扇",
                Hub.SmiVersion, Hub.SkuId, Hub.Support3Fan ? "3" : "2");

            BuildMetricChecks();
            Win1.IsChecked = true;

            _init = false;
            UpdateChart();

            // 订阅做成 Loaded / Unloaded 成对：页面实例被缓存复用，
            // 只有成对订阅才能保证切回本页后继续收到刷新事件。
            Loaded += (s, e) => { Hub.Updated += OnUpdated; OnUpdated(); };
            Unloaded += (s, e) => Hub.Updated -= OnUpdated;
            OnUpdated();
        }

        private void BuildMetricChecks()
        {
            for (int i = 0; i < 9; i++)
            {
                var cb = new CheckBox
                {
                    Content = SampleHistory.Meta.Names[i],
                    Tag = i,
                    Margin = new Thickness(0, 0, 14, 6),
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary")
                };
                cb.Checked += Metric_Toggled;
                cb.Unchecked += Metric_Toggled;
                _checks.Add(cb);
                MetricPanel.Children.Add(cb);
            }
            _checks[(int)Metric.CpuTemp].IsChecked = true;
            _checks[(int)Metric.GpuTemp].IsChecked = true;
        }

        private void Metric_Toggled(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            UpdateChart();
        }

        private void Window_Changed(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            int v;
            if (int.TryParse((string)rb.Tag, out v)) _windowSeconds = v;
            UpdateChart();
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            var b = sender as Button;
            if (b == null) return;
            Metric[] group;
            switch ((string)b.Tag)
            {
                case "load": group = SampleHistory.Meta.GroupLoad; break;
                case "fan": group = SampleHistory.Meta.GroupFan; break;
                default: group = SampleHistory.Meta.GroupTemp; break;
            }
            _init = true;
            for (int i = 0; i < _checks.Count; i++)
                _checks[i].IsChecked = Array.IndexOf(group, (Metric)i) >= 0;
            _init = false;
            UpdateChart();
        }

        private void UpdateChart()
        {
            var selected = new List<Metric>();
            foreach (var cb in _checks)
            {
                if (cb.IsChecked == true) selected.Add((Metric)(int)cb.Tag);
            }
            Chart.Update(Hub.History, selected, _windowSeconds);
            _lastChart = DateTime.Now;
        }

        private void OnUpdated()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnUpdated));
                return;
            }

            CpuTemp.Text = Hub.CpuTemp.ToString();
            CpuLoad.Text = "占用 " + (int)Hub.CpuLoad + "%";
            CpuBar.Value = Hub.CpuLoad;
            CpuFan.Text = "风扇 " + Hub.CpuFan + " RPM";
            var max = SystemStats.CpuMaxMhz();
            CpuClock.Text = max > 0
                ? string.Format("{0} MHz  /  基频 {1} MHz", Hub.CpuMhz, max)
                : Hub.CpuMhz + " MHz";

            GpuTemp.Text = Hub.GpuTemp.ToString();
            GpuLoad.Text = "占用 " + Hub.GpuLoad + "%";
            GpuBar.Value = Hub.GpuLoad;
            GpuFan.Text = "风扇 " + Hub.GpuFan + " RPM";
            GpuClock.Text = string.Format("核心 {0} MHz  ·  显存 {1} MHz", Hub.GpuClock, Hub.GpuMemClock);

            SysTemp.Text = Hub.SysTemp > 0 ? Hub.SysTemp.ToString() : "--";
            SysFan.Text = "风扇 " + Hub.SysFan + " RPM";
            RamBar.Value = Hub.RamPercent;
            RamText.Text = "内存 " + (int)Hub.RamPercent + "%";
            RamDetail.Text = string.Format("{0:0.0} / {1:0.0} GB   ·   GPU {2:0} / {3:0} MB",
                Hub.RamUsedMb / 1024.0, Hub.RamTotalMb / 1024.0, Hub.GpuMemUsedMb, Hub.GpuMemTotalMb);

            int mode = Settings.PowerMode;
            ModeText.Text = ModeNames[Math.Max(0, Math.Min(2, mode))];
            FanText.Text = Settings.FanControlEnabled
                ? "自定义曲线（档位 " + (Settings.FanProfileIndex + 1) + "）"
                : "BIOS 自动";
            int led = Settings.LedModule;
            LedText.Text = new[] { "游戏", "高性能", "办公", "自定义" }[Math.Max(0, Math.Min(3, led))] + " 灯效档案";
            DisplayText.Text = Hub.DisplayMode == 2 ? "独显直连" : Hub.DisplayMode == 3 ? "核显（UMA）" : "混合输出";

            // 图表 2 秒重绘一次，避免每秒重建大量 Canvas 元素
            if ((DateTime.Now - _lastChart).TotalSeconds >= 2) UpdateChart();
        }
    }
}

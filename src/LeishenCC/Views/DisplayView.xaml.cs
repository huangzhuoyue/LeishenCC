using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    public partial class DisplayView : UserControl
    {
        private bool _init = true;

        public DisplayView()
        {
            InitializeComponent();

            if (Hub.DisplayMode == 1) ModeHybrid.IsChecked = true;
            else if (Hub.DisplayMode == 2) ModeDiscrete.IsChecked = true;
            else if (Hub.DisplayMode == 3) ModeUma.IsChecked = true;

            HsrHint.Text = Hub.SupportHsr
                ? "本机支持面板刷新率切换，切换后立即生效。"
                : "本机 BIOS 未声明 HSR 支持（0xFA00/0x0202 的 a4 != 2），相关按钮仅作诊断用。";
            Hsr1.IsEnabled = Hsr2.IsEnabled = Hub.SupportHsr;
            if (Hub.HsrStatus == 2) Hsr2.IsChecked = true; else Hsr1.IsChecked = true;

            StateText.Text = string.Format("显示模式 = {0}   ·   适配器状态 = {1}   ·   DDS 支持 = {2}",
                Hub.DisplayMode == 1 ? "混合" : Hub.DisplayMode == 2 ? "独显" : "UMA",
                Hub.AdapterStatus, Hub.SupportDds ? "是" : "否");

            _init = false;
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            int mode = int.Parse((string)rb.Tag);

            if (mode == Hub.DisplayMode)
            {
                Result.Text = "已经是当前通路。";
                return;
            }
            var box = MessageBox.Show(
                "切换显示通路需要重启电脑才会生效，期间屏幕可能黑屏数次。\n\n确认写入？",
                "切换显示通路", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (box != MessageBoxResult.OK)
            {
                _init = true;
                if (Hub.DisplayMode == 1) ModeHybrid.IsChecked = true;
                else if (Hub.DisplayMode == 2) ModeDiscrete.IsChecked = true;
                else ModeUma.IsChecked = true;
                _init = false;
                return;
            }

            bool ok = Hub.WmiOk && Hub.Wmi.SetDisplayMode(mode);
            Hub.Write(string.Format("SetDisplayMode({0}) -> {1}", mode, ok));
            Hub.DisplayMode = (uint)mode;
            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Warn" : "Danger");
            Result.Text = ok ? "已写入 BIOS 请求，重启后生效。" : "写入失败（EC 未响应）。";
        }

        private void Hsr_Checked(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            int v = int.Parse((string)rb.Tag);
            bool ok = Hub.WmiOk && Hub.Wmi.SetHsr(v);
            Hub.HsrStatus = (uint)v;
            Result.Text = ok ? "刷新率模式已切换。" : "刷新率切换失败。";
        }

        private void Reboot_Click(object sender, RoutedEventArgs e)
        {
            var box = MessageBox.Show("将立即重启电脑，未保存的工作会丢失。继续？",
                "重启", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (box != MessageBoxResult.OK) return;
            Process.Start("shutdown.exe", "-r -t 0");
        }
    }
}

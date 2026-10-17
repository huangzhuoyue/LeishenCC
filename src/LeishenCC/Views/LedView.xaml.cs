using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    public partial class LedView : UserControl
    {
        private readonly Dictionary<int, LedData[]> _profiles = new Dictionary<int, LedData[]>();
        private readonly DispatcherTimer _debounce = new DispatcherTimer();
        private int _profile;
        private int _zone;
        private bool _init = true;
        private bool _suspend;

        public LedView()
        {
            InitializeComponent();
            _debounce.Interval = TimeSpan.FromMilliseconds(140);
            _debounce.Tick += (s, e) => { _debounce.Stop(); PushCurrent(false); };

            for (int i = 0; i < 4; i++) _profiles[i] = LedData.ParseGroup(Settings.GetLedProfile(i));

            _profile = Math.Max(0, Math.Min(3, Settings.LedModule));
            if (_profile == 0) Prof0.IsChecked = true;
            else if (_profile == 1) Prof1.IsChecked = true;
            else if (_profile == 2) Prof2.IsChecked = true;
            else Prof3.IsChecked = true;

            Zone0.IsChecked = true;
            _init = false;
            SyncFromData();
        }

        private LedData Current => _profiles[_profile][_zone];

        private void SyncFromData()
        {
            _suspend = true;
            var d = Current;
            SetModeRadio(d.Mode);
            Bright.Value = Math.Max(0, Math.Min(15, d.Brightness));
            BrightText.Text = ((int)Bright.Value).ToString();
            SlideR.Value = d.R;
            SlideG.Value = d.G;
            SlideB.Value = d.B;
            TextR.Text = d.R.ToString();
            TextG.Text = d.G.ToString();
            TextB.Text = d.B.ToString();
            HexBox.Text = d.Hex.Substring(2);
            UpdatePreview();
            _suspend = false;
        }

        private void SetModeRadio(int mode)
        {
            if (mode == 0) ModeOff.IsChecked = true;
            else if (mode == 1) ModeStatic.IsChecked = true;
            else if (mode == 3) ModeBreath.IsChecked = true;
            else if (mode == 6) ModeCycle.IsChecked = true;
            else if (mode == 7) ModeAmbi.IsChecked = true;
        }

        private void UpdatePreview()
        {
            Preview.Background = new SolidColorBrush(Color.FromRgb(
                (byte)SlideR.Value, (byte)SlideG.Value, (byte)SlideB.Value));
        }

        private void Prof_Checked(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            _profile = int.Parse((string)rb.Tag);
            Settings.LedModule = _profile;
            SyncFromData();
        }

        private void Zone_Checked(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            _zone = int.Parse((string)rb.Tag);
            SyncFromData();
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_init || _suspend) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            Current.Mode = int.Parse((string)rb.Tag);
            if (Current.Mode != 0 && Current.Brightness == 0) Current.Brightness = 15;
            _debounce.Stop();
            _debounce.Start();
            SyncFromData();
        }

        private void Bright_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_init || _suspend || BrightText == null) return;
            BrightText.Text = ((int)Bright.Value).ToString();
            Current.Brightness = (int)Bright.Value;
            if (Current.Mode == 0 && Bright.Value > 0) Current.Mode = 1;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Rgb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_init || _suspend || TextR == null) return;
            Current.R = (byte)SlideR.Value;
            Current.G = (byte)SlideG.Value;
            Current.B = (byte)SlideB.Value;
            TextR.Text = ((int)SlideR.Value).ToString();
            TextG.Text = ((int)SlideG.Value).ToString();
            TextB.Text = ((int)SlideB.Value).ToString();
            HexBox.Text = Current.Hex.Substring(2);
            UpdatePreview();
            _debounce.Stop();
            _debounce.Start();
        }

        private void HexBox_Changed(object sender, TextChangedEventArgs e)
        {
            if (_init || _suspend || HexBox.Text.Length != 6) return;
            uint rgb;
            if (!uint.TryParse(HexBox.Text, System.Globalization.NumberStyles.HexNumber, null, out rgb)) return;
            _suspend = true;
            SlideR.Value = (rgb >> 16) & 0xFF;
            SlideG.Value = (rgb >> 8) & 0xFF;
            SlideB.Value = rgb & 0xFF;
            TextR.Text = ((int)SlideR.Value).ToString();
            TextG.Text = ((int)SlideG.Value).ToString();
            TextB.Text = ((int)SlideB.Value).ToString();
            UpdatePreview();
            _suspend = false;
            Current.R = (byte)SlideR.Value;
            Current.G = (byte)SlideG.Value;
            Current.B = (byte)SlideB.Value;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            uint rgb = Convert.ToUInt32((string)btn.Tag, 16);
            Current.R = (byte)((rgb >> 16) & 0xFF);
            Current.G = (byte)((rgb >> 8) & 0xFF);
            Current.B = (byte)(rgb & 0xFF);
            if (Current.Mode == 0) Current.Mode = 1;
            SyncFromData();
            PushCurrent(false);
        }

        private void PushCurrent(bool save)
        {
            if (!Hub.WmiOk) return;
            var z = LedData.Zones[_zone];
            bool ok = Hub.Wmi.SetLedZone(z.Zone, Current.Raw);
            Hub.Write(string.Format("LED zone {0} <- 0x{1:X8} {2}", z.Zone, Current.Raw, ok ? "OK" : "FAIL"));
            if (save) SaveProfile();
            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextMuted" : "Danger");
            Result.Text = ok ? "已下发到 " + z.Name : "下发失败";
        }

        private void SaveProfile()
        {
            Settings.SetLedProfile(_profile, LedData.Join(_profiles[_profile]));
            Settings.LedModule = _profile;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            PushCurrent(true);
        }

        private void ApplyAll_Click(object sender, RoutedEventArgs e)
        {
            if (!Hub.WmiOk) return;
            var d = Current;
            int ok = 0;
            foreach (var z in LedData.Zones)
            {
                if (Hub.Wmi.SetLedZone(z.Zone, d.Raw)) ok++;
            }
            SaveProfile();
            Result.Foreground = (System.Windows.Media.Brush)FindResource(ok > 0 ? "TextMuted" : "Danger");
            Result.Text = string.Format("已下发 {0}/5 个灯区（原厂还会额外写一次 zone 6=整个键盘）", ok);
        }
    }
}

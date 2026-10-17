using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    public partial class DiagnosticView : UserControl
    {
        public DiagnosticView()
        {
            InitializeComponent();
            LogList.ItemsSource = Hub.Log;
            if (Hub.Log.Count > 0) LogList.ScrollIntoView(Hub.Log[Hub.Log.Count - 1]);
            Hub.Log.CollectionChanged += (s, e) =>
            {
                if (Hub.Log.Count > 0) LogList.ScrollIntoView(Hub.Log[Hub.Log.Count - 1]);
            };
        }

        private static ushort Parse16(string s)
        {
            ushort v;
            ushort.TryParse((s ?? "").Trim().Replace("0x", ""), NumberStyles.HexNumber, null, out v);
            return v;
        }

        private static uint Parse32(string s)
        {
            uint v;
            uint.TryParse((s ?? "").Trim().Replace("0x", ""), NumberStyles.HexNumber, null, out v);
            return v;
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            var cmd = new Smi
            {
                a0 = Parse16(InA0.Text),
                a1 = Parse16(InA1.Text),
                a2 = Parse32(InA2.Text),
                a3 = Parse32(InA3.Text),
                a4 = Parse32(InA4.Text),
                a5 = Parse32(InA5.Text),
                a6 = Parse32(InA6.Text),
            };
            Smi res;
            bool ok = Hub.Wmi.TryInvoke(cmd, out res);
            Hub.Write("RAW " + cmd + (ok ? "  ->  " + res : "  ->  FAILED: " + Hub.Wmi.LastError));
            OutText.Text = ok
                ? string.Format("请求  {0}\n响应  {1}", cmd, res)
                : "请求失败：" + Hub.Wmi.LastError;
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var parts = ((string)btn.Tag).Split(',');
            InA0.Text = parts[0];
            InA1.Text = parts[1];
            InA2.Text = parts[2];
            InA3.Text = parts[3];
            InA4.Text = parts[4];
            InA5.Text = parts[5];
            InA6.Text = parts[6];
            Send_Click(sender, e);
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Hub.Log.Clear();
        }
    }
}

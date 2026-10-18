using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LeishenCC.Core;
using LeishenCC.Views;
using WinForms = System.Windows.Forms;

namespace LeishenCC
{
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, FrameworkElement> _views = new Dictionary<string, FrameworkElement>();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly DispatcherTimer _slow = new DispatcherTimer();
        private WinForms.NotifyIcon _tray;
        private bool _exiting;

        public MainWindow()
        {
            InitializeComponent();

            ThemeSwitch.IsChecked = Settings.DarkTheme;
            SetNavCollapsed(Settings.NavCollapsed, animate: false);

            if (!Hub.WmiOk)
            {
                LblStatus.Text = "EC 接口不可用：" + (Hub.Wmi.LastError ?? "未知原因");
                LblStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
                NavStatusText.Text = "EC 不可用";
                NavStatusDot.Fill = (System.Windows.Media.Brush)FindResource("Danger");
            }
            else
            {
                LblStatus.Text = string.Format("EC 接口已连接 · SMI {0} · SKU {1} · {2}",
                    Hub.SmiVersion, Hub.SkuId, Hub.CpuName);
                NavStatusText.Text = "EC 已连接";
            }
            LblModel.Text = "重建版 · " + (Hub.GpuName.Length > 0 ? Hub.GpuName : "原生界面");

            // 确保初始页已加载
            SelectPage("overview");

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) =>
            {
                Hub.Poll();                 // 快刷新：温度/占用/转速（1s）
                LblClock.Text = DateTime.Now.ToString("HH:mm:ss");
            };
            _timer.Start();

            // 慢刷新：参考原厂 timer2(5s) + refresh_info，重新读取可能被外部改动的状态
            _slow.Interval = TimeSpan.FromSeconds(5);
            _slow.Tick += (s, e) => Hub.RefreshStates();
            _slow.Start();

            Hub.Poll();
            SetupTray();

            // Fn 组合键：控制中心键拉起窗口，性能模式键弹提示
            FnKeyService.ActivateRequested += OnFnActivate;
            FnKeyService.PowerModeChanged += OnFnPowerMode;

            // 恢复上次的自定义风扇状态：常驻下发，不依赖页面可见性
            if (!App.Headless) FanControlService.StartIfEnabled();

            Closing += OnClosing;
            Activated += (s, e) => Hub.Poll();      // 从托盘恢复时立刻补一次刷新
            StateChanged += (s, e) =>
            {
                UpdateMaxIcon();
                if (WindowState == WindowState.Minimized && Settings.MinimizeToTray && _tray != null)
                {
                    Hide();
                }
            };
            UpdateMaxIcon();
        }

        // ---------- 自绘标题栏 ----------

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdateMaxIcon()
        {
            if (MaxIcon == null) return;
            MaxIcon.Data = WindowState == WindowState.Maximized
                ? Geometry.Parse("M4.2 6.2 H15.8 V17.8 H4.2 Z M6.2 6.2 V4.2 H17.8 V15.8 H15.8")
                : Geometry.Parse("M4.5 4.5 h15 v15 h-15 Z");
            BtnMax.ToolTip = WindowState == WindowState.Maximized ? "向下还原" : "最大化";
        }

        private void SetupTray()
        {
            try
            {
                _tray = new WinForms.NotifyIcon();
                _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                _tray.Text = "雷神控制中心（重建版）";
                _tray.Visible = true;
                _tray.DoubleClick += (s, e) => Restore();

                var menu = new WinForms.ContextMenuStrip();
                menu.Items.Add("打开控制中心", null, (s, e) => Restore());
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add("退出", null, (s, e) =>
                {
                    _exiting = true;
                    Hub.AppExiting = true;
                    _tray.Visible = false;
                    Application.Current.Shutdown();
                });
                _tray.ContextMenuStrip = menu;
            }
            catch (Exception ex)
            {
                Hub.Write("托盘初始化失败: " + ex.Message);
            }
        }

        private void OnFnActivate()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Restore();
                SelectPage("perf");
            }));
        }

        private static readonly string[] ModeNames = { "高性能", "游戏", "办公 / 静音" };

        private void OnFnPowerMode(int mode)
        {
            if (mode < 0 || mode > 2) return;
            try
            {
                _tray?.ShowBalloonTip(2500, "性能模式", "已切换到 " + ModeNames[mode], WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        private void Restore()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_exiting && !Hub.AppExiting && Settings.MinimizeToTray && _tray != null)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            _timer.Stop();
            _slow.Stop();
            FanControlService.Shutdown();   // 退出前交回 BIOS，避免把风扇锁在最后的占空比
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }

        private void Theme_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool dark = ThemeSwitch.IsChecked == true;
            Settings.DarkTheme = dark;
            App.ApplyTheme(dark);
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null || Host == null) return;
            string key = rb.Tag as string ?? "overview";
            if (!_views.TryGetValue(key, out var view))
            {
                switch (key)
                {
                    case "perf": view = new PerformanceView(); break;
                    case "fan": view = new FanView(); break;
                    case "led": view = new LedView(); break;
                    case "display": view = new DisplayView(); break;
                    case "system": view = new SystemView(); break;
                    case "diag": view = new DiagnosticView(); break;
                    default: view = new OverviewView(); break;
                }
                _views[key] = view;
            }
            if (!ReferenceEquals(Host.Content, view))
            {
                Host.Content = view;
                AnimateIn(view);
            }
            Hub.RaiseUpdated();     // 切页后立刻把当前数据推给新页面
        }

        // ---------- 页面切换动效 ----------

        /// <summary>新页面淡入 + 轻微上移，参照 Win11 的页面过渡节奏。</summary>
        private void AnimateIn(FrameworkElement el)
        {
            if (el == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(240);

            var tt = new TranslateTransform(0, 14);
            el.RenderTransform = tt;
            el.Opacity = 0;

            var yAnim = new DoubleAnimation(14, 0, dur) { EasingFunction = ease };
            var oAnim = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            oAnim.Completed += (s, a) =>
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
            };
            tt.BeginAnimation(TranslateTransform.YProperty, yAnim);
            el.BeginAnimation(UIElement.OpacityProperty, oAnim);

            // 兜底：无论动画是否真正时钟化，结束后都把内容恢复到可见状态
            var guard = new DispatcherTimer { Interval = dur + TimeSpan.FromMilliseconds(80) };
            guard.Tick += (s, a) =>
            {
                guard.Stop();
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
                el.RenderTransform = Transform.Identity;
            };
            guard.Start();
        }

        // ---------- 侧边栏展开 / 收起 ----------

        private const double NavWidthExpanded = 250.0;   // 对应 OpenFRP 的 OpenPaneLength
        private const double NavWidthCollapsed = 60.0;   // 对应 CompactPaneLength
        private bool _navCollapsed;

        private void Collapse_Click(object sender, RoutedEventArgs e) => SetNavCollapsed(!_navCollapsed);

        public void SetNavCollapsed(bool collapsed, bool animate = true)
        {
            _navCollapsed = collapsed;
            Settings.NavCollapsed = collapsed;
            BtnCollapse.ToolTip = collapsed ? "展开导航栏" : "收起导航栏";

            // 对齐规则（与 Fluent NavigationView 一致）：
            // 展开时汉堡图标与导航图标「左边缘」对齐（都在 24px 处）；
            // 收起时二者图标「中心」都落在栏中心 30px 处。
            // 按钮 x0 + Padding.Left = 图标左边缘；导航项 8(Margin) + Padding.Left = 图标左边缘。
            NavHeader.Margin = collapsed ? new Thickness(14, 14, 6, 14) : new Thickness(16, 14, 8, 14);
            NavDivider.Margin = collapsed ? new Thickness(10, 8, 10, 8) : new Thickness(16, 8, 16, 8);
            NavStatusBox.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            NavThemeBox.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            NavStatusRow.Margin = collapsed ? new Thickness(0, 10, 0, 0) : new Thickness(14, 10, 14, 0);
            NavThemeRow.Margin = collapsed ? new Thickness(0, 12, 0, 0) : new Thickness(14, 12, 14, 0);
            LblModel.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;

            foreach (var rb in new[] { NavOverview, NavPerf, NavFan, NavLed, NavDisplay, NavSystem, NavDiag })
                rb.Padding = collapsed ? new Thickness(14, 0, 0, 0) : new Thickness(16, 0, 10, 0);

            if (!animate)
            {
                Nav.Width = collapsed ? NavWidthCollapsed : NavWidthExpanded;
                ApplyNavLabels(!collapsed);
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(220);
            if (collapsed) ApplyNavLabels(false);   // 收起时先隐文字，避免动画中被裁切

            var anim = new DoubleAnimation(collapsed ? NavWidthCollapsed : NavWidthExpanded, dur)
            {
                EasingFunction = ease
            };
            anim.Completed += (s, a) => { if (!collapsed) ApplyNavLabels(true); };
            Nav.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private void ApplyNavLabels(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            NavTitle.Visibility = v;
            NavStatusText.Visibility = v;
            NavThemeLabel.Visibility = v;
            LblModel.Visibility = v;
            foreach (var rb in new[] { NavOverview, NavPerf, NavFan, NavLed, NavDisplay, NavSystem, NavDiag })
                SetChildTextVisibility(rb, v);
        }

        private static void SetChildTextVisibility(System.Windows.Media.Visual parent, Visibility v)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb) tb.Visibility = v;
                else if (child is System.Windows.Media.Visual vis) SetChildTextVisibility(vis, v);
            }
        }

        /// <summary>供 UI 自检脚本切页；animate=false 用于截图回归，避免抓到淡入起始帧。</summary>
        public void SelectPage(string key, bool animate = true)
        {
            if (!_views.TryGetValue(key, out var view))
            {
                switch (key)
                {
                    case "perf": view = new PerformanceView(); break;
                    case "fan": view = new FanView(); break;
                    case "led": view = new LedView(); break;
                    case "display": view = new DisplayView(); break;
                    case "system": view = new SystemView(); break;
                    case "diag": view = new DiagnosticView(); break;
                    default: view = new OverviewView(); break;
                }
                _views[key] = view;
            }
            if (!ReferenceEquals(Host.Content, view))
            {
                Host.Content = view;
                if (animate) AnimateIn(view);
            }
            Hub.RaiseUpdated();
            switch (key)
            {
                case "perf": NavPerf.IsChecked = true; break;
                case "fan": NavFan.IsChecked = true; break;
                case "led": NavLed.IsChecked = true; break;
                case "display": NavDisplay.IsChecked = true; break;
                case "system": NavSystem.IsChecked = true; break;
                case "diag": NavDiag.IsChecked = true; break;
                default: NavOverview.IsChecked = true; break;
            }
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>供 UI 自检脚本检查当前内容。</summary>
        public UIElement HostContent => Host.Content as UIElement;

        /// <summary>自检用：输出关键元素的实际布局位置。</summary>
        public string DebugLayout()
        {
            var sb = new System.Text.StringBuilder();
            void Dump(string name, FrameworkElement el)
            {
                if (el == null) { sb.AppendLine(name + " = null"); return; }
                var p = el.TransformToAncestor(this).Transform(new Point(0, 0));
                sb.AppendLine(string.Format("{0}: {1:0}x{2:0} @ ({3:0},{4:0}) vis={5} op={6:0.##}",
                    name, el.ActualWidth, el.ActualHeight, p.X, p.Y, el.Visibility, el.Opacity));
            }
            Dump("BtnMax", BtnMax);
            Dump("BtnCollapse", BtnCollapse);
            Dump("Nav", Nav);
            Dump("NavOverview", NavOverview);
            Dump("Host", Host);
            DumpIcon("汉堡图标", BtnCollapse);
            DumpIcon("导航图标", NavOverview);
            return sb.ToString();
        }

        private void DumpIcon(string name, System.Windows.DependencyObject root)
        {
            var vb = FindVisualChild<Viewbox>(root);
            if (vb == null) { System.Diagnostics.Debug.WriteLine(name + " = null"); return; }
            var p = vb.TransformToAncestor(this).Transform(new Point(0, 0));
            var text = string.Format("{0}: {1:0}x{2:0} 左边缘={3:0} 中心={4:0}",
                name, vb.ActualWidth, vb.ActualHeight, p.X, p.X + vb.ActualWidth / 2.0);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout_icons.txt"), text + "\n");
        }

        private static T FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
        {
            if (parent == null) return null;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var r = FindVisualChild<T>(child);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>把窗口自身渲染成 PNG，用于回归检查界面。</summary>
        public void CaptureTo(string path)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        }
    }
}

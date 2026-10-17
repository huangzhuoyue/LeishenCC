using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    /// <summary>
    /// 8 点风扇曲线编辑：拖动折点即可改占空比，横轴为 30-100℃，纵轴 0-100%。
    /// </summary>
    public partial class CurveEditor : UserControl
    {
        private readonly double _left = 36, _right = 10, _top = 10, _bottom = 22;
        private readonly Ellipse[] _dots = new Ellipse[8];
        private Polyline _line;
        private Line _liveLine;
        private Ellipse _liveDot;
        private int _dragIndex = -1;
        private int _liveTemp = -1, _livePercent = -1;

        public FanCurve Curve { get; private set; } = new FanCurve();
        public event Action Changed;

        public CurveEditor()
        {
            InitializeComponent();
            SizeChanged += (s, e) => Render();
            Loaded += (s, e) => Render();
        }

        public void SetCurve(FanCurve curve)
        {
            Curve = curve ?? new FanCurve();
            Render();
        }

        public void SetLive(int temp, int percent)
        {
            _liveTemp = temp;
            _livePercent = percent;
            UpdateLive();
        }

        private double XFor(int i)
        {
            double w = Math.Max(10, Plot.ActualWidth - _left - _right);
            return _left + i * w / 7.0;
        }

        private double YFor(double v)
        {
            double h = Math.Max(10, Plot.ActualHeight - _top - _bottom);
            v = Math.Max(0, Math.Min(100, v));
            return _top + (1 - v / 100.0) * h;
        }

        private void Render()
        {
            if (Plot.ActualWidth < 40 || Plot.ActualHeight < 40) return;
            Plot.Children.Clear();
            _dots[0] = null;
            var grid = (Brush)FindResource("ChartGrid");
            var muted = (Brush)FindResource("TextMuted");
            var accent = (Brush)FindResource("Accent");
            var card = (Brush)FindResource("CardFill");

            // 横向网格
            for (int v = 0; v <= 100; v += 25)
            {
                var y = YFor(v);
                Plot.Children.Add(new Line
                {
                    X1 = _left, X2 = Plot.ActualWidth - _right, Y1 = y, Y2 = y,
                    Stroke = grid, StrokeThickness = 1
                });
                var lbl = new TextBlock
                {
                    Text = v + "%",
                    FontSize = 10,
                    Foreground = muted,
                    FontFamily = (FontFamily)FindResource("NumFont")
                };
                Canvas.SetLeft(lbl, 2);
                Canvas.SetTop(lbl, y - 7);
                Plot.Children.Add(lbl);
            }

            // 纵向网格 + 温度标签
            for (int i = 0; i < 8; i++)
            {
                var x = XFor(i);
                Plot.Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = _top, Y2 = Plot.ActualHeight - _bottom,
                    Stroke = grid, StrokeThickness = 1
                });
                var lbl = new TextBlock
                {
                    Text = FanCurve.Temps[i].ToString(),
                    FontSize = 10,
                    Foreground = muted,
                    FontFamily = (FontFamily)FindResource("NumFont")
                };
                lbl.Measure(new Size(100, 100));
                Canvas.SetLeft(lbl, x - lbl.DesiredSize.Width / 2);
                Canvas.SetTop(lbl, Plot.ActualHeight - _bottom + 5);
                Plot.Children.Add(lbl);
            }

            // 曲线
            var pts = new PointCollection();
            for (int i = 0; i < 8; i++) pts.Add(new Point(XFor(i), YFor(Curve.Percent[i])));
            _line = new Polyline
            {
                Points = pts,
                Stroke = accent,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            Plot.Children.Add(_line);

            // 折点
            for (int i = 0; i < 8; i++)
            {
                var dot = new Ellipse
                {
                    Width = 11,
                    Height = 11,
                    Fill = accent,
                    Stroke = card,
                    StrokeThickness = 2,
                    Tag = i
                };
                Canvas.SetLeft(dot, pts[i].X - 5.5);
                Canvas.SetTop(dot, pts[i].Y - 5.5);
                Plot.Children.Add(dot);
                _dots[i] = dot;
            }

            // 实时标记层
            _liveLine = new Line { Stroke = (Brush)FindResource("Warn"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
            _liveDot = new Ellipse { Width = 9, Height = 9, Fill = (Brush)FindResource("Warn"), Visibility = Visibility.Collapsed };
            Plot.Children.Add(_liveLine);
            Plot.Children.Add(_liveDot);
            UpdateLive();
        }

        private void UpdateLive()
        {
            if (_liveLine == null || _liveTemp < 0) return;
            int idx = Curve.IndexFor(_liveTemp);
            double x = XFor(idx);
            _liveLine.X1 = x; _liveLine.X2 = x;
            _liveLine.Y1 = _top; _liveLine.Y2 = Plot.ActualHeight - _bottom;
            double y = YFor(_livePercent >= 0 ? _livePercent : Curve.Percent[idx]);
            Canvas.SetLeft(_liveDot, x - 4.5);
            Canvas.SetTop(_liveDot, y - 4.5);
            _liveDot.Visibility = Visibility.Visible;
        }

        private int IndexFromX(double x)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                double d = Math.Abs(XFor(i) - x);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private void Plot_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(Plot);
            if (p.Y < _top - 6 || p.Y > Plot.ActualHeight - _bottom + 4) return;
            _dragIndex = IndexFromX(p.X);
            Apply(p);
            Plot.CaptureMouse();
        }

        private void Plot_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            Apply(e.GetPosition(Plot));
        }

        private void Plot_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragIndex >= 0)
            {
                _dragIndex = -1;
                Plot.ReleaseMouseCapture();
            }
        }

        private void Apply(Point p)
        {
            if (_dragIndex < 0) return;
            double h = Math.Max(10, Plot.ActualHeight - _top - _bottom);
            double v = (1 - (p.Y - _top) / h) * 100.0;
            v = Math.Round(Math.Max(0, Math.Min(100, v)) / 5.0) * 5;   // 5% 步进
            if ((int)v == Curve.Percent[_dragIndex]) return;
            Curve.Percent[_dragIndex] = (int)v;
            Render();
            Changed?.Invoke();
        }

        public void Refresh() => Render();
    }
}

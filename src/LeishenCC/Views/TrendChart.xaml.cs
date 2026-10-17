using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LeishenCC.Core;

namespace LeishenCC.Views
{
    /// <summary>
    /// 时间序列折线图。左轴画温度/占用（0-100），右轴画转速（自适应 RPM），
    /// 这样混选时也不会把温度压成一条直线。绘制方式沿用 CurveEditor 的自绘 Canvas。
    /// </summary>
    public partial class TrendChart : UserControl
    {
        private sealed class Series
        {
            public string Name;
            public string Unit;
            public string Color;
            public bool IsRpm;
            public double[] Values;
            public double Latest;
        }

        private readonly List<Series> _series = new List<Series>();
        private DateTime[] _times = new DateTime[0];
        private readonly double _left = 40, _right = 44, _top = 10, _bottom = 20;

        public TrendChart()
        {
            InitializeComponent();
            SizeChanged += (s, e) => Render();
            Loaded += (s, e) => Render();
        }

        /// <summary>按给定的指标集合刷新图表。</summary>
        public void Update(SampleHistory history, IEnumerable<Metric> metrics, int windowSeconds)
        {
            _series.Clear();
            DateTime[] times = null;
            bool first = true;

            foreach (var m in metrics)
            {
                var vals = history.Values(m, windowSeconds, out times);
                if (first) { _times = times; first = false; }
                _series.Add(new Series
                {
                    Name = SampleHistory.Meta.Names[(int)m],
                    Unit = SampleHistory.Meta.Units[(int)m],
                    Color = SampleHistory.Meta.Colors[(int)m],
                    IsRpm = SampleHistory.Meta.IsRpm[(int)m],
                    Values = vals,
                    Latest = vals.Length > 0 ? vals[vals.Length - 1] : 0
                });
            }
            if (_series.Count == 0) _times = new DateTime[0];
            Render();
        }

        private Brush BrushOf(string key) => (Brush)FindResource(key);

        private void Render()
        {
            Plot.Children.Clear();
            Legend.Children.Clear();
            if (Plot.ActualWidth < 80)
            {
                Plot.Children.Add(new TextBlock
                {
                    Text = "正在采集…",
                    FontSize = 12,
                    Foreground = BrushOf("TextMuted"),
                    Margin = new Thickness(_left, 80, 0, 0)
                });
                return;
            }

            double w = Plot.ActualWidth, h = Plot.ActualHeight;
            double plotW = Math.Max(10, w - _left - _right);
            double plotH = Math.Max(10, h - _top - _bottom);

            var grid = BrushOf("ChartGrid");
            var muted = BrushOf("TextMuted");
            var numFont = (FontFamily)FindResource("NumFont");

            bool hasRpm = _series.Any(s => s.IsRpm);
            bool hasPct = _series.Any(s => !s.IsRpm);
            double rpmMax = 1000;
            if (hasRpm)
            {
                double mx = _series.Where(s => s.IsRpm).SelectMany(s => s.Values).DefaultIfEmpty(0).Max();
                rpmMax = Math.Max(1000, Math.Ceiling(mx / 500.0) * 500.0);
            }

            // 横向网格 + 左轴刻度（0/25/50/75/100）
            for (int i = 0; i <= 4; i++)
            {
                double y = _top + plotH * i / 4.0;
                Plot.Children.Add(new Line
                {
                    X1 = _left, X2 = _left + plotW, Y1 = y, Y2 = y,
                    Stroke = grid, StrokeThickness = 1
                });
                var lbl = new TextBlock
                {
                    Text = (100 - i * 25).ToString(CultureInfo.InvariantCulture),
                    FontSize = 10, Foreground = muted, FontFamily = numFont
                };
                Canvas.SetLeft(lbl, 6);
                Canvas.SetTop(lbl, y - 7);
                Plot.Children.Add(lbl);
            }

            // 右轴（转速）
            if (hasRpm)
            {
                for (int i = 0; i <= 4; i++)
                {
                    double y = _top + plotH * i / 4.0;
                    var lbl = new TextBlock
                    {
                        Text = ((int)(rpmMax * (4 - i) / 4.0)).ToString(CultureInfo.InvariantCulture),
                        FontSize = 10, Foreground = muted, FontFamily = numFont
                    };
                    Canvas.SetLeft(lbl, _left + plotW + 6);
                    Canvas.SetTop(lbl, y - 7);
                    Plot.Children.Add(lbl);
                }
            }

            // 纵向网格 + 时间标签
            int n = _times.Length;
            for (int i = 0; i <= 4; i++)
            {
                double x = _left + plotW * i / 4.0;
                Plot.Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = _top, Y2 = _top + plotH,
                    Stroke = grid, StrokeThickness = 1
                });
                if (n > 1)
                {
                    int idx = (int)Math.Round((n - 1) * i / 4.0);
                    var lbl = new TextBlock
                    {
                        Text = _times[idx].ToString("HH:mm:ss"),
                        FontSize = 10, Foreground = muted, FontFamily = numFont
                    };
                    lbl.Measure(new Size(120, 40));
                    double lx = x - lbl.DesiredSize.Width / 2;
                    Canvas.SetLeft(lbl, Math.Max(0, Math.Min(w - lbl.DesiredSize.Width, lx)));
                    Canvas.SetTop(lbl, _top + plotH + 4);
                    Plot.Children.Add(lbl);
                }
            }

            if (_series.Count == 0 || n == 0)
            {
                Plot.Children.Add(new TextBlock
                {
                    Text = "尚未选择指标",
                    FontSize = 12, Foreground = muted,
                    Margin = new Thickness(_left + 8, plotH / 2, 0, 0)
                });
                return;
            }

            // 折线
            foreach (var s in _series)
            {
                var pts = new PointCollection();
                for (int i = 0; i < s.Values.Length; i++)
                {
                    double x = _left + (n <= 1 ? 0 : plotW * i / (double)(n - 1));
                    double v = s.IsRpm ? s.Values[i] / rpmMax * 100.0 : s.Values[i];
                    v = Math.Max(0, Math.Min(100, v));
                    double y = _top + (1 - v / 100.0) * plotH;
                    pts.Add(new Point(x, y));
                }
                var color = (Color)ColorConverter.ConvertFromString(s.Color);
                var brush = new SolidColorBrush(color);
                Plot.Children.Add(new Polyline
                {
                    Points = pts,
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeLineJoin = PenLineJoin.Round
                });
                if (pts.Count > 0)
                {
                    var last = pts[pts.Count - 1];
                    Plot.Children.Add(new Ellipse
                    {
                        Width = 7, Height = 7, Fill = brush,
                        Stroke = BrushOf("CardFill"), StrokeThickness = 1
                    });
                    var dot = Plot.Children[Plot.Children.Count - 1];
                    Canvas.SetLeft(dot, last.X - 3.5);
                    Canvas.SetTop(dot, last.Y - 3.5);
                }
            }

            // 图例
            foreach (var s in _series)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
                panel.Children.Add(new Rectangle
                {
                    Width = 10, Height = 10, RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Color)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                panel.Children.Add(new TextBlock
                {
                    Text = string.Format("{0} {1:0.#}{2}{3}",
                        s.Name, s.Latest, s.Unit, s.IsRpm ? "（右轴）" : "（左轴）"),
                    FontSize = 11,
                    Foreground = BrushOf("TextSecondary"),
                    Margin = new Thickness(6, 0, 0, 0)
                });
                Legend.Children.Add(panel);
            }

            if (hasRpm && hasPct)
            {
                Legend.Children.Add(new TextBlock
                {
                    Text = "提示：温度为左轴，转速为右轴",
                    FontSize = 11,
                    Foreground = BrushOf("TextMuted"),
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
        }
    }
}

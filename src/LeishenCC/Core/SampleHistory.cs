using System;

namespace LeishenCC.Core
{
    /// <summary>可绘制的历史指标。</summary>
    public enum Metric
    {
        CpuTemp = 0,
        GpuTemp,
        SysTemp,
        CpuLoad,
        GpuLoad,
        RamLoad,
        CpuFan,
        GpuFan,
        SysFan
    }

    /// <summary>
    /// 采样历史：环形缓冲，默认 1Hz × 600 点 = 10 分钟。
    /// 参考原厂 refresh_info 的周期刷新思路，这里只做数据留存，绘制交给 TrendChart。
    /// </summary>
    public sealed class SampleHistory
    {
        public const int Capacity = 600;

        private readonly double[][] _buf = new double[9][];
        private readonly DateTime[] _time = new DateTime[Capacity];
        private int _head;      // 下一个写入位置
        private int _count;     // 已写入数量（上限 Capacity）

        public int Count => _count;

        public SampleHistory()
        {
            for (int i = 0; i < 9; i++) _buf[i] = new double[Capacity];
        }

        public void Add(DateTime t, double[] values)
        {
            for (int i = 0; i < 9; i++)
                _buf[i][_head] = i < values.Length ? values[i] : 0;
            _time[_head] = t;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>取最近 take 个样本，按时间从旧到新排列。</summary>
        public double[] Values(Metric m, int take, out DateTime[] times)
        {
            int n = Math.Min(take, _count);
            var vals = new double[n];
            times = new DateTime[n];
            if (n == 0) return vals;
            int start = (_head - n + Capacity) % Capacity;
            var src = _buf[(int)m];
            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % Capacity;
                vals[i] = src[idx];
                times[i] = _time[idx];
            }
            return vals;
        }

        /// <summary>最新一个样本值。</summary>
        public double Latest(Metric m)
        {
            if (_count == 0) return 0;
            int idx = (_head - 1 + Capacity) % Capacity;
            return _buf[(int)m][idx];
        }

        public static class Meta
        {
            public static readonly string[] Names =
            {
                "CPU 温度", "GPU 温度", "SYS 温度",
                "CPU 占用", "GPU 占用", "内存占用",
                "CPU 转速", "GPU 转速", "SYS 转速"
            };

            public static readonly string[] Units =
            {
                "°C", "°C", "°C",
                "%", "%", "%",
                "RPM", "RPM", "RPM"
            };

            /// <summary>温度/占用是 0-100，转速按实际 RPM 自适应。</summary>
            public static readonly bool[] IsRpm =
            {
                false, false, false,
                false, false, false,
                true, true, true
            };

            /// <summary>固定色板，深浅主题下都可读。</summary>
            public static readonly string[] Colors =
            {
                "#FF4CC2FF", "#FFFF7B7B", "#FFFCE100",
                "#FF6CCB5F", "#FFC77DFF", "#FF00D0B0",
                "#FFFF9F43", "#FFFF6FB5", "#FF9BD0FF"
            };

            /// <summary>预设组合，避免把温度（0-100）和转速（几千）画在一条轴上。</summary>
            public static readonly Metric[] GroupTemp = { Metric.CpuTemp, Metric.GpuTemp, Metric.SysTemp };
            public static readonly Metric[] GroupLoad = { Metric.CpuLoad, Metric.GpuLoad, Metric.RamLoad };
            public static readonly Metric[] GroupFan = { Metric.CpuFan, Metric.GpuFan, Metric.SysFan };
        }
    }
}

using System;
using System.Globalization;
using System.Linq;

namespace LeishenCC.Core
{
    /// <summary>
    /// 风扇曲线：8 个温度断点 30/40/.../100℃，每个断点对应 0-100% 占空比。
    /// 原厂注册表里就是 "30,30,30,30,50,60,70,100" 这种逗号分隔串，双风扇用 ';' 分 CPU/GPU。
    /// </summary>
    public sealed class FanCurve
    {
        public static readonly int[] Temps = { 30, 40, 50, 60, 70, 80, 90, 100 };

        public int[] Percent = new int[8];

        public FanCurve()
        {
            for (int i = 0; i < 8; i++) Percent[i] = 30 + i * 10;
        }

        public static FanCurve Parse(string s, FanCurve fallback = null)
        {
            var c = new FanCurve();
            if (string.IsNullOrWhiteSpace(s))
            {
                if (fallback != null) Array.Copy(fallback.Percent, c.Percent, 8);
                return c;
            }
            var parts = s.Split(',');
            for (int i = 0; i < 8; i++)
            {
                int v = c.Percent[i];
                if (i < parts.Length && int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                { }
                c.Percent[i] = Math.Max(0, Math.Min(100, v));
            }
            return c;
        }

        public override string ToString()
        {
            return string.Join(",", Percent.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        }

        public FanCurve Clone()
        {
            var c = new FanCurve();
            Array.Copy(Percent, c.Percent, 8);
            return c;
        }

        /// <summary>按原厂逻辑取档：温度&lt;30 视作 30；落在 [T, T+10) 区间取该档。</summary>
        public int IndexFor(int tempC)
        {
            if (tempC < 30) tempC = 30;
            for (int i = 0; i < 8; i++)
            {
                if (tempC >= Temps[i] && tempC - Temps[i] < 10) return i;
            }
            return 7;
        }

        public int PercentFor(int tempC) => Percent[IndexFor(tempC)];

        public static (FanCurve Cpu, FanCurve Gpu) ParsePair(string s)
        {
            var parts = (s ?? "").Split(';');
            var cpu = Parse(parts.Length > 0 ? parts[0] : null);
            var gpu = Parse(parts.Length > 1 ? parts[1] : parts.Length > 0 ? parts[0] : null);
            return (cpu, gpu);
        }

        public string FormatPair(FanCurve gpu) => ToString() + ";" + gpu;
    }
}

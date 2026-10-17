using System;
using System.Collections.Generic;

namespace LeishenCC.Core
{
    /// <summary>
    /// LED 数据位域：bit31..28 模式，bit27..24 亮度(0-15)，bit23..16 R，bit15..8 G，bit7..0 B。
    /// 逆向自 MY_DATA_LED。
    /// </summary>
    public sealed class LedData
    {
        public uint Raw;

        public LedData(uint raw = 0) { Raw = raw; }

        public int Mode
        {
            get { return (int)((Raw >> 28) & 0xF); }
            set { Raw = (Raw & 0x0FFFFFFFu) | ((uint)(value & 0xF) << 28); }
        }

        public int Brightness
        {
            get { return (int)((Raw >> 24) & 0xF); }
            set { Raw = (Raw & 0xF0FFFFFFu) | ((uint)(value & 0xF) << 24); }
        }

        public byte R
        {
            get { return (byte)((Raw >> 16) & 0xFF); }
            set { Raw = (Raw & 0xFF00FFFFu) | ((uint)value << 16); }
        }

        public byte G
        {
            get { return (byte)((Raw >> 8) & 0xFF); }
            set { Raw = (Raw & 0xFFFF00FFu) | ((uint)value << 8); }
        }

        public byte B
        {
            get { return (byte)(Raw & 0xFF); }
            set { Raw = (Raw & 0xFFFFFF00u) | value; }
        }

        public string Hex => Raw.ToString("X8");

        public static LedData Parse(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return new LedData();
            uint v;
            if (!uint.TryParse(hex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out v))
                return new LedData();
            return new LedData(v);
        }

        public static class Modes
        {
            public const int Off = 0;
            public const int Static = 1;
            public const int Breathing = 3;
            public const int ColorCycle = 6;
            public const int Ambilight = 7;

            public static readonly Dictionary<int, string> Names = new Dictionary<int, string>
            {
                { 0, "关闭" },
                { 1, "常亮" },
                { 3, "呼吸" },
                { 6, "彩色循环" },
                { 7, "氛围灯" }
            };
        }

        /// <summary>五个灯区：LED1;LED2;LED3;LOGO;TRUNK（与原厂字符串顺序一致）</summary>
        public static readonly (int Zone, string Name)[] Zones =
        {
            (5, "键盘区 1"),
            (4, "键盘区 2"),
            (3, "键盘区 3"),
            (8, "LOGO 灯"),
            (7, "尾部灯带"),
        };

        public static LedData[] ParseGroup(string s)
        {
            var arr = new LedData[5];
            for (int i = 0; i < 5; i++) arr[i] = new LedData();
            if (string.IsNullOrWhiteSpace(s)) return arr;
            var parts = s.Split(';');
            for (int i = 0; i < Math.Min(5, parts.Length); i++) arr[i] = Parse(parts[i]);
            return arr;
        }

        public static string Join(LedData[] arr)
        {
            var parts = new List<string>();
            for (int i = 0; i < 5; i++) parts.Add((arr[i] ?? new LedData()).Hex);
            return string.Join(";", parts);
        }
    }
}

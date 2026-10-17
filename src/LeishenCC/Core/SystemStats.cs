using System;
using System.Management;
using System.Runtime.InteropServices;

namespace LeishenCC.Core
{
    /// <summary>
    /// CPU 占用 / 内存占用 / CPU 当前频率。用 GetSystemTimes + GlobalMemoryStatusEx，不依赖性能计数器。
    /// </summary>
    public static class SystemStats
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { public uint Low; public uint High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        private static ulong _prevIdle, _prevKernel, _prevUser;
        private static bool _primed;

        public static double CpuPercent()
        {
            FileTime idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return 0;
            ulong i = ((ulong)idle.High << 32) | idle.Low;
            ulong k = ((ulong)kernel.High << 32) | kernel.Low;
            ulong u = ((ulong)user.High << 32) | user.Low;

            if (!_primed)
            {
                _prevIdle = i; _prevKernel = k; _prevUser = u; _primed = true;
                return 0;
            }
            ulong di = i - _prevIdle;
            ulong dk = k - _prevKernel;
            ulong du = u - _prevUser;
            _prevIdle = i; _prevKernel = k; _prevUser = u;

            ulong total = dk + du;      // kernel 已含 idle
            ulong busy = total - di;
            if (total == 0) return 0;
            return Math.Round(busy * 100.0 / total, 0);
        }

        public static void Memory(out double percent, out ulong usedMb, out ulong totalMb)
        {
            percent = 0; usedMb = 0; totalMb = 0;
            var m = new MemoryStatusEx();
            m.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref m)) return;
            totalMb = m.TotalPhys / 1048576;
            usedMb = (m.TotalPhys - m.AvailPhys) / 1048576;
            percent = m.MemoryLoad;
        }

        public static uint CpuCurrentMhz()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col) return Convert.ToUInt32(o["CurrentClockSpeed"]);
                }
            }
            catch { }
            return 0;
        }

        public static uint CpuMaxMhz()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col) return Convert.ToUInt32(o["MaxClockSpeed"]);
                }
            }
            catch { }
            return 0;
        }

        public static string CpuName()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col) return Convert.ToString(o["Name"]).Trim();
                }
            }
            catch { }
            return "CPU";
        }

        public static string GpuName()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col)
                    {
                        var n = Convert.ToString(o["Name"]);
                        if (!string.IsNullOrEmpty(n) && n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return n;
                    }
                }
            }
            catch { }
            return "GPU";
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace LeishenCC.Core
{
    /// <summary>
    /// Windows 电源计划切换。优先用原厂那三个方案 GUID，不存在则退回系统自带方案。
    /// </summary>
    public static class PowerPlanService
    {
        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        public static readonly Guid HighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        public static readonly Guid VendorHigh = new Guid("a9932169-f1fb-4dff-9034-fe1c4ca1886e");
        public static readonly Guid VendorGame = new Guid("653fbb7f-248b-41d0-aa49-03fabbfd5e8e");
        public static readonly Guid VendorAudio = new Guid("5DA85D85-EB0C-4710-A805-6DA8BBCF1F02");

        private static bool TryActivate(Guid g)
        {
            try { return PowerSetActiveScheme(IntPtr.Zero, ref g) == 0; }
            catch { return false; }
        }

        /// <summary>mode: 0=高性能 1=游戏 2=办公</summary>
        public static bool Apply(int mode)
        {
            switch (mode)
            {
                case 0:
                    return TryActivate(VendorHigh) || TryActivate(HighPerf);
                case 1:
                    return TryActivate(VendorGame) || TryActivate(HighPerf);
                default:
                    return TryActivate(VendorAudio) || TryActivate(Balanced);
            }
        }
    }
}

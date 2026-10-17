using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LeishenCC.Core
{
    /// <summary>
    /// 通过 nvml.dll 读 NVIDIA GPU 状态（温度/占用/频率/显存），等价原厂的 NVGPU 类。
    /// </summary>
    public sealed class Nvml : IDisposable
    {
        private IntPtr _lib;
        private IntPtr _device;
        private bool _ok;

        private delegate int NvmlInit();
        private delegate int NvmlShutdown();
        private delegate int GetCount(out uint count);
        private delegate int GetHandle(uint index, out IntPtr dev);
        private delegate int GetTemp(IntPtr dev, int sensor, out uint temp);
        private delegate int GetClock(IntPtr dev, int type, out uint mhz);
        private delegate int GetUtil(IntPtr dev, out Utilization u);
        private delegate int GetMem(IntPtr dev, out Memory m);

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { public uint Gpu; public uint Memory; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Memory { public ulong Total; public ulong Free; public ulong Used; }

        private GetTemp _getTemp;
        private GetClock _getClock;
        private GetUtil _getUtil;
        private GetMem _getMem;
        private NvmlShutdown _shutdown;

        public bool Available => _ok;

        public static string ResolvePath()
        {
            foreach (var p in new[]
            {
                @"C:\Windows\System32\nvml.dll",
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll",
                @"C:\Program Files (x86)\NVIDIA Corporation\NVSMI\nvml.dll",
            })
            {
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        public bool Init()
        {
            try
            {
                var path = ResolvePath();
                if (path == null) return false;
                _lib = NativeLibrary.Load(path);
                var init = Marshal.GetDelegateForFunctionPointer<NvmlInit>(NativeLibrary.GetExport(_lib, "nvmlInit_v2"));
                var countFn = Marshal.GetDelegateForFunctionPointer<GetCount>(NativeLibrary.GetExport(_lib, "nvmlDeviceGetCount_v2"));
                var handleFn = Marshal.GetDelegateForFunctionPointer<GetHandle>(NativeLibrary.GetExport(_lib, "nvmlDeviceGetHandleByIndex_v2"));
                _getTemp = Marshal.GetDelegateForFunctionPointer<GetTemp>(NativeLibrary.GetExport(_lib, "nvmlDeviceGetTemperature"));
                _getClock = Marshal.GetDelegateForFunctionPointer<GetClock>(NativeLibrary.GetExport(_lib, "nvmlDeviceGetClockInfo"));
                _getUtil = Marshal.GetDelegateForFunctionPointer<GetUtil>(NativeLibrary.GetExport(_lib, "nvmlDeviceGetUtilizationRates"));
                _getMem = Marshal.GetDelegateForFunctionPointer<GetMem>(NativeLibrary.GetExport(_lib, "nvmlDeviceGetMemoryInfo"));
                _shutdown = Marshal.GetDelegateForFunctionPointer<NvmlShutdown>(NativeLibrary.GetExport(_lib, "nvmlShutdown"));

                if (init() != 0) return false;
                uint count;
                if (countFn(out count) != 0 || count == 0) return false;
                if (handleFn(0, out _device) != 0) return false;
                _ok = true;
                return true;
            }
            catch
            {
                _ok = false;
                return false;
            }
        }

        public int Temperature()
        {
            if (!_ok) return 0;
            uint t;
            return _getTemp(_device, 0, out t) == 0 ? (int)t : 0;
        }

        public int Usage()
        {
            if (!_ok) return 0;
            Utilization u;
            return _getUtil(_device, out u) == 0 ? (int)u.Gpu : 0;
        }

        public int CoreClock()
        {
            if (!_ok) return 0;
            uint m;
            return _getClock(_device, 0, out m) == 0 ? (int)m : 0;
        }

        public int MemClock()
        {
            if (!_ok) return 0;
            uint m;
            return _getClock(_device, 2, out m) == 0 ? (int)m : 0;
        }

        public void MemInfo(out double usedMb, out double totalMb)
        {
            usedMb = totalMb = 0;
            if (!_ok) return;
            Memory mi;
            if (_getMem(_device, out mi) == 0)
            {
                usedMb = mi.Used / 1048576.0;
                totalMb = mi.Total / 1048576.0;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_ok && _shutdown != null) _shutdown();
            }
            catch { }
            _ok = false;
            if (_lib != IntPtr.Zero)
            {
                try { NativeLibrary.Free(_lib); } catch { }
                _lib = IntPtr.Zero;
            }
        }
    }
}

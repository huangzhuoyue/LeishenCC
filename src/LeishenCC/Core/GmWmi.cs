using System;
using System.Management;

namespace LeishenCC.Core
{
    /// <summary>
    /// 32 字节 SMI 结构，与 BIOS/EC 侧 RW_GMWMI 交换。
    /// 布局（小端）：a0(2) a1(2) a2(4) a3(4) a4(4) a5(4) a6(4) rev0(4) rev1(4)
    /// </summary>
    public struct Smi
    {
        public ushort a0, a1;
        public uint a2, a3, a4, a5, a6;

        public byte[] ToBytes()
        {
            var b = new byte[32];
            BitConverter.GetBytes(a0).CopyTo(b, 0);
            BitConverter.GetBytes(a1).CopyTo(b, 2);
            BitConverter.GetBytes(a2).CopyTo(b, 4);
            BitConverter.GetBytes(a3).CopyTo(b, 8);
            BitConverter.GetBytes(a4).CopyTo(b, 12);
            BitConverter.GetBytes(a5).CopyTo(b, 16);
            BitConverter.GetBytes(a6).CopyTo(b, 20);
            return b;
        }

        public static Smi FromBytes(byte[] b)
        {
            var s = new Smi();
            if (b == null || b.Length < 32) return s;
            s.a0 = BitConverter.ToUInt16(b, 0);
            s.a1 = BitConverter.ToUInt16(b, 2);
            s.a2 = BitConverter.ToUInt32(b, 4);
            s.a3 = BitConverter.ToUInt32(b, 8);
            s.a4 = BitConverter.ToUInt32(b, 12);
            s.a5 = BitConverter.ToUInt32(b, 16);
            s.a6 = BitConverter.ToUInt32(b, 20);
            return s;
        }

        public override string ToString()
        {
            return string.Format("set/get={0:X4} code={1:X4} | {2:X8} {3:X8} {4:X8} {5:X8} {6:X8}",
                a0, a1, a2, a3, a4, a5, a6);
        }
    }

    /// <summary>
    /// root\wmi:RW_GMWMI 通道。逆向自 ControlCenter 的 MyWmiBase.ACPIPerformSMI。
    /// </summary>
    public sealed class GmWmi
    {
        public const ushort GET = 0xFA00;   // 64000
        public const ushort SET = 0xFB00;   // 64256

        // GET 子命令
        public const ushort GET_LED           = 0x0100;
        public const ushort GET_THERMAL       = 0x0200;
        public const ushort GET_CAPS          = 0x0201;
        public const ushort GET_HW_STATUS     = 0x0202;
        public const ushort GET_DISPLAY       = 0x0203;
        public const ushort GET_HSR           = 0x0204;
        public const ushort GET_FAN_TABLE     = 0x0205;
        public const ushort GET_FAN_CTRL      = 0x0206;
        public const ushort GET_SYS_THERMAL   = 0x0207;

        // SET 子命令
        public const ushort SET_LED           = 0x0100;
        public const ushort SET_WINKEY        = 0x0200;
        public const ushort SET_DISPLAY       = 0x0203;
        public const ushort SET_HSR           = 0x0204;
        public const ushort SET_FAN_SPEED     = 0x0205;
        public const ushort SET_FAN_CTRL      = 0x0206;
        public const ushort SET_POWER_MODE    = 0x0300;
        public const ushort SET_EVENT_CLEAR   = 0x0400;

        private readonly object _lock = new object();
        private ManagementObject _instance;
        public string LastError { get; private set; }
        public bool Connected => _instance != null;

        public bool Connect()
        {
            lock (_lock)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM RW_GMWMI"))
                    using (var col = searcher.Get())
                    {
                        foreach (ManagementObject o in col)
                        {
                            _instance = o;
                            break;
                        }
                    }
                    if (_instance == null) LastError = "找不到 root\\wmi:RW_GMWMI（BIOS 未提供该 WMI 接口）";
                    return _instance != null;
                }
                catch (Exception ex)
                {
                    _instance = null;
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        private byte[] Query()
        {
            using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT BufferBytes FROM RW_GMWMI"))
            using (var col = searcher.Get())
            {
                foreach (ManagementObject o in col)
                {
                    using (o) return (byte[])o["BufferBytes"];
                }
            }
            return null;
        }

        public bool TryInvoke(Smi inBuf, out Smi outBuf)
        {
            outBuf = default(Smi);
            lock (_lock)
            {
                if (_instance == null && !Connect()) return false;
                try
                {
                    var bytes = inBuf.ToBytes();
                    _instance.SetPropertyValue("BufferBytes", bytes);
                    _instance.Put();
                    var res = Query();
                    if (res == null) { LastError = "读取 BufferBytes 失败"; return false; }
                    outBuf = Smi.FromBytes(res);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _instance = null; // 下次重连
                    return false;
                }
            }
        }

        public bool Read(ushort code, uint a2, out Smi res)
        {
            var s = new Smi { a0 = GET, a1 = code, a2 = a2 };
            return TryInvoke(s, out res);
        }

        public bool Read(ushort code, out Smi res)
        {
            return Read(code, 0u, out res);
        }

        public bool Write(ushort code, uint a2, uint a3, uint a4, uint a5, uint a6, out Smi res)
        {
            var s = new Smi { a0 = SET, a1 = code, a2 = a2, a3 = a3, a4 = a4, a5 = a5, a6 = a6 };
            return TryInvoke(s, out res);
        }

        public bool Write(ushort code, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0)
        {
            Smi ignored;
            return Write(code, a2, a3, a4, a5, a6, out ignored);
        }

        // ---------- 高层封装 ----------

        /// <summary>0xFA00/0x0200 + 0x0207：整机温度与转速</summary>
        public bool ReadThermal(out int cpuTemp, out int gpuTemp, out int cpuFan, out int gpuFan, out int sysTemp, out int sysFan)
        {
            cpuTemp = gpuTemp = cpuFan = gpuFan = sysTemp = sysFan = 0;
            Smi r;
            if (!Read(GET_THERMAL, 0, out r)) return false;
            cpuTemp = (int)(r.a2 & 0xFF);
            gpuTemp = (int)(r.a3 & 0xFF);
            cpuFan = (int)(r.a4 & 0xFFFF);
            gpuFan = (int)(r.a5 & 0xFFFF);
            if (Read(GET_SYS_THERMAL, 0, out r))
            {
                sysTemp = (int)(r.a2 & 0xFF);
                sysFan = (int)(r.a3 & 0xFFFF);
            }
            return true;
        }

        /// <summary>0xFA00/0x0206：自定义风扇是否开启</summary>
        public int GetFanControl()
        {
            Smi r;
            return Read(GET_FAN_CTRL, 0, out r) ? (int)r.a2 : -1;
        }

        public bool SetFanControl(bool on)
        {
            return Write(SET_FAN_CTRL, on ? 1u : 0u);
        }

        /// <summary>0xFB00/0x0205：直接给定三路风扇占空比（0-100）</summary>
        public bool SetFanSpeed(int cpu, int gpu, int sys)
        {
            return Write(SET_FAN_SPEED,
                (uint)Math.Max(0, Math.Min(255, cpu)),
                (uint)Math.Max(0, Math.Min(255, gpu)),
                (uint)Math.Max(0, Math.Min(255, sys)));
        }

        /// <summary>0xFB00/0x0300：0=高性能 1=游戏 2=办公/静音</summary>
        public bool SetPowerMode(int mode)
        {
            return Write(SET_POWER_MODE, (uint)mode);
        }

        /// <summary>0xFB00/0x0200：a6=0 启用 Win 键，a6=1 屏蔽</summary>
        public bool SetWinKey(bool enabled)
        {
            return Write(SET_WINKEY, 0, 0, 0, 0, enabled ? 0u : 1u);
        }

        /// <summary>0xFB00/0x0100：单区 LED（zone 3/4/5=分区 6=整个键盘 7=灯带 8=LOGO）</summary>
        public bool SetLedZone(int zone, uint ledData)
        {
            return Write(SET_LED, (uint)zone, ledData);
        }

        /// <summary>0xFB00/0x0203：1=混合 2=独显 3=UMA</summary>
        public bool SetDisplayMode(int mode)
        {
            return Write(SET_DISPLAY, (uint)mode);
        }

        /// <summary>0xFB00/0x0204：1/2 两档刷新率</summary>
        public bool SetHsr(int mode)
        {
            return Write(SET_HSR, (uint)mode);
        }

        /// <summary>
        /// 0xFB00/0x0400：Fn 组合键事件确认（不确认的话 EC 会一直挂着这个事件）。
        /// id 与原厂 SetEventStatus 一致：2=性能模式键 3=控制中心键 4=摄像头键。
        /// </summary>
        public bool ClearEvent(int id)
        {
            return Write(SET_EVENT_CLEAR,
                id == 2 ? 1u : 0u,
                id == 3 ? 1u : 0u,
                id == 4 ? 1u : 0u);
        }
    }
}

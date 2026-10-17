using System;
using System.Management;
using System.Text;

namespace LeishenCC.Core
{
    /// <summary>Fn 组合键事件种类，与 EC 上报的事件位一一对应。</summary>
    public enum FnKeyKind
    {
        Unknown = 0,
        Backlight,      // EventDetail[0]  键盘背光亮度
        Hdmi,           // EventDetail[4]  外接显示
        Adapter,        // EventDetail[5]  电源适配器插入
        Camera,         // EventDetail[6]  摄像头开关
        Performance,    // EventDetail[7]  性能模式键（原厂注释：Switch performance hot key）
        ControlCenter   // EventDetail[8]  控制中心键
    }

    /// <summary>一次 EC 上报的 Fn 键事件（原始 32 字节 EventDetail）。</summary>
    public sealed class FnKeyEvent
    {
        public byte[] Raw;
        public FnKeyKind Kind;

        /// <summary>EventDetail[0]：键盘背光档位</summary>
        public int Brightness { get { return Raw != null && Raw.Length > 0 ? Raw[0] : 0; } }

        /// <summary>EventDetail[7]：性能键原始值，用于判断是 Fn+1/2/3 里的哪一个</summary>
        public int PerfValue { get { return Raw != null && Raw.Length > 7 ? Raw[7] : 0; } }

        /// <summary>EventDetail[8]：控制中心键原始值</summary>
        public int CcValue { get { return Raw != null && Raw.Length > 8 ? Raw[8] : 0; } }

        /// <summary>EventDetail[6]：1=摄像头开 2=摄像头关</summary>
        public int CameraValue { get { return Raw != null && Raw.Length > 6 ? Raw[6] : 0; } }

        public static readonly string[] KindNames =
        {
            "未知", "键盘背光", "外接显示", "电源适配器", "摄像头", "性能模式键", "控制中心键"
        };

        /// <summary>前 9 个字节的十六进制，排查 Fn+1/2/3 具体键值时看这个。</summary>
        public string Hex
        {
            get
            {
                if (Raw == null) return "";
                var sb = new StringBuilder();
                for (int i = 0; i < Math.Min(9, Raw.Length); i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(Raw[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }

        public string Describe()
        {
            string extra = "";
            switch (Kind)
            {
                case FnKeyKind.Backlight: extra = "亮度 " + Brightness; break;
                case FnKeyKind.Camera: extra = CameraValue == 1 ? "开启" : CameraValue == 2 ? "关闭" : "值 " + CameraValue; break;
                case FnKeyKind.Performance: extra = "键值 0x" + PerfValue.ToString("X2"); break;
                case FnKeyKind.ControlCenter: extra = "键值 0x" + CcValue.ToString("X2"); break;
            }
            return KindNames[(int)Kind] + (extra.Length > 0 ? "（" + extra + "）" : "") + "  [" + Hex + "]";
        }
    }

    /// <summary>
    /// 监听 root\wmi 下的 GMC_WMIEvent（原厂 ControlCenterDaemon / MainWindow 都靠它收 Fn 键）。
    /// 事件负载是 32 字节的 EventDetail，解析规则逆向自 ParseWMIEventData。
    /// </summary>
    public sealed class FnKeyWatcher : IDisposable
    {
        private ManagementEventWatcher _watcher;
        private byte _alpha = 0xFF;
        private bool _alphaInit;

        public bool Running { get; private set; }
        public string LastError { get; private set; }
        public event Action<FnKeyEvent> Arrived;

        public bool Start()
        {
            Stop();
            try
            {
                var query = new WqlEventQuery("GMC_WMIEvent");
                var scope = new ManagementScope("root\\wmi");
                _watcher = new ManagementEventWatcher(scope, query);
                _watcher.EventArrived += OnArrived;
                _watcher.Start();
                Running = true;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Running = false;
                return false;
            }
        }

        public void Stop()
        {
            if (_watcher != null)
            {
                try
                {
                    _watcher.EventArrived -= OnArrived;
                    _watcher.Stop();
                    _watcher.Dispose();
                }
                catch { }
                _watcher = null;
            }
            Running = false;
        }

        private void OnArrived(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var data = e.NewEvent["EventDetail"] as byte[];
                if (data == null) return;
                var ev = Parse(data);
                if (ev != null) Arrived?.Invoke(ev);
            }
            catch { }
        }

        /// <summary>按原厂规则解析事件负载。</summary>
        public FnKeyEvent Parse(byte[] b)
        {
            if (b == null || b.Length != 32) return null;

            if (!_alphaInit) { _alpha = b[0]; _alphaInit = true; }

            // 只有亮度变化、其余事件位全 0 → 背光调节
            bool onlyBrightness = true;
            for (int j = 0; j < 5; j++)
            {
                if (b[4 + j] != 0) { onlyBrightness = false; break; }
            }

            var ev = new FnKeyEvent { Raw = b };

            if (onlyBrightness)
            {
                ev.Kind = FnKeyKind.Backlight;
                _alpha = b[0];
                return ev;
            }

            if (b[4] == 1) ev.Kind = FnKeyKind.Hdmi;
            else if (b[5] == 159) ev.Kind = FnKeyKind.Adapter;
            else if (b[6] != 0) ev.Kind = FnKeyKind.Camera;
            else if (b[7] != 0) ev.Kind = FnKeyKind.Performance;
            else if (b[8] != 0) ev.Kind = FnKeyKind.ControlCenter;
            else ev.Kind = FnKeyKind.Unknown;

            if (ev.Kind != FnKeyKind.Unknown && _alpha != b[0])
            {
                // 亮度同时变了，记下来供下次比对
                _alpha = b[0];
            }
            return ev;
        }

        public void Dispose() { Stop(); }
    }
}

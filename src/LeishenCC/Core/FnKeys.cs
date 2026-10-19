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

        /// <summary>界面"模拟按键"按钮造出来的事件，不参与去重/历史统计。</summary>
        public bool Simulated;

        /// <summary>EventDetail[0]：键盘背光档位</summary>
        public int Brightness { get { return Raw != null && Raw.Length > 0 ? Raw[0] : 0; } }

        /// <summary>EventDetail[7]：性能键原始值，用于判断是 Fn+1/2/3 里的哪一个</summary>
        public int PerfValue { get { return Raw != null && Raw.Length > 7 ? Raw[7] : 0; } }

        /// <summary>
        /// EventDetail[1]：性能键的键码。实测 Fn+1=0x07、Fn+3=0x00，
        /// 两者 b[7] 都是 1，只有这个字节能区分。
        /// </summary>
        public int KeyCode { get { return Raw != null && Raw.Length > 1 ? Raw[1] : 0; } }

        /// <summary>EventDetail[8]：控制中心键原始值</summary>
        public int CcValue { get { return Raw != null && Raw.Length > 8 ? Raw[8] : 0; } }

        /// <summary>EventDetail[6]：1=摄像头开 2=摄像头关</summary>
        public int CameraValue { get { return Raw != null && Raw.Length > 6 ? Raw[6] : 0; } }

        public bool HasPerfBit { get { return PerfValue != 0; } }
        public bool HasCcBit { get { return CcValue != 0; } }
        public bool HasCameraBit { get { return CameraValue != 0; } }

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

        /// <summary>完整 32 字节，排查未识别事件时必须看全量。</summary>
        public string FullHex
        {
            get { return Raw == null ? "" : BitConverter.ToString(Raw).Replace("-", " "); }
        }

        public string Describe()
        {
            string extra = "";
            switch (Kind)
            {
                case FnKeyKind.Backlight: extra = "亮度 " + Brightness; break;
                case FnKeyKind.Camera: extra = CameraValue == 1 ? "开启" : CameraValue == 2 ? "关闭" : "值 " + CameraValue; break;
                case FnKeyKind.Performance: extra = "键值 0x" + PerfValue.ToString("X2") + " · 键码 0x" + KeyCode.ToString("X2"); break;
                case FnKeyKind.ControlCenter: extra = "键值 0x" + CcValue.ToString("X2"); break;
            }
            return KindNames[(int)Kind] + (extra.Length > 0 ? "（" + extra + "）" : "") + "  [" + Hex + "]";
        }
    }

    /// <summary>
    /// 监听 root\wmi 下的 GMC_WMIEvent（原厂 ControlCenterDaemon / MainWindow 都靠它收 Fn 键）。
    /// 事件负载是 32 字节的 EventDetail，解析规则逆向自 ParseWMIEventData。
    ///
    /// 与驱动的关系：GMC_WMIEvent 由 BIOS 的 ACPI WMI mapper（PNP0C14 / wmiacpi.sys）提供，
    /// 原厂 ControlCenter64.sys（\Device\CCDRV1）只是 CPU 频率/EC 辅助驱动，且在本机因签名校验
    /// 无法加载（系统日志 7000），原厂控制中心在驱动未加载时 Fn 键依然工作 —— 所以这里只依赖
    /// root\wmi，不依赖该驱动。Ghidra 侧证据见 analysis/ghidra-driver-report.md。
    /// </summary>
    public sealed class FnKeyWatcher : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _className;
        private volatile ManagementEventWatcher _watcher;

        /// <summary>
        /// 默认订阅 BIOS 提供的 `GMC_WMIEvent`。本机还装有厂商 Hotkey OSD 的 `WMI2Event`
        /// （C:\Program Files\Hotkey OSD\WMI\uw3.mof，同一 ACPI WMI 事件源），
        /// 诊断时可用另一个实例对比两个类名是否都能收到同一按键。
        /// </summary>
        public FnKeyWatcher(string className = "GMC_WMIEvent")
        {
            _className = string.IsNullOrEmpty(className) ? "GMC_WMIEvent" : className;
        }

        public string ClassName { get { return _className; } }

        public bool Running { get; private set; }
        public string LastError { get; private set; }

        /// <summary>成功订阅次数（诊断用：自动重启后能看到计数增长）。</summary>
        public int StartCount { get; private set; }
        public DateTime StartedAt { get; private set; }

        /// <summary>事件到达（已在 WMI 线程）。</summary>
        public event Action<FnKeyEvent> Arrived;

        /// <summary>订阅意外中断（WMI 服务重启、查询被吊销等）。参数是原因。</summary>
        public event Action<string> Faulted;

        public bool Start()
        {
            lock (_sync)
            {
                StopLocked();
                try
                {
                    var scope = new ManagementScope("root\\wmi");
                    var query = new WqlEventQuery("SELECT * FROM " + _className);
                    var watcher = new ManagementEventWatcher(scope, query);
                    watcher.EventArrived += OnArrived;
                    watcher.Stopped += OnStopped;
                    watcher.Start();

                    _watcher = watcher;
                    Running = true;
                    LastError = null;
                    StartCount++;
                    StartedAt = DateTime.Now;
                    return true;
                }
                catch (Exception ex)
                {
                    _watcher = null;
                    Running = false;
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_sync) { StopLocked(); }
        }

        private void StopLocked()
        {
            var watcher = _watcher;
            _watcher = null;
            Running = false;
            if (watcher != null)
            {
                try
                {
                    watcher.EventArrived -= OnArrived;
                    watcher.Stopped -= OnStopped;
                    watcher.Stop();
                    watcher.Dispose();
                }
                catch { }
            }
        }

        /// <summary>WMI 订阅被系统停掉时触发（显式 Stop 不算）。</summary>
        private void OnStopped(object sender, StoppedEventArgs e)
        {
            // 注意：这里不能拿 _sync —— Start/Stop 在持有 _sync 时 Dispose watcher，
            // 若 Dispose 等待本回调完成就会死锁。_watcher 是 volatile，直接比较即可。
            if (!ReferenceEquals(sender, _watcher)) return;
            Running = false;
            LastError = "WMI 事件订阅被终止（status=" + e.Status + "）";
            Faulted?.Invoke(LastError);
        }

        private void OnArrived(object sender, EventArrivedEventArgs e)
        {
            if (!ReferenceEquals(sender, _watcher)) return;
            try
            {
                var data = e.NewEvent["EventDetail"] as byte[];
                if (data == null)
                {
                    Hub.Write("Fn 事件：EventDetail 不是 byte[]，忽略");
                    return;
                }
                var ev = Parse(data);
                if (ev == null)
                {
                    // 原厂只认 32 字节；长度不对说明是别的事件或固件变了，必须留痕，否则界面上一片空白无法排查
                    Hub.Write("Fn 事件长度异常(" + data.Length + "字节): " + BitConverter.ToString(data));
                    return;
                }
                Arrived?.Invoke(ev);
            }
            catch (Exception ex)
            {
                Hub.Write("Fn 事件解析异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 按原厂规则解析事件负载。
        ///
        /// 原厂还有一个 `Event_Alpha` 门控（负载里的背光值必须等于缓存值才处理按键动作），
        /// 它的真实作用是"背光快照一致性校验"，不是去重；副作用是 daemon 版会吞掉启动后
        /// 第一条非背光事件（CC 版不会）。本实现有意不复刻这个吞事件的副作用：
        /// 背光值作为 `FnKeyService.IsEcho` 的快照新鲜度判据使用（b[0] 不同 = 新快照）。
        /// </summary>
        public FnKeyEvent Parse(byte[] b)
        {
            if (b == null || b.Length != 32) return null;

            // 只有亮度变化、其余事件位全 0 → 背光调节
            bool onlyBrightness = true;
            for (int j = 0; j < 5; j++)
            {
                if (b[4 + j] != 0) { onlyBrightness = false; break; }
            }

            var ev = new FnKeyEvent { Raw = b };

            // 注意：全 0 负载是合法事件（Fn+2 把键盘背光调到 0），不能过滤，
            // 否则背光键关不掉灯。实测每次按键 EC 会连发两条相同负载（间隔约 26ms），
            // 幂等处理即可，不用去重。
            if (onlyBrightness)
            {
                ev.Kind = FnKeyKind.Backlight;
                return ev;
            }

            if (b[4] == 1) ev.Kind = FnKeyKind.Hdmi;
            else if (b[5] == 159) ev.Kind = FnKeyKind.Adapter;
            else if (b[6] != 0) ev.Kind = FnKeyKind.Camera;
            else if (b[7] != 0) ev.Kind = FnKeyKind.Performance;
            else if (b[8] != 0) ev.Kind = FnKeyKind.ControlCenter;
            else ev.Kind = FnKeyKind.Unknown;
            return ev;
        }

        public void Dispose() { Stop(); }
    }

    /// <summary>
    /// 厂商 Hotkey OSD 的 `root\wmi:WMI2Event` 事件（标准 ACPI WMI 事件 GUID
    /// {ABBC0F20-8EA1-11d1-00A0-C90629100000}），与本机 BIOS 私有的 `GMC_WMIEvent`
    /// （GUID {74286D6E-429C-427a-B34B-B5D15D032B05}）是**两路独立事件流**，互不覆盖。
    ///
    /// 编码（逆向 C:\Program Files\Hotkey OSD\HkeyOSDSvis.exe 的 WMI2EventSink::Indicate）：
    ///   EventDetail 固定 32 字节；b[0] = 事件 ID；数据位置按事件 ID 查表（不是固定的 b[1]）。
    ///   有效事件 ID：0x0D/0x30/0x33/0x34/0x35/0x39；0xFF 视为无效，其余忽略。
    ///   OSD 只订阅、不通过 SMI 清事件（WMI2Event 流不需要 0x0400 确认）。
    /// 事件 ID 与具体热键（风扇/触摸板/FnLock/麦克风/使用模式）的对应关系仍在实机标注中，
    /// 所以这里只做解码与记录，不执行硬件动作。
    /// </summary>
    public sealed class Wmi2Event
    {
        /// <summary>eventID → EventDetail 里的数据字节下标（来自 HkeyOSDSvis 静态 map）。</summary>
        public static readonly System.Collections.Generic.Dictionary<int, int> DataIndex =
            new System.Collections.Generic.Dictionary<int, int>
            {
                { 0x01, 0x0D }, { 0x02, 0x0C }, { 0x03, 0x1C }, { 0x04, 0x0B },
                { 0x05, 0x0B }, { 0x0D, 0x1D }, { 0x1B, 0x1F }, { 0x30, 0x1E },
                { 0x31, 0x1F }, { 0x32, 0x17 }, { 0x33, 0x1B }, { 0x35, 0x1C },
                { 0x39, 0x19 }, { 0x3A, 0x0A }
            };

        /// <summary>OSD 实际处理的事件 ID（switch 分支）。</summary>
        public static readonly int[] HandledIds = { 0x0D, 0x30, 0x33, 0x34, 0x35, 0x39 };

        public byte[] Raw;
        public int EventId;
        public int DataIdx = -1;
        public int Data = -1;

        public static Wmi2Event Parse(byte[] b)
        {
            if (b == null || b.Length != 32) return null;
            var e = new Wmi2Event { Raw = b, EventId = b[0] };
            int idx;
            if (DataIndex.TryGetValue(e.EventId, out idx))
            {
                e.DataIdx = idx;
                e.Data = b[idx];
            }
            return e;
        }

        public bool IsHandled
        {
            get { return Array.IndexOf(HandledIds, EventId) >= 0; }
        }

        public string Describe()
        {
            string data = DataIdx >= 0 ? "data[b[" + DataIdx + "]]=0x" + Data.ToString("X2") + " (" + Data + ")" : "无数据参数";
            return "OSD事件 0x" + EventId.ToString("X2") + " · " + data +
                   (IsHandled ? "" : "（OSD 不处理）") + "  [" + BitConverter.ToString(Raw, 0, Math.Min(16, Raw.Length)) + "…]";
        }
    }

    /// <summary>订阅 `root\wmi:WMI2Event` 的轻量 watcher（只读，不发任何 SMI）。</summary>
    public sealed class Wmi2Watcher : IDisposable
    {
        private readonly object _sync = new object();
        private volatile ManagementEventWatcher _watcher;

        public bool Running { get; private set; }
        public string LastError { get; private set; }
        public event Action<Wmi2Event> Arrived;
        public event Action<string> Faulted;

        public bool Start()
        {
            lock (_sync)
            {
                StopLocked();
                try
                {
                    var scope = new ManagementScope("root\\wmi");
                    var query = new WqlEventQuery("SELECT * FROM WMI2Event");
                    var watcher = new ManagementEventWatcher(scope, query);
                    watcher.EventArrived += OnArrived;
                    watcher.Stopped += OnStopped;
                    watcher.Start();
                    _watcher = watcher;
                    Running = true;
                    LastError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    _watcher = null;
                    Running = false;
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_sync) { StopLocked(); }
        }

        private void StopLocked()
        {
            var watcher = _watcher;
            _watcher = null;
            Running = false;
            if (watcher != null)
            {
                try
                {
                    watcher.EventArrived -= OnArrived;
                    watcher.Stopped -= OnStopped;
                    watcher.Stop();
                    watcher.Dispose();
                }
                catch { }
            }
        }

        private void OnStopped(object sender, StoppedEventArgs e)
        {
            if (!ReferenceEquals(sender, _watcher)) return;
            Running = false;
            LastError = "WMI2Event 订阅被终止（status=" + e.Status + "）";
            Faulted?.Invoke(LastError);
        }

        private void OnArrived(object sender, EventArrivedEventArgs e)
        {
            if (!ReferenceEquals(sender, _watcher)) return;
            try
            {
                var data = e.NewEvent["EventDetail"] as byte[];
                if (data == null)
                {
                    Hub.Write("WMI2 事件：EventDetail 不是 byte[]，忽略");
                    return;
                }
                var ev = Wmi2Event.Parse(data);
                if (ev == null)
                {
                    Hub.Write("WMI2 事件长度异常(" + data.Length + "字节): " + BitConverter.ToString(data));
                    return;
                }
                // 不在 OSD 处理表里的事件 ID 也交给上层记录：Fn+3 在本机至今没抓到，
                // 保留全量事件才能判断它到底从哪个 ID 来（是否 OSD 不处理的那几个）。
                Arrived?.Invoke(ev);
            }
            catch (Exception ex)
            {
                Hub.Write("WMI2 事件解析异常: " + ex.Message);
            }
        }

        public void Dispose() { Stop(); }
    }
}

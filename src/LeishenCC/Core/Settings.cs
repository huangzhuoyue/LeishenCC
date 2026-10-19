using System;
using Microsoft.Win32;

namespace LeishenCC.Core
{
    /// <summary>
    /// 与原厂共用同一份注册表配置，保证两个程序互不打架：
    /// HKLM\SOFTWARE\WOW6432Node\LeiShen\ControlCenter
    /// </summary>
    public static class Settings
    {
        public const string VendorKey = @"SOFTWARE\WOW6432Node\LeiShen\ControlCenter";
        private const string OwnKey = @"SOFTWARE\LeishenCC";

        private static RegistryKey Open(bool writable)
        {
            try
            {
                var k = writable
                    ? Registry.LocalMachine.CreateSubKey(VendorKey)
                    : Registry.LocalMachine.OpenSubKey(VendorKey);
                return k;
            }
            catch { return null; }
        }

        public static uint GetUInt(string name, uint def = 0)
        {
            using (var k = Open(false))
            {
                if (k == null) return def;
                var v = k.GetValue(name);
                if (v == null) return def;
                try { return Convert.ToUInt32(v); } catch { return def; }
            }
        }

        public static void SetUInt(string name, uint value)
        {
            using (var k = Open(true))
            {
                if (k == null) return;
                k.SetValue(name, value, RegistryValueKind.DWord);
            }
        }

        public static string GetStr(string name, string def = "")
        {
            using (var k = Open(false))
            {
                if (k == null) return def;
                var v = k.GetValue(name);
                return v == null ? def : Convert.ToString(v);
            }
        }

        public static void SetStr(string name, string value)
        {
            using (var k = Open(true))
            {
                if (k == null) return;
                k.SetValue(name, value, RegistryValueKind.String);
            }
        }

        // LED 模式档案（0=游戏 1=高性能 2=办公 3=自定义），名字与原厂一致
        public static readonly string[] LedProfileNames = { "Game", "HighPerformance", "PowerSaving", "Custom" };

        public static string GetLedProfile(int index)
        {
            index = Math.Max(0, Math.Min(3, index));
            return GetStr(LedProfileNames[index], "1100FFFF;1100FFFF;1100FFFF;1100FFFF;1100FFFF");
        }

        public static void SetLedProfile(int index, string data)
        {
            index = Math.Max(0, Math.Min(3, index));
            SetStr(LedProfileNames[index], data);
        }

        public static int LedModule
        {
            get { return (int)GetUInt("LEDModule", 0); }
            set { SetUInt("LEDModule", (uint)Math.Max(0, Math.Min(3, value))); }
        }

        /// <summary>注册表 WinKey 与原厂一致：0=Win 键可用，1=Win 键被屏蔽</summary>
        public static bool WinKeyEnabled
        {
            get { return GetUInt("WinKey", 0) == 0; }
            set { SetUInt("WinKey", value ? 0u : 1u); }
        }

        /// <summary>界面语义：true = 已屏蔽 Win 键（注册表值 1）。开关就是这个方向。</summary>
        public static bool WinKeyBlocked
        {
            get { return GetUInt("WinKey", 0) == 1; }
            set { SetUInt("WinKey", value ? 1u : 0u); }
        }

        public static bool FanControlEnabled
        {
            get { return GetUInt("FanControlStatus", 0) == 1; }
            set { SetUInt("FanControlStatus", value ? 1u : 0u); }
        }

        /// <summary>
        /// 性能模式 0=高性能 1=游戏 2=办公。
        ///
        /// 原厂把当前模式存在 `ModeBeforeDC`：守护进程的 `REG_MAIN.POWERPLAN`(=11) 在
        /// `GetRegistry(byte)` 的 switch 里就映射成这个名字，Fn 性能键切换时也写它。
        /// 早期版本只写本程序私有的 `HKCU\SOFTWARE\LeishenCC\PowerMode`，与原厂/EC 不同源，
        /// 循环切换时会算出「等于当前模式」的下一档，表现为按 Fn+1 毫无变化。
        /// 现在以原厂键为准，私有键只作兼容回退。
        /// </summary>
        public static int PowerMode
        {
            get
            {
                int v;
                if (TryGetUInt("ModeBeforeDC", out v) && v >= 0 && v <= 2) return v;

                using (var k = Registry.CurrentUser.OpenSubKey(OwnKey))
                {
                    if (k != null && k.GetValue("PowerMode") != null)
                        return Math.Max(0, Math.Min(2, Convert.ToInt32(k.GetValue("PowerMode", 1))));
                }
                return 1;
            }
            set
            {
                int m = Math.Max(0, Math.Min(2, value));
                SetUInt("ModeBeforeDC", (uint)m);       // 与原厂共用，两个程序看到的模式一致
                using (var k = Registry.CurrentUser.CreateSubKey(OwnKey))
                {
                    if (k != null) k.SetValue("PowerMode", m, RegistryValueKind.DWord);   // 兼容旧版
                }
            }
        }

        private static bool TryGetUInt(string name, out int value)
        {
            value = 0;
            using (var k = Open(false))
            {
                if (k == null) return false;
                var v = k.GetValue(name);
                if (v == null) return false;
                try { value = (int)Convert.ToUInt32(v); return true; }
                catch { return false; }
            }
        }

        /// <summary>切换性能模式时是否联动 Windows 电源计划（与性能页开关同一份配置）</summary>
        public static bool PlanFollowMode
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(OwnKey))
                {
                    if (k == null) return true;
                    return Convert.ToInt32(k.GetValue("PlanFollow", 1)) == 1;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(OwnKey))
                {
                    if (k != null) k.SetValue("PlanFollow", value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }

        public static int FanProfileIndex
        {
            get
            {
                var v = (int)GetUInt("FanControlSelect", 0);
                return Math.Max(0, Math.Min(2, v));
            }
            set { SetUInt("FanControlSelect", (uint)Math.Max(0, Math.Min(2, value))); }
        }

        public static string GetUserFan(int slot)
        {
            var name = "UserFan" + (slot + 1);
            return GetStr(name, "30,30,30,30,50,60,70,100;30,30,30,30,50,60,70,100");
        }

        public static void SetUserFan(int slot, string data)
        {
            SetStr("UserFan" + (slot + 1), data);
        }

        public static string GetUserFanSys(int slot)
        {
            return GetStr("UserFan" + (slot + 1) + "_SYS", "30,30,30,30,50,60,70,100");
        }

        public static void SetUserFanSys(int slot, string data)
        {
            SetStr("UserFan" + (slot + 1) + "_SYS", data);
        }

        // ---- 本程序自有的界面偏好 ----
        public static bool DarkTheme
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(OwnKey))
                {
                    if (k == null) return true;
                    return Convert.ToInt32(k.GetValue("Dark", 1)) == 1;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(OwnKey))
                {
                    if (k != null) k.SetValue("Dark", value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }

        /// <summary>侧边栏是否收起（只显示图标）</summary>
        public static bool NavCollapsed
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(OwnKey))
                {
                    if (k == null) return false;
                    return Convert.ToInt32(k.GetValue("NavCollapsed", 0)) == 1;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(OwnKey))
                {
                    if (k != null) k.SetValue("NavCollapsed", value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }

        /// <summary>是否监听 Fn 组合键（EC 的 GMC_WMIEvent）</summary>
        public static bool FnKeyEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(OwnKey))
                {
                    if (k == null) return true;
                    return Convert.ToInt32(k.GetValue("FnKey", 1)) == 1;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(OwnKey))
                {
                    if (k != null) k.SetValue("FnKey", value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }

        /// <summary>
        /// Fn 事件去重窗口（毫秒）。EC 对同一次按键会连发两条相同负载：实测背光键 13/13 次
        /// 都是双发、间隔 190–375ms（中位 253ms），旧版 2500ms 会把真实的第二次按键也吞掉。
        /// 默认 600ms（覆盖回声上界并留 1.6 倍余量），可在 HKCU\SOFTWARE\LeishenCC\FnEchoWindowMs 调整。
        /// </summary>
        public static int FnEchoWindowMs
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(OwnKey))
                {
                    if (k == null) return 600;
                    int v = Convert.ToInt32(k.GetValue("FnEchoWindowMs", 600));
                    return Math.Max(100, Math.Min(2500, v));
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(OwnKey))
                {
                    if (k != null)
                        k.SetValue("FnEchoWindowMs", Math.Max(100, Math.Min(2500, value)), RegistryValueKind.DWord);
                }
            }
        }

        public static bool MinimizeToTray
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(OwnKey))
                {
                    if (k == null) return true;
                    return Convert.ToInt32(k.GetValue("Tray", 1)) == 1;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(OwnKey))
                {
                    if (k != null) k.SetValue("Tray", value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace LeishenCC.Core
{
    /// <summary>
    /// 摄像头开关：原厂是靠 setupapi 禁用/启用 PnP 设备实现，这里改用系统自带的 pnputil。
    /// 设备 HWID 从 Camera.ini 读取（与原厂同一份文件），读不到则按 Quanta 摄像头 VID 兜底。
    /// </summary>
    public static class CameraService
    {
        private const string VendorIni = @"C:\Program Files (x86)\Leishen\ControlCenter\Camera.ini";

        public static List<string> LoadHwids()
        {
            var list = new List<string>();
            try
            {
                string ini = File.Exists(VendorIni)
                    ? VendorIni
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Camera.ini");
                if (File.Exists(ini))
                {
                    foreach (var line in File.ReadAllLines(ini))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("CAMERA", StringComparison.OrdinalIgnoreCase) && t.Contains("="))
                        {
                            var v = t.Substring(t.IndexOf('=') + 1).Trim();
                            if (v.Length > 8) list.Add(v.ToUpperInvariant());
                        }
                    }
                }
            }
            catch { }
            if (list.Count == 0) list.Add(@"USB\VID_0408");
            return list;
        }

        public static string FindDeviceId(out bool enabled)
        {
            enabled = false;
            var hwids = LoadHwids();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, Name, Status, ConfigManagerErrorCode FROM Win32_PnPEntity"))
                using (var col = searcher.Get())
                {
                    foreach (ManagementObject o in col)
                    {
                        string id = Convert.ToString(o["DeviceID"]);
                        if (string.IsNullOrEmpty(id)) continue;
                        bool match = false;
                        foreach (var h in hwids)
                        {
                            if (id.ToUpperInvariant().Contains(h)) { match = true; break; }
                        }
                        if (!match) continue;
                        string status = Convert.ToString(o["Status"]);
                        int code;
                        int.TryParse(Convert.ToString(o["ConfigManagerErrorCode"]), out code);
                        enabled = status == "OK" || (code == 0);
                        return id;
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool SetEnabled(bool enable, out string message)
        {
            message = "";
            bool currentlyEnabled;
            var id = FindDeviceId(out currentlyEnabled);
            if (id == null)
            {
                message = "未找到匹配的摄像头设备（Camera.ini 中的 HWID）";
                return false;
            }
            if (currentlyEnabled == enable)
            {
                message = enable ? "摄像头已处于启用状态" : "摄像头已处于禁用状态";
                return true;
            }
            var psi = new ProcessStartInfo("pnputil.exe")
            {
                Arguments = (enable ? "/enable-device \"" : "/disable-device \"") + id + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    message = output.Trim();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace LeishenCC.Core
{
    /// <summary>
    /// 开机自启：用计划任务接管，并停用原厂 <c>\ControlCenter\ControlCenter</c>。
    ///
    /// 为什么用计划任务而不是 HKCU\Run：
    /// 本程序是 requireAdministrator 清单，走 Run 键每次登录都会弹一次 UAC；
    /// 计划任务以 HighestAvailable + InteractiveToken 运行，登录时静默提权，不弹窗。
    /// 原厂也是同一套做法（同一个任务，执行 ControlCenterDaemon.exe /1）。
    ///
    /// 全部操作可逆：接管 = 注册 \LeishenCC + 禁用原任务；取消 = 删除 \LeishenCC + 恢复原任务。
    /// </summary>
    public static class StartupTask
    {
        public const string TaskName = "LeishenCC";
        public const string OriginalTaskName = "ControlCenter";
        public const string OriginalTaskPath = @"\ControlCenter\";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public static string LastError = "";

        public static string ExePath
        {
            get
            {
                // 注意：Assembly.Location 在框架依赖应用里返回的是 .dll，不能直接用于自启。
                try
                {
                    var p = Process.GetCurrentProcess().MainModule.FileName;
                    if (!string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return p;
                }
                catch { }
                try
                {
                    var p = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(p)) return Path.ChangeExtension(p, ".exe");
                }
                catch { }
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LeishenCC.exe");
            }
        }

        /// <summary>诊断输出：用于排查任务读取失败的原因。</summary>
        public static string DebugDump()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                object svc = Service();
                sb.AppendLine("service     = ok");
                object folder = GetFolder(svc, OriginalTaskPath);
                sb.AppendLine("folder      = " + (folder == null ? "null" : "ok"));
                object task = Call(folder, "GetTask", OriginalTaskName);
                sb.AppendLine("task        = " + (task == null ? "null" : "ok"));
                sb.AppendLine("Enabled raw = " + GetProp(task, "Enabled"));
                sb.AppendLine("State raw   = " + GetProp(task, "State"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERR: " + Detail(ex));
            }
            return sb.ToString();
        }

        private static string Detail(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(ex.GetType().Name + ": " + ex.Message);
            var inner = ex.InnerException;
            while (inner != null)
            {
                sb.Append(" <- " + inner.GetType().Name + ": " + inner.Message);
                inner = inner.InnerException;
            }
            return sb.ToString();
        }

        // ---------- 对外接口 ----------

        /// <summary>任务状态。Unknown 表示读不到（多半是没有管理员权限），不能当成"未启用"。</summary>
        private enum TaskState { Missing, Enabled, Disabled, Unknown }

        /// <summary>重置版是否已接管开机自启（\LeishenCC 存在且启用）。</summary>
        public static bool Active
        {
            get { return Query(TaskName, @"\") == TaskState.Enabled; }
        }

        /// <summary>原厂自启任务当前是否启用。</summary>
        public static bool OriginalEnabled
        {
            get { return Query(OriginalTaskName, OriginalTaskPath) == TaskState.Enabled; }
        }

        /// <summary>能否读到原厂任务的状态（非管理员时该文件夹可能不可读）。</summary>
        public static bool OriginalKnown
        {
            get { var s = Query(OriginalTaskName, OriginalTaskPath); return s == TaskState.Missing || s == TaskState.Enabled || s == TaskState.Disabled; }
        }

        /// <summary>接管：注册 \LeishenCC（登录触发、最高权限、启动后最小化到托盘），并禁用原厂任务。</summary>
        public static bool Enable()
        {
            LastError = "";
            try
            {
                Register();
                SetEnabled(OriginalTaskName, OriginalTaskPath, false);
                RemoveLegacyRunKey();
                return true;
            }
            catch (Exception ex)
            {
                LastError = Friendly(ex);
                return false;
            }
        }

        /// <summary>取消接管：删除 \LeishenCC，恢复原厂任务。可重复调用。</summary>
        public static bool Disable()
        {
            LastError = "";
            try
            {
                DeleteTask(TaskName, @"\");
                SetEnabled(OriginalTaskName, OriginalTaskPath, true);
                RemoveLegacyRunKey();
                return true;
            }
            catch (Exception ex)
            {
                LastError = Friendly(ex);
                return false;
            }
        }

        /// <summary>
        /// 路径自愈：程序被移动或重新构建到别的目录后，任务里记录的旧路径会失效。
        /// 已接管且路径不一致时，用当前 exe 重新注册一次。
        /// </summary>
        public static void RepairIfNeeded()
        {
            try
            {
                if (!Active) return;
                string reg = RegisteredExe;
                if (string.IsNullOrEmpty(reg)) return;
                if (string.Equals(reg, ExePath, StringComparison.OrdinalIgnoreCase)) return;
                Register();
                LastError = "";
            }
            catch (Exception ex)
            {
                LastError = Friendly(ex);
            }
        }

        /// <summary>把当前状态写成人话，用于界面显示。</summary>
        public static string Describe()
        {
            bool me = Active;
            var orig = Query(OriginalTaskName, OriginalTaskPath);

            if (orig == TaskState.Unknown)
                return me
                    ? "重置版已接管开机自启。（原版任务状态读不到，通常需要管理员权限。）"
                    : "重置版未接管；原版任务状态读不到（通常需要管理员权限）。";

            bool o = orig == TaskState.Enabled;

            if (me && !o)
                return "已接管：重置版登录时自动启动（最小化到托盘），原版 ControlCenter 自启已禁用。";
            if (me && o)
                return "重置版已启用，但原版任务仍启用 —— 两边会同时启动，建议关闭原版。";
            if (!me && o)
                return "当前由原版 ControlCenter 开机自启，重置版未接管。";
            return "两边都未设置开机自启。";
        }

        /// <summary>任务里记录的可执行文件路径，用于发现"程序已被移动到别处"。</summary>
        public static string RegisteredExe
        {
            get
            {
                try
                {
                    object svc = Service();
                    object folder = GetFolder(svc, @"\");
                    object task = Call(folder, "GetTask", TaskName);
                    if (task == null) return null;
                    object def = Call(task, "get_Definition");
                    object actions = Call(def, "get_Actions");
                    int n = Convert.ToInt32(Call(actions, "get_Count"));
                    if (n <= 0) return null;
                    object act = Call(actions, "get_Item", 1);
                    return Convert.ToString(Call(act, "get_Path"));
                }
                catch { return null; }
            }
        }

        // ---------- 内部实现 ----------

        /// <summary>
        /// 自检：用同一份 XML 注册一个临时任务再删掉，验证 COM 通道与 XML 是否可用。
        /// 不触碰真实的 \LeishenCC 与 \ControlCenter\ControlCenter。
        /// </summary>
        public static bool SelfTest(out string report)
        {
            var sb = new System.Text.StringBuilder();
            const string probe = "LeishenCC.SelfTest";
            try
            {
                object svc = Service();
                sb.AppendLine("1) Schedule.Service 连接成功");

                string xml = BuildXml();
                try
                {
                    System.Xml.Linq.XDocument.Parse(xml);
                    sb.AppendLine("2) 任务 XML 格式校验通过");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("2) 任务 XML 格式错误: " + ex.Message);
                    report = sb.ToString();
                    return false;
                }

                object folder = OpenFolder(svc, @"\");
                sb.AppendLine("3) 根任务文件夹打开成功");

                string probeXml = xml.Replace("<URI>\\" + TaskName + "</URI>", "<URI>\\" + probe + "</URI>");
                string user = Environment.UserDomainName + "\\" + Environment.UserName;
                Call(folder, "RegisterTask", probe, probeXml, 6, user, null, 3, null);
                sb.AppendLine("4) 临时任务注册成功（注册参数有效）");

                object t = Call(folder, "GetTask", probe);
                sb.AppendLine("5) 回读 Enabled=" + GetProp(t, "Enabled"));

                object def = Call(t, "get_Definition");
                object actions = Call(def, "get_Actions");
                int n = Convert.ToInt32(Call(actions, "get_Count"));
                for (int i = 1; i <= n; i++)
                {
                    object act = Call(actions, "get_Item", i);
                    sb.AppendLine("   动作" + i + ": " + Call(act, "get_Path") + " " + Call(act, "get_Arguments"));
                }

                Call(folder, "DeleteTask", probe, 0);
                sb.AppendLine("6) 临时任务已删除");

                report = sb.ToString();
                return true;
            }
            catch (Exception ex)
            {
                sb.AppendLine("失败: " + Friendly(ex));
                try
                {
                    object folder = GetFolder(Service(), @"\");
                    Call(folder, "DeleteTask", probe, 0);
                }
                catch { }
                report = sb.ToString();
                return false;
            }
        }

        private static string BuildXml()
        {
            string exe = ExePath;
            string dir = Path.GetDirectoryName(exe) ?? "";
            string sid;
            try { sid = WindowsIdentity.GetCurrent().User.Value; }
            catch { sid = Environment.UserDomainName + "\\" + Environment.UserName; }

            // 不写 XML 声明，避免 BSTR 与声明的 encoding 不一致导致部分系统拒绝导入
            return
                "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>雷神控制中心（重建版）：登录时启动并最小化到托盘</Description>" +
                "<URI>\\" + TaskName + "</URI></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled>" +
                "<UserId>" + Esc(sid) + "</UserId><Delay>PT8S</Delay></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + Esc(sid) + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings>" +
                "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<AllowHardTerminate>true</AllowHardTerminate>" +
                "<StartWhenAvailable>true</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
                "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand>" +
                "<Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle>" +
                "<WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority>" +
                "</Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command>" +
                "<Arguments>--startup</Arguments>" +
                "<WorkingDirectory>" + Esc(dir) + "</WorkingDirectory></Exec></Actions>" +
                "</Task>";
        }

        private static string Esc(string s)
        {
            return s == null ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static void Register()
        {
            object svc = Service();
            object folder = OpenFolder(svc, @"\");
            if (folder == null) throw new InvalidOperationException("无法打开根任务文件夹");
            // TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3
            // userId 在 INTERACTIVE_TOKEN 下会被忽略，但部分实现传 null 会报参数错误，显式给出更稳
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            Call(folder, "RegisterTask", TaskName, BuildXml(), 6, user, null, 3, null);
        }

        private static TaskState Query(string name, string path)
        {
            try
            {
                object svc = Service();
                object folder;
                try { folder = OpenFolder(svc, path); }
                catch (Exception ex) { return IsNotFound(ex) ? TaskState.Missing : TaskState.Unknown; }

                object task;
                try { task = Call(folder, "GetTask", name); }
                catch { return TaskState.Missing; }
                if (task == null) return TaskState.Missing;

                return Convert.ToBoolean(GetProp(task, "Enabled")) ? TaskState.Enabled : TaskState.Disabled;
            }
            catch { return TaskState.Unknown; }
        }

        private static void SetEnabled(string name, string path, bool enabled)
        {
            object svc = Service();
            object folder;
            try { folder = OpenFolder(svc, path); }
            catch (Exception ex)
            {
                if (IsNotFound(ex)) return;     // 原厂没装，无需处理
                throw;                          // 无权限 → 交给调用方报错，避免"假成功"
            }

            object task;
            try { task = Call(folder, "GetTask", name); }
            catch (Exception ex)
            {
                if (IsNotFound(ex)) return;
                throw;
            }
            if (task == null) return;
            SetProp(task, "Enabled", enabled);
        }

        private static bool IsNotFound(Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            int hr = inner.HResult & 0xFFFF;
            return hr == 2 || hr == 3;          // ERROR_FILE_NOT_FOUND / PATH_NOT_FOUND
        }

        private static void DeleteTask(string name, string path)
        {
            object svc = Service();
            object folder = GetFolder(svc, path);
            if (folder == null) return;
            try { Call(folder, "DeleteTask", name, 0); }
            catch { /* 不存在即视为已删除 */ }
        }

        /// <summary>清掉旧版遗留的 HKCU\Run 项（否则会和计划任务一起启动，并弹一次 UAC）。</summary>
        private static void RemoveLegacyRunKey()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null && k.GetValue(TaskName) != null) k.DeleteValue(TaskName, false);
                }
            }
            catch { }
        }

        private static string Friendly(Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            int hr = inner.HResult & 0xFFFF;
            if (hr == 5 || inner is UnauthorizedAccessException) return "需要管理员权限（拒绝访问）";
            if (hr == 2) return "计划任务服务不可用";
            return inner.Message;
        }

        // ---------- 任务计划程序 COM 的极简调用 ----------

        private static object Service()
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) throw new InvalidOperationException("任务计划程序不可用");
            object svc = Activator.CreateInstance(t);
            svc.GetType().InvokeMember("Connect", BindingFlags.InvokeMethod, null, svc, null);
            return svc;
        }

        private static object OpenFolder(object svc, string path)
        {
            if (svc == null) throw new InvalidOperationException("任务计划服务未连接");
            return svc.GetType().InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, svc, new object[] { path });
        }

        private static object GetFolder(object svc, string path)
        {
            if (svc == null) return null;
            try { return OpenFolder(svc, path); }
            catch { return null; }
        }

        private static object Call(object target, string member, params object[] args)
        {
            if (target == null) return null;
            var a = (args == null || args.Length == 0) ? null : args;
            return target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, a);
        }

        private static object GetProp(object target, string name)
        {
            if (target == null) return null;
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        private static void SetProp(object target, string name, object value)
        {
            if (target == null) return;
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }
    }
}

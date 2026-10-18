using System;
using LeishenCC.Core;

namespace StartupProbe
{
    /// <summary>
    /// 自启诊断：只读查询 LeishenCC 计划任务与原厂任务的状态，
    /// 再试注册一个临时任务验证 COM 通道（成功后立刻删除）。
    /// 不修改、不停用任何真实任务。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var log = new System.Text.StringBuilder();
            void W(string s) { log.AppendLine(s); }

            W("ExePath            = " + StartupTask.ExePath);
            W("LeishenCC 任务启用 = " + StartupTask.Active);
            W("原厂任务启用       = " + StartupTask.OriginalEnabled + "  (可读=" + StartupTask.OriginalKnown + ")");
            W("任务登记的可执行文件 = " + (StartupTask.RegisteredExe ?? "(无任务)"));
            W("状态描述           = " + StartupTask.Describe());

            W("");
            W("--- 原厂任务读取诊断 ---");
            W(StartupTask.DebugDump());

            W("");
            W("--- COM 注册通道自检 ---");
            bool ok = StartupTask.SelfTest(out string report);
            W("SelfTest = " + ok);
            W(report);

            // 只读模式：默认不接管；加 --enable 才真正执行
            if (args.Length > 0 && args[0] == "--enable")
            {
                W("");
                W("--- 执行接管 ---");
                bool r = StartupTask.Enable();
                W("Enable = " + r + (r ? "" : "  错误: " + StartupTask.LastError));
                W("Enable 后状态 = " + StartupTask.Describe());
            }
            else if (args.Length > 0 && args[0] == "--disable")
            {
                W("");
                W("--- 恢复原版自启 ---");
                bool r = StartupTask.Disable();
                W("Disable = " + r + (r ? "" : "  错误: " + StartupTask.LastError));
                W("Disable 后状态 = " + StartupTask.Describe());
            }

            var outPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startupprobe.log");
            System.IO.File.WriteAllText(outPath, log.ToString(), new System.Text.UTF8Encoding(false));
            try { Console.Write(log.ToString()); } catch { }
            return 0;
        }
    }
}

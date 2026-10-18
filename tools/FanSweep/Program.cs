using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using LeishenCC.Core;

namespace FanSweep
{
    /// <summary>
    /// SYS（南桥）风扇扫频：固定 CPU / GPU 占空比，只改 SYS，逐档稳定后取均值，
    /// 用来回答「SYS 每多 1000 转换几度」。
    ///
    /// 前提：控制中心（LeishenCC）必须已退出，否则它每 2 秒会覆盖这里的设定。
    /// 结束时会 SetFanControl(false) 交回 BIOS；注册表 FanControlStatus 不动，
    /// 重新启动控制中心即可恢复曲线控制。
    /// </summary>
    internal static class Program
    {
        private static readonly int[] Steps = { 20, 40, 60, 80, 100 };

        private static int Main(string[] args)
        {
            int cpuFixed = 75, gpuFixed = 45;
            int settle = 90, sample = 45;
            if (args.Length >= 2)
            {
                int.TryParse(args[0], out cpuFixed);
                int.TryParse(args[1], out gpuFixed);
            }
            if (args.Length >= 4)
            {
                int.TryParse(args[2], out settle);
                int.TryParse(args[3], out sample);
            }

            var log = new List<string>();
            void W(string s)
            {
                log.Add(s);
                try { Console.WriteLine(s); } catch { }
            }

            var wmi = new GmWmi();
            if (!wmi.Connect())
            {
                W("EC 连接失败：" + wmi.LastError);
                Flush(log);
                return 2;
            }

            W(string.Format(CultureInfo.InvariantCulture,
                "固定 CPU {0}% / GPU {1}%，稳定 {2}s + 采样 {3}s，SYS 档位 {4}",
                cpuFixed, gpuFixed, settle, sample, string.Join("/", Steps)));

            if (!wmi.SetFanControl(true))
            {
                W("SetFanControl(true) 失败，可能没有管理员权限或被其他进程占用");
                Flush(log);
                return 3;
            }

            var results = new List<string>();
            results.Add("sys_duty,cpu_temp,gpu_temp,sys_temp,cpu_rpm,gpu_rpm,sys_rpm");

            try
            {
                foreach (int duty in Steps)
                {
                    string t0 = DateTime.Now.ToString("HH:mm:ss");
                    if (!wmi.SetFanSpeed(cpuFixed, gpuFixed, duty))
                    {
                        W(t0 + "  SYS " + duty + "% 下发失败");
                        continue;
                    }

                    // 稳定期
                    for (int i = 0; i < settle; i += 5)
                    {
                        Thread.Sleep(5000);
                        // 固定值模式 EC 可能会被别的写入复位，这里周期性重发
                        if (i % 30 == 0 && i > 0) wmi.SetFanSpeed(cpuFixed, gpuFixed, duty);
                    }

                    // 采样期
                    var rows = new List<int[]>();
                    int n = Math.Max(1, sample / 5);
                    for (int i = 0; i < n; i++)
                    {
                        Thread.Sleep(5000);
                        int ct, gt, cf, gf, st, sf;
                        if (wmi.ReadThermal(out ct, out gt, out cf, out gf, out st, out sf))
                            rows.Add(new[] { ct, gt, st, cf, gf, sf });
                    }

                    if (rows.Count == 0) { W(t0 + "  SYS " + duty + "% 采样为空"); continue; }

                    // 去掉首个/末个极值，取中位数更稳
                    Func<int, int> med = idx =>
                    {
                        var v = rows.Select(r => r[idx]).OrderBy(x => x).ToList();
                        return v[v.Count / 2];
                    };

                    int mtCpu = med(0), mtGpu = med(1), mtSys = med(2);
                    int mfCpu = med(3), mfGpu = med(4), mfSys = med(5);

                    results.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6}", duty, mtCpu, mtGpu, mtSys, mfCpu, mfGpu, mfSys));

                    W(string.Format(CultureInfo.InvariantCulture,
                        "SYS {0,3}%  →  CPU {1}℃/{2}RPM  GPU {3}℃/{4}RPM  南桥 {5}℃/{6}RPM   (n={7})",
                        duty, mtCpu, mfCpu, mtGpu, mfGpu, mtSys, mfSys, rows.Count));
                }
            }
            finally
            {
                wmi.SetFanControl(false);
                W("");
                W("已交回 BIOS 控制");
                Flush(log, results);
            }
            return 0;
        }

        private static void Flush(List<string> lines, List<string> csv = null)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            try { File.WriteAllLines(Path.Combine(dir, "fansweep.log"), lines, new System.Text.UTF8Encoding(false)); } catch { }
            if (csv != null)
            {
                try { File.WriteAllLines(Path.Combine(dir, "fansweep.csv"), csv, new System.Text.UTF8Encoding(false)); } catch { }
            }
        }
    }
}

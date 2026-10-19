using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using LeishenCC.Core;

namespace LeishenFanMcp
{
    /// <summary>
    /// 本地 MCP 服务（stdio 传输，不监听任何端口）。
    /// 由 MCP 客户端以子进程方式拉起，只能通过标准输入输出通信，因此天然仅本机可达。
    /// 复用桌面端同一份 GmWmi / FanProfile / Settings 源码，直接对 EC 下发风扇曲线。
    /// </summary>
    internal static class Program
    {
        private const string ServerName = "leishen-fan";
        private const string ServerVersion = "1.0.0";

        private static readonly object Sync = new object();
        private static StreamWriter _out;
        private static readonly GmWmi Wmi = new GmWmi();
        private static bool _wmiOk;
        private static bool _threeFan;
        private static Timer _keepAlive;
        private static volatile bool _running;      // 本进程是否已接管风扇
        private static int _slot;
        private static int _tick;
        private static Mutex _ownerMutex;
        private static string _lastDelivery = "尚未下发";

        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreBios();
            Console.CancelKeyPress += (s, e) => RestoreBios();

            // --hold <分钟> [档位]：常驻接管模式（不经 MCP 协议），用于长时间实测
            if (args.Length > 0 && args[0] == "--hold")
            {
                _wmiOk = Wmi.Connect();
                _threeFan = ReadThreeFan();
                return RunHold(args);
            }

            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(TextWriter.Null);        // 任何意外输出都不能污染协议流
            _out = stdout;

            _wmiOk = Wmi.Connect();
            _threeFan = ReadThreeFan();
            _slot = Settings.FanProfileIndex;      // 状态里报告的档位必须与注册表/桌面端一致

            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { Handle(line); }
                catch (Exception ex) { Log("handle error: " + ex); }
            }

            RestoreBios();
            return 0;
        }

        /// <summary>
        /// 常驻接管：读注册表里指定档位的曲线，每 2 秒按当前温度下发，直到时间到或被终止。
        /// 时间到/收到退出信号时交回 BIOS。
        /// </summary>
        private static int RunHold(string[] args)
        {
            // 与桌面端 LeishenCC 的 FanControlService 互斥，避免两处同时写 EC
            try
            {
                using (var existing = Mutex.OpenExisting(@"Local\LeishenFanOwner"))
                {
                    Console.WriteLine("已有进程在接管风扇（控制中心已开启自定义风扇），本进程退出");
                    return 4;
                }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (Exception) { }
            try { _ownerMutex = new Mutex(true, @"Local\LeishenFanOwner"); }
            catch (Exception ex) { Console.WriteLine("创建互斥量失败: " + ex.Message); }
            int minutes = 60;
            if (args.Length > 1) int.TryParse(args[1], out minutes);
            // 档位：显式给了就用给的，否则跟随桌面端当前选择的档位
            int slot;
            if (args.Length > 2 && int.TryParse(args[2], out slot) && slot >= 1 && slot <= 3)
                _slot = slot - 1;
            else
                _slot = Settings.FanProfileIndex;

            if (!_wmiOk)
            {
                Console.WriteLine("EC 接口不可用：" + (Wmi.LastError ?? "未知"));
                return 2;
            }

            bool ok = Wmi.SetFanControl(true);
            if (!ok)
            {
                Console.WriteLine("SetFanControl(true) 失败");
                return 3;
            }
            Settings.FanControlEnabled = true;
            Settings.FanProfileIndex = _slot;
            _running = true;

            var c = Curves(_slot);
            Console.WriteLine("已接管: 档位 {0}, 时长 {1} 分钟, 三风扇={2}", _slot + 1, minutes, _threeFan);
            Console.WriteLine("曲线 CPU={0} GPU={1} SYS={2}", Join(c.Item1.Percent), Join(c.Item2.Percent), Join(c.Item3.Percent));

            var end = DateTime.Now.AddMinutes(minutes);
            int tick = 0;
            while (DateTime.Now < end)
            {
                DeliverOnce();
                if (++tick % 10 == 0)   // 每 20 秒打印一次
                {
                    int ct, gt, cf, gf, st, sf;
                    if (Wmi.ReadThermal(out ct, out gt, out cf, out gf, out st, out sf))
                    {
                        var cur = Curves(_slot);
                        Console.WriteLine("{0:HH:mm:ss} CPU {1}℃ {2}RPM → {3}% | GPU {4}℃ {5}RPM → {6}% | SYS {7}℃ {8}RPM → {9}%",
                            DateTime.Now, ct, cf, cur.Item1.PercentFor(ct),
                            gt, gf, cur.Item2.PercentFor(gt),
                            st, sf, _threeFan ? cur.Item3.PercentFor(st).ToString() : "不控制");
                    }
                }
                Thread.Sleep(2000);
            }

            RestoreBios();
            Console.WriteLine("到时，已交回 BIOS");
            return 0;
        }

        // ---------------- JSON-RPC ----------------

        private static void Handle(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("method", out var methodEl)) return;

            string method = methodEl.GetString();
            bool hasId = root.TryGetProperty("id", out var idEl);
            JsonElement id = hasId ? idEl.Clone() : default(JsonElement);
            JsonElement prms = root.TryGetProperty("params", out var p) ? p.Clone() : default(JsonElement);

            switch (method)
            {
                case "initialize":
                    Reply(id, new Dictionary<string, object>
                    {
                        ["protocolVersion"] = ReadString(prms, "protocolVersion", "2024-11-05"),
                        ["capabilities"] = new Dictionary<string, object>
                        {
                            ["tools"] = new Dictionary<string, object>()
                        },
                        ["serverInfo"] = new Dictionary<string, object>
                        {
                            ["name"] = ServerName,
                            ["version"] = ServerVersion
                        }
                    });
                    break;

                case "notifications/initialized":
                case "notifications/cancelled":
                    break;

                case "ping":
                    Reply(id, new Dictionary<string, object>());
                    break;

                case "tools/list":
                    Reply(id, new Dictionary<string, object> { ["tools"] = ToolDefinitions() });
                    break;

                case "tools/call":
                    CallTool(id, prms);
                    break;

                default:
                    if (hasId) ReplyError(id, -32601, "Method not found: " + method);
                    break;
            }
        }

        private static void CallTool(JsonElement id, JsonElement prms)
        {
            string name = ReadString(prms, "name", "");
            JsonElement args = default(JsonElement);
            if (prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("arguments", out var a) &&
                a.ValueKind == JsonValueKind.Object)
                args = a;

            string text;
            bool isError;
            try
            {
                var r = Dispatch(name, args);
                text = r.Item1;
                isError = r.Item2;
            }
            catch (Exception ex)
            {
                text = "调用失败：" + ex.Message;
                isError = true;
            }

            Reply(id, new Dictionary<string, object>
            {
                ["content"] = new object[]
                {
                    new Dictionary<string, object> { ["type"] = "text", ["text"] = text }
                },
                ["isError"] = isError
            });
        }

        private static void Reply(JsonElement id, object result)
        {
            Send(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            });
        }

        private static void ReplyError(JsonElement id, int code, string message)
        {
            Send(new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
            });
        }

        private static void Send(object msg)
        {
            string json = JsonSerializer.Serialize(msg);
            lock (Sync) _out.WriteLine(json);
        }

        // ---------------- 工具实现 ----------------

        private static Tuple<string, bool> Dispatch(string name, JsonElement args)
        {
            switch (name)
            {
                case "fan_status": return Ok(Status());
                case "fan_list_profiles": return Ok(ListProfiles());
                case "fan_set_curve": return SetCurve(args);
                case "fan_apply_slot": return ApplySlot(args);
                case "fan_set_speed": return SetSpeed(args);
                case "fan_stop": return Stop();
                case "fan_reset_curve": return ResetCurve(args);
                case "ec_read": return EcRead(args);
                default: return Err("未知工具：" + name);
            }
        }

        private static Tuple<string, bool> Ok(object o) => Tuple.Create(Pretty(o), false);
        private static Tuple<string, bool> Err(string msg) => Tuple.Create(msg, true);

        private static object Status()
        {
            int ct, gt, cf, gf, st, sf;
            bool thermalOk = Wmi.ReadThermal(out ct, out gt, out cf, out gf, out st, out sf);
            var c = Curves(_slot);
            var result = new Dictionary<string, object>
            {
                ["ecAvailable"] = _wmiOk && thermalOk,
                ["customControlEnabled"] = Settings.FanControlEnabled,
                ["activeSlot"] = _slot + 1,
                ["threeFanSupported"] = _threeFan,
                ["temperaturesC"] = new Dictionary<string, object> { ["cpu"] = ct, ["gpu"] = gt, ["sys"] = st },
                ["fansRpm"] = new Dictionary<string, object> { ["cpu"] = cf, ["gpu"] = gf, ["sys"] = sf },
                ["curveTargetPercent"] = new Dictionary<string, object>
                {
                    ["cpu"] = c.Item1.PercentFor(ct),
                    ["gpu"] = c.Item2.PercentFor(gt),
                    ["sys"] = _threeFan ? (object)c.Item3.PercentFor(st) : null
                },
                ["summary"] = _running
                    ? "本进程已接管风扇，正在按曲线每 2 秒下发"
                    : (Settings.FanControlEnabled ? "自定义风扇已开启（由桌面端或其他进程维持）" : "BIOS 自动控制"),
                ["lastDelivery"] = _lastDelivery
            };
            return result;
        }

        private static object ListProfiles()
        {
            var list = new List<object>();
            for (int i = 0; i < 3; i++)
            {
                var c = Curves(i);
                list.Add(new Dictionary<string, object>
                {
                    ["slot"] = i + 1,
                    ["active"] = i == _slot,
                    ["cpu"] = c.Item1.Percent,
                    ["gpu"] = c.Item2.Percent,
                    ["sys"] = c.Item3.Percent
                });
            }
            return new Dictionary<string, object>
            {
                ["temperatureBreakpointsC"] = FanCurve.Temps,
                ["note"] = "每档 8 个断点 30/40/50/60/70/80/90/100℃，对应 0-100% 占空比",
                ["profiles"] = list
            };
        }

        private static Tuple<string, bool> SetCurve(JsonElement args)
        {
            int slot;
            if (!TryInt(args, "slot", out slot) || slot < 1 || slot > 3)
                return Err("slot 必须是 1/2/3");

            int[] cpu, gpu, sys;
            string err;
            if (!TryCurve(args, "cpu", out cpu, out err)) return Err("cpu " + err);
            if (!TryCurve(args, "gpu", out gpu, out err)) return Err("gpu " + err);
            bool hasSys = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("sys", out _);
            if (hasSys) { if (!TryCurve(args, "sys", out sys, out err)) return Err("sys " + err); }
            else { var cur = Curves(slot - 1); sys = cur.Item3.Percent; }

            if (!_wmiOk) return Err("EC 接口不可用：" + (Wmi.LastError ?? "未知原因"));

            Settings.SetUserFan(slot - 1, Join(cpu) + ";" + Join(gpu));
            Settings.SetUserFanSys(slot - 1, Join(sys));

            return Ok(new Dictionary<string, object>
            {
                ["saved"] = true,
                ["slot"] = slot,
                ["cpu"] = cpu,
                ["gpu"] = gpu,
                ["sys"] = sys,
                ["note"] = "已写入注册表。调用 fan_apply_slot 才会开启控制并生效。"
            });
        }

        private static Tuple<string, bool> ApplySlot(JsonElement args)
        {
            int slot;
            if (!TryInt(args, "slot", out slot) || slot < 1 || slot > 3)
                return Err("slot 必须是 1/2/3");

            bool enable = true;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("enable", out var e) &&
                (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
                enable = e.GetBoolean();

            if (!_wmiOk) return Err("EC 接口不可用，请确认 BIOS 提供 RW_GMWMI 且以合适权限运行");

            _slot = slot - 1;
            Settings.FanProfileIndex = _slot;

            bool ok = Wmi.SetFanControl(enable);
            Settings.FanControlEnabled = enable;

            if (ok && enable)
            {
                _running = true;
                StartKeepAlive();
                DeliverOnce();
            }
            else
            {
                StopKeepAlive();
            }

            return Ok(new Dictionary<string, object>
            {
                ["applied"] = ok,
                ["slot"] = slot,
                ["customControlEnabled"] = Settings.FanControlEnabled,
                ["maintaining"] = _running,
                ["note"] = ok
                    ? (enable ? "已接管风扇：每 2 秒按曲线与当前温度下发。进程退出会自动交回 BIOS。"
                              : "已关闭自定义控制。")
                    : "EC 写入失败（0xFB00/0x0206）"
            });
        }

        private static Tuple<string, bool> SetSpeed(JsonElement args)
        {
            int cpu, gpu, sys = 255;
            if (!TryInt(args, "cpu", out cpu) || cpu < 0 || cpu > 100) return Err("cpu 必须是 0-100");
            if (!TryInt(args, "gpu", out gpu) || gpu < 0 || gpu > 100) return Err("gpu 必须是 0-100");
            bool hasSys = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("sys", out _);
            if (hasSys && (!TryInt(args, "sys", out sys) || sys < 0 || sys > 100))
                return Err("sys 必须是 0-100");
            if (!_threeFan) sys = 255;

            if (!_wmiOk) return Err("EC 接口不可用");

            // 固定占空比需要先打开自定义控制，否则 EC 不采纳
            bool ctrl = Wmi.SetFanControl(true);
            bool sent = Wmi.SetFanSpeed(cpu, gpu, sys);
            Settings.FanControlEnabled = ctrl;

            StopKeepAlive();          // 固定值模式：停止曲线维持
            _running = true;          // 仍然视为已接管，退出时交回 BIOS
            _lastDelivery = string.Format("{0:HH:mm:ss} 固定 CPU {1}% GPU {2}% SYS {3} -> {4}",
                DateTime.Now, cpu, gpu, _threeFan ? sys.ToString() : "不控制", sent ? "成功" : "失败");

            return Ok(new Dictionary<string, object>
            {
                ["controlEnabled"] = ctrl,
                ["sent"] = sent,
                ["cpu"] = cpu,
                ["gpu"] = gpu,
                ["sys"] = _threeFan ? (object)sys : null,
                ["note"] = "当前为固定占空比，不随温度变化。调用 fan_stop 交回 BIOS。"
            });
        }

        private static Tuple<string, bool> Stop()
        {
            bool ok = _wmiOk && Wmi.SetFanControl(false);
            Settings.FanControlEnabled = false;
            _running = false;
            StopKeepAlive();
            _lastDelivery = string.Format("{0:HH:mm:ss} 已交回 BIOS", DateTime.Now);
            return Ok(new Dictionary<string, object>
            {
                ["stopped"] = ok,
                ["note"] = ok ? "已关闭自定义风扇，交回 BIOS 自动策略。" : "EC 写入失败"
            });
        }

        private static Tuple<string, bool> ResetCurve(JsonElement args)
        {
            int slot;
            if (!TryInt(args, "slot", out slot) || slot < 1 || slot > 3)
                return Err("slot 必须是 1/2/3");

            int table = ReadFanTable();
            string cpuGpu, sysCurve;
            if (table == 4)
            {
                cpuGpu = "30,30,30,30,50,60,70,100;30,30,30,30,50,60,70,100";
                sysCurve = "30,30,30,30,50,60,70,100";
            }
            else
            {
                cpuGpu = "0,0,0,0,40,50,60,70;0,0,0,0,30,40,50,70";
                sysCurve = "0,0,0,0,30,40,50,70";
            }

            var pair = FanCurve.ParsePair(cpuGpu);
            Settings.SetUserFan(slot - 1, pair.Cpu.ToString() + ";" + pair.Gpu.ToString());
            Settings.SetUserFanSys(slot - 1, sysCurve);

            return Ok(new Dictionary<string, object>
            {
                ["slot"] = slot,
                ["fanTableId"] = table,
                ["cpu"] = pair.Cpu.Percent,
                ["gpu"] = pair.Gpu.Percent,
                ["sys"] = FanCurve.Parse(sysCurve).Percent,
                ["note"] = "已恢复该机型默认曲线（写入注册表），fan_apply_slot 后生效。"
            });
        }

        private static Tuple<string, bool> EcRead(JsonElement args)
        {
            string code = ReadString(args, "code", "");
            if (string.IsNullOrEmpty(code)) return Err("code 必填，十六进制子命令，例如 \"0201\"");
            ushort sub;
            if (!ushort.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out sub))
                return Err("code 解析失败，应为十六进制，如 0201");
            if (!_wmiOk) return Err("EC 接口不可用");

            uint a2 = 0;
            int tmp;
            if (TryInt(args, "a2", out tmp) && tmp >= 0) a2 = (uint)tmp;

            Smi r;
            bool ok = Wmi.Read(sub, a2, out r);
            return Ok(new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["code"] = "0x" + sub.ToString("X4"),
                ["a0"] = "0x" + r.a0.ToString("X4"),
                ["a2"] = r.a2,
                ["a3"] = r.a3,
                ["a4"] = r.a4,
                ["a5"] = r.a5,
                ["a6"] = r.a6
            });
        }

        // ---------------- 维持下发 ----------------

        private static void StartKeepAlive()
        {
            lock (Sync)
            {
                if (_keepAlive == null)
                    _keepAlive = new Timer(_ => Tick(), null, 500, 1000);
            }
        }

        private static void StopKeepAlive()
        {
            lock (Sync)
            {
                if (_keepAlive != null)
                {
                    _keepAlive.Dispose();
                    _keepAlive = null;
                }
            }
        }

        private static void Tick()
        {
            if (!_running) return;
            try
            {
                if (++_tick % 2 != 0) return;   // 与桌面端一致：2 秒一次
                DeliverOnce();
            }
            catch { }
        }

        private static void DeliverOnce()
        {
            int ct, gt, cf, gf, st, sf;
            if (!Wmi.ReadThermal(out ct, out gt, out cf, out gf, out st, out sf)) return;

            // 跟随注册表：桌面端切性能模式会把 FanControlSelect 换掉，这里必须跟着换曲线，
            // 否则本进程每 2 秒的固定档位下发会把模式切换的效果盖掉。
            int slot = Settings.FanProfileIndex;
            if (slot != _slot)
            {
                _slot = slot;
                _lastIdxCpu = _lastIdxGpu = _lastIdxSys = -1;
            }

            var c = Curves(_slot);
            int cpu = PercentHysteresis(c.Item1, ct, ref _lastIdxCpu);
            int gpu = PercentHysteresis(c.Item2, gt, ref _lastIdxGpu);
            int sys = _threeFan ? PercentHysteresis(c.Item3, st, ref _lastIdxSys) : 255;
            bool ok = Wmi.SetFanSpeed(cpu, gpu, sys);
            _lastDelivery = string.Format("{0:HH:mm:ss} CPU {1}% GPU {2}% SYS {3} -> {4}",
                DateTime.Now, cpu, gpu, _threeFan ? sys.ToString() : "不控制", ok ? "成功" : "失败");
        }

        private static int _lastIdxCpu = -1, _lastIdxGpu = -1, _lastIdxSys = -1;

        /// <summary>
        /// 带滞回的档位取值。原厂 PercentFor 是 10℃ 一档的阶梯：79℃→55%、80℃→75%，
        /// 温度在断点附近抖动时转速会来回跳（实测 2626↔3297 RPM）。
        /// 这里保留「升温立即升档」，但降档要求低于当前档下界 2℃，把抖动压掉。
        /// </summary>
        private static int PercentHysteresis(FanCurve curve, int tempC, ref int lastIdx, int hyst = 2)
        {
            int idx = curve.IndexFor(tempC);
            if (lastIdx >= 0 && idx < lastIdx)
            {
                int boundary = FanCurve.Temps[lastIdx];
                if (tempC > boundary - hyst) idx = lastIdx;   // 还没凉透，先不降
            }
            lastIdx = idx;
            return curve.Percent[idx];
        }

        /// <summary>进程退出时把风扇交回 BIOS；本进程从未接管则不碰用户当前状态。</summary>
        private static void RestoreBios()
        {
            if (!_running) return;
            _running = false;
            try
            {
                StopKeepAlive();
                Wmi.SetFanControl(false);
                Settings.FanControlEnabled = false;
            }
            catch { }
        }

        // ---------------- 辅助 ----------------

        private static Tuple<FanCurve, FanCurve, FanCurve> Curves(int slot)
        {
            var pair = FanCurve.ParsePair(Settings.GetUserFan(slot));
            return Tuple.Create(pair.Cpu, pair.Gpu, FanCurve.Parse(Settings.GetUserFanSys(slot)));
        }

        private static bool ReadThreeFan()
        {
            Smi r;
            return Wmi.Read(GmWmi.GET_CAPS, 0, out r) && (r.a2 == 52 || r.a2 == 53);
        }

        private static int ReadFanTable()
        {
            Smi r;
            return Wmi.Read(GmWmi.GET_FAN_TABLE, 0, out r) ? (int)r.a2 : 0;
        }

        private static string Join(int[] v) => string.Join(",", v);

        private static string Pretty(object o) =>
            JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });

        private static string ReadString(JsonElement obj, string name, string def)
        {
            if (obj.ValueKind != JsonValueKind.Object) return def;
            if (!obj.TryGetProperty(name, out var v)) return def;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : def;
        }

        private static bool TryInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            if (obj.ValueKind != JsonValueKind.Object) return false;
            if (!obj.TryGetProperty(name, out var v)) return false;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value)) return true;
            if (v.ValueKind == JsonValueKind.String) return int.TryParse(v.GetString(), out value);
            return false;
        }

        private static bool TryCurve(JsonElement obj, string name, out int[] values, out string error)
        {
            values = null;
            error = "";
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                error = "必须是 8 个 0-100 的整数数组";
                return false;
            }
            var list = new List<int>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int x))
                {
                    error = "必须是 8 个 0-100 的整数数组";
                    return false;
                }
                if (x < 0 || x > 100)
                {
                    error = "取值必须在 0-100 之间";
                    return false;
                }
                list.Add(x);
            }
            if (list.Count != 8)
            {
                error = "必须正好 8 个点（对应 30/40/50/60/70/80/90/100℃）";
                return false;
            }
            values = list.ToArray();
            return true;
        }

        private static void Log(string msg)
        {
            try { Console.Error.WriteLine("[leishen-fan] " + msg); } catch { }
        }

        // ---------------- 工具定义 ----------------

        private static object ToolDefinitions()
        {
            return new object[]
            {
                Tool("fan_status", "读取当前风扇状态：自定义控制开关、CPU/GPU/SYS 温度与转速、当前曲线目标占空比、最近一次下发结果。只读。",
                    new Dictionary<string, object>()),

                Tool("fan_list_profiles", "列出 3 个风扇档位的完整曲线（CPU/GPU/SYS 各 8 点）与当前激活档位。只读。",
                    new Dictionary<string, object>()),

                Tool("fan_set_curve",
                    "写入某个档位的自定义风扇曲线（只写注册表，不立即生效）。8 个点依次对应 30/40/50/60/70/80/90/100℃，值为 0-100 的占空比百分比。",
                    new Dictionary<string, object>
                    {
                        ["slot"] = Prop("integer", "档位：1/2/3", 1, 3),
                        ["cpu"] = CurveProp("CPU 曲线，8 个 0-100 整数"),
                        ["gpu"] = CurveProp("GPU 曲线，8 个 0-100 整数"),
                        ["sys"] = CurveProp("系统风扇曲线，8 个 0-100 整数（可选，缺省沿用当前值）")
                    },
                    new[] { "slot", "cpu", "gpu" }),

                Tool("fan_apply_slot",
                    "启用某个档位的自定义风扇并立即接管：每 2 秒按“曲线 + 当前温度”下发占空比。会绕过 BIOS 自动策略。",
                    new Dictionary<string, object>
                    {
                        ["slot"] = Prop("integer", "档位：1/2/3", 1, 3),
                        ["enable"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "true 开启（默认），false 关闭" }
                    },
                    new[] { "slot" }),

                Tool("fan_set_speed",
                    "立即下发固定占空比（不随温度变化），用于临时压测或降噪。会自动开启自定义控制。sys 仅在支持三风扇的机型生效。",
                    new Dictionary<string, object>
                    {
                        ["cpu"] = Prop("integer", "CPU 占空比 0-100", 0, 100),
                        ["gpu"] = Prop("integer", "GPU 占空比 0-100", 0, 100),
                        ["sys"] = Prop("integer", "系统风扇占空比 0-100（可选）", 0, 100)
                    },
                    new[] { "cpu", "gpu" }),

                Tool("fan_stop", "关闭自定义风扇控制，交回 BIOS 自动策略。建议结束操作前调用。",
                    new Dictionary<string, object>()),

                Tool("fan_reset_curve", "把某个档位恢复为该机型（按风扇表编号）的出厂默认曲线，仅写注册表。",
                    new Dictionary<string, object> { ["slot"] = Prop("integer", "档位：1/2/3", 1, 3) },
                    new[] { "slot" }),

                Tool("ec_read", "只读诊断：执行 0xFA00 查询并返回 a0/a2..a6。常用 code：0201 能力、0202 硬件状态、0203 显示、0205 风扇表、0207 系统温度。",
                    new Dictionary<string, object>
                    {
                        ["code"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "十六进制子命令，如 0201" },
                        ["a2"] = Prop("integer", "a2 参数，默认 0", 0, 65535)
                    },
                    new[] { "code" })
            };
        }

        private static Dictionary<string, object> Tool(string name, string desc,
            Dictionary<string, object> props, string[] required = null)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props
            };
            if (required != null && required.Length > 0) schema["required"] = required;
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = schema
            };
        }

        private static Dictionary<string, object> Prop(string type, string desc, int min, int max)
        {
            return new Dictionary<string, object>
            {
                ["type"] = type,
                ["description"] = desc,
                ["minimum"] = min,
                ["maximum"] = max
            };
        }

        private static Dictionary<string, object> CurveProp(string desc)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = desc,
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["maximum"] = 100
                },
                ["minItems"] = 8,
                ["maxItems"] = 8
            };
        }
    }
}

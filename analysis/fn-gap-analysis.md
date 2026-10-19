# Fn 热键监听：原厂反编译实现 vs 当前重写实现 逐条差距分析

- 任务：task-2（原厂 Fn 事件处理逐行对比与缺陷清单）
- 工作区：`C:\Users\Administrator\Documents\LeishenReverse`
- 分析方式：只读静态对比 + 现成日志统计；**未修改 src/、tools/ 任何文件**，未构建、未上机重跑。
- 主要证据：
  - 原厂：`decomp/ControlCenterDaemon/ControlCenterDaemon/ControlCenterDaemon.cs`（守护进程）、`decomp/ControlCenter/ControlCenterSpace/MainWindow.cs`（UI 进程）、`decomp/Daemon/ControlCenterDaemon.all.cs`（另一份守护进程）、`decomp/ControlCenter/ControlCenterSpace.BIOS/*`（BIOS 分支变体）、`*/MyWmiBase.cs`。
  - 重写：`src/LeishenCC/Core/FnKeys.cs`、`FnKeyService.cs`、`GmWmi.cs`、`Hub.cs`、`App.xaml.cs`、`MainWindow.xaml.cs`、`FanControlService.cs`、`PowerModeService.cs`、`Views/SystemView.xaml.cs`。
  - 实测日志：`src/LeishenCC/bin/Release/net5.0-windows/leishen.log`（2026-10-19 19:24–20:27，64 条 Fn 事件）+ 现场记录 `.workbuddy/memory/2026-10-19.md`。

### 0.1 快照声明（重要，避免误读行号）

- 本文的**原厂结论、日志统计、协议判据**对所有版本成立，不受重写实现改动影响。
- 对"重写版现状"的逐行引用，主体基于 **20:31–20:36 的工作树快照**（当时 `FnKeyService` 用 2500ms 窗口、无看门狗、`ClearEvent` 总是执行但忽略返回值）。
- 在本文写作期间（20:36–20:38），并行工作流修改了 `FnKeyService.cs`、`FnKeys.cs`、`Settings.cs`、`GmWmi.cs`、`App.xaml.cs`。当前工作树已包含：**700ms 可配去重窗口、`Stopped` + 30s 看门狗自动重订阅、`finally` 里统一清位、Fn+3 电池门控、Unknown 事件日志、`WMI2Event` 对照抓包**。因此 §7 的清单按"当前状态 = 已实现 / 部分实现 / 未实现"标注；§2 的逐行引用若与当前文件不符，以函数名/行为描述为准（见 §2.10）。

---

## 0. 结论速览（先给判断，证据在后文）

| # | 问题 | 判断 | 置信度 |
|---|------|------|--------|
| 1 | 原厂是否去重 | **完全不去重**。三份原厂代码（daemon / CC / all）的 `ParseWMIEventData` 中没有任何时间窗、序号或负载比较；相同负载会逐条走完整分支（重复按性能键会连跳两档）。 | 确定（代码穷举） |
| 2 | `Event_Alpha` 门控的真实作用 | **不是去重，是「当前背光值快照一致性校验」**：只有负载里的当前背光值 == 缓存值才处理按键，不等则只把缓存同步为负载值并**丢弃该条事件**。副作用是：daemon 版会把「启动后第一条非背光事件」吞掉，CC 版不会（两者初始化位置不同）。 | 行为确定；"设计意图"为推断（见 §2.2） |
| 3 | 事件清除缺失分支 | 原厂缺：DC/TypeC 下的性能键、摄像头「状态未变/不支持/找不到设备/未知值」、HDMI/适配器（从不确认）、Unknown、alpha 门控失败、daemon 启动后首条事件。重写版 20:31 快照"总是清"但**不检查清位返回值、失败不重试、启动/恢复后不清残位**；20:37 快照已把清位移入 `finally` 并只清负载位，但返回值/重试/启动残位三项仍未做。这是"热键停报只能重启"最强的软件侧嫌疑。 | 缺失分支：确定；停报因果：不确定（§4） |
| 4 | DC/TypeC 门控 | 原厂**有**：`!IsOnDCMode && !IsTypeCAdapterIn` 才切换性能模式（daemon 536、CC 3734）。重写版 20:31 快照**完全没有**该门控；20:37 快照已有"Fn+3 电池供电忽略"（`FnKeyService.cs:416-422`），但**仍无 TypeC 判断**，`Hub` 也没读 `0x0206 a4`。 | 原厂语义确定；重写版状态：部分 |
| 5 | watcher 生命周期 | 原厂有一套跨进程交接协议：registry `REG_MAIN.CONTROLCENTER_CLOSE(14)` 在 daemon 与 CC 之间切换监听权；CC 隐藏到托盘时 watcher 保留，关闭时 Dispose；但交接存在"点 X 隐藏后 daemon 与 CC 同时监听"的竞态。重写版单进程不需要交接；20:31 快照缺 `Stopped`/看门狗/重建，**20:37 快照已补上**（`FnKeys.cs:144,188-203`；`FnKeyService.cs:171-237`），仍缺 Resume 重建与退出 Stop。 | 原厂机制确定；重写版：大部分已补 |
| 6 | Unknown 事件是否清位 | 原厂**不清**；重写版 20:31 快照无 Unknown 分支（静默），20:37 快照已打印全 32 字节（`FnKeyService.cs:307-310`）但**仍不清位**。风险来自"EC 是否要求确认"，见 §2.8。 | 行为确定；风险不确定 |
| 7 | 去重窗口是否吞真实按键 | **会**。日志逐条回放可精确复现 20:31 快照（2500ms）行为：20:20:19–20:20:40 的 13 条性能键事件里只执行 6 次、忽略 7 次；与现场记录「连按 10 次」对不上（至少 3～4 次真实按键下落不明）。20:37 快照已改成 **700ms 可配**（`Settings.FnEchoWindowMs`），方向正确；但仍是"前 9 字节 + 不重置 + 无 alpha 判据"，边界误判仍在（§2.10）。建议算法见 §3。 | 日志可复现=确定；"7 条里哪几条是真实按键"=不确定 |
| 8 | 性能键彻底停报的原因 | 现有证据**不足以定案**。可排除"程序丢事件"（独立监听同样收不到，而同一时刻背光事件正常）；「EC 侧闩锁」与「SMI 高频写饿死 EC 热键任务」两条假设都有支持证据也都有反证。因此补丁应同时做"**提高确认可靠性**"和"**把 SMI 流量降到原厂量级**"，并加可观测性。 | 不确定（§4.2 列三假设与判决实验） |
| 9 | 32/48 字节 | 原厂**只接受恰好 32 字节**（daemon 452、CC 3635、all 1846），三份代码里**没有任何 48 字节分支**；`LEDCtrl.cs:19` 的 `evBytesLength=32` 只是常量。重写版同样只认 32 并额外打印长度异常负载，优于原厂。 | 确定 |
| 10 | 本机通信方式 | 重写版 `GmWmi.TryInvoke` 沿用 daemon 的「SetPropertyValue+Put → 重新查询读回」范式，结构布局与原厂一致；两个可复刻差异：**原厂每次 SMI 都重新枚举 WMI 对象（不缓存）**、**BIOS 变体用 `ManagementClass["BufferBytes"]` 单例写法**。 | 确定（差异）；影响=不确定 |

---

## 1. 原厂证据边界说明

- `decomp/ControlCenterDaemon/ControlCenterDaemon/ControlCenterDaemon.cs`（下文简写 **D:**）与 `decomp/Daemon/ControlCenterDaemon.all.cs`（**ALL:**）的 `ParseWMIEventData` 函数体**逐字相同**（已做切片字符串比较：ALL:1841-1980 == D:447-586），`SetEventStatus`、Stop/Restart/Close 语义也相同；ALL 只是同一反编译产物的更大拼合版本（多出常量等成员，行号偏移）。**因此不存在"两个版本语义打架"的情况**，下文以 D 为准，ALL 只标注行号。
- `decomp/ControlCenter/ControlCenterSpace.BIOS/*` 中**没有** `ParseWMIEventData`/`GMC_WMIEvent`/`SetEventStatus`（全目录 grep 0 命中），Fn 解析只有 `ControlCenterSpace/MainWindow.cs` 一份；BIOS 目录差异集中在 LED/SMI/UI（例如 `BIOS/MyWmiBase.cs` 是另一套 `WMI_Query` 探测 + `ManagementClass` 调用范式，`BIOS/SMI_STRUCT_S.cs` 的 `rev0/rev1` 改名为 `rev7a/rev7b`，偏移不变）。对本机（SKU 52、SMI 版本 512）走的是 daemon 那套。
- 现场记录 `.workbuddy/memory/2026-10-19.md` 是**用户人工观察/实验记录**，不是原厂证据；引用处会单独标注。

---

## 2. 逐项差异（差异 → 证据 → 风险 → 建议）

### 2.1 事件接收与 32 字节校验

| 项 | 原厂 | 重写 |
|---|---|---|
| 订阅 | `WqlEventQuery("GMC_WMIEvent")` + `ManagementScope("root\\wmi")`，`EventArrived += Handler`（D:412-433、CC:3597-3616） | 同（`FnKeys.cs:92-112`） |
| 长度 | 仅 `if (32 == evBytes.GetLength(0))`，否则只打日志（D:452/575-578、CC:3635/3775-3778、ALL:1846/1969-1972） | `Parse` 仅长度==32，否则 `OnArrived` 打印完整负载（`FnKeys.cs:158`、`FnKeys.cs:141-146`） |
| 异常 | `HandleWMIACPIEvent` 外包 try/catch 打 HRESULT（D:435-445） | `OnArrived` 全包 try/catch 并写日志（`FnKeys.cs:149-152`） |

**结论**：这一层重写版**不落后**，甚至更好（长度异常留痕）。无补丁必要。

### 2.2 `Event_Alpha` 门控的真实作用（重点）

**原厂代码（daemon 版，D:447-585）：**

1. `Event_Alpha` 初值 255（D:79）。
2. 先算 `flag` = `b[4..8]` 全 0（即"只有背光在变"），D:459-465。
3. 若 `flag`：若 alpha 还是 255 则取 `b[0]`，然后无条件应用背光亮度 `SetAlphaChangeData(b[0])`（D:466-474）。
4. **门控**：`if (Event_Alpha == evBytes[0]) { ...HDMI/适配器/摄像头/性能/控制中心... } else { Event_Alpha = evBytes[0]; }`（D:475、D:570-573）。
5. CC 版顺序相反：**先**无条件 `if (Event_Alpha == 255) Event_Alpha = b[0];`（CC:3637-3640），**再**进同一门控（CC:3667、CC:3770-3773）。

**确定性结论：**

- `b[0]` 就是"当前键盘背光档位"：`SetAlphaChangeData(evBytes[0])` 把它当亮度写进 LED 分区（D:630-642、CC:2624-2674），回读同样用 `0xFA00/0x0200` 的 `a6` 作为 `M_Alpha`（CC:3061-3079）。日志也印证：`[02 07 …]` 出现在背光=2 时，背光关掉后性能键负载变成 `[00 07 …]`（leishen.log:19、:23-25、:29）。
- 门控**不是去重**：同一次按键的两条相同负载，`b[0]` 相同，两条都能通过门控并被完整处理（D:475 只比较 alpha；性能分支内无任何去抖，D:533-556）。也就是说**原厂对重复事件是"来几条执行几次"**。
- 门控的真实行为是：**负载里的背光值与我们缓存的背光值不一致时，整条事件只用于刷新缓存、不执行任何按键动作**。可以理解为"EC 给的是整帧状态快照，快照里的背光字段对不上，说明帧已过期/被别的属主改过，先不做动作"。
- 两个原厂宿主行为不同（这是静态代码事实，不是猜测）：
  - **daemon（实际在后台负责热键的进程）**：首条事件如果是**非背光**事件，会走 else 分支只同步 alpha（D:570-573）→ **这条按键被吞**；首条若是背光事件则先初始化 alpha（D:466-471）再通过门控。
  - **CC UI**：先把 alpha 初始化（CC:3637-3640），所以**首条事件不会被吞**。
  - 含义：原厂 daemon 启动后第一次按 Fn+1/Fn+3 **可能无反应**，这是原厂已有的小毛病，不是重写引入的。

**重写版：** `_alpha` 只在首次事件初始化（`FnKeys.cs:160`），其余事件仅执行一个**空操作**（`FnKeys.cs:188-192` 的 if 体内只赋值），既没有门控也没有被使用 → 属于死状态。**风险**：无功能风险；但失去了一条可用的"快照是否新鲜"判据（可用于去重，见 §3），且代码会误导后续维护者。

**建议**：二选一，不要保留死代码：
- 方案 A（省事）：删掉 `_alpha/_alphaInit`（`FnKeys.cs:85-86,160,188-192`），并在注释里写明"原厂 alpha 门控的作用与 daemon 吞首事件行为，本实现有意不复刻"。
- 方案 B（复刻 + 利用）：按 **CC 版**顺序实现（先初始化再门控，不吞首条）；把"`b[0] != 缓存背光`"作为去重算法的**新事件信号**（见 §3.2 判据 3），而不是像原厂那样直接丢事件。

> 20:37 快照现状：`_alpha/_alphaInit` 仍在（`FnKeys.cs:105-106,236,253,264-268`），仍只写不用——与 20:31 快照相同，属于 §2.10 未完成项。

### 2.3 事件分类规则（onlyBrightness / 优先级 / 分类条件）

| 环节 | 原厂 | 重写 | 差异/风险 |
|---|---|---|---|
| onlyBrightness | `for j in 0..4: if b[4+j]!=0 flag=false`（D:459-465、CC:3652-3659） | 同（`FnKeys.cs:162-167`） | 无差异 |
| 背光 | flag 为真即应用亮度；CC 额外要求 `!IsHideToTray && !IsNonKeyboardLED && IsKeyboardEvent && !LockLEDCotrol`（CC:3645-3665） | 直接 `ApplyBacklight`（`FnKeyService.cs:72-74,172-186`），总是应用 | 语义差异：原厂在 CC 隐藏到托盘时由 daemon 负责背光、CC 不重复写；单进程无需区分，可接受 |
| 优先级 | `b[4]==1` → HDMI；`b[5]==159` → 适配器；`b[6]!=0` → 摄像头；`b[7]!=0` → 性能；`b[8]!=0` → CC（D:477-568、CC:3673-3768） | **完全相同顺序**（`FnKeys.cs:181-186`） | 无差异 |
| 48 字节 | 不存在 | 不存在 | 无差异 |
| 未知值 | 无显式 Unknown 分支；`b[4]!=1` 且 b[5..8] 条件不中时**静默什么都不做**（D:477-568 的 else-if 链） | 有 `FnKeyKind.Unknown`；20:31 快照 switch 无分支、无清位，20:37 快照已加 default 日志（`FnKeyService.cs:307-310`）但仍不清位 | 见 §2.8 |

**性能键原厂语义补充（重要）**：原厂对 `b[7]!=0` **完全不看 `b[1]`**，一律按"切换性能模式"处理，模式来源是 registry `POWERPLAN`（0=高性能/1=游戏/2=办公），下一档 = `(cur!=0)? cur-1 : 2`（D:538-553、CC:3736-3760）。重写版把 `b[1]==0x07` 解释为 Fn+1 强冷、其余走模式循环（`FnKeys.cs` 注释 `:32-35`、`FnKeyService.cs:148-163`）。这是**重写版新增行为，不是原厂语义**；保留可以（用户明确要求 Fn+1=强冷），但应写进文档，别在"复刻原厂"名义下误解。

### 2.4 事件清除（0xFB00/0x0400）时机、位值与缺失分支（重点）

**原厂清除原语**（D:644-665、CC:3787-3816、ALL:2038-2059）：

```
data.clear(); data.a0 = 64256(0xFB00); data.a1 = 1024(0x0400);
case 2: data.a2 = 1   // 性能键
case 3: data.a3 = 1   // 控制中心键
case 4: data.a4 = 1   // 摄像头键
ACPIPerformSMI();
```

即：**一次 SMI 只置一个位字段（其余为 0），值固定为 1**；清除后**不校验返回值**（daemon 直接丢弃 `ACPIPerformSMI()` 结果 D:664；CC 会打 "WMI Error" 但仍不回滚 CC:3808-3815）。只有这 3 个事件 ID 存在确认动作，全工程 SMI `a1` 常量只有 0x0100/0x0200-0x0207/0x0300/0x0400（穷举 `data.a1 = …`），**没有任何"读事件状态"的 GET 命令**。

**原厂清除缺失/条件不完整的分支清单（逐分支证据）：**

| 分支 | 清除位置 | 缺失情形 | 证据 |
|---|---|---|---|
| 性能键 | 只在动作块内、动作后 | `IsOnDCMode \|\| IsTypeCAdapterIn` 时**整块跳过 → 不清位** | D:536-555（clear 在 554，guard 在 536）、CC:3734-3762（clear 3761） |
| 摄像头 | 只在有效切换后 | ① `CameraStatus == b[6]`（状态未变/回声）不清；② `CameraStatus == 0`（不支持）不清；③ 找不到 HW 设备不清；④ `b[6]` 非 1/2 不清；⑤ alpha 门控失败不清 | D:498-531（clear 509/516 在 498 条件内）、CC:3695-3729 |
| 控制中心键 | 启动 UI 之后（CC 版 UI 已存在也清） | 若 `StopMonitorWMIEvent`/`ExecuteControlCenter` 抛异常，外层 catch 吞掉，**清位被跳过** | D:557-568 + catch 580-583；CC:3764-3768 |
| HDMI / 适配器 | **从不发 0x0400** | 原厂认为这两类不需要确认（只读 GET 0x0203） | D:477-494、CC:3673-3692（分支内无 SetEventStatus） |
| 未知/无匹配位 | **从不发** | 无 else 兜底 | D:477-568 链尾 |
| alpha 门控失败 | 不清，只同步 alpha | 任何 `b[0]` 与缓存不符的事件 | D:570-573、CC:3770-3773 |
| 启动/关机 | 不做残位清理 | daemon 启动时不清 2/3/4；退出只 Dispose watcher | D:156-221、D:223-236 |
| 首条非背光事件（daemon） | 不清 | alpha 未初始化时被吞 | D:468-475 + 570-573 |

**重写版（`FnKeyService.cs:62-116`、`GmWmi.cs:246-252`）：**

- 性能键：`Hub.Wmi.ClearEvent(2)` 在**回声判定之前、动作之前**执行（`:82`）→ 含回声在内"总是清"；
- CC：`ClearEvent(3)`（`:90`）；摄像头：`ClearEvent(4)`（`:96`）→ 也总是清（比原厂更安全）；
- HDMI/适配器：不清（`:99-107`，与原厂一致）；Unknown：不清；背光：不清（与原厂一致，EC 未定义其确认）；
- `ClearEvent` 位值映射与原厂一致（`GmWmi.cs:246-252`：id==2→a2、3→a3、4→a4，值 1）；
- **但**：`ClearEvent` 返回值所有调用点都被忽略，失败无日志、无重试；`GmWmi.TryInvoke` 异常时 `_instance=null` 直接返回 false（`GmWmi.cs:139-144`）。
- **20:37 快照的变化**（并发更新后）：清位移入 `OnArrived` 的 `finally`（`FnKeyService.cs:318-323`），且只清负载中实际出现的位（`FnKeyService.cs:349-355` + `GmWmi.cs:253-267` 的 `ClearEventBits(perf, cc, camera)`）——这正好补上了原厂"分支内清位、异常/门控失败就不清"的缺陷。**仍未做**：返回值校验/重试/日志、启动与 Resume 后的残位清理；另外 `ClearEventBits` 是**一条 SMI 同时写多个位**，原厂从未这样用过（原厂每条只写一个字段，D:649-663），组合位语义未验证——若实测无副作用可接受，但更稳的做法仍是"顺序发 3 条单字段 SMI"。

**风险**：若 EC 是"事件位挂起等待确认，确认不到就不抬新事件"的模型，则**一次清除写入失败就可能永久静默**（与现场"只能重启复位"完全吻合）；而重写版没有任何失败可见性。反过来现场记录又显示"手动发 0x0400（a2=a3=a4=1）后仍不复位"（`.workbuddy/memory/2026-10-19.md:59`）——说明单靠清位不一定够，但**这不能证明清位写入本身成功到达了 EC**（当时只"清位成功"没有定义校验方式，`ClearEvent` 返回值也没被看）。因此该假设**不确定，必须靠补丁里的可观测性来判定**。

**建议补丁（P0）**：把清位改为"可观测、可重试、永不跳过"：
1. `FnKeyService.OnArrived`：把清除逻辑集中到 `finally`/统一收尾；2/3/4 事件无论动作走哪个分支（含 DC/TypeC、摄像头状态未变、未知值、Unknown）都至少尝试一次确认；记 `bool ok = Hub.Wmi.ClearEvent(id)`，失败重试一次，仍失败写 `Hub.Write("Fn 事件确认失败 id=… err=…")`。
2. 增加 `GmWmi.ClearAllEvents()`：**顺序发 3 条 SMI**（ClearEvent(2)、ClearEvent(3)、ClearEvent(4)），不要像现场手工实验那样一条 SMI 里三个字段都置 1（原厂从未这么用过，语义未知）。
3. `FnKeyService.Start()` 成功后、以及系统 Resume 后，各调用一次 `ClearAllEvents()`（清掉可能的残位）；退出前也可选调用。
4. 若清位失败连续 N 次（如 3 次），触发"监听自愈"：Stop→重建 watcher（见 P2-2），并记录。

### 2.5 DC / TypeC 门控

- 原厂：性能键动作前 `if (!IsOnDCMode && !IsTypeCAdapterIn)`（D:536、CC:3734）。`IsOnDCMode` 来自 `SystemInformation.PowerStatus.PowerLineStatus`（CC:1344-1357）；`IsTypeCAdapterIn = DetectAdapterStatus()`，后者读 `GET 0xFA00/0x0206`（a1=518）的 **a4==1**（CC:5337-5353），且原厂会 `Thread.Sleep(100)` 后重试最多 5 次（CC:1358-1365）。
- 重写版：`FnKeyService.cs:76-86` 无任何门控；`Hub.ReadCaps` 读 `GET 0x0202` 的 `a3`（AC 适配器，`Hub.cs:104-111`），**没有读 TypeC（0x0206 a4），也没有跟踪 DC 状态**；`Hub.GetFanControl` 只取 0x0206 的 a2（`GmWmi.cs:192-196`）。
- 风险：本机日志全程 AC（`硬件状态: 适配器=1`，leishen.log:2 等），所以实测没暴露；但在电池/TypeC 供电场景，重写版会做原厂明确禁止的模式切换（原厂此时是"不动作、也不确认"，重写版至少会确认）。功能上可接受甚至更好，但**不是原厂语义**。
- 建议（P1）：补 `Hub.IsOnDc`（`SystemInformation.PowerStatus`）与 `Hub.IsTypeCAdapterIn`（GET 0x0206 a4，按 CC:5337-5353 的 100ms 退避重试），**只用来抑制"动作"，绝不用来跳过"确认"**（原厂在这一点上做了坏示范）。

### 2.6 摄像头分支

- 原厂：先比状态，只有"支持（CameraStatus!=0）且上报值 != 当前状态"才切换并确认；状态未变时不动也不清；不支持时只打日志；找不到设备时弹框；值非 1/2 时弹框（D:495-532、CC:3693-3730）。切换成功后更新 `CameraStatus = b[6]`。
- 重写版：只要 `b[6]!=0` 就 `ToggleCamera(b[6])`，`CameraService.SetEnabled(1 或 2)` 是"设置目标状态"→ 幂等，重复事件不会来回翻（`FnKeyService.cs:94-97,188-195`）。但**没有同步 `Hub.CameraStatus`**，只能等 5s 轮询修正（`Hub.cs:187-197`），期间界面状态可能短暂滞后。
- 实测注意：本日志里**没有任何一条摄像头 Fn 事件**（"Fn 键事件"仅 38 条性能键 + 26 条背光键），而 `摄像头状态变化` 是 5s 轮询读到 EC 状态变化（leishen.log:114-115、145-146、164-165、173-174、185-186、191-192、242-245）。因此**摄像头热键分支在本机尚未被真实事件验证过**，"轮询到状态变化"不等于"收到 GMC_WMIEvent"。
- 建议（P1）：对齐原厂判据——动作前比较 `Hub.CameraStatus`（并处理 0/未知值），动作后更新 `Hub.CameraStatus`；确认照旧总是发。

### 2.7 HDMI / 适配器分支

- 原厂：
  - HDMI（`b[4]==1`）：若 `!IsSupportDDS`，读 `GET 0x0203`（a1=515）显示状态，`DisplayStatus==3` 时执行 `SetHybridMode()`（daemon D:479-489；CC 还会更新 UI，CC:3673-3686）。
  - 适配器（`b[5]==159`）：daemon 只打日志（D:490-494），CC 弹"适配器警告"（CC:3687-3692）。
- 重写版：两者都只是 `Hub.ReadCaps()`（`FnKeyService.cs:99-107`），**没有 DisplayStatus==3 → SetHybridMode 的动作**，也没有适配器提示。
- 风险：外接 HDMI 时，原厂会在特定状态自动切混合模式；重写版只刷新显示输出信息。属于功能缺口，但不影响 Fn 通道稳定性。
- 建议（P2）：要么补齐 `SetHybridMode` 路径（需谨慎，会写 0xFB00/0x0203），要么在 README/注释明确"HDMI 事件仅刷新状态，不自动切模式"。

### 2.8 Unknown 事件是否清位（风险判断）

- 原厂"Unknown"没有分类；能进入 "无匹配位" 的负载示例：`b[4]==2`（非 0/1）、`b[5]!=159`、`b[6]==0`、`b[7]==0`、`b[8]==0` 的组合。原厂此时**什么都不做、也不清位**（D:477-568 链尾无 else）。
- 重写版行为相同（`FnKind.Unknown` 无分支）。但重写版**有 Unknown 分类却没有日志**：`Hub.Write("Fn 键事件: " + ev.Describe())` 会打，但不会标出"'未知'类型未确认"。
- **风险**：如果 EC 对某些未文档化的事件位也要求确认，残留位会抑制后续事件（与 §2.4 的停报假设同源）。当前无法证明 EC 需要；但"多清一次未置位的位"按原厂语义是安全的——原厂每次确认都会把未涉及字段写 0（D:646-663），说明"对未挂起的位写 0/1"在协议里是常态。
- 建议（P1）：Unknown 时 `Hub.Write` 明确告警，并调用 `ClearAllEvents()`（或至少 3 个 ID 都发一次）；同时统计"Unknown 出现频率"，若为 0 说明本机固件不产生该类事件，可回收该逻辑。

### 2.9 日志证据：实测事件序列与统计（用于 §3 去重决策）

统计脚本：按 `Fn 键事件:` 行解析时间戳 + 前 9 字节负载；"相邻同负载间隔"= 同类型事件里与上一条**完全相同前 9 字节**的间隔。

日志共 64 条 Fn 事件：性能键 38 条、背光键 26 条。

**背光键（天然幂等，可作"EC 回声"标尺）**：26 条 = 13 次按键 × 每条 2 次；相邻同负载间隔（ms）：

```
190, 191, 194, 197, 199, 210, 253, 265, 275, 330, 336, 355, 375
```

→ 全部落在 **190–375ms**（中位 253ms）。人工不可能 200ms 连按两次，这是**确定无疑的 EC 回声**（同一按键发两条）。

**性能键**：38 条里相邻同负载间隔 35 个，分桶：

| 区间 (ms) | 条数 | 值 |
|---|---|---|
| 200–400 | 2 | 212, 225 |
| 400–800 | 0 | — |
| 800–1000 | 2 | 878, 887 |
| 1000–1500 | 7 | 1062, 1075, 1232, 1247, 1248, 1363, 1418 |
| 1500–2000 | 1 | 1884 |
| 2000–2500 | 1 | 2380 |
| ≥2500 | 18 | 2587, 2910, 3069, 3151, 3513, 4135, 4632, … |

**双峰分布**：≤375ms 与 ≥878ms 之间**没有任何样本**。200–400ms 那 2 条与背光回声量级完全一致，可判定为硬件回声；878–2500ms 这一组**无法从日志本身判定**是回声还是真实按键。

**关键序列 A（无去重时代的伤害，leishen.log:51-56）**：19:36:19.429 / .654 / .866 三条相同负载（间隔 225ms、212ms）**全部被执行** → 强冷"开→关→开"抖了三次。这说明：① 回声确实会以 200ms 级连发；② 不去重的确会把一次物理按键放大成多次动作。**这是加去重的正当理由。**

**关键序列 B（2500ms 窗口的伤害，leishen.log:211-239）**：20:20:19.888 起 20 秒内 13 条性能键事件。用当前算法（前 9 字节 + 距上次"已执行"事件 2500ms）逐条回放：

| 窗口 | 执行 | 忽略 | 执行时刻 |
|---|---|---|---|
| 2500ms（=现网代码） | 6 | 7 | 19.888, 24.437, 27.588, 30.846, 36.236, 39.963 |
| 1000ms | 11 | 2 | 19.888, 20.963, 24.437, 27.588, 29.968, 32.264, 33.326, 36.236, 37.468, 38.715, 39.963 |
| **600ms（建议默认）** | **13** | **0** | 全部执行 |
| 400ms | 13 | 0 | 全部执行 |

2500ms 模拟结果与日志里的 6 条"强冷键"执行行、7 条"同一按键的重复通知，已忽略"（leishen.log:217/219/225/229/231/235/237）**逐一吻合**，说明模拟模型正确。

**与现场记录对照**：`.workbuddy/memory/2026-10-19.md:67` 写"连按 Fn+1 十次，10 条事件全部到达；IsEcho 正确忽略 4 条回声"。但日志同一时段是 **13 条事件、执行 6 条、忽略 7 条**。若"连按 10 次"可信，则 2500ms 窗口**至少吞掉 4 次真实按键**；即便把用户认账的 4 条当回声，也还有 3 条无法解释。→ **2500ms 窗口的代价已经实测发生，不只是理论。**

### 2.10 并发更新后的当前实现（20:37 快照）对照

并行工作流在我分析期间改动了 Fn 相关文件。按当前工作树重新核对，本节列"已落地 / 仍未落地"，以便把本文结论直接映射成剩余补丁：

| 事项 | 20:31 快照 | 20:37 快照 | 结论 |
|---|---|---|---|
| 去重窗口 | 2500ms 写死 | **默认 700ms，可配** `Settings.FnEchoWindowMs`（`Settings.cs:266-289`），`IsEcho` 使用（`FnKeyService.cs:362-389`），日志带间隔（`:251,266-276`） | 方向正确；**仍**只比前 9 字节、未用 alpha 判据、未在其他事件到达时重置（见下） |
| 回声判定对象 | 仅性能键 | 同（Backlight 仍不去重，幂等，合理） | 一致 |
| 清位 | 分支内、动作前、忽略返回值 | `finally` 统一清，只清负载里出现的位，仍忽略返回值 | 已补原厂最大缺陷；仍需返回值日志/重试/启动残位清理；组合位清法待实测 |
| watcher 生命周期 | 只有 Start/Stop | `Stopped` + 30s 看门狗 + 退避重启 + 手工 Restart | 已落地 |
| DC 门控 | 无 | Fn+3 在 `GetSystemPowerStatus().ACLineStatus==0` 时忽略（`FnKeyService.cs:416-422,476-485`） | 部分落地；**仍缺 TypeC**（原厂 `GET 0x0206 a4`，CC:5337-5353） |
| Unknown | 无日志、不清位 | `default` 分支打印全 32 字节（`FnKeyService.cs:307-310`） | 部分落地；未清位 |
| 摄像头状态比对 | 无 | 无 | 未落地 |
| HDMI 自动切混合 | 无 | 无 | 未落地 |
| 全 32 字节比较 / alpha | 无 | 无 | 未落地 |
| SMI 读取频率 | ~3.6 次/s | **不变**（`MainWindow.xaml.cs:48-59`、`Hub.cs:121-152,159-213`、`FanControlService.cs:19,251-252`；30s 保活仍在 `FanControlService.cs:38,283-293`） | 未落地（§5.2） |
| 启动/Resume 清残位 | 无 | 无 | 未落地（§4.4） |

**当前实现仍存在的一个具体边界缺陷**（可直接复现于代码阅读）：`IsEcho` 只由性能键更新 `_lastPerfRaw/_lastPerfTime`，**其他 Fn 事件到达时不清空该状态**。于是"Fn+1 → 700ms 内按 Fn+2 调背光 → 再按 Fn+1"这一串里，第三次按键（新快照、新动作）会被判成第一次的回声而忽略。修法很简单：`OnArrived` 里对非性能键事件调用 `ResetEchoState()`（`FnKeyService.cs:391-398` 已有现成函数）。这也和原厂 alpha 门控的"状态一致性"思想一致。

**另注**：当前 `FnKeyWatcher` 的类注释引用了 `analysis/ghidra-driver-report.md`（另一路并行分析产物），说明"原厂控制中心在 `ControlCenter64.sys` 未加载时 Fn 键仍工作、热键通道只依赖 `root\wmi`"这一结论已被独立验证；本文 §2.1 的订阅路径结论与之一致。

---

## 3. 去重算法改造建议（含窗口取值、算法、代价、验证协议）

### 3.1 判断依据小结

1. **必须去重**（否则一次按键多次动作，log:51-56 已证实）；
2. 但去重的对象只应是**同一次物理按压的 EC 回声**，不能把人类第二次按键误判为回声；
3. 可靠的"回声"标尺 = 背光键的 190–375ms 双发（13/13 样本）；性能键 ≤375ms 的 2 条同量级；
4. 878–2500ms 这组**既不能证明是回声，也不能证明是按键**，用固定长窗口强行归类就是在赌；
5. **EC 侧没有"读事件位状态"的 SMI 命令**（原厂全部 a1 常量穷举：0x0100、0x0200–0x0207、0x0300、0x0400），所以题目里"事件位是否已被清除的判据"**不能直接读**，只能用"我们刚刚成功发出过确认"这一**间接判据**；
6. 现有代码 `ClearEvent` 结果被丢弃，连"确认是否发出"都不可知 → 无法作为判据。

### 3.2 建议算法（分层，默认保守但可配置）

```
状态：lastAccepted[kind] = { payload32, time, ackOk }
      lastAlpha  (来自原厂 Event_Alpha 语义)

OnArrived(ev):
  ackSent = AckAlways(ev)          // 2/3/4 无条件确认，记录返回值；见 P0-1
  if kind != Performance:  lastAccepted = null; return NormalHandle(ev)   // 任何其他事件都重置回声状态

  Δ = now - lastAccepted.time
  same9  = 前9字节相同
  same32 = 全32字节相同
  alphaChanged = ev.b[0] != lastAlpha      // 快照新鲜度信号（原厂门控的语义）

  if lastAccepted == null || !same9 || Δ > LongWindow(2500):   -> 真实按键
  elif Δ <= ImmediateWindow(600ms):                            -> 回声，丢弃（记 debug 日志含 Δ）
  else:                                                        // 600–2500ms 灰区
      if alphaChanged || !same32:                              -> 真实按键（快照变了=新动作）
      else:                                                    -> 默认按"真实按键"执行，但记 WARN
                                                                  "疑似回声已执行 Δ=…ms"，
                                                                  供后续统计校准
  执行后更新 lastAccepted = {payload, now, ackOk}
```

要点：
- **保留一次"立即回声"短窗**：600ms（>背光回声上界 375ms，留 1.6× 余量）。当前工作树已用 700ms（`Settings.FnEchoWindowMs`），同量级，可以保留——**关键不是 600 还是 700，而是比较维度（9 字节 vs 32 字节）和状态重置**；
- **灰区默认"执行"而不是"丢弃"**：用户的痛点是"真实按键被吞"，而一次按键动作两次的代价（强冷开了又关）用户能从界面看到并可再按一次纠正，吞按键则是完全无反馈；
- **用原厂 alpha 语义做辅助判据**：若 `b[0]` 与缓存的当前背光不一致，说明这是**新的状态快照**，不可能是上一条的重复（因为回声负载与上一条相同）→ 直接执行。这条判据是白捡的（原厂已经有这个状态，重写版现在没接上）；
- **改用全 32 字节比较**（20:31 快照只比前 9 字节 `FnKeyService.cs:134-138`；20:37 快照仍是 9 字节 `:376-379`）：`--fnprobe` 已经记录全 32 字节（`App.xaml.cs` 的 fnprobe 分支，20:37 快照还加了 `间隔=` 与 `[GMC]/[WMI2]` 标记），只要采集一次就能验证 EC 是否在字节 9–31 放序号/计数器；**若存在差异字节，直接用"全 32 字节相等"做回声判据，窗口可以放宽到 2500ms 也不误伤**——这是最优解，且成本极低；
- **任何其他 Fn 事件（背光/摄像头/CC/HDMI）到达时重置 `_lastPerfRaw/_lastPerfTime`**（`ResetEchoState()` 现成，只需在 `OnArrived` 调用）：物理上不存在"按键 A 的回声夹在按键 B 之后"的合理情形；不重置会误判（§2.10 边界缺陷）；
- 窗口保持可配（`Settings.FnEchoWindowMs`，默认 700、上限 5000 已实现），方便现场 A/B，不用重编译。

### 3.3 代价（必须写清楚）

| 方案 | 代价 |
|---|---|
| 2500ms 固定窗（现状） | 2.5s 内的真实第二次按键被静默吞掉；实测已发生（§2.9 序列 B）。 |
| **600ms 短窗（建议默认）** | 若 878–2500ms 那批事件里确实有"回声"，一次按压会被执行两次 → 强冷会开一下又关/模式连跳两档；用户能从界面发现；比"吞按键"可恢复。 |
| 600ms + alpha 判据 + 全 32 字节 | 同上，但灰区误判概率下降，且每次误判都有 WARN 日志可追。 |
| 双发配对（"每次按压固定 2 条"→ 每 2 条执行 1 次） | 前提是固件严格 2 条/次；日志里 19:33:01 只有 1 条性能键事件，**不满足**，不可靠；且要延迟到 burst 结束（≥600ms）才动作，响应变慢。**不推荐**。 |
| 状态机"确认后再允许新动作" | 需要读事件位，协议不支持；只能拿 `ClearEvent` 返回值近似，见 P0-1。 |

### 3.4 判定实验（用现有 `--fnprobe`，一次采集就能定窗）

1. 用 `LeishenCC.exe --fnprobe 180`（`App.xaml.cs:133-153`，无窗口、写 `fnprobe.log`，含**全 32 字节**）；
2. 人工打标签（同时写一个 txt，记录每次按压的秒级时间/动作）：
   - A 组：单按 20 次，间隔 ≥3s；
   - B 组：每次连按 2 下，两下间隔 ~1s，共 10 组；
   - C 组：每次连按 2 下，两下间隔 ~2s，共 10 组；
3. 用 A 组得到"一次按压产生几条事件、间隔分布"；用 B/C 组检验"能否区分第二下按键"；
4. 用全 32 字节比较 A 组内回声与新按键是否可区分。
5. 根据结果把 `EchoWindowMs` 定在"A 组回声上界 × 1.5"；若 32 字节可区分则直接切到全字节判据、窗口退化为安全网。

---

## 4. 事件清除/延迟序列与"性能键停报"（重点，含确定性边界）

### 4.1 从日志能确定的事实

1. **不是程序丢事件**：19:42:54.831 之后到 19:54:41.382 之间没有任何性能键事件，但 19:45:43–19:45:56 背光事件连续正常到达（leishen.log:107、:116-139、:153）；现场还用独立 `ManagementEventWatcher` 复现了"同时刻背光有、性能键空"（`.workbuddy/memory/2026-10-19.md:57-60、:70-73`）。→ EC/WMI 侧只停掉了 b[7] 事件。
2. **重启能恢复，且恢复后只正常 1–3 次**（memory:60、73；日志侧 19:54:41 / 19:55:12 / 19:55:14 三条后再次静默至 20:18）。
3. **0x0400 清位没有复活它**：用过 a2=a3=a4=1（memory:59）；恢复依赖重启——但该实验没有校验 SMI 是否真的到达 EC，所以**不能据此排除"确认失败"假设**。
4. **原厂 daemon 启动后按键恢复过**（memory:65，20:04:13 启动 → 20:04:59 恢复）：说明它不是"只能断电"的硬闩锁，至少存在一种软件路径能解开（原厂 daemon 会做什么？启动时 `GetSupportStatus`(0x0201)/`GetHardwareStatus`(0x0202)/`SetInitialFanControl`，并在收到首条事件后 `SetEventStatus(2,1)`；其中最后一项正是"补确认"）。
5. **降写频后 20:20 一轮连按事件全到**（memory:64-68；日志 20:20 那段"已忽略"是去重造成，不是停报）。

### 4.2 三个候选机制与判定

| 假设 | 支持 | 反证/疑点 | 判定 |
|---|---|---|---|
| H1 EC 性能键事件位挂起，等待 0xFB00/0x0400 确认；确认写入曾失败/丢失 → 后续事件被抑制 | 与"只能重启"吻合；原厂 daemon 启动后恢复可以用"它补发了一次确认"解释；重写版**不检查清位返回值**（`FnKeyService.cs:82`、`GmWmi.cs:246-252`），失败完全不可见 | memory:59 手动清位无效 | **最值得先排除**：加返回值日志/重试/启动清残位后若停报消失，即证实 |
| H2 高频 SMI 写（尤其 0xFB00/0x0205）挤占 EC 热键任务 | 20:09 起降写频后一整轮按键全到（memory:67）；原厂只在 10℃ 档位变化时写（D:1015-1026），写频远低于重写版旧实现 | 19:44 故障发生时程序已"降写"？按 memory:64 时间线，20:08:35 才改版，19:44 时仍是每 2s 无条件写 → 不能算反证；但 20:02 "关程序 + 静置 60s 仍 0 事件"（memory:71-73）说明**一旦停报，停写也救不回来**，与 H1 的"闩锁"叠加 | **保留**：按"降低 SMI 总流量"改进，但需 A/B |
| H3 其他一次性固件/状态闩锁（与某一 SMI 序列、供电/TypeC 事件相关） | 现象"重启后 1–3 次即复发"像固件状态机 | 没有可控实验能定位 | **不确定，只能靠可观测性+自愈兜底** |

### 4.3 与"确认时机/位值"的对照结论

- 重写版确认位值/字段映射**与原厂一致**（`GmWmi.cs:246-252` vs D:644-665）→ 位值不是问题。
- 时机差异：原厂**先动作、后确认**（D:554/509/516/567、CC:3761/3707/3714/3767）；重写版**先确认、后动作**（`FnKeyService.cs:82`、`:90`、`:96`）。先确认对"尽快让 EC 看到确认"更有利，**建议保留先确认**，但要写进设计说明（属于有意偏离）。
- 真正缺的是：**失败可见性 + 重试 + 启动/恢复清残位 + 自愈**（P0-1/P0-3）。

### 4.4 建议补丁（P0/P1）

1. **P0**：`FnKeyService.OnArrived` 统一收尾确认，检查返回值、失败重试+日志；Unknown 也确认（`GmWmi.ClearAllEvents()` 顺序 3 条）。
2. **P0**：`FnKeyService.Start` 成功后 `ClearAllEvents()`；`SystemEvents.PowerModeChanged` 的 `Resume` 事件里 `ClearAllEvents()` + 重建 watcher。
3. **P1**：把 SMI 总流量降到原厂量级（§5）。
4. **P1**：自愈：连续 3 次确认失败，或 watcher `Stopped` 事件触发，或用户手动点"修复 Fn 通道"→ Stop/重建 watcher + ClearAllEvents。
5. **P2**：给 EC 通道加"黑匣子"：最近一次确认时间/结果、事件计数、丢弃计数，SystemView 可见（`Views/SystemView.xaml.cs:97-161` 已有 Fn 区，加三行即可）。

---

## 5. 通信方式对比（SMI / WMI 调用、频率）

### 5.1 通道实现

| 项 | 原厂 daemon/CC | 原厂 BIOS 变体 | 重写 |
|---|---|---|---|
| 结构布局 | `SMI_STRUCT_S`：a0(2) a1(2) a2..a6(4) rev0/rev1(4) = 32B（`decomp/.../SMI_STRUCT_S.cs:3-21`） | 同名，`rev0/rev1` 改名 `rev7a/rev7b`，偏移不变（`BIOS/SMI_STRUCT_S.cs`） | `struct Smi` 同布局（`GmWmi.cs:10-47`），rev 字段省略但写 0 → 等价 |
| 调用 | 每次 `new ManagementObjectSearcher(scope, SelectQuery("RW_GMWMI"))` → `SetPropertyValue("BufferBytes")` + `Put()`；随后**重新枚举**读取（`MyWmiBase.cs:62-124`） | `(byte[])new ManagementClass("root\\wmi:RW_GMWMI"){["BufferBytes"]=bytes}["BufferBytes"]` 一次往返（`BIOS/MyWmiBase.cs:67-103`，关键行 86） | 缓存 `_instance` 只 `Put()` 一次；读回再 `new ManagementObjectSearcher("SELECT BufferBytes FROM RW_GMWMI")`（`GmWmi.cs:83-121`、`123-146`） |
| 字节序 | `byte_swap(..., swap=false)` 为恒等（`SMI_STRUCT_S.cs:97-129`） | 同 | 直接 `BitConverter` 小端（`GmWmi.cs:15-40`）→ 等价 |
| 命令常量 | GET 0xFA00 / SET 0xFB00；GET 0x0200–0x0207、SET 0x0100/0x0200-0x0206/0x0300/0x0400（穷举 `data.a1`） | 同 | 完全覆盖（`GmWmi.cs:54-76`） |

**差异与建议**：
- 缓存 `ManagementObject` 是重写版独有的风险点：睡眠/恢复、WMI 提供者重载、驱动实例重建后，缓存的旧对象 `Put()` 可能"成功"但不再触发 ACPI 方法（无法从代码证明，属工程风险）。原厂两种范式都**每次重新获取**。
- **P1**：`TryInvoke` 每次调用重新枚举/或直接采用 BIOS 变体的 `ManagementClass` 单例写法；至少在 `PowerModeChanged(Resume)` 后强制 `Disconnect()` 再连接。
- 注意：本机实测读取正常（SMI 版本 512、SKU 52、温度/转速合理，leishen.log:1-3），说明当前范式**能用**，此条属于稳健性优化，不是"通信错误"。

### 5.2 SMI 流量（这是"与原厂完美匹配"最容易被忽略的一条）

原厂稳态：
- daemon 只有用户自定义风扇开启时才起 `timer2`，**5s** 一次，`GetHardwareInformation`（GET 0x0200）+（三风扇时）`GetHardwareInformation2`（GET 0x0207）→ **0.4 次 SMI/s**（D:829-836、838-859、889-921）。
- daemon 写 0x0205 只在 CPU/GPU/SYS 温度跨 10℃ 档位时（`MonitorAndControlFan` D:933-1027，写调用 D:1015-1026）→ 实际接近 0 次/s。
- CC 的 `timer2` 也是 5s，且 `refresh_info` 只在"系统页可见"时才读 EC（CC:944-945、1704-1707、1782-1801）→ ≤0.4 次/s。

重写版稳态（当前代码）：
- `MainWindow` 1s 定时器 → `Hub.Poll()` → `ReadThermal` = **2 次 SMI/s**（`MainWindow.xaml.cs:48-55`、`Hub.cs:121-152`、`GmWmi.cs:174-189` 里 0x0200 + 0x0207 各一次）。
- `FanControlService` 2s 定时器 → `DeliverNow` → `ReadThermal` = **1 次 SMI/s**（`FanControlService.cs:19`、`:98-99`、`:223-280`、`:251-252`）。
- `Hub.RefreshStates` 5s → 0x0203/0x0206/0x0202 = **0.6 次 SMI/s**（`MainWindow.xaml.cs:57-59`、`Hub.cs:159-213`）。
- 合计 ≈ **3.6 次 SMI/s，约为原厂 daemon 的 9 倍**；每次 SMI 还是"写+读"两次 WMI 往返。写侧虽然已从"每 2s"降到"变化才写"，但 **30s 保活**仍在写（`FanControlService.cs:33-38,282-293`），原厂是"档位不变永不写"。

**建议（P1）**：
- EC 温度/转速读改为 **2–5s 一次**（UI 1s 刷新时复用缓存值，NVML/CPU 负载仍 1s 采）；
- 风扇服务复用同一份缓存，不要独立再读一次 EC；
- 30s 保活去掉或延长到 ≥5 分钟（原厂无保活）；
- 目标：SMI 总次数 ≤0.5/s，接近原厂。预期收益：降低"EC 热键任务被挤掉"（H2）的概率；风险：极端情况下外部改动 EC 后最长 5s 才纠正（原厂也是 5s，可接受）。
- 可观测性：在 `Hub` 加 SMI 计数/耗时，`--fnprobe` 或诊断页可见。

---

### 5.3 原厂启动/关闭的 EC SMI 序列（只挑与热键上报相关的部分）

**daemon 启动（`/1` 常驻模式）**（D:156-221）：

1. `GetSupportStatus()` → `GET 0xFA00/0x0201`：`a0>=262` 决定 `IsNewPowerMode`，`a0>=512` 决定 `IsSupporUserFan`，`a2∈{52,53}` 决定三风扇（D:238-268）。
2. `GetHardwareStatus()` → `GET 0xFA00/0x0202`：`a5=CameraStatus`、`a4`=HSR 支持、`a6`=DDS 支持（D:802-827）。
3. （支持用户风扇时）`SetInitialFanControl()`：读注册表 16/17，**启动 5s 的 timer2**（D:184-187、838-859）。
4. `PowerLineStatusCheck()`：AC/DC 判定，必要时 `power_*_click_func → refresh_power_plan → SetPowerMode(0xFB00/0x0300)`（D:214、1066-1097、1129-1163）。
5. `IsTypeCAdapterIn = DetectAdapterStatus()` → `GET 0xFA00/0x0206` 的 `a4`（D:215-219、1165-1179）。
6. `WatchRegistry()` 启动 registry watcher（D:220、749-788）。
7. **最后**才 `MonitorWMIACPIEvent()` 订阅热键（D:190-199）。

顺序要点：**先完成能力/状态查询（0x0201/0x0202/0x0206），再订阅 `GMC_WMIEvent`**。重写版顺序一致（`App.xaml.cs:102-112`：`Connect` → `ReadCaps` → `FnKeyService.Start`），这一点不需要改。

**CC 启动**（CC:836-1048）：写 `registry 14=2` 让出 daemon 监听（CC:862）→ `CheckBIOSVersion`（GET 0x0201，CC:3581-3595）→ `GetHardwareStatus`（CC:924）→ `PowerLineStatusCheck` + `DetectAdapterStatus`（CC:1003-1005）→ `refresh_info()` → 订阅/重启 watcher（CC:1033-1046）→ `MonitorAlpha()` 读背光回 `0x0200 a6`（CC:3061-3079）。

**关闭**：daemon `FormClosed → CloseMonitorWMIACPIEvent → Dispose`（D:223-236、620-628）；CC `Window_Closing` 写 `registry 14=1`（CC:2200）→ `Window_Closed` 里 `CloseMonitorWMIACPIEvent`（Dispose）+ `m_power.close()`（CC:2205-2216）。**原厂退出时不清 2/3/4 事件位、不额外写 EC**。

对热键上报的启示：
- 原厂**没有**"启动清残位"这一步（§2.4 表），所以如果停报确由残位引起，这属于原厂没覆盖、重写版应补的增强；
- 原厂启动只做 3 次 GET，重写版 `ReadCaps` 做 5 次 GET（多 0x0203/0x0204/0x0205）——一次性开销，可接受；
- 原厂稳态只有 timer2(5s) 的 2 次 GET（且仅在用户风扇开启时），重写版 ~3.6 次/s（§5.2），这是"通信方式与原厂不匹配"的主要量级差异。

---

## 6. watcher 生命周期对比与可复刻做法

### 6.1 原厂怎么做（证据）

- **两进程轮值监听**：daemon 常驻监听（D:190-199）；CC 打开时 daemon 通过 registry 信号让出：
  - CC 在 `Window_Loaded` 写 `registry 14 = 2`（CC:862）；daemon 的 registry watcher 收到后 `watcher_acpi.Stop()`（D:780-787）。
  - daemon 收到"控制中心键"时自己 `StopMonitorWMIEvent()`（D:565）→ `watcher_acpi.Stop()` 且 `IsStartToHook=false`（D:587-602）→ 启动 CC（D:387-401）。
  - CC 隐藏到托盘（`page_hidden`）**不**停 watcher，只停 timer（CC:3119-3129）；真正关闭时 `Window_Closing` 写 `registry 14 = 1`（CC:2184-2203）→ daemon 收到后 `RestartMonitorWMIEvent()`（D:766-788）重建监听；CC `Window_Closed` 里 `CloseMonitorWMIACPIEvent()` → `Dispose()`（CC:2205-2216、3836-3843）。
  - **原厂交接存在已知竞态（不要照抄）**：`Window_Closing` 在"点 X 隐藏到托盘"时同样会先把 `registry 14 = 1`（CC:2189-2200），daemon 的 `RestartMonitorWMIEvent` 在 `IsStartToHook` 仍为 true 时直接 `Start()`（D:604-618），而 CC 此时只是 Hide、watcher 并未停止（CC:3119-3129）→ 存在 **daemon 与 CC 同时监听**的窗口，一次按键可能被执行两次。重写版的单进程单 watcher 架构天然规避了这个问题，这是架构优点，不需要复刻交接协议。
  - CC 被重新拉起时若 daemon 还在监听：`Window_Loaded` 里 `RestartMonitorWMIEvent()` 或 `MonitorWMIACPIEvent()`（CC:1033-1046）。
- **单实例互斥**：daemon 用 `ControlCenter_NL5_tool`（D:270-292），CC 用 `NL5_tool`（CC:1228-1250）；第二个实例只负责把已有窗口置顶/拉起（`set_top()`）。
- **Stop/Start 复用**：CC 版 `StopMonitorWMIEvent` 只 `Stop()` 不置 `IsStartToHook=false`，`RestartMonitorWMIEvent` 直接 `Start()` 复用同一 watcher（CC:3818-3834）；daemon 版 Stop 后置 false、Restart 时重建（D:587-618）——**daemon 版这条路径会泄漏旧 watcher（Stop 后不 Dispose，重建新对象）**，属于原厂缺陷，不要照抄。
- **异常与恢复**：`MonitorWMIACPIEvent` 只 catch `ManagementException` 打错误码（D:427-430）；**没有** watcher `Stopped` 回调、没有 watchdog、Resume 后不重建（`SystemEvents_PowerModeChanged` 只处理电源计划，D:1047-1064）。

### 6.2 重写版现状与差距

- **20:31 快照**：`FnKeyWatcher.Start()` 先 `Stop()`（会 Dispose）再新建（旧 `FnKeys.cs:92-112`），`Stop()` 反订阅 + Stop + Dispose（`:114-128`）→ 生命周期本身干净，比原厂 daemon 好；`FnKeyService.Start/Stop` 幂等（旧 `:40-60`），SystemView 可手动开关（`Views/SystemView.xaml.cs:114-132`）。
- **20:37 快照**：上述生命周期缺口已补上——`watcher.Stopped` 事件（`FnKeys.cs:144,188-203`）、只认当前代订阅（`:205-207`）、`FnKeyService` 30s 看门狗 + 退避自动重订阅（`FnKeyService.cs:171-237`）、`Restart(reason)` 手工重建（`:151-169`）、`StartCount/StartedAt/RestartCount` 诊断字段（`:59-60,123-125`）。这与本报告 P2-1/P2-2 的建议一致。
- 仍缺：
  1. 无 Resume 重建（原厂也没有，但值得做；睡眠/唤醒后 WMI 事件订阅失效是常见问题）；`App.OnExit` 只 Dispose NVML，不 `FnKeyService.Stop()`（`App.xaml.cs` 尾部）；
  2. `Start()` 成功后没有清 2/3/4 残位（`ClearAllEvents` 未实现）；
  3. 无"监听权"概念：单进程设计天然避免了原厂的双监听/双重动作问题（原厂靠 registry 14 交接，且交接本身有竞态，见 §6.1），这是重写版架构优势，**不需要复刻交接协议**；但要把"同一时刻只有一个 watcher（Start 前先 Stop）"写进注释，防止以后又加一个监听者。

### 6.3 建议（P1/P2）

- P2-1：`FnKeyWatcher` 订阅 `_watcher.Stopped`；`FnKeyService` 增加 5s 轻量看门狗（仅在 `Settings.FnKeyEnabled && !Running` 或 `Stopped` 标记为 true 时重建，**不要每秒 Start**）。
- P2-2：`SystemEvents.PowerModeChanged(Resume)` → `FnKeyService.Restart()`（Stop + ClearAllEvents + Start）。
- P2-3：`App.OnExit` → `FnKeyService.Stop()`（可选 `ClearAllEvents()`）。
- P2-4：文档化"单进程单 watcher"约束（替代原厂 registry 14 交接），并说明原厂 `ControlCenter_NL5_tool` 互斥只用于防多开，不参与热键分发。

---

## 7. 修改建议清单（按优先级，具体到文件/函数/预期行为）

> 原则：**不引入新行为时不改动已实测正常的部分**；所有改动都要能被 `leishen.log` 观测。
>
> **当前状态（20:37 快照）**：P0-1 的去重窗口部分、P1-1 的电池门控（不含 TypeC）、P2-1/P2-2 的看门狗已由并行工作流落地；**仍未落地**：全 32 字节比较、alpha 判据、其他事件重置回声状态、清位返回值校验/重试、启动/Resume 清残位、摄像头状态比对、HDMI 自动切混合、TypeC 门控、SMI 读取降频、WMI 对象缓存。下表按状态标注。

### P0（先修，直接对应两个痛点）

| # | 文件/函数 | 改动 | 预期行为/验收 |
|---|---|---|---|
| P0-1 | `src/LeishenCC/Core/FnKeyService.cs` `OnArrived`、`IsEcho` | ① 2/3/4 确认改为"无条件 + 检查返回值 + 失败重试一次 + 日志"；② 性能键去重窗口改为短窗（**已落地 700ms 可配**）；③ 比较改为**全 32 字节**；④ 灰区默认执行并打 WARN；⑤ 非性能键事件到达时 `ResetEchoState()`（**未落地**）；⑥ `b[0]` 与缓存背光不同视为新快照（**未落地**，`_alpha` 仍是死代码） | **状态：部分**。剩余：清位返回值/重试日志、全 32 字节、重置、alpha。验收：日志出现"回声丢弃 Δ=…"；1s 内两次真实按键都动作；Fn+1→Fn+2→Fn+1 不误吞第三个 |
| P0-2 | `src/LeishenCC/Core/GmWmi.cs` | 新增 `ClearAllEvents()`：**顺序**发 `ClearEvent(2)`、`(3)`、`(4)` 三条 SMI（不要一条 SMI 里三字段置 1），返回逐条结果 | **状态：未落地**。已落地的是 `ClearEventBits(perf,cc,camera)`（多字段同条 SMI）；建议保留它用于"按负载位清"，另加顺序单字段的 `ClearAllEvents()` 供启动/恢复/Unknown 兜底 |
| P0-3 | `src/LeishenCC/Core/FnKeyService.cs` `Start`、`Stop`；`App.xaml.cs` | `Start` 成功后 `ClearAllEvents()`；`Stop` 记录原因；`Restart(reason)`（**已落地**，`FnKeyService.cs:151-169`）；`OnExit` 调 `Stop()` | **状态：部分**（Restart 已有；启动清残位、OnExit Stop 未落地）。验收：启动日志出现"清残位 2/3/4=ok" |
| P0-4 | `src/LeishenCC/Core/FnKeys.cs` | 删死代码 `_alpha`（20:37 快照 `:105-106,236,253,264-268`）或按 §2.2 方案 B 接入；`Parse` 增加 `b[0]` 语义注释 | **状态：未落地**。无行为回归，可读性/正确性提升 |

### P1（对齐原厂语义 + 降 SMI 流量）

| # | 文件/函数 | 改动 | 预期行为/验收 |
|---|---|---|---|
| P1-1 | `Hub.cs`（新增 `IsOnDc`/`IsTypeCAdapterIn`，`ReadCaps`/`RefreshStates`）、`FnKeyService.OnArrived` 性能分支 | 按原厂 `!IsOnDCMode && !IsTypeCAdapterIn` 只抑制**动作**；TypeC 读 `GET 0x0206 a4`（CC:5337-5353 的 100ms 退避），DC 用 `GetSystemPowerStatus/SystemInformation`。确认仍无条件发 | **状态：部分**（20:37 已有 DC 判断 `FnKeyService.cs:416-422,476-485`；缺 TypeC）。验收：电池下 Fn+3 不切模式、TypeC 供电下也不切 |
| P1-2 | `FnKeyService.OnArrived` 摄像头分支 | 动前比较 `Hub.CameraStatus`（0=不支持、相等=回声），动后更新 `Hub.CameraStatus` | **状态：未落地**。重复事件不再重复执行；界面状态即时刷新 |
| P1-3 | `FnKeyService.OnArrived` Unknown/HDMI/适配器 | Unknown：日志告警（**已落地** `:307-310`）+ `ClearAllEvents()`（未落地）；HDMI：补 `!SupportDds && DisplayMode==3 → SetHybridMode`（或明确注释"不自动切"）；适配器：日志/提示 | **状态：部分**。未知事件不会成为潜在残位；HDMI 行为与原厂一致或差异被明确记录 |
| P1-4 | `MainWindow.xaml.cs`（1s 定时器）、`Hub.Poll`/`RefreshStates`、`FanControlService` | EC 读数降频到 2–5s（UI 复用缓存）；风扇服务复用 Hub 缓存；30s 保活改为 ≥300s 或删除；加 SMI 计数/耗时 | **状态：未落地**。稳态 SMI 从 ~3.6/s 降到 ≤0.5/s；温度/转速显示延迟 ≤5s（与原厂一致） |
| P1-5 | `GmWmi.cs` `TryInvoke`/`Connect` | 不再长期缓存 `ManagementObject`：每次调用重新取（或采用 `BIOS/MyWmiBase.cs:86` 的 `ManagementClass` 写法）；`Resume` 后强制重连 | **状态：未落地**。睡眠/唤醒后读写仍可靠；消除"Put 成功但没触发 ACPI"的隐患 |

### P2（健壮性/可观测性/文档）

| # | 文件/函数 | 改动 | 状态 |
|---|---|---|---|
| P2-1 | `FnKeys.cs` `FnKeyWatcher` | 订阅 `Stopped`；`FnKeyService` 看门狗（30s，仅在异常/停止时重建） | **已落地**（20:37 快照） |
| P2-2 | `App.xaml.cs` / `FnKeyService` | `SystemEvents.PowerModeChanged(Resume)` → Restart + ClearAllEvents | **未落地** |
| P2-3 | `Views/SystemView.xaml(.cs)` | Fn 区增加：最近确认结果、丢弃计数、确认失败计数、Unknown 计数、「重建监听/清事件位」按钮 | **部分落地**（20:37 已有事件历史/计数，缺清位与确认状态） |
| P2-4 | `App.xaml.cs` `--fnprobe` | 日志追加：32 字节全量、`ackOk`、是否被判回声、Δms、`[GMC]/[WMI2]` 对照 | **大部分已落地**（缺 ackOk/ts） |
| P2-5 | `README.md` | 写明：原厂不去重、alpha 门控作用、daemon 吞首条事件、Fn+1=强冷是重写新增语义、去重窗口可配、性能键停报的已知未知 | **部分**（README 已补充 Fn 章节，但上述"原厂事实/不确定性"未写入） |

---

## 8. 不确定项 / 需真机验证清单

1. **878–2500ms 的性能键重复到底是回声还是真实按键**：必须用 §3.4 的标注实验 + 全 32 字节数据定案；当前只能确认 190–375ms 那批是回声。
2. **EC 是否要求 2/3/4 事件位的确认、确认失败是否导致永久静默**：协议里没有读事件位的命令，无法直接验证；只能靠 P0-1/P0-3 的可观测性 + 停报是否消失来间接判定。
3. **性能键停报的真正触发条件**（H1/H2/H3）：需要"同一 boot、可控变量"的 A/B（例如：只降写频 vs 只加确认重试 vs 两者都做），并记录每次停报前 60s 的 SMI 次数/时间线。
4. **0x0400 单字段 vs 多字段同写的语义**：原厂只写单字段；"三字段置 1 一次清全部"从未被原厂使用，语义未知，故建议顺序三次调用。
5. **睡眠/恢复后 watcher 与 SMI 通道是否失效**：本机日志未覆盖 resume 场景（`App.OnStartup` 无 PowerModeChanged 处理），需实测。
6. **摄像头/CC/HDMI/适配器分支在本机是否真的会产生 GMC_WMIEvent**：本日志 0 条 → 这些分支的"原厂 vs 重写"差异目前只有代码级证据，无实测证据。
7. **原厂 daemon 启动后"首条非背光事件被吞"**：静态代码确定；但没有原厂运行日志可验证真机表现，标注为代码推断。

---

## 附录 A：日志关键序列索引（`src/LeishenCC/bin/Release/net5.0-windows/leishen.log`）

- `:1-4` 能力查询/硬件状态/显示输出/监听启动
- `:19-26` 性能键 `[02 07 …]` 与背光 `[00…]` 的 b[0]=背光档位对应关系
- `:51-56` **一次按键产生 3 条相同负载并全部执行**（19:36:19，强冷开→关→开）
- `:65-76` 背光键成对出现（间隔 190–375ms）
- `:107` 最后一条性能键（19:42:54.831）→ `:116-139` 期间只有背光事件 → `:153` 重启后首条性能键（19:54:41）
- `:155-158` 19:55:12.934 / 19:55:14.818 两条性能键（现场记录称同一次按压，**无法从日志证实**）
- `:211-239` 20:20:19–20:20:40 的 13 条性能键事件、6 执行 7 忽略（2500ms 窗口效果）
- `:177-180` 20:09:19 出现 `SMI版本=64000 SKU=64` 的异常读取（`a0` 回显请求值 0xFA00），说明该次能力查询实际未得到有效响应；与 Fn 无直接关系，但提示 SMI 偶发异常是真实存在的

## 附录 B：原厂行号索引（本报告引用的全部位置）

- daemon：`ControlCenterDaemon.cs` 79（alpha 初值）、156-221（Load）、238-268（GetSupportStatus/0x0201）、270-292（单实例互斥）、387-401（启动 CC）、412-433（MonitorWMIACPIEvent）、435-445（Handle）、447-585（ParseWMIEventData：452 长度、459-465 flag、466-474 背光、475 门控、477-489 HDMI、490-494 适配器、495-532 摄像头、533-556 性能、536 DC/TypeC、554 清位、557-568 CC、565 停监听、570-573 alpha else、575-578 长度错）、587-602（Stop）、604-618（Restart）、620-628（Close/Dispose）、644-665（SetEventStatus）、749-788（registry watcher + 14 信号）、790-800（SystemEventMonitor）、802-827（GetHardwareStatus/0x0202）、829-836（timer2）、838-859（SetInitialFanControl/5s）、889-907（0x0200）、909-921（0x0207）、933-1027（MonitorAndControlFan/10℃档）、1015-1026（只在档位变化时写）、1029-1045（0x0205）、1047-1064（PowerModeChanged）、1066-1097（PowerLineStatusCheck）、1152-1163（0x0300）。
- CC：`MainWindow.cs` 272（alpha）、760-811（构造/互斥/NVML）、836-1048（Loaded：862 写 registry 14=2、1033-1046 起/重启监听）、944-945（timer2 5s）、1228-1250（NL5_tool 互斥）、1344-1400（PowerLineStatusCheck/TypeC 重试）、1492-1609（WndProc 服务消息）、1704-1707、1782-1812（refresh_info 仅系统页读 EC）、2039-2067（模式切换）、2184-2216（Closing/Closed：2200 写 registry 14=1、2209 Dispose）、2222-2257（0x0200/0x0207）、2624-2674（SetAlphaChangeData）、3061-3079（MonitorAlpha/0x0200 a6=亮度）、3105-3129（page_show/hidden）、3542-3567（GetHardwareStatus）、3597-3616（Monitor）、3630-3785（Parse：3637-3640 alpha 先初始化、3645-3665 背光门控、3667 门控、3673-3686 HDMI、3687-3692 适配器、3693-3730 摄像头、3731-3763 性能、3734 DC/TypeC、3764-3768 CC、3770-3773 alpha else）、3787-3816（SetEventStatus/失败打 WMI Error）、3818-3843（Stop/Restart/Close）、4394-4407（GetAdpaterStatus）、5316-5353（0x0206 fan/TypeC）。
- 公共：`MyWmiBase.cs:62-124`（读写范式）、`126-154`（ACPIPerformSMI）；`SMI_STRUCT_S.cs:3-21,97-129`；`BIOS/MyWmiBase.cs:45-103`（ManagementClass 范式，关键行 86）；`BIOS/MySpecialFunc.cs:43-100`（广播封装，非解析）；`ALL:1841-1979、2038-2059、1981-2022`。

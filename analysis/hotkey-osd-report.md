# HotKey OSD（I3D/Quanta hkosdservice）逆向报告——WMI2Event 订阅、EventDetail 解析、OSD 图标映射

> 分析对象（`C:\Program Files\Hotkey OSD\`，只读，复制到 `analysis/raw/` 后用 Ghidra 12 headless 分析）
>
> | 文件 | 大小 | MD5 | 说明 |
> |---|---|---|---|
> | `HkeyOSDSvis.exe` | 4,882,272 B | `92BB7B7DC31A4FFBA3070BF2962701DD` | 服务 `hkosdservice`，LocalSystem，LISTEN 6262 |
> | `HkeyUserPS.exe` | 2,875,232 B | `E62BF4F4AC5CE43C0BB24F6AFCD391C0` | 用户会话 OSD 渲染进程（JUCE + drogon 客户端 + 键盘钩子） |
> | `WMI\uw3.mof` | 1,067 B | — | 安装 `WMI2Event`（GUID `{ABBC0F20-8EA1-11d1-00A0-C90629100000}`）与 `WMI2Wireless` |
>
> 反编译产物：`analysis/raw/OSD_*.c`、`OSD2_*.c`、`OSD3_*.c`、`OSD4_*.c`、`OSD5_*.c`、`OSD6_*.c`、
> `OSD7_*.c`、`OSD8_thunks.asm`、`OSD9_usage.c`、`PS_*.c`；脚本在 `analysis/ghidra_scripts/`。

---

## 0. 结论摘要

1. **`hkosdservice`（HkeyOSDSvis.exe）只订阅 `root\wmi:WMI2Event`**（WQL `SELECT * FROM WMI2Event`），
   **不订阅 `GMC_WMIEvent`**（二进制中无该字符串），通过 COM `IWbemLocator→IWbemServices::ExecNotificationQueryAsync`
   异步接收，回调为 `WMI2EventSink::Indicate`。
2. **两路事件流是独立的**（CIM 仓库证据，见 §2）：`GMC_WMIEvent` 绑定 ACPI WMI 设备实例
   `ACPI\PNP0C14\0x1_0` 和 GUID `{74286D6E-429C-427a-B34B-B5D15D032B05}`；`WMI2Event` 绑定标准 ACPI WMI 事件
   GUID `{ABBC0F20-8EA1-11D1-00A0-C90629100000}`（与 Windows 自带 `WMIACPIEvent` 同 GUID，设备实例
   `ACPI\PNP0C14\CBWMI_0`）。→ 新项目应**同时监听两路**。
3. `WMI2Event` 的 EventDetail 固定 32 字节：**`b[0] = 事件 ID`**，数据位置由 `<eventID,dataIndex>` 静态表决定
   （不是固定的 `b[1]`）；事件 ID 只处理 `0x0D/0x30/0x33/0x34/0x35/0x39`，`0xFF` 无效，长度 ≠32 丢弃。
4. 事件语义（已定位到 vtable/处理函数/下发给用户进程的 `Command`）：

   | EventDetail[0] | 处理函数（HkeyOSDSvis vtable 槽） | 数据 | 下发 Command | 语义 |
   |---|---|---|---|---|
   | `0x0D` | slot1 `OnTouchpadLocked(bool)` `0x1402e33d0` | `b[29]` | `0x65`(101) | 触摸板锁 |
   | `0x30` | slot0 `OnFanStatusChanged(bool)` `0x1402e3960` | `b[30]` | `0x66`(102) | 风扇模式 max/normal |
   | `0x33` | slot2 `FUN_1402e3070(byte)` | `b[27]` | `0x67`(103) | FnLock（代码确证，见 §4.4） |
   | `0x34` | slot3 `FUN_1402e2cf0()`（内部调 `ToggleMicrophoneMute`） | 无（表无 0x34） | `0x69`(105) | 麦克风静音切换 |
   | `0x35` | slot4 `OnUsageModeChanged(UsageMode)` `0x1402e2750` | `b[28]`，值 0/1/2 | `0x6B`(107) | 使用模式：0=turbo、1=game、2=normal |
   | `0x39` | slot5 `FUN_1402e23d0(byte)` | `b[25]` | `0x6A`(106) | Camera（代码确证，见 §4.4） |

   （用户进程 `HkeyUserPS` 再把 0x65..0x6B 的 Command 映射成对应 PNG，见 §5。）
5. **OSD 不“清位”**：服务二进制中没有 `RW_GMWMI` / `BufferBytes` / 端口 IO / `DeviceIoControl`；`WMI2Event` 的 MOF
   也没有方法。→ `WMI2Event` 流无需 `0xFB00/0x0400` 的 SMI 确认；只有 `GMC_WMIEvent` 流需要（ControlCenter 负责）。
6. IPC：服务是 uWS WebSocket **服务端，监听 6262**（`wss://localhost`，TLS 证书 `server_crt.pem/server_key.pem`），
   用户进程是 drogon WebSocket **客户端**连接 `wss://localhost:6262`；消息为 JSON（字段 `Command`、`Data`，
   另有 `BroadcastAll` 广播路径）。运行时 `netstat` 证实：`0.0.0.0:6262 LISTENING` +
   `127.0.0.1:6262 ESTABLISHED`（HkeyUserPS）。

---

## 1. 服务/进程架构与 IPC

### 1.1 服务注册（只读 SCM 查询）

```
SERVICE_NAME: hkosdservice
TYPE               : 10  WIN32_OWN_PROCESS
START_TYPE         : 2   AUTO_START
BINARY_PATH_NAME   : C:\Program Files\Hotkey OSD\HkeyOSDSvis.exe
SERVICE_START_NAME : LocalSystem
STATE              : RUNNING
```

`HkeyOSDSvis` 内符号（RTTI/日志串）显示它是 Windows 服务：
`ServiceMain`、`ServiceCtrlHandler`、`OnStart/OnStop`、`StartUserProcess/LaunchUserPSInActiveUserSession`
（`CreateProcessAsUserW`、`ServiceCreateHighILProcessInSessionAsActiveUser`、`ServiceCreateUIAccessProcessInSession`），
即服务在活动用户会话中拉起 **`HkeyUserPS.exe`**（用户进程带 `uiAccess="true"` manifest）。

### 1.2 WebSocket IPC

- 服务端：`HkeyOSDSvis::StartWebSocketServer`（uWS），失败日志中打印端口常量 **`0x1876 = 6262`**
  （`analysis/raw/OSD3_server.c`：`FUN_140080d20(...,0x1876)`），使用 `server_crt.pem/server_key.pem` 做 TLS。
- 客户端：`HkeyUserPS::InitializeWebSocket`（`analysis/raw/PS_ws.c`）构造 `wss://localhost`（0x15 字节）+ 端口 6262
  （`FUN_1400b4360(...,0x1876)`），并设置连接建立/断开/消息三个回调。
- 运行时验证（非管理员）：
  ```
  TCP 0.0.0.0:6262  LISTENING  5948
  TCP [::]:6262     LISTENING  5948
  TCP 127.0.0.1:6262 ESTABLISHED 5948 <-> 127.0.0.1:1290 ESTABLISHED 2672 (HkeyUserPS)
  ```
- 消息协议：jsoncpp/drogon 文本帧，JSON 键 `Command` / `Data`（服务端字符串 0x3FD270/0x3FD280，用户端同样符号），
  广播路径字符串 `BroadcastAll`（0x3FD298）。事件通知 = `{ "Command": <0x65..0x6B>, "Data": <值或字符串> }`。

---

## 2. 两路 WMI 事件流（CIM 仓库 + MOF 证据）

非管理员无法经 WMI API 枚举 `root\wmi`（返回“拒绝访问”），因此直接读取 CIM 仓库
`C:\Windows\System32\wbem\Repository\OBJECTS.DATA`（可读，`findstr` 可命中类名），其中保存了**已安装类的
完整定义文本**。关键原文（偏移 `0xD8B6CB` / `0xD8B71D` / `0xD8BB48` / `0xD8BC52`）：

```
GMC_WMIEvent
  WMI, WmiProv, Locale MS\0x409, Description "WMI Event Data"
  guid {74286D6E-429C-427a-B34B-B5D15D032B05}
  Active boolean; EventDetail uint8[32] (WmiDataId 1, Description "WMI Event Data")
  WDMClassesOfDriver GMC_WMIEvent  ACPI\PNP0C14\0x1_0-{05901221-D566-11d1-B2F0-00A0C9062910}

WMI2Event
  WMI, WmiProv, Description "Class containing event generated package data"
  guid {ABBC0F20-8EA1-11D1-00A0-C90629100000}
  Active boolean; EventDetail uint8[32] (WmiDataId 1, Description "Package Data")

WMIACPIEvent（Windows 自带，ACPI.sys MOF）
  guid {ABBC0F20-8EA1-11D1-00A0-C90629100000}
  Active boolean; evBytes uint8[32]
  WDMClassesOfDriver WMIACPIEvent  ACPI\PNP0C14\CBWMI_0-{05901221-D566-11d1-B2F0-00A0C9062910}

RW_GMWMI
  WMI, WmiProv, WmiExpense, Description "Data class for IO read"
  guid {644C5791-B7B0-4123-A90B-E93876E0DAAD}
  Active boolean; BufferBytes uint8[32] (Description "Gaming WMI data")
  WDMClassesOfDriver RW_GMWMI  ACPI\PNP0C14\0x1_0-{05901221-D566-11d1-B2F0-00A0C9062910}
```

**结论**：
- `GMC_WMIEvent` 与 `WMI2Event` **GUID 不同、绑定的 ACPI WMI 设备实例也不同**（`0x1_0` vs `CBWMI_0`），
  确系两路独立事件流，必须双订阅（互不覆盖、也互不“抢”）。
- `WMI2Event` 是厂商 MOF `uw3.mof` 安装的类名，与 Windows 自带 `WMIACPIEvent` 共用标准 ACPI 事件 GUID
  `ABBC0F20`；两者会收到同一 `CBWMI_0` 事件源的数据。
- `RW_GMWMI`（SMI 缓冲/清位通道）与 `GMC_WMIEvent` 同属 `0x1_0` 设备实例，因此 GMC 流的清位走 RW_GMWMI 顺理成章；
  WMI2Event 流没有对应的清位数据类。
- `uw3.mof` 内容（原文）：`class WMI2Event : WmiEvent { [key,read] string InstanceName; [read] boolean Active;
  [WmiDataId(1), read, write] uint8 EventDetail[32]; };` 另定义 `WMI2Wireless`
  （GUID `{101F83BB-30B7-DF11-8D81-0800200C9A66}`，`Get_Method/Set_Method`）。

---

## 3. WMI2Event 订阅实现（Ghidra）

### 3.1 查询构造与 COM 订阅

`QuantaWMIEvent::Start`（`FUN_14032e740`，`analysis/raw/OSD2_wmihelper.c:185+`）：

```c
// 构造 WQL 与命名空间（宽字符串常量，长度 0x17=23 / 0x8=8）
FUN_14007b910(local_58, L"SELECT * FROM WMI2Event", 0x17);
FUN_14007b910(local_78, L"root\\WMI", 8);
...
uVar5 = FUN_1402e0cb0(local_b8, plVar1);   // 交给 COM 订阅封装
```

`FUN_1402e0cb0`（`analysis/raw/OSD2_getdataindex.c:4+`）：

```c
CoCreateInstance(&CLSID_IWbemLocator, NULL, CLSCTX_INPROC_SERVER,
                 &IID_IWbemLocator, &local_38);          // 0x1403de478 / 0x1403de448
...
// IWbemLocator::ConnectServer → 取 IWbemServices
local_res18 = ...
iVar4 = (**(code **)(*local_res20 + 0x18))(local_res20, *param_2, &local_res18);  // ConnectServer
iVar4 = (**(code **)*local_res18)(local_res18, &DAT_1403de458, ...);              // SetProxyBlanket
...
pcVar2 = *(code **)(*plVar5 + 0xb8);     // IWbemServices vtable + 0xB8 = ExecNotificationQueryAsync
iVar4 = (*pcVar2)(plVar5, queryBSTR, namespace, 0x80 /*WBEM_FLAG_*/, 0, sink);
```

- 失败日志字符串：`"Calling CoCreateInstance for IWbemLocator object failed"`、`"Failed to get WMI service object"`。
- 回调实现：`WMI2EventSink::Indicate()`（异常日志 `"WMI2EventSink::Indicate() exception occur:"`，
  `analysis/raw/OSD2_sink.c`），内部调用 `QuantaWMIEvent::OnEvent(long, IWbemClassObject**)`。
- 结论：**是 COM 原生 WMI 异步订阅（IWbemObjectSink::Indicate），不是 .NET ManagementEventWatcher**；
  查询类名就是 `WMI2Event`，命名空间 `root\WMI`（大小写不敏感）。

### 3.2 事件接收 `QuantaWMIEvent::OnEvent`（`FUN_14032cfa0`）

```c
// 1) 取 EventDetail
pOVar14 = SysAllocString(L"EventDetail");
iVar10  = (*pcVar4)(plVar3, *puVar19, 0, local_68, 0, 0);   // IWbemClassObject::Get(L"EventDetail",...)
...
// 2) 校验长度必须 32
if (local_60->rgsabound[0].cElements != 0x20) { log("Invalid WMI event received, the report data size is wrong: "); return; }
// 3) 取数据
SafeArrayAccessData(local_60, local_340);
cVar7 = *local_340[0];                    // EventDetail[0] = EventID
if (cVar7 == -1 /*0xFF*/) { log("() Incorrect event id: "); return; }
cVar8 = FUN_14032dfb0(&local_348);        // WMI2EventDataParser::GetData()
// 4) switch(EventID) → 观察者回调
```

`WMI2EventDataParser::GetData`（`FUN_14032dfb0`）：

```c
bVar4 = FUN_14032e3d0();                  // GetDataIndex(): map[EventDetail[0]]，缺省 0xFF
if (bVar4 == 0xFF) { log("() Unknown data index!"); return 0xFF; }
return *(byte *)((ulonglong)bVar4 + *(longlong *)(param_1 + 8));  // EventDetail[dataIndex]
```

---

## 4. EventDetail 32 字节解析表

### 4.1 事件 ID 与数据索引映射（`FUN_14032e3d0` 的静态 `std::map<byte,byte>`）

汇编证据：`analysis/raw/OSD3_mapinit.asm`（`0x14032e3d0-0x14032e53b`）；键写入 `[RBP+0x18]`，值写入 map 节点字节。
完整初始化表（按汇编顺序）：

| EventDetail[0]（eventID） | dataIndex（取 EventDetail[idx]） | 备注 |
|---|---|---|
| `0x01` | `0x0D` (13) | |
| `0x02` | `0x0C` (12) | |
| `0x03` | `0x1C` (28) | |
| `0x04` | `0x0B` (11) | |
| `0x05` | `0x0B` (11) | |
| `0x0D` | `0x1D` (29) | 触摸板事件 |
| `0x1B` | `0x1F` (31) | |
| `0x30` | `0x1E` (30) | 风扇事件 |
| `0x31` | `0x1F` (31) | |
| `0x32` | `0x17` (23) | |
| `0x33` | `0x1B` (27) | FnLock 事件（推断） |
| `0x35` | `0x1C` (28) | UsageMode 事件 |
| `0x39` | `0x19` (25) | Camera 事件（推断） |
| `0x3A` | `0x0A` (10) | |

`0x34` 不在表中 → `GetData()` 返回 `0xFF`；这与 `OnEvent` 对 `0x34` 的无参调用一致。

### 4.2 有效事件 ID 与处理槽位（`OnEvent` switch）

`FUN_14032cfa0` 的 `switch(cVar7)`（`analysis/raw/OSD_wmi.c:548-617`）：

| EventID | 观察者 vtable 槽位 | 调用签名 |
|---|---|---|
| `0x0D` ('\r') | `+0x08` (slot1) | `(void*, bool)` |
| `0x30` ('0') | `+0x00` (slot0) | `(void*, bool)` |
| `0x33` ('3') | `+0x10` (slot2) | `(void*, bool)` |
| `0x34` ('4') | `+0x18` (slot3) | `(void*)` 无数据 |
| `0x35` ('5') | `+0x20` (slot4) | `(void*, enum UsageMode)`，值规整：1→1、2→2、其它→0 |
| `0x39` ('9') | `+0x28` (slot5) | `(void*, bool)` |

其它事件 ID 一律忽略（无 default 分支）。

### 4.3 观察者 vtable 与处理函数（`0x14042A138`）

`VtableFind` 结果（`analysis/raw/OSD4_vtables.txt`）：`i3d::HkeyOSDSvis` 的 vtable 在 `0x14042A138`，
`vtable[-1] = 0x140430F40 (RTTI CompleteObjectLocator)`，槽位如下：

```
[0] 0x1402E3960  i3d::HkeyOSDSvis::OnFanStatusChanged(bool)     // 事件 0x30
[1] 0x1402E33D0  i3d::HkeyOSDSvis::OnTouchpadLocked(bool)       // 事件 0x0D
[2] 0x1402E3070  FUN_1402e3070(byte)                            // 事件 0x33
[3] 0x1402E2CF0  FUN_1402e2cf0(void)  -> i3d::ToggleMicrophoneMute()  // 事件 0x34
[4] 0x1402E2750  i3d::HkeyOSDSvis::OnUsageModeChanged(UsageMode)// 事件 0x35
[5] 0x1402E23D0  FUN_1402e23d0(byte)                            // 事件 0x39
```

处理函数均把动作包装成异步任务（`FUN_140242600`/`FUN_140095370` 投递到 JUCE 消息队列），
任务调用体是 `LAB_1402e1fc0` 等 thunk：

```
LAB_1402e1fc0 JMP FUN_1402e3760   -> Command 0x66  (slot0 Fan)
LAB_1402e1ff0 JMP FUN_1402e31d0   -> Command 0x65  (slot1 Touchpad)
LAB_1402e2020 JMP FUN_1402e2e70   -> Command 0x67  (slot2 FnLock)
LAB_1402e2050 JMP FUN_1402e2af0   -> Command 0x69  (slot3 Mic)
LAB_1402e2080 JMP FUN_1402e2530   -> Command 0x6B  (slot4 UsageMode)
LAB_1402e20b0 JMP FUN_1402e21d0   -> Command 0x6A  (slot5 Camera)
```
（证据：`analysis/raw/OSD8_thunks.asm` 的 JMP 目标；各发送函数的 `FUN_1402df0b0(...,<byte>,...)`
为 JSON `{"Command":<byte>,"Data":...}` 构造，`analysis/raw/OSD7_senders.c` 中分别见
`0x65/0x66/0x67/0x69/0x6A/0x6B` 常量。）

### 4.4 slot2/slot5 语义判定（FnLock / Camera）——已由用户进程代码确证

用户进程 `HkeyUserPS::OnWebSocketMessage`（`FUN_1400b5e50`）只接受 `Command ∈ [0x65,0x6B]`
（条件 `(byte)(cmd + 0x9B) < 7`），然后调用 `FUN_1400b5b60` 做 `Command → 内部 OSD 命令` 映射
（`analysis/raw/PS3_cmdmap.c:371-504`）：

```c
switch (*param_2 /* Command 首字节 */) {
case 0x65:  // 触摸板：不从 Data 取值，直接读注册表实际状态
    RegOpenKeyExA(..., "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\PrecisionTouchPad\\Status", ...);
    RegQueryValueExA(..., "Enabled", ...);  bVar7 = (Enabled == 1);
    cVar6 = bVar7 + 3;              // Enabled==1 -> 4(touchpad_on)；否则 3(touchpad_off)
    break;
case 0x66:  // 风扇：Data>0 -> 2(fan_max)，否则 1(fan_normal)
    cVar6 = (_wtoi(Data) > 0) + 1;  break;
case 0x67:  // FnLock：Data<1 -> 6(fn unlock)，否则 5(fnlock)
    cVar6 = (_wtoi(Data) < 1) + 5;  break;
case 0x68:  // 保留/忽略
    break;
case 0x69:  // 麦克风：Data<1 -> 10(mic 未静音)，否则 9(mic off 静音)
    cVar6 = (_wtoi(Data) < 1) + 9;  break;
case 0x6A:  // Camera：Data<1 -> 8(camera_off)，否则 7(camera_on)
    cVar6 = (_wtoi(Data) < 1) + 7;  break;
case 0x6B:  // 使用模式：0->0x0B(turbo)，1->0x0C(game)，2->0x0D(normal)，其它忽略
    cVar6 = (iVar5==0) ? 0x0B : (iVar5==1) ? 0x0C : (iVar5==2) ? 0x0D : 0;  break;
}
FUN_1401f86f0(osdWindow, cVar6);   // 载入对应 PNG
```

由此可**代码级确证**：`0x67 = FnLock`、`0x6A = Camera`、`0x6B` 的 0/1/2 = turbo/game/normal
（此前“按图标集合推断”的结论被证实）。另外 slot3 已由 `FUN_1402e2cf0 → FUN_1402dc440`
（日志 `i3d::ToggleMicrophoneMute`、`microphone_util.cpp`）确证为麦克风切换。

注意 `Data` 是 jsoncpp 序列化后的值：fan/fnlock/mic/camera 用 `_wtoi` 当数字解析（实际可能以字符串下发），
touchpad 完全忽略 `Data`（读注册表），usage mode 服务端用 `FUN_140314150`（整数转十进制）转成字符串下发。

---

## 5. HkeyUserPS（用户进程）职责与命令表

- 职责：JUCE 界面（`i3d::OSDWindow`）+ 读取 `Assets\*.png` 显示 OSD + **低级键盘钩子**
  `SetWindowsHookExW(WH_KEYBOARD_LL=0xD, FUN_1400b1b10, ...)`（`analysis/raw/PS_ws.c`）。
- 不订阅 WMI：`HkeyUserPS.exe` 中无 `WMI2Event`/`root\WMI` 字符串、无 `IWbem*`；
  它通过 WebSocket 客户端连接服务接收 `Command`（`OnWebSocketMessage`），并渲染 OSD。
  因此**它不会抢先清事件位**，也不参与 WMI 订阅。
- 键盘钩子上报（`HkeyUserPS::OnKeyboardHook`，`FUN_1400b5400`，`analysis/raw/PS_keys.c`）：
  `KeyboardInfo { int virtual_key; int scan_code; int flags; bool is_break; bool is_extension; }`。
  仅对以下键合成 OSD 命令（调用 `FUN_1401f86f0(this->osd, cmd)`）：
  - 扩展键：`VK_NEXT_TRACK(0xB0)→0x11`、`VK_PREV_TRACK(0xB1)→0x10`、`VK_STOP(0xB2)→0x0F`、
    `VK_PLAY_PAUSE(0xB3)→0x0E`；
  - `VK_NUMLOCK(0x90)` → `(~GetKeyState(0x90)&1) | 0x12`（即 0x12/0x13 对应 ON/OFF）；
  - `VK_CAPITAL(0x14)` → `(~GetKeyState(0x14)&1) | 0x14`（0x14/0x15）；
  - `VK_SCROLL(0x91)` → `(~GetKeyState(0x91)&1) | 0x16`（0x16/0x17）。
- `FUN_1401f86f0` 的 **OSD 图标命令表**（`analysis/raw/PS_assets.c`，switch(param_2)）：

  | cmd | 图标 | cmd | 图标 |
  |---|---|---|---|
  | 1 / 2 | `fan_normal.png` / `fan_max.png` | 0x0E | `play_pause.png` |
  | 3 / 4 | `touchpad_off.png` / `touchpad_on.png` | 0x0F | `stop.png` |
  | 5 / 6 | `fnlock.png` / `fn unlock.png` | 0x10 | `prev.png` |
  | 7 / 8 | `camera_on.png` / `camera_off.png` | 0x11 | `next.png` |
  | 9 / 10 | `mic off.png` / `mic.png` | 0x12 / 0x13 | `NumLock_ON/OFF.png` |
  | 0x0B / 0x0C / 0x0D | `turbo.png` / `game.png` / `normal.png` | 0x14 / 0x15 | `CapsLock_ON/OFF.png` |
  | | | 0x16 / 0x17 | `ScrollLock_ON/OFF.png` |

  → 服务侧 `Command 0x65..0x6B`（101..107）在用户进程侧再映射到上表内部 ID
  （`HkeyUserPS::OnWebSocketMessage` = `FUN_1400b5e50`，命令分发 `FUN_1400b5b60`；
  `analysis/raw/PS2_wsmsg.c`、`analysis/raw/PS3_cmdmap.c`）。完整链路：

  | 服务 Command | 事件 ID 来源 | Data 语义 | 内部 OSD 命令 | 最终 PNG |
  |---|---|---|---|---|
  | `0x65` (101) | `0x0D` touchpad | 忽略，读 `PrecisionTouchPad\Status\Enabled` | 4 / 3 | `touchpad_on.png` / `touchpad_off.png` |
  | `0x66` (102) | `0x30` fan | >0 = max | 2 / 1 | `fan_max.png` / `fan_normal.png` |
  | `0x67` (103) | `0x33` fnlock | >=1 = 锁定 | 5 / 6 | `fnlock.png` / `fn unlock.png` |
  | `0x69` (105) | `0x34` mic | <1 = 未静音 | 10 / 9 | `mic.png` / `mic off.png` |
  | `0x6A` (106) | `0x39` camera | >=1 = 开启 | 7 / 8 | `camera_on.png` / `camera_off.png` |
  | `0x6B` (107) | `0x35` usage mode | 0/1/2 | 0x0B/0x0C/0x0D | `turbo.png`/`game.png`/`normal.png` |
  | `0x68` (104) | 未使用（switch 中直接忽略） | — | — | — |

  用户进程仅在 `WTSGetActiveConsoleSessionId() == 自己的 session` 时处理消息（非活动会话直接忽略）。

---

## 6. 清位/确认（Clear/Ack）行为

| 事件流 | 订阅者 | 清位方式 | OSD 是否清位 |
|---|---|---|---|
| `GMC_WMIEvent`（GUID 74286D6E…，ACPI\PNP0C14\0x1_0） | ControlCenter.exe / ControlCenterDaemon / LEDCtrl | `root\wmi:RW_GMWMI.BufferBytes` 写 32 B：`a0=0xFB00(64256), a1=0x0400(1024), a2/a3/a4=事件位值`，`SetPropertyValue+Put` | 不订阅，不参与 |
| `WMI2Event`（GUID ABBC0F20…，ACPI\PNP0C14\CBWMI_0） | HkeyOSDSvis（hkosdservice） | **无**：MOF 无方法；服务无 RW_GMWMI/端口 IO/DeviceIoControl；无可观测的确认链路 | 不 clear |

因此新项目的双订阅逻辑应区别对待：
- GMC 流：需要 AckAll（0x0400）清位，并且注意“可观测/失败重试/启动清残位”（Lead 侧已实现）。
- WMI2Event 流：**只读，不需要也不应该写 SMI 清位**；重复事件由自身去重/短窗处理（现有 600ms 逻辑适用）。
- 两路订阅互不影响；两个类如果都收到同名事件也不会互相清除（不同 GUID/设备实例）。

---

## 7. 对“完善 Fn 监听”的直接建议

1. **双订阅**：`root\wmi:GMC_WMIEvent`（已有）+ `root\wmi:WMI2Event`（新增）。后者负责
   fan / touchpad / fnlock / mic / camera / usage-mode，前者负责背光 / HDMI / 适配器 / 摄像头状态 / 性能 / CC。
2. `WMI2Event` 解析严格照抄 OSD：
   ```csharp
   byte[] b = (byte[])e.NewEvent["EventDetail"];
   if (b.Length != 32 || b[0] == 0xFF) return;
   switch (b[0]) {
     case 0x0D: bool touchpad = b[29] != 0; break;   // 触摸板
     case 0x30: bool fanMax   = b[30] != 0; break;   // 风扇 max/normal
     case 0x33: bool fnLock   = b[27] != 0; break;   // FnLock（推断）
     case 0x34: /* mic toggle，无数据 */   break;
     case 0x35: int  mode     = b[28] switch {1=>1, 2=>2, _=>0}; break; // turbo/game/normal
     case 0x39: bool camera   = b[25] != 0; break;   // Camera（推断）
   }
   ```
3. `WMI2Event` 类可能因 `uw3.mof` 未安装/Root\WMI 权限不足而不可用：注册失败要 fallback 到 GMC 流 + 提示安装 MOF；
   订阅建立/服务重启/唤醒后重新订阅（沿用现有 Watchdog 思路）。
4. 事件 ID→PNG 映射可直接照 §5 表；服务 Command 0x65..0x6B 的语义见 §4。
5. 不要为了 WMI2Event 写 `RW_GMWMI` 清位；GMC 流才需要。

---

## 8. 验证边界

- 静态：Ghidra 12 headless 反编译 + CIM 仓库文件解析 + PE 导入/字符串；未修改系统、未启动/停止服务。
- 动态：`netstat` 证实 6262 端口与 `HkeyUserPS` 连接；非管理员无法 `Get-WmiObject -Namespace root\wmi`
  （“拒绝访问”），所以未能订阅 WMI 抓取真实 EventDetail 样例；事件字节表来自反编译，非抓包。
- 事件 ID→处理函数→Command→PNG 全链路均为代码级确证（§4.3/§4.4/§5）；仍缺的是“真机按键 → EventDetail
  原始字节”的实测样本（受 root\wmi 权限限制），建议新项目上线后记录原始 32 字节日志复核各 dataIndex。

# 雷神控制中心原生层逆向报告（Ghidra 12）——驱动 / 原生 DLL / Fn 通信链

> 分析对象（原厂安装目录，只读复制到 `analysis/raw/` 后用 Ghidra headless 分析）
>
> | 文件 | 大小 | MD5 | 版本/签名 |
> |---|---|---|---|
> | `ControlCenter64.sys`（64 位内核驱动） | 18,712 B | `01A4E3FF559E38A5C32374750E8D59FF` | 1.0.0.1，Quanta，EV 签名有效 |
> | `ControlCenterC64.dll`（64 位原生 DLL） | 2,660,120 B | `D1C9CA0BC32C39C222F3DF27C082547D` | 3.0.0.9，Quanta |
> | `ControlCenter.exe`（.NET WPF，反编译源码在 `decomp/`） | — | — | 3.0.0.22 |
>
> 环境：Ghidra 12.0.1 headless（JDK 21，`APPDATA` 重定向到工作区以避开沙箱对 `%APPDATA%` 的写拒绝），
> 分析脚本见 `analysis/ghidra_scripts/`，原始反编译产物见 `analysis/raw/CC64_sys_*.{txt,c}`、
> `CC64_dll_*.{txt,c}`。工作机非管理员，未加载/未启动任何驱动或服务。

---

## 0. 一句话结论

1. **Fn 热键监听不需要 `ControlCenter64.sys`**。原厂两条热键事件链全部走 `root\wmi` 的 ACPI WMI 事件类
   （`GMC_WMIEvent` / `WMI2Event`），.NET 侧没有任何 `DeviceIoControl`/`CreateFile("\\.\CCDRV1")` 调用。
2. `ControlCenter64.sys` 是一个 2017 年的“硬件寄存器 poke / POST 测试”驱动：设备名 `\Device\CCDRV1`，
   符号链接 `\DosDevices\CCDRV1`，IOCTL 只提供**端口 in/out、RDMSR/WRMSR、RDPMC、MmMapIoSpace 物理内存读写、
   HalGet/SetBusDataByOffset PCI 配置空间读写**，没有 WMI/ACPI/EC 专用逻辑，也没有热键事件通道。
   其 PDB 路径 `f:\_prog_prj\1610_nl5_led\js970_driver\jspostestercmd\sys\output\amd64\ControlCenter.pdb`
   与“LED/POST 测试器”用途一致。
3. `ControlCenterC64.dll` 只被 .NET 用于 **CPU 频率显示**：`init3()` 打开 `\\.\CCDRV1`，
   `GetCoreFrequency1000()` 通过 IOCTL `0x9C402084` 读 MSR `0xE7/0xE8`（IA32_MPERF/IA32_APERF）算频率；
   其余导出是 `rdtsc`/`QPC` 测速和 `MessageBox("Test okay")` 测试桩。与 Fn 热键无关。
4. 本机驱动服务实际名称是 **`ControlCenter`**（DisplayName=`ControlCenterService`），`AUTO_START`，
   当前 `STOPPED`，`WIN32_EXIT_CODE=577 (0x241)`（`ERROR_INVALID_IMAGE_HASH`，内核拒绝加载该旧签名驱动）。
   即：原厂热键功能在“驱动未加载”状态下依然工作 —— 反向证明 Fn 不依赖该驱动。

---

## 1. ControlCenter64.sys 逆向

### 1.1 设备与 DriverEntry（Ghidra 地址 `0x00011008`，Ghidra 名 `FUN_00011008`）

反编译（`analysis/raw/CC64_sys_decomp.c:4-37`）：

```c
ulonglong FUN_00011008(longlong param_1)          // DriverEntry(DRIVER_OBJECT*)
{
  RtlInitUnicodeString(local_28, L"\\Device\\CCDRV1");
  uVar2 = IoCreateDevice(param_1, 0, local_28, 40000, 0x100, 0, local_res18);
  ...
  *(code **)(param_1 + 0x70) = FUN_000110e4;      // IRP_MJ_CREATE
  *(code **)(param_1 + 0x80) = FUN_000110e4;      // IRP_MJ_CLOSE
  *(code **)(param_1 + 0xe0) = FUN_000110e4;      // IRP_MJ_DEVICE_CONTROL
  *(code **)(param_1 + 0x68) = FUN_000114ac;      // IRP_MJ_CLEANUP / Unload
  RtlInitUnicodeString(local_18, L"\\DosDevices\\CCDRV1");
  uVar1 = IoCreateSymbolicLink(local_18, local_28);
  ...
  KeInitializeMutex(&DAT_00013118, 0);
}
```

证据（`CC64_sys_info.txt`）：
- 字符串 `00011790 [unicode] \Device\CCDRV1`、`000117b0 [unicode] \DosDevices\CCDRV1`
- 导入表：`IoCreateDevice / IoCreateSymbolicLink / IofCompleteRequest / IoDeleteDevice /
  IoDeleteSymbolicLink / KeInitializeMutex / MmMapIoSpace / MmUnmapIoSpace / KeBugCheckEx /
  HalGetBusDataByOffset / HalSetBusDataByOffset / RtlInitUnicodeString / __C_specific_handler`
  （**没有** `WmiSystemControl`、`ACPI`、`READ_PORT_*`、`WRITE_PORT_*` 导入；端口访问是编译器内联 `in`/`out` 指令）
- 设备类型 `40000 = 0x9C40`（Quanta 自定义设备类型），`DeviceCharacteristics = 0x100`（`FILE_DEVICE_SECURE_OPEN`），
  **未设置** `DO_BUFFERED_IO(0x4)` / `DO_DIRECT_IO(0x2)`；但分发函数实际按 `SystemBuffer` 处理，
  且所有 IOCTL 的 Method 位都是 0（`METHOD_BUFFERED`），见 1.3。

### 1.2 分发函数 `FUN_000110e4`（`0x000110e4`）

关键结构体偏移（x64 `_IRP` / `_IO_STACK_LOCATION`）：
- `Irp->AssociatedIrp.SystemBuffer` = `Irp+0x18`；
- `Irp->Tail.Overlay.CurrentStackLocation` = `Irp+0xB8`；
- `stack->Parameters.DeviceIoControl.OutputBufferLength` = `stack+0x8`，
  `InputBufferLength` = `stack+0x10`，`IoControlCode` = `stack+0x18`；
- `Irp->IoStatus.Status` = `Irp+0x30`，`Information` = `Irp+0x38`。

MajorFunction 处理：
- `0`（`IRP_MJ_CREATE`）：把全局句柄计数 `DAT_00013110` +1（初值 -1 表示驱动创建失败）；
- `2`（`IRP_MJ_CLOSE`）：计数 -1；
- `0x0E`（`IRP_MJ_DEVICE_CONTROL`）：进入 IOCTL switch（下节）。

### 1.3 IOCTL 全表

`CTL_CODE(DeviceType=0x9C40, Function, Method=0, Access)`；全部 `METHOD_BUFFERED`，输入/输出共用 `SystemBuffer`。
（除最后一条 `0xA2AB208C` 外，所有 DeviceType 均为 `0x9C40`。）

| IOCTL | Access | Func | 功能（反编译判定） | 输入布局 | 输出/`Information` |
|---|---|---|---|---|---|
| `0x9C402000` | ANY | 0x800 | 返回魔数 `8`（疑似版本/函数数量探测） | 无 | `u32 = 8`，Info=4 |
| `0x9C402004` | ANY | 0x801 | 返回当前打开句柄计数（引用计数） | 无 | `i32 refcount`，Info=4 |
| `0x9C402084` | ANY | 0x821 | **RDMSR**：`rdmsr(msr)` | `u32 msr`（4 B） | `u64 value`，Info=8 |
| `0x9C402088` | ANY | 0x822 | **WRMSR**：`wrmsr(msr, value)` | `u32 msr; u64 value`（偏移 4，非对齐） | 无，Info=0 |
| `0x9C40208C` | ANY | 0x823 | **RDPMC**：`rdpmc(counter)` | `u32 counter`（4 B） | `u64 value`，Info=8 |
| `0x9C402090` | ANY | 0x824 | **死循环**（`do { } while(true);`，Ghidra 标注 “infinite loop”） | 无 | 永不返回（会挂起调用线程/系统） |
| `0x9C4060C4` | READ | 0x831 | 未实现（落到 invalid parameter） | — | `STATUS_INVALID_PARAMETER` |
| `0x9C4060CC` | READ | 0x833 | **读 I/O 端口（1 字节）**：`in` | `u16 port`（+0） | 1 B，Info=InputBufferLength |
| `0x9C4060D0` | READ | 0x834 | **读 I/O 端口（2 字节）**：`in` | `u16 port` | 2 B |
| `0x9C4060D4` | READ | 0x835 | **读 I/O 端口（4 字节）**：`in` | `u16 port` | 4 B |
| `0x9C406104` | READ | 0x841 | **物理内存读**（`MmMapIoSpace` + 按 1/2/4 字节拷贝） | `u64 phys; u32 unitSize(1/2/4); u32 count`（16 B） | `unitSize*count` 字节，Info=OutputBufferLength |
| `0x9C406144` | READ | 0x851 | **PCI 配置空间读**（`HalGetBusDataByOffset(4=PCIConfiguration,…)`） | `u32 pciAddr; u32 offset`（8 B） | 配置数据（OutputBufferLength 字节） |
| `0x9C40A0C8` | WRITE | 0x832 | 未实现（注释外的 fallthrough → invalid parameter） | — | `STATUS_INVALID_PARAMETER` |
| `0x9C40A0D8` | WRITE | 0x836 | **写 I/O 端口（1 字节）**：`out` | `u16 port`（+0）；值在 +4 | 无 |
| `0x9C40A0DC` | WRITE | 0x837 | **写 I/O 端口（2 字节）**：`out` | `u16 port`；值在 +4 | 无 |
| `0x9C40A0E0` | WRITE | 0x838 | **写 I/O 端口（4 字节）**：`out` | `u16 port`；值在 +4 | 无 |
| `0x9C40A108` | WRITE | 0x842 | **物理内存写**（`MmMapIoSpace` + 按 1/2/4 字节拷贝） | `u64 phys; u32 unitSize; u32 count; u8 data[]`（≥16 B） | 无，Info=0 |
| `0x9C40A148` | WRITE | 0x852 | **PCI 配置空间写**（`HalSetBusDataByOffset(4,…)`） | `u32 pciAddr; u32 offset; u8 data[]`（>7 B） | 无 |
| `0xA2AB208C` | ANY | 0x823 | 非标准魔数 IOCTL：输入长度 ≥4 时向输出写 `u16 1 + u16 0`（疑似“驱动在否/ping”） | ≥4 B | `{1,0}`，Info=4；否则 `0xC0000023` |

硬件访问证据（反编译片段）：
- 端口读：`uVar6 = in(uVar7);`（`0x9C4060CC/0xD0/0xD4`）
- 端口写：`out(uVar7, …);`（`0x9C40A0D8/0xDC/0xE0`）
- MSR：`uVar1 = rdmsr(*param_1);`（`FUN_000114f0`）、`wrmsr(*param_1, …)`（`FUN_00011524`）
- PMC：`uVar1 = rdpmc(*param_1);`（`FUN_00011558`）
- 物理内存：`puVar4 = MmMapIoSpace(*param_1, uVar3, 0); … MmUnmapIoSpace(puVar4, uVar3);`
  （`FUN_0001158c` 读 / `FUN_00011654` 写）
- PCI：`HalGetBusDataByOffset(4, bus, slot, buffer, offset, len)` / `HalSetBusDataByOffset(4, …)`
- 另有 `FUN_00011758` 调 `KeBugCheckEx(0xF7, …)`（`0xF7 = CRITICAL_PROCESS_DIED`？此处仅说明驱动含蓝屏兜底/bugcheck 路径）。

### 1.4 与 EC / Fn 热键的关系判定

- 没有 `WMI`/`ACPI`/`IoWMIWriteEvent`/`WmiSystemControl` 调用；
- 没有硬编码 EC 端口/命令（0x60/0x62/0x64/0x66 等）或 EC 状态机；
- 端口 in/out 是**通用、任意端口**，EC 只是该能力的一个潜在使用者（若要复刻 EC 访问，可以自己走这套端口 IOCTL），
  但原厂热键链路从未调用它（见第 3 节 .NET 侧证据）；
- 结论：**该驱动不参与 Fn 热键监听**，也不提供“热键事件读取/使能”通道。

### 1.5 服务安装状态（运行时只读查询）

```
> sc query type= driver state= all
SERVICE_NAME: ControlCenter
DISPLAY_NAME: ControlCenterService
TYPE               : 1  KERNEL_DRIVER
STATE              : 1  STOPPED
WIN32_EXIT_CODE    : 577  (0x241)

> sc qc ControlCenter
TYPE               : 1  KERNEL_DRIVER
START_TYPE         : 2  AUTO_START
BINARY_PATH_NAME   : \??\C:\Program Files (x86)\Leishen\ControlCenter\ControlCenter64.sys
DISPLAY_NAME       : ControlCenterService
```

- 服务键名为 `ControlCenter`，显示名 `ControlCenterService`（Lead 侧“ControlCenterService STOPPED/577”即指此服务）。
- `577 (0x241) = ERROR_INVALID_IMAGE_HASH`（Windows 系统错误文本：“Windows 无法验证此文件的数字签名…”）。
  文件本身 Authenticode 有效（GlobalSign，2023 时间戳），但内核加载器拒绝该旧驱动（未走 WHQL/微软内核签名通道），
  所以服务起不来。
- 该服务是 `AUTO_START` 但从未加载成功；.NET 侧也不负责启动/停止它（见 3.3）。

---

## 2. ControlCenterC64.dll 逆向

### 2.1 导入/导出与整体形态

- 导入（`analysis/pe_scan.py`）：KERNEL32 / USER32 / GDI32 / MSIMG32 / ADVAPI32 / SHELL32 / SHLWAPI /
  UxTheme / ole32 / OLEAUT32 / OLEACC / gdiplus / IMM32 / WINMM；另有 **`DeviceIoControl`、`CreateFileW`**。
- 链接形态：静态 MFC（反编译中出现 `CMFCToolBarImages`、`CWinApp`、`AfxFillExceptionInfo`），
  大量 GUI/资源代码，2.6 MB。
- 导出（PE Export Table，`analysis/raw/CC64_dll_exports.c`）：
  `func1`(0x24A0)、`ProcSpeedCalc`(0x24D0)、`init3`(0x2810)、`close3`(0x2870)、`GetCoreFrequency1000`(0x28A0)。
- **没有** `OpenSCManager/StartService/CreateService/ControlService` 导入；DLL 不管理驱动服务。

### 2.2 五个导出函数的真实功能（Ghidra 反编译）

```c
// func1 @ 0x24A0：测试桩，弹 MessageBox
hWnd = GetFocus();
MessageBoxW(hWnd, L"Test okay", L"Test", 0);
return 1;

// init3 @ 0x2810：打开驱动设备
if (DAT_180267540 == '\x01') return true;
DAT_180267538 = CreateFileW(L"\\\\.\\CCDRV1", 0xC0000000 /*GENERIC_READ|WRITE*/,
                            0, ..., OPEN_EXISTING(3), 0x80, 0);
DAT_180267540 = (DAT_180267538 != INVALID_HANDLE_VALUE);

// close3 @ 0x2870：CloseHandle + 清标志

// ProcSpeedCalc @ 0x24D0：QueryPerformanceFrequency/Counter + rdtsc，500ms 级采样算主频（不碰驱动）

// GetCoreFrequency1000 @ 0x28A0：读 MSR 0xE7/0xE8 算 APERF/MPERF 比例
if (DAT_180267540 == 0) return 0.0;           // 驱动句柄未打开直接返回 0
QueryPerformanceFrequency(&freq);
uVar1 = rdtsc();  dVar3 = FUN_180002640();     // 读 (MPERF, APERF) 比值
QueryPerformanceCounter(&t0); Sleep(1000);
uVar2 = rdtsc();  dVar4 = FUN_180002640();
QueryPerformanceCounter(&t1);
...
```

MSR 读取封装（`FUN_180002640` → `FUN_1800025a0`）：

```c
// FUN_1800025a0：唯一 DeviceIoControl 调用点
local_20 = param_2;                              // msr 索引
BVar1 = DeviceIoControl(DAT_180267538, 0x9C402084, // RDMSR IOCTL
                        &local_20, 4, param_3, 8, local_28, NULL);
// FUN_180002640：读 0xE7(IA32_MPERF) 与 0xE8(IA32_APERF)，返回 APERF/MPERF
FUN_1800025a0(param_1, 0xE7, &local_20);
FUN_1800025a0(extraout_XMM0_Qa, 0xE8, &local_28);
return (double)local_28 / (double)local_20;
```

- 其余 `DeviceIoControl`/`CreateFileW` 交叉引用都是 MFC/CRT 的 GUI 文件对话框、控制台初始化代码
  （`analysis/raw/CC64_dll_decomp2.c`：`__dcrt_lowio_ensure_console_output_initialized`、
  `CMFCToolBarImages` 载图等），与设备通信无关。

### 2.3 与 Fn 热键的关系

- DLL 只在 .NET 的 `MyQQ_IO`（`decomp/ControlCenter/ControlCenterSpace.BIOS/MyQQ_IO.cs`）中被 P/Invoke，
  唯一调用点是 `MyCpuClockInfo` / `MyControlCenterC.ProcSpeedCalc()`（CPU 主频显示）。
- 没有热键/EC/WMI 相关代码；**不参与 Fn 热键**。
- 注意：由于驱动服务加载失败（1.5），`init3()` 会失败，`GetCoreFrequency1000()` 返回 0.0 —— 新项目若显示 CPU 频率，
  不能依赖这个 DLL/驱动，需另找方法（如 `CallNtPowerInformation`/`ProcessorPerformance` 计数器/`GetSystemTimes` 估算）。

---

## 3. .NET 侧证据：Fn 热键 = 纯 WMI（无驱动）

反编译源码在 `decomp/`（由 fn-original-analyst 提供）。关键点：

### 3.1 事件订阅：`root\wmi` + `GMC_WMIEvent`

`decomp/ControlCenter/ControlCenterSpace/MainWindow.cs:3600-3622`：

```csharp
WqlEventQuery query = new WqlEventQuery("GMC_WMIEvent");
ManagementScope scope = new ManagementScope("root\\wmi");
watcher_acpi = new ManagementEventWatcher(scope, query);
watcher_acpi.EventArrived += HandleWMIACPIEvent;
watcher_acpi.Start();
...
ParseWMIEventData((byte[])e.NewEvent["EventDetail"]);
```

同样逻辑出现在 `ControlCenterDaemon`（`decomp/ControlCenterDaemon/ControlCenterDaemon/ControlCenterDaemon.cs:420/439`）
和 `LEDCtrl.cs`（常量 `WMIACPIEventClass = "GMC_WMIEvent"`、`evBytesLength = 32`）。

### 3.2 SMI 通道：`root\wmi:RW_GMWMI` 的 32 字节 `BufferBytes`

`decomp/ControlCenter/ControlCenterSpace/MyWmiBase.cs`：

```
WMINamespace = "root\\wmi";  WMIClassRW_GMWMI = "RW_GMWMI";  BUFFER_BYTES_SIZE = 32;
读：ManagementObjectSearcher(scope, new SelectQuery("RW_GMWMI")) → current["BufferBytes"]
写：obj.SetPropertyValue("BufferBytes", data); obj.Put();
```

`SMI_STRUCT_S`（32 B）= `u16 a0; u16 a1; u32 a2; u32 a3; u32 a4; u32 a5; u32 a6; u32 rev0; u32 rev1;`
- LED：`a0=64256`(0xFB00)、`a1=256`(0x0100)、`a2=zone`、`a3=ARGB`；
- 清事件位：`a0=64256`、`a1=1024`(0x0400)、`a2/a3/a4` 按事件类型填（见 3.4）。
- 字节序：`SMI_STRUCT_S.byte_swap(..., swap:false)` 即按原字节拷入 WMI BufferBytes。

### 3.3 服务管理：.NET 不启动 `ControlCenter` 驱动服务

`decomp/ControlCenter/ControlCenterSpace/SCMService.cs` 只操作 `XTU3SERVICE`（Intel XTU 服务）：
`ServiceController("XTU3SERVICE").Start()`；`ControlCenterDaemon` 中也无 `ControlCenterService/CCDRV1/.sys`
相关代码（全库 `grep ControlCenterService|ControlCenter64.sys|CreateService|StartService|OpenSCManager` 无命中）。
→ 驱动服务由安装器（`InstallerSub2.exe` 等）注册，运行期无人使用。

### 3.4 `GMC_WMIEvent` 的 EventDetail 解析（`MainWindow.cs:3625-3760`）

`ParseWMIEventData(byte[] evBytes)`，要求长度 32：
- `Event_Alpha` 初值 0xFF；首个事件把 `evBytes[0]` 记为 alpha；之后只有 `evBytes[0] == alpha` 才继续解析。
- `evBytes[0]` 同时被当作“亮度值”打印（`Brightness value=…`）。
- `evBytes[4] == 1` → HDMI 事件；`evBytes[5] == 159` → 适配器插入；`evBytes[6]` → 摄像头（1=ON，2=OFF）；
  `evBytes[7] != 0` → 性能模式事件（按注册表 `POWERPLAN` 轮换 0/1/2 档）；`evBytes[8] != 0` → “UI 已存在”事件。
- 处理完调用 `SetEventStatus(EventID, 1)` → `RW_GMWMI` SMI `a0=0xFB00, a1=0x0400`，`a2/a3/a4` 分别对应
  performance(2)/CC(3)/camera(4) 事件位，即**清事件位**（这正是我们实测“清位需可观测/重试”的来源）。
- 背光：`evBytes[4..8] 全 0` 且 `IsKeyboardEvent` 时，用 `evBytes[0]` 调 `func_backlight()`（回显/延迟的来源）。

> 这套 `GMC_WMIEvent` 报文格式（`b0=背光/alpha, b4=HDMI, b5=适配器, b6=摄像头, b7=性能, b8=CC`）与
> HotKey OSD 的 `WMI2Event`（`b0=EventID, b[i]=data`）**是两路不同事件流**，见
> `analysis/hotkey-osd-report.md` 第 2 节的 CIM 仓库证据（GUID 不同、ACPI 设备实例不同）。

---

## 4. 可复刻的调用序列（结论）

### 4.1 Fn 热键监听（推荐，当前项目已实现）

**不需要** `ControlCenter64.sys`，也不需要 `ControlCenterC64.dll`。两种来源任选/并用：

```csharp
// 来源 A：原厂 ControlCenter 同款（GMC 私有事件流，含背光/性能/CC/摄像头）
var scopeA = new ManagementScope(@"root\wmi");
var qA = new WqlEventQuery("GMC_WMIEvent");        // GUID {74286D6E-429C-427a-B34B-B5D15D032B05}
var wA = new ManagementEventWatcher(scopeA, qA);
wA.EventArrived += (s, e) => {
    var b = (byte[])e.NewEvent["EventDetail"];    // 32 B
    if (b.Length != 32) return;
    // b[0]=背光/alpha；b[4]=HDMI；b[5]=适配器(159)；b[6]=摄像头(1/2)；b[7]=性能；b[8]=CC
    if (b.Length == 32 && (b[7] != 0 || b[6] != 0 || b[8] != 0)) {
        // 清位（可重试）：RW_GMWMI 写 a0=0xFB00, a1=0x0400, a2/a3/a4 对应事件位
    }
};
wA.Start();

// 来源 B：HotKey OSD 同款（WMI2Event，含 fan/touchpad/fnlock/camera/mic/性能模式）
var qB = new WqlEventQuery("SELECT * FROM WMI2Event");  // GUID {ABBC0F20-...}
var wB = new ManagementEventWatcher(scopeA, qB);
wB.EventArrived += (s, e) => {
    var b = (byte[])e.NewEvent["EventDetail"];          // 32 B
    if (b.Length != 32 || b[0] == 0xFF) return;         // 与 OSD 一致
    byte ev = b[0];
    // dataIndex 查表见 hotkey-osd-report.md；data = b[dataIndex]
};
wB.Start();
```

复刻要点（与既有实现对照）：
- 两个 `ManagementEventWatcher` 同时挂 `root\wmi` 没有互斥问题（WMI 事件按订阅广播）。
- `GMC_WMIEvent` 需要 `RW_GMWMI` 清位（`SMI a0=0xFB00,a1=0x0400`）；`WMI2Event` 无清位通道（MOF 无方法、OSD 未清）。
- 订阅建立失败/服务重启后要重订阅；`WMI2Event` 类可能因 `mofcomp uw3.mof` 未执行而不存在 → 需要 fallback。
- 32 字节长度校验、`0xFF` 无效事件丢弃，与 OSD 行为一致。

### 4.2 CPU 频率（可选，若仍想用原厂 DLL）

若驱动成功加载：`CreateFile("\\.\CCDRV1")` → `DeviceIoControl(0x9C402084, {u32 msr}, out u64)` 读
`0xE7/0xE8`。**但本机驱动加载失败（577），该路径不可用**；建议改用性能计数器/`GetSystemTimes` 等替代。

---

## 5. 验证边界与限制

- 全部结论来自静态反编译 + PE 导入/字符串 + 只读系统查询；**未**动态加载驱动、**未**启动/停止服务。
- 非管理员无法访问 `root\wmi`（`Get-WmiObject`/`Get-CimClass` 返回“拒绝访问”），因此无法在本会话动态枚举/订阅
  `GMC_WMIEvent`、`WMI2Event` 验证类是否存在；类定义证据来自 CIM 仓库文件 `OBJECTS.DATA`（见 OSD 报告）。
- 驱动 IOCTL 表为静态判读；由于驱动加载失败（577），无法动态验证 IOCTL 行为。
- `0x9C4060C4`、`0x9C40A0C8` 两个 IOCTL 在分发函数中未实现（返回 `STATUS_INVALID_PARAMETER`），可能是历史遗留定义。
- 驱动 PDB 路径显示其来源为 2017 年 Quanta LED/POST 测试项目（`1610_nl5_led/js970_driver/jspostestercmd`），
  与当前 2023 版控制中心不是同一代代码。

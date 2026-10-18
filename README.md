# 雷神控制中心 · 重建版（LeishenCC）

从零复刻的雷神（Leishen）笔记本控制中心桌面程序，Windows 11 原生风格 WPF 实现。

原厂 `ControlCenter` 的硬件控制协议通过逆向分析还原，本仓库只包含**自行编写的代码**，不含任何第三方程序的反编译产物。

---

## 特性

| 页面 | 功能 |
| --- | --- |
| 概览 | CPU/GPU/内存实时指标、当前状态汇总、可选指标的历史趋势折线图（1/3/5 分钟） |
| 性能模式 | 高性能 / 游戏 / 办公静音切换，可联动 Windows 电源计划 |
| 风扇与散热 | 3 档位 × CPU/GPU/系统 三路风扇曲线编辑，可拖动折点；**常驻每 2 秒按曲线下发，与窗口是否可见无关** |
| 键盘灯效 | 4 套灯效档案 × 5 个灯区，模式/亮度/RGB 与预设色 |
| 显卡与显示 | 混合输出 / 独显直连 / 核显切换，HSR 刷新率切换 |
| 系统设置 | **开机自启（计划任务替代原厂）**、Win 键屏蔽、摄像头启停、Fn 组合键监听、托盘与主题 |
| 接口诊断 | 原始 SMI 命令收发（a0–a6 十六进制输入）与调用日志 |

- **Fn 组合键监听**：复刻原厂守护进程行为，监听 `root\wmi:GMC_WMIEvent`，响应性能模式键、控制中心键、摄像头键与背光键。
- **界面**：Fluent / OpenFRP 风格的侧边栏（主区菜单 + 底区分页），可收起为仅图标；页面切换淡入动效；沉浸式无边框标题栏与覆盖式细滚动条；深浅双主题。

---

## 验证环境

本项目的协议结论与界面均在下述设备上实测验证：

| 项目 | 值 |
| --- | --- |
| 机型 | THUNDEROBOT R16（911 series） |
| BIOS | AMI `TP162`（ALASKA - 1072009），发布日期 2024-06-11 |
| 原厂控制中心版本 | **Control Center 3.0.0.22**（`ControlCenter.exe` 与 `ControlCenterDaemon.exe` 同版本） |
| 原厂安装路径 | `C:\Program Files (x86)\Leishen\ControlCenter\` |
| 原厂附带组件 | `CTS.exe` 1.0.0.3、`InstallerSub2.exe` 3.0.0.9、`SetPwrPlan.exe` 1.0.0.4 |
| EC / BIOS 接口版本 | SMI 版本 `0x0200`（512），SKU `52` |
| 本程序版本 | 1.0.0（`Leishen Control Center (Rebuild)`） |

### 接口能力实测

以下为 `0xFA00` 查询的实机快照（接电状态）：

| 查询 | 返回 | 含义 |
| --- | --- | --- |
| `0x0201` 能力 | a0=`0x0200`、a2=`52` | SMI 版本 512；SKU 52 → 三风扇 + 自定义风扇可用 |
| `0x0202` 硬件状态 | a3=`1`、a4=`1`、a5=`1`、a6=`1` | 适配器已接、摄像头状态 1；`a4==2` 判定 HSR、`a6==2` 判定 DDS |
| `0x0203` 显示 | a2=`2`、a4=`2` | 当前独显直连（1=混合 2=独显 3=UMA） |
| `0x0205` 风扇默认表 | a2=`4` | 默认风扇表 ID = 4 |

SKU 与能力位随BIOS 版本而异：`Support3Fan` 判定为 `SKU ∈ {52, 53}`，`SupportUserFan` 判定为 `SMI 版本 ≥ 512`。

---

## 硬件控制协议

通道：`root\wmi` 命名空间下的 `RW_GMWMI`，通过 32 字节 `BufferBytes` 读写，即原厂 `MyWmiBase.ACPIPerformSMI`。

结构体（小端）：`a0(2) a1(2) a2(4) a3(4) a4(4) a5(4) a6(4) rev0(4) rev1(4)`。
方向位：`a0 = 0xFA00`（GET，64000）/ `0xFB00`（SET，64256）。

### GET (`0xFA00`)

| a1 | 含义 | 返回 |
| --- | --- | --- |
| `0x0100` | LED 状态 | — |
| `0x0200` | 温度与转速 | a2=CPU 温度 a3=GPU 温度 a4=CPU 转速 a5=GPU 转速 |
| `0x0201` | 能力查询 | a0=SMI 版本 a2=SKU |
| `0x0202` | 硬件状态 | a3=适配器 a4=HSR(==2) a5=摄像头 a6=DDS(==2) |
| `0x0203` | 显示状态 | a2=当前 a3=HDMI a4=初始 |
| `0x0204` | HSR | — |
| `0x0205` | 风扇默认表 ID | — |
| `0x0206` | 自定义风扇状态 | a2=1 开启 |
| `0x0207` | 系统温度/风扇 | a2=温度 a3=转速 |

### SET (`0xFB00`)

| a1 | 含义 | 参数 |
| --- | --- | --- |
| `0x0100` | LED 设置 | a2=灯区 a3=LED 数据 |
| `0x0200` | Win 键 | a6=1 屏蔽 a6=0 恢复 |
| `0x0203` | 显示模式 | a2: 1=混合 2=独显 3=UMA |
| `0x0204` | HSR | a2=档位 |
| `0x0205` | 风扇转速 | a2=CPU% a3=GPU% a4=SYS% |
| `0x0206` | 风扇控制开关 | a2=1/0 |
| `0x0300` | 性能模式 | a2: 0=高性能 1=游戏 2=办公 |
| `0x0400` | 事件清除 | a2=性能键 a3=控制中心键 a4=摄像头键 |

### LED 数据位域

```
bit31..28  模式（0 关闭 / 1 常亮 / 3 呼吸 / 6 彩色循环 / 7 氛围灯）
bit27..24  亮度 0-15
bit23..16  R
bit15..8   G
bit7..0    B
```
灯区：5=键盘区1、4=键盘区2、3=键盘区3、8=LOGO、7=尾部灯带。

### Fn 组合键事件

事件类 `root\wmi:GMC_WMIEvent`，负载 `EventDetail` 为 32 字节：

| 字节 | 含义 |
| --- | --- |
| `[0]` | 键盘背光亮度（0-15） |
| `[4]` | ==1 外接显示事件 |
| `[5]` | ==159 电源适配器事件 |
| `[6]` | 摄像头（1=开 / 2=关） |
| `[7]` | 性能模式键 |
| `[8]` | 控制中心键 |

处理完必须下发 `0xFB00/0x0400` 清事件，否则 EC 会一直挂起该事件。

### 风扇曲线

8 个温度断点 `30/40/50/60/70/80/90/100 ℃`，注册表中存为 `30,30,30,30,50,60,70,100` 形式的逗号串，双风扇用 `;` 分隔 CPU/GPU，系统风扇单独存 `UserFan*_SYS`。

### 注册表

与原厂共用 `HKLM\SOFTWARE\WOW6432Node\LeiShen\ControlCenter`（`Game`/`HighPerformance`/`PowerSaving`/`Custom`/`LEDModule`/`WinKey`/`FanControlStatus`/`FanControlSelect`/`UserFan*`）；本程序自身的界面偏好存 `HKCU\SOFTWARE\LeishenCC`。

---

## 构建

需要 .NET 5 SDK（Windows）。

```bash
cd src/LeishenCC
dotnet build -c Release
```

产物：`src/LeishenCC/bin/Release/net5.0-windows/LeishenCC.exe`。程序清单带 `requireAdministrator`，写入 EC / 注册表需要管理员权限。

自检用命令行参数：

| 参数 | 作用 |
| --- | --- |
| `--shot <目录> [light]` | 逐页自渲染截图，用于界面回归 |
| `--fnprobe <秒数>` | 抓 Fn 组合键原始事件，写入 `fnprobe.log` |

---

## MCP 服务（可选，仅本地）

`tools/FanMcp` 提供一个 stdio 传输的 MCP 服务，让本地 agent 直接读写自定义风扇曲线。

- **仅本地可达**：使用 stdio，不监听任何端口，只能由 MCP 客户端以子进程方式拉起，不存在对外暴露面。
- **复用同一份源码**：直接链接 `src/LeishenCC/Core` 下的 `GmWmi` / `FanProfile` / `Settings` / `SystemStats`，与界面行为一致（每 2 秒按「曲线 + 当前温度」下发）。
- **安全兜底**：进程退出（含 stdin EOF）时，只有本进程接管过风扇才会自动下发 `0xFB00/0x0206` 交回 BIOS；从未接管则不触碰当前状态。

### 工具

| 工具 | 作用 |
| --- | --- |
| `fan_status` | 控制开关、三路温度/转速、曲线目标占空比、最近下发结果（只读） |
| `fan_list_profiles` | 三档曲线与当前档位（只读） |
| `fan_set_curve` | 写入某档曲线（仅注册表，8 点对应 30–100℃） |
| `fan_apply_slot` | 启用某档并接管，按曲线持续下发 |
| `fan_set_speed` | 立即下发固定占空比（不随温度变化） |
| `fan_stop` | 关闭自定义控制，交回 BIOS |
| `fan_reset_curve` | 恢复该机型默认曲线（按风扇表编号） |
| `ec_read` | 只读诊断查询（`0xFA00`，返回 a0/a2..a6） |

### 构建与接入

```bash
cd tools/FanMcp
dotnet build -c Release
```

MCP 客户端配置（`mcpServers` 格式）：

```json
{
  "mcpServers": {
    "leishen-fan": {
      "command": "C:/Users/<you>/Documents/LeishenReverse/tools/FanMcp/bin/Release/net5.0-windows/LeishenFanMcp.exe",
      "args": []
    }
  }
}
```

自检：`python tools/FanMcp/probe.py` —— 走一遍握手、列工具、读曲线，并把曲线**原样写回**，不改变实际风扇行为。

> 桌面端与本服务通过命名互斥量 `Local\LeishenFanOwner` 互斥：只要 LeishenCC 开着自定义风扇，外部 `--hold` 会直接退出；反之桌面端会让位。

---

## 目录结构

```
src/LeishenCC/
  Core/        GmWmi（SMI 协议）、Settings、LedData、FanProfile、Nvml、
               CameraService、PowerPlanService、SystemStats、Hub、
               FnKeys / FnKeyService（Fn 事件）、SampleHistory（采样历史）、
               FanControlService（常驻风扇下发）、StartupTask（开机自启接管）
  Views/       七个功能页 + CurveEditor + TrendChart
  Theme/       Dark / Light 配色与公共样式
  MainWindow.xaml(.cs)
tools/Decomp/         自建的反编译 CLI（ICSharpCode.Decompiler），用于还原原厂结构
tools/FanMcp/         本地 MCP 服务（stdio），把风扇曲线控制暴露给 agent
tools/StartupProbe/   自启诊断（复用 StartupTask，只读检查或 --enable/--disable）
```

## 状态刷新机制

- 1 秒快刷新：温度 / 占用 / 转速，并写入采样历史。
- 5 秒慢刷新：重读显示输出、风扇控制、摄像头与性能模式，与上次快照比对，变化才刷新界面（对应原厂 `timer2` 5 秒 tick）。
- 切页、从托盘恢复、Fn 键切换模式时立即补一次刷新。
- 页面事件订阅采用 `Loaded`/`Unloaded` 成对方式，切页回来后不会停止刷新。

## 风扇常驻下发（`Core/FanControlService`）

原实现的下发写在 `FanView.OnUpdated` 里，而该订阅随页面 `Unloaded` 取消——打游戏时窗口在托盘、页面不在可视树，**下发会停止**，EC 就锁在最后一次的占空比上（实测：50 分钟游戏转速恒定 2886 RPM，CPU 卡在 82℃ 温度墙而风扇只有 ~65%）。

现在改为独立服务：

- 用 `System.Threading.Timer` 每 2 秒下发，**与页面可见性无关**，只要 LeishenCC 在运行（含托盘）就持续生效。
- 取档带 **2℃ 滞回**：升温立即升档，降档需低于当前档下界 2℃，避免原厂 10℃ 阶梯在断点附近抖动（实测 2630↔3617 RPM 来回跳）。
- 连续 5 次下发失败自动交回 BIOS，不会锁死在错误占空比。
- **程序退出时自动 `SetFanControl(false)` 交回 BIOS**，避免把风扇锁在最后的设定值。
- 与外部 `LeishenFanMcp --hold` 用 `Local\LeishenFanOwner` 互斥，谁先接管谁生效。

---

## 开机自启：替代原厂控制中心（`Core/StartupTask`）

原厂的登录自启不是 Run 键，而是一个计划任务：

```
\ControlCenter\ControlCenter  →  ControlCenterDaemon.exe /1
                                LogonTrigger · RunLevel=HighestAvailable
```

重置版沿用同一机制（`系统设置 → 开机自启`）：

- **接管**：注册 `\LeishenCC` 计划任务（登录触发、`HighestAvailable`、延迟 8 秒、带 `--startup` 参数），并把原厂任务 `Enabled=false`。
- **取消**：删除 `\LeishenCC`，把原厂任务恢复为启用。所有操作可逆，原任务定义另外备份在 `backup/ControlCenter.task.xml`。

为什么不用 `HKCU\...\Run`：本程序是 `requireAdministrator` 清单，走 Run 键每次登录都会弹一次 UAC；计划任务用 `HighestAvailable + InteractiveToken`，登录时静默提权，不弹窗。旧版本写过的 Run 键会在接管/取消时自动清掉。

启动参数：

| 参数 | 行为 |
|---|---|
| `--startup` | 不显示主窗口，直接驻留托盘（计划任务使用），并弹一次托盘提示 |
| `--startup-enable` / `--startup-disable` | 不建窗口，直接接管 / 恢复开机自启，结果写 `startup.log` 后退出（便于脚本化） |
| （无参数） | 正常显示窗口 |

配套细节：

- **单实例**：`Local\LeishenCC.App` 互斥量；重复启动会通过命名事件 `Local\LeishenCC.Show` 让已有实例把窗口显示出来，而不是开出第二个托盘图标。
- **路径自愈**：程序被移动或重新构建后，若任务里记录的 exe 路径与当前不一致，启动时自动按当前路径重新注册。
- **状态三态**：任务状态分「启用 / 禁用 / 不存在 / 读不到」；非管理员读不到 `\ControlCenter\` 文件夹时会如实提示，不会误判成「未启用」。
- 自检：`tools/StartupProbe`（编译后运行，输出 `startupprobe.log`；`--enable` / `--disable` 可直接执行接管/恢复）。

---

## 免责声明

本项目仅用于个人设备上的学习与自用，不包含、也不分发任何第三方程序的代码或二进制文件。硬件协议结论来自对本人设备上接口的观测与实机验证，不同机型/BIOS 版本可能存在差异。修改风扇曲线、功耗与显示输出存在风险，请自行评估。

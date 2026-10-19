using System;
using System.Text;

namespace LeishenCC.Core
{
    /// <summary>
    /// 性能模式（EC `0xFB00 / 0x0300`）的唯一入口。
    ///
    /// 之前 Fn 键与界面各写一套，而且都只管 SMI + 注册表，不管风扇：
    /// 自定义风扇开着时，<see cref="FanControlService"/> 每 2 秒按「当前档位曲线」重写一次占空比，
    /// 把刚下发的模式风扇策略整个盖掉，用户看到的就是「按 Fn+1 没反应」。
    ///
    /// 现在统一下发顺序：
    ///   SMI → 模式状态（与原厂共用 ModeBeforeDC）→ Windows 电源计划（可选）→ 风扇档位联动 → 通知界面。
    /// </summary>
    public static class PowerModeService
    {
        public static readonly string[] Names = { "高性能", "游戏", "办公 / 静音" };

        /// <summary>模式变化（Fn 键与界面两条路径都会触发）。</summary>
        public static event Action<int> Changed;

        public static int Current => Settings.PowerMode;

        /// <summary>原厂循环顺序：0(高性能) → 2(办公) → 1(游戏) → 0(高性能)。</summary>
        public static int NextMode(int current) => current != 0 ? current - 1 : 2;

        /// <summary>Fn 性能模式键：按原厂顺序切到下一档。</summary>
        public static bool Cycle(string source) => Apply(NextMode(Current), source, Settings.PlanFollowMode);

        /// <summary>切换性能模式。返回 false 表示 SMI 下发失败（状态仍会记录，便于下次重试）。</summary>
        public static bool Apply(int mode, string source, bool switchPlan)
        {
            mode = Math.Max(0, Math.Min(2, mode));
            int cur = Current;

            bool ok = Hub.WmiOk && Hub.Wmi.SetPowerMode(mode);
            Settings.PowerMode = mode;

            bool planOk = false;
            if (switchPlan) planOk = PowerPlanService.Apply(mode);

            // 关键一步：自定义风扇运行时把档位切到对应模式并立即重发，否则模式切换会被 2 秒周期覆盖
            string fan = FanControlService.FollowPowerMode(mode);

            var sb = new StringBuilder();
            sb.AppendFormat("性能模式[{0}]: {1} → {2}", source, Names[cur], Names[mode]);
            sb.Append("（SMI=").Append(Hub.WmiOk ? (ok ? "成功" : "失败") : "EC 不可用");
            sb.Append("，电源计划=").Append(switchPlan ? (planOk ? "已切换" : "切换失败") : "不联动");
            if (!string.IsNullOrEmpty(fan)) sb.Append("，").Append(fan);
            sb.Append("）");
            Hub.Write(sb.ToString());

            Changed?.Invoke(mode);
            Hub.RaiseUpdated();     // 概览页的「当前状态」与风扇页同步刷新
            return ok;
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // 无限剑雨（Rage）只在 NORMAL 将出剑频率提高 1.5 倍；EASY/HARD 保持原版。
    //
    // 剑雨的循环在 `Absolute Radiance/Control` FSM（与 NailTopSweep 里那处 `Rage Comb` 同一 FSM）：
    //   Arena 1 Idle --RAGE--> Rage1 Tele（瞬移）--TELEPORTED--> Rage1 Antic（播 "Antic" 前摇动画）
    //     --FINISHED--> Rage1 Start（播 "Cast" + Wait 1s + 发 RAGE START）--FINISHED-->
    //   Rage Comb（Wait 0.85s → SpawnObjectFromGlobalPool 生成一剑 Radiant Nail Comb，Type=3=Top）
    //     --FINISHED--> Rage1 Loop（无动作，当帧就 FINISHED）--FINISHED--> 回 Rage Comb …
    //
    // 所以**每轮剑雨的间隔 = `Rage Comb` 里那个 Wait**（`Rage1 Loop` 是空状态，当帧回环，
    // 不额外占时间）。原版 0.85s，NORMAL 按「原值 ÷ 1.5」缩短间隔（Z/H 不改）。
    // 前摇（起手到第一波）含 `Rage1 Start` 的 Wait 1s + `Rage1 Antic` 的动画时长 + 瞬移，本补丁不动。
    //
    // 只原位改 Wait.time，不增删动作（见 [[feedback-fsm-index-stability]]）；改前的值记下来供关总开关写回。
    internal static class RageRain
    {
        private const string StateName = "Rage Comb";

        // NORMAL 用 1.5 倍频率，其余模式倍率为 1。

        private static readonly Dictionary<Wait, float> Original = new Dictionary<Wait, float>();

        // 每次遇到 FSM 都重新扫一遍，不靠全局「已处理」标记：新一局的 Control FSM 是新对象，
        // 旧标记会把它跳过 → 第二局起剑雨节奏失效。幂等靠两点：① 原值按 Wait 实例只记一次；
        // ② 每轮都从「记下的真原值」重算 → 重复调用不会累乘。
        internal static void Patch(Fsm fsm)
        {
            FsmState st = fsm.GetState(StateName);
            if (st == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Control 里没有 " + StateName + " 状态，剑雨节奏未改");
                return;
            }

            Wait w = st.Actions.OfType<Wait>().FirstOrDefault();
            if (w == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + StateName + " 里找不到 Wait，剑雨节奏未改");
                return;
            }

            bool firstTime = !Original.ContainsKey(w);
            float oldTime = w.time.Value;
            if (firstTime) Original[w] = oldTime;        // 只记一次真原值
            float speedup = !OrbRadiance.EffectiveHardMode && !OrbRadiance.EffectiveEasyMode ? 1.5f : 1f;
            w.time.Value = Original[w] / speedup;        // 恒由真原值推出，模式切换不累乘

            if (firstTime || oldTime != w.time.Value)
            {
                string mode = OrbRadiance.EffectiveHardMode ? "H" : (OrbRadiance.EffectiveEasyMode ? "Z" : "N");
                OrbRadiance.Instance.Log("OrbRadiance: 无限剑雨模式 " + mode + " 间隔 " + Original[w] + "s → " +
                                         w.time.Value.ToString("0.###") + "s（×" + speedup + "）");
            }
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<Wait, float> kv in Original)
                if (kv.Key != null) kv.Key.time.Value = kv.Value;
            Original.Clear();
        }
    }
}

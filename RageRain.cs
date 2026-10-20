using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // 无限剑雨节奏：NORMAL 提速 1.5 倍，Z/H 保留原版间隔；只改动 Wait.time。


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

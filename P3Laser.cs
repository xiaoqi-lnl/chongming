using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // P3 的原版 ASCEND BEAM 仅在 N/H 抑制，Z 保留原版激光。


    internal static class P3Laser
    {
        private const string StateName = "Ascend Cast";
        private const string EventName = "ASCEND BEAM";

        // 改前的 Enabled，只在总开关关掉时写回（只记一次）
        private static readonly Dictionary<SendEventByName, bool> Original =
            new Dictionary<SendEventByName, bool>();

        // 每次遇到 FSM 都重新扫一遍，不靠任何全局「已处理」标记：新一局的 Control FSM 是
        // 新对象，旧标记会把它误判成「已处理」而跳过 —— 那正是第二局 P3 又放激光的根因。
        // 幂等靠两点：① 原值按动作实例只记一次（ContainsKey）；② Enabled 恒置 false。
        internal static void Patch(Fsm fsm)
        {
            FsmState st = fsm.GetState(StateName);
            if (st == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Control 里没有 " + StateName + " 状态，P3 激光未关");
                return;
            }

            int n = 0, newlyRecorded = 0;
            foreach (SendEventByName se in st.Actions.OfType<SendEventByName>())
            {
                if (se.sendEvent == null || se.sendEvent.Value != EventName) continue;
                if (!Original.ContainsKey(se))
                {
                    Original[se] = se.Enabled;   // 只记一次真原值，重复调用不再覆盖
                    newlyRecorded++;
                }
                se.Enabled = false;              // 幂等：重复调用不产生额外变化
                n++;
            }

            if (n == 0)
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + StateName + " 里没有发 " + EventName +
                                             " 的 SendEventByName，P3 激光未关");
            else if (newlyRecorded > 0)
                OrbRadiance.Instance.Log("[p3laser] 关掉 " + StateName + " 的 " + EventName +
                                         " 送信（" + n + " 个）→ N/H P3 由自定义光束组件接管");
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<SendEventByName, bool> kv in Original)
                if (kv.Key != null) kv.Key.Enabled = kv.Value;
            Original.Clear();
        }
    }
}

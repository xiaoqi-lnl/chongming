using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // P3 攀爬段：不放原版激光（照天国余晖）。
    //
    // 原版 `Absolute Radiance/Control` 的 `Ascend Cast` 状态 = P3 攀爬段常驻态
    // （`Ascend Tele --FINISHED--> Ascend Cast`，等玩家爬到 Y>152 才 `NEXT → Scream`）。
    // 它第 2 个动作是 `SendEventByName "ASCEND BEAM"`（eventTarget = FSM Owner）——
    // 全场景只有这一处发这个事件（`Absolute_Radiance__Control__fsm_3672.md:4197`），
    // `Attack Commands` 收到后进 `AB Start → … → Aim → Aim Back` 循环，就是 P3 那发
    // 追着玩家打的攀爬激光（mod3 AscendLaserDeflect 改的正是这条链的偏转）。
    //
    // 天国余晖普通模式下把这个发射器从 `Ascend Cast` 里删掉（RadianceBoss.cs:537-570）→ P3 无激光。
    // 这里做同一件事，但按项目一贯约束不增删动作（见 [[feedback-fsm-index-stability]]）：
    // 只把那个 `SendEventByName` 的 `Enabled` 置 false —— PlayMaker 的 `FsmState.ActivateActions`
    // 会跳过 Enabled=false 的动作，效果与删除等价、后续动作下标不动。
    //
    // 不用加相位门：`Ascend Cast` 整场只在 P3 出现（P1/P2/P4 都到不了它），关它只影响 P3。
    // `Ascend Cast` 自己的推进链（Tk2dPlayAnimation / GetPosition 跟随玩家 Y / FloatCompare→NEXT）
    // 一个没动，爬到顶照样转 P4。
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
                                         " 送信（" + n + " 个）→ P3 不再放原版攀爬激光");
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<SendEventByName, bool> kv in Original)
                if (kv.Key != null) kv.Key.Enabled = kv.Value;
            Original.Clear();
        }
    }
}

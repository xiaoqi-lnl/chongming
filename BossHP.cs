using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // 统一管理血量与阶段阈值：总血量 5000，P2=2450，P3=Z/N 1050、H 750。
    // P4 重置为 1600，处决线 Z/N=600、H=300；阈值依次通过 Phase Control 的 Check 状态触发。


    internal static class BossHP
    {
        internal const int Total = 5000;

        // Check 链的 4 个阈值（剩余血量）
        internal const int HalfSpikeHP = 4350;   // "P2 Spike Waves"：结构性取值，必须 > RageHP，不产生可见相位
        internal const int RageHP = 3150;        // "P3 A1 Rage"：进入无限落刺
        internal const int Phase2HP = 2450;      // "P4 Stun1"：无限落刺结束，准备转 P2
        internal const int AscendHP_ZN = 1050;  // Z/N：P2 结束，转 P3
        internal const int AscendHP_H = 750;     // H：P2 结束，转 P3

        // P4
        internal const int Phase4HP = 1600;     // Scream 里 SetHP 重置值，所有难度保持
        internal const int DeathHP_ZN = 600;    // Z/N 处决血线
        internal const int DeathHP_H = 300;     // H 处决血线不变
        internal static int AscendHP => OrbRadiance.EffectiveHardMode ? AscendHP_H : AscendHP_ZN;
        internal static int DeathHP => OrbRadiance.EffectiveHardMode ? DeathHP_H : DeathHP_ZN;

        // 同一个 FSM 只打一次（不然第二次 OnEnable 会把我们改过的值当成「原值」记下来）
        private static readonly HashSet<PlayMakerFSM> HookedControl = new HashSet<PlayMakerFSM>();
        private static readonly HashSet<PlayMakerFSM> HookedPhase = new HashSet<PlayMakerFSM>();

        private static readonly Dictionary<FsmInt, int> Original = new Dictionary<FsmInt, int>();

        // `Absolute Radiance/Control`：总血 + P4 重置值 + 处决血线
        internal static void PatchControl(PlayMakerFSM fsm)
        {
            if (!HookedControl.Add(fsm)) return;

            HealthManager hm = fsm.gameObject.GetComponent<HealthManager>();
            if (hm != null) hm.hp = Total;
            else OrbRadiance.Instance.LogWarn("OrbRadiance: Absolute Radiance 上没有 HealthManager，总血未改");

            FsmState scream = fsm.Fsm.GetState("Scream");
            SetHP p4Hp = scream == null ? null : scream.Actions.OfType<SetHP>().FirstOrDefault();
            if (p4Hp != null && p4Hp.hp != null) SetInt(p4Hp.hp, Phase4HP);
            else OrbRadiance.Instance.LogWarn("OrbRadiance: Control 的 Scream 里找不到 SetHP，P4 血量未改");

            FsmInt deathHp = fsm.FsmVariables.GetFsmInt("Death HP");
            if (deathHp != null) SetInt(deathHp, DeathHP);
            else OrbRadiance.Instance.LogWarn("OrbRadiance: Control 里没有 Death HP 变量，P4 处决血线未改");
        }

        // `Absolute Radiance/Phase Control`：4 个相位阈值
        internal static void PatchPhase(PlayMakerFSM fsm)
        {
            if (!HookedPhase.Add(fsm)) return;

            SetVar(fsm, "P2 Spike Waves", HalfSpikeHP);
            SetVar(fsm, "P3 A1 Rage", RageHP);
            SetVar(fsm, "P4 Stun1", Phase2HP);
            SetVar(fsm, "P5 Acend", AscendHP);

            OrbRadiance.Instance.Log("OrbRadiance: 血量 5000 / 无限落刺 " + RageHP + " / 转P2 " + Phase2HP +
                                     " / 转P3 " + AscendHP + " / P4 重置 " + Phase4HP + " / P4 结束 " + DeathHP);
        }

        private static void SetVar(PlayMakerFSM fsm, string name, int value)
        {
            FsmInt f = fsm.FsmVariables.GetFsmInt(name);
            if (f == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Phase Control 里没有 " + name + " 变量，该阈值未改");
                return;
            }
            SetInt(f, value);
        }

        private static void SetInt(FsmInt f, int value)
        {
            if (!Original.ContainsKey(f)) Original[f] = f.Value;
            f.Value = value;
        }

        // 关总开关时写回原值。清掉已挂钩集合，之后重新开启（下一次 OnEnable）能重新打。
        internal static void Restore()
        {
            foreach (KeyValuePair<FsmInt, int> kv in Original)
                if (kv.Key != null) kv.Key.Value = kv.Value;
            Original.Clear();
            HookedControl.Clear();
            HookedPhase.Clear();
        }
    }
}

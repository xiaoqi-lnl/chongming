using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // 辐光血量规划（照天国余晖 RadianceBoss.cs 的 HookControl / HookPhase，数值按用户 2026-10-05 定）。
    //
    // 总血：`Absolute Radiance/Control` 启用时把 HealthManager.hp 设成 5000（原版 3200）。
    //
    // 相位切换靠 `Absolute Radiance/Phase Control` 里的 4 个 FsmInt 阈值，原版每个 `Check` 状态
    // 做 `GetHP`(取剩余血) + `IntCompare`(HP ≤ 阈值 → NEXT)，**顺序推进**（不是循环轮询）：
    //   Idle 1 --TOOK DAMAGE--> Check 1 --NEXT--> Set Phase 2 --FINISHED--> Idle 2
    //       Check 1 的阈值 = "P2 Spike Waves"  → 触发送 "SPIKE WAVES"（半地刺）
    //   Idle 2 --TOOK DAMAGE--> Check 2 --NEXT--> Set Phase 3 --RAGE START--> Idle 3
    //       Check 2 的阈值 = "P3 A1 Rage"      → 触发送 "SPIKES OUTER"（已被 ArenaSpikes 关掉）+
    //                                            SetFsmBool Control "Rage Ready"（无限落刺）
    //   Idle 3 --TOOK DAMAGE--> Check 3 --NEXT--> Stun 1 --ARENA 2 START--> Idle 4
    //       Check 3 的阈值 = "P4 Stun1"        → 触发广播 "SPIKES NONE"(已关) + "STUN 1"（转 P2）
    //   Idle 4 --TOOK DAMAGE--> Check 4 --NEXT--> Set Ascend
    //       Check 4 的阈值 = "P5 Acend"        → SetFsmBool Control "Ascend Ready"（转 P3 攀爬）
    //
    // ⚠ Check 链是**逐级放行**的：没过 Check 1 就到不了 Check 2。所以 "P2 Spike Waves" 必须
    //   比 "P3 A1 Rage" 大，否则 HP 越过 3150 时 FSM 还卡在 Check 1 等 ≤P2 Spike Waves，
    //   无限落刺会被推迟到那个更低的阈值才触发（还会先误发一次 SPIKE WAVES）。原版 2600 < 3150
    //   就正好踩这个坑。本 mod 没有独立的半地刺相位（P1 地刺由 ArenaSpikes 常驻刷条控制，
    //   这里发的 SPIKE WAVES 只是让 Spike Control 回到同一个常驻状态，无可见变化），
    //   所以沿用天国余晖的 4350：只保证 Check 链先过 Check 1，不产生任何可见相位。
    //
    // P4（最终形态）：`Absolute Radiance/Control` 的 `Scream` 状态里那个 SetHP 把血量重置成
    //   1600（原版 1000）；同一 FSM 的 FsmInt "Death HP" 是最终形态的处决血线，设成 300（原版 720）。
    //
    // 只原位改字段值、不增删动作（见 [[feedback-fsm-index-stability]]）；改前的值记下来供关总开关写回。
    // 总血是唯一不还原的：它的「原值」在场景加载时读到的是预制体默认值，战斗中写回会把血补回去
    //   （天国余晖也不还原）。其余阈值/Scream/Death HP 的还原只动数字、不动当前血量，是安全的。
    internal static class BossHP
    {
        internal const int Total = 5000;

        // Check 链的 4 个阈值（剩余血量）
        internal const int HalfSpikeHP = 4350;   // "P2 Spike Waves"：结构性取值，必须 > RageHP，不产生可见相位
        internal const int RageHP = 3150;        // "P3 A1 Rage"：进入无限落刺
        internal const int Phase2HP = 2450;      // "P4 Stun1"：无限落刺结束，准备转 P2
        internal const int AscendHP = 1050;      // "P5 Acend"：P2 结束，转 P3

        // P4
        internal const int Phase4HP = 1600;      // Scream 里 SetHP 重置值
        internal const int DeathHP = 300;        // "Death HP" 处决血线

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

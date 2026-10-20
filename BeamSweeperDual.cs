using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P1 和 Z/N P2 双向光墙：本体与克隆从 x=61 同时向两侧移动。
    // 克隆反转 Idle 的转移目标；本体仅原位修改 FSM 字段，禁用 Force Left/Right 翻转，退出时复原。


    internal class BeamSweeperDual : MonoBehaviour
    {
        private const string VanillaName = "Beam Sweeper";
        private const string CloneSuffix = " (镜像)";

        // 两堵墙共同的起点 x（用户定，2026-10-01 由 64 改 61）
        private const float CenterX = 61f;

        // 四个会平移发射器的状态（起点 + 方向都在这几处）
        private static readonly string[] SweepStates =
            { "Beam Sweep L", "Beam Sweep R", "Beam Sweep L 2", "Beam Sweep R 2" };
        private static readonly string[] P1SweepStates = { "Beam Sweep L", "Beam Sweep R" };

        // Idle 的事件 → 目标状态；克隆体把目标镜像掉（值 = 原版目标的对侧）
        private static readonly Dictionary<string, string> MirrorIdle =
            new Dictionary<string, string>
            {
                { "BEAM SWEEP L", "Beam Sweep R" },
                { "BEAM SWEEP R", "Beam Sweep L" },
                { "BEAM SWEEP L2", "Beam Sweep R 2" },
                { "BEAM SWEEP R2", "Beam Sweep L 2" },
            };

        private PlayMakerFSM control;   // 本体 Control
        private GameObject clone;
        private bool built;

        // 本体及克隆的改前值，关总开关时写回（随场景销毁，不会跨场累积）
        private readonly Dictionary<FsmFloat, float> origPos = new Dictionary<FsmFloat, float>();
        private readonly Dictionary<FsmStateAction, float> origMoveTime = new Dictionary<FsmStateAction, float>();
        private readonly Dictionary<FsmFloat, bool> origMoveTimeUseVariable = new Dictionary<FsmFloat, bool>();
        private readonly Dictionary<FsmStateAction, bool> origBool = new Dictionary<FsmStateAction, bool>();

        internal void Init(PlayMakerFSM f) => control = f;

        private void Update()
        {
            if (gameObject.name != VanillaName) return;   // 克隆体随 Instantiate 带过来的这份不干活
            if (control == null || control.Fsm == null) return;

            if (!OrbRadiance.Settings.Enabled)
            {
                Teardown();
                return;
            }

            if (!built) Build();
        }

        private void Build()
        {
            built = true;

            try
            {
                clone = Instantiate(gameObject, transform.parent);
            }
            catch (Exception e)
            {
                clone = null;
                OrbRadiance.Instance.LogError("OrbRadiance: 克隆 Beam Sweeper 抛异常 " + e);
                return;
            }
            if (clone == null) return;

            clone.name = VanillaName + CloneSuffix;

            // 克隆体现在也带着一份本组件（Instantiate 会连组件一起复制），去掉它
            BeamSweeperDual dup = clone.GetComponent<BeamSweeperDual>();
            if (dup != null) Destroy(dup);

            PlayMakerFSM cf = FindControl(clone);
            if (cf == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 克隆的 Beam Sweeper 上没有 Control FSM，光墙不双发");
                Destroy(clone);
                clone = null;
                return;
            }

            float p1SpeedMultiplier = P1SpeedMultiplier();
            int a = PatchOriginal(control.Fsm, p1SpeedMultiplier);
            int b = PatchClone(cf.Fsm, clone, p1SpeedMultiplier);

            OrbRadiance.Instance.Log("[beam] 光墙双发就位：本体 " + a + " 处 / 克隆 " + b +
                                     " 处 sweep 状态改为 x=" + CenterX +
                                     "；克隆 Idle 转移已镜像（同一发广播 → 相反方向）");
        }

        // 本体：只原位改字段值（SetPosition.x、BoolTest.Enabled），不增删动作
        private int PatchOriginal(Fsm fsm, float p1SpeedMultiplier)
        {
            int n = 0;
            foreach (string name in SweepStates)
            {
                FsmState st = fsm.GetState(name);
                if (st == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Beam Sweeper Control 里没有 " + name + " 状态");
                    continue;
                }
                SetPosX(st);
                DisableBoolTest(st);
                if (P1SweepStates.Contains(name)) SetP1MoveDuration(st, p1SpeedMultiplier);
                n++;
            }
            return n;
        }

        // 克隆体：本 mod 自造、别的 mod 不读它，可以自由改
        private int PatchClone(Fsm fsm, GameObject cloneGo, float p1SpeedMultiplier)
        {
            // 克隆体的 `Self` 变量必须指回它自己：sweep 状态的生成点（spawnPoint = Self）靠它，
            // 不修的话克隆会照着本体的位置吐墙。Init 里 GetOwner 会写 Self，但保险起见直接写死，
            // 并把 GetOwner 的落点换成一次性对象，别让它把手写值又覆盖掉（照参照实现的精神）。
            FsmGameObject self = fsm.Variables.GetFsmGameObject("Self");
            if (self != null) self.Value = cloneGo;

            FsmState init = fsm.GetState("Init");
            GetOwner go = init?.Actions.OfType<GetOwner>().FirstOrDefault();
            if (go != null) go.storeGameObject = cloneGo;

            FsmState idle = fsm.GetState("Idle");
            if (idle == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 克隆 Beam Sweeper 没有 Idle 状态，镜像不了");
            }
            else if (idle.Transitions != null)
            {
                foreach (FsmTransition t in idle.Transitions)
                {
                    if (t.FsmEvent == null) continue;
                    string to;
                    if (!MirrorIdle.TryGetValue(t.FsmEvent.Name, out to)) continue;
                    t.ToState = to;
                    // ⚠ 运行期走的是 ToFsmState 引用（Fsm.DoTransition，Fsm.decompiled.cs:2329），
                    
                    t.ToFsmState = fsm.GetState(to);
                }
            }
            if (idle != null) DisableAudio(idle);

            int n = 0;
            foreach (string name in SweepStates)
            {
                FsmState st = fsm.GetState(name);
                if (st == null) continue;
                SetPosX(st);
                DisableBoolTest(st);
                if (P1SweepStates.Contains(name)) SetP1MoveDuration(st, p1SpeedMultiplier);
                DisableAudio(st);
                n++;
            }
            return n;
        }

        private void SetPosX(FsmState st)
        {
            SetPosition sp = st.Actions.OfType<SetPosition>().FirstOrDefault();
            if (sp == null || sp.x == null) return;
            if (!origPos.ContainsKey(sp.x)) origPos[sp.x] = sp.x.Value;
            sp.x.UseVariable = false;
            sp.x.Value = CenterX;
        }

        // P1 的持续时间始终从原版动作值推导，避免多次补丁累积加速；
        // Teardown 因而能准确恢复每个原版动作。
        private void SetP1MoveDuration(FsmState st, float multiplier)
        {
            if (st == null || multiplier <= 0f) return;
            iTweenMoveBy action = st.Actions.OfType<iTweenMoveBy>().FirstOrDefault();
            if (action == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Beam Sweeper " + st.Name + " has no iTweenMoveBy action; P1 speed unchanged");
                return;
            }

            FsmFloat time = action.time;
            if (time == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Beam Sweeper " + st.Name + " iTweenMoveBy has no time; P1 speed unchanged");
                return;
            }
            if (!origMoveTime.ContainsKey(action)) origMoveTime[action] = time.Value;
            if (!origMoveTimeUseVariable.ContainsKey(time)) origMoveTimeUseVariable[time] = time.UseVariable;
            time.UseVariable = false;
            time.Value = origMoveTime[action] / multiplier;
        }

        private static float P1SpeedMultiplier() => OrbRadiance.EffectiveHardMode ? 2f :
            (OrbRadiance.EffectiveEasyMode ? 1f : 1.5f);

        // 关掉 Force Left/Right 的翻转：方向只由「进了哪个状态」决定
        private void DisableBoolTest(FsmState st)
        {
            foreach (BoolTest bt in st.Actions.OfType<BoolTest>())
            {
                if (!origBool.ContainsKey(bt)) origBool[bt] = bt.Enabled;
                bt.Enabled = false;
            }
        }

        // 克隆体的声音不要（免得双份）：Idle / sweep 状态里的音频动作全关
        private static void DisableAudio(FsmState st)
        {
            foreach (FsmStateAction a in st.Actions)
                if (a is FadeAudio || a is AudioPlaySimple || a is AudioPlayInState)
                    a.Enabled = false;
        }

        private void Teardown()
        {
            if (!built) return;
            built = false;

            if (clone != null)
            {
                Destroy(clone);
                clone = null;
            }

            foreach (KeyValuePair<FsmFloat, float> kv in origPos)
                if (kv.Key != null) kv.Key.Value = kv.Value;
            foreach (KeyValuePair<FsmStateAction, float> kv in origMoveTime)
            {
                FsmFloat time = kv.Key is iTweenMoveBy move ? move.time : null;
                if (time != null) time.Value = kv.Value;
            }
            foreach (KeyValuePair<FsmFloat, bool> kv in origMoveTimeUseVariable)
                if (kv.Key != null) kv.Key.UseVariable = kv.Value;
            foreach (KeyValuePair<FsmStateAction, bool> kv in origBool)
                if (kv.Key != null) kv.Key.Enabled = kv.Value;
            origPos.Clear();
            origMoveTime.Clear();
            origMoveTimeUseVariable.Clear();
            origBool.Clear();

            OrbRadiance.Instance.Log("[beam] 光墙双发已还原（克隆销毁、本体起点/速度/BoolTest 写回）");
        }

        private static PlayMakerFSM FindControl(GameObject go)
        {
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
                if (f.Fsm != null && f.FsmName == "Control") return f;
            return null;
        }
    }
}

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
        private static BeamSweeperDual active;

        // 本体及克隆的改前值，关总开关时写回（随场景销毁，不会跨场累积）
        private readonly Dictionary<FsmFloat, float> origPos = new Dictionary<FsmFloat, float>();
        private readonly Dictionary<FsmStateAction, float> origMoveTime = new Dictionary<FsmStateAction, float>();
        private readonly Dictionary<FsmFloat, bool> origMoveTimeUseVariable = new Dictionary<FsmFloat, bool>();
        private readonly Dictionary<FsmStateAction, bool> origBool = new Dictionary<FsmStateAction, bool>();

        internal void Init(PlayMakerFSM f)
        {
            control = f;
            if (gameObject.name == VanillaName) active = this;
        }

        // P1(Z/N/H) 和 P2(Z/N) 的广播必须在两个发射器都处于 Idle 时发送。
        // 若克隆尚未就绪，不允许只发出单侧光墙，也不报告虚假的发射成功。
        internal static bool CanTriggerSweep(out string stateInfo)
        {
            BeamSweeperDual instance = active;
            if (!OrbRadiance.Settings.Enabled || instance == null || instance.control == null ||
                instance.control.Fsm == null)
            {
                stateInfo = "Beam Sweeper 本体尚未就绪";
                return false;
            }
            if (!instance.built || instance.clone == null) instance.Build();
            PlayMakerFSM cloneControl = instance.clone != null ? FindControl(instance.clone) : null;
            if (cloneControl == null || cloneControl.Fsm == null)
            {
                stateInfo = "镜像发射器尚未就绪";
                return false;
            }
            string nativeState = instance.control.Fsm.ActiveStateName;
            string mirrorState = cloneControl.Fsm.ActiveStateName;
            stateInfo = "native=" + nativeState + ", mirror=" + mirrorState;
            return nativeState == "Idle" && mirrorState == "Idle";
        }

        // 两种 P1/P2 事件均可通过同一发射器广播；克隆在 Idle 中把状态镜像反转。
        internal static bool TriggerSweep(string sweepEvent)
        {
            if (sweepEvent != "BEAM SWEEP L" && sweepEvent != "BEAM SWEEP R" &&
                sweepEvent != "BEAM SWEEP L2" && sweepEvent != "BEAM SWEEP R2") return false;
            if ((sweepEvent == "BEAM SWEEP L2" || sweepEvent == "BEAM SWEEP R2") &&
                OrbRadiance.EffectiveHardMode) return false;
            if (!CanTriggerSweep(out string stateInfo)) return false;
            BeamSweeperDual instance = active;
            PlayMakerFSM mirror = FindControl(instance.clone);
            instance.control.SendEvent(sweepEvent);
            mirror.SendEvent(sweepEvent);
            OrbRadiance.Instance.Log("[beam] SWEEP_DISPATCH t=" + Time.time.ToString("F3") +
                " event=" + sweepEvent + " before=" + stateInfo);
            return true;
        }

        // 保留旧接口，便于现有调用者继续使用。
        internal static bool CanTriggerP1Sweep(out string stateInfo) => CanTriggerSweep(out stateInfo);
        internal static bool TriggerP1Sweep(string sweepEvent)
        {
            if (sweepEvent != "BEAM SWEEP L" && sweepEvent != "BEAM SWEEP R") return false;
            return TriggerSweep(sweepEvent);
        }

        internal static void Restore()
        {
            if (active != null) active.Teardown();
        }

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
            if (clone != null) return;
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

            built = true;
            float p1SpeedMultiplier = P1SpeedMultiplier();
            int a = PatchOriginal(control.Fsm, p1SpeedMultiplier);
            int b = PatchClone(cf.Fsm, clone, p1SpeedMultiplier);

            OrbRadiance.Instance.Log("[beam] 光墙双发就位：本体 " + a + " 处 / 克隆 " + b +
                                     " 处 sweep 状态改为 x=" + CenterX +
                                     "；克隆 Idle 转移已镜像（同一发广播 → 相反方向）");

            DumpP1BeamVisualInfo();
        }

        // 临时诊断：打印原版 P1 光墙真正生成的预制体、渲染器和材质。
        // 只读取原版 FSM，不改预制体及战斗逻辑，确认后可删除本方法及调用。
        private void DumpP1BeamVisualInfo()
        {
            if (control == null || control.Fsm == null) return;
            try
            {
                foreach (string stateName in P1SweepStates)
                {
                    FsmState state = control.Fsm.GetState(stateName);
                    if (state == null)
                    {
                        OrbRadiance.Instance.LogWarn("[beam-prefab] 缺少状态 " + stateName);
                        continue;
                    }
                    int found = 0;
                    foreach (FsmStateAction action in state.Actions)
                    {
                        SpawnObjectFromGlobalPoolOverTime spawn = action as SpawnObjectFromGlobalPoolOverTime;
                        if (spawn == null) continue;
                        found++;
                        GameObject prefab = spawn.gameObject != null ? spawn.gameObject.Value : null;
                        if (prefab == null)
                        {
                            OrbRadiance.Instance.LogWarn("[beam-prefab] " + stateName + " 的生成动作为空预制体");
                            continue;
                        }
                        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                        tk2dBaseSprite[] sprites = prefab.GetComponentsInChildren<tk2dBaseSprite>(true);
                        OrbRadiance.Instance.Log("[beam-prefab] " + stateName + " => " + prefab.name +
                            " / renderers=" + renderers.Length + " / tk2d=" + sprites.Length);
                        foreach (Renderer renderer in renderers)
                        {
                            if (renderer == null) continue;
                            Material mat = renderer.sharedMaterial;
                            string shaderName = mat != null && mat.shader != null ? mat.shader.name : "(无)";
                            Texture tex = mat != null ? mat.mainTexture : null;
                            OrbRadiance.Instance.Log("[beam-prefab] renderer=" + renderer.GetType().Name +
                                " object=" + renderer.gameObject.name + " shader=" + shaderName +
                                " texture=" + (tex != null ? tex.name : "(无)"));
                        }
                        foreach (tk2dBaseSprite sprite in sprites)
                        {
                            if (sprite != null)
                                OrbRadiance.Instance.Log("[beam-prefab] tk2d=" + sprite.gameObject.name +
                                    " spriteId=" + sprite.spriteId);
                        }
                    }
                    if (found == 0)
                    {
                        string names = string.Join(", ", state.Actions
                            .Where(a => a != null && a.GetType().Name.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(a => a.GetType().Name).Distinct().ToArray());
                        OrbRadiance.Instance.LogWarn("[beam-prefab] " + stateName +
                            " 找不到 SpawnObjectFromGlobalPoolOverTime；其他 Spawn 动作：" + names);
                    }
                }
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogWarn("[beam-prefab] 读取原版光墙渲染信息失败：" + e);
            }
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

        private void OnDestroy()
        {
            Teardown();
            if (active == this) active = null;
        }

        private static PlayMakerFSM FindControl(GameObject go)
        {
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
                if (f.Fsm != null && f.FsmName == "Control") return f;
            return null;
        }
    }
}

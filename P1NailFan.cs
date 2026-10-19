using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // 把 **P1** 的脸刺（剑环链）改成 ANY1（AnyRAD 1.0）同款。
    //
    // 来源：`参考代码/ANY1不对称/_decomp/UltimatumRadiance/Abs.cs`（AnyRAD1.0，其剑环改动继承自基础 ANY1）。
    // 剑环链（`Absolute Radiance/Attack Commands`，P1/P2 共用同一条，见 P2NailFanRing 的头注释）：
    //   Nail Fan（Wait 0.4、Rotation=随机 0~360、Spawns=12）→ Dir → CW/CCW Spawn
    //     （生成 1 把、FloatAdd Rotation ∓30、Spawns--）→ CW/CCW Msg（Spawns>0 回 Repeat）
    //     → CW/CCW Repeat（Wait）→ 回 Spawn … 一圈铺满 360°
    //     → Fire → Double（BoolTest Repeated）→ Restart（下一圈）或 End
    // ANY1 相对原版只动 3 处，**不动**每圈把数（仍 12）和圈内步进（仍 ∓30）：
    //   · `CW/CCW Restart` 的 FloatAdd：∓15 → **∓10**（圈间扇心偏移）
    //   · `CW/CCW Repeat` 的 Wait：0.01 → **-0.5**（≤0 时 PlayMaker 当帧直接发 FINISHED、不占时间）
    //   · 圈数 2 → **3**：由本组件的计数器复刻 ANY1 的 `CWRepeats`（把 `Repeated` 翻回 false 一次）
    //   · ANY1 还 `RemoveAction(CW/CCW Restart, 1)`（那是个本就被禁用的 Wait）——本 mod 不删动作
    //     （见 [[feedback-fsm-index-stability]]），而它原样就是禁用的，不动即等价。
    // 效果：三圈起始角 θ / θ-10 / θ-20（CCW 为 +10/+20），叠加成整圈实际 **10° 一钉**（原版两圈 15° 一钉）。
    //
    // P1/P2 分工（与 P2NailFanRing 互斥，绝不同时翻 `Repeated`）：
    //   · P1：本组件套 ANY1 参数 + 数 3 圈。挂在 Absolute Radiance 上，进场景即 Arm。
    //   · P2：P2NailFanRing 进 `Arena 2 Start` 时套自己的扇形参数（它把 ANY1 值记成「原值」，
    //     出 P2 时原样还回来）；这段时间本组件靠 `P2NailFanRing.P2Patched` 让位、不驱动。
    // 关总开关：`OrbRadiance.RestorePatched` 先让 P2NailFanRing 还原（P2 中点关会写回 ANY1），
    // 再走本组件的 Restore 写回原版 —— 所以 Restore 必须排在 P2NailFanRing.Restore 之后。
    internal class P1NailFan : MonoBehaviour
    {
        private const string FanState = "Nail Fan";     // 新一轮起手（重置圈计数）

        // ANY1 参数
        private const int Rings = 3;                    // 总圈数（原版 2）
        private const float RestartOffset = 10f;        // CW Restart -本值 / CCW Restart +本值（原版 15）
        private const float RepeatWait = -0.5f;         // CW/CCW Repeat 的 Wait（原版 0.01；≤0 = 当帧放行）

        private PlayMakerFSM commands;                  // Absolute Radiance/Attack Commands：剑环链
        private PlayMakerFSM phaseControl;               // Absolute Radiance/Control：阶段切换与重打边沿
        private PlayMakerFSM arenaControl;               // 父 Boss Control：常驻 Battle Start / Refight 边沿

        private bool armed;                             // ANY1 参数已套用
        private bool orbitMode;                          // NORMAL/HARD P1 ring; EASY keeps legacy ANY1 behavior
        private bool orbitAttackPending;
        private bool orbitEndSent;
        private bool commandsSubscribed;
        private bool phaseSubscribed;
        private bool arenaSubscribed;
        private bool arenaTried;
        private bool awaitingFreshP1Phase;
        private string lastArenaState;
        private int orbitGeneration;
        private Coroutine orbitCompletion;
        private float orbitStartAt;
        private bool suspendedForLaterPhase;
        private readonly List<GameObject> orbitNails = new List<GameObject>();
        private readonly List<Action> undo = new List<Action>();

        private string lastFan;
        private int rings;

        private static P1NailFan active;

        private void Awake()
        {
            active = this;
        }

        private void OnEnable()
        {
            SubscribePhaseControl();
        }

        private void OnDisable()
        {
            if (phaseSubscribed && phaseControl != null && phaseControl.Fsm != null)
                phaseControl.Fsm.StateChanged -= OnPhaseStateChanged;
            phaseSubscribed = false;
            if (arenaSubscribed && arenaControl != null && arenaControl.Fsm != null)
                arenaControl.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
            Unpatch(true);
        }

        private void OnDestroy()
        {
            if (active == this) active = null;
            Unpatch(true);
        }

        // 总开关关掉时 OrbRadiance.RestorePatched 走这里把字段写回原值
        internal static void Restore()
        {
            if (active != null) active.Unpatch(true);
        }

        // P1 and P2 share Attack Commands. P2 must restore any P1-owned edit before it
        // captures values; otherwise its "original" snapshot would contain P1 overrides.
        internal static void SuspendForPhase2()
        {
            if (active != null)
            {
                active.suspendedForLaterPhase = true;
                active.Unpatch();
            }
        }

        private void Update()
        {
            SubscribePhaseControl();
            if (!OrbRadiance.Settings.Enabled)
            {
                Unpatch(true);
                return;
            }

            if (!CanRunP1())
            {
                if (armed) Unpatch();
                return;
            }

            // P2 期间让位：字段由 P2NailFanRing 接管，计数器也归它（两边都翻 Repeated 会互相打断）
            if (P2NailFanRing.P2Patched) return;

            if (!armed && !Arm()) return;
            if (orbitMode) return;
            DriveRings();
        }

        private bool CanRunP1()
        {
            SubscribePhaseControl();
            ApplyPhaseState(phaseControl != null && phaseControl.Fsm != null ? phaseControl.Fsm.ActiveStateName : null);
            return !suspendedForLaterPhase && !P2NailFanRing.P2Patched;
        }

        private void SubscribePhaseControl()
        {
            if (phaseControl == null || phaseControl.Fsm == null)
                phaseControl = GetComponents<PlayMakerFSM>()
                    .FirstOrDefault(f => f != null && f.Fsm != null && f.FsmName == "Control");
            if (!phaseSubscribed && phaseControl != null && phaseControl.Fsm != null)
            {
                phaseControl.Fsm.StateChanged += OnPhaseStateChanged;
                phaseSubscribed = true;
                ApplyPhaseState(phaseControl.Fsm.ActiveStateName);
            }
            PlayMakerFSM parent = ResolveArenaControl();
            if (!arenaSubscribed && parent != null && parent.Fsm != null)
            {
                arenaControl = parent;
                lastArenaState = arenaControl.Fsm.ActiveStateName;
                arenaControl.Fsm.StateChanged += OnArenaStateChanged;
                arenaSubscribed = true;
                if (IsArenaResetState(lastArenaState)) ResetForArenaRetry();
            }
        }

        private void OnPhaseStateChanged(FsmState state)
        {
            if (state == null) return;
            if (awaitingFreshP1Phase)
            {
                if (state.Name != "Intro Recover" && state.Name != "Arena 1 Start") return;
                awaitingFreshP1Phase = false;
                suspendedForLaterPhase = false;
                return;
            }
            bool wasSuspended = suspendedForLaterPhase;
            ApplyPhaseState(state.Name);
            if (!wasSuspended && suspendedForLaterPhase) Unpatch();
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null || state.Name == lastArenaState) return;
            lastArenaState = state.Name;
            if (IsArenaResetState(state.Name)) ResetForArenaRetry();
        }

        private void ResetForArenaRetry()
        {
            awaitingFreshP1Phase = true;
            suspendedForLaterPhase = true;
            Unpatch();
        }

        private static bool IsArenaResetState(string state) =>
            state == "Battle Start" || state == "Refight" || state == "Init" || state == "Setup";

        private PlayMakerFSM ResolveArenaControl()
        {
            if (arenaControl != null && arenaControl.Fsm != null) return arenaControl;
            if (!arenaTried)
            {
                arenaTried = true;
                Transform parent = transform.parent;
                if (parent != null)
                    foreach (PlayMakerFSM fsm in parent.GetComponents<PlayMakerFSM>())
                        if (fsm != null && fsm.Fsm != null && fsm.FsmName == "Control") { arenaControl = fsm; break; }
                if (arenaControl == null)
                {
                    GameObject root = GameObject.Find("Boss Control");
                    if (root != null)
                        foreach (PlayMakerFSM fsm in root.GetComponents<PlayMakerFSM>())
                            if (fsm != null && fsm.Fsm != null && fsm.FsmName == "Control") { arenaControl = fsm; break; }
                }
            }
            return arenaControl;
        }

        private void ApplyPhaseState(string state)
        {
            if (awaitingFreshP1Phase)
            {
                if (state == "Intro Recover" || state == "Arena 1 Start")
                {
                    awaitingFreshP1Phase = false;
                    suspendedForLaterPhase = false;
                }
                return;
            }
            if (state == "Battle Start" || state == "Refight" || state == "Init" || state == "Setup")
                suspendedForLaterPhase = false;
            else if (state == "Arena 2 Start" || state == "Ascend Tele" || state == "Scream")
                suspendedForLaterPhase = true;
        }

        // ---------------- 套用 / 还原 ----------------

        private bool Arm()
        {
            if (!EnsureCommands()) return false;

            orbitMode = !OrbRadiance.EffectiveEasyMode;
            if (orbitMode) return ArmOrbit();

            Fsm fsm = commands.Fsm;
            FsmState cwRestart = fsm.GetState("CW Restart");
            FsmState ccwRestart = fsm.GetState("CCW Restart");
            FsmState cwRepeat = fsm.GetState("CW Repeat");
            FsmState ccwRepeat = fsm.GetState("CCW Repeat");
            if (cwRestart == null || ccwRestart == null || cwRepeat == null || ccwRepeat == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 缺剑环状态，P1 脸刺未改成 ANY1");
                return false;
            }

            // 先全部取到再改，避免中途失败留下「已改但未记 armed」的半套状态
            SetFloatAdd(cwRestart, -RestartOffset);
            SetFloatAdd(ccwRestart, RestartOffset);
            SetWait(cwRepeat, RepeatWait);
            SetWait(ccwRepeat, RepeatWait);

            armed = true;
            rings = 0;
            lastFan = null;

            OrbRadiance.Instance.Log("[p1fan] P1 脸刺 → ANY1 同款：" + Rings + " 圈 ×12 把（圈内 30°）、" +
                                     "圈间偏移 ∓" + RestartOffset + "°（整圈 10° 一钉）、Repeat Wait " + RepeatWait + "s");
            return true;
        }

        private void Unpatch(bool releaseCurrentAttack = false)
        {
            bool shouldReleaseEnd = releaseCurrentAttack && ShouldReleaseCurrentAttackEnd();
            if (commandsSubscribed && commands != null && commands.Fsm != null)
                commands.Fsm.StateChanged -= OnCommandsStateChanged;
            commandsSubscribed = false;
            if (orbitMode)
            {
                orbitGeneration++;
                if (orbitCompletion != null) { StopCoroutine(orbitCompletion); orbitCompletion = null; }
                P1FaceOrbitColumns.Stop(this);
                StopOrbitNails();
            }
            for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
            undo.Clear();
            if (shouldReleaseEnd) SendAttackEnd();
            armed = false;
            orbitMode = false;
            orbitAttackPending = false;
            orbitEndSent = false;
            orbitStartAt = -1f;
            rings = 0;
            lastFan = null;
        }

        private bool ShouldReleaseCurrentAttackEnd()
        {
            if (!orbitMode || !orbitAttackPending || orbitEndSent || suspendedForLaterPhase || awaitingFreshP1Phase ||
                P2NailFanRing.P2Patched) return false;
            string localState = phaseControl != null && phaseControl.Fsm != null ? phaseControl.Fsm.ActiveStateName : null;
            if (localState == "Arena 2 Start" || localState == "Ascend Tele" || localState == "Scream") return false;
            string arenaState = arenaControl != null && arenaControl.Fsm != null ? arenaControl.Fsm.ActiveStateName : null;
            if (IsArenaResetState(arenaState)) return false;
            if (commands == null || commands.Fsm == null || commands.Fsm.ActiveStateName != "End") return false;
            PlayMakerFSM choices = FindFsm("Attack Choices");
            string choicesState = choices != null && choices.Fsm != null ? choices.Fsm.ActiveStateName : null;
            return choicesState == "Nail Fan";
        }

        private void OnCommandsStateChanged(FsmState state)
        {
            if (state == null || !orbitMode || P2NailFanRing.P2Patched) return;
            if (state.Name == FanState)
            {
                int ticket = ++orbitGeneration;
                orbitAttackPending = true;
                orbitEndSent = false;
                orbitStartAt = -1f;
            }
            else if (state.Name == "CW Double" || state.Name == "CCW Double")
            {
                orbitStartAt = -1f;
                P1FaceOrbitColumns.Begin(this, transform);
                orbitCompletion = StartCoroutine(CompleteOrbitAttack(orbitGeneration));
                FsmBool repeated = commands.Fsm.Variables.GetFsmBool("Repeated");
                if (repeated != null) repeated.Value = true; // one native 10-nail ring, then native End
            }
        }

        private IEnumerator CompleteOrbitAttack(int ticket)
        {
            while (P1FaceOrbitColumns.IsRunningFor(this))
            {
                if (ticket != orbitGeneration || !orbitMode || P2NailFanRing.P2Patched) yield break;
                if (P1FaceOrbitColumns.TryGetScheduleStart(this, out float startAt) && orbitStartAt < 0f)
                {
                    orbitStartAt = startAt;
                    foreach (GameObject nail in orbitNails)
                    {
                        P1NailOrbit orbit = nail != null ? nail.GetComponent<P1NailOrbit>() : null;
                        if (orbit != null) orbit.BeginAt(orbitStartAt);
                    }
                }
                yield return null;
            }
            if (ticket != orbitGeneration || !orbitMode || !orbitAttackPending || orbitEndSent || P2NailFanRing.P2Patched) yield break;
            if (orbitStartAt < 0f) StopOrbitNails();
            SendAttackEnd();
            orbitCompletion = null;
        }

        private void SendAttackEnd()
        {
            if (orbitEndSent) return;
            orbitEndSent = true;
            orbitAttackPending = false;
            PlayMakerFSM choices = FindFsm("Attack Choices");
            if (choices != null && choices.Fsm != null) choices.SendEvent("ATTACK END");
            else OrbRadiance.Instance.LogWarn("OrbRadiance: P1 orbit could not send final ATTACK END (Attack Choices missing)");
        }

        private bool ArmOrbit()
        {
            Fsm fsm = commands.Fsm;
            FsmState fan = fsm.GetState("Nail Fan");
            FsmState cwSpawn = fsm.GetState("CW Spawn");
            FsmState ccwSpawn = fsm.GetState("CCW Spawn");
            FsmState cwRestart = fsm.GetState("CW Restart");
            FsmState ccwRestart = fsm.GetState("CCW Restart");
            FsmState cwFire = fsm.GetState("CW Fire");
            FsmState ccwFire = fsm.GetState("CCW Fire");
            FsmState cwMsg = fsm.GetState("CW Msg");
            FsmState ccwMsg = fsm.GetState("CCW Msg");
            FsmState end = fsm.GetState("End");
            FsmState cwDouble = fsm.GetState("CW Double");
            FsmState ccwDouble = fsm.GetState("CCW Double");
            if (fan == null || cwSpawn == null || ccwSpawn == null || cwRestart == null ||
                ccwRestart == null || cwFire == null || ccwFire == null || end == null ||
                cwDouble == null || ccwDouble == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands lacks states for the P1 orbit ring");
                orbitMode = false;
                return false;
            }

            SetSpawns(fan, 10);
            SetSpawns(cwRestart, 10);
            SetSpawns(ccwRestart, 10);
            SetFloatAdd(cwSpawn, -36f);
            SetFloatAdd(ccwSpawn, 36f);
            DisableEvent(cwFire, "FAN ATTACK CW");
            DisableEvent(ccwFire, "FAN ATTACK CCW");
            DisableEvent(cwFire, "DOWN");
            DisableEvent(ccwFire, "DOWN");
            if (cwMsg != null) DisableEvent(cwMsg, "FAN ANTIC");
            if (ccwMsg != null) DisableEvent(ccwMsg, "FAN ANTIC");
            DisableEvent(end, "ATTACK END");

            armed = true;
            P1FaceOrbitColumns.Prepare(this, transform);
            rings = 0;
            lastFan = null;
            if (!commandsSubscribed)
            {
                commands.Fsm.StateChanged += OnCommandsStateChanged;
                commandsSubscribed = true;
            }
            OrbRadiance.Instance.Log("[p1fan] P1 N/H face nails orbit: one native randomized CW/CCW ring, 10 nails, radius 5, 720 deg/s for 2s; three locked-X beam columns finish at 2.15s");
            return true;
        }

        private void SetSpawns(FsmState state, int value)
        {
            SetIntValue action = state.Actions.OfType<SetIntValue>()
                .FirstOrDefault(item => item != null && item.intVariable != null && item.intVariable.Name == "Spawns");
            if (action == null) throw new InvalidOperationException("Missing Spawns in " + state.Name);
            int old = action.intValue.Value;
            undo.Add(() => action.intValue.Value = old);
            action.intValue.Value = value;
        }

        private void DisableEvent(FsmState state, string eventName)
        {
            foreach (SendEventByName action in state.Actions.OfType<SendEventByName>())
            {
                if (action == null || action.sendEvent == null || action.sendEvent.Value != eventName) continue;
                bool old = action.Enabled;
                undo.Add(() => action.Enabled = old);
                action.Enabled = false;
            }
        }

        private PlayMakerFSM FindFsm(string name)
        {
            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
                if (f != null && f.Fsm != null && f.FsmName == name) return f;
            return null;
        }

        private void StopOrbitNails()
        {
            foreach (GameObject nail in orbitNails)
                if (nail != null) UnityEngine.Object.Destroy(nail);
            orbitNails.Clear();
        }

        internal static bool TryAttachOrbit(PlayMakerFSM nailControl)
        {
            if (!OrbRadiance.Settings.Enabled) return false;
            if (active == null || !active.orbitMode || nailControl == null || nailControl.Fsm == null ||
                active.commands == null || active.commands.Fsm == null || P2NailFanRing.P2Patched ||
                OrbRadiance.EffectiveEasyMode || nailControl.gameObject == null ||
                nailControl.gameObject.name.IndexOf("Radiant Nail", StringComparison.OrdinalIgnoreCase) < 0 ||
                nailControl.gameObject.GetComponent<DamageHero>() == null) return false;
            string state = active.commands.Fsm.ActiveStateName;
            float direction = state == "CW Spawn" ? -1f : state == "CCW Spawn" ? 1f : 0f;
            if (direction == 0f) return false;
            if (nailControl.gameObject.GetComponent<P1NailOrbit>() != null) return true;
            Transform glow = active.transform.Find("Eye Beam Glow");
            if (glow == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: P1 orbit could not find Eye Beam Glow center");
                return false;
            }
            P1NailOrbit orbit = nailControl.gameObject.AddComponent<P1NailOrbit>();
            orbit.Init(nailControl, glow, direction, -1f);
            active.orbitNails.Add(nailControl.gameObject);
            if (nailControl.gameObject.GetComponent<NailProjectile>() == null)
                nailControl.gameObject.AddComponent<NailProjectile>();
            return true;
        }

        // ---------------- 圈数计数 ----------------

        // 复刻 ANY1 的 CWRepeats：`Repeated` 被 CW/CCW Double 置真后翻回假并递增，
        // 翻 (Rings-2) 次后最后一波只递增、不再翻 → 第 Rings 圈放行 END。公式与 P2NailFanRing.DriveWaves 同。
        private void DriveRings()
        {
            if (!EnsureCommands()) return;

            Fsm fsm = commands.Fsm;
            string cur = fsm.ActiveStateName;
            if (cur == FanState && lastFan != FanState) rings = 0;   // 新一轮重置计数
            lastFan = cur;

            FsmBool repeated = fsm.Variables.GetFsmBool("Repeated");
            if (repeated == null) return;

            if (repeated.Value)
            {
                if (rings < Rings - 2) { rings++; repeated.Value = false; }
                else if (rings == Rings - 2) { rings++; }
            }
            else if (rings == Rings - 1)
            {
                rings = 0;
            }
        }

        // ---------------- 字段改写（原位，不增删动作）----------------

        private void SetFloatAdd(FsmState state, float add)
        {
            FloatAdd f = state.Actions.OfType<FloatAdd>().FirstOrDefault();
            if (f == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + state.Name + " 里找不到 FloatAdd，未改");
                return;
            }
            float old = f.add.Value;
            undo.Add(() => f.add.Value = old);
            f.add.Value = add;
        }

        private void SetWait(FsmState state, float time)
        {
            Wait w = state.Actions.OfType<Wait>().FirstOrDefault();
            if (w == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + state.Name + " 里找不到 Wait，未改");
                return;
            }
            float old = w.time.Value;
            undo.Add(() => w.time.Value = old);
            w.time.Value = time;
        }

        // ---------------- 找 FSM ----------------

        private bool EnsureCommands()
        {
            if (commands != null && commands.Fsm != null) return true;

            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Attack Commands") continue;
                commands = f;
                break;
            }

            if (commands == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands FSM，P1 脸刺改不成 ANY1");
            return commands != null;
        }
    }
}

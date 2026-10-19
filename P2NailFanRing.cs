using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P2 脸刺按模式分流：NORMAL 使用 3 轮×6 枚整圆齐发并逐枚配对激光；HARD 8枚/扇，EASY 4枚/扇。
    //
    // 第一版做成「5 圈×24 剑环」= 每圈铺满 360° 的**整圆**，用户实测判「仍是整圈、不是扇形」→ 改成本设计。
    //
    // 做法照搬天国余晖（RadianceBoss.cs 的 HookAttackCommands）：只改 Attack Commands 里
    // 剑环链那几个状态的**字段值**，不增删动作（见 [[feedback-fsm-index-stability]]）。
    //
    // 剑环链（`Absolute Radiance/Attack Commands`，P1/P2 共用同一条）：
    //   NAIL FAN(全局) → `Nail Fan`(Wait 0.4、float Rotation=随机 0~360、int Spawns=12、bool Repeated=false)
    //     → `Eb Extra Wait 2`(Wait 0.25；那个 Arena==1 判断不影响流程 —— 动作跑完
    //        `FsmState.CheckAllActionsFinished` 会自动发 FINISHED，见 FsmState.decompiled.cs:609-620)
    //     → `Dir`(随机 CW/CCW，**每招只选一次 → 方向固定，圈与圈同向递进 = 扫射方向**) → `CW/CCW Spawn`
    //        (生成 1 把、FloatAdd Rotation ∓RingStep、Spawns--)
    //     → `CW/CCW Msg`(Spawns>0 回 Repeat，否则 Fire) → `CW/CCW Fire`(广播 FAN ATTACK，delay 0.3)
    //     → `CW/CCW Double`(BoolTest Repeated) → `Restart`(下一圈) 或 `End`(发 ATTACK END)
    // 所以 P1/P2 都走这条链 —— 要「P1 原版、P2 才扇形扫射」只能按阶段切字段值：
    //   进 P2（Control 的 `Arena 2 Start`）→ 套用扇形参数；出 P2（`Ascend Tele`）/ 重打 / 关总开关 → 还原。
    //
    // 扇形怎么来（一圈 = 一个扇面，扇心逐圈同向递进）：
    //   一圈内 Spawns 把剑只铺 **SectorWidth** 一个扇形：扇内步进 RingStep = SectorWidth / RingNails。
    //   一圈走完 Rotation 已推进 N·RingStep = SectorWidth，再加 Restart 的圈间偏移 RingOffset
    //   → 下一圈扇心 = 上一圈末端 + RingOffset。取 RingOffset=0 → 相邻扇首尾相接，FanWaves 个扇面
    //   正好首尾拼满 360°，绕一圈扫回来。
    //   **单旋钮**：FanWaves 越大 → 扇越窄、扫得越细越连续（SectorWidth = 360 / FanWaves）。
    //
    // H/Z 扇形圈数靠组件自己翻 `Repeated`（复刻天国余晖 NailFanWaveCounter），**只在 P2 生效**：
    // `CW/CCW Double` 的 BoolTest 见 Repeated=false 就再去下一圈；翻 (FanWaves-2) 次后
    // 最后一波不再翻 → 第 FanWaves 圈放行 END。P1 不翻 → 保持原版 2 圈。
    internal class P2NailFanRing : MonoBehaviour
    {
        // ---------------- 时间点（状态名，与 P2NailStream / P2GroundSpikes 同一套）----------------

        private const string StateStart = "Arena 2 Start";   // 进 P2
        private const string StateStop = "Ascend Tele";      // 出 P2（P3 第一步）
        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };
        private const string ArenaFsmOwner = "Boss Control";
        private const string NormalSpawnPointName = "Eye Beam Glow";
        private const int NormalWaves = 3;
        private const int NormalNailsPerWave = 6;
        private const int EasyVolleyWaves = 1;
        private const int EasyNailsPerWave = 4;
        private const float NormalPrepareTime = 0.3f;
        private const float NormalWaveInterval = 0.01f;
        private const float NormalReadyTimeout = 5f;

        // ---------------- 剑环参数（照搬天国余晖）----------------

        private const int FanWaves = 5;              // 圈数（= 扇面数）
        private const float SectorWidth = 360f / FanWaves;  // 扇面张角（度）= 360/圈数 → FanWaves 个扇面首尾拼满一圈
        private const int HardRingNails = 8;          // HARD 保留每扇 8 枚
        private const int EasyRingNails = 4;          // EASY 每扇 4 枚，扇宽仍为 72°
        private const float RingOffset = 0f;         // 圈间扇心递进（CW/CCW Restart 的 FloatAdd 大小；0 = 相邻扇相接）
        private const float FanWindup = 0.01f;       // Nail Fan 的 Wait（原版 0.4）
        private const float InterRingDelay = 0.01f;  // CW/CCW Restart 的 Wait（原版 0.25 且禁用）
        // CW/CCW Repeat 的 Wait：原版 0.01 / 0.02。压到 0.001 与天国余晖清空动作等效 ——
        // 每把剑本身由 `CW Spawn` 的 NextFrameEvent 卡在 1 帧，Repeat 的 Wait 只要 ≤1 帧就不是瓶颈。
        private const float RepeatWait = 0.001f;

        // ---------------- 运行时 ----------------

        private PlayMakerFSM control;               // Absolute Radiance/Control：阶段
        private PlayMakerFSM commands;              // Absolute Radiance/Attack Commands：剑环链

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;
        private string lastState;

        // 改前值 + 写回动作；进 P2 记录、出 P2 倒序还原。Count > 0 即「当前在 P2」。
        private readonly List<Action> undo = new List<Action>();

        private int waves;
        private string lastFan;

        private bool customVolleyMode;
        private int customWaves;
        private int customNailsPerWave;
        private float customRingOffset;
        private bool normalPatternRunning;
        private GameObject normalNailPrefab;
        private GameObject normalNailStaging;
        private readonly List<GameObject> normalNails = new List<GameObject>();
        private readonly List<PlayMakerFSM> normalNailFsms = new List<PlayMakerFSM>();
        private NFaceVolleySchedule normalSchedule;
        private bool normalClockwise;
        private bool normalDirectionKnown;
        private float normalTheta;
        private const float NormalRingOffset = 15f;
        private bool normalWarnedBeam;
        private bool normalBeamsAvailable;
        private bool normalHadWave;
        private float normalLastWaveAt;
        private readonly List<float> normalActualWaveIntervals = new List<float>();
        private float normalAttackStartAt;
        private float normalReadyAt;
        private bool normalHasReadyAt;
        private bool commandsSubscribed;

        private static P2NailFanRing active;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
            active = this;
        }

        private void OnDestroy()
        {
            if (undo.Count > 0) Unpatch();
            if (active == this) active = null;
        }

        private void OnDisable()
        {
            if (undo.Count > 0) Unpatch();
        }

        // 「当前正在按 P2 参数跑剑环」：P1NailFan 靠它判断该不该让位（P2 期间只有本组件的计数器在翻 Repeated）
        internal static bool P2Patched => active != null && active.undo.Count > 0;

        // 总开关关掉时 OrbRadiance.RestorePatched 走这里把字段写回原值
        internal static void Restore()
        {
            if (active != null) active.Unpatch();
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // 总开关关掉：还原（原版脸刺立刻回来）
            if (!OrbRadiance.Settings.Enabled)
            {
                Unpatch();
                lastState = null;
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同场景重来，Control 不会自己回 Init
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) Unpatch();
            }

            string cur = control.Fsm.ActiveStateName;
            if (cur != lastState)
            {
                lastState = cur;
                if (cur == StateStart) Patch();
                else if (cur == StateStop) Unpatch();
            }

            // 5 圈：只在 P2 期间翻 Repeated
            if (customVolleyMode && undo.Count > 0)
            {
                TickNormalPattern();
                return;
            }
            if (undo.Count > 0) DriveWaves();
        }

        // ---------------- 套用 / 还原 ----------------

        private void Patch()
        {
            if (undo.Count > 0) return;
            P1NailFan.SuspendForPhase2();
            if (!EnsureCommands()) return;

            Fsm fsm = commands.Fsm;
            FsmState fan = fsm.GetState("Nail Fan");
            FsmState cwSpawn = fsm.GetState("CW Spawn");
            FsmState ccwSpawn = fsm.GetState("CCW Spawn");
            FsmState cwRestart = fsm.GetState("CW Restart");
            FsmState ccwRestart = fsm.GetState("CCW Restart");
            FsmState cwRepeat = fsm.GetState("CW Repeat");
            FsmState ccwRepeat = fsm.GetState("CCW Repeat");
            if (fan == null || cwSpawn == null || ccwSpawn == null || cwRestart == null ||
                ccwRestart == null || cwRepeat == null || ccwRepeat == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 缺剑环状态，P2 脸刺未改成扇形扫射");
                return;
            }

            if (IsNZVolleyMode())
            {
                PatchNormal(fsm, fan, cwSpawn, ccwSpawn);
                return;
            }

            int ringNails = OrbRadiance.EffectiveEasyMode ? EasyRingNails : HardRingNails;
            float ringStep = SectorWidth / ringNails;
            SetSpawns(fan, ringNails);
            SetWait(fan, FanWindup);
            SetFloatAdd(cwSpawn, -ringStep);
            SetFloatAdd(ccwSpawn, ringStep);
            SetSpawns(cwRestart, ringNails);
            SetSpawns(ccwRestart, ringNails);
            SetFloatAdd(cwRestart, -RingOffset);
            SetFloatAdd(ccwRestart, RingOffset);
            SetWaitEnabled(cwRestart, InterRingDelay);
            SetWaitEnabled(ccwRestart, InterRingDelay);
            SetWait(cwRepeat, RepeatWait);
            SetWait(ccwRepeat, RepeatWait);

            waves = 0;
            lastFan = null;

            OrbRadiance.Instance.Log("[fanring] P2 脸刺 → 扇形扫射：" + FanWaves + " 个 " + SectorWidth +
                                     "° 扇面（每扇 " + ringNails + " 把、扇内步进 " + ringStep +
                                     "°、扇心递进 " + RingOffset + "°）→ 首尾拼满 360°；圈间 " +
                                     InterRingDelay + "s、出手前摇 " + FanWindup + "s（P1 脸刺不受影响）");
        }

        private void Unpatch()
        {
            if (customVolleyMode) AbortNormalPattern(true);
            if (commandsSubscribed && commands != null && commands.Fsm != null)
            {
                commands.Fsm.StateChanged -= OnCommandsStateChanged;
                commandsSubscribed = false;
            }
            for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
            undo.Clear();
            customVolleyMode = false;
            customWaves = 0;
            customNailsPerWave = 0;
            customRingOffset = 0f;
            waves = 0;
            lastFan = null;
        }

        private static bool IsNZVolleyMode() => NFaceVolleySchedule.IsNZVolleyMode(
            OrbRadiance.EffectiveHardMode);

        private void PatchNormal(Fsm fsm, FsmState fan, FsmState cwSpawn, FsmState ccwSpawn)
        {
            // Resolve while the native CW Spawn action still contains the original global prefab reference.
            normalNailPrefab = P2NailStream.ResolveNailPrefab(gameObject);
            if (normalNailPrefab == null)
            {
                OrbRadiance.Instance.LogWarn("[nface] 原生 Radiant Nail prefab 解析失败，NORMAL 脸刺保持原版");
                return;
            }

            bool easy = OrbRadiance.EffectiveEasyMode;
            customWaves = easy ? EasyVolleyWaves : NormalWaves;
            customNailsPerWave = easy ? EasyNailsPerWave : NormalNailsPerWave;
            customRingOffset = easy ? 0f : NormalRingOffset;
            SetSpawns(fan, customNailsPerWave);
            if (easy)
            {
                SetWait(fan, 0f);
                FsmState extraWait = fsm.GetState("Eb Extra Wait 2");
                if (extraWait == null)
                    throw new InvalidOperationException("Z face volley requires Eb Extra Wait 2");
                SetWait(extraWait, 0f);
            }
            ReplaceActions(cwSpawn, new FsmStateAction[] { new NFaceVolleyHoldAction() });
            ReplaceActions(ccwSpawn, new FsmStateAction[] { new NFaceVolleyHoldAction() });

            customVolleyMode = true;
            normalPatternRunning = false;
            normalSchedule = null;
            normalHadWave = false;
            normalActualWaveIntervals.Clear();
            normalAttackStartAt = 0f;
            normalHasReadyAt = false;
            if (!commandsSubscribed)
            {
                commands.Fsm.StateChanged += OnCommandsStateChanged;
                commandsSubscribed = true;
            }
            if (commands.Fsm.ActiveStateName == "Nail Fan")
                OnCommandsStateChanged(fsm.GetState("Nail Fan"));
            OrbRadiance.Instance.Log(easy
                ? "[zface] EASY P2 接管发射链：1轮×4枚整圆钉刺+4束同步激光，90°间隔"
                : "[nface] NORMAL P2 接管 CW/CCW 发射链：3轮×6枚；整圆60°，原生圈偏移±15°；轮距按实际派发+0.01s");
        }

        private void ReplaceActions(FsmState state, FsmStateAction[] replacement)
        {
            FsmStateAction[] old = state.Actions;
            undo.Add(() => state.Actions = old);
            state.Actions = replacement;
            foreach (FsmStateAction action in replacement) action.Init(state);
        }

        internal void BeginNormalVolley(bool clockwise)
        {
            if (!customVolleyMode || undo.Count == 0)
            {
                if (commands != null && commands.Fsm != null &&
                    (commands.Fsm.ActiveStateName == "CW Spawn" || commands.Fsm.ActiveStateName == "CCW Spawn"))
                    commands.Fsm.SetState("End");
                return;
            }
            if (normalPatternRunning && customNailsPerWave == EasyNailsPerWave)
            {
                SetNormalDirection(clockwise);
                return;
            }
            if (normalPatternRunning) return;
            if (normalAttackStartAt <= 0f) normalAttackStartAt = Time.time;
            PrepareNormalVolley(true, clockwise);
        }

        private void PrepareNormalVolley(bool directionKnown, bool clockwise)
        {
            normalPatternRunning = true;
            normalClockwise = clockwise;
            normalDirectionKnown = directionKnown;
            normalTheta = Rotation();
            normalNails.Clear();
            normalNailFsms.Clear();
            normalWarnedBeam = false;
            normalHadWave = false;
            normalActualWaveIntervals.Clear();

            try
            {
                Transform nativeSpawnPoint = transform.Find(NormalSpawnPointName);
                if (nativeSpawnPoint == null)
                    throw new InvalidOperationException("native CW Spawn point Eye Beam Glow missing");
                normalNailStaging = new GameObject("NORMAL P2 face nail staging");
                normalNailStaging.transform.SetParent(transform, false);
                normalNailStaging.SetActive(false);
                for (int wave = 0; wave < customWaves; wave++)
                {
                    for (int slot = 0; slot < customNailsPerWave; slot++)
                    {
                        int index = wave * customNailsPerWave + slot;
                        float angle = NFaceVolleySchedule.ProjectileAngle(normalTheta, clockwise, wave, slot,
                            customNailsPerWave, customRingOffset);
                        GameObject nail = Instantiate(normalNailPrefab, nativeSpawnPoint.position,
                            Quaternion.Euler(0f, 0f, angle), normalNailStaging.transform);
                        if (nail == null) throw new InvalidOperationException("Radiant Nail instantiate returned null");
                        nail.name = "NZ P2 face nail " + (index + 1).ToString("00");
                        nail.transform.eulerAngles = new Vector3(0f, 0f, angle);
                        if (nail.GetComponent<NailProjectile>() == null) nail.AddComponent<NailProjectile>();
                        nail.SetActive(true);
                        PlayMakerFSM fsm = FindNailFsm(nail);
                        if (fsm == null) throw new InvalidOperationException("native Fan Ready FSM missing for nail " + index);
                        normalNails.Add(nail);
                        normalNailFsms.Add(fsm);
                    }
                }

                normalNailStaging.SetActive(true);
                FanEyeBeam eye = GetComponent<FanEyeBeam>();
                bool beamsReady = eye != null && eye.PrepareNormalBeams(customWaves * customNailsPerWave);
                normalBeamsAvailable = beamsReady;
                if (!beamsReady && !normalWarnedBeam)
                {
                    normalWarnedBeam = true;
                    throw new InvalidOperationException("could not prepare all private native face beams");
                }

                normalSchedule = new NFaceVolleySchedule(customWaves, customNailsPerWave,
                    NormalPrepareTime, NormalWaveInterval, NormalReadyTimeout,
                    customNailsPerWave == EasyNailsPerWave);
                float scheduleStart = customNailsPerWave == EasyNailsPerWave
                    ? normalAttackStartAt : Time.time;
                normalSchedule.Start(scheduleStart, AnticipateNormalNail, FireNormalNail, CompleteNormalAttack);
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("[nface] NORMAL P2 脸刺准备失败，结束本次原生攻击：" + e);
                AbortNormalPattern(true);
            }
        }

        private void OnCommandsStateChanged(FsmState state)
        {
            if (state == null || !customVolleyMode || undo.Count == 0) return;
            if (state.Name != "Nail Fan") return;
            normalAttackStartAt = Time.time;
            normalHasReadyAt = false;
            if (customNailsPerWave == EasyNailsPerWave && !normalPatternRunning)
                PrepareNormalVolley(false, false);
        }

        private void SetNormalDirection(bool clockwise)
        {
            normalClockwise = clockwise;
            normalDirectionKnown = true;
            normalTheta = Rotation();
            for (int i = 0; i < normalNails.Count; i++)
            {
                float angle = NFaceVolleySchedule.ProjectileAngle(normalTheta, clockwise, 0,
                    i % customNailsPerWave, customNailsPerWave, customRingOffset);
                normalNails[i].transform.eulerAngles = new Vector3(0f, 0f, angle);
            }
        }

        private static PlayMakerFSM FindNailFsm(GameObject nail)
        {
            foreach (PlayMakerFSM fsm in nail.GetComponents<PlayMakerFSM>())
                if (fsm != null && fsm.Fsm != null && fsm.Fsm.GetState("Fan Ready") != null &&
                    fsm.Fsm.GetState("Fan Out") != null) return fsm;
            return null;
        }

        private void TickNormalPattern()
        {
            if (!normalPatternRunning || normalSchedule == null) return;
            bool[] started = new bool[normalNailFsms.Count];
            bool[] ready = new bool[normalNailFsms.Count];
            FanEyeBeam eye = GetComponent<FanEyeBeam>();
            for (int i = 0; i < normalNailFsms.Count; i++)
            {
                PlayMakerFSM nailFsm = normalNailFsms[i];
                bool nailStarted = nailFsm != null && nailFsm.Fsm != null && nailFsm.Fsm.Started;
                bool beamInert = !normalBeamsAvailable || eye == null || eye.NormalBeamReady(i);
                started[i] = normalDirectionKnown && NFaceVolleySchedule.StartupGate(nailStarted, beamInert,
                    normalSchedule.IsWaitingForStart);
                string state = started[i] ? nailFsm.Fsm.ActiveStateName : null;
                ready[i] = started[i] && (state == "Fan Out" || state == "Fan Ready");
            }
            if (normalDirectionKnown && !normalHasReadyAt && ready.All(value => value))
            {
                normalReadyAt = Time.time;
                normalHasReadyAt = true;
            }
            NFaceVolleySchedule schedule = normalSchedule;
            schedule.Tick(Time.time, started, ready);
            if (schedule.IsFailed)
            {
                OrbRadiance.Instance.LogWarn("[nface] 本轮失败（" + schedule.FailureReason + "）；取消延迟发射并正常结束攻击");
                AbortNormalPattern(true);
            }
        }

        private void CompleteNormalAttack()
        {
            normalPatternRunning = false;
            normalSchedule = null;
            if (normalNailStaging != null) Destroy(normalNailStaging);
            normalNailStaging = null;
            normalNails.Clear();
            normalNailFsms.Clear();
            FanEyeBeam eye = GetComponent<FanEyeBeam>();
            if (eye != null) eye.CompleteNormalPattern();
            commands.Fsm.SetState("End");
            float elapsed = Math.Max(0f, Time.time - normalAttackStartAt);
            float readyDelay = normalHasReadyAt ? Math.Max(0f, normalReadyAt - normalAttackStartAt) : -1f;
            string intervals = normalActualWaveIntervals.Count == 0 ? "无" :
                string.Join(",", normalActualWaveIntervals.Select(x => x.ToString("0.000")));
            OrbRadiance.Instance.Log("[nface] " + customWaves + "轮 " + (customWaves * customNailsPerWave) +
                                     "枚全部派发；每轮" + customNailsPerWave + "枚同帧，实测轮距=" + intervals +
                                     "s（目标0.010s，Unity帧间隔量化）；elapsed=" + elapsed.ToString("0.000") +
                                     "s readyDelay=" + (readyDelay < 0f ? "timeout" : readyDelay.ToString("0.000") + "s") +
                                     "；原生 End 路径继续并仅发一次 ATTACK END");
        }

        private void AnticipateNormalNail(int index)
        {
            GameObject nail = normalNails[index];
            PlayMakerFSM fsm = normalNailFsms[index];
            fsm.SendEvent("FAN ANTIC");
            FanEyeBeam eye = GetComponent<FanEyeBeam>();
            if (eye != null)
            {
                float anticipatedAngle = NFaceVolleySchedule.ProjectileAngle(normalTheta, normalClockwise,
                    index / customNailsPerWave, index % customNailsPerWave, customNailsPerWave, customRingOffset) + 90f;
                eye.AnticipateNormalBeam(index, anticipatedAngle);
            }
        }

        private void FireNormalNail(int index)
        {
            GameObject nail = normalNails[index];
            if (nail == null) throw new InvalidOperationException("prepared nail disappeared before fire " + index);
            if (index % customNailsPerWave == 0)
            {
                float now = Time.time;
                if (normalHadWave) normalActualWaveIntervals.Add(now - normalLastWaveAt);
                normalLastWaveAt = now;
                normalHadWave = true;
            }
            nail.transform.SetParent(null, true);
            normalNailFsms[index].SendEvent(NFaceVolleySchedule.NativeAttackEvent(normalClockwise));
            string expectedState = normalClockwise ? "Fire CW" : "Fire CCW";
            if (normalNailFsms[index].Fsm.ActiveStateName != expectedState)
                throw new InvalidOperationException("native FAN ATTACK did not enter " + expectedState +
                                                    " (actual " + normalNailFsms[index].Fsm.ActiveStateName + ")");
            Rigidbody2D body = nail.GetComponent<Rigidbody2D>();
            if (body == null) body = nail.GetComponentInChildren<Rigidbody2D>();
            Vector2 velocity = body != null ? body.velocity : Vector2.zero;
            if (velocity.sqrMagnitude <= 0.0001f)
            {
                FanEyeBeam eyeMissing = GetComponent<FanEyeBeam>();
                if (eyeMissing != null && !normalWarnedBeam)
                {
                    normalWarnedBeam = true;
                    OrbRadiance.Instance.LogWarn("[nface] Fire 后未读到原生刚体速度；该枚激光跳过以免错向");
                }
                throw new InvalidOperationException("native Fire state has no readable rigidbody velocity");
            }
            float angle = NFaceVolleySchedule.AngleFromVelocity(velocity.x, velocity.y);
            FanEyeBeam eye = GetComponent<FanEyeBeam>();
            if (eye != null) eye.FireNormalBeam(index, angle);
        }

        private void AbortNormalPattern(bool finishAttack)
        {
            if (normalSchedule != null) normalSchedule.Abort();
            normalSchedule = null;
            normalPatternRunning = false;
            normalBeamsAvailable = false;
            FanEyeBeam eye = GetComponent<FanEyeBeam>();
            if (eye != null) eye.CancelNormalPattern();
            foreach (GameObject nail in normalNails) if (nail != null) Destroy(nail);
            if (normalNailStaging != null) Destroy(normalNailStaging);
            normalNailStaging = null;
            normalNails.Clear();
            normalNailFsms.Clear();

            if (!finishAttack || commands == null || commands.Fsm == null) return;
            string state = commands.Fsm.ActiveStateName;
            if (state == "CW Spawn" || state == "CCW Spawn" || state == "Nail Fan" ||
                state == "Eb Extra Wait 2" || state == "Dir")
            {
                // Move out of the temporary held state while the original End path is still installed.
                commands.Fsm.SetState("End");
            }
        }

        private float Rotation()
        {
            FsmFloat rotation = commands != null && commands.Fsm != null
                ? commands.Fsm.Variables.GetFsmFloat("Rotation") : null;
            return rotation != null ? rotation.Value : 0f;
        }

        // ---------------- 5 圈计数 ----------------

        // 复刻天国余晖 NailFanWaveCounter：`Repeated` 被 CW/CCW Double 置真后翻回假并递增，
        // 翻满 FanWaves-2 次后，最后一波只递增、**不翻**（保持真 → 下一圈 Double 见真 → End）。
        //
        // ⚠ 最后一波绝不能再翻回假：那样会多放一圈，且下面 `waves == FanWaves-1` 那支随即把计数清零
        //   → 剑环无限循环、ATTACK END 永不发出、Attack Choices 卡在 `Nail Fan`（boss 定住）。
        //   2026-10-02 实测踩到（原条件写成 `waves < FanWaves-1`）。
        private void DriveWaves()
        {
            if (!EnsureCommands()) return;

            Fsm fsm = commands.Fsm;
            string cur = fsm.ActiveStateName;
            if (cur == "Nail Fan" && lastFan != "Nail Fan") waves = 0;   // 新一轮攻击重置计数
            lastFan = cur;

            FsmBool repeated = fsm.Variables.GetFsmBool("Repeated");
            if (repeated == null) return;

            if (repeated.Value)
            {
                if (waves < FanWaves - 2) { waves++; repeated.Value = false; }
                else if (waves == FanWaves - 2) { waves++; }
            }
            else if (waves == FanWaves - 1)
            {
                waves = 0;
            }
        }

        // ---------------- 字段改写（原位，不增删动作）----------------

        private void SetSpawns(FsmState state, int value)
        {
            SetIntValue a = state.Actions.OfType<SetIntValue>()
                .FirstOrDefault(x => x.intVariable != null && x.intVariable.Name == "Spawns");
            if (a == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + state.Name + " 里找不到 Spawns，未改");
                return;
            }
            int old = a.intValue.Value;
            undo.Add(() => a.intValue.Value = old);
            a.intValue.Value = value;
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

        // CW/CCW Restart 的 Wait 原版是禁用的（0.25），这里连 Enabled 一起改，还原时也要写回
        private void SetWaitEnabled(FsmState state, float time)
        {
            Wait w = state.Actions.OfType<Wait>().FirstOrDefault();
            if (w == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + state.Name + " 里找不到 Wait，未改");
                return;
            }
            float oldT = w.time.Value;
            bool oldE = w.Enabled;
            undo.Add(() => { w.time.Value = oldT; w.Enabled = oldE; });
            w.time.Value = time;
            w.Enabled = true;
        }

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
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands FSM，P2 脸刺改不成剑环");
            return commands != null;
        }

        // ---------------- 重打复位 ----------------

        private string ArenaState()
        {
            if (!arenaTried)
            {
                arenaTried = true;
                Transform parent = transform.parent;
                if (parent != null)
                    foreach (PlayMakerFSM f in parent.GetComponents<PlayMakerFSM>())
                        if (f != null && f.Fsm != null && f.FsmName == "Control") { arenaFsm = f; break; }

                if (arenaFsm == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + ArenaFsmOwner +
                                                 " 的 Control FSM，重打时 P2 剑环参数不会自动还原");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

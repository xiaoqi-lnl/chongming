using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // Z/N P3 的 19 个攀爬平台各生成 9 根独立原版地刺，使用各自的 Hero Saver 控制整排收回和恢复。
    // Z 的 hazardType=1，N 的 hazardType=0；根据平台实际显示状态控制升刺。
    
    
    internal sealed class P3PlatformSpikes : MonoBehaviour
    {
        private const string StartState = "Ascend Tele";
        private const string CastState = "Ascend Cast";
        private const string StopState = "Scream";
        private const float RetrySeconds = 1.0f;
        private const int NZPrewarmPerFrame = 3;
        private const double NZPrewarmBudgetMilliseconds = 1.5;
        private const string P2StartState = "Arena 2 Start";
        private const string PlatSetsPath = "Boss Control/Plat Sets";
        private const string SpikePath = "Boss Control/Spike Control";
        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };
        private static readonly string[] PlatformGroups = { "Climb Set", "Ascend Set" };
        private const int SpikeCount = 9;

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private bool arenaTried;
        private bool subscribed;
        private bool arenaSubscribed;
        private string lastArenaState;
        private bool awaitingFreshEntry;
        private bool active;
        private bool deathCleanupDone;
        private float nextBuildAttempt;
        private float builtAt;
        private bool firstRiseLogged;
        private GameObject privateRoot;
        private GameObject prewarmStaging;
        private Coroutine prewarmRoutine;
        private bool nzPrewarmMode;
        private bool nzPrewarmCloneComplete;
        private bool nzPrewarmSummaryLogged;
        private bool nzPhaseRequested;
        private float nzPhaseStartedAt;
        private int prewarmRigIndex;
        private float spikeHalfWidth;
        private float spikeBottomOffset;
        private float spikeZ;
        private Transform spikeTemplate;
        private PlayMakerFSM sourceControl;
        private double prewarmMilliseconds;
        private double prewarmWorstBatchMilliseconds;
        private int prewarmBatches;
        private readonly List<PlatformRig> rigs = new List<PlatformRig>();
        private static P3PlatformSpikes activeInstance;

        private sealed class PlatformRig
        {
            internal PlayMakerFSM platform;
            internal Collider2D platformCollider;
            internal Renderer[] platformRenderers;
            internal GameObject privateGroup;
            internal bool prewarmFailed;
            internal bool prewarmComplete;
            internal bool prewarmReady;
            internal bool positionedForP3;
            internal readonly List<PlayMakerFSM> spikes = new List<PlayMakerFSM>();
            internal readonly HashSet<PlayMakerFSM> initialRiseSent = new HashSet<PlayMakerFSM>();
            internal readonly HashSet<PlayMakerFSM> normalizedDown = new HashSet<PlayMakerFSM>();
        }

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
            activeInstance = this;
            if (control == null || control.Fsm == null) return;
            lastArenaState = ArenaState();
            if (IsResetState(lastArenaState)) awaitingFreshEntry = true;
            Subscribe();
            HandleState(control.Fsm.ActiveStateName);
        }

        // OrbRadiance.SetEnabled(false) 会同步调用这里，无需等待下一帧。
        internal static void Restore()
        {
            if (activeInstance != null) activeInstance.Stop();
        }

        private void OnEnable() => Subscribe();

        private void Subscribe()
        {
            if (!subscribed && control != null && control.Fsm != null)
            {
                control.Fsm.StateChanged += OnStateChanged;
                subscribed = true;
            }
            PlayMakerFSM parentControl = ResolveArena();
            if (!arenaSubscribed && parentControl != null && parentControl.Fsm != null)
            {
                parentControl.Fsm.StateChanged += OnArenaStateChanged;
                arenaSubscribed = true;
            }
        }

        private void OnDisable()
        {
            if (subscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnStateChanged;
            subscribed = false;
            if (arenaSubscribed && arena != null && arena.Fsm != null)
                arena.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
            Stop();
        }

        private void OnDestroy()
        {
            OnDisable();
            if (activeInstance == this) activeInstance = null;
        }

        private void OnStateChanged(FsmState state)
        {
            if (state != null) HandleState(state.Name);
        }

        private void Update()
        {
            HeroController hero = HeroController.instance;
            if (IsSpikeMode() && hero != null && hero.cState.dead)
            {
                deathCleanupDone = true;
                if (active || nzPrewarmMode) Stop();
                return;
            }
            if (IsSpikeMode() && deathCleanupDone && control != null && control.Fsm != null &&
                control.Fsm.ActiveStateName != P2StartState) return;
            if (IsSpikeMode() && deathCleanupDone && control != null && control.Fsm != null &&
                control.Fsm.ActiveStateName == P2StartState) deathCleanupDone = false;
            if (!OrbRadiance.Settings.Enabled || !IsSpikeMode())
            {
                Stop();
                return;
            }
            if (PollArenaReset()) return;
            if (control != null && control.Fsm != null) HandleState(control.Fsm.ActiveStateName);
            if (nzPrewarmMode) UpdateNZPrewarmState();
            if (active)
            {
                DrivePlatforms();
                if (!firstRiseLogged && Time.time - builtAt >= 2f)
                {
                    firstRiseLogged = true;
                    int total = rigs.Sum(rig => rig.spikes.Count);
                    int up = rigs.Sum(rig => rig.spikes.Count(spike => spike != null &&
                        spike.Fsm != null && spike.Fsm.ActiveStateName == "Up"));
                    int started = rigs.Sum(rig => rig.spikes.Count(spike => spike != null &&
                        spike.Fsm != null && spike.Fsm.Started));
                    int downed = rigs.Sum(rig => rig.spikes.Count(spike => spike != null &&
                        spike.Fsm != null && spike.Fsm.ActiveStateName == "Downed"));
                    int readyPlatforms = rigs.Count(IsPlatformReady);
                    PlatformRig sample = rigs.FirstOrDefault();
                    string sampleState = sample != null && sample.platform != null && sample.platform.Fsm != null
                        ? sample.platform.Fsm.ActiveStateName : "<missing>";
                    string sampleSpikeState = sample != null && sample.spikes.Count > 0 && sample.spikes[0] != null &&
                        sample.spikes[0].Fsm != null ? sample.spikes[0].Fsm.ActiveStateName : "<missing>";
                    int rendererOn = rigs.Sum(rig => rig.spikes.Sum(spike => spike == null ? 0 :
                        spike.GetComponentsInChildren<Renderer>(true).Count(renderer => renderer != null && renderer.enabled)));
                    int colliderOn = rigs.Sum(rig => rig.spikes.Sum(spike => spike == null ? 0 :
                        spike.GetComponentsInChildren<Collider2D>(true).Count(collider => collider != null && collider.enabled)));
                    OrbRadiance.Instance.Log("[p3spikes] First rise check: Up=" + up + "/" + total +
                        ", Downed=" + downed + ", Started=" + started + ", platformReady=" + readyPlatforms +
                        "/" + rigs.Count + ", samplePlatformState=" + sampleState +
                        ", sampleSpikeState=" + sampleSpikeState + ", rendererOn=" + rendererOn +
                        ", colliderOn=" + colliderOn + ", activeInHierarchy=" +
                        (privateRoot != null && privateRoot.activeInHierarchy));
                }
            }
        }

        private void HandleState(string state)
        {
            if (!OrbRadiance.Settings.Enabled || !IsSpikeMode() || state == StopState)
            {
                Stop();
                return;
            }
            if (state == P2StartState && IsSpikeMode())
            {
                deathCleanupDone = false;
                if (!nzPrewarmMode && Time.time >= nextBuildAttempt)
                {
                    nextBuildAttempt = Time.time + RetrySeconds;
                    BeginNZPrewarm();
                }
                return;
            }
            // Ascend Tele 是瞬态，组件可能在它结束后才初始化。
            // 只要原生 Control 已处在长期运行的 Ascend Cast，也允许本轮 P3 起刺。
            // 构建失败时在 P3 按间隔重试，避免首帧源组件未就绪而整阶段无刺。
            if (state == StartState || state == CastState)
            {
                awaitingFreshEntry = false;
                if (IsSpikeMode())
                {
                    if (!nzPhaseRequested)
                    {
                        nzPhaseRequested = true;
                        nzPhaseStartedAt = Time.realtimeSinceStartup;
                        builtAt = Time.time;
                    }
                    if (!nzPrewarmMode && Time.time >= nextBuildAttempt)
                    {
                        nextBuildAttempt = Time.time + RetrySeconds;
                        BeginNZPrewarm();
                    }
                    active = nzPrewarmMode;
                    return;
                }
            }
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null || state.Name == lastArenaState) return;
            lastArenaState = state.Name;
            if (!IsResetState(state.Name)) return;
            awaitingFreshEntry = true;
            nextBuildAttempt = 0f;
            Stop();
        }

        private bool PollArenaReset()
        {
            string state = ArenaState();
            if (state == null || state == lastArenaState) return false;
            lastArenaState = state;
            if (!IsResetState(state)) return false;
            awaitingFreshEntry = true;
            nextBuildAttempt = 0f;
            Stop();
            return true;
        }

        private static bool IsResetState(string state) => Array.IndexOf(ResetStates, state) >= 0;

        private string ArenaState()
        {
            PlayMakerFSM parent = ResolveArena();
            return parent != null && parent.Fsm != null ? parent.Fsm.ActiveStateName : null;
        }

        private PlayMakerFSM ResolveArena()
        {
            if (arena != null && arena.Fsm != null) return arena;
            if (!arenaTried)
            {
                arenaTried = true;
                Transform parent = transform.parent;
                if (parent != null)
                    foreach (PlayMakerFSM fsm in parent.GetComponents<PlayMakerFSM>())
                        if (fsm != null && fsm.Fsm != null && fsm.FsmName == "Control") { arena = fsm; break; }
                if (arena == null)
                {
                    GameObject bossRoot = GameObject.Find("Boss Control");
                    if (bossRoot != null)
                        foreach (PlayMakerFSM fsm in bossRoot.GetComponents<PlayMakerFSM>())
                            if (fsm != null && fsm.Fsm != null && fsm.FsmName == "Control") { arena = fsm; break; }
                }
            }
            return arena;
        }

        private static bool IsSpikeMode() => !OrbRadiance.EffectiveHardMode;

        private void BeginNZPrewarm()
        {
            if (!IsSpikeMode() || nzPrewarmMode) return;
            if (privateRoot != null) Clear();
            GameObject platforms = GameObject.Find(PlatSetsPath);
            GameObject spikeRoot = GameObject.Find(SpikePath);
            spikeTemplate = spikeRoot != null ? spikeRoot.transform.Find("Centre/Radiant Spike (8)") : null;
            if (platforms == null || spikeTemplate == null)
            {
                OrbRadiance.Instance.LogWarn("[p3spikes] N/Z P3 prewarm source missing; no synchronous fallback");
                return;
            }

            sourceControl = spikeTemplate.GetComponentsInChildren<PlayMakerFSM>(true)
                .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Control");
            PolygonCollider2D polygon = spikeTemplate.GetComponent<PolygonCollider2D>();
            if (sourceControl == null || polygon == null || polygon.points == null || polygon.points.Length == 0)
            {
                OrbRadiance.Instance.LogWarn("[p3spikes] N/Z P3 prewarm template is incomplete; no synchronous fallback");
                return;
            }
            spikeHalfWidth = PolygonHalfWidth(polygon);
            spikeBottomOffset = PolygonBottomOffset(polygon, spikeTemplate.position.y);
            spikeZ = spikeTemplate.position.z;
            if (spikeHalfWidth <= 0f || spikeBottomOffset <= 0f)
            {
                OrbRadiance.Instance.LogWarn("[p3spikes] N/Z P3 prewarm native spike geometry is invalid");
                return;
            }

            privateRoot = new GameObject("N/Z P3 climbing platform spikes");
            privateRoot.transform.position = Vector3.zero;
            privateRoot.transform.rotation = Quaternion.identity;
            privateRoot.transform.localScale = Vector3.one;
            prewarmStaging = new GameObject("N/Z P3 private spike staging");
            prewarmStaging.transform.SetParent(privateRoot.transform, false);
            prewarmStaging.SetActive(false);

            int platformCount = 0;
            try
            {
                foreach (string groupName in PlatformGroups)
                {
                    Transform group = platforms.transform.Find(groupName);
                    if (group == null) throw new InvalidOperationException("Missing platform group " + groupName);
                    foreach (Transform platform in group)
                    {
                        if (!platform.gameObject.name.StartsWith("Radiant Plat", StringComparison.Ordinal)) continue;
                        BoxCollider2D box = platform.GetComponentsInChildren<BoxCollider2D>(true)
                            .FirstOrDefault(item => item != null && !item.isTrigger);
                        GameObject privateGroup = null;
                        try
                        {
                            if (!P2PlatformSpikes.TryGetBoxBounds(box, out _))
                                throw new InvalidOperationException("no usable solid collider");
                            PlayMakerFSM nativePlatform = platform.GetComponents<PlayMakerFSM>()
                                .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "radiant_plat");
                            if (nativePlatform == null) throw new InvalidOperationException("platform FSM missing");
                            privateGroup = new GameObject("N/Z P3 spikes " + platform.name);
                            privateGroup.transform.SetParent(privateRoot.transform, false);
                            rigs.Add(new PlatformRig
                            {
                                platform = nativePlatform,
                                platformCollider = box,
                                platformRenderers = platform.GetComponentsInChildren<Renderer>(true),
                                privateGroup = privateGroup
                            });
                            platformCount++;
                        }
                        catch (Exception platformError)
                        {
                            if (privateGroup != null) Destroy(privateGroup);
                            OrbRadiance.Instance.LogWarn("[p3spikes] N/Z prewarm skip " + groupName + "/" +
                                platform.name + ": " + platformError.Message);
                        }
                    }
                }
                if (platformCount == 0) throw new InvalidOperationException("No usable climb platforms");
            }
            catch (Exception e)
            {
                Clear();
                OrbRadiance.Instance.LogWarn("[p3spikes] N/Z P3 prewarm setup aborted: " + e.Message);
                return;
            }

            nzPrewarmMode = true;
            nzPrewarmCloneComplete = false;
            nzPrewarmSummaryLogged = false;
            prewarmRigIndex = 0;
            prewarmMilliseconds = 0d;
            prewarmWorstBatchMilliseconds = 0d;
            prewarmBatches = 0;
            privateRoot.SetActive(true);
            active = nzPhaseRequested;
            if (active) builtAt = Time.time;
            prewarmRoutine = StartCoroutine(PrewarmNZSpikes());
        }

        private IEnumerator PrewarmNZSpikes()
        {
            while (nzPrewarmMode && privateRoot != null && prewarmRigIndex < rigs.Count)
            {
                Stopwatch batchWatch = Stopwatch.StartNew();
                int operationsThisBatch = 0;
                while (nzPrewarmMode && prewarmRigIndex < rigs.Count &&
                       operationsThisBatch < NZPrewarmPerFrame &&
                       (operationsThisBatch == 0 || batchWatch.Elapsed.TotalMilliseconds < NZPrewarmBudgetMilliseconds))
                {
                    PlatformRig rig = rigs[prewarmRigIndex];
                    if (rig.prewarmFailed || rig.spikes.Count >= SpikeCount)
                    {
                        if (!rig.prewarmFailed) rig.prewarmComplete = true;
                        prewarmRigIndex++;
                        continue;
                    }
                    operationsThisBatch++;
                    try
                    {
                        CreateNZPrewarmSpike(rig);
                    }
                    catch (Exception e)
                    {
                        rig.prewarmFailed = true;
                        if (rig.privateGroup != null)
                        {
                            rig.privateGroup.SetActive(false);
                            Destroy(rig.privateGroup);
                            rig.privateGroup = null;
                        }
                        rig.spikes.Clear();
                        OrbRadiance.Instance.LogWarn("[p3spikes] N/Z prewarm row discarded: " + e.Message);
                        prewarmRigIndex++;
                    }
                }
                batchWatch.Stop();
                double batchMs = batchWatch.Elapsed.TotalMilliseconds;
                prewarmMilliseconds += batchMs;
                prewarmWorstBatchMilliseconds = Math.Max(prewarmWorstBatchMilliseconds, batchMs);
                prewarmBatches++;
                yield return null;
            }

            if (nzPrewarmMode && privateRoot != null)
            {
                nzPrewarmCloneComplete = true;
                prewarmRoutine = null;
                LogNZPrewarmSummaryIfReady();
            }
        }

        private void CreateNZPrewarmSpike(PlatformRig rig)
        {
            if (spikeTemplate == null || sourceControl == null || prewarmStaging == null || rig.privateGroup == null ||
                !P2PlatformSpikes.TryGetBoxBounds(rig.platformCollider as BoxCollider2D,
                    out P2PlatformSpikes.Bounds2 bounds))
                throw new InvalidOperationException("prewarm row references are not ready");

            float[] centers = P2PlatformSpikePlan.EvenlySpacedCenters(bounds.minX, bounds.maxX,
                spikeHalfWidth, SpikeCount);
            int index = rig.spikes.Count;
            GameObject spike = Instantiate(spikeTemplate.gameObject, prewarmStaging.transform);
            if (spike == null) throw new InvalidOperationException("native spike clone returned null");
            spike.name = "N/Z P3 spike " + rig.platform.name + " #" + (index + 1);
            spike.transform.localScale = spikeTemplate.transform.localScale;
            spike.transform.rotation = spikeTemplate.transform.rotation;
            spike.transform.position = new Vector3(centers[index], bounds.maxY + spikeBottomOffset, spikeZ);
            if (!P2PlatformSpikes.ConfigurePrivateSpike(spike, rig.privateGroup, sourceControl,
                    -1f, rig.spikes, OrbRadiance.EffectiveEasyMode ? 1 : 0))
            {
                Destroy(spike);
                throw new InvalidOperationException("native spike FSM setup failed: " + spike.name);
            }
            // Configure while parented to inactive staging, then start this one private FSM.
            // Its collision and renderers are already disabled by ConfigurePrivateSpike.
            spike.transform.SetParent(rig.privateGroup.transform, true);
            DisableSpikeOutputs(spike);
            if (rig.spikes.Count == SpikeCount) rig.prewarmComplete = true;
        }

        private void UpdateNZPrewarmState()
        {
            foreach (PlatformRig rig in rigs)
            {
                if (!rig.prewarmComplete || rig.prewarmFailed || rig.prewarmReady) continue;
                bool ready = true;
                foreach (PlayMakerFSM spike in rig.spikes)
                {
                    if (spike == null || spike.Fsm == null || !spike.Fsm.Started)
                    {
                        ready = false;
                        continue;
                    }
                    if (spike.Fsm.ActiveStateName != "Downed") spike.SetState("Downed");
                    DisableSpikeOutputs(spike.gameObject);
                    if (spike.Fsm.ActiveStateName != "Downed") ready = false;
                }
                if (!ready) continue;
                rig.prewarmReady = true;
                if (active) PositionNZRigForP3(rig);
            }

            if (active)
                foreach (PlatformRig rig in rigs)
                    if (rig.prewarmReady && !rig.positionedForP3) PositionNZRigForP3(rig);
            LogNZPrewarmSummaryIfReady();
        }

        private void PositionNZRigForP3(PlatformRig rig)
        {
            if (rig == null || rig.positionedForP3 || !rig.prewarmReady ||
                !P2PlatformSpikes.TryGetBoxBounds(rig.platformCollider as BoxCollider2D,
                    out P2PlatformSpikes.Bounds2 bounds)) return;
            float[] centers = P2PlatformSpikePlan.EvenlySpacedCenters(bounds.minX, bounds.maxX,
                spikeHalfWidth, rig.spikes.Count);
            for (int i = 0; i < rig.spikes.Count; i++)
            {
                PlayMakerFSM spike = rig.spikes[i];
                if (spike != null) spike.transform.position = new Vector3(centers[i], bounds.maxY + spikeBottomOffset, spikeZ);
            }
            rig.positionedForP3 = true;
        }

        private void LogNZPrewarmSummaryIfReady()
        {
            if (!nzPrewarmCloneComplete || !nzPhaseRequested || nzPrewarmSummaryLogged ||
                rigs.Any(rig => !rig.prewarmFailed && !rig.prewarmReady)) return;
            nzPrewarmSummaryLogged = true;
            int count = rigs.Sum(rig => rig.spikes.Count);
            int readyPlatforms = rigs.Count(rig => rig.prewarmReady);
            string phaseWait = nzPhaseRequested
                ? (Math.Max(0f, Time.realtimeSinceStartup - nzPhaseStartedAt) * 1000f).ToString("F2") + " ms"
                : "not yet entered";
            OrbRadiance.Instance.Log("[p3spikes] N/Z prewarm summary: spikes=" + count + "/" +
                (P3PhasePlan.ClimbPlatformCount * SpikeCount) + ", platformsReady=" + readyPlatforms + "/" +
                P3PhasePlan.ClimbPlatformCount + ", batches=" + prewarmBatches + ", prepMs=" +
                prewarmMilliseconds.ToString("F2") + ", worstPrepBatchMs=" +
                prewarmWorstBatchMilliseconds.ToString("F2") + ", P3ReadyWait=" + phaseWait);
        }

        private static void DisableSpikeOutputs(GameObject spike)
        {
            foreach (Collider2D collider in spike.GetComponentsInChildren<Collider2D>(true))
                if (collider != null) collider.enabled = false;
            foreach (Renderer renderer in spike.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = false;
        }

        private void Stop()
        {
            active = false;
            Clear();
        }

        private void Clear()
        {
            firstRiseLogged = false;
            if (prewarmRoutine != null) StopCoroutine(prewarmRoutine);
            prewarmRoutine = null;
            nzPrewarmMode = false;
            nzPrewarmCloneComplete = false;
            nzPrewarmSummaryLogged = false;
            nzPhaseRequested = false;
            prewarmStaging = null;
            spikeTemplate = null;
            sourceControl = null;
            if (privateRoot != null)
            {
                privateRoot.SetActive(false);
                Destroy(privateRoot);
            }
            privateRoot = null;
            rigs.Clear();
        }

        // 实测 P3 攀爬平台可见时停在 Appear 2；
        // 原先只允许 Idle 且要求 BoxCollider.enabled 的条件无法满足，
        // 结果虽然创建了 171 根地刺，却全部保持收回。
        // 因此同时检查原版可见状态与平台自身的 Renderer，
        private static bool IsPlatformReady(PlatformRig rig)
        {
            if (rig == null || rig.platform == null || rig.platform.Fsm == null ||
                !rig.platform.gameObject.activeInHierarchy) return false;
            string state = rig.platform.Fsm.ActiveStateName;
            if (state != "Appear 2" && state != "Idle" && state != "In") return false;
            return rig.platformRenderers != null && rig.platformRenderers.Any(renderer =>
                renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy);
        }

        private void DrivePlatforms()
        {
            foreach (PlatformRig rig in rigs)
            {
                if (nzPrewarmMode && !rig.positionedForP3) continue;
                if (rig.platform == null || rig.platform.Fsm == null) continue;
                bool ready = IsPlatformReady(rig);
                if (!ready)
                {
                    // 不要只依赖碰撞体启用标志。
                    // 平台隐藏时必须清除有效伤害；重新出现后应再次执行原版升刺，
                    rig.initialRiseSent.Clear();
                    rig.normalizedDown.Clear();
                    foreach (PlayMakerFSM spike in rig.spikes)
                    {
                        if (spike != null && spike.Fsm != null && spike.Fsm.Started &&
                            spike.Fsm.ActiveStateName != "Downed")
                            spike.SendEvent("SPIKES DOWN");
                    }
                    continue;
                }

                // 避免不可见的碰撞体残留。
                // 首次激活时，即使配置期间调用过 SetState("Downed")，
                // 克隆 FSM 也可能进入 Init/Up；必须再次归位再升刺，
                bool initialComplete = true;
                foreach (PlayMakerFSM spike in rig.spikes)
                {
                    if (spike == null || spike.Fsm == null || !spike.Fsm.Started)
                    {
                        initialComplete = false;
                        continue;
                    }
                    if (rig.initialRiseSent.Contains(spike)) continue;
                    initialComplete = false;
                    if (spike.Fsm.ActiveStateName != "Downed")
                    {
                        // 这一顺序与已经验证过的 P2 地刺初始化一致。
                        // 从未激活的根对象启用时，克隆可能重新进入 Init；
                        // 只对该私有克隆强制设置已知的 Downed 状态，
                        if (rig.normalizedDown.Add(spike)) spike.SetState("Downed");
                    }
                    else
                    {
                        spike.SendEvent("UP");
                        rig.initialRiseSent.Add(spike);
                    }
                }
                if (!initialComplete) continue;

                int downCount = rig.spikes.Count(spike => spike != null && spike.Fsm != null &&
                    spike.Fsm.Started && spike.Fsm.ActiveStateName == "Downed");
                if (P3PhasePlan.ShouldRiseWholeGroup(ready, downCount, rig.spikes.Count))
                    foreach (PlayMakerFSM spike in rig.spikes) spike.SendEvent("UP");
            }
        }

        private static float PolygonHalfWidth(PolygonCollider2D polygon)
        {
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            foreach (Vector2 point in polygon.points)
            {
                Vector3 world = polygon.transform.TransformPoint(new Vector3(point.x + polygon.offset.x,
                                                                             point.y + polygon.offset.y, 0f));
                minX = Mathf.Min(minX, world.x);
                maxX = Mathf.Max(maxX, world.x);
            }
            return (maxX - minX) * 0.5f;
        }

        private static float PolygonBottomOffset(PolygonCollider2D polygon, float templateY)
        {
            float minY = float.PositiveInfinity;
            foreach (Vector2 point in polygon.points)
            {
                Vector3 world = polygon.transform.TransformPoint(new Vector3(point.x + polygon.offset.x,
                                                                             point.y + polygon.offset.y, 0f));
                minY = Mathf.Min(minY, world.y);
            }
            return templateY - minY;
        }
    }

    internal static class P3PhasePlan
    {
        internal static bool IsNormalMode(bool hard, bool easy) => !hard && !easy;
        internal static bool IsClimbGroup(string groupName) => groupName == "Climb Set" || groupName == "Ascend Set";
        internal static bool KeepAtPlatformThreshold(bool normalP3, string groupName, string platformName,
                                                     string p4Group, string p4Platform)
        {
            return (normalP3 && IsClimbGroup(groupName)) || (groupName == p4Group && platformName == p4Platform);
        }
        internal static bool ShouldForceDown(bool platformReady, string spikeState) => !platformReady && spikeState != "Downed";
        internal static bool ShouldRiseWholeGroup(bool platformReady, int downCount, int spikeCount) =>
            platformReady && spikeCount > 0 && downCount == spikeCount;
        internal const int SpikesPerPlatform = 9;
        internal const int ClimbPlatformCount = 19;
        internal const float UpwardRainInterval = 0.85f / 1.5f;
        internal const int UpwardRainNailCount = 16;
        internal const float UpwardRainSpeed = 20f;
        internal static float[] BuildCenteredRow(float centerX, int count, float spacing)
        {
            if (count <= 0 || spacing <= 0f) throw new ArgumentOutOfRangeException();
            float[] result = new float[count];
            float first = centerX - (count - 1) * spacing * 0.5f;
            for (int i = 0; i < count; i++) result[i] = first + i * spacing;
            return result;
        }
        internal static float[] CreateNativeOffsets()
        {
            int[] right = new int[4];
            int[] left = new int[4];
            for (int i = 0; i < 4; i++)
            {
                right[i] = UnityEngine.Random.Range(1, 4);
                left[i] = UnityEngine.Random.Range(1, 4);
            }
            return BuildNativeOffsets(right, left);
        }
        internal static float[] BuildNativeOffsets(int[] rightGaps, int[] leftGaps)
        {
            if (rightGaps == null || leftGaps == null || rightGaps.Length != 4 || leftGaps.Length != 4)
                throw new ArgumentException("Native comb requires four gap choices per side.");
            var offsets = new List<float>(16);
            AppendNativeSide(offsets, 0f, 2f, rightGaps);
            AppendNativeSide(offsets, -2f, -2f, leftGaps);
            return offsets.ToArray();
        }
        private static void AppendNativeSide(List<float> offsets, float position, float increment, int[] gaps)
        {
            for (int i = 0; i < gaps.Length; i++)
            {
                switch (gaps[i])
                {
                    case 1:
                        position += increment; offsets.Add(position);
                        position += increment; offsets.Add(position);
                        position += increment;
                        break;
                    case 2:
                        offsets.Add(position);
                        position += 2f * increment; offsets.Add(position);
                        position += increment;
                        break;
                    case 3:
                        offsets.Add(position);
                        position += increment; offsets.Add(position);
                        position += 2f * increment;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(gaps), "Native comb gap must be 1, 2, or 3.");
                }
            }
        }
        internal static float CameraBottomY(float cameraCenterY, float orthographicSize) => cameraCenterY - orthographicSize;
    }
}

using System;
using System.Collections;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // NORMAL P2 在 P1 分帧预创建 36 根原版私有地刺，P2 每 8 秒只复用并挪动这 4×9 根刺。
    // Hero Saver 仅广播到对应的九刺组，不影响其他组或原版刺池。
    
    
    internal sealed class P2PlatformSpikes : MonoBehaviour
    {
        private const string StateStart = "Arena 2 Start";
        private const string StateStop = "Ascend Tele";
        private const string StateP4 = "Scream";
        private const string SpikePath = "Boss Control/Spike Control";
        private const string PlatSetsPath = "Boss Control/Plat Sets";
        private const float CycleSeconds = 8f;
        private const int SpikesPerPlatform = 9;
        private const int PlatformsPerCycle = 4;
        private const float AnticSeconds = 0.5f;
        private const int PrewarmPerFrame = 2;
        private const double PrewarmBudgetMilliseconds = 1.5;
        private const float PrewarmRetrySeconds = 1f;
        private static readonly HashSet<P2PlatformSpikes> ActiveInstances = new HashSet<P2PlatformSpikes>();

        private static readonly string[] PlatformPaths =
        {
            "P2 SetA/Radiant Plat Small (3)",
            "P2 SetA/Radiant Plat Small (2)",
            "P2 SetA/Radiant Plat Thick (2)",
            "P2 SetA/Radiant Plat Wide (2)",
            "Hazard Plat/Radiant Plat Small (1)",
            "Hazard Plat/Radiant Plat Wide (3)",
            "Hazard Plat/Radiant Plat Wide (4)",
        };

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private bool arenaTried;
        private string lastArenaState;
        private bool subscribed;
        private bool arenaSubscribed;
        private bool awaitingFreshP2Entry;
        private bool running;
        private string lastState;
        private float cycleElapsed;
        private bool riseStatusLogged;
        private Transform spikeRoot;
        private GameObject spikeTemplate;
        private PlayMakerFSM templateControl;
        private float spikeBottomOffset;
        private float spikeHalfWidth;
        private GameObject poolRoot;
        private Coroutine prewarmRoutine;
        private bool poolReady;
        private bool pendingStart;
        private bool phaseFinished;
        private float nextPrewarmAttempt;
        private int prewarmCount;
        private double prewarmTotalMs;
        private double prewarmWorstBatchMs;
        private int prewarmBatches;
        private readonly GameObject[] poolGroups = new GameObject[PlatformsPerCycle];
        private readonly List<PlayMakerFSM>[] poolControls =
        {
            new List<PlayMakerFSM>(), new List<PlayMakerFSM>(),
            new List<PlayMakerFSM>(), new List<PlayMakerFSM>()
        };
        private readonly List<PlayMakerFSM> cycleControls = new List<PlayMakerFSM>();
        private readonly HashSet<PlayMakerFSM> initialRiseSent = new HashSet<PlayMakerFSM>();
        private readonly HashSet<PlayMakerFSM> normalizedDown = new HashSet<PlayMakerFSM>();

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
            ActiveInstances.Add(this);
            if (control == null || control.Fsm == null) return;

            lastState = control.Fsm.ActiveStateName;
            // OnEnable 可能早于 Init；取得引用后才能设置父 FSM 的初始状态。
            lastArenaState = ArenaState();
            Subscribe();
            HandleState(lastState);
        }

        private void OnEnable() { Subscribe(); }

        private void Subscribe()
        {
            if (!subscribed && control != null && control.Fsm != null)
            {
                control.Fsm.StateChanged += OnStateChanged;
                subscribed = true;
            }
            PlayMakerFSM arenaControl = ResolveArena();
            if (!arenaSubscribed && arenaControl != null && arenaControl.Fsm != null)
            {
                arenaControl.Fsm.StateChanged += OnArenaStateChanged;
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
            StopCycle();
            DisposePool();
        }

        private void OnDestroy()
        {
            OnDisable();
            ActiveInstances.Remove(this);
        }

        // MOD 中途关闭也立刻移除尚未进入 P2 的预创建刺，防止留在场景中。
        internal static void RestoreAll()
        {
            foreach (P2PlatformSpikes instance in ActiveInstances.ToArray())
            {
                if (instance == null) continue;
                instance.StopCycle();
                instance.DisposePool();
            }
        }

        private void OnStateChanged(FsmState state)
        {
            if (state == null) return;
            lastState = state.Name;
            if (state.Name == StateStart) awaitingFreshP2Entry = false;
            HandleState(state.Name);
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null) return;
            lastArenaState = state.Name;
            if (!P2PlatformSpikePlan.ShouldStopCycle(state.Name)) return;
            awaitingFreshP2Entry = true;
            OrbRadiance.Instance.Log("[spikes] NORMAL P2 cycle reset by parent state event: " + state.Name);
            StopCycle();
            DisposePool();
            phaseFinished = state.Name == StateStop || state.Name == StateP4;
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;
            if (PollArenaReset()) return;

            if (!P2PlatformSpikePlan.ShouldRun(OrbRadiance.Settings.Enabled,
                                               OrbRadiance.EffectiveEasyMode,
                                               OrbRadiance.EffectiveHardMode))
            {
                StopCycle();
                DisposePool();
                return;
            }
            HeroController hero = HeroController.instance;
            if (hero != null && hero.cState.dead)
            {
                StopCycle();
                DisposePool();
                phaseFinished = true;
                return;
            }

            string state = control.Fsm.ActiveStateName;
            if (state != lastState)
            {
                lastState = state;
                HandleState(state);
            }
            else if (state == StateStart && !running && !awaitingFreshP2Entry)
            {
                pendingStart = true;
            }

            // 从 P1 开始做准备；未找到原版对象时每秒重试，绝不在进入 P2 的同帧批量实例化。
            if (!phaseFinished && !poolReady && prewarmRoutine == null && poolRoot == null &&
                Time.time >= nextPrewarmAttempt)
            {
                nextPrewarmAttempt = Time.time + PrewarmRetrySeconds;
                BeginPrewarm();
            }
            if (pendingStart && poolReady && !phaseFinished && !awaitingFreshP2Entry)
                StartCycle();
            if (!running) return;

            DriveSpikeRises();
            int upCount = cycleControls.Count(fsm => fsm != null && fsm.Fsm != null && fsm.Fsm.ActiveStateName == "Up");
            if (!riseStatusLogged && cycleControls.Count > 0 &&
                (upCount == cycleControls.Count || cycleElapsed >= 2f))
            {
                int renderers = cycleControls.Sum(fsm => fsm != null ? fsm.GetComponentsInChildren<Renderer>(true).Count(r => r != null && r.enabled) : 0);
                int colliders = cycleControls.Sum(fsm => fsm != null ? fsm.GetComponentsInChildren<Collider2D>(true).Count(c => c != null && c.enabled) : 0);
                OrbRadiance.Instance.Log("[spikes] NORMAL P2 first rise status: Up=" + upCount + "/" + cycleControls.Count +
                                         ", rendererOn=" + renderers + ", colliderOn=" + colliders);
                riseStatusLogged = true;
            }
            if (P2PlatformSpikePlan.AdvanceCycle(ref cycleElapsed, Time.deltaTime, CycleSeconds))
                BuildCycle();
        }

        private void HandleState(string state)
        {
            if (P2PlatformSpikePlan.ShouldStopCycle(state))
            {
                StopCycle();
                DisposePool();
                // 进入 P3/P4 后不在同场战斗重新预热；只有下一场重战才能再次准备。
                phaseFinished = state == StateStop || state == StateP4;
                return;
            }
            if (state == StateStart &&
                P2PlatformSpikePlan.ShouldRun(OrbRadiance.Settings.Enabled,
                                              OrbRadiance.EffectiveEasyMode,
                                              OrbRadiance.EffectiveHardMode))
            {
                phaseFinished = false;
                awaitingFreshP2Entry = false;
                pendingStart = true;
                if (poolReady) StartCycle();
            }
        }

        private void StartCycle()
        {
            if (running || !poolReady || phaseFinished) return;
            pendingStart = false;
            running = true;
            cycleElapsed = 0f;
            BuildCycle();
        }

        private void StopCycle()
        {
            running = false;
            pendingStart = false;
            cycleElapsed = 0f;
            ClearCycle();
        }

        private void BeginPrewarm()
        {
            if (!ResolveSpikeTemplate()) return;
            Transform plats = ResolvePlatSets();
            if (plats == null) return;
            // P1 便验证七个平台存在，失败时稍后重试，避免造了半个对象池。
            for (int i = 0; i < PlatformPaths.Length; i++)
                if (plats.Find(PlatformPaths[i]) == null) return;

            poolRoot = new GameObject("NORMAL P2 pooled platform spikes");
            poolRoot.transform.SetParent(spikeRoot, false);
            poolRoot.transform.localPosition = Vector3.zero;
            poolRoot.transform.localRotation = Quaternion.identity;
            poolRoot.transform.localScale = Vector3.one;
            poolRoot.SetActive(false);
            for (int i = 0; i < PlatformsPerCycle; i++)
            {
                poolGroups[i] = new GameObject("P2 pooled spike group " + (i + 1));
                poolGroups[i].transform.SetParent(poolRoot.transform, false);
            }
            prewarmCount = 0;
            prewarmTotalMs = 0d;
            prewarmWorstBatchMs = 0d;
            prewarmBatches = 0;
            prewarmRoutine = StartCoroutine(PrewarmSpikes());
        }

        private IEnumerator PrewarmSpikes()
        {
            while (poolRoot != null && !poolReady && prewarmCount < PlatformsPerCycle * SpikesPerPlatform)
            {
                Stopwatch batch = Stopwatch.StartNew();
                int done = 0;
                try
                {
                    while (prewarmCount < PlatformsPerCycle * SpikesPerPlatform &&
                           done < PrewarmPerFrame &&
                           (done == 0 || batch.Elapsed.TotalMilliseconds < PrewarmBudgetMilliseconds))
                    {
                        int groupIndex = prewarmCount / SpikesPerPlatform;
                        int spikeIndex = prewarmCount % SpikesPerPlatform;
                        GameObject group = poolGroups[groupIndex];
                        GameObject spike = Instantiate(spikeTemplate, group.transform);
                        if (spike == null) throw new InvalidOperationException("Instantiate returned null");
                        spike.name = "P2 pooled spike " + (groupIndex + 1) + " #" + (spikeIndex + 1);
                        spike.transform.localScale = spikeTemplate.transform.localScale;
                        spike.transform.rotation = spikeTemplate.transform.rotation;
                        spike.transform.position = spikeTemplate.transform.position;
                        if (!ConfigurePrivateSpike(spike, group, templateControl, AnticSeconds,
                                                   poolControls[groupIndex]))
                            throw new InvalidOperationException("Private spike setup failed: " + spike.name);
                        prewarmCount++;
                        done++;
                    }
                }
                catch (Exception e)
                {
                    OrbRadiance.Instance.LogWarn("[spikes] NORMAL P2 prewarm failed: " + e.Message);
                    prewarmRoutine = null;
                    DisposePool();
                }
                finally
                {
                    batch.Stop();
                    double batchMs = batch.Elapsed.TotalMilliseconds;
                    prewarmTotalMs += batchMs;
                    prewarmWorstBatchMs = Math.Max(prewarmWorstBatchMs, batchMs);
                    prewarmBatches++;
                }
                if (poolRoot == null) yield break;
                yield return null;
            }
            if (poolRoot != null && prewarmCount == PlatformsPerCycle * SpikesPerPlatform)
            {
                poolReady = true;
                prewarmRoutine = null;
                OrbRadiance.Instance.Log("[spikes] NORMAL P2 prewarm ready: " + prewarmCount +
                    " private spikes, batches=" + prewarmBatches + ", prepMs=" +
                    prewarmTotalMs.ToString("F2") + ", worstBatchMs=" +
                    prewarmWorstBatchMs.ToString("F2"));
            }
        }

        // 周期开始只调整 36 个已创建对象的位置和状态，不再 Instantiate/Destroy。
        private void BuildCycle()
        {
            ClearCycle();
            if (!poolReady || poolRoot == null) return;
            Transform plats = ResolvePlatSets();
            if (plats == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + PlatSetsPath + "，本轮平台刺取消");
                return;
            }
            GameObject[] platforms = new GameObject[PlatformPaths.Length];
            P2PlatformSpikePlan.PlatformSpan[] spans = new P2PlatformSpikePlan.PlatformSpan[PlatformPaths.Length];
            for (int i = 0; i < PlatformPaths.Length; i++)
            {
                Transform platform = plats.Find(PlatformPaths[i]);
                if (platform == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: P2 native platform missing: " + PlatformPaths[i]);
                    return;
                }
                platforms[i] = platform.gameObject;
                BoxCollider2D box = platform.GetComponentsInChildren<BoxCollider2D>(true)
                    .FirstOrDefault(candidate => candidate != null && !candidate.isTrigger);
                if (box == null || !TryGetBoxBounds(box, out Bounds2 bounds))
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: P2 platform has no usable solid BoxCollider2D: " + PlatformPaths[i]);
                    return;
                }
                spans[i] = new P2PlatformSpikePlan.PlatformSpan(bounds.minX, bounds.maxX, bounds.maxY);
            }

            int[] chosen = P2PlatformSpikePlan.ChooseFour(platforms.Length, max => UnityEngine.Random.Range(0, max));
            P2PlatformSpikePlan.SpikePlacement[] placements = P2PlatformSpikePlan.BuildPlacements(
                chosen, spans, spikeHalfWidth, SpikesPerPlatform);
            try
            {
                for (int groupIndex = 0; groupIndex < PlatformsPerCycle; groupIndex++)
                {
                    GameObject group = poolGroups[groupIndex];
                    if (group == null || poolControls[groupIndex].Count != SpikesPerPlatform)
                        throw new InvalidOperationException("Private spike pool group missing");
                    int platformIndex = chosen[groupIndex];
                    group.name = "P2 cycle platform " + PlatformPaths[platformIndex];
                    for (int i = 0; i < SpikesPerPlatform; i++)
                    {
                        PlayMakerFSM fsm = poolControls[groupIndex][i];
                        if (fsm == null || fsm.Fsm == null) throw new InvalidOperationException("Pooled FSM missing");
                        P2PlatformSpikePlan.SpikePlacement placement = placements[groupIndex * SpikesPerPlatform + i];
                        GameObject spike = fsm.gameObject;
                        spike.transform.position = new Vector3(placement.x,
                            placement.platformTopY + spikeBottomOffset, spikeTemplate.transform.position.z);
                        // 隐藏的子树归位；启用前清除旧周期的碰撞与渲染状态。
                        if (fsm.Fsm.Started && fsm.Fsm.ActiveStateName != "Downed") fsm.SetState("Downed");
                        foreach (Collider2D collider in spike.GetComponentsInChildren<Collider2D>(true))
                            if (collider != null) collider.enabled = false;
                        foreach (Renderer renderer in spike.GetComponentsInChildren<Renderer>(true))
                            if (renderer != null) renderer.enabled = false;
                        cycleControls.Add(fsm);
                    }
                }
                if (cycleControls.Count != PlatformsPerCycle * SpikesPerPlatform)
                    throw new InvalidOperationException("Expected 36 private spikes");
                initialRiseSent.Clear();
                normalizedDown.Clear();
                riseStatusLogged = false;
                poolRoot.SetActive(true);
            }
            catch (Exception e)
            {
                ClearCycle();
                OrbRadiance.Instance.LogWarn("[spikes] NORMAL P2 pooled cycle skipped safely: " + e.Message);
                return;
            }
            OrbRadiance.Instance.Log("[spikes] NORMAL P2 pooled cycle: 4 distinct platforms × 9 spikes, next cycle in " +
                                     CycleSeconds + "s; no spike Instantiate");
        }

        private bool ResolveSpikeTemplate()
        {
            if (spikeRoot == null)
            {
                GameObject root = GameObject.Find(SpikePath);
                if (root == null) return false;
                spikeRoot = root.transform;
            }

            if (spikeTemplate == null)
            {
                Transform source = spikeRoot.Find("Centre/Radiant Spike (8)");
                if (source == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 原版地刺模板缺失 " + SpikePath + "/Centre/Radiant Spike (8)");
                    return false;
                }
                spikeTemplate = source.gameObject;
                templateControl = source.GetComponentsInChildren<PlayMakerFSM>(true)
                    .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Control");
                PolygonCollider2D polygon = source.GetComponent<PolygonCollider2D>();
                if (templateControl == null || polygon == null || polygon.points == null || polygon.points.Length == 0)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 原版地刺模板缺少 Control FSM 或 PolygonCollider2D");
                    spikeTemplate = null;
                    return false;
                }

                float minX = float.PositiveInfinity;
                float maxX = float.NegativeInfinity;
                float minY = float.PositiveInfinity;
                foreach (Vector2 point in polygon.points)
                {
                    Vector3 world = polygon.transform.TransformPoint(new Vector3(point.x + polygon.offset.x,
                                                                                 point.y + polygon.offset.y, 0f));
                    minX = Mathf.Min(minX, world.x);
                    maxX = Mathf.Max(maxX, world.x);
                    minY = Mathf.Min(minY, world.y);
                }
                spikeBottomOffset = spikeTemplate.transform.position.y - minY;
                spikeHalfWidth = (maxX - minX) * 0.5f;
                if (spikeBottomOffset <= 0f || spikeHalfWidth <= 0f)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 原版地刺碰撞轮廓投影无效");
                    spikeTemplate = null;
                    return false;
                }
            }
            return true;
        }

        private Transform ResolvePlatSets()
        {
            GameObject root = GameObject.Find(PlatSetsPath);
            return root != null ? root.transform : null;
        }

        internal static bool ConfigurePrivateSpike(GameObject spike, GameObject platformGroup,
                                                   PlayMakerFSM sourceControl, float anticSeconds,
                                                   List<PlayMakerFSM> destination, int hazardType = 0)
        {
            PlayMakerFSM[] fsms = spike.GetComponentsInChildren<PlayMakerFSM>(true);
            foreach (PlayMakerFSM fsm in fsms)
                if (fsm != null && !HiddenRoomDoor.PreparePrivateFsm(fsm)) return false;
            PlayMakerFSM privateControl = fsms.FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Control");
            PlayMakerFSM heroSaver = fsms.FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Hero Saver");
            if (privateControl == null || heroSaver == null || privateControl.UsesTemplate || heroSaver.UsesTemplate ||
                sourceControl == null || sourceControl.Fsm == null ||
                ReferenceEquals(privateControl.Fsm, sourceControl.Fsm))
                return false;

            int localized = LocalizeHeroSaver(heroSaver, platformGroup);
            if (localized == 0) return false;

            // 原版 Floor Antic 的 SetStateDowned 会在运行时解析该变量。
            // 每个分离的克隆都指向自己的子对象，防止继承的模板引用
            // 误改原版地刺树，或触发子物体缺失警告。
            FsmGameObject floorAnticObject = privateControl.FsmVariables.GetFsmGameObject("Floor Antic");
            Transform floorAntic = spike.transform.Find("Floor Antic");
            if (floorAnticObject == null || floorAntic == null) return false;
            floorAnticObject.Value = floorAntic.gameObject;

            DamageHero[] damage = spike.GetComponentsInChildren<DamageHero>(true);
            if (damage.Length == 0) return false;
            foreach (DamageHero component in damage)
                component.hazardType = hazardType;

            FsmState antic = privateControl.Fsm.GetState("Floor Antic");
            Wait wait = antic != null
                ? antic.Actions.OfType<Wait>().FirstOrDefault(action => action.time != null &&
                    action.finishEvent != null && action.finishEvent.Name == "FINISHED")
                : null;
            if (wait == null) return false;
            if (anticSeconds >= 0f) wait.time.Value = anticSeconds;

            // 原版模板可能停在 Up 且碰撞体已激活；新克隆必须先隐藏并收回，
            // 首次升起前不可造成伤害；之后受击可重新进入原版升起流程。
            privateControl.SetState("Downed");
            foreach (Collider2D collider in spike.GetComponentsInChildren<Collider2D>(true))
                if (collider != null) collider.enabled = false;
            foreach (Renderer renderer in spike.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = false;
            spike.SetActive(true);

            if (destination != null) destination.Add(privateControl);
            return true;
        }

        private static int LocalizeHeroSaver(PlayMakerFSM heroSaver, GameObject platformGroup)
        {
            int changed = 0;
            foreach (FsmState state in heroSaver.Fsm.States)
            {
                foreach (SendEventByName send in state.Actions.OfType<SendEventByName>())
                {
                    if (send.sendEvent == null || send.sendEvent.Value != "SPIKES DOWN" || send.eventTarget == null ||
                        send.eventTarget.target != FsmEventTarget.EventTarget.BroadcastAll) continue;

                    FsmEventTarget target = send.eventTarget;
                    target.target = FsmEventTarget.EventTarget.GameObject;
                    target.gameObject = new FsmOwnerDefault();
                    target.gameObject.OwnerOption = OwnerDefaultOption.SpecifyGameObject;
                    target.gameObject.GameObject.Value = platformGroup;
                    target.sendToChildren = true;
                    target.excludeSelf = false;
                    send.eventTarget = target;
                    changed++;
                }
            }
            return changed;
        }

        internal static bool TryGetBoxBounds(BoxCollider2D box, out Bounds2 bounds)
        {
            bounds = default(Bounds2);
            if (box == null || box.size.x <= 0f || box.size.y <= 0f) return false;
            Vector2 offset = box.offset;
            Vector2 half = box.size * 0.5f;
            Vector3[] corners =
            {
                box.transform.TransformPoint(new Vector3(offset.x-half.x, offset.y-half.y, 0f)),
                box.transform.TransformPoint(new Vector3(offset.x-half.x, offset.y+half.y, 0f)),
                box.transform.TransformPoint(new Vector3(offset.x+half.x, offset.y-half.y, 0f)),
                box.transform.TransformPoint(new Vector3(offset.x+half.x, offset.y+half.y, 0f)),
            };
            float minX = corners.Min(point => point.x);
            float maxX = corners.Max(point => point.x);
            float maxY = corners.Max(point => point.y);
            bounds = new Bounds2(minX, maxX, maxY);
            return maxX > minX;
        }

        private void DriveSpikeRises()
        {
            foreach (PlayMakerFSM fsm in cycleControls)
            {
                if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Started) continue;
                if (fsm.Fsm.ActiveStateName != "Downed")
                {
                    if (P2PlatformSpikePlan.ShouldNormalizeDown(fsm.Fsm.Started, fsm.Fsm.ActiveStateName,
                                                               normalizedDown.Contains(fsm), initialRiseSent.Contains(fsm)))
                    {
                        normalizedDown.Add(fsm);
                        fsm.SendEvent("SPIKES DOWN");
                    }
                    continue;
                }
                if (!P2PlatformSpikePlan.ShouldSendRise(fsm.Fsm.Started, fsm.Fsm.ActiveStateName)) continue;
                fsm.SendEvent("UP");
                initialRiseSent.Add(fsm);
            }
        }

        private void ClearCycle()
        {
            if (poolRoot != null) poolRoot.SetActive(false);
            riseStatusLogged = false;
            cycleControls.Clear();
            initialRiseSent.Clear();
            normalizedDown.Clear();
        }

        private void DisposePool()
        {
            // 等待下一次真正入场时重新预热，不能保留指向已经卸载场景的 FSM 引用。
            if (prewarmRoutine != null) StopCoroutine(prewarmRoutine);
            prewarmRoutine = null;
            poolReady = false;
            prewarmCount = 0;
            ClearCycle();
            if (poolRoot != null)
            {
                poolRoot.SetActive(false);
                Destroy(poolRoot);
            }
            poolRoot = null;
            for (int i = 0; i < PlatformsPerCycle; i++)
            {
                poolControls[i].Clear();
                poolGroups[i] = null;
            }
            spikeTemplate = null;
            templateControl = null;
            spikeRoot = null;
        }

        private string ArenaState()
        {
            PlayMakerFSM resolved = ResolveArena();
            return resolved != null && resolved.Fsm != null ? resolved.Fsm.ActiveStateName : null;
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

        private bool PollArenaReset()
        {
            string state = ArenaState();
            if (state == null || state == lastArenaState) return false;
            lastArenaState = state;
            if (!P2PlatformSpikePlan.ShouldStopCycle(state)) return false;
            awaitingFreshP2Entry = true;
            OrbRadiance.Instance.Log("[spikes] NORMAL P2 cycle reset by parent state poll: " + state);
            StopCycle();
            DisposePool();
            phaseFinished = state == StateStop || state == StateP4;
            return true;
        }


        internal struct Bounds2
        {
            internal readonly float minX;
            internal readonly float maxX;
            internal readonly float maxY;
            internal Bounds2(float min, float max, float top) { minX = min; maxX = max; maxY = top; }
        }
    }
}

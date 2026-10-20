using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // NORMAL P2 每周期在 4 个平台生成 36 根私有原版地刺。
    // Hero Saver 仅广播到平台自己的九刺组，不影响其他组或原版刺池。
    
    
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
        private GameObject cycleRoot;
        private readonly List<PlayMakerFSM> cycleControls = new List<PlayMakerFSM>();
        private readonly HashSet<PlayMakerFSM> initialRiseSent = new HashSet<PlayMakerFSM>();
        private readonly HashSet<PlayMakerFSM> normalizedDown = new HashSet<PlayMakerFSM>();

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
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
        }

        private void OnDestroy()
        {
            OnDisable();
        }

        private void OnStateChanged(FsmState state)
        {
            if (state == null) return;
            lastState = state.Name;
            awaitingFreshP2Entry = false;
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
                return;
            }

            // StateChanged 处理短暂的阶段切换；轮询用于场景重新加载时兜底。
            string state = control.Fsm.ActiveStateName;
            if (state != lastState)
            {
                lastState = state;
                HandleState(state);
            }
            else if (state == StateStart && !running && !awaitingFreshP2Entry)
            {
                // 在 P2 切换总开关或重新启用组件时，完整重启一次私有地刺周期。
                HandleState(state);
            }

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
            {
                // 帧卡顿后不补发错过的周期，而是从当前时刻开始新周期。
                BuildCycle();
            }
        }

        private void HandleState(string state)
        {
            if (P2PlatformSpikePlan.ShouldStopCycle(state))
            {
                StopCycle();
                return;
            }

            if (state == StateStart &&
                P2PlatformSpikePlan.ShouldRun(OrbRadiance.Settings.Enabled,
                                              OrbRadiance.EffectiveEasyMode,
                                              OrbRadiance.EffectiveHardMode))
            {
                StartCycle();
            }
        }

        private void StartCycle()
        {
            if (running) return;
            running = true;
            cycleElapsed = 0f;
            BuildCycle();
        }

        private void StopCycle()
        {
            running = false;
            cycleElapsed = 0f;
            ClearCycle();
        }

        private void BuildCycle()
        {
            ClearCycle();
            if (!ResolveSpikeTemplate()) return;

            Transform plats = ResolvePlatSets();
            if (plats == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + PlatSetsPath + "，本轮平台刺取消");
                return;
            }

            GameObject[] platforms = new GameObject[PlatformPaths.Length];
            BoxCollider2D[] boxes = new BoxCollider2D[PlatformPaths.Length];
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
                boxes[i] = platform.GetComponentsInChildren<BoxCollider2D>(true)
                    .FirstOrDefault(box => box != null && !box.isTrigger);
                if (boxes[i] == null || !TryGetBoxBounds(boxes[i], out Bounds2 bounds))
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: P2 platform has no usable solid BoxCollider2D: " + PlatformPaths[i]);
                    return;
                }
                spans[i] = new P2PlatformSpikePlan.PlatformSpan(bounds.minX, bounds.maxX, bounds.maxY);
            }

            int[] chosen = P2PlatformSpikePlan.ChooseFour(platforms.Length, max => UnityEngine.Random.Range(0, max));
            P2PlatformSpikePlan.SpikePlacement[] placements = P2PlatformSpikePlan.BuildPlacements(
                chosen, spans, spikeHalfWidth, SpikesPerPlatform);
            GameObject root = new GameObject("NORMAL P2 platform spikes cycle");
            cycleRoot = root; // 配置克隆前先登记所有权，以便失败时统一清理部分创建的对象。
            root.transform.SetParent(spikeRoot, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            root.SetActive(false);

            GameObject[] platformGroups = new GameObject[PlatformPaths.Length];
            foreach (int platformIndex in chosen)
            {
                GameObject group = new GameObject("P2 cycle platform " + PlatformPaths[platformIndex]);
                group.transform.SetParent(root.transform, false);
                platformGroups[platformIndex] = group;
            }

            try
            {
                for (int placementIndex = 0; placementIndex < placements.Length; placementIndex++)
                {
                    P2PlatformSpikePlan.SpikePlacement placement = placements[placementIndex];
                    GameObject platformGroup = platformGroups[placement.platformIndex];
                    if (platformGroup == null) throw new InvalidOperationException("Missing private platform group");
                    GameObject spike = Instantiate(spikeTemplate, platformGroup.transform);
                    if (spike == null) throw new InvalidOperationException("Instantiate returned null");
                    spike.name = "P2 cycle spike " + PlatformPaths[placement.platformIndex] + " #" +
                                 (placementIndex % SpikesPerPlatform + 1);
                    spike.transform.localScale = spikeTemplate.transform.localScale;
                    spike.transform.rotation = spikeTemplate.transform.rotation;
                    spike.transform.position = new Vector3(placement.x, placement.platformTopY + spikeBottomOffset,
                                                           spikeTemplate.transform.position.z);

                    if (!ConfigurePrivateSpike(spike, platformGroup, templateControl, AnticSeconds, cycleControls))
                        throw new InvalidOperationException("Native spike clone failed private FSM/hazard setup: " + spike.name);
                }

                if (cycleControls.Count != PlatformsPerCycle * SpikesPerPlatform)
                    throw new InvalidOperationException("Expected 36 configured spikes, got " + cycleControls.Count);
                root.SetActive(true);
            }
            catch (Exception e)
            {
                ClearCycle();
                OrbRadiance.Instance.LogWarn("OrbRadiance: NORMAL P2 platform spike cycle aborted safely: " + e.Message);
                return;
            }

            OrbRadiance.Instance.Log("[spikes] NORMAL P2 cycle: 4 distinct platforms, 9 native spikes each (36 total), " +
                                     "next cycle in " + CycleSeconds + "s; Hero Saver sends SPIKES DOWN to its private platform group, " +
                                     AnticSeconds + "s antic");
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
            riseStatusLogged = false;
            cycleControls.Clear();
            initialRiseSent.Clear();
            normalizedDown.Clear();
            if (cycleRoot == null) return;
            cycleRoot.SetActive(false);
            Destroy(cycleRoot);
            cycleRoot = null;
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

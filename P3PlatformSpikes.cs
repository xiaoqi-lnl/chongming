using System;
using System.Collections.Generic;
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
        private float nextBuildAttempt;
        private float builtAt;
        private bool firstRiseLogged;
        private GameObject privateRoot;
        private readonly List<PlatformRig> rigs = new List<PlatformRig>();
        private static P3PlatformSpikes activeInstance;

        private sealed class PlatformRig
        {
            internal PlayMakerFSM platform;
            internal Collider2D platformCollider;
            internal Renderer[] platformRenderers;
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
            if (!OrbRadiance.Settings.Enabled || !IsSpikeMode())
            {
                Stop();
                return;
            }
            if (PollArenaReset()) return;
            if (control != null && control.Fsm != null) HandleState(control.Fsm.ActiveStateName);
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
            // Ascend Tele 是瞬态，组件可能在它结束后才初始化。
            // 只要原生 Control 已处在长期运行的 Ascend Cast，也允许本轮 P3 起刺。
            // 构建失败时在 P3 按间隔重试，避免首帧源组件未就绪而整阶段无刺。
            if (state == StartState || state == CastState)
            {
                awaitingFreshEntry = false;
                if (!active && Time.time >= nextBuildAttempt)
                {
                    nextBuildAttempt = Time.time + RetrySeconds;
                    BeginPlatformPhase();
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

        private void BeginPlatformPhase()
        {
            active = true;
            if (privateRoot != null) return;

            GameObject platforms = GameObject.Find(PlatSetsPath);
            GameObject spikeRoot = GameObject.Find(SpikePath);
            Transform template = spikeRoot != null ? spikeRoot.transform.Find("Centre/Radiant Spike (8)") : null;
            if (platforms == null || template == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: N/Z P3 native platform spike source missing; climb spikes not created");
                active = false;
                return;
            }

            PlayMakerFSM sourceControl = template.GetComponentsInChildren<PlayMakerFSM>(true)
                .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Control");
            PolygonCollider2D polygon = template.GetComponent<PolygonCollider2D>();
            if (sourceControl == null || polygon == null || polygon.points == null || polygon.points.Length == 0)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: N/Z P3 native spike template is incomplete");
                active = false;
                return;
            }

            // Z（EASY）P3 使用 hazardType=1，N 使用 hazardType=0；
            // 先配置原版克隆再激活，避免瞬间使用错误伤害类型。
            int spikeHazardType = OrbRadiance.EffectiveEasyMode ? 1 : 0;
            float halfWidth = PolygonHalfWidth(polygon);
            float bottomOffset = PolygonBottomOffset(polygon, template.position.y);
            if (halfWidth <= 0f || bottomOffset <= 0f)
            {
                OrbRadiance.Instance.LogWarn("[p3spikes] Invalid native spike geometry: halfWidth=" + halfWidth +
                    ", bottomOffset=" + bottomOffset);
                active = false;
                return;
            }

            // 私有原版地刺克隆挂在场景根节点；原版 Spike Control 父对象
            // 可能在阶段切换时被停用，挂在其下会导致所有子地刺隐藏。
            privateRoot = new GameObject("N/Z P3 climbing platform spikes");
            privateRoot.transform.position = Vector3.zero;
            privateRoot.transform.rotation = Quaternion.identity;
            privateRoot.transform.localScale = Vector3.one;
            privateRoot.SetActive(false);

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
                        PlatformRig rig = null;
                        try
                        {
                            if (!P2PlatformSpikes.TryGetBoxBounds(box, out P2PlatformSpikes.Bounds2 bounds))
                                throw new InvalidOperationException("no usable solid collider");
                            float[] centers = P2PlatformSpikePlan.EvenlySpacedCenters(bounds.minX, bounds.maxX, halfWidth, SpikeCount);
                            PlayMakerFSM nativePlatform = platform.GetComponents<PlayMakerFSM>()
                                .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "radiant_plat");
                            if (nativePlatform == null) throw new InvalidOperationException("platform FSM missing");
                            privateGroup = new GameObject("P3 platform spikes " + platform.name);
                            privateGroup.transform.SetParent(privateRoot.transform, false);
                            rig = new PlatformRig
                            {
                                platform = nativePlatform,
                                platformCollider = box,
                                platformRenderers = platform.GetComponentsInChildren<Renderer>(true)
                            };
                            rigs.Add(rig);
                            for (int i = 0; i < centers.Length; i++)
                            {
                                GameObject spike = Instantiate(template.gameObject, privateGroup.transform);
                                if (spike == null) throw new InvalidOperationException("native spike clone returned null");
                                spike.name = "P3 spike " + platform.name + " #" + (i + 1);
                                spike.transform.localScale = template.transform.localScale;
                                spike.transform.rotation = template.transform.rotation;
                                spike.transform.position = new Vector3(centers[i], bounds.maxY + bottomOffset, template.position.z);
                                if (!P2PlatformSpikes.ConfigurePrivateSpike(spike, privateGroup, sourceControl,
                                        -1f, rig.spikes, spikeHazardType))
                                    throw new InvalidOperationException("native spike FSM setup failed: " + spike.name);
                            }
                            platformCount++;
                        }
                        catch (Exception platformError)
                        {
                            if (rig != null) rigs.Remove(rig);
                            if (privateGroup != null)
                            {
                                privateGroup.SetActive(false);
                                Destroy(privateGroup);
                            }
                            OrbRadiance.Instance.LogWarn("[p3spikes] Skip " + groupName + "/" + platform.name + ": " + platformError.Message);
                        }
                    }
                }
                if (platformCount == 0)
                    throw new InvalidOperationException("No usable climb platforms; expected " + P3PhasePlan.ClimbPlatformCount);
                if (platformCount != P3PhasePlan.ClimbPlatformCount)
                    OrbRadiance.Instance.LogWarn("[p3spikes] Partial construction: " + platformCount +
                        "/" + P3PhasePlan.ClimbPlatformCount + " climb platforms have spikes");
            }
            catch (Exception e)
            {
                Clear();
                active = false;
                OrbRadiance.Instance.LogWarn("OrbRadiance: N/Z P3 platform spike setup aborted safely: " + e.Message);
                return;
            }

            privateRoot.SetActive(true);
            builtAt = Time.time;
            firstRiseLogged = false;
            OrbRadiance.Instance.Log("[spikes] N/Z P3: " + platformCount + " climb platforms, " + rigs.Sum(r => r.spikes.Count) +
                                     " private native spikes; each platform retracts/regrows as one group");
        }

        private void Stop()
        {
            active = false;
            Clear();
        }

        private void Clear()
        {
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

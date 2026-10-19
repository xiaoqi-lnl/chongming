using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // Normal P3 uses nine private native spike FSMs on every native climbing platform.
    // Hero Saver events are localized to the platform's private group, so one hit retracts
    // that whole row and the native FSMs handle their usual regrowth timing.
    internal sealed class P3PlatformSpikes : MonoBehaviour
    {
        private const string StartState = "Ascend Tele";
        private const string StopState = "Scream";
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
        private GameObject privateRoot;
        private readonly List<PlatformRig> rigs = new List<PlatformRig>();

        private sealed class PlatformRig
        {
            internal PlayMakerFSM platform;
            internal Collider2D platformCollider;
            internal readonly List<PlayMakerFSM> spikes = new List<PlayMakerFSM>();
        }

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
            if (control == null || control.Fsm == null) return;
            lastArenaState = ArenaState();
            if (IsResetState(lastArenaState)) awaitingFreshEntry = true;
            Subscribe();
            HandleState(control.Fsm.ActiveStateName);
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

        private void OnDestroy() => OnDisable();

        private void OnStateChanged(FsmState state)
        {
            if (state != null) HandleState(state.Name);
        }

        private void Update()
        {
            if (!OrbRadiance.Settings.Enabled || !IsNormalMode())
            {
                Stop();
                return;
            }
            if (PollArenaReset()) return;
            if (control != null && control.Fsm != null) HandleState(control.Fsm.ActiveStateName);
            if (active) DrivePlatforms();
        }

        private void HandleState(string state)
        {
            if (!OrbRadiance.Settings.Enabled || !IsNormalMode() || state == StopState)
            {
                Stop();
                return;
            }
            if (awaitingFreshEntry)
            {
                if (state == StartState) return;
                awaitingFreshEntry = false;
            }
            if (state == StartState && !active) BeginPlatformPhase();
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null || state.Name == lastArenaState) return;
            lastArenaState = state.Name;
            if (!IsResetState(state.Name)) return;
            awaitingFreshEntry = true;
            Stop();
        }

        private bool PollArenaReset()
        {
            string state = ArenaState();
            if (state == null || state == lastArenaState) return false;
            lastArenaState = state;
            if (!IsResetState(state)) return false;
            awaitingFreshEntry = true;
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

        private static bool IsNormalMode() => P3PhasePlan.IsNormalMode(OrbRadiance.EffectiveHardMode, OrbRadiance.EffectiveEasyMode);

        private void BeginPlatformPhase()
        {
            active = true;
            if (privateRoot != null) return;

            GameObject platforms = GameObject.Find(PlatSetsPath);
            GameObject spikeRoot = GameObject.Find(SpikePath);
            Transform template = spikeRoot != null ? spikeRoot.transform.Find("Centre/Radiant Spike (8)") : null;
            if (platforms == null || template == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: NORMAL P3 native platform spike source missing; climb spikes not created");
                active = false;
                return;
            }

            PlayMakerFSM sourceControl = template.GetComponentsInChildren<PlayMakerFSM>(true)
                .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Control");
            PolygonCollider2D polygon = template.GetComponent<PolygonCollider2D>();
            if (sourceControl == null || polygon == null || polygon.points == null || polygon.points.Length == 0)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: NORMAL P3 native spike template is incomplete");
                active = false;
                return;
            }

            float halfWidth = PolygonHalfWidth(polygon);
            float bottomOffset = PolygonBottomOffset(polygon, template.position.y);
            if (halfWidth <= 0f || bottomOffset <= 0f)
            {
                active = false;
                return;
            }

            privateRoot = new GameObject("NORMAL P3 climbing platform spikes");
            privateRoot.transform.SetParent(spikeRoot.transform, false);
            privateRoot.transform.localPosition = Vector3.zero;
            privateRoot.transform.localRotation = Quaternion.identity;
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
                        if (!P2PlatformSpikes.TryGetBoxBounds(box, out P2PlatformSpikes.Bounds2 bounds))
                            throw new InvalidOperationException("Platform has no solid collider: " + platform.name);

                        float[] centers = P2PlatformSpikePlan.EvenlySpacedCenters(bounds.minX, bounds.maxX, halfWidth, SpikeCount);
                        PlayMakerFSM nativePlatform = platform.GetComponents<PlayMakerFSM>()
                            .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "radiant_plat");
                        if (nativePlatform == null) throw new InvalidOperationException("Platform FSM missing: " + platform.name);
                        GameObject privateGroup = new GameObject("P3 platform spikes " + platform.name);
                        privateGroup.transform.SetParent(privateRoot.transform, false);
                        var rig = new PlatformRig
                        {
                            platform = nativePlatform,
                            platformCollider = box
                        };
                        rigs.Add(rig);
                        for (int i = 0; i < centers.Length; i++)
                        {
                            GameObject spike = Instantiate(template.gameObject, privateGroup.transform);
                            if (spike == null) throw new InvalidOperationException("Native spike clone returned null");
                            spike.name = "P3 spike " + platform.name + " #" + (i + 1);
                            spike.transform.localScale = template.transform.localScale;
                            spike.transform.rotation = template.transform.rotation;
                            spike.transform.position = new Vector3(centers[i], bounds.maxY + bottomOffset, template.position.z);
                            if (!P2PlatformSpikes.ConfigurePrivateSpike(spike, privateGroup, sourceControl, -1f, rig.spikes))
                                throw new InvalidOperationException("Native spike FSM setup failed: " + spike.name);
                        }
                        platformCount++;
                    }
                }
                if (platformCount != P3PhasePlan.ClimbPlatformCount)
                    throw new InvalidOperationException("Expected " + P3PhasePlan.ClimbPlatformCount +
                        " native climb platforms, found " + platformCount);
            }
            catch (Exception e)
            {
                Clear();
                active = false;
                OrbRadiance.Instance.LogWarn("OrbRadiance: NORMAL P3 platform spike setup aborted safely: " + e.Message);
                return;
            }

            privateRoot.SetActive(true);
            OrbRadiance.Instance.Log("[spikes] NORMAL P3: " + platformCount + " climb platforms, " + rigs.Sum(r => r.spikes.Count) +
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

        private void DrivePlatforms()
        {
            foreach (PlatformRig rig in rigs)
            {
                if (rig.platform == null || rig.platform.Fsm == null) continue;
                bool ready = rig.platform.Fsm.ActiveStateName == "Idle" && rig.platformCollider != null &&
                             rig.platformCollider.enabled && rig.platformCollider.gameObject.activeInHierarchy;
                foreach (PlayMakerFSM spike in rig.spikes)
                {
                    if (spike == null || spike.Fsm == null || !spike.Fsm.Started) continue;
                    string state = spike.Fsm.ActiveStateName;
                    if (P3PhasePlan.ShouldForceDown(ready, state))
                    {
                        spike.SendEvent("SPIKES DOWN");
                    }
                }
                int downCount = rig.spikes.Count(spike => spike != null && spike.Fsm != null &&
                    spike.Fsm.Started && spike.Fsm.ActiveStateName == "Downed");
                if (P3PhasePlan.ShouldRiseWholeGroup(ready, downCount, rig.spikes.Count))
                {
                    // Let the native down state register for a frame, then the unmodified native
                    // Floor Antic wait controls the whole row's regrowth timing.
                    foreach (PlayMakerFSM spike in rig.spikes) spike.SendEvent("UP");
                }
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

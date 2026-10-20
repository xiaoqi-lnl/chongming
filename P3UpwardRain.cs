using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // NORMAL P3 上行剑雨试验组件；当前在 OrbRadiance.cs 中未挂载。
    
    
    internal sealed class P3UpwardRain : MonoBehaviour
    {
        private const string StateStart = "Ascend Tele";
        private const string StateStop = "Scream";
        private const float CameraCenterJitter = 8f;
        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };

        private sealed class Shot
        {
            internal Transform transform;
            internal float speed;
        }

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private bool arenaTried;
        private bool subscribed;
        private bool arenaSubscribed;
        private string lastArenaState;
        private bool awaitingFreshEntry;
        private bool running;
        private float nextWaveAt;
        private float z;
        private GameObject prefab;
        private readonly List<Shot> shots = new List<Shot>();

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
            if (!OrbRadiance.Settings.Enabled || !P3PhasePlan.IsNormalMode(OrbRadiance.EffectiveHardMode,
                                                                          OrbRadiance.EffectiveEasyMode))
            {
                Stop();
                return;
            }
            if (control == null || control.Fsm == null) return;
            if (PollArenaReset()) return;
            HandleState(control.Fsm.ActiveStateName);
            if (!running) return;

            shots.RemoveAll(shot => shot.transform == null);
            Camera camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
            bool hasView = TryGetViewRect(camera, out float centerX, out float bottomY, out float topY);
            float top = hasView ? topY + 1f : float.NegativeInfinity;
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                Shot shot = shots[i];
                Vector3 position = shot.transform.position;
                position.y += shot.speed * Time.deltaTime;
                shot.transform.position = position;
                if (hasView && position.y > top)
                {
                    Destroy(shot.transform.gameObject);
                    shots.RemoveAt(i);
                }
            }

            if (Time.time >= nextWaveAt)
            {
                FireWave();
                nextWaveAt = Time.time + P3PhasePlan.UpwardRainInterval;
            }
        }

        private void HandleState(string state)
        {
            if (!OrbRadiance.Settings.Enabled || !P3PhasePlan.IsNormalMode(OrbRadiance.EffectiveHardMode,
                                                                          OrbRadiance.EffectiveEasyMode) ||
                state == StateStop || IsResetState(state))
            {
                Stop();
                return;
            }
            if (awaitingFreshEntry)
            {
                if (state == StateStart) return;
                awaitingFreshEntry = false;
            }
            if (state == StateStart && !running) BeginRain();
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

        private void BeginRain()
        {
            if (prefab == null) prefab = P2NailStream.ResolveNailPrefab(gameObject);
            if (prefab == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: P3 upward rain could not resolve the Radiant Nail prefab");
                return;
            }
            running = true;
            z = transform.position.z;
            nextWaveAt = Time.time;
            OrbRadiance.Instance.Log("[p3rain] NORMAL P3 native Type3 row enabled; cadence " + P3PhasePlan.UpwardRainInterval +
                                     "s, speed " + P3PhasePlan.UpwardRainSpeed + "");
        }

        private void Stop()
        {
            running = false;
            nextWaveAt = 0f;
            foreach (Shot shot in shots)
                if (shot.transform != null) Destroy(shot.transform.gameObject);
            shots.Clear();
        }

        private void FireWave()
        {
            Camera camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
            if (!TryGetViewRect(camera, out float cameraCenterX, out float bottomY, out _))
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: no main camera; skipped one P3 upward rain row");
                return;
            }

            float centerX = cameraCenterX + UnityEngine.Random.Range(-CameraCenterJitter, CameraCenterJitter);
            float[] offsets = P3PhasePlan.CreateNativeOffsets();

            for (int i = 0; i < P3PhasePlan.UpwardRainNailCount; i++)
            {
                GameObject nail = null;
                try
                {
                    nail = Instantiate(prefab, new Vector3(centerX + offsets[i], bottomY, z), Quaternion.identity);
                }
                catch (Exception e)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: P3 upward rain projectile failed: " + e.Message);
                }
                if (nail == null) continue;
                nail.SetActive(true);
                // 保留原版弹体的 Control、伤害和碰撞体配置，不发送
                // 原版 FAN 事件；阶段组件只负责驱动向上的轨迹。
                if (nail.GetComponent<NailProjectile>() == null) nail.AddComponent<NailProjectile>();
                nail.transform.rotation = Quaternion.Euler(0f, 0f, 180f);
                shots.Add(new Shot { transform = nail.transform, speed = P3PhasePlan.UpwardRainSpeed });
            }
        }

        private static bool TryGetViewRect(Camera camera, out float centerX, out float bottomY, out float topY)
        {
            centerX = bottomY = topY = 0f;
            if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0) return false;
            Rect pixels = camera.pixelRect;
            float distance = -camera.transform.position.z;
            if (distance <= 0f) distance = 1f;
            Vector3 bottomLeft = camera.ScreenToWorldPoint(new Vector3(pixels.xMin, pixels.yMin, distance));
            Vector3 topRight = camera.ScreenToWorldPoint(new Vector3(pixels.xMax, pixels.yMax, distance));
            float width = topRight.x - bottomLeft.x;
            float height = topRight.y - bottomLeft.y;
            if (width <= 10f || width >= 80f || height <= 5f || height >= 60f) return false;
            centerX = (bottomLeft.x + topRight.x) * 0.5f;
            bottomY = bottomLeft.y;
            topY = topRight.y;
            return true;
        }
    }
}

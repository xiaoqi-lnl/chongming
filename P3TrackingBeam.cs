using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // NORMAL P3 每轮一束追踪激光：追踪 0.3s、锁定 0.1s、发射 0.2s、间隔 0.2s。


    internal sealed class P3TrackingBeam : MonoBehaviour
    {
        private const string CastState = "Ascend Cast";
        private const string EntryState = "Ascend Tele";
        private const string ExitState = "Scream";
        private const string TemplatePath = "Eye Beam Glow/Ascend Beam";
        private const string BeamFsmName = "Control";
        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };

        // 用户指定的三段时序，不计首次克隆 FSM 的启动等待。
        private const float TrackTime = 0.3f;
        private const float HoldTime = 0.1f;
        private const float FireTime = 0.2f;
        private const float Interval = 0.2f;
        private const float StartTimeout = 1f;
        private const float RetryInterval = 0.4f;

        // 采用 HardBeamWall 已验证的 Ascend Beam 几何单位和屏外锚点。
        private const float BeamLength = 400f;
        private const float BeamUnitLen = 1.765625f;
        private const float BeamWidth = 1f;
        private const float BeamUnitThick = 0.5030646f;
        private const float OutMargin = 20f;
        private const float ViewHalfW = 16f;
        private const float ViewHalfH = 9f;

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private bool arenaTried;
        private bool subscribed;
        private bool arenaSubscribed;
        private bool awaitingFreshEntry;
        private string lastArenaState;

        private Transform template;
        private GameObject liveBeam;
        private PlayMakerFSM liveFsm;
        private Coroutine loop;
        private int side;
        private float edgeFraction;
        private bool warnedTemplateMissing;
        private bool warnedFsmMissing;
        private static P3TrackingBeam active;

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
            active = this;
            lastArenaState = ArenaState();
            if (IsResetState(lastArenaState)) awaitingFreshEntry = true;
            Subscribe();
            HandleState(control != null && control.Fsm != null ? control.Fsm.ActiveStateName : null);
        }

        internal static void Restore()
        {
            if (active != null) active.StopShots();
        }

        private static bool InNormalMode()
        {
            return P3PhasePlan.IsNormalMode(OrbRadiance.EffectiveHardMode, OrbRadiance.EffectiveEasyMode);
        }

        private static bool IsResetState(string name) => Array.IndexOf(ResetStates, name) >= 0;

        private bool CanRun()
        {
            return OrbRadiance.Settings.Enabled && InNormalMode() && !awaitingFreshEntry &&
                   control != null && control.Fsm != null &&
                   control.Fsm.ActiveStateName == CastState;
        }

        private void OnEnable() => Subscribe();

        private void Subscribe()
        {
            if (!subscribed && control != null && control.Fsm != null)
            {
                control.Fsm.StateChanged += OnControlStateChanged;
                subscribed = true;
            }
            PlayMakerFSM parent = ResolveArena();
            if (!arenaSubscribed && parent != null && parent.Fsm != null)
            {
                parent.Fsm.StateChanged += OnArenaStateChanged;
                arenaSubscribed = true;
            }
        }

        private void OnDisable()
        {
            if (subscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnControlStateChanged;
            subscribed = false;
            if (arenaSubscribed && arena != null && arena.Fsm != null)
                arena.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
            StopShots();
        }

        private void OnDestroy()
        {
            OnDisable();
            if (active == this) active = null;
        }

        private void OnControlStateChanged(FsmState state)
        {
            if (state == null) return;
            HandleState(state.Name);
        }

        private void HandleState(string state)
        {
            if (!OrbRadiance.Settings.Enabled || !InNormalMode() || state == ExitState ||
                IsResetState(state))
            {
                StopShots();
                return;
            }
            if (state == EntryState)
            {
                // 从 Ascend Tele 正式进入新的 P3；防止重打复位后误沿用上一轮 Cast。
                awaitingFreshEntry = false;
                StopShots();
                return;
            }
            if (state == CastState && CanRun() && loop == null)
                loop = StartCoroutine(Loop());
        }

        private void Update()
        {
            if (!OrbRadiance.Settings.Enabled || !InNormalMode())
            {
                StopShots();
                return;
            }
            if (PollArenaReset()) return;
            if (control == null || control.Fsm == null) { StopShots(); return; }
            HandleState(control.Fsm.ActiveStateName);
            if (!CanRun()) StopShots();
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null || state.Name == lastArenaState) return;
            lastArenaState = state.Name;
            if (!IsResetState(state.Name)) return;
            awaitingFreshEntry = true;
            StopShots();
        }

        private bool PollArenaReset()
        {
            string state = ArenaState();
            if (state == null || state == lastArenaState) return false;
            lastArenaState = state;
            if (!IsResetState(state)) return false;
            awaitingFreshEntry = true;
            StopShots();
            return true;
        }

        private string ArenaState()
        {
            PlayMakerFSM parent = ResolveArena();
            return parent != null && parent.Fsm != null ? parent.Fsm.ActiveStateName : null;
        }

        private PlayMakerFSM ResolveArena()
        {
            if (arena != null && arena.Fsm != null) return arena;
            if (arenaTried) return null;
            arenaTried = true;
            Transform parent = transform.parent;
            if (parent != null)
            {
                foreach (PlayMakerFSM f in parent.GetComponents<PlayMakerFSM>())
                    if (f != null && f.Fsm != null && f.FsmName == "Control")
                    { arena = f; break; }
            }
            return arena;
        }

        private IEnumerator Loop()
        {
            // 先让出一帧，避免 StartCoroutine 返回之前已同步结束而写回悬空句柄。
            yield return null;
            OrbRadiance.Instance.Log("[p3tracking] NORMAL P3 启动：一束追踪，跟踪 0.3s / 静止 0.3s / 开火 0.2s / 间隔 0.3s");

            while (CanRun())
            {
                if (!SpawnBeam())
                {
                    yield return new WaitForSeconds(RetryInterval);
                    continue;
                }

                // 克隆自带 Control FSM：必须等 Init -> Inert 后才发送 ANTIC。
                float deadline = Time.time + StartTimeout;
                while (CanRun() && liveFsm != null && liveFsm.Fsm != null &&
                       (!liveFsm.Fsm.Started || liveFsm.Fsm.ActiveStateName == "Init") &&
                       Time.time < deadline)
                    yield return null;
                if (!CanRun()) break;

                if (liveFsm == null || liveFsm.Fsm == null || !liveFsm.Fsm.Started ||
                    liveFsm.Fsm.ActiveStateName != "Inert")
                {
                    if (!warnedFsmMissing)
                    {
                        warnedFsmMissing = true;
                        OrbRadiance.Instance.LogWarn("OrbRadiance: [p3tracking] 克隆激光未进入 Inert，跳过这一发");
                    }
                    DestroyBeam();
                    yield return new WaitForSeconds(RetryInterval);
                    continue;
                }

                // 每轮独立选择屏外发射点；仅前 0.3s 跟踪，随后 0.1s 完全冻结。
                AimAtHero();
                liveFsm.SendEvent("ANTIC");
                EyeBeamTrack.PlayP1ColumnSound(false);
                float endTracking = Time.time + TrackTime;
                while (CanRun() && liveFsm != null && Time.time < endTracking)
                {
                    AimAtHero();
                    yield return null;
                }
                if (!CanRun() || liveFsm == null) break;

                // 预瞄结束那一刻锁定位置/角度；冻结 0.1 秒内绝不再次 AimAtHero。
                AimAtHero();
                yield return new WaitForSeconds(HoldTime);
                if (!CanRun() || liveFsm == null) break;
                liveFsm.SendEvent("FIRE");
                EyeBeamTrack.PlayP1ColumnSound(true);
                yield return new WaitForSeconds(FireTime);
                if (!CanRun() || liveFsm == null) break;

                liveFsm.SendEvent("END");
                // END 淡出属于间隔的一部分；下一发预警前一定销毁旧克隆，不叠两束。
                yield return new WaitForSeconds(Interval);
                DestroyBeam();
            }

            DestroyBeam();
            loop = null;
        }

        private bool SpawnBeam()
        {
            DestroyBeam();
            if (template == null) template = transform.Find(TemplatePath);
            if (template == null)
            {
                if (!warnedTemplateMissing)
                {
                    warnedTemplateMissing = true;
                    OrbRadiance.Instance.LogWarn("OrbRadiance: [p3tracking] 找不到 " + TemplatePath);
                }
                return false;
            }

            try
            {
                // 与 HP2 的 HardBeamWall 相同：克隆挂在 Boss Control 下，避免继承辐光自身的位移。
                liveBeam = Instantiate(template.gameObject, transform.parent);
                if (liveBeam == null) return false;
                liveBeam.name = template.name + " (normal p3 tracking)";
                Vector3 scale = liveBeam.transform.localScale;
                liveBeam.transform.localScale = new Vector3(BeamLength / BeamUnitLen,
                    BeamWidth / BeamUnitThick, scale.z);
                liveFsm = liveBeam.GetComponents<PlayMakerFSM>()
                    .FirstOrDefault(f => f != null && f.Fsm != null && f.FsmName == BeamFsmName);
                if (liveFsm == null)
                {
                    if (!warnedFsmMissing)
                    {
                        warnedFsmMissing = true;
                        OrbRadiance.Instance.LogWarn("OrbRadiance: [p3tracking] 克隆缺少 Control FSM");
                    }
                    DestroyBeam();
                    return false;
                }
                // 模板处于 Inert（未预警、无碰撞）；摆到屏外后显式激活。
                // 和已实战使用的 HardBeamWall 保持相同的克隆初始化方式。
                side = UnityEngine.Random.Range(0, 4);
                edgeFraction = UnityEngine.Random.Range(0.15f, 0.85f);
                AimAtHero();
                liveBeam.SetActive(true);
                return true;
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: [p3tracking] 克隆激光失败 " + e);
                DestroyBeam();
                return false;
            }
        }

        private void AimAtHero()
        {
            HeroController hc = HeroController.instance;
            if (liveBeam == null || hc == null) return;
            Vector3 hero = hc.transform.position;
            ViewRect(hero, out float x0, out float x1, out float y0, out float y1);
            Vector3 origin;
            switch (side)
            {
                case 0: origin = new Vector3(x0 - OutMargin, Mathf.Lerp(y0, y1, edgeFraction), 0f); break;
                case 1: origin = new Vector3(x1 + OutMargin, Mathf.Lerp(y0, y1, edgeFraction), 0f); break;
                case 2: origin = new Vector3(Mathf.Lerp(x0, x1, edgeFraction), y1 + OutMargin, 0f); break;
                default: origin = new Vector3(Mathf.Lerp(x0, x1, edgeFraction), y0 - OutMargin, 0f); break;
            }
            Vector2 diff = new Vector2(hero.x - origin.x, hero.y - origin.y);
            liveBeam.transform.position = origin;
            liveBeam.transform.rotation = Quaternion.Euler(0f, 0f,
                Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg);
        }

        // HK 使用 tk2dCamera 自定义投影；通过视口角点反投影而不是 orthographicSize。
        // 相机暂不可用时使用 HP2 同尺寸的英雄中心兜底框。
        private static void ViewRect(Vector3 hero, out float x0, out float x1, out float y0, out float y1)
        {
            Camera cam = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
            if (cam != null && cam.pixelWidth > 0 && cam.pixelHeight > 0)
            {
                Rect rect = cam.pixelRect;
                float dz = -cam.transform.position.z;
                if (dz <= 0f) dz = 1f;
                Vector3 bottomLeft = cam.ScreenToWorldPoint(new Vector3(rect.xMin, rect.yMin, dz));
                Vector3 topRight = cam.ScreenToWorldPoint(new Vector3(rect.xMax, rect.yMax, dz));
                float w = topRight.x - bottomLeft.x, h = topRight.y - bottomLeft.y;
                if (w > 10f && w < 80f && h > 5f && h < 60f)
                {
                    x0 = bottomLeft.x; x1 = topRight.x;
                    y0 = bottomLeft.y; y1 = topRight.y;
                    return;
                }
            }
            x0 = hero.x - ViewHalfW; x1 = hero.x + ViewHalfW;
            y0 = hero.y - ViewHalfH; y1 = hero.y + ViewHalfH;
        }

        private void StopShots()
        {
            if (loop != null)
            {
                StopCoroutine(loop);
                loop = null;
            }
            DestroyBeam();
        }

        private void DestroyBeam()
        {
            if (liveBeam != null)
            {
                // 立即停止碰撞和显示，Destroy 的实际销毁发生在帧末。
                liveBeam.SetActive(false);
                Destroy(liveBeam);
            }
            liveBeam = null;
            liveFsm = null;
        }
    }
}

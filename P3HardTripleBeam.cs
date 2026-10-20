using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // HARD P3 从辐光脸部发射三道原版激光克隆，角度相对玩家偏转 1.5°～6°。
    // P3Laser 已抑制原版 ASCEND BEAM，玩家世界 Y≥115 时立即清理激光。


    internal sealed class P3HardTripleBeam : MonoBehaviour
    {
        private const string CastState = "Ascend Cast";
        private const string EntryState = "Ascend Tele";
        private const string ExitState = "Scream";
        private const string TemplatePath = "Eye Beam Glow/Ascend Beam";
        // EyeBeamTrack 也会使用原版 Radiant Beam 的位置瞄准脸激光。
        // 必须查找原版对象而非自身克隆，以保留游戏定义的脸部偏移。
        private static readonly string[] FaceBeamPaths =
        {
            "Eye Beam Glow/Burst 2/Radiant Beam",
            "Eye Beam Glow/Burst 1/Radiant Beam",
            "Eye Beam Glow/Burst 3/Radiant Beam"
        };
        private const string BeamFsmName = "Control";
        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };

        private const int BeamCount = 3;
        private const float WarningTime = 0.01f;
        // 原版 Ascend Beam 的 Fire Wait 为 0.4s，此处保持一致。
        private const float FireTime = 0.4f;
        // H 的重复间隔未指定，因此保留为可独立调节的参数。
        private const float Interval = 0.4f;
        private const float HeightLimit = 115f;
        private const float MinDeflection = 1.5f;
        private const float MaxDeflection = 6f;
        private const float SpawnTimeout = 1f;
        private const float RetryTime = 0.4f;

        
        private const float BeamLength = 400f;
        private const float BeamUnitLen = 1.765625f;
        private const float BeamWidth = 1f;
        private const float BeamUnitThick = 0.5030646f;

        private sealed class Shot
        {
            internal GameObject obj;
            internal PlayMakerFSM fsm;
        }

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private bool arenaTried;
        private bool subscribed;
        private bool arenaSubscribed;
        private bool awaitingFreshEntry;
        private string lastArenaState;
        private Transform template;
        private Transform faceOrigin;
        private Coroutine loop;
        private bool warnedMissingTemplate;
        private bool warnedMissingFaceOrigin;
        private bool warnedMissingFsm;
        private readonly List<Shot> live = new List<Shot>();
        private static P3HardTripleBeam active;

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

        private static bool IsResetState(string state) => Array.IndexOf(ResetStates, state) >= 0;

        private static bool BelowLimit()
        {
            HeroController hero = HeroController.instance;
            return hero != null && hero.transform.position.y < HeightLimit;
        }

        private bool CanRun()
        {
            return OrbRadiance.Settings.Enabled && OrbRadiance.EffectiveHardMode &&
                   !awaitingFreshEntry && control != null && control.Fsm != null &&
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
            if (state != null) HandleState(state.Name);
        }

        private void HandleState(string state)
        {
            if (!OrbRadiance.Settings.Enabled || !OrbRadiance.EffectiveHardMode ||
                state == ExitState || IsResetState(state))
            {
                StopShots();
                return;
            }
            if (state == EntryState)
            {
                awaitingFreshEntry = false;
                StopShots();
                return;
            }
            if (state == CastState && CanRun() && BelowLimit() && loop == null)
                loop = StartCoroutine(FireLoop());
        }

        private void Update()
        {
            if (!OrbRadiance.Settings.Enabled || !OrbRadiance.EffectiveHardMode)
            {
                StopShots();
                return;
            }
            if (PollArenaReset()) return;
            if (!CanRun() || !BelowLimit())
            {
                // 立即禁用碰撞体和渲染器，不等待本轮发射结束。
                StopShots();
                return;
            }
            if (loop == null) HandleState(CastState);
        }

        // 激光克隆挂在 Eye Beam Glow 之外，因为该对象在 P3 可能未激活；
        // 辐光移动时，出射点仍需跟随原版脸部位置。
        private void LateUpdate()
        {
            if (live.Count == 0 || !CanRun() || !BelowLimit() || faceOrigin == null) return;
            Vector3 muzzle = faceOrigin.position;
            foreach (Shot shot in live)
                if (shot.obj != null) shot.obj.transform.position = muzzle;
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
                foreach (PlayMakerFSM f in parent.GetComponents<PlayMakerFSM>())
                    if (f != null && f.Fsm != null && f.FsmName == "Control")
                    { arena = f; break; }
            return arena;
        }

        private IEnumerator FireLoop()
        {
            // 避免协程在 StartCoroutine 返回句柄前同步完成造成竞态。
            yield return null;
            OrbRadiance.Instance.Log("[p3hardbeam] H P3: three face-origin native beam clones, antic 0.01s, deflection +/-1.5~6 deg; stop above Y=115");

            while (CanRun() && BelowLimit())
            {
                if (!SpawnVolley())
                {
                    yield return new WaitForSeconds(RetryTime);
                    continue;
                }

                // 等待三道克隆 FSM 全部经过 Init 并进入 Inert。
                float deadline = Time.time + SpawnTimeout;
                while (CanRun() && BelowLimit() && Time.time < deadline &&
                       live.Any(s => s.fsm != null && s.fsm.Fsm != null &&
                           (!s.fsm.Fsm.Started || s.fsm.Fsm.ActiveStateName == "Init")))
                    yield return null;
                if (!CanRun() || !BelowLimit()) break;
                if (live.Count != BeamCount || live.Any(s => s.fsm == null || s.fsm.Fsm == null ||
                    !s.fsm.Fsm.Started || s.fsm.Fsm.ActiveStateName != "Inert"))
                {
                    if (!warnedMissingFsm)
                    {
                        warnedMissingFsm = true;
                        OrbRadiance.Instance.LogWarn("[p3hardbeam] cloned native lasers did not all enter Inert; skipping volley");
                    }
                    DestroyLive();
                    yield return new WaitForSeconds(RetryTime);
                    continue;
                }

                foreach (Shot shot in live) shot.fsm.SendEvent("ANTIC");
                EyeBeamTrack.PlayP1ColumnSound(false);
                yield return new WaitForSeconds(WarningTime);
                if (!CanRun() || !BelowLimit()) break;
                foreach (Shot shot in live) shot.fsm.SendEvent("FIRE");
                EyeBeamTrack.PlayP1ColumnSound(true);
                yield return new WaitForSeconds(FireTime);
                if (!CanRun() || !BelowLimit()) break;
                foreach (Shot shot in live)
                    if (shot.fsm != null) shot.fsm.SendEvent("END");
                yield return new WaitForSeconds(Interval);
                DestroyLive();
            }

            DestroyLive();
            loop = null;
        }

        private bool SpawnVolley()
        {
            DestroyLive();
            if (template == null) template = transform.Find(TemplatePath);
            if (template == null)
            {
                if (!warnedMissingTemplate)
                {
                    warnedMissingTemplate = true;
                    OrbRadiance.Instance.LogWarn("[p3hardbeam] missing original beam prefab " + TemplatePath);
                }
                return false;
            }
            HeroController hc = HeroController.instance;
            if (hc == null || !ResolveFaceOrigin()) return false;
            Vector3 hero = hc.transform.position;
            Vector3 origin = faceOrigin.position; 
            float baseAngle = Mathf.Atan2(hero.y - origin.y, hero.x - origin.x) * Mathf.Rad2Deg;
            // 三道光束分别偏向玩家两侧，偏差绝对值均在 1.5°～6° 之间。
            float[] angles =
            {
                -UnityEngine.Random.Range(4.5f, MaxDeflection),
                UnityEngine.Random.Range(0, 2) == 0
                    ? -UnityEngine.Random.Range(MinDeflection, 3f)
                    : UnityEngine.Random.Range(MinDeflection, 3f),
                UnityEngine.Random.Range(4.5f, MaxDeflection)
            };

            try
            {
                for (int i = 0; i < BeamCount; i++)
                {
                    GameObject beam = Instantiate(template.gameObject, transform.parent);
                    if (beam == null) throw new InvalidOperationException("Instantiate returned null");
                    beam.name = template.name + " (H P3 triple " + i + ")";
                    Vector3 oldScale = beam.transform.localScale;
                    beam.transform.localScale = new Vector3(BeamLength / BeamUnitLen,
                        BeamWidth / BeamUnitThick, oldScale.z);
                    beam.transform.position = origin;
                    beam.transform.rotation = Quaternion.Euler(0f, 0f, baseAngle + angles[i]);
                    var shot = new Shot
                    {
                        obj = beam,
                        fsm = beam.GetComponents<PlayMakerFSM>()
                            .FirstOrDefault(f => f != null && f.Fsm != null && f.FsmName == BeamFsmName)
                    };
                    live.Add(shot); // 先登记克隆，再执行可能抛异常的初始化，以便清理不完整对象。
                    if (shot.fsm == null) throw new InvalidOperationException("native beam Control FSM missing");
                    beam.SetActive(true);
                }
                return true;
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogWarn("[p3hardbeam] failed to spawn volley: " + e.Message);
                DestroyLive();
                return false;
            }
        }

        private bool ResolveFaceOrigin()
        {
            if (faceOrigin != null) return true;
            foreach (string path in FaceBeamPaths)
            {
                Transform candidate = transform.Find(path);
                if (candidate == null) continue;
                faceOrigin = candidate;
                return true;
            }
            if (!warnedMissingFaceOrigin)
            {
                warnedMissingFaceOrigin = true;
                OrbRadiance.Instance.LogWarn("[p3hardbeam] native face-beam muzzle missing; skipping volley (not falling back to offscreen)");
            }
            return false;
        }

        private void StopShots()
        {
            if (loop != null)
            {
                StopCoroutine(loop);
                loop = null;
            }
            DestroyLive();
        }

        private void DestroyLive()
        {
            foreach (Shot shot in live)
            {
                if (shot.obj == null) continue;
                shot.obj.SetActive(false); // 在 Unity 延迟销毁对象前先关闭碰撞体。
                Destroy(shot.obj);
            }
            live.Clear();
        }
    }
}

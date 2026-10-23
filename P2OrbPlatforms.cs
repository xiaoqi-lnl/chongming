using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // H P2 每次 Orbs 招式尝试生成一对私有旋转平台与平台地刺；已有未到期平台时不新增也不续时。
    internal sealed class P2OrbPlatforms : MonoBehaviour
    {
        private const string StateP2 = "Arena 2 Start";
        private const string StateP3 = "Ascend Tele";
        private const string StateP4 = "Scream";
        private const string ResetA = "Battle Start";
        private const string ResetB = "Refight";
        private const string ResetC = "Init";
        private const string ResetD = "Setup";
        private const string HolderName = "H P2 Orb Attack Platforms";
        private const string BossControlName = "Boss Control";
        private const string PlatformPath = "Plat Sets/P2 SetA/Radiant Plat Small (3)";
        private const string SpikePath = "Spike Control/Far L/Radiant Spike";
        private const float Lifetime = 5f;
        private const float OrbitSpeed = 37.5f;
        private const float CenterPeriod = 20f;
        private const float RadiusPeriod = 20f / 1.5f;
        private const float BaseRadius = 7.92f;
        private const float RadiusAmplitude = 3.96f;
        private const float SpikeDownSeconds = 0.5f;
        private const float SpikeAnticSeconds = 0.5f;
        private const float PreVanishSeconds = 0.01f;
        private const int SpikeCount = 6;
        private const int SpikeDamage = 2;
        private const int SpikeHazardType = 0;
        private static readonly string[] ResetStates = { ResetA, ResetB, ResetC, ResetD };
        private static readonly HashSet<P2OrbPlatforms> ActiveInstances = new HashSet<P2OrbPlatforms>();

        private sealed class PlatformInstance
        {
            internal GameObject gameObject;
            internal PlayMakerFSM fsm;
            internal float initialAngle;
            internal float z;
            internal bool vanishSent;
            internal bool spikesSealed;
            internal Action<FsmState> stateHandler;
            internal HOrbPlatformSpike[] spikes;
            internal Renderer[] renderers;
        }

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private HealthManager health;
        private bool p2Started;
        private bool controlSubscribed;
        private bool arenaSubscribed;
        private bool spikeCycleActive;
        private bool clearing;
        private float birthTime;
        private float expiryTime;
        private float vanishAt;
        private float appearDuration;
        private float disappearDuration;
        private float animationFrameBudget;
        private Transform detector;
        private GameObject pairRoot;
        private readonly List<PlatformInstance> platforms = new List<PlatformInstance>();
        private GameObject platformTemplate;
        private GameObject spikeTemplate;
        private Transform bossControl;
        private bool templatesResolved;

        internal void Init(PlayMakerFSM fsm)
        {
            ActiveInstances.Add(this);
            control = fsm;
            health = fsm != null ? fsm.GetComponent<HealthManager>() : null;
            Subscribe();
            HandleControlState(control != null && control.Fsm != null ? control.Fsm.ActiveStateName : null);
        }

        private void OnEnable() { Subscribe(); }

        private void OnDisable()
        {
            Unsubscribe();
            p2Started = false;
            ClearPair();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            p2Started = false;
            ClearPair();
            ActiveInstances.Remove(this);
        }

        internal static void RestoreAll()
        {
            foreach (P2OrbPlatforms instance in ActiveInstances.ToArray())
            {
                if (instance == null) continue;
                instance.p2Started = false;
                instance.ClearPair();
            }
        }

        private void Subscribe()
        {
            if (!controlSubscribed && control != null && control.Fsm != null)
            {
                control.Fsm.StateChanged += OnControlStateChanged;
                controlSubscribed = true;
            }

            if (arenaSubscribed || control == null || control.transform.parent == null) return;
            foreach (PlayMakerFSM fsm in control.transform.parent.GetComponents<PlayMakerFSM>())
            {
                if (fsm == null || fsm.Fsm == null || fsm.FsmName != "Control") continue;
                arena = fsm;
                arena.Fsm.StateChanged += OnArenaStateChanged;
                arenaSubscribed = true;
                break;
            }
        }

        private void Unsubscribe()
        {
            if (controlSubscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnControlStateChanged;
            controlSubscribed = false;
            if (arenaSubscribed && arena != null && arena.Fsm != null)
                arena.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
        }

        private void OnControlStateChanged(FsmState state)
        {
            if (state != null) HandleControlState(state.Name);
        }

        private void HandleControlState(string state)
        {
            if (state == StateP2)
            {
                p2Started = true;
                return;
            }
            if (state == StateP3 || state == StateP4 || Array.IndexOf(ResetStates, state) >= 0)
            {
                p2Started = false;
                ClearPair();
            }
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null || Array.IndexOf(ResetStates, state.Name) < 0) return;
            p2Started = false;
            ClearPair();
        }

        private void Update()
        {
            if (!IsActiveP2())
            {
                if (HeroIsDead() || (health != null && health.hp <= BossHP.AscendHP_H))
                    p2Started = false;
                ClearPair();
                return;
            }
            if (pairRoot == null) return;

            float age = Time.time - birthTime;
            UpdatePlatformPositions(age);
            if (Time.time >= expiryTime)
            {
                ClearPair();
                return;
            }

            foreach (PlatformInstance platform in platforms)
            {
                if (platform.vanishSent || platform.fsm == null || platform.fsm.Fsm == null) continue;
                string state = platform.fsm.Fsm.ActiveStateName;
                if (state != "Appear 2" && state != "Idle") continue;
                if (Time.time < vanishAt) continue;
                platform.vanishSent = true;
                platform.fsm.SendEvent("SLOW VANISH");
            }
        }

        internal static void TrySpawnAtOrbAttack(GameObject owner)
        {
            if (owner == null) return;
            P2OrbPlatforms driver = owner.GetComponent<P2OrbPlatforms>();
            if (driver != null) driver.TrySpawnPair();
        }

        private bool IsActiveP2()
        {
            if (!OrbRadiance.Settings.Enabled || !OrbRadiance.EffectiveHardMode || !p2Started ||
                control == null || control.Fsm == null || control.gameObject.scene.name != "GG_Radiance" ||
                control.Fsm.ActiveStateName == StateP3 || control.Fsm.ActiveStateName == StateP4 ||
                (health != null && health.hp <= BossHP.AscendHP_H) || HeroIsDead())
                return false;
            return true;
        }

        private static bool HeroIsDead()
        {
            HeroController hero = HeroController.instance;
            return hero != null && hero.cState.dead;
        }

        private void TrySpawnPair()
        {
            if (!IsActiveP2()) return;
            if (pairRoot != null)
            {
                if (Time.time < expiryTime) return;
                ClearPair();
            }
            try
            {
                if (!ResolveTemplates())
                {
                    OrbRadiance.Instance.LogWarn("[p2-orb-platforms] H P2 orb platform spawn skipped: exact platform/spike template path unavailable");
                    return;
                }

                float appear;
                float disappear;
                float frameBudget;
                if (!TryReadAnimationDuration(platformTemplate, "Anim Appear", "Anim Disappear", out appear, out disappear, out frameBudget))
                {
                    OrbRadiance.Instance.LogWarn("[p2-orb-platforms] H P2 orb platform spawn skipped: private platform animation duration unavailable");
                    return;
                }

                float required = appear + 2f + PreVanishSeconds + disappear + frameBudget;
                if (required > Lifetime)
                {
                    OrbRadiance.Instance.LogWarn("[p2-orb-platforms] H P2 orb platform spawn skipped: appear/flash/disappear exceeds 5s budget (A=" + appear.ToString("F3") + ", D=" + disappear.ToString("F3") + ")");
                    return;
                }

                Transform detectorTransform = ResolveDetector();
                if (detectorTransform == null)
                {
                    OrbRadiance.Instance.LogWarn("[p2-orb-platforms] H P2 orb platform spawn skipped: Phase2 Detector not found");
                    return;
                }

                appearDuration = appear;
                disappearDuration = disappear;
                animationFrameBudget = frameBudget;
                pairRoot = new GameObject(HolderName);
                pairRoot.transform.SetParent(bossControl, false);
                pairRoot.transform.localPosition = Vector3.zero;
                pairRoot.transform.localRotation = Quaternion.identity;
                pairRoot.transform.localScale = Vector3.one;
                pairRoot.SetActive(false);
                detector = detectorTransform;
                BuildPlatform(22.5f);
                BuildPlatform(202.5f);
                if (appearDuration + 2f + PreVanishSeconds + disappearDuration + animationFrameBudget > Lifetime)
                    throw new InvalidOperationException("private animation timing exceeds the 5s lifetime budget");
                birthTime = Time.time;
                expiryTime = birthTime + Lifetime;
                vanishAt = expiryTime - disappearDuration - 2f - PreVanishSeconds - animationFrameBudget;
                UpdatePlatformPositions(0f);
                pairRoot.SetActive(true);
                StartCoroutine(RevealAndExpire());
                OrbRadiance.Instance.Log("[p2-orb-platforms] H P2 Orbs pair created; lifetime=5s, appear=" + appearDuration.ToString("F3") + "s, flash=2s, disappear=" + disappearDuration.ToString("F3") + "s; vanishAt=" + (vanishAt - birthTime).ToString("F3") + "s");
            }
            catch (Exception e)
            {
                ClearPair();
                OrbRadiance.Instance.LogWarn("[p2-orb-platforms] H P2 orb platform spawn aborted safely: " + e.Message);
            }
        }

        private bool ResolveTemplates()
        {
            if (templatesResolved) return platformTemplate != null && spikeTemplate != null;
            if (control == null || control.gameObject.scene.name != "GG_Radiance") return false;
            GameObject[] roots = control.gameObject.scene.GetRootGameObjects();
            Transform root = FindRoot(roots, BossControlName);
            if (root == null) return false;
            bossControl = root;
            Transform platform = root.Find(PlatformPath);
            Transform spike = root.Find(SpikePath);
            Transform phaseDetector = root.Find("Phase2 Detector");
            if (platform == null || spike == null || phaseDetector == null) return false;
            platformTemplate = platform.gameObject;
            spikeTemplate = spike.gameObject;
            detector = phaseDetector;
            templatesResolved = true;
            return true;
        }

        private Transform ResolveDetector()
        {
            if (detector != null) return detector;
            if (!ResolveTemplates()) return null;
            return detector;
        }

        private static Transform FindRoot(GameObject[] roots, string name)
        {
            foreach (GameObject root in roots)
                if (root != null && root.name == name) return root.transform;
            return null;
        }

        private void BuildPlatform(float angle)
        {
            GameObject clone = Instantiate(platformTemplate, pairRoot.transform, false);
            clone.name = HolderName + " " + angle.ToString("0") + "deg";
            clone.SetActive(false);
            PlayMakerFSM[] fsms = clone.GetComponentsInChildren<PlayMakerFSM>(true);
            foreach (PlayMakerFSM fsm in fsms)
                if (fsm != null && !HiddenRoomDoor.PreparePrivateFsm(fsm))
                    throw new InvalidOperationException("platform FSM could not be detached: " + fsm.FsmName);

            PlayMakerFSM platformFsm = fsms.FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "radiant_plat");
            if (platformFsm == null || platformFsm.UsesTemplate) throw new InvalidOperationException("private radiant_plat FSM missing");
            if (!SetPreVanishWait(platformFsm.Fsm)) throw new InvalidOperationException("Pre Vanish WaitRandom missing");
            if (!SetPlatformColliderReference(platformFsm, clone)) throw new InvalidOperationException("private Colliders variable unresolved");
            if (!TryConfigureAnimation(clone, platformFsm, angle)) throw new InvalidOperationException("platform clip or FSM animation references invalid");

            PlatformInstance instance = new PlatformInstance
            {
                gameObject = clone,
                fsm = platformFsm,
                initialAngle = angle,
                z = platformTemplate.transform.position.z
            };
            platforms.Add(instance);
            instance.stateHandler = state =>
            {
                if (state != null && (state.Name == "Disappear" || state.Name == "Disappeared"))
                    SealPlatformSpikes(instance);
            };
            platformFsm.Fsm.StateChanged += instance.stateHandler;
            BuildSpikes(instance);
            clone.SetActive(true);
        }

        private bool TryConfigureAnimation(GameObject clone, PlayMakerFSM fsm, float angle)
        {
            tk2dSpriteAnimator animator = clone.GetComponentInChildren<tk2dSpriteAnimator>(true);
            if (animator == null || animator.Library == null) return false;
            FsmString appearName = fsm.FsmVariables.GetFsmString("Anim Appear");
            FsmString disappearName = fsm.FsmVariables.GetFsmString("Anim Disappear");
            if (appearName == null || disappearName == null) return false;
            tk2dSpriteAnimationClip appearClip = animator.Library.GetClipByName(appearName.Value);
            tk2dSpriteAnimationClip disappearClip = animator.Library.GetClipByName(disappearName.Value);
            if (appearClip == null || disappearClip == null || appearClip.fps <= 0f || disappearClip.fps <= 0f ||
                appearClip.frames == null || disappearClip.frames == null || appearClip.frames.Length == 0 || disappearClip.frames.Length == 0)
                return false;
            float measuredAppear = appearClip.frames.Length / appearClip.fps;
            float measuredDisappear = disappearClip.frames.Length / disappearClip.fps;
            if (Mathf.Abs(measuredAppear - appearDuration) > 0.01f || Mathf.Abs(measuredDisappear - disappearDuration) > 0.01f)
            {
                appearDuration = measuredAppear;
                disappearDuration = measuredDisappear;
                animationFrameBudget = Mathf.Max(1f / appearClip.fps, 1f / disappearClip.fps);
                if (appearDuration + 2f + PreVanishSeconds + disappearDuration + animationFrameBudget > Lifetime) return false;
            }
            OrbRadiance.Instance.Log("[p2-orb-platforms] platform " + angle.ToString("0") + "deg clips: appear=" + appearName.Value + " (" + appearClip.frames.Length + "f @" + appearClip.fps.ToString("0.##") + "fps = " + appearDuration.ToString("F3") + "s), disappear=" + disappearName.Value + " (" + disappearClip.frames.Length + "f @" + disappearClip.fps.ToString("0.##") + "fps = " + disappearDuration.ToString("F3") + "s)");
            return true;
        }

        private static bool TryReadAnimationDuration(GameObject platform, string appearVariable,
                                                     string disappearVariable, out float appear,
                                                     out float disappear, out float frameBudget)
        {
            appear = 0f;
            disappear = 0f;
            frameBudget = 0f;
            PlayMakerFSM fsm = platform.GetComponentsInChildren<PlayMakerFSM>(true)
                .FirstOrDefault(component => component != null && component.Fsm != null && component.FsmName == "radiant_plat");
            tk2dSpriteAnimator animator = platform.GetComponentInChildren<tk2dSpriteAnimator>(true);
            if (fsm == null || animator == null || animator.Library == null) return false;
            FsmString appearName = fsm.FsmVariables.GetFsmString(appearVariable);
            FsmString disappearName = fsm.FsmVariables.GetFsmString(disappearVariable);
            if (appearName == null || disappearName == null) return false;
            tk2dSpriteAnimationClip appearClip = animator.Library.GetClipByName(appearName.Value);
            tk2dSpriteAnimationClip disappearClip = animator.Library.GetClipByName(disappearName.Value);
            if (appearClip == null || disappearClip == null || appearClip.frames == null || disappearClip.frames == null ||
                appearClip.frames.Length == 0 || disappearClip.frames.Length == 0 || appearClip.fps <= 0f || disappearClip.fps <= 0f)
                return false;
            appear = appearClip.frames.Length / appearClip.fps;
            disappear = disappearClip.frames.Length / disappearClip.fps;
            frameBudget = Mathf.Max(1f / appearClip.fps, 1f / disappearClip.fps);
            return appear > 0f && disappear > 0f;
        }

        private static bool SetPreVanishWait(Fsm fsm)
        {
            FsmState preVanish = fsm.GetState("Pre Vanish");
            WaitRandom wait = preVanish == null ? null : preVanish.Actions.OfType<WaitRandom>().FirstOrDefault();
            if (wait == null || wait.timeMin == null || wait.timeMax == null) return false;
            wait.timeMin.Value = PreVanishSeconds;
            wait.timeMax.Value = PreVanishSeconds;
            return true;
        }

        private static bool SetPlatformColliderReference(PlayMakerFSM fsm, GameObject clone)
        {
            FsmGameObject colliders = fsm.FsmVariables.GetFsmGameObject("Colliders");
            Transform colliderRoot = clone.transform.Find("Colliders");
            if (colliders == null || colliderRoot == null) return false;
            colliders.Value = colliderRoot.gameObject;
            return true;
        }

        private void BuildSpikes(PlatformInstance platform)
        {
            Transform colliderRoot = platform.gameObject.transform.Find("Colliders");
            BoxCollider2D platformBox = colliderRoot == null ? null : colliderRoot.GetComponentsInChildren<BoxCollider2D>(true)
                .FirstOrDefault(collider => collider != null && !collider.isTrigger);
            if (platformBox == null || !P2PlatformSpikes.TryGetBoxBounds(platformBox, out P2PlatformSpikes.Bounds2 bounds))
                throw new InvalidOperationException("private platform solid BoxCollider2D bounds unavailable");
            PolygonCollider2D spikePolygon = spikeTemplate.GetComponent<PolygonCollider2D>();
            if (spikePolygon == null || spikePolygon.points == null || spikePolygon.points.Length == 0)
                throw new InvalidOperationException("spike template root PolygonCollider2D missing");
            float spikeMinY = float.PositiveInfinity;
            foreach (Vector2 point in spikePolygon.points)
            {
                Vector3 world = spikePolygon.transform.TransformPoint(new Vector3(point.x + spikePolygon.offset.x,
                                                                                  point.y + spikePolygon.offset.y, 0f));
                spikeMinY = Mathf.Min(spikeMinY, world.y);
            }
            float spikeBottomOffset = spikeTemplate.transform.position.y - spikeMinY;
            if (spikeBottomOffset <= 0f) throw new InvalidOperationException("spike template bottom offset invalid");
            platform.spikes = new HOrbPlatformSpike[SpikeCount];
            for (int i = 0; i < SpikeCount; i++)
            {
                GameObject spike = Instantiate(spikeTemplate, platform.gameObject.transform, false);
                spike.name = "H P2 Orb Platform Spike " + (i + 1);
                spike.SetActive(false);
                spike.transform.localScale = spikeTemplate.transform.localScale;
                spike.transform.localRotation = Quaternion.identity;
                float x = bounds.minX + (bounds.maxX - bounds.minX) * (i + 0.5f) / SpikeCount;
                Vector3 worldPosition = new Vector3(x, bounds.maxY + spikeBottomOffset, spikeTemplate.transform.position.z);
                spike.transform.position = worldPosition;

                PlayMakerFSM[] fsms = spike.GetComponentsInChildren<PlayMakerFSM>(true);
                foreach (PlayMakerFSM fsm in fsms)
                    if (fsm != null && !HiddenRoomDoor.PreparePrivateFsm(fsm))
                        throw new InvalidOperationException("spike FSM could not be detached: " + fsm.FsmName);
                PlayMakerFSM controlFsm = fsms.FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Control");
                if (controlFsm == null || controlFsm.UsesTemplate) throw new InvalidOperationException("private spike Control FSM missing");
                FsmGameObject anticVariable = controlFsm.FsmVariables.GetFsmGameObject("Floor Antic");
                Transform antic = spike.transform.Find("Floor Antic");
                if (anticVariable == null || antic == null) throw new InvalidOperationException("private spike Floor Antic reference missing");
                anticVariable.Value = antic.gameObject;
                if (!SetSpikeAnticWait(controlFsm.Fsm)) throw new InvalidOperationException("private Floor Antic Wait missing");

                PlayMakerFSM saver = fsms.FirstOrDefault(fsm => fsm != null && fsm.FsmName == "Hero Saver");
                if (saver != null)
                {
                    saver.enabled = false;
                    Destroy(saver);
                }
                DamageHero damage = spike.GetComponent<DamageHero>();
                if (damage == null) damage = spike.GetComponentInChildren<DamageHero>(true);
                if (damage == null) throw new InvalidOperationException("spike DamageHero missing");
                damage.damageDealt = SpikeDamage;
                damage.hazardType = SpikeHazardType;
                damage.resetOnEnable = false;
                damage.enabled = false;
                HOrbPlatformSpike marker = spike.AddComponent<HOrbPlatformSpike>();
                marker.Init(this, controlFsm, platform.gameObject, damage);

                PolygonCollider2D damageCollider = spike.GetComponent<PolygonCollider2D>();
                if (damageCollider == null) throw new InvalidOperationException("spike root PolygonCollider2D missing");
                damageCollider.enabled = false;
                foreach (Renderer renderer in spike.GetComponentsInChildren<Renderer>(true))
                    if (renderer != null) renderer.enabled = false;
                spike.SetActive(true);
                platform.spikes[i] = marker;
            }
            platform.renderers = platform.gameObject.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer != null && renderer.GetComponentInParent<HOrbPlatformSpike>() == null)
                .ToArray();
            foreach (Renderer renderer in platform.renderers)
                if (renderer != null) BluePhaseTint.RegisterTemporaryTerrain(renderer);
        }

        private static bool SetSpikeAnticWait(Fsm fsm)
        {
            FsmState antic = fsm.GetState("Floor Antic");
            Wait wait = antic == null ? null : antic.Actions.OfType<Wait>().FirstOrDefault(a => a.time != null && a.finishEvent != null && a.finishEvent.Name == "FINISHED");
            if (wait == null) return false;
            wait.time.Value = SpikeAnticSeconds;
            return true;
        }

        private IEnumerator RevealAndExpire()
        {
            float readyDeadline = Mathf.Min(expiryTime, birthTime + 1.5f);
            while (Time.time < readyDeadline && pairRoot != null && IsActiveP2())
            {
                bool ready = true;
                foreach (PlatformInstance platform in platforms)
                    if (platform.fsm == null || platform.fsm.Fsm == null || !platform.fsm.Fsm.Started || platform.fsm.Fsm.ActiveStateName != "Out")
                    { ready = false; break; }
                if (ready) break;
                yield return null;
            }
            if (pairRoot == null || !IsActiveP2()) { ClearPair(); yield break; }
            foreach (PlatformInstance platform in platforms)
            {
                if (platform.fsm == null || platform.fsm.Fsm == null || !platform.fsm.Fsm.Started || platform.fsm.Fsm.ActiveStateName != "Out")
                {
                    OrbRadiance.Instance.LogWarn("[p2-orb-platforms] private radiant_plat did not reach Out before appearance deadline; pair removed");
                    ClearPair();
                    yield break;
                }
                platform.fsm.SendEvent("APPEAR");
            }
            for (int i = 0; i < platforms.Count; i++)
                StartCoroutine(PrepareSpikeRise(platforms[i]));
        }

        private IEnumerator PrepareSpikeRise(PlatformInstance platform)
        {
            float deadline = Mathf.Min(expiryTime, birthTime + 2f);
            while (Time.time < deadline && pairRoot != null && !clearing && !platform.spikesSealed)
            {
                bool appeared = platform.fsm != null && platform.fsm.Fsm != null && platform.fsm.Fsm.ActiveStateName == "Appear 2";
                if (appeared) break;
                yield return null;
            }
            if (pairRoot == null || clearing || platform.spikesSealed) yield break;
            if (platform.fsm == null || platform.fsm.Fsm == null || platform.fsm.Fsm.ActiveStateName != "Appear 2") yield break;
            float downedDeadline = Mathf.Min(expiryTime, birthTime + 1.5f);
            while (Time.time < downedDeadline && pairRoot != null && !clearing && !platform.spikesSealed && !AllSpikesDowned(platform))
                yield return null;
            if (pairRoot == null || clearing || platform.spikesSealed || !AllSpikesDowned(platform))
            {
                OrbRadiance.Instance.LogWarn("[p2-orb-platforms] private spike FSMs did not reach Downed; collision remains disabled");
                yield break;
            }
            SetPlatformSpikes(platform, false);
            foreach (HOrbPlatformSpike spike in platform.spikes)
            {
                if (spike == null || !spike.SendInitialRise())
                {
                    OrbRadiance.Instance.LogWarn("[p2-orb-platforms] initial private spike rise failed; pair removed safely");
                    ClearPair();
                    yield break;
                }
            }
            yield return WaitForPlatformSpikesUp(platform);
            if (pairRoot != null && Time.time < expiryTime && !clearing && !platform.spikesSealed && IsActiveP2())
                SetPlatformSpikes(platform, true);
        }

        private static bool AllSpikesDowned(PlatformInstance platform)
        {
            if (platform.spikes == null || platform.spikes.Length != SpikeCount) return false;
            foreach (HOrbPlatformSpike spike in platform.spikes)
                if (spike == null || !spike.IsDowned) return false;
            return true;
        }

        private void UpdatePlatformPositions(float age)
        {
            if (detector == null) return;
            float centerPhase = age * Mathf.PI * 2f / CenterPeriod;
            Vector2 center = (Vector2)detector.position + new Vector2(0f, 7f) +
                             new Vector2(2f * Mathf.Sin(centerPhase), 1.5f * Mathf.Cos(centerPhase));
            float radius = BaseRadius + RadiusAmplitude * Mathf.Sin(age * Mathf.PI * 2f / RadiusPeriod);
            foreach (PlatformInstance platform in platforms)
            {
                if (platform.gameObject == null) continue;
                float angle = (platform.initialAngle + OrbitSpeed * age) * Mathf.Deg2Rad;
                Vector2 position = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                platform.gameObject.transform.position = new Vector3(position.x, position.y, platform.z);
                platform.gameObject.transform.rotation = Quaternion.identity;
            }
        }

        internal void ConsumeSpikePair()
        {
            if (spikeCycleActive || pairRoot == null || clearing) return;
            spikeCycleActive = true;
            StartCoroutine(ConsumeAndRegrowSpikes());
        }

        private IEnumerator ConsumeAndRegrowSpikes()
        {
            float downDeadline = Time.time + SpikeDownSeconds;
            foreach (PlatformInstance platform in platforms)
            {
                foreach (HOrbPlatformSpike spike in platform.spikes ?? new HOrbPlatformSpike[0])
                {
                    if (spike == null) continue;
                    spike.SetCollisionEnabled(false);
                    spike.SendDown();
                }
            }
            while (pairRoot != null && !clearing && Time.time < expiryTime && !AllLiveSpikesDowned())
                yield return null;
            while (pairRoot != null && !clearing && Time.time < expiryTime && Time.time < downDeadline)
                yield return null;
            if (pairRoot == null || clearing || Time.time >= expiryTime) yield break;
            foreach (PlatformInstance platform in platforms)
                foreach (HOrbPlatformSpike spike in platform.spikes ?? new HOrbPlatformSpike[0])
                    if (spike != null && !spike.IsSealed) spike.SendRise();
            foreach (PlatformInstance platform in platforms)
                yield return WaitForPlatformSpikesUp(platform);
            if (pairRoot == null || clearing || Time.time >= expiryTime || !IsActiveP2()) yield break;
            foreach (PlatformInstance platform in platforms)
                if (!platform.spikesSealed) SetPlatformSpikes(platform, true);
            spikeCycleActive = false;
        }

        private bool AllLiveSpikesDowned()
        {
            foreach (PlatformInstance platform in platforms)
            {
                foreach (HOrbPlatformSpike spike in platform.spikes ?? new HOrbPlatformSpike[0])
                    if (spike != null && !spike.IsSealed && !spike.IsDowned) return false;
            }
            return true;
        }

        private IEnumerator WaitForPlatformSpikesUp(PlatformInstance platform)
        {
            while (pairRoot != null && !clearing && Time.time < expiryTime && !platform.spikesSealed)
            {
                bool up = true;
                foreach (HOrbPlatformSpike spike in platform.spikes ?? new HOrbPlatformSpike[0])
                    if (spike != null && !spike.IsUp) { up = false; break; }
                if (up) yield break;
                yield return null;
            }
        }

        private void SetPlatformSpikes(PlatformInstance platform, bool enabled)
        {
            foreach (HOrbPlatformSpike spike in platform.spikes ?? new HOrbPlatformSpike[0])
            {
                if (spike == null || spike.IsSealed) continue;
                spike.SetCollisionEnabled(enabled);
            }
        }

        private void SealPlatformSpikes(PlatformInstance platform)
        {
            if (platform.spikesSealed) return;
            platform.spikesSealed = true;
            foreach (HOrbPlatformSpike spike in platform.spikes ?? new HOrbPlatformSpike[0])
                if (spike != null) spike.Seal();
        }

        private void ClearPair()
        {
            if (pairRoot == null) return;
            clearing = true;
            StopAllCoroutines();
            foreach (PlatformInstance platform in platforms)
            {
                if (platform.fsm != null && platform.fsm.Fsm != null && platform.stateHandler != null)
                    platform.fsm.Fsm.StateChanged -= platform.stateHandler;
                if (platform.renderers != null)
                    foreach (Renderer renderer in platform.renderers)
                        if (renderer != null) BluePhaseTint.UnregisterTemporaryTerrain(renderer);
                if (platform.gameObject != null)
                {
                    platform.gameObject.SetActive(false);
                    Destroy(platform.gameObject);
                }
            }
            platforms.Clear();
            pairRoot.SetActive(false);
            Destroy(pairRoot);
            pairRoot = null;
            spikeCycleActive = false;
            clearing = false;
        }
    }

    internal sealed class HOrbPlatformSpike : MonoBehaviour
    {
        private P2OrbPlatforms owner;
        private PlayMakerFSM control;
        private GameObject platform;
        private PolygonCollider2D damageCollider;
        private DamageHero damage;
        private bool sealedSpike;

        internal bool IsSealed { get { return sealedSpike; } }
        internal bool IsUp { get { return control != null && control.Fsm != null && control.Fsm.Started && control.Fsm.ActiveStateName == "Up"; } }
        internal bool IsDowned { get { return control != null && control.Fsm != null && control.Fsm.Started && control.Fsm.ActiveStateName == "Downed"; } }

        internal void Init(P2OrbPlatforms driver, PlayMakerFSM fsm, GameObject platformObject, DamageHero damageHero)
        {
            owner = driver;
            control = fsm;
            platform = platformObject;
            damage = damageHero;
            damageCollider = GetComponent<PolygonCollider2D>();
            if (damageCollider == null) throw new InvalidOperationException("spike root PolygonCollider2D missing");
        }

        private void OnTriggerEnter2D(Collider2D other) { CheckHero(other); }
        private void OnTriggerStay2D(Collider2D other) { CheckHero(other); }
        private void OnCollisionEnter2D(Collision2D collision) { CheckHero(collision.collider); }
        private void OnCollisionStay2D(Collision2D collision) { CheckHero(collision.collider); }

        private void CheckHero(Collider2D other)
        {
            if (sealedSpike || !IsUp || damageCollider == null || !damageCollider.enabled || damage == null || !damage.enabled ||
                owner == null || platform == null || other == null ||
                other.name != "HeroBox" || other.transform.root.name != "Knight") return;
            HeroController hero = other.GetComponentInParent<HeroController>();
            if (hero == null || hero.cState == null || !hero.cState.invulnerable) return;
            owner.ConsumeSpikePair();
        }

        internal void SetCollisionEnabled(bool enabled)
        {
            if (sealedSpike && enabled) return;
            if (damageCollider != null) damageCollider.enabled = enabled;
            if (damage != null) damage.enabled = enabled;
        }

        internal void SendDown()
        {
            if (!sealedSpike && control != null && control.Fsm != null && control.Fsm.Started)
                control.SendEvent("SPIKES DOWN");
        }

        internal void SendRise()
        {
            if (!sealedSpike && control != null && control.Fsm != null && control.Fsm.Started)
                control.SendEvent("UP");
        }

        internal bool SendInitialRise()
        {
            if (sealedSpike || !IsDowned) return false;
            Transform floorAntic = transform.Find("Floor Antic");
            if (floorAntic == null) return false;
            MeshRenderer floorAnticRenderer = floorAntic.GetComponentInChildren<MeshRenderer>(true);
            if (floorAnticRenderer == null) return false;
            floorAnticRenderer.enabled = true;
            floorAntic.gameObject.SetActive(true);
            control.SetState("Spike Up");
            return control.Fsm.ActiveStateName == "Spike Up" || control.Fsm.ActiveStateName == "Up";
        }

        internal void Seal()
        {
            sealedSpike = true;
            SetCollisionEnabled(false);
            if (control != null && control.Fsm != null && control.Fsm.Started)
                control.SendEvent("SPIKES DOWN");
        }
    }
}

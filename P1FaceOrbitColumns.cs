using System.Collections;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // Three native Ascend Beam columns paired with the N/H P1 orbit ring.
    internal sealed class P1FaceOrbitColumns : MonoBehaviour
    {
        private const float WarningTime = 0.5f;
        private const float FireTime = 0.15f;
        private const float StartSpacing = 0.75f;
        private const float TotalTime = 2.15f;
        private const float BeamLength = 400f;
        private const float BeamWidth = 1f;
        private const float UnitLength = 1.765625f;
        private const float UnitWidth = 0.5030646f;
        private const string InitState = "Init";
        private const string InertState = "Inert";

        private Transform boss;
        private GameObject beamObject;
        private GameObject stagingRoot;
        private PlayMakerFSM beamFsm;
        private Coroutine running;
        private bool runActive;
        private int generation;
        private float attackStartedAt;
        private bool scheduleStarted;
        internal bool IsRunning => runActive;
        internal bool ScheduleStarted => scheduleStarted;
        internal float ScheduleStartTime => attackStartedAt;

        internal static void Begin(P1NailFan owner, Transform bossTransform)
        {
            if (owner == null || bossTransform == null) return;
            P1FaceOrbitColumns columns = owner.GetComponent<P1FaceOrbitColumns>();
            if (columns == null) columns = owner.gameObject.AddComponent<P1FaceOrbitColumns>();
            columns.BeginAttack(bossTransform);
        }

        internal static void Prepare(P1NailFan owner, Transform bossTransform)
        {
            if (owner == null || bossTransform == null) return;
            P1FaceOrbitColumns columns = owner.GetComponent<P1FaceOrbitColumns>();
            if (columns == null) columns = owner.gameObject.AddComponent<P1FaceOrbitColumns>();
            columns.boss = bossTransform;
            columns.ResolveBeam();
        }

        internal static bool IsRunningFor(P1NailFan owner)
        {
            return owner != null && owner.GetComponent<P1FaceOrbitColumns>()?.IsRunning == true;
        }

        internal static bool TryGetScheduleStart(P1NailFan owner, out float startTime)
        {
            P1FaceOrbitColumns columns = owner != null ? owner.GetComponent<P1FaceOrbitColumns>() : null;
            startTime = columns != null ? columns.ScheduleStartTime : -1f;
            return columns != null && columns.ScheduleStarted;
        }

        internal static void Stop(P1NailFan owner)
        {
            if (owner == null) return;
            P1FaceOrbitColumns columns = owner.GetComponent<P1FaceOrbitColumns>();
            if (columns != null) columns.StopAttack();
        }

        private void BeginAttack(Transform root)
        {
            StopAttack();
            boss = root;
            runActive = true;
            scheduleStarted = false;
            int ticket = ++generation;
            running = StartCoroutine(Run(ticket));
        }

        private IEnumerator Run(int ticket)
        {
            if (!ResolveBeam()) { Finish(ticket); yield break; }
            if (!beamObject.activeSelf) beamObject.SetActive(true);
            float deadline = Time.time + 1f;
            while (ticket == generation && Time.time < deadline && !ReadyForAntic(false)) yield return null;
            if (ticket != generation) yield break;
            if (!ReadyForAntic(false))
            {
                OrbRadiance.Instance.LogWarn("[p1orbit] native Ascend Beam did not leave Init; skipped the three columns");
                Finish(ticket);
                yield break;
            }
            attackStartedAt = Time.time;
            scheduleStarted = true;

            for (int i = 0; i < 3; i++)
            {
                float startAt = i * StartSpacing;
                float readyDeadline = attackStartedAt + startAt + 0.5f;
                while (Time.time < attackStartedAt + startAt || !ReadyForAntic(i > 0))
                {
                    if (ticket != generation) yield break;
                    if (Time.time >= readyDeadline)
                    {
                        OrbRadiance.Instance.LogWarn("[p1orbit] private Ascend Beam failed readiness for column " + (i + 1));
                        Finish(ticket);
                        yield break;
                    }
                    yield return null;
                }
                if (ticket != generation) yield break;
                if (!PlaceAtLockedHeroX())
                {
                    OrbRadiance.Instance.LogWarn("[p1orbit] main camera or hero missing; skipped column " + (i + 1));
                    yield return new WaitForSeconds(WarningTime + FireTime);
                    continue;
                }
                EyeBeamTrack.PlayP1ColumnSound(false);
                Send("ANTIC");
                yield return new WaitForSeconds(WarningTime);
                if (ticket != generation) yield break;
                EyeBeamTrack.PlayP1ColumnSound(true);
                Send("FIRE");
                yield return new WaitForSeconds(FireTime);
                if (ticket != generation) yield break;
                Send("END");
            }

            float remaining = TotalTime - (Time.time - attackStartedAt);
            // Keep the native Attack Choices held through the requested 2.15s window.
            if (remaining > 0f) yield return new WaitForSeconds(remaining);
            if (ticket == generation) Finish(ticket);
        }

        private bool ResolveBeam()
        {
            if (beamObject != null && beamFsm != null && beamFsm.Fsm != null) return true;
            Transform template = boss != null ? boss.Find("Eye Beam Glow/Ascend Beam") : null;
            if (template == null)
            {
                OrbRadiance.Instance.LogWarn("[p1orbit] native Ascend Beam template missing");
                return false;
            }
            try
            {
                stagingRoot = new GameObject("P1 face orbit beam staging");
                stagingRoot.transform.SetParent(transform, false);
                stagingRoot.SetActive(false);
                beamObject = Instantiate(template.gameObject, stagingRoot.transform);
                beamObject.name = "P1 face orbit vertical beam";
                beamFsm = beamObject.GetComponents<PlayMakerFSM>()
                    .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Control");
                if (beamFsm == null || !HiddenRoomDoor.PreparePrivateFsm(beamFsm))
                    throw new System.InvalidOperationException("private Control FSM preparation failed");
                Vector3 scale = beamObject.transform.localScale;
                beamObject.transform.localScale = new Vector3(BeamLength / UnitLength, BeamWidth / UnitWidth, scale.z);
                stagingRoot.SetActive(true);
                beamObject.SetActive(true);
            }
            catch (System.Exception e)
            {
                OrbRadiance.Instance.LogError("[p1orbit] cloning native Ascend Beam failed: " + e.Message);
                if (stagingRoot != null) Destroy(stagingRoot);
                stagingRoot = null;
                beamObject = null;
                beamFsm = null;
                return false;
            }
            return beamFsm != null;
        }

        private void SetBeamVisible(bool visible)
        {
            if (beamObject != null && beamObject.activeSelf != visible) beamObject.SetActive(visible);
        }

        private bool ReadyForAntic(bool allowNativeEnd)
        {
            if (beamObject == null || !beamObject.activeInHierarchy || beamFsm == null || beamFsm.Fsm == null || !beamFsm.Fsm.Started)
                return false;
            string state = beamFsm.Fsm.ActiveStateName;
            return state == InertState || (allowNativeEnd && state == "End");
        }

        private bool PlaceAtLockedHeroX()
        {
            HeroController hero = HeroController.instance;
            Camera camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
            if (hero == null || camera == null || beamObject == null) return false;
            float bottom = camera.ViewportToWorldPoint(new Vector3(0f, 0f, 10f)).y;
            Vector3 p = hero.transform.position;
            beamObject.transform.position = new Vector3(p.x, bottom - 20f, p.z);
            beamObject.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            return true;
        }

        private void Send(string eventName)
        {
            if (beamFsm != null && beamFsm.Fsm != null) beamFsm.SendEvent(eventName);
        }

        private void Finish(int ticket)
        {
            if (ticket != generation) return;
            running = null;
            runActive = false;
            if (beamFsm != null && beamFsm.Fsm != null && beamFsm.Fsm.Started && beamFsm.Fsm.ActiveStateName != InertState)
                beamFsm.Fsm.SetState(InertState);
        }

        private void StopAttack()
        {
            generation++;
            if (running != null) { StopCoroutine(running); running = null; }
            runActive = false;
            if (beamFsm != null && beamFsm.Fsm != null && beamFsm.Fsm.Started)
                beamFsm.Fsm.SetState(InertState);
        }

        private void OnDisable() => StopAttack();
        private void OnDestroy() => StopAttack();
    }

    // Keeps the native projectile's damage/collider/FSM and moves only its transform.
    internal sealed class P1NailOrbit : MonoBehaviour
    {
        private const float Radius = 5f;
        private const float AngularSpeed = 90f;
        private const float Lifetime = 2f;
        private PlayMakerFSM control;
        private float direction;
        private float startedAt;
        private float initialAngle;
        private bool angleCaptured;
        private Transform orbitCenter;

        internal void Init(PlayMakerFSM nativeControl, Transform center, float sign, float startTime)
        {
            control = nativeControl;
            orbitCenter = center;
            direction = sign;
            startedAt = startTime;
        }

        internal void BeginAt(float startTime)
        {
            startedAt = startTime;
            angleCaptured = false;
        }

        private void Update()
        {
            float elapsed = Time.time - startedAt;
            if (startedAt < 0f) return;
            if (elapsed >= Lifetime) { Destroy(gameObject); return; }
            if (control == null || control.Fsm == null || !control.Fsm.Started || orbitCenter == null) return;
            if (!angleCaptured)
            {
                initialAngle = transform.eulerAngles.z;
                angleCaptured = true;
            }
            float angle = initialAngle + direction * AngularSpeed * elapsed;
            float radians = angle * Mathf.Deg2Rad;
            Vector3 center = orbitCenter.position;
            Vector3 position = transform.position;
            position.x = center.x + Mathf.Cos(radians) * Radius;
            position.y = center.y + Mathf.Sin(radians) * Radius;
            transform.position = position;
            // Radiant Nail tip points along local +Y; orient that tip radially outward.
            transform.eulerAngles = new Vector3(0f, 0f, angle - 90f);
        }

        internal void StopAndDestroy()
        {
            if (gameObject != null) Destroy(gameObject);
        }
    }
}

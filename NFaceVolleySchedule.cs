using System;
using HutongGames.PlayMaker;

namespace OrbRadiance
{
    // N 难度脸刺波次调度器；每次 Update 最多发射一轮，避免卡顿后瞬间补发多轮。
    
    internal sealed class NFaceVolleySchedule
    {
        private enum Phase { WaitingStart, WaitingReady, Firing, Complete, Aborted, Failed }

        private readonly int waves;
        private readonly int nailsPerWave;
        private readonly float firstDelay;
        private readonly float interval;
        private readonly float readyTimeout;
        private readonly bool firstDelayFromStart;
        private Phase phase;
        private float deadline;
        private float nextFireAt;
        private float anticipatedAt;
        private float startedAt;
        private int wave;
        private Action<int> anticipate;
        private Action<int> fire;
        private Action complete;
        private Exception failure;

        internal NFaceVolleySchedule(int waveCount, int perWave, float initialDelay, float interWaveInterval,
                                     float timeout, bool anchorFirstDelayToStart = false)
        {
            if (waveCount <= 0 || perWave <= 0 || initialDelay < 0f || interWaveInterval <= 0f || timeout <= 0f)
                throw new ArgumentOutOfRangeException("N-face schedule parameters must be positive");
            waves = waveCount;
            nailsPerWave = perWave;
            firstDelay = initialDelay;
            interval = interWaveInterval;
            readyTimeout = timeout;
            firstDelayFromStart = anchorFirstDelayToStart;
        }

        internal bool IsComplete => phase == Phase.Complete;
        internal bool IsAborted => phase == Phase.Aborted;
        internal bool IsFailed => phase == Phase.Failed;
        internal string FailureReason => failure == null ? (phase == Phase.Failed ? "invalid readiness array" : null) : failure.Message;
        internal bool IsWaitingForStart => phase == Phase.WaitingStart;
        internal static bool IsNormalMode(bool effectiveHard, bool effectiveEasy) => !effectiveHard && !effectiveEasy;
        internal static bool IsNZVolleyMode(bool effectiveHard) => !effectiveHard;
        internal static string NativeAttackEvent(bool clockwise) => clockwise ? "FAN ATTACK CW" : "FAN ATTACK CCW";
        internal static bool StartupGate(bool nailStarted, bool beamIsInert, bool waitingForStart) =>
            nailStarted && (!waitingForStart || beamIsInert);

        internal void Start(float now, Action<int> onAnticipate, Action<int> onFire, Action onComplete = null)
        {
            if (phase != 0) throw new InvalidOperationException("N-face schedule can only start once");
            anticipate = onAnticipate ?? throw new ArgumentNullException(nameof(onAnticipate));
            fire = onFire ?? throw new ArgumentNullException(nameof(onFire));
            complete = onComplete;
            deadline = now + readyTimeout;
            startedAt = now;
            phase = Phase.WaitingStart;
        }

        // 每次 Unity Update 最多发射一轮。
        internal void Tick(float now, bool[] allStarted, bool[] allReady)
        {
            if (phase == Phase.Complete || phase == Phase.Aborted || phase == Phase.Failed) return;
            int count = waves * nailsPerWave;
            if (allStarted == null || allReady == null || allStarted.Length != count || allReady.Length != count)
            {
                phase = Phase.Failed;
                failure = new InvalidOperationException("readiness arrays must contain every prepared projectile");
                ClearCallbacks();
                return;
            }

            if ((phase == Phase.WaitingStart || phase == Phase.WaitingReady) && now > deadline)
            {
                phase = Phase.Failed;
                failure = new TimeoutException("native projectiles did not reach Started within the readiness timeout");
                ClearCallbacks();
                return;
            }

            if (phase == Phase.WaitingStart)
            {
                if (!Every(allStarted)) return;
                try
                {
                    for (int i = 0; i < count; i++) anticipate(i);
                }
                catch (Exception e)
                {
                    phase = Phase.Failed;
                    failure = e;
                    ClearCallbacks();
                    return;
                }
                anticipatedAt = now;
                phase = Phase.WaitingReady;
                deadline = now + readyTimeout;
                return;
            }

            if (phase == Phase.WaitingReady)
            {
                if (!Every(allReady)) return;
                phase = Phase.Firing;
                nextFireAt = Math.Max((firstDelayFromStart ? startedAt : anticipatedAt) + firstDelay, now);
                return;
            }

            if (phase != Phase.Firing || now < nextFireAt) return;

            int first = wave * nailsPerWave;
            try
            {
                for (int i = first; i < first + nailsPerWave; i++) fire(i);
            }
            catch (Exception e)
            {
                phase = Phase.Failed;
                failure = e;
                ClearCallbacks();
                return;
            }

            wave++;
            if (wave >= waves)
            {
                phase = Phase.Complete;
                Action onComplete = complete;
                ClearCallbacks();
                try { onComplete?.Invoke(); }
                catch (Exception e)
                {
                    phase = Phase.Failed;
                    failure = e;
                    ClearCallbacks();
                }
            }
            else
            {
                
                
                nextFireAt = now + interval;
            }
        }

        internal void Abort()
        {
            if (phase == Phase.Complete || phase == Phase.Failed || phase == Phase.Aborted) return;
            phase = Phase.Aborted;
            ClearCallbacks();
        }

        internal static float ProjectileAngle(float theta, bool clockwise, int waveIndex, int slot,
                                              float nativeRingOffset = 15f)
            => ProjectileAngle(theta, clockwise, waveIndex, slot, 6, nativeRingOffset);

        internal static float ProjectileAngle(float theta, bool clockwise, int waveIndex, int slot,
                                              int nailsPerWave, float nativeRingOffset)
        {
            if (nailsPerWave <= 0) throw new ArgumentOutOfRangeException(nameof(nailsPerWave));
            float sign = clockwise ? -1f : 1f;
            return theta + sign * (slot * (360f / nailsPerWave) + waveIndex * nativeRingOffset);
        }

        internal static float AngleFromVelocity(float x, float y)
        {
            return (float)(Math.Atan2(y, x) * (180.0 / Math.PI));
        }

        private static bool Every(bool[] values)
        {
            for (int i = 0; i < values.Length; i++) if (!values[i]) return false;
            return true;
        }

        private void ClearCallbacks()
        {
            anticipate = null;
            fire = null;
            complete = null;
        }
    }

    internal sealed class NFaceVolleyHoldAction : FsmStateAction
    {
        public override void OnEnter()
        {
            P2NailFanRing ring = Owner != null ? Owner.GetComponent<P2NailFanRing>() : null;
            if (ring == null)
            {
                Fsm.Event("FINISHED");
                return;
            }
            ring.BeginNormalVolley(Fsm.ActiveStateName.StartsWith("CW", StringComparison.Ordinal));
        }
    }
}

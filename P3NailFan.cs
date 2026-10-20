using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // HARD P3 在玩家附近循环生成五圈剑刺，Z/N 不启用该组件。


    internal class P3NailFan : MonoBehaviour
    {
        // ---------------- 时间点（状态名，与 P2NailStream 同一套）----------------

        private const string StateStart = "Ascend Tele";   // 进 P3
        private const string StateStop = "Scream";         // P4 起（瞬态）

        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };

        private const string ArenaFsmOwner = "Boss Control";

        // ---------------- 圆心（相对玩家，用户 2026-10-06 定）----------------

        private const float CenterUpY = -30f;        // 玩家底下 40 个单位
        private const float CenterXJitter = 10f;    // X = 玩家当前 X ± 10

        // ---------------- 剑环参数（照天国余晖 P2 / ANY2）----------------

        private const int NailCount = 24;           // 每圈枚数（原版 12）
        private const float RingStep = 360f / NailCount;   
        private const int RingCount = 5;            // 圈数（原版 2）
        private const float RingOffset = 7.5f;      // 圈间偏移（`Restart` 的 FloatAdd；原版 15°=整格）

        private const int NailsPerFrame = 6;        // 每帧铺几枚（加快铺圈；原版 1）
        private const float NailInterval = 0.01f;   // 每批之间的间隔（`CW Repeat` 的 Wait 0.01）
        private const float RingInterval = 0.01f;   // 圈间铺圈间隔（`CW/CCW Restart` 的 Wait，原版禁用）
        private const float FanDelay = 0.3f;        // 整圈铺完 → `FAN ATTACK` 的延迟（`CW/CCW Fire` 的 delay）

        // 发射节奏（与铺圈速度解耦）：相邻两圈固定隔这么久发射，不随帧率/铺圈快慢漂移。
        private const float WaveInterval = 0.25f;

        // 钉子飞行时长（`Fire CW/CCW` 的 `Wait`，原版 2s）。拉长让钉子飞更久、够玩家下劈；
        // 注意同一状态每帧 `Angle ∓= 30°/s`，时长越长卷得越厉害（2s≈60°、4s≈120°）。
        //用户改了
        private const float NailLifetime = 4f;

        // 钉子飞行段线速度（`Fire CW/CCW` 的 `SetVelocityAsAngle.speed`，原版 25）。
        // 转向（`FloatAdd.add`）按速度同比例缩放 → 弧线半径 = 线速 ÷ 角速 保持不变（原版 ≈47.75）。
        private const float VanillaFlightSpeed = 25f;
        private const float VanillaTurnRate = 30f;      // 原版转向 °/s
        private const float FlightSpeed = 23f;
        private const float TurnRate = VanillaTurnRate * FlightSpeed / VanillaFlightSpeed;   // 30 × 23/25 = 27.6°/s

        // ---------------- 运行时 ----------------

        private sealed class Ring
        {
            public readonly List<GameObject> Nails = new List<GameObject>();
            public float FireAt = float.MaxValue;   // 到点发 FAN ATTACK
            public bool Fired;
        }

        private PlayMakerFSM control;               // Absolute Radiance/Control：起停

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;
        private string lastState;

        private GameObject prefab;
        private float nailZ;

        private Coroutine run;
        private readonly List<GameObject> live = new List<GameObject>();   // 在场脸刺（停火时清）

        private bool subscribed;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
            Subscribe();
        }

        private void OnDestroy()
        {
            if (subscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnStateChanged;
            subscribed = false;
            StopFan();
        }

        // `Scream`（P4 起点）是瞬态、逐帧采样采不到 —— 同 P2NailStream。
        private void Subscribe()
        {
            if (subscribed) return;
            if (control == null || control.Fsm == null) return;
            control.Fsm.StateChanged += OnStateChanged;
            subscribed = true;
        }

        private void OnStateChanged(FsmState state)
        {
            if (state == null) return;
            if (state.Name == StateStop) StopFan();
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // Z/N 在 P3 不使用此剑环；N 的 P3UpwardRain 已停用，只有 HARD 保留本攻击。
            if (!OrbRadiance.EffectiveHardMode)
            {
                StopFan();
                lastState = control.Fsm.ActiveStateName;
                return;
            }

            // 钉子被 `RecycleSelf` 回收后会变成 Unity 假 null，顺手剔出维护列表
            live.RemoveAll(n => n == null);

            // 总开关关掉：停火清场；lastState 置空，重新打开时能按当前状态补开
            if (!OrbRadiance.Settings.Enabled)
            {
                StopFan();
                lastState = null;
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同场景重来，Control 不会自己回 Init
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (Array.IndexOf(ResetStates, arena) >= 0) StopFan();
            }

            string cur = control.Fsm.ActiveStateName;
            if (cur != lastState)
            {
                lastState = cur;
                if (cur == StateStart) StartFan();
            }
        }

        // ---------------- 起停 ----------------

        private void StartFan()
        {
            if (run != null) return;    // 已在放

            if (prefab == null) prefab = P2NailStream.ResolveNailPrefab(gameObject);
            if (prefab == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 解析脸刺 prefab 失败（Attack Commands / CW Spawn），P3 脸刺没开");
                return;
            }

            nailZ = transform.position.z;
            run = StartCoroutine(Loop());

            OrbRadiance.Instance.Log("[p3fan] P3 脸刺开火：圆心 = 玩家头顶 " + CenterUpY + "、X 玩家±" +
                                     CenterXJitter + "；每轮 " + RingCount + " 圈 × " + NailCount +
                                     " 枚（圈内 " + RingStep + "°、圈间 " + RingOffset + "°），一轮铺完即接下一轮");
        }

        private void StopFan()
        {
            if (run != null)
            {
                StopCoroutine(run);
                run = null;
            }

            int n = 0;
            foreach (GameObject g in live)
            {
                if (g == null) continue;    // 场景卸载 / 已被回收（Unity 假 null）
                Destroy(g);
                n++;
            }
            live.Clear();
            if (n > 0) OrbRadiance.Instance.Log("[p3fan] 清掉在飞的脸刺 " + n + " 枚");
        }

        // ---------------- 一轮剑环 ----------------

        // 一轮铺完立刻接下一轮（用户 2026-10-06 定）。钉子自己 2s 后回收，不等。
        private IEnumerator Loop()
        {
            while (true)
            {
                yield return RunBarrage();
                yield return null;      // 拿不到玩家当帧空跑时不至于死循环
            }
        }

        private IEnumerator RunBarrage()
        {
            HeroController hc = HeroController.instance;
            if (hc == null) yield break;

            Vector2 hero = hc.transform.position;
            Vector2 center = new Vector2(
                hero.x + UnityEngine.Random.Range(-CenterXJitter, CenterXJitter),
                hero.y + CenterUpY);

            float baseAngle = UnityEngine.Random.Range(0f, 360f);
            bool cw = UnityEngine.Random.value < 0.5f;
            float sgn = cw ? -1f : 1f;                                  // 圈内/圈间同方向递进（原版 `Dir` 一次定）
            string fireEvent = cw ? "FAN ATTACK CW" : "FAN ATTACK CCW";

            var pending = new List<Ring>(RingCount);
            float nextFireAt = 0f;                                      // 上一圈的发射时点（解耦用）

            for (int r = 0; r < RingCount; r++)
            {
                var ring = new Ring();
                pending.Add(ring);

                for (int i = 0; i < NailCount; i++)
                {
                    float deg = baseAngle + sgn * (i * RingStep + r * RingOffset);
                    GameObject nail = SpawnNail(center, deg);
                    if (nail != null) ring.Nails.Add(nail);

                    if ((i + 1) % NailsPerFrame != 0 && i != NailCount - 1) continue;   // 攒够一帧的量再让帧

                    FireDue(pending, fireEvent);
                    yield return null;                                  // 原版 `CW Spawn` 的 NextFrameEvent（一帧）
                    yield return new WaitForSeconds(NailInterval);      // 原版 `CW Repeat` 的 Wait 0.01（批间）
                }

                // 解耦：发射时点 = max(这圈铺完 + 前摇, 上一圈 + WaveInterval)
                ring.FireAt = Mathf.Max(Time.time + FanDelay, nextFireAt);
                nextFireAt = ring.FireAt + WaveInterval;
                FireDue(pending, fireEvent);

                yield return new WaitForSeconds(RingInterval);          // 圈间铺圈 0.01s（InterRingDelay）
                FireDue(pending, fireEvent);
            }

            while (!AllFired(pending))                                  // 等最后几圈发射完
            {
                FireDue(pending, fireEvent);
                yield return null;
            }
        }

        // 到点的圈发 FAN ATTACK（整圈一起转着飞）
        private void FireDue(List<Ring> pending, string fireEvent)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                Ring ring = pending[i];
                if (ring.Fired || Time.time < ring.FireAt) continue;
                ring.Fired = true;
                foreach (GameObject n in ring.Nails) SendEvent(n, fireEvent);
            }
        }

        private static bool AllFired(List<Ring> pending)
        {
            for (int i = 0; i < pending.Count; i++)
                if (!pending[i].Fired) return false;
            return true;
        }

        private GameObject SpawnNail(Vector2 center, float deg)
        {
            GameObject nail = null;
            try
            {
                nail = Instantiate(prefab, new Vector3(center.x, center.y, nailZ), Quaternion.Euler(0f, 0f, deg));
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: Instantiate P3 脸刺抛异常 " + e);
            }
            if (nail == null) return null;

            nail.SetActive(true);

            // 原版 `CW/CCW Spawn` 里 `SetRotation zAngle = Rotation`：钉子 `Fan Out` 的 GetRotation 读它定弹射方向
            nail.transform.eulerAngles = new Vector3(0f, 0f, deg);

            // 保留 prefab 自带 FSM（外观 / 伤害 / 下劈手感同天国余晖），只补 VoidKill 放行标记
            if (nail.GetComponent<NailProjectile>() == null)
                nail.AddComponent<NailProjectile>();

            PatchFireStates(nail);      // 只改这枚自己的 Fire CW/CCW，不动共享 prefab

            SendEvent(nail, "FAN ANTIC");       // → Fan Antic → Fan Out（向外弹射 ~5 单位）

            live.Add(nail);
            return nail;
        }

        private static void SendEvent(GameObject go, string evt)
        {
            if (go == null) return;
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null) continue;
                f.SendEvent(evt);
            }
        }

        // 改这枚自己的 `Fire CW`/`Fire CCW`（原位改值，不动共享 prefab，不增删动作）：
        // 飞行时长 `Wait`、线速度 `SetVelocityAsAngle.speed`、转向 `FloatAdd.add`（转向按速度同比例缩 → 弧线半径不变）
        private static void PatchFireStates(GameObject nail)
        {
            foreach (PlayMakerFSM f in nail.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null) continue;
                PatchFireState(f.Fsm, "Fire CW");
                PatchFireState(f.Fsm, "Fire CCW");
            }
        }

        private static void PatchFireState(Fsm fsm, string stateName)
        {
            FsmState st = fsm.GetState(stateName);
            if (st == null) return;
            foreach (FsmStateAction a in st.Actions)
            {
                Wait w = a as Wait;
                if (w != null && w.time != null) w.time.Value = NailLifetime;

                SetVelocityAsAngle v = a as SetVelocityAsAngle;
                if (v != null && v.speed != null) v.speed.Value = FlightSpeed;

                FloatAdd fa = a as FloatAdd;
                if (fa != null && fa.add != null)
                    fa.add.Value = Mathf.Sign(fa.add.Value) * TurnRate;   // 保符号：CW=−、CCW=+
            }
        }

        // ---------------- 重打复位 ----------------

        private string ArenaState()
        {
            if (!arenaTried)
            {
                arenaTried = true;
                Transform parent = transform.parent;
                if (parent != null)
                    foreach (PlayMakerFSM f in parent.GetComponents<PlayMakerFSM>())
                        if (f != null && f.Fsm != null && f.FsmName == "Control") { arenaFsm = f; break; }

                if (arenaFsm == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + ArenaFsmOwner +
                                                 " 的 Control FSM，重打时 P3 脸刺不会自动清");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

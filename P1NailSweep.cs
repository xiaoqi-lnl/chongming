using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P1 横刺改造（2026-10-05 用户定）。
    //
    // 原版：`Attack Choices` 的 `A1 Choice` 随机选 `Nail L Sweep` / `Nail R Sweep` 之一，
    //   该状态 3 个 SendEventByName 在 0.5 / 1.7 / 2.9s 发 `COMB L`（或 `COMB R`）→ 同一侧连出
    //   3 堵靠 `Tween` 平移横扫的 comb 墙，`Wait 4.4s` 后 `Return` 收招。
    //
    // 改成：**左右各立一堵固定不动的 6 把竖墙**，起手 0.1s（保留出剑音效），之后**每 0.3s 一发、
    //   左右交替** —— 从该侧墙上**随机抽一把**、脱离墙、原地锁定朝向玩家 0.01s、再以 **30 u/s
    //   追着小骑士**飞出去；两排抽空即结束。
    //
    // 为什么不用原版 comb 的墙（见 [[feedback-fsm-index-stability]]）：comb 那一排是写死 **16 把**
    //   （右 8 左 8，每次循环 +2 把）、间距不匀（组内隔 2、组间隔 4），墙还靠 `Tween` 平移扫过去。
    //   要「固定不动 + 匀排 6 把」反而得动它 6 处 spawn 动作、还凑不出对称的 6。这里直接自己
    //   `Instantiate` `Radiant Nail`（外观/伤害口径与 P2 飞剑一致，复用 P2NailStream.StripAndArm）。
    //
    // 挂钩：只针对神居无上辐光（`OnFsmEnable` 里判 `self.gameObject.name == "Absolute Radiance"`）。
    //   普通辐光（Dream_Final_Boss/Radiance）也有同名 `Nail L/R Sweep` 状态，这边**不接管** ——
    //   否则那边会被关掉 COMB 却没有替代，横刺直接没了。
    //
    // 窗口时长：把 `Nail L/R Sweep` 的 `Wait`（原版 4.4）改成算出来的总时长（见 WindowTime），
    //   保证 boss 一直停在这一招里，直到最后一发打完才 `Return`。
    internal class P1NailSweep : MonoBehaviour
    {
        // Native GG comb profile used by the N P1/P3 Type 3 rain. Values are sourced from
        // Attack Commands' native Comb Top (Type 3): four 4-nail rows, Increment=2,
        // Radiant Nail Speed=20; the N rain's approved interval is native Wait 0.85 / 1.5.
        internal readonly struct NativeShapeProfile
        {
            internal readonly int Count;
            internal readonly float Speed;
            internal readonly float Interval;

            internal NativeShapeProfile(int count, float speed, float interval)
            {
                Count = count;
                Speed = speed;
                Interval = interval;
            }
        }

        internal static NativeShapeProfile Type3Profile => new NativeShapeProfile(16, 20f, 0.85f / 1.5f);

        // Native Spawn RG/LG chooses one of three gap layouts per group. Preserve its asymmetric
        // positions relative to the owner instead of synthesizing a uniformly spaced centered row.
        internal static float[] CreateNativeOffsets()
            => P3PhasePlan.CreateNativeOffsets();

        internal static float[] BuildNativeOffsets(int[] rightGaps, int[] leftGaps)
            => P3PhasePlan.BuildNativeOffsets(rightGaps, leftGaps);

        // 只改这两个状态（P2 是 `Nail L/R Sweep 2`，完全不动）
        private static readonly string[] SweepStates = { "Nail L Sweep", "Nail R Sweep" };

        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };
        private const string ArenaFsmOwner = "Boss Control";

        // ---------------- 墙的几何 ----------------

        // 两侧墙的生成点 = 原版 comb `L` / `R` 状态的 SetPosition（用户定「停留在原版生成点」）。
        // 竖墙：钉子沿 y 匀排、以生成点居中（原版 comb 的 Increment = 2）。
        private const float LeftX = 42.46f;
        private const float LeftY = 26.73f;
        private const float RightX = 78.46f;
        private const float RightY = 28.73f;

        private const int NailsPerWall = 6;         // 每排 6 把（2026-10-05 用户定）
        private const float NailSpacing = 2f;       // 同原版 Increment
        private const float NativeWallSpeed = 17f;
        private const float NativeWallSetInterval = 0.4f;
        private const int NativeWallSetCount = 2;
        private const float NativeWallWindow = 5.8f;

        // ---------------- 节奏 / 运动 ----------------

        private const float Windup = 0.1f;          // 起手（原版 comb 的 Antic 前摇 0.4 → 0.1，音效保留）
        private const float Interval = 0.3f;        // 左右交替：每 0.3s 一发（每侧实际 0.6s 一发）
        private const float AimTime = 0.01f;        // 抽出后原地锁定朝向玩家的时间（之后才飞）
        private const float FlightSpeed = 30f;      // 同 P2 飞剑（原版 Fire 线速度 25 的 1.2 倍）
        private const float TurnRate = 30f;         // 飞行中追踪的转率 °/s（用户定：先按原版 Fire 的 30 起步）

        // ---------------- 伤害（同 P2 飞剑口径，见 P2NailStream）----------------

        private const int NailDamage = 2;
        private const int NailHazardType = 0;
        private const int LayerProjectile = 11;

        private static readonly Vector2 BoundsMin = new Vector2(15f, 5f);
        private static readonly Vector2 BoundsMax = new Vector2(104f, 85f);

        // 窗口时长 = 起手 + 间隔×(总发数-1) + 余量。总发数 = 两侧各 NailsPerWall 把、交替发。
        private const int TotalShots = NailsPerWall * 2;
        private const float WindowTime = Windup + Interval * (TotalShots - 1) + 0.25f;   // ≈3.65s

        // ---------------- 静态补丁 / 还原 ----------------

        // 同一 FSM 只打一次（Attack Choices 是常驻 FSM，OnEnable 会重入）。
        private static readonly HashSet<Fsm> Patched = new HashSet<Fsm>();

        // 关总开关时写回：3 个 COMB sender 的 Enabled + 窗口 Wait 的原值。
        private static readonly Dictionary<SendEventByName, bool> OriginalEnabled =
            new Dictionary<SendEventByName, bool>();
        private static readonly Dictionary<Wait, float> OriginalWait = new Dictionary<Wait, float>();

        private static P1NailSweep active;

        internal static void Restore()
        {
            foreach (KeyValuePair<SendEventByName, bool> kv in OriginalEnabled)
                if (kv.Key != null) kv.Key.Enabled = kv.Value;
            foreach (KeyValuePair<Wait, float> kv in OriginalWait)
                if (kv.Key != null) kv.Key.time.Value = kv.Value;
            OriginalEnabled.Clear();
            OriginalWait.Clear();
            Patched.Clear();
            if (active != null) active.HardCleanup();
        }

        // 原位改字段值、不增删动作：关掉 3 个 COMB sender + 改窗口 Wait。
        internal static void Patch(Fsm fsm)
        {
            if (!Patched.Add(fsm)) return;

            foreach (string name in SweepStates)
            {
                FsmState st = fsm.GetState(name);
                if (st == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Choices 里没有 " + name +
                                                 " 状态，P1 横刺未接管");
                    continue;
                }

                int off = 0;
                foreach (SendEventByName s in st.Actions.OfType<SendEventByName>())
                {
                    if (s == null || s.sendEvent == null) continue;
                    string e = s.sendEvent.Value;
                    if (e != "COMB L" && e != "COMB R") continue;
                    if (!OriginalEnabled.ContainsKey(s)) OriginalEnabled[s] = s.Enabled;
                    s.Enabled = false;
                    off++;
                }

                Wait w = st.Actions.OfType<Wait>().FirstOrDefault();
                if (w == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + name + " 里找不到 Wait，窗口时长未改");
                    continue;
                }
                if (!OriginalWait.ContainsKey(w)) OriginalWait[w] = w.time.Value;
                w.time.Value = OrbRadiance.EffectiveHardMode ? WindowTime : NativeWallWindow;

                OrbRadiance.Instance.Log("[p1sweep] " + name + "：关掉 " + off + " 个 COMB sender、" +
                                         "窗口 " + w.time.Value.ToString("0.##") + "s（原版 4.4）");
            }

            OrbRadiance.Instance.Log("[p1sweep] P1 横刺 → " + (OrbRadiance.EffectiveHardMode
                                     ? "H legacy projectile sweep"
                                     : "N/Z native 16-nail rows, two paired starts at 0/.4s, local +Y speed 17") +
                                     "; window " + (OrbRadiance.EffectiveHardMode ? WindowTime : NativeWallWindow) + "s");
        }

        // ---------------- 运行时 ----------------

        private sealed class Shot
        {
            public Transform tr;
            public Vector2 dir;     // 当前飞行方向（单位向量，逐帧朝小骑士转）
            public float aimLeft;   // 剩余原地瞄准时间（>0 时不移动，只锁定朝向）
        }

        private PlayMakerFSM choices;               // Absolute Radiance/Attack Choices：招式窗口
        private PlayMakerFSM commands;              // Absolute Radiance/Attack Commands：取 prefab / 音效

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private bool subscribed;                    // 已订阅 choices.Fsm.StateChanged

        private GameObject nailPrefab;
        private float nailZ;

        private readonly List<Transform> leftWall = new List<Transform>();
        private readonly List<Transform> rightWall = new List<Transform>();
        private readonly List<Shot> flying = new List<Shot>();
        private readonly List<Transform> nativeWalls = new List<Transform>();
        private readonly List<Vector2> nativeWallDirections = new List<Vector2>();
        private float nativeWallStartedAt;
        private int nativeWallSet;
        private int nativeWallSetsGenerated;
        private int nativeWallTicket;
        private bool nativeWallActive;

        private Coroutine running;

        private AudioSource audio;
        private AudioClip shootClip;
        private bool audioBuilt;

        internal void Init(PlayMakerFSM choicesFsm)
        {
            choices = choicesFsm;
            active = this;
            Subscribe();
        }

        private void Awake()
        {
            active = this;
        }

        // 靠 FSM 的「状态进入」事件驱动，而不是 Update 里每帧采样 ActiveStateName：
        // PlayMaker 会**同帧连跳多个状态**（`Fsm.UpdateStateChanges` 里的 `while (IsSwitchingState)`，
        // PlayMaker.decompiled.cs:11941）。连放横刺时一帧内就走完
        // `Nail L Sweep → Return → A1 Choice → Nail R Sweep`（或再次 `Nail L Sweep`），
        // 采样式检测会漏掉 `Return`/`A1 Choice`，也分不清「同名状态重入」——第二发既不立墙也不清场，
        // boss 停在窗口里没动作，看起来就是「不出下一招」。
        // StateChanged 在每次 EnterState 都触发（PlayMaker.decompiled.cs:12002-12022），
        // 且全 PlayMaker 只有「声明 + 调用」两处、没人会把它置空（12019 / 9773）。
        private void Subscribe()
        {
            if (subscribed) return;
            if (choices == null || choices.Fsm == null) return;
            choices.Fsm.StateChanged += OnStateChanged;
            subscribed = true;
        }

        private void OnStateChanged(FsmState state)
        {
            if (state == null) return;
            if (!OrbRadiance.Settings.Enabled) return;   // 总开关关着不立墙（Update 会清场）

            if (Array.IndexOf(SweepStates, state.Name) >= 0) Begin();
            else CleanupWalls();                         // 出招：收墙，在飞的那几把照常飞完
        }

        private void OnDestroy()
        {
            if (subscribed && choices != null && choices.Fsm != null)
                choices.Fsm.StateChanged -= OnStateChanged;
            subscribed = false;
            HardCleanup();
        }

        private void Update()
        {
            // 总开关关掉：清场（墙 + 在飞的都收掉）
            if (!OrbRadiance.Settings.Enabled)
            {
                HardCleanup();
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同一场景内重来，Attack Choices 不会自己复位
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) HardCleanup();
            }

            DriveFlying(Time.deltaTime);
            DriveNativeWalls(Time.deltaTime);
        }

        // ---------------- 一轮 ----------------

        private void Begin()
        {
            CleanupWalls();     // 保险：清掉上一轮残留

            if (nailPrefab == null)
            {
                nailPrefab = ResolveNailPrefab();
                if (nailPrefab == null) return;
            }
            nailZ = transform.position.z;
            EnsureAudio();

            if (!OrbRadiance.EffectiveHardMode)
            {
                nativeWallTicket++;
                nativeWallActive = true;
                nativeWallStartedAt = Time.time;
                nativeWallSet = 0;
                nativeWallSetsGenerated = 0;
                CreateNativeWallSet();
                return;
            }

            BuildWall(LeftX, LeftY, leftWall, true);
            BuildWall(RightX, RightY, rightWall, false);
            if (leftWall.Count == 0 && rightWall.Count == 0) return;

            EnsureAudio();
            running = StartCoroutine(Run());

            OrbRadiance.Instance.Log("[p1sweep] 左右各立 " + leftWall.Count + " / " + rightWall.Count +
                                     " 把固定竖墙，开抽");
        }

        private IEnumerator Run()
        {
            PlaySound(shootClip);
            yield return new WaitForSeconds(Windup);

            bool useLeft = true;
            for (int i = 0; i < TotalShots; i++)
            {
                if (i > 0) yield return new WaitForSeconds(Interval);

                List<Transform> w = useLeft ? leftWall : rightWall;
                if (w.Count > 0) FireOne(w, useLeft);
                useLeft = !useLeft;

                if (leftWall.Count == 0 && rightWall.Count == 0) break;
            }

            running = null;
        }

        // ---------------- 立墙 / 抽剑 ----------------

        private void DriveNativeWalls(float dt)
        {
            if (OrbRadiance.EffectiveHardMode || !nativeWallActive) return;
            int ticket = nativeWallTicket;
            if (nativeWallSet + 1 < NativeWallSetCount && Time.time >= nativeWallStartedAt + NativeWallSetInterval)
            {
                nativeWallSet++;
                nativeWallStartedAt = Time.time;
                CreateNativeWallSet();
            }
            bool haveView = TryVisibleRect(out float xMin, out float xMax, out float yMin, out float yMax);
            for (int i = nativeWalls.Count - 1; i >= 0; i--)
            {
                Transform nail = nativeWalls[i];
                if (nail == null) { RemoveNativeWallAt(i, false); continue; }
                Vector3 p = nail.position;
                p += (Vector3)(nativeWallDirections[i] * NativeWallSpeed * dt);
                nail.position = p;
                if (haveView && TryVisualBounds(nail, out UnityEngine.Bounds bounds))
                {
                    if (!IntersectsView(bounds, xMin, xMax, yMin, yMax) &&
                        PastForwardViewEdge(bounds, nativeWallDirections[i], xMin, xMax, yMin, yMax))
                        RemoveNativeWallAt(i, true);
                }
                else if (!haveView && (p.x < BoundsMin.x || p.x > BoundsMax.x || p.y < BoundsMin.y || p.y > BoundsMax.y))
                {
                    // Geometry is still bounded off-screen, but completion falls back to the native 5.8s Wait.
                    RemoveNativeWallAt(i, true);
                }
            }

            if (ticket != nativeWallTicket) return;
            if (haveView && nativeWallSetsGenerated == NativeWallSetCount && nativeWalls.Count == 0)
                CompleteNativeWalls(ticket);
        }

        private void CreateNativeWallSet()
        {
            float[] offsets = CreateNativeOffsets();
            int left = CreateNativeSide(offsets, LeftX, LeftY, -45f);
            int right = CreateNativeSide(offsets, RightX, RightY, 45f);
            if (left > 0 && right > 0)
            {
                nativeWallSetsGenerated++;
                PlaySound(shootClip); // one original sword sound for each paired left/right wall
            }
        }

        private int CreateNativeSide(float[] offsets, float centerX, float centerY, float rotation)
        {
            int created = 0;
            float radians = rotation * Mathf.Deg2Rad;
            Vector2 travel = new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 position = new Vector3(centerX + offsets[i] * Mathf.Cos(radians),
                                               centerY + offsets[i] * Mathf.Sin(radians), nailZ);
                GameObject nail = null;
                try { nail = Instantiate(nailPrefab, position, Quaternion.identity); }
                catch (Exception e) { OrbRadiance.Instance.LogWarn("P1 native wall instantiate failed: " + e.Message); }
                if (nail == null) continue;
                nail.SetActive(true);
                nail.transform.eulerAngles = new Vector3(0f, 0f, rotation);
                DamageHero damage = nail.GetComponent<DamageHero>();
                if (damage != null) damage.hazardType = 1;
                if (nail.GetComponent<NailProjectile>() == null) nail.AddComponent<NailProjectile>();
                nativeWalls.Add(nail.transform);
                nativeWallDirections.Add(travel);
                created++;
            }
            return created;
        }

        private void CompleteNativeWalls(int ticket)
        {
            if (!nativeWallActive || ticket != nativeWallTicket) return;
            nativeWallActive = false;
            nativeWallTicket++;
            int setCount = nativeWallSetsGenerated;
            nativeWalls.Clear();
            nativeWallDirections.Clear();
            OrbRadiance.Instance.Log("[p1sweep] 两对原生横刺已完整生成并越过视口，立即结束Choices（轮数=" + setCount + "，清场t=" + Time.time.ToString("F2") + "s）");
            if (choices != null && choices.Fsm != null && Array.IndexOf(SweepStates, choices.Fsm.ActiveStateName) >= 0)
                choices.SendEvent("FINISHED");
        }

        private static bool TryVisibleRect(out float xMin, out float xMax, out float yMin, out float yMax)
        {
            xMin = xMax = yMin = yMax = 0f;
            Camera cam = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
            if (cam == null || cam.pixelWidth <= 0 || cam.pixelHeight <= 0) return false;
            Rect pixel = cam.pixelRect;
            float depth = -cam.transform.position.z;
            if (depth <= 0f) depth = 1f;
            Vector3 bottomLeft = cam.ScreenToWorldPoint(new Vector3(pixel.xMin, pixel.yMin, depth));
            Vector3 topRight = cam.ScreenToWorldPoint(new Vector3(pixel.xMax, pixel.yMax, depth));
            float width = topRight.x - bottomLeft.x, height = topRight.y - bottomLeft.y;
            if (width < 10f || width > 100f || height < 5f || height > 70f) return false;
            xMin = bottomLeft.x; xMax = topRight.x; yMin = bottomLeft.y; yMax = topRight.y;
            return true;
        }

        private static bool TryVisualBounds(Transform nail, out UnityEngine.Bounds bounds)
        {
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            bool found = false;
            foreach (Renderer renderer in nail.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                IncludeBounds(renderer.bounds, ref min, ref max);
                found = true;
            }
            foreach (Collider2D collider in nail.GetComponentsInChildren<Collider2D>(true))
            {
                if (collider == null || !collider.enabled) continue;
                IncludeBounds(collider.bounds, ref min, ref max);
                found = true;
            }
            bounds = found ? new UnityEngine.Bounds(new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, (min.z + max.z) * 0.5f), max - min) : default;
            return found;
        }

        private static void IncludeBounds(UnityEngine.Bounds bounds, ref Vector3 min, ref Vector3 max)
        {
            Vector3 bMin = bounds.min, bMax = bounds.max;
            min = new Vector3(Mathf.Min(min.x, bMin.x), Mathf.Min(min.y, bMin.y), Mathf.Min(min.z, bMin.z));
            max = new Vector3(Mathf.Max(max.x, bMax.x), Mathf.Max(max.y, bMax.y), Mathf.Max(max.z, bMax.z));
        }

        private static bool IntersectsView(UnityEngine.Bounds b, float xMin, float xMax, float yMin, float yMax)
            => b.max.x >= xMin && b.min.x <= xMax && b.max.y >= yMin && b.min.y <= yMax;

        private static bool PastForwardViewEdge(UnityEngine.Bounds b, Vector2 direction, float xMin, float xMax, float yMin, float yMax)
            => (direction.x > 0.001f && b.min.x > xMax) || (direction.x < -0.001f && b.max.x < xMin) ||
               (direction.y > 0.001f && b.min.y > yMax) || (direction.y < -0.001f && b.max.y < yMin);

        private void RemoveNativeWallAt(int index, bool destroy)
        {
            Transform nail = nativeWalls[index];
            if (destroy && nail != null) Destroy(nail.gameObject);
            nativeWalls.RemoveAt(index);
            nativeWallDirections.RemoveAt(index);
        }

        private void BuildWall(float x, float y, List<Transform> into, bool faceRight)
        {
            into.Clear();

            float top = y + (NailsPerWall - 1) * 0.5f * NailSpacing;
            // 钉子贴图沿本地 +Y 指（见 NailTopSweep.cs）→ 转 -90° 朝世界 +X（右墙朝场地内），+90° 朝 -X（左墙）。
            float zRot = faceRight ? -90f : 90f;

            for (int i = 0; i < NailsPerWall; i++)
            {
                Vector3 pos = new Vector3(x, top - i * NailSpacing, nailZ);

                GameObject go = null;
                try
                {
                    go = Instantiate(nailPrefab, pos, Quaternion.identity);
                }
                catch (Exception e)
                {
                    OrbRadiance.Instance.LogError("OrbRadiance: P1 横刺立墙 Instantiate 抛异常 " + e);
                }
                if (go == null) continue;

                go.SetActive(true);
                P2NailStream.StripAndArm(go);       // 毁私有 FSM + 开碰撞 + 补伤害
                DamageHero damage = go.GetComponent<DamageHero>();
                if (damage != null) damage.hazardType = OrbRadiance.EffectiveHardMode ? NailHazardType : 1;
                go.transform.position = pos;
                go.transform.eulerAngles = new Vector3(0f, 0f, zRot);
                into.Add(go.transform);
            }
        }

        private void FireOne(List<Transform> wall, bool fromLeft)
        {
            int idx = UnityEngine.Random.Range(0, wall.Count);
            Transform nail = wall[idx];
            wall.RemoveAt(idx);
            if (nail == null) return;

            HeroController hc = HeroController.instance;
            Vector2 origin = nail.position;
            Vector2 target = hc != null
                ? (Vector2)hc.transform.position
                : origin + (fromLeft ? Vector2.right : Vector2.left);

            Vector2 d = target - origin;
            if (d.sqrMagnitude < 0.0001f) d = fromLeft ? Vector2.right : Vector2.left;
            d.Normalize();

            nail.eulerAngles = new Vector3(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg - 90f);
            flying.Add(new Shot { tr = nail, dir = d, aimLeft = AimTime });
        }

        // ---------------- 飞行（逐帧追踪）----------------

        private void DriveFlying(float dt)
        {
            HeroController hc = HeroController.instance;

            for (int i = flying.Count - 1; i >= 0; i--)
            {
                Shot s = flying[i];
                if (s.tr == null) { flying.RemoveAt(i); continue; }

                Vector2 to = hc != null
                    ? (Vector2)hc.transform.position - (Vector2)s.tr.position
                    : Vector2.zero;

                // 瞄准：抽出后原地瞬间锁定朝向玩家，AimTime 内不移动
                if (s.aimLeft > 0f)
                {
                    s.aimLeft -= dt;
                    if (to.sqrMagnitude > 0.0001f)
                    {
                        float a = Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
                        s.dir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
                        s.tr.eulerAngles = new Vector3(0f, 0f, a - 90f);
                    }
                    continue;
                }

                if (hc != null && to.sqrMagnitude > 0.0001f)
                {
                    float cur = Mathf.Atan2(s.dir.y, s.dir.x) * Mathf.Rad2Deg;
                    float tgt = Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
                    float next = Mathf.MoveTowardsAngle(cur, tgt, TurnRate * dt);
                    s.dir = new Vector2(Mathf.Cos(next * Mathf.Deg2Rad), Mathf.Sin(next * Mathf.Deg2Rad));
                    s.tr.eulerAngles = new Vector3(0f, 0f, next - 90f);
                }

                Vector3 p = s.tr.position;
                p.x += s.dir.x * FlightSpeed * dt;
                p.y += s.dir.y * FlightSpeed * dt;
                s.tr.position = p;

                if (p.x < BoundsMin.x || p.x > BoundsMax.x || p.y < BoundsMin.y || p.y > BoundsMax.y)
                {
                    Destroy(s.tr.gameObject);
                    flying.RemoveAt(i);
                }
            }
        }

        // ---------------- 清场 ----------------

        private void CleanupWalls()
        {
            if (running != null) { StopCoroutine(running); running = null; }
            nativeWallTicket++;
            nativeWallActive = false;
            DestroyList(leftWall);
            DestroyList(rightWall);
            DestroyList(nativeWalls);
            nativeWallDirections.Clear();
            nativeWallSetsGenerated = 0;
        }

        private void HardCleanup()
        {
            CleanupWalls();
            foreach (Shot s in flying)
                if (s != null && s.tr != null) Destroy(s.tr.gameObject);
            flying.Clear();
        }

        private static void DestroyList(List<Transform> list)
        {
            foreach (Transform t in list)
                if (t != null) Destroy(t.gameObject);
            list.Clear();
        }

        // ---------------- 音效 ----------------

        private void EnsureAudio()
        {
            if (audioBuilt) return;
            audioBuilt = true;

            GameObject go = new GameObject("P1NailSweep Audio");
            go.transform.SetParent(transform, false);
            audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f;    // 全局 2D，同原版 Audio Player Actor 2D

            shootClip = ResolveShootClip();
            if (shootClip == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 取不到 P1 横刺出剑音效，本轮静音");
        }

        private void PlaySound(AudioClip clip)
        {
            if (clip != null && audio != null) audio.PlayOneShot(clip);
        }

        // comb 预制体的 `Control/Antic` 里那个 AudioPlayerOneShotSingle（radiance_sword_shoot）
        private AudioClip ResolveShootClip()
        {
            GameObject comb = ResolveCombPrefab();
            if (comb == null) return null;

            foreach (PlayMakerFSM f in comb.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Control") continue;
                FsmState antic = f.Fsm.GetState("Antic");
                if (antic == null) continue;
                AudioPlayerOneShotSingle snd = antic.Actions.OfType<AudioPlayerOneShotSingle>().FirstOrDefault();
                if (snd != null && snd.audioClip != null) return snd.audioClip.Value as AudioClip;
            }
            return null;
        }

        private GameObject ResolveCombPrefab()
        {
            if (!EnsureCommands()) return null;
            FsmState combL = commands.Fsm.GetState("Comb L");
            if (combL == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 Comb L，取不到 comb prefab 音效");
                return null;
            }
            SpawnObjectFromGlobalPool act = combL.Actions.OfType<SpawnObjectFromGlobalPool>().FirstOrDefault();
            if (act == null || act.gameObject == null) return null;
            return act.gameObject.Value;
        }

        // Radiant Nail prefab 的引用在 Attack Commands 的 `CW Spawn` 里（同 P2NailStream）
        private GameObject ResolveNailPrefab()
        {
            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Attack Commands") continue;

                FsmState st = f.Fsm.GetState("CW Spawn");
                if (st == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 CW Spawn，拿不到飞剑 prefab");
                    continue;
                }

                SpawnObjectFromGlobalPool act = st.Actions.OfType<SpawnObjectFromGlobalPool>().FirstOrDefault();
                if (act == null || act.gameObject == null) continue;

                GameObject p = act.gameObject.Value;
                if (p != null) return p;
            }

            OrbRadiance.Instance.LogWarn("OrbRadiance: 没解析出 Radiant Nail prefab，P1 横刺立不了墙");
            return null;
        }

        private bool EnsureCommands()
        {
            if (commands != null && commands.Fsm != null) return true;

            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Attack Commands") continue;
                commands = f;
                break;
            }

            if (commands == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands FSM，P1 横刺音效取不到");
            return commands != null;
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
                                                 " 的 Control FSM，重打时 P1 横刺不会自动清");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

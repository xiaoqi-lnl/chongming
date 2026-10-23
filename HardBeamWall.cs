using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // HARD P2 光墙：在镜头外生成 6～10 道原版激光，并同时向画面内随机目标射击。
    // 发射前显示预警，控制偏转和碰撞；结束攻击时清理私有光束，不修改其他难度。


    internal class HardBeamWall : MonoBehaviour
    {
        // ---------------- 时间点（状态名，与 EyeBeamTrack 一套）----------------

        // P2 光墙那招的两个状态：命中任意一个就出一套随机墙（不再分左右）
        private static readonly string[] WallStates = { "Beam Sweep L 2", "Beam Sweep R 2" };

        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };
        private const string ArenaFsmOwner = "Boss Control";

        // ---------------- 抑制原版 P2 墙 ----------------

        private const string BypassEvent = "BEAM SWEEP BYPASS"; // Beam Sweeper 不认识 → 原版墙不启动

        // ---------------- 克隆的 Ascend Beam ----------------

        private const string TemplatePath = "Eye Beam Glow/Ascend Beam";
        private const string BeamFsmName = "Control";
        private const string BeamStartState = "Init";  // 起始态：收到 ANTIC 会丢
        private const float StartTimeout = 1f;          // 等克隆子 FSM 起步的上限（同 EyeBeamTrack）
        private const string CloneSuffix = " (hard wall)";

        // ---------------- 用户参数（2026-10-08 定；要调就改这里）----------------

        private const int MinCount = 6;              // 一次几道（下限）
        private const int MaxCount = 10;             // 一次几道（含上限；Random 使用 MaxCount + 1）
        private const float WarnTime = 1f;           // 预警（ANTIC 蓄力）到开火
        private const float FireTime = 0.7f;         // 开火（FIRE）持续
        private const float EndFade = 0.5f;          // END 之后等 "Beam End" 淡出再销毁
        private const float MaxRunTime = 10f;        // 看门狗：协程异常中断时兜底收尾的上限
        private const float BeamLength = 400f;       // 光墙激光世界长度（模板 scale.x=80 时 ≈141）
        private const float BeamUnitLen = 1.765625f; // 模板 BoxCollider2D.size.x：scale.x=1 对应的世界长
        private const float BeamWidth = 1.0f;        // 光墙激光世界粗细（模板 scale.y=2.78 时 ≈1.40）
        private const float BeamUnitThick = 0.5030646f; // 模板 BoxCollider2D.size.y：scale.y=1 对应的世界粗

        // ---------------- 摆位（2026-10-08；「根部锚在画面外一圈」）----------------

        // 为什么：HK 激光 prefab 的枢轴在**根部**——collider 的 offset.x = size.x/2，占本地 x∈[0, size.x]，
        //   即从枢轴只往本地 +x 单向伸出。所以只要把根部丢到「可见矩形」**外面** OutMargin，梁就一定是
        //   「从画面外射进来、从对面穿出去」，两端都出屏（以前在场地里随机摆根部：朝外的那半场没覆盖，
        //   根部断面还悬在场中，就是「能看到光柱末端」那个问题）。可见矩形每趟现取（见 ViewRect）；
        //   瞄点取矩形内缩 AimInset 后的一点，方向 = 瞄点 - 根部。
        private const float OutMargin = 20f;         // 根部落在可见矩形外多少世界单位（>0 即在屏外）
        private const float AimInset = 3f;           // 瞄点相对可见矩形内缩多少（免得梁贴着边擦过去）
        private const float MinGap = 2.5f;           // 两根本部（发射点）最小间距，免得叠在一起

        // 相机取不到可用矩形时的兜底：以英雄为中心、固定半宽的可见框（实测辐光场地一屏约 31×17.6）
        private const float ViewHalfW = 16f;
        private const float ViewHalfH = 9f;

        // ---------------- 运行时 ----------------

        private PlayMakerFSM control;     
        private PlayMakerFSM choices;     // Absolute Radiance/Attack Choices：招式触发
        private bool choicesTried;

        private PlayMakerFSM arenaFsm;    // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private Transform template;                          // Eye Beam Glow/Ascend Beam（克隆模板）
        private readonly List<GameObject> live = new List<GameObject>();
        private Coroutine running;
        private float runStart;              // 本次协程起手时间（看门狗用）
        private int runGeneration;
        private bool subscribed;             // 已订阅 choices.Fsm.StateChanged

        private static HardBeamWall active;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
            active = this;
            EnsureChoices();
            Subscribe();
        }

        // 总开关关掉时 OrbRadiance.RestorePatched 走这里收尾
        internal static void Restore()
        {
            if (active != null) active.Teardown();
        }

        // ---------------- 抑制原版 P2 光墙（原位改名，不增删动作）----------------

        internal static void PatchSuppress(Fsm fsm)
        {
            int n = 0;
            foreach (string name in WallStates)
            {
                FsmState st = fsm.GetState(name);
                if (st == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Choices 里没有 " + name + " 状态");
                    continue;
                }
                SendEventByName send = st.Actions.OfType<SendEventByName>()
                    .FirstOrDefault(a => a != null && a.sendEvent != null);
                if (send == null || send.sendEvent == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + name +
                                                 " 里没有带 sendEvent 的 SendEventByName");
                    continue;
                }
                if (!OrbSpawner.OriginalSendEvent.ContainsKey(send))
                    OrbSpawner.OriginalSendEvent[send] = send.sendEvent.Value;
                send.sendEvent.Value = BypassEvent;
                n++;
            }
            OrbRadiance.Instance.Log("[hardbeam] 原版 P2 光墙广播已改名抑制：" + n + " 处 → " + BypassEvent);
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // 总开关关掉：拆掉克隆（原版广播会由 RestorePatched 写回）
            if (!OrbRadiance.Settings.Enabled)
            {
                Teardown();
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同场景重来，Control 不会自己回 Init
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) Teardown();
            }

            // 招式触发不在这里逐帧采样（见 Subscribe 的注释），改由 StateChanged 回调

            // 看门狗：协程若被异常中断，running 会卡住不清 → 整套 hard 光墙永久废掉、
            // 克隆梁也烂在场上。正常预警、开火与收尾共 2.2s，另有克隆FSM启动等待；MaxRunTime 是兜底上限。
            if (running != null && Time.time - runStart > MaxRunTime)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: [hardbeam] 一趟超时，强制收尾");
                Teardown();
            }
        }

        // ---------------- 一次随机墙 ----------------

        private void Begin()
        {
            if (!ResolveTemplate()) return;
            // 上一趟还没放完（招式最短约 2.0s 一次，比预警、开火、收尾固定共 2.2s 更快；另有启动等待）→ 直接顶掉重放，
            // 保证「每出一次这招就有一套墙」，而不是隔一次才有一套
            runGeneration++;
            int ticket = runGeneration;
            if (running != null) { StopCoroutine(running); running = null; }
            DestroyLive();               // 清掉上一趟可能残留的克隆，免得叠起来
            runStart = Time.time;
            running = StartCoroutine(Run(ticket));
        }

        private IEnumerator Run(int ticket)
        {
            // 先让出一帧：保证协程不会同步跑完（否则 StartCoroutine 返回前 finally 就把
            // running 清了，回到 Begin 又把一个已结束的协程赋给 running → 永久卡住）
            yield return null;
            if (ticket != runGeneration) yield break;

            try
            {
                int want = UnityEngine.Random.Range(MinCount, MaxCount + 1);
                if (!ViewRect(out float vx0, out float vx1, out float vy0, out float vy1))
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: [hardbeam] 取不到主摄像机，" +
                                                 "没法把激光锚到画面外，本趟跳过");
                    yield break;
                }
                List<Spawn> spawns = PickSpawns(want, vx0, vx1, vy0, vy1);

                var fsms = new List<PlayMakerFSM>();
                foreach (Spawn sp in spawns)
                {
                    GameObject go;
                    try
                    {
                        go = Instantiate(template.gameObject, transform.parent);
                    }
                    catch (Exception e)
                    {
                        OrbRadiance.Instance.LogError("OrbRadiance: 克隆 Ascend Beam 抛异常 " + e);
                        continue;
                    }
                    if (go == null) continue;

                    go.name = template.name + CloneSuffix;
                    // 根部摆在屏外、朝向瞄点（梁从根部沿本地 +x 单向伸出）
                    go.transform.position = sp.Pos;
                    go.transform.rotation = Quaternion.Euler(0f, 0f, sp.Angle);
                    // 长度：模板 localScale.x 就是梁长（原 80 → 世界长≈141）→ 换成 BeamLength
                    // 粗细：同理 localScale.y → BeamWidth（sc.z 2D 用不到，保持模板值）
                    Vector3 sc = go.transform.localScale;
                    go.transform.localScale = new Vector3(BeamLength / BeamUnitLen,
                                                          BeamWidth / BeamUnitThick, sc.z);
                    go.SetActive(true);
                    live.Add(go);

                    PlayMakerFSM f = go.GetComponents<PlayMakerFSM>()
                        .FirstOrDefault(p => p != null && p.Fsm != null && p.FsmName == BeamFsmName);
                    if (f != null) fsms.Add(f);
                }

                if (fsms.Count == 0)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: hard 光墙没造出可用克隆（模板缺 Control FSM？）");
                    yield break;
                }

                OrbRadiance.Instance.Log("[hardbeam] 随机光墙起手：" + fsms.Count + " 道（要 " + want +
                                         "），根部在屏外 " + OutMargin + "（可见 x " + vx0.ToString("F1") +
                                         ".." + vx1.ToString("F1") + " / y " + vy0.ToString("F1") +
                                         ".." + vy1.ToString("F1") + "），预警 " + WarnTime +
                                         "s / 激光 " + FireTime + "s / 长 " + BeamLength +
                                         " / 粗 " + BeamWidth);

                // 等所有克隆的子 FSM 起步并离开 Init（同帧发的事件会被 Init 吃掉）
                float deadline = Time.time + StartTimeout;
                while (Time.time < deadline)
                {
                    if (ticket != runGeneration) yield break;
                    bool ready = true;
                    foreach (PlayMakerFSM f in fsms)
                        if (f == null || f.Fsm == null || !f.Fsm.Started ||
                            f.Fsm.ActiveStateName == BeamStartState) { ready = false; break; }
                    if (ready) break;
                    yield return null;
                }
                if (ticket != runGeneration) yield break;

                // 同时预警
                SendAll(fsms, "ANTIC");
                EyeBeamTrack.PlayP1ColumnSound(false);
                yield return new WaitForSeconds(WarnTime);
                if (ticket != runGeneration) yield break;

                // 同时开火
                SendAll(fsms, "FIRE");
                EyeBeamTrack.PlayP1ColumnSound(true);
                yield return new WaitForSeconds(FireTime);
                if (ticket != runGeneration) yield break;

                // 收尾
                SendAll(fsms, "END");
                yield return new WaitForSeconds(EndFade);

                OrbRadiance.Instance.Log("[hardbeam] 随机光墙收束（正常放完）");
            }
            finally
            {
                // 无论正常结束、被 StopCoroutine 拆、还是中途抛异常，都保证拆干净、
                // running 一定清掉（否则一次「连放」就能把整套 hard 光墙永久废掉）
                if (ticket == runGeneration)
                {
                    DestroyLive();
                    running = null;
                }
            }
        }

        // 一次摆位的产物：根部世界坐标 + 朝向（z 角，度）
        private struct Spawn
        {
            public Vector3 Pos;
            public float Angle;
        }

        // 在可见矩形的**外扩框**上随机挑 n 个根部（两两间距 ≥ MinGap），各朝框里一个随机瞄点打。
        // 拒绝采样；放不下就少放几道。
        private static List<Spawn> PickSpawns(int n, float x0, float x1, float y0, float y1)
        {
            // 根部外扩一圈 ⇒ 必然在屏外；瞄点内缩一点 ⇒ 别让梁贴着边擦过去
            float ox0 = x0 - OutMargin, ox1 = x1 + OutMargin;
            float oy0 = y0 - OutMargin, oy1 = y1 + OutMargin;
            float ix0 = x0 + AimInset,  ix1 = x1 - AimInset;
            float iy0 = y0 + AimInset,  iy1 = y1 - AimInset;
            if (ix1 <= ix0 || iy1 <= iy0) { ix0 = x0; ix1 = x1; iy0 = y0; iy1 = y1; }

            var list = new List<Spawn>();
            for (int i = 0; i < n; i++)
            {
                Vector2 b = Vector2.zero;
                bool ok = false;
                for (int t = 0; t < 50; t++)
                {
                    b = RandomOnPerimeter(ox0, ox1, oy0, oy1);
                    ok = true;
                    foreach (Spawn e in list)
                        if ((new Vector2(e.Pos.x, e.Pos.y) - b).sqrMagnitude < MinGap * MinGap)
                        { ok = false; break; }
                    if (ok) break;
                }
                if (!ok) continue;

                Vector2 aim = new Vector2(UnityEngine.Random.Range(ix0, ix1),
                                          UnityEngine.Random.Range(iy0, iy1));
                float ang = Mathf.Atan2(aim.y - b.y, aim.x - b.x) * Mathf.Rad2Deg;
                list.Add(new Spawn { Pos = new Vector3(b.x, b.y, 0f), Angle = ang });
            }
            return list;
        }

        // 在矩形边界上按周长均匀取一点
        private static Vector2 RandomOnPerimeter(float x0, float x1, float y0, float y1)
        {
            float w = x1 - x0, h = y1 - y0;
            float t = UnityEngine.Random.Range(0f, 2f * (w + h));
            if (t < w) return new Vector2(x0 + t, y0);
            t -= w;
            if (t < h) return new Vector2(x1, y0 + t);
            t -= h;
            if (t < w) return new Vector2(x1 - t, y1);
            t -= w;
            return new Vector2(x0, y1 - t);
        }

        // 关卡相机当前可见的世界矩形（画面会随战斗平移/缩放，所以每趟现取）。
        
        // ⚠ 不能用 `orthographicSize`：关卡相机是 `tk2dCamera`，它给 Unity Camera 塞了**自定义投影矩阵**
        //   （`tk2dCamera.UpdateCameraMatrix` 里 `unityCamera.projectionMatrix = …`），所以 orthographicSize
        //   是废数（实测读到 ≈480，算出的矩形 1700×960，激光全被摆到天外去了）。`Camera.main` 也不一定是
        //   这台（实测那台给的是背景相机）。所以先拿 `GameCameras.instance.mainCamera`，把**视口角点反投影**
        //   回世界坐标（`ScreenToWorldPoint` 走的就是实际投影矩阵；z 要传「相机到 z=0 游戏平面」的距离，
        //   传 0 会退化到相机自身平面 / 透视下直接返回相机点）。
        //   取到的矩形还要过一遍合理性检查（宽高要像个可见框，否则就是那台背景相机）；不通过就以英雄
        //   为中心取固定大小的兜底框——HK 关卡相机在锁区里是贴着英雄的（ModLog 实测 cam==hero）。
        private static bool ViewRect(out float x0, out float x1, out float y0, out float y1)
        {
            x0 = x1 = y0 = y1 = 0f;

            Camera cam = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
            if (cam != null && cam.pixelWidth > 0 && cam.pixelHeight > 0)
            {
                Rect pr = cam.pixelRect;                 // 视口（有黑边时就不是整屏）
                float dz = -cam.transform.position.z;    // 相机到 z=0 游戏平面的距离
                if (dz <= 0f) dz = 1f;
                Vector3 bl = cam.ScreenToWorldPoint(new Vector3(pr.xMin, pr.yMin, dz));
                Vector3 tr = cam.ScreenToWorldPoint(new Vector3(pr.xMax, pr.yMax, dz));
                float w = tr.x - bl.x, h = tr.y - bl.y;
                if (w > 10f && w < 80f && h > 5f && h < 60f)
                {
                    x0 = bl.x; x1 = tr.x; y0 = bl.y; y1 = tr.y;
                    OrbRadiance.Instance.Log("[hardbeam] 可见框来自相机 " + cam.name +
                                             "（" + w.ToString("F1") + "×" + h.ToString("F1") + "）");
                    return true;
                }
                OrbRadiance.Instance.LogWarn("OrbRadiance: [hardbeam] 相机 " + cam.name +
                                             " 反投影出来不可用（" + w.ToString("F1") + "×" +
                                             h.ToString("F1") + "），改用英雄兜底框");
            }

            // 兜底：以英雄为中心取固定大小的可见框
            HeroController hc = HeroController.instance;
            if (hc == null) return false;
            Vector3 hp = hc.transform.position;
            x0 = hp.x - ViewHalfW; x1 = hp.x + ViewHalfW;
            y0 = hp.y - ViewHalfH; y1 = hp.y + ViewHalfH;
            OrbRadiance.Instance.Log("[hardbeam] 可见框来自英雄兜底（hero=" + hp.x.ToString("F1") +
                                     "," + hp.y.ToString("F1") + " ±" + ViewHalfW + "/" + ViewHalfH + "）");
            return true;
        }

        private static void SendAll(List<PlayMakerFSM> fsms, string evt)
        {
            foreach (PlayMakerFSM f in fsms)
                if (f != null && f.Fsm != null) f.SendEvent(evt);
        }

        // ---------------- 造 / 拆 ----------------

        private void DestroyLive()
        {
            int n = 0;
            foreach (GameObject g in live)
            {
                if (g == null) continue;   // 场景卸载 / 已被销毁（Unity 假 null）
                Destroy(g);
                n++;
            }
            live.Clear();
            if (n > 0) OrbRadiance.Instance.Log("[hardbeam] 已拆 " + n + " 道克隆梁");
        }

        private void Teardown()
        {
            runGeneration++;
            if (running != null) { StopCoroutine(running); running = null; }
            DestroyLive();
        }

        // ---------------- 找对象 ----------------

        private bool ResolveTemplate()
        {
            if (template != null) return true;

            // Transform.Find 不看 active：模板在 P4 会被 Set Final Orbs 关掉，也能找到
            template = transform.Find(TemplatePath);
            if (template == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + TemplatePath + "，hard 光墙放不出");
            return template != null;
        }

        private bool EnsureChoices()
        {
            if (choices != null && choices.Fsm != null) return true;
            if (choicesTried) return false;
            choicesTried = true;

            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Attack Choices") continue;
                choices = f;
                break;
            }

            if (choices == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Choices FSM，hard 光墙放不出");
            return choices != null;
        }

        // ---------------- 订阅招式触发（P2 光墙那招）----------------

        // PlayMaker 会同帧连跳多状态（`Return → A2 Choice → Beam Sweep L 2` 在同一帧走完，
        // ModLog 里这些状态共享同一个 t）。逐帧采样 ActiveStateName，上一帧和这一帧看到的
        // 可能是同一状态、边沿永远不回落 ⇒ 只有第一趟会起手、后面这招全成了空放。
        // 改成订阅 StateChanged，一进 L 2 / R 2 就来一趟（同 §20 对 P1NailSweep 的修法）。
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
            if (Array.IndexOf(WallStates, state.Name) < 0) return;
            Begin();
        }

        private void OnDestroy()
        {
            if (subscribed && choices != null && choices.Fsm != null)
                choices.Fsm.StateChanged -= OnStateChanged;
            subscribed = false;
            Teardown();
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
                                                 " 的 Control FSM，重打时 hard 光墙不会自动拆");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

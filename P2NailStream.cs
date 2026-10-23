using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // HARD P2 两侧交替发射飞剑；Z/N 保留和维护原版平台。
    // P3 恢复 P2 平台并维护 Z/N 攀爬平台；进入 P4 后仅保留顶部目标平台。


    internal class P2NailStream : MonoBehaviour
    {
        // ---------------- 时间点（状态名）----------------

        private const string StateStart = "Arena 2 Start";
        private const string StateP3 = "Ascend Tele";     // P2 结束 → 恢复 P2 平台 + 切 P3 模式
        private const string StateEnd = "Scream";         // P4 开始 → 停火清场（瞬态，靠 StateChanged）

        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };

        private const string ArenaFsmOwner = "Boss Control";

        // 「辐光放横刺 / 脸刺期间不生成飞剑」要监视的 Attack Choices 状态（2026-10-02 用户定，含前摇）。
        //   横刺 = NAIL LR SWEEP → L or R Choice → Nail L/R Sweep 2（各 Wait 3.5s）
        //   脸刺 = NAIL FAN → Nail Fan Wait → Nail Fan → Nail Fan Recover
        // 只在 P2 用得到（`* 2` / `Nail Fan*` 都在 A2 Choice 的招池里）。
        private static readonly string[] BlockStates =
        {
            "L or R Choice",
            "Nail L Sweep 2",
            "Nail R Sweep 2",
            "Nail Fan Wait",
            "Nail Fan",
            "Nail Fan Recover",
        };

        // ---------------- 发射 ----------------

        // 场地左右外侧的两个发射 x（用户定）。左 = 34、右 = 85。
        private const float SpawnXLeft = 34f;
        private const float SpawnXRight = 85f;

        // 发射高度 y 的随机区间（用户定）。场地地板顶面 20、外墙顶面 67.63，
        // 34~61 是往里收过一圈的可活动高度带。
        private const float SpawnYMin = 34f;
        private const float SpawnYMax = 61f;

        // P3 攀爬段高度夹（照抄天国余晖 P3NailWallSpawnAction）：
        // 玩家 y 低于下界（还没爬起来）或到上界（已到顶）时不生成。
        private const float ClimbBottom = 35f;
        private const float ClimbTop = 150f;

        // P3 玩家 y>64 时撤除普通平台；Z/N 保留并维护 Climb Set / Ascend Set，H 只保留 P4 顶端平台。
        // 一次性闩锁（过 64 后不再重建），死亡重打时复位。
        private const float PlatKillY = 64f;

        // 左右交替的间隔（用户定 0.5s）：一发打完 0.5s 再打另一侧。
        private const float Interval = 0.5f;

        // 原版 `Fire CW` / `Fire CCW` 的恒定线速度是 25，这里取它的 1.2 倍。
        // （2026-10-01 先在 1.5 倍 = 37.5 上测过、用户改回 1 倍 = 25；随后定为 1.2 倍 = 30。）
        private const float FlightSpeed = 30f;

        // --- P3 专用（2026-10-06 从上面两项拆出，避免 P3 参数影响 P2）---
        // P3 发射间隔 0.3s（P2 为 0.5s）；线速度当前均为 30。
        private const float P3Interval = 0.3f;
        private const float P3FlightSpeed = 30f;

        // P3 生成 y 在玩家当前高度上的随机抖动幅度（用户 2026-10-06 定 ±7 世界单位）：
        // 免得每发都精确贴玩家高度。门控（ClimbBottom/ClimbTop）看的是**玩家高度**，抖动不改门控。
        private const float P3YJitter = 7f;

        // 生成后到开始飞行的瞄准时间（用户定 0.01s）。锁方向是当帧做的，
        // 这个值只决定「停在生成点多久才开始动」——留成常量是为了以后想加前摇时能直接调。
        private const float AimTime = 0.01f;

        // 出界销毁：发射点在 34/85，场地横跨 48.22~72.95，所以框要留得比场地宽。
        // 剑往任意方向飞，横竖都会先撞出这个框，不会留下永不回收的实例。
        // y 上限抬到 175：P3 攀爬顶 150 之上还得留余量，否则从高处射出的剑一冒头就被回收。
        private static readonly Vector2 BoundsMin = new Vector2(15f, 5f);
        private static readonly Vector2 BoundsMax = new Vector2(104f, 175f);

        // ---------------- 伤害 ----------------

        private const int NailDamage = 2;
        private const int NailHazardType = 0;

        // 层 11（PhysLayers.ENEMIES）：能伤到玩家（11 × 20 Hero Box 的矩阵原版就开着），
        // 同时玩家下劈能弹开它（`NailSlash` 的下劈分支硬判层号 11）—— 和前身环刺一致的口径。
        // 想换成「劈不动」就把它写回 prefab 出厂值 17。
        private const int LayerProjectile = 11;

        // ---------------- 场地 ----------------

        private const string PlatsRoot = "Boss Control/Plat Sets";

        // P2 常规撤除组；P3 时 Climb Set / Ascend Set 的保留与维护按 Z/N/H 模式分别处理。
        private static readonly string[] RemoveGroups = { "P2 SetA", "Hazard Plat" };

        private const string PlatPrefix = "Radiant Plat";
        private const string PlatFsmName = "radiant_plat";

        // `Ascend Set` 最顶那块是 P4 战唯一站的平台（天国余晖叫它「P4 唯一顶端平台」，
        // `AddP4SpikesAction` 里认的就这个名字）。y>64 撤全部平台时**跳过它**，否则 P4 没得站。
        private const string P4PlatGroup = "Ascend Set";
        private const string P4Plat = "Radiant Plat Small (11)";

        // 平台 FSM 认这三个事件：APPEAR / DISAPPEAR / SLOW VANISH
        private const string VanishEvent = "DISAPPEAR";
        private const string AppearEvent = "APPEAR";

        // ---------------- 运行时 ----------------

        private sealed class Shot
        {
            public Transform tr;
            public Vector2 dir;     // 生成那一刻锁死的飞行方向（单位向量）
            public float speed;     // 生成那一刻锁死的速度（P2/P3 各不同，锁死免得中途换模式变档）
            public float age;       // 生成后经过的时间，够 AimTime 才开始移动
        }

        private PlayMakerFSM control;               // Absolute Radiance/Control：撤平台 / 起停

        private PlayMakerFSM choices;               // Absolute Radiance/Attack Choices：读横刺/脸刺是否在放
        private bool warnedNoChoices;

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private GameObject nailPrefab;
        private float nailZ;

        private readonly List<Shot> shots = new List<Shot>();
        private bool streaming;
        private float streamT;
        private bool fireLeft = true;               // 下一发打哪边（左右交替）

        private bool p3Mode;                        // 进 P3 后的发射模式（y 跟玩家当前高度）
        private bool platsGone;                     // 玩家 y 过 64 的一次性闩锁：所有平台已撤、不再维护
        private readonly List<PlayMakerFSM> p2PlatFsms = new List<PlayMakerFSM>();  // P3 里恢复的那 7 块
        private readonly List<PlayMakerFSM> p3ClimbPlatFsms = new List<PlayMakerFSM>();
        private bool normalP2PlatformsKept;

        private bool subscribed;                    // 已订阅 control.Fsm.StateChanged
        private string lastState;

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
        }

        // `Scream`（P4 起点）是个瞬态：8 个动作里没有一个 Wait，逐帧采样采不到，
        // 所以「P4 开始就停火」这条只能靠状态进入事件（理由同 P1NailSweep 的 Subscribe）。
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
            if (state.Name == StateEnd)
            {
                bool climbP3Ended = p3Mode && IsClimbPlatformMode();
                StopStream();
                if (climbP3Ended)
                {
                    // Z/N P3 在 y=64 以上保留攀爬路径；进入 P4 的瞬间
                    // 按既定规则撤除多余平台，只留下 Ascend Set/Small (11)。
                    p3ClimbPlatFsms.Clear();
                    VanishAllPlats();
                }
            }
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // 总开关关闭：停止发射并清理弹体；停止本组件的平台维护，不主动重建平台。
            if (!OrbRadiance.Settings.Enabled)
            {
                StopStream();
                lastState = null;
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同一场景内重来，Absolute Radiance/Control 不会自己回到 Init
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena))
                {
                    StopStream();
                    normalP2PlatformsKept = false;
                    p2PlatFsms.Clear();
                    p3ClimbPlatFsms.Clear();
                }
            }

            // 模式可能在不重新进入 P2 的情况下变化；清理旧弹体，但保留
            // 流状态和 P3 平台维护状态，避免 Z/N 平台停止更新。
            if (!P2PlatformSpikePlan.ShouldEmitNails(OrbRadiance.EffectiveHardMode) && shots.Count > 0)
                ClearShots();

            string cur = control.Fsm.ActiveStateName;
            if (cur != lastState)
            {
                lastState = cur;

                if (cur == StateStart)
                {
                    normalP2PlatformsKept = !OrbRadiance.EffectiveEasyMode && !OrbRadiance.EffectiveHardMode;
                    p2PlatFsms.Clear();
                    if (P2PlatformSpikePlan.ShouldHideVanillaPlatforms(OrbRadiance.EffectiveEasyMode,
                                                                        OrbRadiance.EffectiveHardMode))
                        VanishP2Plats();    // HARD 撤台；NORMAL 留原版七块，EASY 保持原流程
                    else if (normalP2PlatformsKept)
                        CaptureP2Plats(true); // NORMAL 原生初态是 Out，显式发 APPEAR 并维护平台
                    StartStream();
                }
                else if (cur == StateP3) EnterP3();
            }

            if (p3Mode)
            {
                // 玩家 y>64：撤 P2 平台；Z/N 保留攀爬平台，H 延续原撤台规则。
                if (!platsGone && HeroY() > PlatKillY)
                {
                    VanishAllPlats();
                    platsGone = true;
                }
                if (!platsGone) MaintainP2Plats();
                if (IsClimbPlatformMode()) MaintainP3ClimbPlats();
            }
            else if (streaming && normalP2PlatformsKept)
            {
                MaintainP2Plats();
            }

            if (streaming && P2PlatformSpikePlan.ShouldEmitNails(OrbRadiance.EffectiveHardMode))
            {
                // 辐光正在放横刺 / 脸刺（含前摇）时不生成：计时清零，离招后重新从 Interval 起算，
                // 不会一解禁就补射一串。已经在飞的飞剑照常飞完（只停「生成新的」）。
                if (Busy())
                {
                    streamT = 0f;
                }
                else
                {
                    float iv = p3Mode ? P3Interval : Interval;
                    streamT += Time.deltaTime;
                    if (streamT >= iv)
                    {
                        // 卡帧后不要补射一串：跨过两个间隔就直接把计时清掉
                        streamT -= iv;
                        if (streamT > iv) streamT = 0f;
                        if (FireOne(fireLeft)) fireLeft = !fireLeft;
                    }
                }
            }

            if (P2PlatformSpikePlan.ShouldEmitNails(OrbRadiance.EffectiveHardMode))
                DriveShots(Time.deltaTime);
        }

        // ---------------- 起停 ----------------

        private void StartStream()
        {
            StopStream();

            nailZ = transform.position.z;
            streamT = 0f;
            fireLeft = true;
            if (!P2PlatformSpikePlan.ShouldEmitNails(OrbRadiance.EffectiveHardMode))
            {
                nailPrefab = null;
                streaming = true; 
                p3Mode = false;
                OrbRadiance.Instance.Log("[nailstream] N/Z P2飞剑已停用；保留平台阶段与维护");
                return;
            }

            nailPrefab = ResolveNailPrefab(gameObject);
            if (nailPrefab == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: CW Spawn 没解析出飞剑 prefab" +
                                             "（缺 Attack Commands / CW Spawn / SpawnObjectFromGlobalPool），P2 飞剑流没开");
                return;
            }
            OrbRadiance.Instance.Log("[nailstream] 飞剑 prefab = " + nailPrefab.name);

            nailZ = transform.position.z;
            streaming = true;
            p3Mode = false;
            streamT = 0f;
            fireLeft = true;

            OrbRadiance.Instance.Log("[nailstream] P2 飞剑开火：每 " + Interval + "s 左右交替 1 发（左先），" +
                                     "x=" + SpawnXLeft + " / " + SpawnXRight + "，y 每发在 " +
                                     SpawnYMin + "~" + SpawnYMax + " 随机，锁玩家方向后 " + FlightSpeed +
                                     " u/s 直线飞（瞄准 " + AimTime + "s，= 原版 Fire 线速度）");
        }

        private void StopStream()
        {
            streaming = false;
            p3Mode = false;
            platsGone = false;      // 闩锁复位（重打 / 停火都走这里）
            streamT = 0f;

            ClearShots();
        }

        private void ClearShots()
        {
            if (shots.Count == 0) return;

            int n = 0;
            foreach (Shot s in shots)
            {
                if (s.tr == null) continue;     // 场景卸载 / 已被销毁（Unity 假 null）
                Destroy(s.tr.gameObject);
                n++;
            }
            shots.Clear();
            if (n > 0) OrbRadiance.Instance.Log("[nailstream] 清掉在飞的飞剑 " + n + " 枚");
        }

        // ---------------- 横刺 / 脸刺期间暂停生成 ----------------

        // Attack Choices 在 `Absolute Radiance` 上（和 Control / Attack Commands 同一物体），
        // 惰性解析一次。读它的 ActiveStateName 判断辐光是否正在放横刺 / 脸刺（见 BlockStates）。
        private bool Busy()
        {
            if (choices == null)
            {
                foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
                {
                    if (f == null || f.Fsm == null || f.FsmName != "Attack Choices") continue;
                    choices = f;
                    break;
                }

                if (choices == null)
                {
                    if (!warnedNoChoices)
                    {
                        warnedNoChoices = true;
                        OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Choices FSM，" +
                                                     "横刺/脸刺期间不生成飞剑这条不生效");
                    }
                    return false;
                }
            }

            if (choices.Fsm == null) return false;
            return Array.IndexOf(BlockStates, choices.Fsm.ActiveStateName) >= 0;
        }

        // ---------------- 发射 ----------------

        // 返回 true = 真的射了一发（决定左右交替要不要翻面）。P3 里玩家还没爬到高度带时
        // 会返回 false —— 这时不翻面，等爬上来还是从「该打的那一侧」开始。
        private bool FireOne(bool left)
        {
            if (!P2PlatformSpikePlan.ShouldEmitNails(OrbRadiance.EffectiveHardMode)) return false;
            HeroController hc = HeroController.instance;
            if (hc == null) return false;

            Vector2 hero = hc.transform.position;
            float y;
            if (p3Mode)
            {
                // [P3 飞剑停用 2026-10-06] 用户要求把 P3 飞剑相关代码注释掉 —— P3 期间不再生成飞剑。
                // 恢复：删掉下面这行 return，再把两行注释解开。
                return false;
                // if (hero.y < ClimbBottom || hero.y >= ClimbTop) return false;
                // y = hero.y + UnityEngine.Random.Range(-P3YJitter, P3YJitter);
            }
            else
            {
                y = UnityEngine.Random.Range(SpawnYMin, SpawnYMax);
            }

            Spawn(left ? SpawnXLeft : SpawnXRight, y, hero);
            return true;
        }

        private void Spawn(float x, float y, Vector2 hero)
        {
            if (!P2PlatformSpikePlan.ShouldEmitNails(OrbRadiance.EffectiveHardMode) || nailPrefab == null) return;
            Vector2 origin = new Vector2(x, y);

            // 瞄准：用生成当帧的小骑士位置定方向，之后不再更新
            Vector2 d = hero - origin;
            if (d.sqrMagnitude < 0.0001f) d = new Vector2(x < SpawnXRight ? 1f : -1f, 0f);
            d.Normalize();

            GameObject nail = null;
            try
            {
                nail = Instantiate(nailPrefab, new Vector3(origin.x, origin.y, nailZ), Quaternion.identity);
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: Instantiate 飞剑抛异常 " + e);
            }

            if (nail == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 飞剑没生成，P2 飞剑流断了一发");
                return;
            }

            nail.SetActive(true);
            StripAndArm(nail);

            // 钉子贴图沿 local +Y 指（原版 Fire 段 `FaceAngle(angleOffset = -90)`），
            // 所以朝向 = 飞行方向角 - 90°。
            nail.transform.eulerAngles = new Vector3(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg - 90f);

            shots.Add(new Shot
            {
                tr = nail.transform,
                dir = d,
                speed = p3Mode ? P3FlightSpeed : FlightSpeed,
                age = 0f,
            });
        }

        // 毁掉钉子自带的 FSM + 打开判定 + 补伤害（见文件头）
        // P1 横刺立墙也复用（P1NailSweep.BuildWall），故 internal。
        internal static void StripAndArm(GameObject go)
        {
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
            {
                if (f == null) continue;
                f.enabled = false;      // 当帧就停，Destroy 是帧末才生效
                Destroy(f);
            }

            // 原版飞行时不播任何动画（`Fire CW` / `Fire CCW` / `Fan Ready` / `Fly` 里一个动画动作都没有），
            // 显示的是 `Nail Antic` 播完停住的最后一帧 —— 那个 clip 是 wrapMode=Once 的 8 帧、末帧
            // spriteId 7 = `attack_air_spear007`。我们毁了 FSM 就没人播它，会露出 prefab 序列化的静态帧
            // （spriteId 17 = `attack_air_spear008`，也就是只有一帧的 `Nail Fly` clip），所以自己补一次。
            tk2dSpriteAnimator anim = go.GetComponent<tk2dSpriteAnimator>();
            if (anim != null) anim.Play("Nail Antic");

            // FSM 一毁，prefab 上那个 m_Enabled = 0 的 PolygonCollider2D 就没人打开了
            // （原来是 `Set Collider` 状态的 SetPolygonCollider 动作开的）→ 自己开
            Collider2D col = go.GetComponent<Collider2D>();
            if (col != null) col.enabled = true;

            go.layer = LayerProjectile;

            NonBouncer nb = go.GetComponent<NonBouncer>();
            if (nb != null)
            {
                nb.active = false;      // 置假当帧生效（Destroy 要等帧末）
                Destroy(nb);
            }

            // 盖戳：本 mod 的飞剑是 hazardType 0 的不可挡直伤，形状上跟 VoidKill 虚空黑名单要拦的
            // 「非 1 伤害」一模一样 → 会被误判成掉虚空秒杀。VoidKill.OnHeroTakeDamage 认这个标记放行。
            if (go.GetComponent<NailProjectile>() == null)
                go.AddComponent<NailProjectile>();

            // 伤害：`HeroBox.CheckForDamage` 先看物体上有没有名为 `damages_hero` 的 FSM（有就只认
            // 那个 FSM 的 int），没有才读 DamageHero 组件。钉子上的 FSM 被全毁了，所以统一按
            // DamageHero 走 = damageDealt 2 / hazardType 0（照天国余晖 P2 平台地刺）。
            // hazardType 0 的代价：无忧旋律 / 巴德尔之壳躲不掉，原版黑冲 / 黑砸保护也一起失效
            // （`HeroController.TakeDamage` 里那三处判定都带 `hazardType == 1`）—— 这是刻意的，用户定；
            // 这里连 `shadowDashHazard` 一起置假，免得黑冲在 `HeroBox.CheckForDamage` 里整出个例外。
            DamageHero dh = go.GetComponent<DamageHero>();
            if (dh == null) dh = go.AddComponent<DamageHero>();
            dh.damageDealt = NailDamage;
            dh.hazardType = NailHazardType;
            dh.shadowDashHazard = false;
            dh.resetOnEnable = false;   // 免得被重新激活时按 prefab 初值抹回 damageDealt

            // 位移由我们用 transform 做，别让 Rigidbody2D 再插一脚（弹开 / 掉落）
            Rigidbody2D rb = go.GetComponent<Rigidbody2D>();
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = Vector2.zero;
                rb.isKinematic = true;
            }
        }

        // Radiant Nail prefab 的引用在 Attack Commands 的 `CW Spawn` 里（Global 变量）。
        // P2 飞剑流与 P3 脸刺（P3NailFan）共用这一处解析；找不到返回 null。
        internal static GameObject ResolveNailPrefab(GameObject owner)
        {
            foreach (PlayMakerFSM f in owner.GetComponents<PlayMakerFSM>())
            {
                if (f.Fsm == null || f.FsmName != "Attack Commands") continue;

                FsmState st = f.Fsm.GetState("CW Spawn");
                if (st == null) continue;

                SpawnObjectFromGlobalPool act = st.Actions.OfType<SpawnObjectFromGlobalPool>().FirstOrDefault();
                if (act == null || act.gameObject == null) continue;

                GameObject p = act.gameObject.Value;
                if (p != null) return p;
            }
            return null;
        }

        // ---------------- 飞行 ----------------

        private void DriveShots(float dt)
        {
            if (!P2PlatformSpikePlan.ShouldEmitNails(OrbRadiance.EffectiveHardMode)) return;
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                Shot s = shots[i];
                if (s.tr == null) { shots.RemoveAt(i); continue; }

                s.age += dt;
                if (s.age >= AimTime)
                {
                    Vector3 p = s.tr.position;
                    p.x += s.dir.x * s.speed * dt;
                    p.y += s.dir.y * s.speed * dt;
                    s.tr.position = p;
                }

                Vector3 q = s.tr.position;
                if (q.x < BoundsMin.x || q.x > BoundsMax.x || q.y < BoundsMin.y || q.y > BoundsMax.y)
                {
                    Destroy(s.tr.gameObject);
                    shots.RemoveAt(i);
                }
            }
        }

        // ---------------- P2 平台（发 DISAPPEAR，不销毁）----------------

        // 撤的方式是**发 `DISAPPEAR` 事件**，不是 Destroy。平台自带 FSM `radiant_plat`
        // （Start `Init` → `Out` → `Appear 1/2` → `Idle` / `Disappeared`）本身就认
        // `APPEAR` / `DISAPPEAR` / `SLOW VANISH`：`Disappear` 状态立刻关掉子物体 `Colliders`
        // 并播消失动画，之后收到 `APPEAR` 还能回到 `Appear 1`。Destroy 掉的话，死亡重打时
        // `Climb Plats1` 再发 APPEAR 就没人接了 —— 爬升段会一块平台都没有。
        // 顺带，这正是 vanilla 自己在 `Arena 2 Start` 对 `Plats Climb` 做的事（同一事件、同一时刻）。
        
        // 目标组按组遍历、只挑名字以 `Radiant Plat` 开头的：`Hazard Plat` 下还有
        // `Hazard Respawn Marker`（`Climb Plats1` 第 5/6 个动作要读它的位置调
        // `HeroController.SetHazardRespawn`，一个字段都不能动）；而 `P2 SetA` 与 `Climb Set`
        // 里有同名平台（`Radiant Plat Wide (2)`），所以绝不能按名字全局找。
        private void VanishP2Plats()
        {
            GameObject root = GameObject.Find(PlatsRoot);
            if (root == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + PlatsRoot + "，P2 平台没撤");
                return;
            }

            int n = 0;
            foreach (string group in RemoveGroups)
            {
                Transform g = root.transform.Find(group);
                if (g == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + PlatsRoot + " 下没有 " + group + " 组");
                    continue;
                }

                foreach (Transform c in g)
                {
                    if (!c.gameObject.name.StartsWith(PlatPrefix, StringComparison.Ordinal)) continue;

                    foreach (PlayMakerFSM f in c.GetComponents<PlayMakerFSM>())
                    {
                        if (f == null || f.Fsm == null || f.FsmName != PlatFsmName) continue;
                        f.SendEvent(VanishEvent);
                        n++;
                    }
                }
            }

            OrbRadiance.Instance.Log("[nailstream] P2 平台已隐去 " + n + " 块（" +
                                     string.Join(" / ", RemoveGroups) + " 下的 " + PlatPrefix +
                                     "* 发 " + VanishEvent + "；组节点与 Hazard Respawn Marker 未动，" +
                                     "重打时 Climb Plats1 再发 APPEAR 还能长回来）");
        }

        // ---------------- P3 攀爬段：恢复 P2 平台 + 维护 ----------------

        // 进 P3（`Ascend Tele`，P2 结束）：按模式恢复/维护攀爬平台；P3 横向飞剑当前停用，相关参数与生成逻辑保留。
        private void EnterP3()
        {
            if (!streaming) StartStream();      // 保险：万一 P2 段没开起来（StartStream 会把 p3Mode 清回 false）
            p3Mode = true;
            CaptureP2Plats(P2PlatformSpikePlan.ShouldReplayAppear(normalP2PlatformsKept));
            p3ClimbPlatFsms.Clear();
            if (IsClimbPlatformMode()) CaptureP3ClimbPlats();

            OrbRadiance.Instance.Log("[nailstream] 进 P3：恢复 P2 平台供攀爬；" +
                                     (IsClimbPlatformMode() ? "Z/N 保留 Climb/Ascend 平台至 Scream；" : "保留既有模式平台规则；") +
                                     "P3横向飞剑流仍停用");
        }

        private static bool IsClimbPlatformMode() => !OrbRadiance.EffectiveHardMode;

        private void CaptureP3ClimbPlats()
        {
            GameObject root = GameObject.Find(PlatsRoot);
            if (root == null) return;
            foreach (string groupName in new[] { "Climb Set", "Ascend Set" })
            {
                Transform group = root.transform.Find(groupName);
                if (group == null) continue;
                foreach (Transform platform in group)
                {
                    if (!platform.gameObject.name.StartsWith(PlatPrefix, StringComparison.Ordinal)) continue;
                    foreach (PlayMakerFSM fsm in platform.GetComponents<PlayMakerFSM>())
                    {
                        if (fsm == null || fsm.Fsm == null || fsm.FsmName != PlatFsmName) continue;
                        p3ClimbPlatFsms.Add(fsm);
                        if (fsm.Fsm.ActiveStateName == "Out" || fsm.Fsm.ActiveStateName == "Disappeared")
                            fsm.SendEvent(AppearEvent);
                    }
                }
            }
            OrbRadiance.Instance.Log("[nailstream] Z/N P3 保留爬升平台 FSM " + p3ClimbPlatFsms.Count + " 个");
        }

        private void MaintainP3ClimbPlats()
        {
            for (int i = 0; i < p3ClimbPlatFsms.Count; i++)
            {
                PlayMakerFSM fsm = p3ClimbPlatFsms[i];
                if (fsm == null || fsm.Fsm == null) continue;
                string state = fsm.Fsm.ActiveStateName;
                if (state == "Out" || state == "Disappeared") fsm.SendEvent(AppearEvent);
            }
        }

        // APPEAR 那 7 块，并把它们的 FSM 记下来供 P3 期间维护 —— 就是 VanishP2Plats 的反向操作，
        // 目标组 / 前缀 / FSM 名全部照旧（组节点与 Hazard Respawn Marker 一律不碰）。
        private void CaptureP2Plats(bool sendAppear)
        {
            p2PlatFsms.Clear();

            GameObject root = GameObject.Find(PlatsRoot);
            if (root == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + PlatsRoot + "，P2 平台没恢复");
                return;
            }

            int n = 0;
            foreach (string group in RemoveGroups)
            {
                Transform g = root.transform.Find(group);
                if (g == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + PlatsRoot + " 下没有 " + group + " 组");
                    continue;
                }

                foreach (Transform c in g)
                {
                    if (!c.gameObject.name.StartsWith(PlatPrefix, StringComparison.Ordinal)) continue;

                    foreach (PlayMakerFSM f in c.GetComponents<PlayMakerFSM>())
                    {
                        if (f == null || f.Fsm == null || f.FsmName != PlatFsmName) continue;
                        if (sendAppear) f.SendEvent(AppearEvent);
                        p2PlatFsms.Add(f);
                        n++;
                    }
                }
            }

            string action = normalP2PlatformsKept && !p3Mode ? "NORMAL 显现" : (sendAppear ? "P3 恢复" : "NORMAL 保留");
            OrbRadiance.Instance.Log("[nailstream] " + action +
                                     " P2 平台 " + n + " 块（" + string.Join(" / ", RemoveGroups) +
                                     " 下的 " + PlatPrefix + (sendAppear ? "* 发 " + AppearEvent : "*，不重播 APPEAR") + "）");
        }

        // P3 期间原版会对 `Plats P2A` 发 `SLOW VANISH`，会把这批又弄没。哪块落回 `Out`
        // （还没出现）或 `Disappeared`（已消失）就补发 `APPEAR`，保证整段攀爬都在。
        // 过渡态（Appear 1/Appear 2/Pre Vanish/Vanish Antic/Disappear）不碰，免得跟动画打架。
        private void MaintainP2Plats()
        {
            for (int i = 0; i < p2PlatFsms.Count; i++)
            {
                PlayMakerFSM f = p2PlatFsms[i];
                if (f == null || f.Fsm == null) continue;
                string s = f.Fsm.ActiveStateName;
                if (s == "Out" || s == "Disappeared") f.SendEvent(AppearEvent);
            }
        }

        private static float HeroY()
        {
            HeroController hc = HeroController.instance;
            return hc != null ? hc.transform.position.y : 0f;
        }

        // 玩家 y 过 64 之后：Z/N P3 保留原版攀爬用的
        // Ascend Set / Climb Set，Scream 瞬态再切回 P4 规则。维护列表只保留这两组，便于 P3 持续保台。
        // 其它平台发 `DISAPPEAR`；Z/N 保留攀爬组，P4 顶端平台例外。组节点与 `Hazard Respawn Marker` 不碰
        // （和 VanishP2Plats 同一套前缀过滤；Marker 名字不是 `Radiant Plat*`，天然被排除）。
        private void VanishAllPlats()
        {
            p2PlatFsms.Clear();

            GameObject root = GameObject.Find(PlatsRoot);
            if (root == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + PlatsRoot + "，y>" + PlatKillY + " 的平台没撤");
                return;
            }

            int n = 0;
            foreach (Transform g in root.transform)         // Plat Sets 下的 4 个组节点
            {
                foreach (Transform c in g)                  // 组里的平台
                {
                    if (!c.gameObject.name.StartsWith(PlatPrefix, StringComparison.Ordinal)) continue;

                    // Z/N P3 持续维护原版攀爬平台；y>64 仍撤除下方 P2 平台。
                    bool keepAtThreshold = P3PhasePlan.KeepAtPlatformThreshold(IsClimbPlatformMode() && p3Mode,
                        g.name, c.gameObject.name, P4PlatGroup, P4Plat);
                    if (keepAtThreshold) continue;


                    foreach (PlayMakerFSM f in c.GetComponents<PlayMakerFSM>())
                    {
                        if (f == null || f.Fsm == null || f.FsmName != PlatFsmName) continue;
                        f.SendEvent(VanishEvent);
                        n++;
                    }
                }
            }

            OrbRadiance.Instance.Log("[nailstream] 玩家 y>" + PlatKillY + "：撤掉 Plat Sets 下平台 " +
                                     n + " 块（Z/N P3 保留 Climb Set / Ascend Set），" +
                                     "保留 P4 顶端平台 " + P4PlatGroup + "/" + P4Plat);
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
                        if (f.Fsm != null && f.FsmName == "Control") { arenaFsm = f; break; }

                if (arenaFsm == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + ArenaFsmOwner +
                                                 " 的 Control FSM，重打时 P2 飞剑不会自动清");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }

    // 本 mod 自己生成的飞剑标记。飞剑伤害是 hazardType 0 的不可挡直伤（见 NailHazardType），
    // 形状上跟「掉虚空」一样是「非 1 伤害」，会被 VoidKill 的黑名单误判成掉坑秒杀；
    // VoidKill.OnHeroTakeDamage 认这个标记放行。空壳类，只作类型识别用。
    internal class NailProjectile : MonoBehaviour { }
}

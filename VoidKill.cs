using GlobalEnums;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // P1~P3 掉虚空秒杀：把原版的「扣 1 血 + 送回场地」顶成 9999 直接死。
    //
    // 做法跟天国余晖同一套路：不碰场景里任何 hazard 对象，只在伤害出口
    // ModHooks.TakeDamageHook 拦一道。那个 hook 就是 hkapi/HeroController.cs:1221，
    // TakeDamage 的第一行；而 :1401 先判 health==0 → Die() 再 return，
    // hazardType==2 的 DieFromHazard（送回场地那条）在 :1406 —— 血归零就变成真死。
    //
    // ⚠ 来源判定 2026-10-01 改成**照抄天国余晖**（用户定）：
    //   原来用的是「虚空白名单」（只认 `Cloud Hazard` / `Abyss Pit` / `Bounds Cage` 三个来源），
    //   结果 P2 掉下去撞到的东西不在名单里 → 退回原版扣 1 血，没秒杀。
    //   天国余晖反过来做黑名单：**不是地刺的 hazardType != 1 伤害就算掉虚空**（RadianceBoss.cs:1300-1321），
    //   P2 无论掉到哪都秒杀，正好对上「P2 无地面、掉下去就该死」的设计。
    //
    //   它认地刺靠两个条件（`go.GetComponent<SpikeTag>()` 是它自己给平台刺加的内部类，
    //   另一个是 `go.transform.IsChildOf(Spike Control)`）。OrbRadiance 没有 SpikeTag，
    //   但地刺本来就在 `Boss Control/Spike Control` 下 —— P1 那排、以及 §10 新克隆的
    //   P2 高位那排（也挂在同一个父物体下）都靠这一条认出来，够用。
    //
    //   ⚠ 例外（2026-10-01 加）：本 mod 自己生成的 P2 飞剑也是「非 1 伤害」（刻意设的
    //   hazardType 0 不可挡直伤），形状上跟掉落坑一样 → 会被这条黑名单误秒。飞剑在
    //   P2NailStream.StripAndArm 盖 `NailProjectile` 标记，`OnHeroTakeDamage` 认标记放行（见 Decide）。
    //
    // TakeDamageHook 只给 (hazardType, damage)，拿不到来源对象，所以另挂
    // On.HeroController.TakeDamage 记录 go（这一步跟天国余晖一致）。
    //
    // 相位闸门 —— Absolute Radiance 的 Control FSM：
    //   Intro Recover --FINISHED--> Arena 1 Start --FINISHED--> Arena 1 Idle    P1 开始（开）
    //   Scream --FINISHED--> A2 Tele Choice 2 --…--> Final Antic -> Final Idle  P4 顶端战开始（关）
    // 中间 P1/P2/P3 状态来回切很多次，所以用闩锁而不是比对当前状态名。
    // ⚠ 两头的状态名都要挑**非瞬间**的，否则每帧采样看不到 → 见 PhaseBeginStates / PhaseEndStates。
    internal class VoidKill : MonoBehaviour
    {
        // P1 开始的状态。⚠ 唯一可靠的是 `Intro Recover`，另两个只作兜底：
        //   · `Intro Recover`：`Tk2dPlayAnimation` + `Wait 0.25s`，一定活够几十帧被每帧采样到
        //     （2026-10-01 实测 `Ctrl -> Intro Recover` 稳稳出现）。它是 First Tele/Intro End 的唯一后继，
        //     每局 P1 之前必过。闩锁靠它打开。
        //   · `Arena 1 Start`：**瞬间状态**（只有 SendEventByName，无 Wait），进去当帧就 FINISHED，采不到。
        //   · `Arena 1 Idle`：本应靠 `WaitRandom 0.9~1.5s` 常驻，但它第 4/5 个动作是 everyFrame 的
        //     `BoolTest(bool Rage Ready / bool Please Cast)` → RAGE/CAST；这两个 bool 只要进来时已是 true，
        //     它一帧内就跳走（2026-10-01 实测：Intro Recover 的 Wait 一结束就
        //     `Arena 1 Start -> Arena 1 Idle -> Rage1 Tele` 连跳，整局只剩 `Intro Recover -> Rage1 Tele`）。
        private static readonly string[] PhaseBeginStates = { "Intro Recover", "Arena 1 Start", "Arena 1 Idle" };

        // P4（顶端战）开始的第一个**可观察**状态。
        // ⚠ `Scream` 才是 P4 真正的起点（它发 `FINAL ORBS`、把 Attack Commands 的光球生成区设成
        // y 155.1~158.9、开 `CamLock Final`），但它跟 `Arena 1 Start` 一样是**瞬间状态**：
        // 8 个动作没一个 Wait，进去当帧就 FINISHED。它后面那一串
        //   Scream --FINISHED--> A2 Tele Choice 2 --1/2/3--> Tele 11/12/13 --FINISHED--> A1 Tele 4
        // 也全是瞬间状态（SendRandomEvent / 3 个非阻塞动作），照样采不到。
        // 这一串里第一个能采到的是 `Final Antic`（播动画等事件）；`Final Idle` 是 P4 的常驻 idle
        // （带 WaitRandom，`SHIFT` 回去打完再回来），一定观察得到 —— 对应 P1 的 `Arena 1 Idle`。
        // 2026-09-17 实测：只认 `Scream` 时闩锁关不掉，P4 掉下去照样 9999。
        private static readonly string[] PhaseEndStates = { "Scream", "Final Antic", "Final Idle" };
        private const string ResetState = "Init";
        private const string SceneName = "GG_Radiance";

        // hazardType == 1 是普通伤害（Boss 攻击）——辐光的剑雨/光墙都是 1，不能碰。
        // 虚空/地刺走 2(SPIKES)/3(ACID)/4(LAVA)/5(PIT)。
        private const int HazardTypeNormal = 1;

        // P1 地板刺 + §10 克隆出来的 P2 高位刺都挂在这个父物体下（判「这次伤害是不是地刺给的」）
        private const string SpikePath = "Boss Control/Spike Control";

        // 由 Update 维护，OnTakeDamage 是静态 hook，只能读静态字段
        internal static bool KillActive;

        // 这次受击是不是地刺给的（On.HeroController.TakeDamage 记录）。地刺放行正常伤害，
        // 其余 hazardType != 1 的一律当掉虚空 → 9999（照天国余晖）。
        private static bool lastDamageIsSpike;

        // 这次受击是不是本 mod 自己生成的 P2 飞剑给的（认 NailProjectile 标记）。
        // 飞剑伤害是刻意设的 hazardType 0 不可挡直伤，形状上跟掉落坑一样是「非 1 伤害」，
        // 会被上面那条黑名单误判成掉虚空 → 2026-10-01 加这个例外放行。
        private static bool lastDamageIsNail;

        // `Boss Control/Spike Control` 的 transform 缓存。跨场景重载后 Unity 会把它变成"假 null"，
        // 那会儿自动重新 Find（同 ArenaSpikes 的用法）。
        private static Transform spikeRoot;

        private PlayMakerFSM fsm;
        private string lastState;

        internal void Init(PlayMakerFSM f)
        {
            fsm = f;
            if (f.Fsm == null) return;
            foreach (string s in PhaseBeginStates)
                if (f.Fsm.GetState(s) == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Absolute Radiance/Control 里找不到 " +
                                                 s + "，虚空 9999 不会开启");
            foreach (string s in PhaseEndStates)
                if (f.Fsm.GetState(s) == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Absolute Radiance/Control 里找不到 " +
                                                 s + "，虚空 9999 可能不会在 P4 关闭");
        }

        private void Update()
        {
            if (fsm == null || fsm.Fsm == null) return;
            string cur = fsm.Fsm.ActiveStateName;
            bool changed = cur != lastState;
            lastState = cur;

            // 总开关关掉：闩锁清零，但 lastState 照常更新，重新打开时不会漏掉转移
            if (!OrbRadiance.Settings.Enabled)
            {
                KillActive = false;
                return;
            }

            if (!changed) return;

            if (IsPhaseBegin(cur)) KillActive = true;
            else if (IsPhaseEnd(cur) || cur == ResetState) KillActive = false;
        }

        // 离开场景/对象销毁后要清掉，免得下次进场开局就是开启状态
        private void OnDestroy()
        {
            KillActive = false;
            lastDamageIsSpike = false;
            lastDamageIsNail = false;
        }

        // 只为了拿到来源对象：TakeDamageHook 的签名里没有 go
        internal static void OnHeroTakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self,
            GameObject go, CollisionSide damageSide, int damageAmount, int hazardType)
        {
            if (!OrbRadiance.Settings.Enabled)
            {
                orig(self, go, damageSide, damageAmount, hazardType);
                return;
            }
            lastDamageIsSpike = go != null && IsSpike(go);
            lastDamageIsNail = go != null && go.GetComponent<NailProjectile>() != null;
            orig(self, go, damageSide, damageAmount, hazardType);
        }

        internal static int OnTakeDamage(ref int hazardType, int damage)
        {
            return Decide(hazardType, damage);
        }

        private static int Decide(int hazardType, int damage)
        {
            if (!OrbRadiance.Settings.Enabled || damage <= 0) return damage;
            if (hazardType == HazardTypeNormal || !KillActive) return damage;
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != SceneName) return damage;
            // 地刺照常扣血（P1 地板那排 / P2 高位那排都在 Spike Control 下），其余当掉虚空
            if (lastDamageIsSpike) return damage;
            // 本 mod 自己的 P2 飞剑（hazardType 0 的不可挡直伤）放行，别当掉虚空
            if (lastDamageIsNail) return damage;
            if (OrbRadiance.EffectiveHardMode) return OrbRadiance.VoidKillDamage;
            return OrbRadiance.EffectiveEasyMode ? 1 : 2;
        }

        private static bool IsPhaseBegin(string state)
        {
            for (int i = 0; i < PhaseBeginStates.Length; i++)
                if (state == PhaseBeginStates[i]) return true;
            return false;
        }

        private static bool IsPhaseEnd(string state)
        {
            for (int i = 0; i < PhaseEndStates.Length; i++)
                if (state == PhaseEndStates[i]) return true;
            return false;
        }

        // 判「这次伤害是不是地刺给的」：来源在 Spike Control 下（含 P1 地板那排与 P2 高位那排）
        private static bool IsSpike(GameObject go)
        {
            if (go == null) return false;
            Transform t = go.transform;
            if (spikeRoot == null)
            {
                GameObject root = GameObject.Find(SpikePath);
                if (root == null) return false;
                spikeRoot = root.transform;
            }
            return t == spikeRoot || t.IsChildOf(spikeRoot);
        }
    }
}

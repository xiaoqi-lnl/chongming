using GlobalEnums;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // 根据当前难度修改掉入虚空的伤害，保留原版碰撞检测与传送行为。
    // 仅在相关战斗阶段和合法伤害来源下生效，避免误伤其他场景机制。


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

        // 原生 P1 地板刺和克隆 P2 高位刺通过 Spike Control 路径识别；H P2 旋转平台刺另用 HOrbPlatformSpike 标记。
        private const string SpikePath = "Boss Control/Spike Control";

        // 由 Update 维护，OnTakeDamage 是静态 hook，只能读静态字段
        internal static bool KillActive;

        // 这次受击是不是地刺给的（On.HeroController.TakeDamage 记录）。地刺放行原伤害；
        // 其他非普通伤害在 H 改为 9999、N 改为 2、Z/EASY 改为 1（飞剑另有豁免）。
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
            // 原生地刺由 Spike Control 路径、H P2 旋转平台刺由 HOrbPlatformSpike 标记识别；其他非普通伤害按难度改写
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

        // 判「这次伤害是不是地刺给的」：查 Spike Control 下原生/克隆刺，或 HOrbPlatformSpike 专属标记
        private static bool IsSpike(GameObject go)
        {
            if (go == null) return false;
            if (go.GetComponentInParent<HOrbPlatformSpike>() != null) return true;
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

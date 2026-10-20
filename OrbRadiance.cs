using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using UnityEngine;

namespace OrbRadiance
{
    // 重明 Mod 入口：注册攻击组件、统一难度分流，并保留关闭时恢复原版行为的逻辑。
    // 光球追踪寿命与碰撞行为由对应组件修改，P4 光球时序单独处理。


    public class OrbRadiance : Mod, IGlobalSettings<OrbRadianceSettings>, IMenuMod
    {
        internal static OrbRadiance Instance;

        internal static OrbRadianceSettings Settings = new OrbRadianceSettings();

        // ---------------- 总开关 ----------------

        public void OnLoadGlobal(OrbRadianceSettings s) => Settings = s ?? new OrbRadianceSettings();

        public OrbRadianceSettings OnSaveGlobal() => Settings;

        public bool ToggleButtonInsideMenu => true;

        public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? menu)
        {
            return new List<IMenuMod.MenuEntry>
            {
                new IMenuMod.MenuEntry(
                    "启用 重明",
                    new[] { "Off", "On" },
                    "",
                    value => SetEnabled(value == 1),
                    () => Settings.Enabled ? 1 : 0)
            };
        }

        // 神居辐光雕像交互文本：替换 Boss 名，并使用指定雕像描述
        // （照天国余晖 RadianceBoss.OnLangGet 的口径，只覆盖雕像挑战界面那几处 key）。
        //   ABSOLUTE_RADIANCE_SUPER / ABSOLUTE_RADIANCE_MAIN = 雕像挑战界面 Boss 名（原「无上辐光」）
        //   GG_S_RADIANCE                                     = 雕像界面描述（改为指定文案）
        //   FINAL_BOSS_MAIN / NAME_FINAL_BOSS                 = 原文为「辐光」两字的 Boss 名展示位
        // 关掉总开关时返回原值。
        private static string OnLangGet(string key, string sheetTitle, string orig)
        {
            if (!Settings.Enabled || orig == null) return orig;
            if (key == "GG_S_RADIANCE") return "凤皇鸣矣，于彼高冈。";
            if (orig.IndexOf("辐光") < 0) return orig;
            switch (key)
            {
                case "ABSOLUTE_RADIANCE_SUPER":
                case "ABSOLUTE_RADIANCE_MAIN":
                case "FINAL_BOSS_MAIN":
                case "NAME_FINAL_BOSS":
                    return orig.Replace("辐光", "重明");
            }
            return orig;
        }

        // 开关入口（暂停菜单 Mods 页）走这里，保证「关掉 = 把改过的值写回去」
        private static void SetEnabled(bool on)
        {
            if (Settings.Enabled == on) return;
            Settings.Enabled = on;
            if (!on)
            {
                // 关闭立即阻止伤害 Hook 继续使用上帧残留的 P1/P2 状态。
                VoidKill.KillActive = false;
                FsmWatch.StopAll();
                WorkshopStatueLifecycle.OnModDisabled();
                RestorePatched();
            }
            if (Instance != null)
                Instance.Log("OrbRadiance: " + (on ? "已启用" : "已停用（光球 FSM 值已还原）"));
        }

        // ---------------- 可调参数 ----------------

        // 生成后多久进入相位二「可被撞爆」（原版 0.5）。设 0 则一生成就进相位二。
        internal const float ChaseHeroWait = 0.5f;

        // 相位二的兜底消散（原版 2.25）。
        // 光球最长存活 ≈ ChaseHeroWait + 本值，之后 Dissipate 还要约 0.8s 缩没（其间依然致命）。
        internal const float DissipateWait = 4f; // HARD 基线；N/Z 在 Patch 中按模式使用独立目标值

        // 撞到玩家（层 20 Hero Box）是否引爆
        internal const bool BurstOnHeroHit = false;

        // 撞到地形（层 8 Terrain）是否引爆
        internal const bool BurstOnTerrainHit = false;

        // 入场动画播完（Control FSM 进入 Battle Start）后，隔多少秒移除 P1 场地。
        // 这只是「最早可能撤的时刻」：实际还要等地刺立起来（见 ArenaSpikes.GroundReady），
        // 撤除发生在 1.5s 与「24 根刺进 Up」两者都满足时 —— 后者约 2.3~2.7s。
        internal const float FloorRemoveDelay = 1.5f;

        // Attack Choices 的 Orb Recover 硬直（原版 0.5）。
        // 球招已经不走 Attack Commands（见 OrbSpawner），这段硬直失去了原本的用途，
        // 只剩下「把下一招往后推」的副作用 —— 压到 0.05，下一招基本和第一个球同时接上。
        
        // 不能设成 0：PlayMaker 的 Wait 在 time<=0 时走的是另一条分支
        // （Actions/Wait.cs OnEnter 里直接 Fsm.Event(finishEvent) 并结束），
        // 会跳掉同状态后面的动作 —— 这个状态的第 2 个动作是广播 "CAST END"，
        // 而 Control 要靠它才能从施法态出来。少发一次，下一个「请施法」就永远等不到
        
        internal const float OrbRecoverWait = 0.01f;

        // P1 掉进场地下面的虚空时给的伤害，顶掉原版的「扣 1 血 + 送回场地」。
        // 低难度下 TakeDamage 拿到的虚空伤害只有 1，这里在出口直接顶成致死值（见 VoidKill）
        internal const int VoidKillDamage = 9999;

        // 垂直剑阵（Nail Top Sweep）改成从下往上飞：见 NailTopSweep.cs
        // 生成高度（原版 33.5）
        internal const float NailSpawnY = 18f;

        // 方阵朝向（原版 180）。180° 时 comb 自身 +Y 指向世界 -Y，所以整波是往下砸的；
        // 改 0° 就是世界 +Y，整波往上平移。钉子贴图朝向跟着一起翻正。
        internal const float NailUpRotation = 0f;

        // ---------------- 实现 ----------------

        private const int LayerHeroBox = 20;
        private const int LayerTerrain = 8;

        // Unity 层号只可能是 0~31。碰撞判定是精确整数比对（gameObject.layer == collideLayer.Value），
        // 设成 99 等于永远匹配不上，作用等同于把这个触发器关掉。
        private const int NoLayer = 99;

        public override string GetVersion() => "1.0.0";

        public override List<ValueTuple<string, string>> GetPreloadNames()
        {
            var names = base.GetPreloadNames() ?? new List<ValueTuple<string, string>>();
            foreach (string path in WorkshopRoomAssets.RequiredPaths)
                names.Add(ValueTuple.Create("GG_Workshop", path));
            return names;
        }

        public OrbRadiance() : base("重明")
        {
            Instance = this;
        }

        internal static bool EffectiveHardMode => WorkshopStatueLifecycle.EffectiveHardMode(Settings.HardMode);
        internal static bool EffectiveEasyMode => BattleModeState.IsEasy;
        internal static float EffectiveOrbDissipateWait => EffectiveHardMode ? 4f : (EffectiveEasyMode ? 1.35f : 2.675f);

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            On.PlayMakerFSM.OnEnable += OnFsmEnable;
            On.HeroController.TakeDamage += VoidKill.OnHeroTakeDamage;
            ModHooks.TakeDamageHook += VoidKill.OnTakeDamage;
            ModHooks.LanguageGetHook += OnLangGet;
            WorkshopRoomAssets.Configure(preloadedObjects, LogWarn);
            WorkshopStatueLifecycle.Initialize();
            WorkshopHiddenRoom.Install();

            Log("OrbRadiance: 总开关 = 暂停菜单 Mods 页，当前 " + (Settings.Enabled ? "On" : "Off"));
        }

        private void OnFsmEnable(On.PlayMakerFSM.orig_OnEnable orig, PlayMakerFSM self)
        {
            orig(self);
            try
            {
                // Off 必须是无操作状态：任何观察器、MonoBehaviour、FSM 改写都不应安装。
                // 尤其不能挂载 FanEyeBeam；其清理逻辑可能触及原版脸激光的 Burst 1。
                if (!Settings.Enabled || self == null || self.Fsm == null) return;

                // P1 的 N/H 剑环仅接管原版 Radiant Nail 克隆体的位置，
                // 其伤害、碰撞体和原版 FSM 保留。
                if (self.FsmName == "Control" && self.gameObject != null &&
                    self.gameObject.GetComponent<DamageHero>() != null)
                    P1NailFan.TryAttachOrbit(self);

                // 临时调试（2026-10-08）：盯辐光本体关键 FSM 的状态跳转，
                // 定位 hard P2 随机光墙齐射后 Boss 卡死在哪。见 FsmWatch.cs，定位完删。
                FsmWatch.Watch(self);

                if (self.FsmName == "Attack Choices" || self.FsmName == "Attack Commands")
                {
                    // P4 最终光球循环由 P4OrbInterval 分别调整 Orb Summon、Orb Pause 和 FinalOrb Pause；
                    // P1/P2 使用 OrbSpawner，不受 P4 时序改动影响。
                    if (self.FsmName == "Attack Commands" &&
                        self.gameObject.name == "Absolute Radiance" &&
                        self.gameObject.GetComponent<P4OrbInterval>() == null)
                        self.gameObject.AddComponent<P4OrbInterval>().Init(self);

                    // 球招并行：把 Orbs 发的事件改名（Attack Commands 不认识 → 不跑球循环），
                    // 由 OrbSpawner 自己按原版节奏放球；boss 同时能接下一招
                    if (self.FsmName == "Attack Choices")
                    {
                        if (Settings.Enabled)
                        {
                            OrbSpawner.PatchBypass(self.Fsm);

                            // P2 横刺：上行波（0s/2s）之间穿插反向（从上往下）的墙 @1s/@2s（见 P2SweepWaves）
                            P2SweepWaves.Patch(self.Fsm);

                            // 再抽掉 Orb Recover 的 0.5s 硬直，否则下一招要等它走完才接得上
                            FsmState recover = self.Fsm.GetState("Orb Recover");
                            if (recover != null) SetWait(recover, "FINISHED", OrbRecoverWait);
                            else LogWarn("OrbRadiance: Attack Choices 里没有 Orb Recover 状态");

                            // 眼激光招式：把发向 Attack Commands 的 EYE BEAMS 改名，
                            // 由 EyeBeamTrack 接管成「一道、追踪」（见 EyeBeamTrack.cs）
                            EyeBeamTrack.PatchEyeBeamEvent(self.Fsm);

                            // P2 光墙（hard）：把发向 Beam Sweeper 的 BEAM SWEEP L2/R2 改名抑制，
                            // 由 HardBeamWall 接管成「随机位置预警柱 + 齐射」（见 HardBeamWall.cs）
                            if (EffectiveHardMode) HardBeamWall.PatchSuppress(self.Fsm);

                            // P1 横刺：关掉 COMB L/R 的三连发、改窗口时长，由 P1NailSweep 自己立墙抽剑。
                            // 只改神居无上辐光 —— 普通辐光（Dream_Final_Boss/Radiance）也有同名
                            // Nail L/R Sweep，那边没有替代实现，接管了横刺就没了（见 P1NailSweep 头注释）。
                            if (self.gameObject.name == "Absolute Radiance")
                                P1NailSweep.Patch(self.Fsm);
                        }
                        if (self.gameObject.GetComponent<OrbSpawner>() == null)
                            self.gameObject.AddComponent<OrbSpawner>().Init(self);

                        // P1 横刺组件（自己看总开关）也只在无上辐光上挂
                        if (self.gameObject.name == "Absolute Radiance" &&
                            self.gameObject.GetComponent<P1NailSweep>() == null)
                            self.gameObject.AddComponent<P1NailSweep>().Init(self);
                    }
                    return;
                }

                // 竞技场控制 FSM：只挂计时器 + 换报幕贴图，不动它状态流转
                if (self.FsmName == "Control" && self.gameObject.name == "Boss Control")
                {
                    if (self.gameObject.GetComponent<ArenaFloor>() == null)
                        self.gameObject.AddComponent<ArenaFloor>().Init(self);
                    // P1 地刺：和场地移除同一刻起（见 ArenaSpikes.cs）
                    if (self.gameObject.GetComponent<ArenaSpikes>() == null)
                        self.gameObject.AddComponent<ArenaSpikes>().Init(self);

                    return;
                }

                // 辐光本体：挂相位监视器，只读 ActiveStateName，供虚空 9999 判断是否在 P1
                if (self.FsmName == "Control" && self.gameObject.name == "Absolute Radiance")
                {
                    if (self.gameObject.GetComponent<VoidKill>() == null)
                        self.gameObject.AddComponent<VoidKill>().Init(self);

                    // P2：NORMAL 留原平台、HARD 拆平台并持续射飞剑（自己看总开关，所以无条件挂）
                    if (self.gameObject.GetComponent<P2NailStream>() == null)
                        self.gameObject.AddComponent<P2NailStream>().Init(self);

                    // P3：玩家头顶放脸刺（照天国余晖；自己看总开关）
                    if (self.gameObject.GetComponent<P3NailFan>() == null)
                        self.gameObject.AddComponent<P3NailFan>().Init(self);

                    // P2：另起一排 world y = 33 的地刺（同上一路，自己看总开关）
                    if (self.gameObject.GetComponent<P2GroundSpikes>() == null)
                        self.gameObject.AddComponent<P2GroundSpikes>().Init(self);

                    // NORMAL P2 的七块原生平台周期开出独立私有地刺；不复用原五组FSM池。
                    if (self.gameObject.GetComponent<P2PlatformSpikes>() == null)
                        self.gameObject.AddComponent<P2PlatformSpikes>().Init(self);

                    // N/Z P3 平台地刺：每个平台 9 根私有原版刺，H 不启用。
                    if (self.gameObject.GetComponent<P3PlatformSpikes>() == null)
                        self.gameObject.AddComponent<P3PlatformSpikes>().Init(self);

                    // N P3 单道追踪光墙：预瞄追踪 0.3s + 静止 0.1s / 发射 0.2s / 间隔 0.2s。
                    // 独立克隆 Ascend Beam，不恢复原版 P3 追踪激光，也不影响 HP2 光墙。
                    if (self.gameObject.GetComponent<P3TrackingBeam>() == null)
                        self.gameObject.AddComponent<P3TrackingBeam>().Init(self);

                    // H P3：同时发射 3 道克隆原版 Ascend Beam，偏转 1.5~6 度，玩家 y>=115 自动收回。
                    if (self.gameObject.GetComponent<P3HardTripleBeam>() == null)
                        self.gameObject.AddComponent<P3HardTripleBeam>().Init(self);

                    // 2026-10-15 用户实测要求暂时停用 N P3 上行雨；保留实现文件供后续恢复。
                    // if (self.gameObject.GetComponent<P3UpwardRain>() == null)
                    //     self.gameObject.AddComponent<P3UpwardRain>().Init(self);

                    // 无限剑雨阶段放眼激光（自己看总开关）
                    if (self.gameObject.GetComponent<RageEyeBeam>() == null)
                        self.gameObject.AddComponent<RageEyeBeam>().Init(self);

                    // P2 脸刺起手时朝首扇中心补射一道脸激光（自己看总开关）
                    if (self.gameObject.GetComponent<FanEyeBeam>() == null)
                        self.gameObject.AddComponent<FanEyeBeam>().Init(self);

                    // 眼激光招式改成追踪激光：P1/P2 都改，一次一道、追着小骑士（自己看总开关）
                    if (self.gameObject.GetComponent<EyeBeamTrack>() == null)
                        self.gameObject.AddComponent<EyeBeamTrack>().Init(self);

                    // P2 光墙（hard）：随机位置预警柱 + 齐射（自己看总开关）。
                    // 只在 H 挂 —— N 下原版广播没被抑制，挂了会「原版墙 + 本组件」出两套（见 HardBeamWall.cs）
                    if (Settings.Enabled && EffectiveHardMode &&
                        self.gameObject.GetComponent<HardBeamWall>() == null)
                        self.gameObject.AddComponent<HardBeamWall>().Init(self);

                    // P2 脸刺改成 5 圈×24 剑环（照搬天国余晖；P1 脸刺不受影响，自己看总开关）
                    if (self.gameObject.GetComponent<P2NailFanRing>() == null)
                        self.gameObject.AddComponent<P2NailFanRing>().Init(self);

                    // P1 脸刺改成 ANY1 同款（P2 期间让位给 P2NailFanRing，自己看总开关）
                    if (self.gameObject.GetComponent<P1NailFan>() == null)
                        self.gameObject.AddComponent<P1NailFan>();

                    // P2：(61, 40) 的中段宽平台，周期性消失（同上一路，自己看总开关）
                    // —— 2026-10-02 用户要求暂时停用，先注释掉挂载（P2WidePlat.cs 原样保留，要恢复取消注释即可）
                    //if (self.gameObject.GetComponent<P2WidePlat>() == null)
                    //    self.gameObject.AddComponent<P2WidePlat>().Init(self);

                    // 血量规划：总血 5000、转 P3 时 H=750 / ZN=1050、P4 血线 ZN=600 / H=300。
                    if (Settings.Enabled) BossHP.PatchControl(self);

                    // N 无限剑雨间隔按 1.5 倍提速；Z/H 保持原版（见 RageRain.cs）
                    if (Settings.Enabled) RageRain.Patch(self.Fsm);

                    // Z P3 保留原版 ASCEND BEAM；N 由单道追踪梁接管，H 由三道梁接管。
                    if (Settings.Enabled)
                    {
                        if (EffectiveEasyMode) P3Laser.Restore();
                        else P3Laser.Patch(self.Fsm);
                    }

                    // 倒地动画那一步会广播 SPIKES NONE 把五处地刺全收走
                    if (Settings.Enabled) ArenaSpikes.PatchSpikesNone(self.Fsm, ArenaSpikes.StunStartState);
                    return;
                }

                // 辐光的相位 FSM：关掉 Set Phase 3（= 无限剑雨那个状态）发的 SPIKES OUTER，
                // 否则它会把握着全场地刺的 Spike Control 拽回原版 Spikes Outer；
                // 同一 FSM 的 Stun 1 也会广播 SPIKES NONE（见 ArenaSpikes）
                if (self.FsmName == "Phase Control" && self.gameObject.name == "Absolute Radiance")
                {
                    if (Settings.Enabled)
                    {
                        // 血量相位阈值：无限落刺 3150 / 转P2 2450 / 转P3 H=750、ZN=1050。
                        BossHP.PatchPhase(self);

                        ArenaSpikes.PatchSpikesOuter(self.Fsm);
                        ArenaSpikes.PatchSpikesNone(self.Fsm, ArenaSpikes.PhaseStunState);
                    }
                    return;
                }

                // 梁扫（光墙）：克隆一块镜像发射器，两堵墙都从 x=61 向两边走（见 BeamSweeperDual）
                // 两种难度都挂：P1 的 L/R 双墙 N/H 一致；P2 的 L 2/R 2 在 hard 下广播已被
                // HardBeamWall 改名抑制 → 双墙那部分自动空转，P2 走 HardBeamWall 的随机预警墙
                if (self.FsmName == "Control" && self.gameObject.name == "Beam Sweeper")
                {
                    if (self.gameObject.GetComponent<BeamSweeperDual>() == null)
                        self.gameObject.AddComponent<BeamSweeperDual>().Init(self);
                    return;
                }

                // 垂直剑阵的方阵本体（Radiant Nail Comb）：改成从下往上飞，见 NailTopSweep
                if (self.FsmName == "Control" && NailTopSweep.IsComb(self.gameObject.name))
                {
                    if (Settings.Enabled) NailTopSweep.Patch(self.Fsm);

                    // P2 横刺（Type 7/8 的 L 2 / R 2）：上行波从下往上 + 速度 ×1.8；
                    // 反向墙（Type 6 的 Top 2）从上往下 y=80 落（见 P2CombPatch、P2SweepWaves）
                    if (Settings.Enabled) P2CombPatch.Patch(self.Fsm);

                    // P2 横刺（Type 7/8）走正弦轨迹（见 P2NailCombSine；自己看总开关）
                    // —— 2026-10-02 用户要求暂时停用（已验证可用，先注释掉挂载；P2NailCombSine.cs 原样保留，
                    //    要恢复取消注释即可）
                    //if (self.gameObject.GetComponent<P2NailCombSine>() == null)
                    //    self.gameObject.AddComponent<P2NailCombSine>().Init(self);
                    return;
                }

                if (self.FsmName != "Orb Control") return;
                if (!Settings.Enabled) return;
                Patch(self.Fsm);            }
            catch (Exception e)
            {
                LogError("OrbRadiance: " + e);
            }
        }

        private void Patch(Fsm fsm)
        {
            FsmState phase1 = fsm.GetState("Chase Hero");
            FsmState phase2 = fsm.GetState("Chase Hero 2");
            if (phase1 == null || phase2 == null)
            {
                LogWarn("OrbRadiance: Orb Control 缺少 Chase Hero / Chase Hero 2，未改动");
                return;
            }

            // 池化复用时每次按当前模式重算；目标值从原始动作字段推出，不累乘。
            SetWait(phase1, "FINISHED", ChaseHeroWait);
            // 相位一自带一个兜底消散，对齐成总寿命，免得 ChaseHeroWait 调大后被它抢跑
            float phase2Wait = EffectiveOrbDissipateWait;
            SetWait(phase1, "DISSIPATE", ChaseHeroWait + phase2Wait);
            SetWait(phase2, "DISSIPATE", phase2Wait);

            SetBurstLayer(phase2, LayerHeroBox, BurstOnHeroHit);
            SetBurstLayer(phase2, LayerTerrain, BurstOnTerrainHit);

            // P4 Z/N 光球追踪速度与加速度都为原生的 1.3 倍。对象池复用时按基线重算，
            // 非 P4 用倍率 1 恢复，避免对象池中的上一局/上一阶段速度污染下一局。
            float speedScale = IsFinalOrbPhase()
                ? (EffectiveHardMode ? 1f : 1.3f) : 1f;
            ScaleOrbChaseSpeed(phase1, speedScale);
            ScaleOrbChaseSpeed(phase2, speedScale);
            if (speedScale != 1f && lastLoggedP4SpeedScale != speedScale)
            {
                lastLoggedP4SpeedScale = speedScale;
                Log("[orb-speed] P4 追踪光球倍率 " + speedScale.ToString("0.0") + "（速度上限与加速度均按原版值计算）");
            }
        }

        // 记下我们改之前的值：关开关时写回去。只记一次（光球是池化复用的，
        // 同一颗会多次 OnEnable → 多次 Patch，第二次起读到的就是我们的值了）
        private static readonly Dictionary<Wait, float> OriginalWait = new Dictionary<Wait, float>();
        private static readonly Dictionary<Trigger2dEventLayer, int> OriginalLayer =
            new Dictionary<Trigger2dEventLayer, int>();
        // 原始 ChaseObjectV2 追踪参数：FsmFloat 引用按动作实例保存，以免对象池反复乘倍率。
        private static readonly Dictionary<FsmFloat, float> OriginalChase = new Dictionary<FsmFloat, float>();
        private static bool warnedMissingFinalFlag;
        private static bool warnedMissingChaseField;
        private static float lastLoggedP4SpeedScale = 1f;

        private static bool IsFinalOrbPhase()
        {
            // FINAL ORBS 只在最终阶段由 Scream 发送。此处只判断 GG_Radiance 的无上辐光，
            // 不影响普通辐光、P1/P2 光球和其他调用同一 Orb Control 的预制体。
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "GG_Radiance") return false;
            GameObject boss = GameObject.Find("Absolute Radiance");
            if (boss == null) return false;
            PlayMakerFSM commands = boss.GetComponents<PlayMakerFSM>()
                .FirstOrDefault(f => f != null && f.Fsm != null && f.FsmName == "Attack Commands");
            if (commands == null) return false;
            FsmBool final = commands.Fsm.Variables.GetFsmBool("Final Orbs");
            if (final == null)
            {
                if (!warnedMissingFinalFlag)
                {
                    warnedMissingFinalFlag = true;
                    Instance.LogWarn("OrbRadiance: Attack Commands 缺少 Final Orbs 标记，P4 光球加速未应用");
                }
                return false;
            }
            return final.Value;
        }

        private void ScaleOrbChaseSpeed(FsmState state, float multiplier)
        {
            // 通过反射读取原生 ChaseObjectV2 字段，避免对 PlayMaker 动作结构作任何增删。
            foreach (ChaseObjectV2 chase in state.Actions.OfType<ChaseObjectV2>())
            {
                ScaleChaseField(chase, "speedMax", multiplier);
                ScaleChaseField(chase, "accelerationForce", multiplier);
            }
        }

        private void ScaleChaseField(ChaseObjectV2 action, string name, float multiplier)
        {
            FieldInfo field = typeof(ChaseObjectV2).GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FsmFloat number = field == null ? null : field.GetValue(action) as FsmFloat;
            if (number == null)
            {
                if (!warnedMissingChaseField)
                {
                    warnedMissingChaseField = true;
                    LogWarn("OrbRadiance: ChaseObjectV2 缺少 FsmFloat 追踪字段 " + name + "，光球加速未完整应用");
                }
                return;
            }
            if (!OriginalChase.ContainsKey(number)) OriginalChase[number] = number.Value;
            number.Value = OriginalChase[number] * multiplier;
        }

        private void SetWait(FsmState state, string finishEvent, float seconds)
        {
            Wait w = state.Actions.OfType<Wait>()
                .FirstOrDefault(x => x.finishEvent != null && x.finishEvent.Name == finishEvent);
            if (w == null)
            {
                LogWarn($"OrbRadiance: {state.Name} 里找不到 finishEvent={finishEvent} 的 Wait");
                return;
            }
            if (!OriginalWait.ContainsKey(w)) OriginalWait[w] = w.time.Value;
            w.time.Value = seconds;
        }

        private void SetBurstLayer(FsmState state, int layer, bool burst)
        {
            foreach (Trigger2dEventLayer t in state.Actions.OfType<Trigger2dEventLayer>())
            {
                if (t.collideLayer == null || t.collideLayer.Value != layer) continue;
                if (!OriginalLayer.ContainsKey(t)) OriginalLayer[t] = t.collideLayer.Value;
                t.collideLayer.Value = burst ? layer : NoLayer;
            }
        }

        // 关开关时把动过的字段写回原值。销毁过的对象（Unity 假 null）跳过，不硬编原版数值。
        private static void RestorePatched()
        {
            WorkshopHiddenRoom.Cleanup(true);
            BossHP.Restore();
            RageRain.Restore();
            P3Laser.Restore();
            P3TrackingBeam.Restore();
            P3HardTripleBeam.Restore();
            P3PlatformSpikes.Restore();
            P4OrbInterval.Restore();
            foreach (KeyValuePair<Wait, float> kv in OriginalWait)
                if (kv.Key != null) kv.Key.time.Value = kv.Value;
            foreach (KeyValuePair<Trigger2dEventLayer, int> kv in OriginalLayer)
                if (kv.Key != null && kv.Key.collideLayer != null) kv.Key.collideLayer.Value = kv.Value;
            foreach (KeyValuePair<FsmFloat, float> kv in OriginalChase)
                if (kv.Key != null) kv.Key.Value = kv.Value;
            OriginalChase.Clear();
            lastLoggedP4SpeedScale = 1f;
            foreach (KeyValuePair<SendEventByName, string> kv in OrbSpawner.OriginalSendEvent)
                if (kv.Key != null && kv.Key.sendEvent != null) kv.Key.sendEvent.Value = kv.Value;
            ArenaSpikes.RestorePatched();
            ArenaSpikes.RestoreSpikesOuter();
            NailTopSweep.Restore();
            P1NailSweep.Restore();
            P2CombPatch.Restore();
            P2SweepWaves.Restore();
            RageEyeBeam.Restore();
            FanEyeBeam.Restore();
            EyeBeamTrack.Restore();
            HardBeamWall.Restore();
            P2NailFanRing.Restore();
            // 必须排在 P2NailFanRing 之后：P2 中途关开关时，先让 P2NailFanRing 写回 ANY1，再写回原版
            P1NailFan.Restore();

        }
    }
}

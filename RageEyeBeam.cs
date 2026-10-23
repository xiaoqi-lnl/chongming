using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // 无限剑雨阶段 HARD 额外持续发射眼激光，其他难度不启用。
    // 触发并驱动原版 Attack Commands 的 RAGE EYES 链及 Burst 1/2/3；转阶段或组件销毁时收束。


    internal class RageEyeBeam : MonoBehaviour
    {
        // ---------------- 时间点（状态名）----------------

        private const string WindowA = "Rage Comb";
        private const string WindowB = "Rage1 Loop";

        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };

        private const string ArenaFsmOwner = "Boss Control";

        // ---------------- 眼激光 ----------------

        // Attack Commands 的全局事件，进入 P2 专用无限眼激光链
        private const string TriggerEvent = "RAGE EYES";

        // 链上的状态（判断链是否在跑 / 是否需要收链）
        private static readonly string[] ChainStates = { "NF Glow 2", "EB 4", "EB 5", "EB 6" };

        // 链的兜底收尾状态（DOWN 关眼晕 → EB End 2 关眼晕物体 + 发 ATTACK END）
        private const string ChainEndState = "EB Glow End 2";

        // 前摇：把 NF Glow 2 的 Wait 0.5 压到 0.01（不能设 0，见 OrbRadiance.OrbRecoverWait 那条）
        private const float GlowWait = 0.01f;

        // 爆发体的 ACTIVE 状态（这两个才需要补 FIRE+END 复位）
        private static readonly string[] BurstLiveStates = { "Antic", "Fire" };

        // 眼激光物体（Absolute Radiance 的子物体）
        private const string GlowName = "Eye Beam Glow";
        private static readonly string[] BurstNames = { "Burst 1", "Burst 2", "Burst 3" };

        // ---------------- 运行时 ----------------

        private PlayMakerFSM control;               
        private PlayMakerFSM commands;              // Absolute Radiance/Attack Commands（同一 GO）

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private Transform burstRoot;                // Eye Beam Glow（Burst 1/2/3 的父）

        private bool inWindow;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // 总开关关掉：收链复位
            if (!OrbRadiance.Settings.Enabled)
            {
                Disarm();
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同一场景内重来，Absolute Radiance/Control 不会自己回 Init
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) Disarm();
            }

            // N/Z 剑雨阶段不触发脸激光；若模式/阶段在运行中变化，立即收回本链。
            if (!OrbRadiance.EffectiveHardMode)
            {
                Disarm();
                return;
            }

            string cur = control.Fsm.ActiveStateName;
            bool now = cur == WindowA || cur == WindowB;

            if (now != inWindow)
            {
                inWindow = now;
                if (!now) Stop();            // 离开剑雨态那一刻收链（只在跃迁沿做一次）
            }
            if (!inWindow) return;

            if (!EnsureCommands()) return;

            // 剑雨期间只要 Attack Commands 不在眼激光链上就补发 RAGE EYES
            // （`Idle` 是单向死状态：只有起始态 Init 能进去，出去就再也回不来，
            //   顶刺 `Comb Top` / 脸刺 `Nail Fan` 都会把它踢走，所以门不能设在 Idle。
            //   发完就进 NF Glow 2（落在链上）→ 正常只发一次；万一掉下链会自动接上）
            if (Array.IndexOf(ChainStates, commands.Fsm.ActiveStateName) < 0)
            {
                Patch(commands.Fsm);
                commands.SendEvent(TriggerEvent);
                OrbRadiance.Instance.Log("[rageeye] 剑雨阶段触发眼激光（" + TriggerEvent +
                                         "）：NF Glow 2 → EB 4/5/6 循环，前摇 ≈" + (GlowWait + 0.5f) + "s");
            }
        }

        private void Disarm()
        {
            if (inWindow) Stop();
            inWindow = false;
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
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands FSM，剑雨眼激光放不出");
                return false;
            }
            return true;
        }

        // ---------------- 收尾 ----------------

        private void Stop()
        {
            if (commands == null || commands.Fsm == null) return;

            string s = commands.Fsm.ActiveStateName;

            // 只在链上时才切（Idle / 别的攻击态不动，免得误发 ATTACK END）
            if (Array.IndexOf(ChainStates, s) >= 0)
            {
                commands.Fsm.SetState(ChainEndState);
                OrbRadiance.Instance.Log("[rageeye] 剑雨结束，眼激光收链（" + s + " → " + ChainEndState + "）");
            }

            int n = ResetBursts();
            if (n > 0) OrbRadiance.Instance.Log("[rageeye] 复位 " + n + " 个眼激光爆发体");
        }

        // SetState 只杀 Attack Commands 自己的延迟事件，Burst 的子 FSM 不受影响，
        // 且 `Antic` 只认 FIRE → 对处于 Antic/Fire 的补发 FIRE + END（Antic→Fire→End→Inert）
        private int ResetBursts()
        {
            if (burstRoot == null && !ResolveBeamObjects()) return 0;

            int n = 0;
            foreach (string name in BurstNames)
            {
                Transform b = burstRoot.Find(name);
                if (b == null) continue;
                foreach (PlayMakerFSM f in b.GetComponentsInChildren<PlayMakerFSM>(true))
                {
                    if (f == null || f.Fsm == null) continue;
                    if (Array.IndexOf(BurstLiveStates, f.Fsm.ActiveStateName) < 0) continue;
                    f.SendEvent("FIRE");
                    f.SendEvent("END");
                    n++;
                }
            }
            return n;
        }

        private bool ResolveBeamObjects()
        {
            Transform glow = transform.Find(GlowName);
            if (glow == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + GlowName + "，眼激光爆发体没法复位");
                return false;
            }
            burstRoot = glow;
            return true;
        }

        // ---------------- 前摇：压 NF Glow 2 的 Wait ----------------

        private static readonly Dictionary<Wait, float> OriginalWait = new Dictionary<Wait, float>();

        // NF Glow 2 只有一条通往它的转移，只影响我们的链。每次调用都重新扫，不靠全局「已处理」
        // 标记：新一局的 Attack Commands 是新对象，旧标记会把它跳过。幂等靠两点：① 原值按 Wait
        // 实例只记一次（ContainsKey，避免把已压过的 0.01 当成原值写进账本）；② 值恒置 GlowWait。
        private static void Patch(Fsm fsm)
        {
            FsmState st = fsm.GetState("NF Glow 2");
            if (st == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 NF Glow 2，眼激光前摇未改");
                return;
            }

            Wait w = st.Actions.OfType<Wait>().FirstOrDefault();
            if (w == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: NF Glow 2 里找不到 Wait，眼激光前摇未改");
                return;
            }

            if (!OriginalWait.ContainsKey(w)) OriginalWait[w] = w.time.Value;   // 只记一次真原值
            w.time.Value = GlowWait;                                            // 恒为常量 → 幂等
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<Wait, float> kv in OriginalWait)
                if (kv.Key != null) kv.Key.time.Value = kv.Value;
            OriginalWait.Clear();
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
                                                 " 的 Control FSM，重打时剑雨眼激光不会自动收");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

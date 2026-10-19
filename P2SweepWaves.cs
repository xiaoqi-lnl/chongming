using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // P2 横刺：在原有「从下往上」的波里**穿插反向（从上往下）的墙**（2026-10-05 用户定，照 ANY2 P1）。
    //
    // 原版 `Attack Choices/Nail L Sweep 2`（R 同理）：4 个 `SendEventByName` 发 "COMB L2"
    //   （delay 0 / 2 / 3.5 / 5，**后两个 Enabled=false**）+ 一个 `Wait` 3.5s。
    //   所以原版 P2 一次横刺只出 2 波（0s、2s），全是从下往上（见 P2CombPatch.cs）。
    //
    // 参照 ANY2（`参考代码/ANY2/decompiled/AnyRAD2.0（Any2.0辐光）.decompiled.cs:660-673`）：
    //   ANY2 的 P1 横刺在一路波里 `InsertAction` 插了两条**反方向**的 comb（@1s、@2s）——
    //   那套靠「往状态里加动作」，与 [[feedback-fsm-index-stability]]（不增删动作）冲突，不能照搬。
    //   本 mod 用**现成但禁用的第 3、4 个 sender**（0-based 下标 2、3）顶替，纯原位改字段值，效果等价：
    //     idx 2：`sendEvent` "COMB L2" → "COMB TOP2"、`delay` 3.5 → 1、`Enabled` false → true
    //     idx 3：`sendEvent` "COMB L2" → "COMB TOP2"、`delay` 5   → 2、`Enabled` false → true
    //   `COMB TOP2` → Attack Commands/Comb Top 2 → Type 6 → comb 的 `Top 2` 状态
    //     （从上往下；x 居中 60.5、y=80、速度 ×1.8，见 P2CombPatch.cs）
    //   于是 P2 一轮横刺 = **上@0、下@1、上@2、下@2**（4 波交错、Y 轴对穿）。
    //
    // 同帧/连续两个 `COMB TOP2` 不会打架：`COMB L2`/`COMB TOP2` 在 Attack Commands 里都是
    //   **全局转移**（fsm_3634.md:3549-3551），`Comb L 2`/`Comb Top 2` 是 0 转移状态、进状态当帧
    //   即 `OnEnter` 跑动作（FsmState.decompiled.cs:302）→ 每次发都各生成一把 comb、Type 正确；
    //   Attack Choices 自己的全局转移只有 `ARENA 1/2 END`，不会被这两个事件带跑。
    //
    // 挂点：同 OrbSpawner.PatchBypass —— 在 `OrbRadiance.OnFsmEnable` 的 Attack Choices 分支里
    //   调 `Patch`（Attack Choices 是常驻 FSM，OnEnable 会重入，所以记录 Original 保证幂等）。
    //   总开关关掉走 `OrbRadiance.RestorePatched` → `Restore()`。
    internal static class P2SweepWaves
    {
        private const string WallEvent = "COMB TOP2";   // 反向墙（Comb Top 2 → Type 6，从上往下）
        private const float NewWait = 4f;               // 状态结束时间（原版 3.5s；留余量，§17 起就是这个值）

        // 反向墙用现成但禁用的两个 sender：0-based 下标 2、3
        private const int WallIndex = 2;
        private const float FirstWallDelay = 1f;
        private const float SecondWallDelay = 2f;

        private static readonly string[] States = { "Nail L Sweep 2", "Nail R Sweep 2" };

        private static readonly Dictionary<SendEventByName, string> OriginalEvent =
            new Dictionary<SendEventByName, string>();
        private static readonly Dictionary<SendEventByName, bool> OriginalEnabled =
            new Dictionary<SendEventByName, bool>();
        private static readonly Dictionary<SendEventByName, float> OriginalDelay =
            new Dictionary<SendEventByName, float>();
        private static readonly Dictionary<Wait, float> OriginalWait = new Dictionary<Wait, float>();

        internal static void Patch(Fsm fsm)
        {
            foreach (string name in States)
            {
                FsmState st = fsm.GetState(name);
                if (st == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Choices 里没有 " + name + " 状态，反向墙未加");
                    continue;
                }

                // 按下标认（不按 delay —— 本类会改 delay，靠 delay 认会不稳）
                SendEventByName[] senders = st.Actions.OfType<SendEventByName>().ToArray();
                if (senders.Length < WallIndex + 2)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + name + " 只有 " + senders.Length +
                                                 " 个 SendEventByName（需 ≥" + (WallIndex + 2) + "），反向墙未加");
                }
                else
                {
                    PatchWall(senders[WallIndex], FirstWallDelay);
                    PatchWall(senders[WallIndex + 1], SecondWallDelay);
                }

                // Wait 延长，保证最后一波（2s）在状态结束前发得出
                Wait w = st.Actions.OfType<Wait>().FirstOrDefault();
                if (w == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + name + " 里找不到 Wait，结束时间未延");
                }
                else
                {
                    if (!OriginalWait.ContainsKey(w)) OriginalWait[w] = w.time.Value;
                    w.time.Value = NewWait;
                }
            }
        }

        private static void PatchWall(SendEventByName s, float delay)
        {
            if (!OriginalEvent.ContainsKey(s))
            {
                OriginalEvent[s] = s.sendEvent.Value;
                OriginalEnabled[s] = s.Enabled;
                OriginalDelay[s] = s.delay.Value;
            }
            s.Enabled = true;
            if (s.sendEvent != null) s.sendEvent.Value = WallEvent;
            if (s.delay != null) s.delay.Value = delay;
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<SendEventByName, string> kv in OriginalEvent)
                if (kv.Key != null && kv.Key.sendEvent != null) kv.Key.sendEvent.Value = kv.Value;
            foreach (KeyValuePair<SendEventByName, bool> kv in OriginalEnabled)
                if (kv.Key != null) kv.Key.Enabled = kv.Value;
            foreach (KeyValuePair<SendEventByName, float> kv in OriginalDelay)
                if (kv.Key != null && kv.Key.delay != null) kv.Key.delay.Value = kv.Value;
            foreach (KeyValuePair<Wait, float> kv in OriginalWait)
                if (kv.Key != null) kv.Key.time.Value = kv.Value;
        }
    }
}

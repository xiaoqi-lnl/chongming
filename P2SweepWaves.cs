using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // P2 横刺将原版四波改为上、下、上、下交错，原位调整 FSM 发射事件与延迟。


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

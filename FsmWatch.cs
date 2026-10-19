using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // 临时调试（2026-10-08）：打印辐光本体几个关键 FSM 的状态跳转，
    // 用来定位「hard P2 随机光墙齐射之后 Boss 不出招」到底卡在哪个状态。
    //
    // 挂点在 OrbRadiance.OnFsmEnable 里那行 `FsmWatch.Watch(self);`。
    // 定位完两处一起删：本文件 + OrbRadiance.cs 里那行。
    //
    // 用 Fsm.StateChanged（每次 EnterState 都触发）而不是逐帧采样 ActiveStateName
    // —— PlayMaker 会同帧连跳多个状态，采样会漏（见 P1NailSweep.cs 头注释）。
    internal class FsmWatch : MonoBehaviour
    {
        private static readonly Dictionary<Fsm, Action<FsmState>> Live =
            new Dictionary<Fsm, Action<FsmState>>();

        private readonly List<Fsm> heldFsms = new List<Fsm>();
        private readonly List<Action<FsmState>> heldHandlers = new List<Action<FsmState>>();

        internal static void Watch(PlayMakerFSM f)
        {
            if (f == null || f.Fsm == null) return;

            string n = f.FsmName;
            bool want = n == "Attack Choices" || n == "Teleport" ||
                        (n == "Control" && (f.gameObject.name == "Absolute Radiance" ||
                                            f.gameObject.name == "Beam Sweeper"));
            if (!want) return;
            if (Live.ContainsKey(f.Fsm)) return;

            FsmWatch w = f.gameObject.GetComponent<FsmWatch>();
            if (w == null) w = f.gameObject.AddComponent<FsmWatch>();

            string label = f.gameObject.name + "/" + n;
            Action<FsmState> h = s => w.OnChanged(label, s);
            f.Fsm.StateChanged += h;

            Live[f.Fsm] = h;
            w.heldFsms.Add(f.Fsm);
            w.heldHandlers.Add(h);

            OrbRadiance.Instance.Log("[watch] 订阅 " + label + " 状态跳转");
        }

        internal static void StopAll()
        {
            foreach (KeyValuePair<Fsm, Action<FsmState>> pair in Live)
                if (pair.Key != null) pair.Key.StateChanged -= pair.Value;
            Live.Clear();
        }

        private void OnChanged(string label, FsmState s)
        {
            if (s == null) return;
            OrbRadiance.Instance.Log("[watch] " + label + " → " + s.Name +
                                     "  t=" + Time.time.ToString("0.00"));
        }

        private void OnDestroy()
        {
            for (int i = 0; i < heldFsms.Count; i++)
            {
                if (heldFsms[i] != null) heldFsms[i].StateChanged -= heldHandlers[i];
                Live.Remove(heldFsms[i]);
            }
            heldFsms.Clear();
            heldHandlers.Clear();
        }
    }
}

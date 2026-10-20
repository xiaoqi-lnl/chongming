using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // HARD P2 在 y=33 额外生成一排私有地刺，不移动 P1 原有地刺。
    // P2 离开或 Mod 关闭时销毁新增对象。


    internal class P2GroundSpikes : MonoBehaviour
    {
        // ---------------- 时间点（状态名，与 P2NailStream 同一套）----------------

        private const string StateStart = "Arena 2 Start";
        private const string StateStop = "Ascend Tele";

        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };

        private const string ArenaFsmOwner = "Boss Control";

        // ---------------- 地刺 ----------------

        private const string SpikePath = "Boss Control/Spike Control";

        // 五个地面组容器名（原版），克隆的就是它们各自整棵子树
        private static readonly string[] Groups =
            { "Centre", "Mid L", "Mid R", "Far L", "Far R" };

        // 单根刺在组内的高低：组容器在 world (0,0,0)，刺 local y = 21.18
        private const float SpikeLocalY = 21.18f;

        // 用户定的目标高度：整根刺的 world y = 33
        private const float TargetWorldY = 33f;

        // 克隆体名字后缀，用来和原版那 5 个组区分（FindChild 按名字找的是原版，不会撞）
        private const string CloneSuffix = " (P2 33)";

        private const string SpikeFsmName = "Control";

        // ---------------- 运行时 ----------------

        private PlayMakerFSM control;               

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private readonly List<GameObject> clones = new List<GameObject>();
        private readonly List<PlayMakerFSM> cloneFsms = new List<PlayMakerFSM>();
        private string lastState;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // NORMAL / EASY 使用原版 7 平台，不造或驱动 worldY=33 的克隆刺。
            if (!OrbRadiance.EffectiveHardMode)
            {
                Remove();
                lastState = control.Fsm.ActiveStateName;
                lastArenaState = null;
                return;
            }

            // 总开关关掉：拆掉克隆体（原版那排不受影响）
            if (!OrbRadiance.Settings.Enabled)
            {
                Remove();
                lastState = null;
                lastArenaState = null;
                return;
            }

            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) Remove();
            }

            string cur = control.Fsm.ActiveStateName;
            if (cur != lastState)
            {
                lastState = cur;

                if (cur == StateStart) Build();
                else if (cur == StateStop) Remove();
            }

            // 每帧补 UP：克隆体自己走 Init → Downed，被 SPIKES DOWN 打落后也要靠这个重新升起
            if (clones.Count > 0) PushUp();
        }

        // ---------------- 造 / 拆 ----------------

        private void Build()
        {
            Remove();

            GameObject root = GameObject.Find(SpikePath);
            if (root == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + SpikePath + "，P2 高位地刺没造");
                return;
            }

            float groupY = TargetWorldY - SpikeLocalY;

            int made = 0;
            foreach (string name in Groups)
            {
                Transform g = root.transform.Find(name);
                if (g == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + SpikePath + " 下没有 " + name +
                                                 " 组，这一处高台刺没造");
                    continue;
                }

                GameObject clone;
                try
                {
                    clone = Instantiate(g.gameObject, root.transform);
                }
                catch (Exception e)
                {
                    OrbRadiance.Instance.LogError("OrbRadiance: 克隆 " + name + " 抛异常 " + e);
                    continue;
                }
                if (clone == null) continue;

                clone.name = name + CloneSuffix;
                // 绝对赋值（不是加法）：原组此刻可能已被 ArenaSpikes 下沉过
                clone.transform.localPosition = new Vector3(g.localPosition.x, groupY, g.localPosition.z);
                clone.SetActive(true);

                PlayMakerFSM[] fsms = clone.GetComponentsInChildren<PlayMakerFSM>(true);
                // PushUp 反向遍历缓存，所以每组逆序登记以保留原来的组内 UP 顺序。
                for (int i = fsms.Length - 1; i >= 0; i--)
                {
                    PlayMakerFSM fsm = fsms[i];
                    if (fsm != null && fsm.Fsm != null && fsm.FsmName == SpikeFsmName)
                        cloneFsms.Add(fsm);
                }

                clones.Add(clone);
                made++;
            }

            OrbRadiance.Instance.Log("[spikes] P2 高位地刺已生成：克隆 " + made + "/" + Groups.Length +
                                     " 组，刺 world y = " + TargetWorldY + "（组 local y " + groupY +
                                     "）；P1 那排仍在 18.5");
        }

        private void Remove()
        {
            cloneFsms.Clear();
            if (clones.Count == 0) return;

            int n = 0;
            foreach (GameObject c in clones)
            {
                if (c == null) continue;    // 场景卸载 / 已被销毁（Unity 假 null）
                Destroy(c);
                n++;
            }
            clones.Clear();

            if (n > 0) OrbRadiance.Instance.Log("[spikes] P2 高位地刺已拆 " + n + " 组");
        }

        // ---------------- 升起 ----------------

        private void PushUp()
        {
            for (int i = clones.Count - 1; i >= 0; i--)
                if (clones[i] == null) clones.RemoveAt(i);

            for (int i = cloneFsms.Count - 1; i >= 0; i--)
            {
                PlayMakerFSM fsm = cloneFsms[i];
                if (fsm == null) { cloneFsms.RemoveAt(i); continue; }
                if (fsm.Fsm != null) fsm.SendEvent("UP");
            }
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
                                                 " 的 Control FSM，重打时 P2 高位地刺不会自动拆");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

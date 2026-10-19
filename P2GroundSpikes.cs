using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // P2 高位地刺（2026-10-01 用户定）：
    //   「当 Boss 处于 P2 时，在 y=33 处放地刺」—— P1 那排（world y 18.5）原地不动，
    //   另起一排新的，只在 P2 战斗段存在。
    //
    // 时间点与 P2NailStream 完全一致（同一个 FSM、同一套状态名）：
    //   `Absolute Radiance/Control` 进 `Arena 2 Start` → 造刺并升起
    //                          进 `Ascend Tele`   → 拆掉
    //   父物体 `Boss Control/Control` 进 `Battle Start` / `Refight` / `Init` / `Setup` → 拆掉
    //   （死亡重打同场景重来，`Absolute Radiance/Control` 不会自己回 Init，得靠这一路清）
    //
    // 做法 = **克隆原版刺**，不新造视觉/伤害：
    //   把 `Boss Control/Spike Control` 下的 5 个地面组（Centre / Mid L / Mid R / Far L / Far R）
    //   各 Instantiate 一份挂回同一父物体，命名加后缀以便区分。克隆体自带原版那套
    //   MeshRenderer + PolygonCollider2D + `Control` / `Hero Saver` 两个 FSM + 伤害组件，
    //   所以「长什么样、怎么伤人、被英雄碰到怎么收」全和原版地板刺一致，一个字段都不用改。
    //
    // 高度：单根刺在组内的 local y 是 21.18（组容器本身在 world (0,0,0)），
    //   所以把克隆组的 local y 设成 TargetWorldY - 21.18 即可让刺落在 world y = 33。
    //   注意用**绝对值**赋值、不是偏移 —— ArenaSpikes 在 P1 开局已经把那 5 个原组下沉了
    //   2.68（刺 21.18 → 18.5），克隆那会儿抓到的组是下沉后的位置，偏移着算会算错。
    //
    // 为什么要每帧补发 `UP`：
    //   单根刺的 `Control` FSM 是 Init → Downed（碰撞体/网格都关），只有 `Downed` 状态认 `UP`
    //   （`Floor Antic` / `Spike Up` / `Up` 都没有 UP 转移）。克隆体 OnEnable 后要过一帧才走到
    //   Downed，所以创建当帧发一次是不够的；而且英雄碰到任意一根刺时 `Hero Saver` 会
    //   **BroadcastAll** 发 `SPIKES DOWN`（原版机制），克隆体也会被一起打回 `Down` ——
    //   每帧补 `UP` 就等于原版那套 0.1s 刷条，被打落后自动重新升起。
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

        private PlayMakerFSM control;               // Absolute Radiance/Control

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

using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P2 中央宽平台周期消失试验组件；当前在 OrbRadiance.cs 中未挂载。


    internal class P2WidePlat : MonoBehaviour
    {
        // ---------------- 时间点（状态名，与 P2NailStream / P2GroundSpikes 同一套）----------------

        private const string StateStart = "Arena 2 Start";
        private const string StateStop = "Ascend Tele";

        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };

        private const string ArenaFsmOwner = "Boss Control";

        // ---------------- 平台 ----------------

        // 克隆源：`Boss Control/Plat Sets/Ascend Set` 下的宽平台。使用精确路径避免与其他组的同名平台混淆；
        // 模板 scale 为 (1,1,1)，带 `SpriteFlash`（供 `FlashingGhostWounded` 使用），且对象处于 active。
        // ⚠ 不能用 `Radiant Plat Wide (2)` 这个**名字**全局找：`P2 SetA` / `Climb Set` 里各有一块同名的。
        private const string TemplatePath = "Boss Control/Plat Sets/Ascend Set/Radiant Plat Wide (2)";

        private const string PlatFsmName = "radiant_plat";

        // 用户定的位置（平台 transform；模板的站面 `Colliders` 子物体在 local +0.64，所以踩点是 y≈40.64）
        private const float TargetX = 61f;
        private const float TargetY = 40f;

        // 用户定的时长（秒）
        private const float VisibleTime = 3f;    // 显示
        private const float HiddenTime = 10f;    // 完全消失
        private const float WarnTime = 0.01f;    // Pre Vanish：预警（不能设 0，见 OrbRadiance.OrbRecoverWait 那条）
        private const float FlashTime = 0.5f;    // Vanish Antic：闪烁（FlashingGhostWounded 打在这儿）

        // 平台 FSM 认这三个事件：APPEAR / DISAPPEAR / SLOW VANISH
        private const string VanishEvent = "SLOW VANISH";
        private const string AppearEvent = "APPEAR";

        // ---------------- 运行时 ----------------

        private PlayMakerFSM control;               

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private GameObject plat;
        private PlayMakerFSM platFsm;

        private bool started;                       // 已进过 Appear 1/2/Idle（出现动画走完）
        private float visT;                         // 可见计时
        private float goneT;                        // 完全消失计时

        private string lastState;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // 总开关关掉：拆掉平台（原版平台不受影响）
            if (!OrbRadiance.Settings.Enabled)
            {
                Remove();
                lastState = null;
                lastArenaState = null;
                return;
            }

            // 重打复位
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

            if (plat == null || platFsm == null || platFsm.Fsm == null) return;

            // 克隆体 Instantiate 后要等 Unity 调 PlayMakerFSM.Start() 才会把 FSM 从 Start State(`Init`)
            // 跑起来（`Fsm.Started` 不是序列化字段 → 必定重跑）。在那之前 `ActiveStateName` 可能还是
            // 从模板复制来的状态，而 `Colliders` 变量也还指着**模板**那颗子物体 —— 这时发 APPEAR 会去
            // 开模板的碰撞体（`Appear 2` 的 ActivateGameObject(Colliders)）。所以先等 Started。
            if (!platFsm.Fsm.Started) return;

            string p = platFsm.Fsm.ActiveStateName;

            // 克隆体 FSM 从 Init → Out（Init 做 FindChild Colliders + 藏 mesh + 关碰撞）。Out 才认 APPEAR，
            // 所以在 Out 期间每帧补发一次，直到出现动画把它带进 Appear 1。
            if (!started)
            {
                if (p == "Init" || p == "Out") platFsm.SendEvent(AppearEvent);
                if (p == "Appear 1" || p == "Appear 2" || p == "Idle")
                {
                    started = true;
                    visT = 0f;
                }
                return;
            }

            if (p == "Appear 2" || p == "Idle")
            {
                // 可见（Appear 2：mesh 开、Colliders 开；Idle 同理）
                visT += Time.deltaTime;
                if (visT >= VisibleTime)
                {
                    platFsm.SendEvent(VanishEvent);
                    visT = 0f;
                    goneT = 0f;
                }
            }
            else if (p == "Disappeared")
            {
                // 完全消失（mesh 关、Colliders 关）
                goneT += Time.deltaTime;
                if (goneT >= HiddenTime)
                {
                    CancelFlash();              // FlashingGhostWounded 会一直闪，重现前掐掉
                    platFsm.SendEvent(AppearEvent);
                    goneT = 0f;
                    visT = 0f;
                }
            }
            // 其余（Appear 1 / Pre Vanish / Vanish Antic / Disappear）是过渡态，不计时
        }

        // ---------------- 造 / 拆 ----------------

        private void Build()
        {
            Remove();

            GameObject tpl = GameObject.Find(TemplatePath);
            if (tpl == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到模板 " + TemplatePath + "，P2 中段宽平台没造");
                return;
            }

            GameObject parentGo = GameObject.Find(ArenaFsmOwner);
            Transform parent = parentGo != null ? parentGo.transform : transform.parent;

            GameObject c = null;
            try
            {
                c = Instantiate(tpl, parent);
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: 克隆宽平台抛异常 " + e);
            }
            if (c == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 宽平台没克隆出来，P2 中段宽平台没造");
                return;
            }

            c.name = "P2 Wide Plat";
            c.transform.position = new Vector3(TargetX, TargetY, tpl.transform.position.z);
            c.SetActive(true);

            plat = c;
            platFsm = null;
            foreach (PlayMakerFSM f in c.GetComponents<PlayMakerFSM>())
                if (f != null && f.Fsm != null && f.FsmName == PlatFsmName) { platFsm = f; break; }

            if (platFsm == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 克隆体上没有 " + PlatFsmName +
                                             " FSM，P2 中段宽平台拆掉");
                Remove();
                return;
            }

            Patch(platFsm.Fsm);

            started = false;
            visT = 0f;
            goneT = 0f;

            OrbRadiance.Instance.Log("[wideplat] P2 中段宽平台已生成 @ (" + TargetX + ", " + TargetY +
                                     ")；周期 显示 " + VisibleTime + "s / 消失 " + HiddenTime +
                                     "s，收招 预警 " + WarnTime + "s + 闪烁 " + FlashTime + "s（踩点 y≈" +
                                     (TargetY + 0.64f) + "）");
        }

        private void Remove()
        {
            if (plat != null) Destroy(plat);
            plat = null;
            platFsm = null;
            started = false;
            visT = 0f;
            goneT = 0f;
        }

        // ---------------- 压时长 ----------------

        // 只动克隆体 FSM 实例里两个 Wait 的字段值，不增删动作。
        private static void Patch(Fsm fsm)
        {
            FsmState pre = fsm.GetState("Pre Vanish");
            if (pre != null)
            {
                WaitRandom w = pre.Actions.OfType<WaitRandom>().FirstOrDefault();
                if (w != null)
                {
                    if (w.timeMin != null) w.timeMin.Value = WarnTime;
                    if (w.timeMax != null) w.timeMax.Value = WarnTime;
                }
                else OrbRadiance.Instance.LogWarn("OrbRadiance: 宽平台 Pre Vanish 里找不到 WaitRandom，预警时长未改");
            }
            else OrbRadiance.Instance.LogWarn("OrbRadiance: 宽平台 FSM 里没有 Pre Vanish 状态，预警时长未改");

            FsmState antic = fsm.GetState("Vanish Antic");
            if (antic != null)
            {
                Wait w = antic.Actions.OfType<Wait>().FirstOrDefault();
                if (w != null) w.time.Value = FlashTime;
                else OrbRadiance.Instance.LogWarn("OrbRadiance: 宽平台 Vanish Antic 里找不到 Wait，闪烁时长未改");
            }
            else OrbRadiance.Instance.LogWarn("OrbRadiance: 宽平台 FSM 里没有 Vanish Antic 状态，闪烁时长未改");
        }

        // FlashingGhostWounded 是 repeatFlash = true，会一直闪；Disappear / Disappeared 都不掐它。
        private void CancelFlash()
        {
            if (plat == null) return;
            SpriteFlash sf = plat.GetComponent<SpriteFlash>();
            if (sf != null) sf.CancelFlash();
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
                                                 " 的 Control FSM，重打时 P2 中段宽平台不会自动拆");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

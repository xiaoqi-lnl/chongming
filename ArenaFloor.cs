using System;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // P1 仅 HARD 撤除地板：等待 FloorRemoveDelay，且地刺全部升起（GroundReady）后才隐藏。
    // 分别处理容器子物体和自带渲染器的场景物体，退出或重战时恢复原状。


    internal class ArenaFloor : MonoBehaviour
    {
        // HidePaths 隐藏子对象；HideSelfPaths 直接隐藏自身（两根边缘装饰柱子不含碰撞与 FSM）。


        private static readonly string[] HidePaths =
        {
            "TileMap Render Data/Scenemap",
            "GG_Arena_Prefab/Floor"
        };

        private static readonly string[] HideSelfPaths =
        {
            "gg_pillar_corner_large",
            "gg_pillar_corner_large (1)"
        };

        private PlayMakerFSM fsm;
        private readonly Transform[] hideRoots = new Transform[HidePaths.Length];
        private readonly Transform[] hideSelfRoots = new Transform[HideSelfPaths.Length];
        private string lastState;
        private float armTime;
        private bool armed;
        private bool removed;

        internal void Init(PlayMakerFSM f)
        {
            fsm = f;
        }

        private void Update()
        {
            if (fsm == null || fsm.Fsm == null) return;

            // 总开关关掉：把场地还回来（已经移除过才动）。lastState 清空，
            // 重新打开时按“状态变了”重新武装计时。
            if (!OrbRadiance.Settings.Enabled)
            {
                armed = false;
                lastState = null;
                Restore();
                return;
            }

            // NORMAL / EASY 保留原版连续地板；只有 HARD 移除地板。
            if (!OrbRadiance.EffectiveHardMode)
            {
                armed = false;
                lastState = fsm.Fsm.ActiveStateName;
                Restore();
                return;
            }

            string cur = fsm.Fsm.ActiveStateName;

            if (cur == lastState)
            {
                // 除了等够 FloorRemoveDelay，还得等刺真的立起来（ArenaSpikes.GroundReady）：
                // 单根刺从收到 UP 到 SetPolygonCollider(true) 要走完原版上升动画
                // （Downed → Floor Antic 等 2s → Spike Up），这期间撤地板玩家直接掉进虚空。
                if (armed && Time.time - armTime >= OrbRadiance.FloorRemoveDelay && ArenaSpikes.GroundReady)
                    Remove();
                return;
            }
            lastState = cur;

            // 重打会把场地恢复回来，让下一轮计时照常生效
            if (cur == "Battle Start" || cur == "Refight" || cur == "Init" || cur == "Setup")
                Restore();

            if (cur == "Battle Start")
            {
                armTime = Time.time;
                armed = true;
            }
        }

        private void Remove()
        {
            armed = false;
            bool ok = false;
            for (int i = 0; i < HidePaths.Length; i++)
            {
                if (hideRoots[i] == null) hideRoots[i] = Find(HidePaths[i]);
                if (hideRoots[i] == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + HidePaths[i] + "，P1 场地未完全移除");
                    continue;
                }
                for (int c = 0; c < hideRoots[i].childCount; c++)
                    hideRoots[i].GetChild(c).gameObject.SetActive(false);
                ok = true;
            }
            for (int i = 0; i < HideSelfPaths.Length; i++)
            {
                if (hideSelfRoots[i] == null) hideSelfRoots[i] = Find(HideSelfPaths[i]);
                if (hideSelfRoots[i] == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + HideSelfPaths[i] + "，P1 场地未完全移除");
                    continue;
                }
                hideSelfRoots[i].gameObject.SetActive(false);
                ok = true;
            }
            removed = ok;
        }

        private void Restore()
        {
            armed = false;
            if (!removed) return;
            for (int i = 0; i < hideRoots.Length; i++)
            {
                if (hideRoots[i] == null) continue;
                for (int c = 0; c < hideRoots[i].childCount; c++)
                    hideRoots[i].GetChild(c).gameObject.SetActive(true);
            }
            for (int i = 0; i < hideSelfRoots.Length; i++)
                if (hideSelfRoots[i] != null) hideSelfRoots[i].gameObject.SetActive(true);
            removed = false;
        }

        private static Transform Find(string path)
        {
            GameObject go = GameObject.Find(path);
            return go == null ? null : go.transform;
        }
    }
}

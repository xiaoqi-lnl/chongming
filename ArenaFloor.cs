using System;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // P1 场地移除：入场动画播完（Boss Control 的 Control FSM 进入 Battle Start）后
    // 等 FloorRemoveDelay 秒，把场地全部 SetActive(false)（见下面 HidePaths / HideSelfPaths）。
    //
    // 但纯计时不够（2026-09-19 用户定「地板等刺立好再撤」）：FloorRemoveDelay 是 1.5s，
    // 而地刺要 2.3~2.7s 才立得住（ArenaSpikes 里的实测），所以撤除条件再 AND 上
    // ArenaSpikes.GroundReady —— 只有 24 根刺都进了 `Up` 才撤。实际生效时间取决于刺，
    // 不是写死的 1.5s。
    //
    // 容器只关子物体、不关容器本身，避免有东西按路径引用容器时悬空。
    internal class ArenaFloor : MonoBehaviour
    {
        // P1 场地分两部分，关法不一样（区别在 SpriteRenderer 挂在谁身上）：
        //
        // 【容器型 HidePaths】渲染器在**子物体**上 → 关掉容器下所有子物体
        //   Scenemap = TileMap Render Data/Scenemap 下 Chunk 0 1 / 0 2 的
        //              MeshFilter+MeshRenderer+EdgeCollider2D（碰撞 + 底层 mesh）
        //   Floor    = GG_Arena_Prefab/Floor 下 40 个 SpriteRenderer（GG_scenery_0014_7 +
        //              GG_scenery_0013_8 + 6 组 fg_stones），y≈19.5、横跨 x 18~88
        //
        // 【自身型 HideSelfPaths】渲染器就在**物体自己**身上（场景根、路径只有一节，
        // 2026-09-17 实测 `[floordbg] path=gg_pillar_corner_large` 单节确认）→ 「关所有子物体」
        // 对它等于什么都没关，必须关它本身。这就是上次加进 HidePaths 没生效的原因。
        //   世界 (48.92,17.66) / (71.97,17.66)，正好压在可见地面内边界 48.22 / 72.95 的左右两端；
        //   `[floordbg]` 实测 bounds y 15.15~20.18 —— 顶面齐地面(y=20)，场地在时被地面贴图挡着，
        //   地面一没就露出来（用户报「地面左右两侧还有两张贴图」）。
        // 两根柱子只有 Transform + SpriteRenderer，无碰撞体、无 FSM，全场景 FSM 导出里搜不到
        // 这两个名字 → 关掉安全。
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

using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // P2 横刺（comb `Radiant Nail Comb` 的 `Type` 7 = L 2 / 8 = R 2）由直线横穿改成正弦轨迹
    // （2026-10-02 用户定：全部 P2 横刺、波长 15 单位、振幅 5）。
    //
    // 原版怎么动（2026-10-02 探针实测，comb 自己的 `Control` FSM）：
    //   Type=7/8 时 `Init →（瞬时态 Type / L 2 / R 2，单帧内连跳）→ Antic(0.4s) → Tween → Reset → Recycle`
    //   **`L 2` / `R 2` 是瞬时态，每帧采一次样根本看不到** —— 不能拿状态名判断 P2 横刺，要用 `Type` 变量。
    //   `Tween` 对子物体 `Nails` 做 iTweenMoveBy((0,50,0), space=Self)：旋转 270°/90° 下钉子排
    //   （沿自身 local X）在世界里竖成一堵墙，`Nails` 沿自身 +Y = 世界 ±X 平移 → 墙水平横穿。
    //   实测 `nailsWorld.y == rootY`、`Nails.localPosition.y` 从 0 涨到 50，落点 `L 2`=(35.4,36.73)、
    //   `R 2`=(84.8,38.73)。
    //
    // 改法：每帧把 comb **根节点**的世界 y 设成 baseY + A·sin(2π·(x−x₀)/λ)，x 取 `Nails` 当前世界 x。
    //   只动根节点 y（iTween 只写 Nails.localPosition），且根节点 y 就是墙的中线 y → 整堵墙上下起伏。
    //
    // 挂点：comb 是全局池对象，复用走 OnEnable —— 在 OrbRadiance.OnFsmEnable 里按物体名认它
    //   （同 NailTopSweep.IsComb）。`Type` 不是 7/8（垂直剑阵 3、P1 横刺 4/5、TL/TR/Top2…）一律不碰。
    internal class P2NailCombSine : MonoBehaviour
    {
        private const float WaveLength = 15f;  // 波长（单位）
        private const float Amplitude = 5f;    // 振幅（中线上下各摆这么多）

        private const int TypeL2 = 7;
        private const int TypeR2 = 8;

        // 落点已摆好、可以抓基准的状态（瞬时态 Type / L 2 / R 2 之后）
        private static readonly string[] ReadyStates = { "Antic", "Tween" };
        // 重新生成 / 回收 → 收手
        private static readonly string[] ResetStates = { "Init", "Recycle" };

        private PlayMakerFSM control;   // comb 自己的 Control FSM

        private Transform nails;
        private bool nailsTried;

        private bool armed;
        private bool needBase;
        private bool hasBase;
        private float baseY;
        private float x0;

        private bool logged;

        internal void Init(PlayMakerFSM f) => control = f;

        private void LateUpdate()
        {
            if (control == null || control.Fsm == null) return;

            if (!OrbRadiance.Settings.Enabled)
            {
                Disarm();
                return;
            }

            if (!EnsureNails()) return;

            FsmInt typeVar = control.Fsm.Variables.GetFsmInt("Type");
            int type = typeVar != null ? typeVar.Value : -1;
            string s = control.Fsm.ActiveStateName;

            // 只有 P2 横刺（Type 7/8）才碰；生成 / 回收态收手
            if ((type != TypeL2 && type != TypeR2) || System.Array.IndexOf(ResetStates, s) >= 0)
            {
                Disarm();
                return;
            }

            if (!armed)
            {
                armed = true;
                needBase = true;
            }

            // 落点摆好后（Antic/Tween）才抓基准，免得抓到还没 SetPosition 的 rootY=0
            if (needBase && System.Array.IndexOf(ReadyStates, s) >= 0)
            {
                baseY = transform.position.y;
                x0 = nails.position.x;
                hasBase = true;
                needBase = false;
            }

            if (!hasBase) return;

            float x = nails.position.x;
            Vector3 p = transform.position;
            p.y = baseY + Amplitude * Mathf.Sin(2f * Mathf.PI * (x - x0) / WaveLength);
            transform.position = p;

            if (!logged)
            {
                logged = true;
                OrbRadiance.Instance.Log("[combsine] P2 横刺（Type " + type + "）走正弦轨迹：波长 " +
                                         WaveLength + "、振幅 " + Amplitude + "、中线 y = " + baseY +
                                         "、起点 x = " + x0);
            }
        }

        // 停手：把偏移写回基准 y（没抓过基准就什么都不写，等原版 SetPosition 自己摆位）
        private void Disarm()
        {
            if (armed && hasBase)
            {
                Vector3 p = transform.position;
                p.y = baseY;
                transform.position = p;
            }
            armed = false;
            needBase = false;
            hasBase = false;
        }

        private bool EnsureNails()
        {
            if (nails != null) return true;

            Transform t = transform.Find("Nails");
            if (t != null)
            {
                nails = t;
                return true;
            }

            if (!nailsTried)
            {
                nailsTried = true;
                OrbRadiance.Instance.LogWarn("OrbRadiance: comb 找不到子物体 Nails，P2 横刺正弦轨迹不生效");
            }
            return false;
        }
    }
}

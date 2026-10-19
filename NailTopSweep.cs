using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // 垂直剑阵（P1 `Attack Commands/Comb Top`，发 COMB TOP ×4）改成从下往上飞。
    //
    // comb 预制体 `Radiant Nail Comb` 的 `Control` FSM 里，`Type` 决定走哪个方向状态：
    //   3 = Top（垂直剑阵用）、4 = L、5 = R、6 = Top 2、7 = L 2、8 = R 2、1 = TL、2 = TR
    // 每个方向状态做两件事：把 comb 自己 SetPosition 到起点、把 `float Rotation` 写成一个角度。
    // 之后 `Antic` 做 `SetRotation(owner, z=Rotation)`，`Tween` 再对子物体 `Nails` 做
    // `iTweenMoveBy(vector=(0,50,0), space=Self)` —— 也就是「整波沿自身 +Y 平移」。
    // 所以 Rotation 就是方向：180° 时自身 +Y = 世界 -Y（往下砸），0° 时 = 世界 +Y（往上飞）。
    // 钉子本身是 SetParent 到 `Nails` 的（resetLocalRotation=false，局部旋转保持单位），
    // 世界朝向跟着 comb 走 —— 改 Rotation 会让贴图朝向和位移方向一起翻正，不用另动 Tween。
    //
    // 只原位改 `Top` 状态里两个字段值，不增删动作（见 [[feedback-fsm-index-stability]]）：
    //   `SetPosition.y`         33.5 → OrbRadiance.NailSpawnY(17)
    //   `SetFloatValue Rotation` 180 → OrbRadiance.NailUpRotation(0)
    //
    // 生效范围：`Type = 3` 全游戏只有两处发 —— `Attack Commands/Comb Top`（垂直剑阵）和
    // `Absolute Radiance/Control` 的 `Rage Comb`（无限剑雨）。两者是同一个 comb、同一个 `Top`
    // 状态，所以这一改两处一起翻，无法只翻一个。
    //
    // 挂点：comb 是全局池对象（`SpawnObjectFromGlobalPool`），每次复用都走 OnEnable，
    // 所以在 On.PlayMakerFSM.OnEnable 里按物体名认它最稳 —— 预制体本身是未激活的资产，
    // 收不到 OnEnable；池里预建的空闲实例也没收过，都在第一次真正生成时才被改到。
    internal static class NailTopSweep
    {
        private const string CombNamePrefix = "Radiant Nail Comb";
        private const string StateName = "Top";
        private const string RotationVar = "Rotation";

        internal static bool IsComb(string goName) => goName != null && goName.StartsWith(CombNamePrefix);

        // 记下改之前的值，总开关关掉时写回去。池子实例有限（复用），所以不会无限增长。
        private static readonly Dictionary<FsmFloat, float> Original = new Dictionary<FsmFloat, float>();

        internal static void Patch(Fsm fsm)
        {
            FsmState top = fsm.GetState(StateName);
            if (top == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: comb 的 Control FSM 里没有 " + StateName + " 状态，未改动");
                return;
            }

            bool moved = false;
            bool turned = false;
            foreach (FsmStateAction a in top.Actions)
            {
                if (a is SetPosition sp)
                {
                    if (sp.y == null || sp.y.IsNone) continue;
                    Set(sp.y, OrbRadiance.NailSpawnY);
                    moved = true;
                }
                else if (a is SetFloatValue sfv)
                {
                    if (sfv.floatVariable == null || sfv.floatVariable.Name != RotationVar) continue;
                    Set(sfv.floatValue, OrbRadiance.NailUpRotation);
                    turned = true;
                }
            }

            if (!moved)
                OrbRadiance.Instance.LogWarn("OrbRadiance: comb 的 " + StateName + " 状态里找不到 SetPosition.y，生成高度未改");
            if (!turned)
                OrbRadiance.Instance.LogWarn("OrbRadiance: comb 的 " + StateName + " 状态里找不到 SetFloatValue(Rotation)，方向未改");
        }

        private static void Set(FsmFloat f, float v)
        {
            if (!Original.ContainsKey(f)) Original[f] = f.Value;
            f.UseVariable = false;
            f.Value = v;
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<FsmFloat, float> kv in Original)
                if (kv.Key != null) kv.Key.Value = kv.Value;
        }
    }
}

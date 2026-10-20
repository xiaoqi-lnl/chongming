using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // 将 P1 垂直剑阵的原版 Comb Top 改为自下向上发射，仅原位修改方向与生成高度。


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

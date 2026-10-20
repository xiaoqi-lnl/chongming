using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // P2 横刺保留原版 FSM 结构，原位调整生成高度、方向、速度与横向抖动。
    // 修改时不增删动作，避免其他 Mod 依赖原版动作索引发生冲突。


    internal static class P2CombPatch
    {
        private const float RiseX = 60.5f;         // 场地中线（内边界 48.2233 / 72.9484 的中点）
        private const float RiseY = 31f;           // 前两波生成高度
        private const float FallY = 80f;           // 第三波下落起点
        private const float FallRotation = 180f;   // 第三波朝向（自身 +Y = 世界 −Y → 往下掉）
        private const float NailSpeedScale = 1.8f; // 横刺速度倍率（原版 1 倍）
        private const float SpawnXJitter = 5f;     // 每波生成 x 的随机抖动幅度（±，世界单位）2026-10-05 用户定

        private static readonly string[] RiseStates = { "L 2", "R 2" };
        private const string FallState = "Top 2";
        private const string RotationVar = "Rotation";
        private const string NailSpeedVar = "Nail Speed";

        // 记下改之前的值，总开关关掉时写回去。池子实例有限（复用），不会无限增长。
        // ⚠ 速度用「原始值 × 倍率」写，**不能**拿当前值乘 —— 池化复用会多次 Patch，乘当前值会越乘越大。
        private static readonly Dictionary<FsmFloat, float> Original = new Dictionary<FsmFloat, float>();

        internal static void Patch(Fsm fsm)
        {
            foreach (string name in RiseStates)
                PatchState(fsm, name, RiseX, RiseY, OrbRadiance.NailUpRotation);
            PatchState(fsm, FallState, RiseX, FallY, FallRotation);
        }

        private static void PatchState(Fsm fsm, string name, float x, float y, float rotation)
        {
            FsmState st = fsm.GetState(name);
            if (st == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: comb 的 Control FSM 里没有 " + name +
                                             " 状态，P2 横刺该波未改");
                return;
            }

            bool moved = false, turned = false, scaled = false;
            foreach (FsmStateAction a in st.Actions)
            {
                if (a is SetPosition sp)
                {
                    // x 每次 Patch 现取随机（Patch 每波重跑，见类头 4)）；y 是固定值
                    if (sp.x != null && !sp.x.IsNone)
                    { Set(sp.x, x + UnityEngine.Random.Range(-SpawnXJitter, SpawnXJitter)); moved = true; }
                    if (sp.y != null && !sp.y.IsNone) { Set(sp.y, y); moved = true; }
                }
                else if (a is SetFloatValue sfv)
                {
                    if (sfv.floatVariable == null) continue;
                    if (sfv.floatVariable.Name == RotationVar)
                    {
                        Set(sfv.floatValue, rotation);
                        turned = true;
                    }
                    else if (sfv.floatVariable.Name == NailSpeedVar)
                    {
                        Scale(sfv.floatValue, NailSpeedScale);
                        scaled = true;
                    }
                }
            }

            if (!moved)
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + name + " 状态里找不到 SetPosition.x/y，生成位置未改");
            if (!turned)
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + name + " 状态里找不到 SetFloatValue(Rotation)，方向未改");
            if (!scaled)
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + name + " 状态里找不到 SetFloatValue(Nail Speed)，速度未改");
        }

        private static void Set(FsmFloat f, float v)
        {
            if (f == null) return;
            if (!Original.ContainsKey(f)) Original[f] = f.Value;
            f.UseVariable = false;
            f.Value = v;
        }

        // 倍率应用在「记录的原始值」上 → 重复 Patch 幂等
        private static void Scale(FsmFloat f, float factor)
        {
            if (f == null) return;
            if (!Original.ContainsKey(f)) Original[f] = f.Value;
            f.UseVariable = false;
            f.Value = Original[f] * factor;
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<FsmFloat, float> kv in Original)
                if (kv.Key != null) kv.Key.Value = kv.Value;
        }
    }
}

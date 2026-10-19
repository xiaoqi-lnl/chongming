using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace OrbRadiance
{
    // P2 横刺（comb `Control` 的 `Type` 7 = L 2 / 8 = R 2）的改动，都**原位改字段值、不增删动作**
    // （见 [[feedback-fsm-index-stability]]）：
    //   1) 前两波改成**从下往上飞**（2026-10-02 用户定）：「横刺变为从下往上出，y=31 处生成，接着往上飞行」，
    //      横排、x 居中、只 P2。
    //   2) 速度 ×1.8（2026-10-02 用户定，替换早先的 1.3×）：「飞行速度改为原版 1.8 倍」。
    //   3) 第三波改成**从上往下**（2026-10-02 用户定）：「一个横刺技能里三轮，前两轮从下往上，
    //      第三轮从上往下（y=80 处下落）」。第三波走 comb 的 `Type 6 = Top 2` 状态 ——
    //      原版 P2 的攻击池里没有垂直剑阵（`A2 Choice` 只抽 横刺/眼光束/光墙/脸刺/光球），
    //      所以 `Top 2`（`Comb Top 2` 发 COMB TOP2 才进）在 P2 是条死路，正好拿来做第三波，
    //      不会碰到别的招（怎么把第三波导到这条路上见 P2SweepWaves.cs）。
    //   4) 生成 x 加随机抖动（2026-10-05 用户定）：「p2 剑雨（= 横刺）的生成位置在 x 方向上做
    //      随机偏移 ±5 世界单位」。每波基准 x 仍是场地中线 60.5，各路生成时再 ±SpawnXJitter。
    //      随机点放在 Patch 里 —— comb 是池化对象、每波生成都走 OnEnable → Patch 会重跑，
    //      于是在这儿取随机数就是「每波各随机一次」（详见下方 PatchState）。
    //
    // 原版怎么动 / 每个字段的含义（comb 自己的 `Control` FSM，导出
    // `Dream/Dream_Final_Boss.unity/Radiant_Nail_Comb__1__Control__fsm_2617.md`）：
    //   `L 2` 把 comb 根节点 SetPosition 到 (35.4, 36.73)、`Rotation` 写 270°；
    //   `R 2` 到 (84.8, 38.73)、`Rotation` 写 90°。两态都：Nail Speed = 10 →
    //   `BoolTest Radiant`（`Attack Commands/Comb L 2` 把它置 true，所以不会提前 FINISHED）→ Nail Speed = 13。
    //   `Top 2` 到 (60.7, 58)、`Rotation` 写 180°、Nail Speed = 12（无 Radiant 判断）。
    //   之后 `Antic` 做 `SetRotation(owner, z=Rotation)`，`Tween` 对子物体 `Nails` 做
    //   `iTweenMoveBy(vector=(0,50,0), speed=Nail Speed, space=Self)`
    //   —— 「整排沿自身 +Y、以 Nail Speed 单位/秒平移」。
    //   270°/90° 下钉子排（沿自身 local X）在世界里竖成一堵**竖墙**，`Nails` 沿自身 +Y = 世界 ±X
    //   平移 → 墙**水平横穿**竞技场。钉排以 comb 根节点为中心（`Spawn Centre` → 向右 `RG1-3` /
    //   向左 `LG1-3` 对称铺开，间距 `Increment=2`）。
    //
    // 改动（只原位改这几个状态的字段值）：
    //   `L 2` / `R 2`（前两波，从下往上）：
    //     `SetPosition.x`            35.4 / 84.8 → 60.5（场地中线；横排居中）
    //     `SetPosition.y`            36.73 / 38.73 → 31（生成高度）
    //     `SetFloatValue Rotation`   270 / 90 → 0（0° 时自身 +Y = 世界 +Y → 往上飞）
    //     `SetFloatValue Nail Speed` ×1.8（10→18、13→23.4；Radiant=true 生效的是 13→23.4）
    //   `Top 2`（第三波，从上往下）：
    //     `SetPosition.x`            60.7 → 60.5
    //     `SetPosition.y`            58 → 80（下落起点）
    //     `SetFloatValue Rotation`   180 → 180（保持：180° 时自身 +Y = 世界 −Y → 往下掉）
    //     `SetFloatValue Nail Speed` ×1.8（12→21.6）
    //   `Tween` 的 `iTweenMoveBy` **不用动** —— 它本来就沿自身 +Y、速度取 `Nail Speed`，
    //   改了 Rotation 后方向自然变对应世界方向、改了 Nail Speed 后速度自然变快；钉子贴图朝向也跟着 comb 一起翻。
    //
    // 只影响 P2：`L 2`/`R 2` 只有 `Type`=7/8 时才走到，`Top 2` 只有 `Type`=6 时才走到；
    //   Type 7/8/6 全由 Attack Commands 的 `Comb L 2`/`Comb R 2`/`Comb Top 2` 设置，
    //   而这三条都只在 P2 由本 mod 的 P2SweepWaves 路由触发。P1 横刺走 `L`/`R`（Type 4/5），不受影响。
    //
    // 挂点：comb 是全局池对象（复用走 OnEnable），在 `OrbRadiance.OnFsmEnable` 里按物体名认它
    //   （同 NailTopSweep.IsComb）。总开关关掉走 `OrbRadiance.RestorePatched` → `Restore()`。
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

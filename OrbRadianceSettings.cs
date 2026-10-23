using System;

namespace OrbRadiance
{
    // 全局设置由 Modding API 保存；Enabled 可在 Mods 菜单即时切换。
    
    
    [Serializable]
    public class OrbRadianceSettings
    {
        // 总开关。关掉后：已改过的光球 FSM 字段还原、P1 场地还回来、虚空不再秒杀。
        // 场地计时器和虚空相位闩锁要重新进一次战斗才会重新武装。
        public bool Enabled = true;

        // 未通过雕像选择模式时使用的持久化难度回退：true = H，false = N。已选雕像时由当前战斗模式覆盖。
        // 有效难度会影响多项机制；P2 光墙只是其中一项：N 使用双墙，H 使用随机预警墙（见 BeamSweeperDual.cs / HardBeamWall.cs）。
        // P1 双墙的速度倍率为 Z=1、N=1.5、H=2。
        public bool HardMode = false;
    }
}

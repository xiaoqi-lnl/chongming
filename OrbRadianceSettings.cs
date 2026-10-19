using System;

namespace OrbRadiance
{
    // 全局设置（Modding API IGlobalSettings 保存，落在
    // %AppData%/../LocalLow/Team Cherry/Hollow Knight/OrbRadiance.GlobalSettings.json）。
    // Enabled 走菜单开关；改开关即时生效（见 OrbRadiance.SetEnabled）。
    [Serializable]
    public class OrbRadianceSettings
    {
        // 总开关。关掉后：已改过的光球 FSM 字段还原、P1 场地还回来、虚空不再秒杀。
        // 场地计时器和虚空相位闩锁要重新进一次战斗才会重新武装。
        public bool Enabled = true;

        // hard 难度总开关（照天国余晖 RadianceBossSettings.HardMode）：true = hard，false = 普通（默认）。
        // 只有 P2 光墙挂在它下面：普通 = 双墙（BeamSweeperDual.cs，两墙从 x=61 向两边走）；
        // hard = 随机位置预警柱 + 齐射（HardBeamWall.cs，同时抑制原版横扫墙）。
        // 注意 P1 光墙在两种难度下都是双墙（HardBeamWall 只改 P2）。和其它改动一样，进战斗时生效。
        public bool HardMode = false;
    }
}

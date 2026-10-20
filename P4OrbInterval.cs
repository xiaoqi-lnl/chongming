using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P4 使用原版 Attack Commands 光球循环；仅在 Final Orbs 启用时修改等待时间。
    // Z/N 缩短 Orb Pause 与 FinalOrb Pause，三难度的 Orb Summon 统一为 0.55s；离开 P4 时恢复原值。
    
    
    internal sealed class P4OrbInterval : MonoBehaviour
    {
        private const float FinalSummonSeconds = 0.55f; // Z/N/H 的 P4 Orb Summon 等待时间。
        private const float EasyFinalPauseSeconds = 0.61f; 
        private const float NormalFinalPauseSeconds = 0.27f; 
        private const float EasyOrbPauseFactor = 0.8f; 
        private const float NormalOrbPauseFactor = 0.6f; 

        private PlayMakerFSM commands;
        private Wait summonWait;
        private Wait orbPauseWait;
        private Wait finalPauseWait;
        private float originalSummon;
        private float originalOrbPause;
        private float originalFinalPause;
        private bool captured;
        private bool missingWarned;
        private string lastMode;
        private static P4OrbInterval active;

        internal void Init(PlayMakerFSM fsm)
        {
            commands = fsm;
            active = this;
        }

        private void Update()
        {
            if (!captured && !CaptureNativeWaits()) return;
            if (!OrbRadiance.Settings.Enabled || commands == null || commands.Fsm == null)
            {
                RestoreSelf();
                return;
            }

            FsmBool final = commands.Fsm.Variables.GetFsmBool("Final Orbs");
            string mode = final == null || !final.Value ? "off" :
                OrbRadiance.EffectiveHardMode ? "H" :
                OrbRadiance.EffectiveEasyMode ? "Z" : "N";
            if (mode == lastMode) return;

            // 原版 Orb Summon 也用于前期阶段，离开 P4 时必须恢复；
            // 只有 Z/N 缩短其余两段等待。
            float summon = mode == "off" ? originalSummon : FinalSummonSeconds;
            float orbPause = mode == "Z" ? originalOrbPause * EasyOrbPauseFactor :
                mode == "N" ? originalOrbPause * NormalOrbPauseFactor : originalOrbPause;
            float finalPause = mode == "Z" ? EasyFinalPauseSeconds :
                mode == "N" ? NormalFinalPauseSeconds : originalFinalPause;

            summonWait.time.Value = summon;
            orbPauseWait.time.Value = orbPause;
            finalPauseWait.time.Value = finalPause;
            lastMode = mode;

            if (mode != "off")
                OrbRadiance.Instance?.Log("[p4-orbs] " + mode +
                    " Orb Summon=" + summon.ToString("0.###") + "s (native " + originalSummon.ToString("0.###") +
                    "), Orb Pause=" + orbPause.ToString("0.###") + "s (native " + originalOrbPause.ToString("0.###") +
                    "), FinalOrb Pause=" + finalPause.ToString("0.###") + "s (native " + originalFinalPause.ToString("0.###") +
                    "), estimated waits/ball=" + (summon + orbPause + finalPause).ToString("0.###") + "s");
        }

        private bool CaptureNativeWaits()
        {
            if (commands == null || commands.Fsm == null) return false;
            summonWait = GetWait("Orb Summon");
            orbPauseWait = GetWait("Orb Pause");
            finalPauseWait = GetWait("FinalOrb Pause");
            if (summonWait == null || orbPauseWait == null || finalPauseWait == null)
            {
                if (!missingWarned)
                {
                    missingWarned = true;
                    OrbRadiance.Instance?.LogWarn("[p4-orbs] Native Wait not found: Orb Summon=" +
                        (summonWait != null) + ", Orb Pause=" + (orbPauseWait != null) +
                        ", FinalOrb Pause=" + (finalPauseWait != null) + "; P4 timing changes skipped");
                }
                return false;
            }

            originalSummon = summonWait.time.Value;
            originalOrbPause = orbPauseWait.time.Value;
            originalFinalPause = finalPauseWait.time.Value;
            captured = true;
            return true;
        }

        private Wait GetWait(string stateName)
        {
            FsmState state = commands.Fsm.GetState(stateName);
            return state == null ? null : state.Actions.OfType<Wait>().FirstOrDefault(a => a.time != null);
        }

        private void RestoreSelf()
        {
            if (captured)
            {
                if (summonWait != null && summonWait.time != null) summonWait.time.Value = originalSummon;
                if (orbPauseWait != null && orbPauseWait.time != null) orbPauseWait.time.Value = originalOrbPause;
                if (finalPauseWait != null && finalPauseWait.time != null) finalPauseWait.time.Value = originalFinalPause;
            }
            lastMode = "off";
        }

        internal static void Restore()
        {
            if (active != null) active.RestoreSelf();
        }

        private void OnDestroy()
        {
            RestoreSelf();
            if (active == this) active = null;
        }
    }
}

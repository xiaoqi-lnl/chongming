using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    internal static class CombatAudioMixerRoute
    {
        private static readonly HashSet<string> warnedLabels = new HashSet<string>();

        internal static void UseNativeSfxGroup(AudioSource source, AudioPlayerOneShotSingle nativeAction, string label)
        {
            GameObject playerPrefab = nativeAction == null || nativeAction.audioPlayer == null
                ? null : nativeAction.audioPlayer.Value;
            AudioSource prefabSource = playerPrefab == null ? null : playerPrefab.GetComponent<AudioSource>();
            if (source == null || prefabSource == null || prefabSource.outputAudioMixerGroup == null)
            {
                if (warnedLabels.Add(label))
                    OrbRadiance.Instance?.LogWarn("OrbRadiance: " + label + " 原版 SFX 混音组引用缺失；音效路由未设置");
                return;
            }

            source.outputAudioMixerGroup = prefabSource.outputAudioMixerGroup;
        }
    }
}

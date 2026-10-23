using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.Audio;

namespace OrbRadiance
{
    internal static class BluePhaseBindingAudio
    {
        internal const string InspectPath = "GG_Challenge_Door (3)/Door/Unlocked Set/Inspect";

        private struct CachedSound
        {
            internal AudioEvent Event;
            internal AudioMixerGroup Mixer;
            internal bool Available;
        }

        private static CachedSound soul;
        private static CachedSound shell;
        private static CachedSound nail;
        private static CachedSound charms;
        private static readonly List<GameObject> ActiveSources = new List<GameObject>();
        private static bool missingLogged;

        internal static void Configure(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects, Action<string> logWarning)
        {
            soul = shell = nail = charms = default(CachedSound);
            if (preloadedObjects == null || !preloadedObjects.TryGetValue("GG_Atrium", out Dictionary<string, GameObject> atrium) ||
                atrium == null || !atrium.TryGetValue(InspectPath, out GameObject inspect) || inspect == null)
            {
                WarnOnce(logWarning, "GG_Atrium 精确预载对象缺失；跳过蓝调束缚音效");
                return;
            }

            PlayMakerFSM challengeFsm = inspect.GetComponents<PlayMakerFSM>()
                .FirstOrDefault(fsm => fsm != null && fsm.Fsm != null && fsm.FsmName == "Challenge UI");
            // 初始化动作数据以解码预制体引用；不执行FSM状态或动作。
            challengeFsm?.Fsm.InitData();
            FsmState openUi = challengeFsm == null ? null : challengeFsm.Fsm.GetState("Open UI");
            ShowBossDoorChallengeUI show = openUi == null
                ? null : openUi.Actions.OfType<ShowBossDoorChallengeUI>().FirstOrDefault();
            GameObject prefab = show == null || show.prefab == null ? null : show.prefab.Value;
            BossDoorChallengeUI ui = prefab == null ? null : prefab.GetComponent<BossDoorChallengeUI>();
            if (ui == null)
            {
                WarnOnce(logWarning, "GG_Atrium Challenge UI/Open UI 原版界面预制体缺失；跳过蓝调束缚音效");
                return;
            }

            bool complete = true;
            complete &= TryCache(ui.boundSoulButton, out soul);
            complete &= TryCache(ui.boundHeartButton, out shell);
            complete &= TryCache(ui.boundNailButton, out nail);
            complete &= TryCache(ui.boundCharmsButton, out charms);
            if (!complete) WarnOnce(logWarning, "原版一个或多个束缚按钮音效缺失；仅跳过缺失项");
            OrbRadiance.Instance?.Log("[blue-phase-audio] " + Describe("Soul", soul) + "; " + Describe("Shell", shell) +
                "; " + Describe("Nail", nail) + "; " + Describe("Charms", charms));
        }

        internal static void PlaySoul(Transform owner) => Play(soul, owner, "Soul");
        internal static void PlayShell(Transform owner) => Play(shell, owner, "Shell");
        internal static void PlayNail(Transform owner) => Play(nail, owner, "Nail");
        internal static void PlayCharms(Transform owner) => Play(charms, owner, "Charms");

        internal static void PlayRadianceCry(PlayMakerFSM control, Transform owner)
        {
            FsmState scream = control == null || control.Fsm == null ? null : control.Fsm.GetState("Scream");
            AudioPlayerOneShotSingle action = scream == null
                ? null : scream.Actions.OfType<AudioPlayerOneShotSingle>().FirstOrDefault();
            GameObject playerPrefab = action == null || action.audioPlayer == null ? null : action.audioPlayer.Value;
            AudioSource prefabSource = playerPrefab == null ? null : playerPrefab.GetComponent<AudioSource>();
            AudioClip clip = action == null || action.audioClip == null ? null : action.audioClip.Value as AudioClip;
            if (clip == null || playerPrefab == null)
            {
                WarnOnce(OrbRadiance.Instance == null ? null : OrbRadiance.Instance.LogWarn,
                    "辐光 Scream 原版叫声音频缺失；跳过蓝调束缚叫声");
                return;
            }

            float pitchMin = action.pitchMin == null ? 1f : action.pitchMin.Value;
            float pitchMax = action.pitchMax == null ? pitchMin : action.pitchMax.Value;
            float volume = action.volume == null ? 1f : action.volume.Value;
            float pitch = UnityEngine.Random.Range(pitchMin, pitchMax);
            AudioMixerGroup mixer = prefabSource == null ? null : prefabSource.outputAudioMixerGroup;
            PlayClip(clip, volume, pitch, mixer, owner, "Radiance Cry");
            OrbRadiance.Instance?.Log("[blue-phase-audio] Radiance Cry clip=" + clip.name + " volume=" + volume +
                " pitch=" + pitchMin + ".." + pitchMax + " mixer=" + (mixer == null ? "<default>" : mixer.name));
        }

        private static bool TryCache(BossDoorChallengeUIBindingButton button, out CachedSound cached)
        {
            cached = default(CachedSound);
            if (button == null || button.selectedSound.Clip == null || button.audioSourcePrefab == null) return false;
            cached.Event = button.selectedSound;
            cached.Mixer = button.audioSourcePrefab.outputAudioMixerGroup;
            cached.Available = true;
            return true;
        }

        private static string Describe(string label, CachedSound cached)
        {
            if (!cached.Available) return label + "=<missing>";
            return label + " clip=" + cached.Event.Clip.name + " volume=" + cached.Event.Volume +
                " pitch=" + cached.Event.PitchMin + ".." + cached.Event.PitchMax +
                " mixer=" + (cached.Mixer == null ? "<default>" : cached.Mixer.name);
        }

        private static void Play(CachedSound cached, Transform owner, string label)
        {
            if (!cached.Available || owner == null || cached.Event.Clip == null || cached.Event.Volume < Mathf.Epsilon) return;
            float pitch = cached.Event.SelectPitch();
            PlayClip(cached.Event.Clip, cached.Event.Volume, pitch, cached.Mixer, owner, label);
        }

        private static void PlayClip(AudioClip clip, float volume, float pitch, AudioMixerGroup mixer, Transform owner, string label)
        {
            if (clip == null || owner == null || volume < Mathf.Epsilon) return;
            GameObject sourceObject = new GameObject("Blue Phase " + label + " Binding Sound", typeof(AudioSource));
            sourceObject.transform.SetParent(owner, false);
            sourceObject.transform.localPosition = Vector3.zero;
            AudioSource source = sourceObject.GetComponent<AudioSource>();
            source.outputAudioMixerGroup = mixer;
            source.spatialBlend = 0f;
            source.playOnAwake = false;
            source.loop = false;
            source.volume = volume;
            source.pitch = pitch;
            source.PlayOneShot(clip);
            ActiveSources.Add(sourceObject);
            float duration = clip.length / Mathf.Max(Mathf.Abs(pitch), .01f) + .25f;
            UnityEngine.Object.Destroy(sourceObject, duration);
        }

        internal static void StopAll()
        {
            foreach (GameObject sourceObject in ActiveSources)
            {
                if (sourceObject == null) continue;
                AudioSource source = sourceObject.GetComponent<AudioSource>();
                if (source != null) source.Stop();
                UnityEngine.Object.Destroy(sourceObject);
            }
            ActiveSources.Clear();
        }

        private static void WarnOnce(Action<string> logWarning, string message)
        {
            if (missingLogged) return;
            missingLogged = true;
            logWarning?.Invoke(message);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // H P2 高位地刺：P1 分帧克隆原版 5 组，藏在未激活的暂存根对象下。
    // 进入 P2 时只改父节点并启用，保留原版 UP / SPIKES DOWN 行为。
    internal sealed class P2GroundSpikes : MonoBehaviour
    {
        private const string StateStart = "Arena 2 Start";
        private const string StateStop = "Ascend Tele";
        private const string StateP4 = "Scream";
        private const string SpikePath = "Boss Control/Spike Control";
        private const string ArenaFsmOwner = "Boss Control";
        private const string SpikeFsmName = "Control";
        private const float SpikeLocalY = 21.18f;
        private const float TargetWorldY = 33f;
        private const string CloneSuffix = " (P2 33)";
        private const float RetrySeconds = 1f;

        private static readonly string[] Groups = { "Centre", "Mid L", "Mid R", "Far L", "Far R" };
        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };
        private static readonly HashSet<P2GroundSpikes> ActiveInstances = new HashSet<P2GroundSpikes>();

        private PlayMakerFSM control;
        private PlayMakerFSM arenaFsm;
        private bool arenaTried;
        private bool subscribed;
        private bool arenaSubscribed;
        private string lastArenaState;
        private string lastState;
        private GameObject stagingRoot;
        private Transform sourceRoot;
        private Coroutine prewarmRoutine;
        private bool prewarmReady;
        private bool pendingActivation;
        private bool activated;
        private bool phaseFinished;
        private float nextRetry;
        private int prewarmIndex;
        private readonly List<GameObject> clones = new List<GameObject>();
        private readonly List<PlayMakerFSM> cloneFsms = new List<PlayMakerFSM>();

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
            ActiveInstances.Add(this);
            lastState = control != null && control.Fsm != null ? control.Fsm.ActiveStateName : null;
            lastArenaState = ArenaState();
            Subscribe();
            HandleState(lastState);
        }

        private void OnEnable() { Subscribe(); }

        private void Subscribe()
        {
            if (!subscribed && control != null && control.Fsm != null)
            {
                control.Fsm.StateChanged += OnStateChanged;
                subscribed = true;
            }
            ArenaState();
            if (!arenaSubscribed && arenaFsm != null && arenaFsm.Fsm != null)
            {
                arenaFsm.Fsm.StateChanged += OnArenaStateChanged;
                arenaSubscribed = true;
            }
        }

        private void OnDisable()
        {
            if (subscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnStateChanged;
            subscribed = false;
            if (arenaSubscribed && arenaFsm != null && arenaFsm.Fsm != null)
                arenaFsm.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
            Remove();
        }

        private void OnDestroy()
        {
            OnDisable();
            ActiveInstances.Remove(this);
        }

        internal static void RestoreAll()
        {
            foreach (P2GroundSpikes instance in ActiveInstances.ToArray())
            {
                if (instance == null) continue;
                instance.Remove();
            }
        }

        private void OnStateChanged(FsmState state)
        {
            if (state == null) return;
            lastState = state.Name;
            HandleState(state.Name);
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null) return;
            lastArenaState = state.Name;
            if (ResetStates.Contains(state.Name)) ResetForFight();
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;
            if (!OrbRadiance.Settings.Enabled || !OrbRadiance.EffectiveHardMode)
            {
                Remove();
                return;
            }
            HeroController hero = HeroController.instance;
            if (hero != null && hero.cState.dead)
            {
                Remove();
                phaseFinished = true;
                return;
            }

            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) ResetForFight();
            }
            string state = control.Fsm.ActiveStateName;
            if (state != lastState)
            {
                lastState = state;
                HandleState(state);
            }

            // 只在真正结束当前战斗前预热；找不到原版组时保守地延迟重试。
            if (!prewarmReady && !phaseFinished && stagingRoot == null && prewarmRoutine == null &&
                Time.time >= nextRetry)
            {
                nextRetry = Time.time + RetrySeconds;
                BeginPrewarm();
            }
            if (pendingActivation && prewarmReady && !phaseFinished) Activate();
            if (activated) PushUp();
        }

        private void HandleState(string state)
        {
            if (state == StateStart)
            {
                phaseFinished = false;
                pendingActivation = true;
                if (prewarmReady) Activate();
            }
            else if (state == StateStop || state == StateP4)
            {
                phaseFinished = true;
                Remove();
            }
        }

        private void BeginPrewarm()
        {
            GameObject root = GameObject.Find(SpikePath);
            if (root == null) return;
            sourceRoot = root.transform;
            // 先确定全部 5 个源组都存在；避免缺组时制造不可预测的半排刺。
            foreach (string name in Groups)
                if (sourceRoot.Find(name) == null) return;

            stagingRoot = new GameObject("H P2 high spikes prewarm staging");
            stagingRoot.transform.SetParent(sourceRoot, false);
            stagingRoot.transform.localPosition = Vector3.zero;
            stagingRoot.transform.localRotation = Quaternion.identity;
            stagingRoot.transform.localScale = Vector3.one;
            stagingRoot.SetActive(false);
            prewarmIndex = 0;
            prewarmRoutine = StartCoroutine(PrewarmGroups());
        }

        private IEnumerator PrewarmGroups()
        {
            while (stagingRoot != null && prewarmIndex < Groups.Length)
            {
                string name = Groups[prewarmIndex];
                Transform original = sourceRoot != null ? sourceRoot.Find(name) : null;
                bool failed = false;
                try
                {
                    if (original == null) throw new InvalidOperationException("Missing original spike group " + name);
                    GameObject clone = Instantiate(original.gameObject, stagingRoot.transform);
                    if (clone == null) throw new InvalidOperationException("Instantiate returned null for " + name);
                    clones.Add(clone); // 配置失败时也能销毁这棵子树。
                    clone.SetActive(false);
                    clone.name = name + CloneSuffix;
                    // 原版组可能已经下沉；P2 高度仍应采用确定的绝对赋值。
                    clone.transform.localPosition = new Vector3(original.localPosition.x,
                        TargetWorldY - SpikeLocalY, original.localPosition.z);
                    PlayMakerFSM[] fsms = clone.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int i = fsms.Length - 1; i >= 0; i--)
                    {
                        PlayMakerFSM fsm = fsms[i];
                        if (fsm != null && fsm.Fsm != null && fsm.FsmName == SpikeFsmName)
                            cloneFsms.Add(fsm);
                    }
                }
                catch (Exception e)
                {
                    OrbRadiance.Instance.LogWarn("[spikes] H P2 high spikes prewarm failed: " + e.Message);
                    // 在协程外侧一次性移除，避免本帧继续制造不完整的组。
                    prewarmRoutine = null;
                    Remove();
                    failed = true;
                }
                if (failed) yield break;
                prewarmIndex++;
                yield return null; // 每帧最多完整克隆一组，不在 P2 入口集中创建。
            }
            if (stagingRoot == null || clones.Count != Groups.Length) yield break;
            prewarmReady = true;
            prewarmRoutine = null;
            OrbRadiance.Instance.Log("[spikes] H P2 high spikes prewarm ready: " +
                                     clones.Count + "/" + Groups.Length + " groups, waiting for P2");
        }

        private void Activate()
        {
            if (activated || !prewarmReady || sourceRoot == null || clones.Count != Groups.Length) return;
            pendingActivation = false;
            foreach (GameObject clone in clones)
            {
                if (clone == null) { Remove(); return; }
                // 转回原版 Spike Control 的直接子节点，保持原生 FSM 所依赖的父子关系。
                clone.SetActive(false);
                clone.transform.SetParent(sourceRoot, true);
                clone.SetActive(true);
            }
            if (stagingRoot != null) Destroy(stagingRoot);
            stagingRoot = null;
            activated = true;
            OrbRadiance.Instance.Log("[spikes] H P2 high spikes activated: reused " + clones.Count +
                                     " prewarmed groups at world y=" + TargetWorldY);
        }

        private void ResetForFight()
        {
            Remove();
            phaseFinished = false;
            nextRetry = 0f;
        }

        private void Remove()
        {
            if (prewarmRoutine != null) StopCoroutine(prewarmRoutine);
            prewarmRoutine = null;
            pendingActivation = false;
            prewarmReady = false;
            activated = false;
            prewarmIndex = 0;
            cloneFsms.Clear();
            foreach (GameObject clone in clones)
            {
                if (clone == null) continue;
                clone.SetActive(false);
                Destroy(clone);
            }
            clones.Clear();
            if (stagingRoot != null)
            {
                stagingRoot.SetActive(false);
                Destroy(stagingRoot);
            }
            stagingRoot = null;
            sourceRoot = null;
        }

        private void PushUp()
        {
            for (int i = cloneFsms.Count - 1; i >= 0; i--)
            {
                PlayMakerFSM fsm = cloneFsms[i];
                if (fsm == null) { cloneFsms.RemoveAt(i); continue; }
                if (fsm.Fsm != null) fsm.SendEvent("UP");
            }
        }

        private string ArenaState()
        {
            if (!arenaTried)
            {
                arenaTried = true;
                Transform parent = transform.parent;
                if (parent != null)
                    foreach (PlayMakerFSM fsm in parent.GetComponents<PlayMakerFSM>())
                        if (fsm != null && fsm.Fsm != null && fsm.FsmName == "Control")
                        {
                            arenaFsm = fsm;
                            break;
                        }
                if (arenaFsm == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + ArenaFsmOwner +
                                                 " 的 Control FSM，无法通过父状态检测重战");
            }
            return arenaFsm != null && arenaFsm.Fsm != null ? arenaFsm.Fsm.ActiveStateName : null;
        }
    }
}

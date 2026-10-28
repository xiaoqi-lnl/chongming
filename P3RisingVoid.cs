using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OrbRadiance
{
    // N/H P3 用匀速上升替代虚空原生 Ascend 移动；P4 开始时交回原版。
    internal sealed class P3RisingVoid : MonoBehaviour
    {
        private const string EntryState = "Ascend Tele";
        private const string LateEntryState = "Ascend Cast";
        private const string ExitState = "Scream";
        private const float NormalRiseSpeed = 2.5f;
        private const float HardRiseSpeed = 3.5f;
        private const float ActionResolveTimeout = 1.5f;
        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };
        private static readonly HashSet<P3RisingVoid> Instances = new HashSet<P3RisingVoid>();

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private PlayMakerFSM pitFsm;
        private Transform bossControl;
        private GameObject pit;
        private iTweenMoveTo nativeMove;
        private bool nativeMoveOriginalEnabled;
        private bool nativeMoveCaptured;
        private bool subscribed;
        private bool arenaSubscribed;
        private bool sceneSubscribed;
        private bool initialized;
        private bool awaitingFreshEntry;
        private bool ownsRun;
        private bool rising;
        private bool pendingBegin;
        private bool warnedActionUnavailable;
        private Scene ownerScene;
        private Vector3 p3StartPosition;
        private float speed;
        private float beginDeadline;
        private string lastArenaState;

        internal void Init(PlayMakerFSM fsm)
        {
            if (initialized) return;
            initialized = true;
            control = fsm;
            if (control == null || control.Fsm == null || control.gameObject.scene.name != "GG_Radiance") return;

            bossControl = control.transform.parent;
            ResolvePit();

            arena = bossControl != null
                ? bossControl.GetComponents<PlayMakerFSM>().FirstOrDefault(x => x != null && x.Fsm != null && x.FsmName == "Control")
                : null;
            lastArenaState = ArenaState();
            string state = control.Fsm.ActiveStateName;
            bool alreadyInP3 = state == EntryState || state == LateEntryState;
            awaitingFreshEntry = !alreadyInP3 && Array.IndexOf(ResetStates, lastArenaState) >= 0;
            Instances.Add(this);
            Subscribe();

            if (alreadyInP3) BeginRise();
            OrbRadiance.Instance.Log("[P3Void] 已挂载P3阶段监视；Pit/Ascend与MoveTo在接管时按固定路径解析");
        }

        private void OnEnable() => Subscribe();

        private void Subscribe()
        {
            if (!initialized || control == null || control.Fsm == null) return;
            if (!subscribed)
            {
                control.Fsm.StateChanged += OnControlStateChanged;
                subscribed = true;
            }
            if (!arenaSubscribed && arena != null && arena.Fsm != null)
            {
                arena.Fsm.StateChanged += OnArenaStateChanged;
                arenaSubscribed = true;
            }
            if (!sceneSubscribed)
            {
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnActiveSceneChanged;
                sceneSubscribed = true;
            }
            HeroController hero = HeroController.instance;
            if (hero != null) hero.OnDeath -= OnHeroDeath;
            if (hero != null) hero.OnDeath += OnHeroDeath;
        }

        private void Unsubscribe()
        {
            if (subscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnControlStateChanged;
            subscribed = false;
            if (arenaSubscribed && arena != null && arena.Fsm != null)
                arena.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
            if (sceneSubscribed)
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            sceneSubscribed = false;
            HeroController hero = HeroController.instance;
            if (hero != null) hero.OnDeath -= OnHeroDeath;
        }

        private void OnDisable()
        {
            Unsubscribe();
            RestoreRun(ownsRun, "组件停用", true);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            RestoreRun(ownsRun, "组件销毁", true);
            Instances.Remove(this);
        }

        internal static void RestoreAll()
        {
            foreach (P3RisingVoid item in Instances.ToArray())
                if (item != null) item.RestoreRun(item.ownsRun, "MOD 关闭", true);
        }

        private void OnControlStateChanged(FsmState state)
        {
            if (state == null) return;
            if (state.Name == ExitState)
            {
                // P4 的 Final Burst 仍使用 Pit 树；放弃本轮位置所有权，之后清理不得回拉。
                pendingBegin = false;
                RestoreRun(false, "P4 开始，交回原版", false);
                awaitingFreshEntry = true;
            }
            else if (state.Name == EntryState)
            {
                awaitingFreshEntry = false;
                BeginRise();
            }
            else if (state.Name == LateEntryState && !awaitingFreshEntry)
            {
                BeginRise();
            }
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null || state.Name == lastArenaState) return;
            lastArenaState = state.Name;
            if (Array.IndexOf(ResetStates, state.Name) < 0) return;
            awaitingFreshEntry = true;
            pendingBegin = false;
            RestoreRun(true, "战斗重置 " + state.Name, true);
        }

        private void OnHeroDeath()
        {
            awaitingFreshEntry = true;
            pendingBegin = false;
            RestoreRun(true, "骑士死亡", true);
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (control == null || control.gameObject == null || next.handle == control.gameObject.scene.handle) return;
            RestoreRun(false, "离开场景", true);
            Unsubscribe();
            Instances.Remove(this);
            control = null;
            arena = null;
            bossControl = null;
            pitFsm = null;
            pit = null;
            nativeMove = null;
        }

        private string ArenaState()
        {
            return arena != null && arena.Fsm != null ? arena.Fsm.ActiveStateName : null;
        }

        private void ResolvePit()
        {
            if (bossControl == null && control != null) bossControl = control.transform.parent;
            Transform pitTransform = bossControl != null ? bossControl.Find("Abyss Pit") : null;
            if (pitTransform == null) return;
            pit = pitTransform.gameObject;
            pitFsm = pit.GetComponents<PlayMakerFSM>()
                .FirstOrDefault(x => x != null && x.Fsm != null && x.FsmName == "Ascend");
        }

        private void BeginRise()
        {
            if (ownsRun || awaitingFreshEntry || control == null || control.Fsm == null) return;
            if (!OrbRadiance.Settings.Enabled || OrbRadiance.EffectiveEasyMode) return;

            pendingBegin = true;
            beginDeadline = Time.time + ActionResolveTimeout;
            TryBeginRise();
        }

        private bool TryBeginRise()
        {
            if (!pendingBegin || control == null || control.Fsm == null || pitFsm == null || pitFsm.Fsm == null)
            {
                ResolvePit();
                if (!pendingBegin || control == null || control.Fsm == null || pitFsm == null || pitFsm.Fsm == null)
                {
                    WarnIfResolveExpired();
                    return false;
                }
            }
            string controlState = control.Fsm.ActiveStateName;
            if (controlState != EntryState && controlState != LateEntryState)
            {
                pendingBegin = false;
                return false;
            }

            FsmState ascend = pitFsm.Fsm.GetState("Ascend");
            iTweenMoveTo[] moves = ascend != null ? ascend.Actions.OfType<iTweenMoveTo>().ToArray() : Array.Empty<iTweenMoveTo>();
            if (moves.Length != 1)
            {
                WarnIfResolveExpired();
                return false;
            }

            nativeMove = moves[0];
            nativeMoveOriginalEnabled = nativeMove.Enabled;
            nativeMoveCaptured = true;
            ownerScene = pit.scene;
            p3StartPosition = pit.transform.position;
            speed = OrbRadiance.EffectiveHardMode ? HardRiseSpeed : NormalRiseSpeed;
            ownsRun = true;
            rising = true;
            pendingBegin = false;

            // 晚挂载时可能已在原生 Ascend；只停止该对象的 MoveTo，不触碰 MoveBy 或其他目标。
            if (pitFsm.Fsm.ActiveStateName == "Ascend")
                iTween.Stop(pit, "moveto");
            nativeMove.Enabled = false;
            OrbRadiance.Instance.Log("[P3Void] P3 起升：模式=" + (OrbRadiance.EffectiveHardMode ? "H" : "N") +
                                     "，速度=" + speed + "，起点=" + p3StartPosition);
            return true;
        }

        private void WarnIfResolveExpired()
        {
            if (Time.time < beginDeadline) return;
            pendingBegin = false;
            if (warnedActionUnavailable) return;
            warnedActionUnavailable = true;
            OrbRadiance.Instance.LogWarn("OrbRadiance: [P3Void] P3期间未能解析Abyss Pit/Ascend唯一MoveTo，保持原生虚空移动");
        }

        private void Update()
        {
            if (pendingBegin)
            {
                if (!OrbRadiance.Settings.Enabled || OrbRadiance.EffectiveEasyMode || awaitingFreshEntry)
                    pendingBegin = false;
                else
                    TryBeginRise();
            }
            if (!ownsRun || !rising) return;
            if (!OrbRadiance.Settings.Enabled || OrbRadiance.EffectiveEasyMode)
            {
                RestoreRun(true, "模式不再适用", true);
                return;
            }
            HeroController hero = HeroController.instance;
            if (hero != null && hero.cState.dead)
            {
                OnHeroDeath();
                return;
            }
            if (!CanWriteOwnerScene())
            {
                RestoreRun(false, "离开场景", true);
                return;
            }
            if (control.Fsm.ActiveStateName == ExitState)
            {
                RestoreRun(false, "P4 开始，交回原版", false);
                awaitingFreshEntry = true;
                return;
            }

            Vector3 position = pit.transform.position;
            position.y += speed * Time.deltaTime;
            pit.transform.position = position;
        }

        private bool CanWriteOwnerScene()
        {
            if (!ownerScene.IsValid() || !ownerScene.isLoaded || pit == null) return false;
            Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return pit.scene.handle == ownerScene.handle && activeScene.handle == ownerScene.handle;
        }

        private void RestoreRun(bool restoreStartPosition, string reason, bool waitForFreshEntry)
        {
            pendingBegin = false;
            bool hadOwnership = ownsRun;
            rising = false;
            ownsRun = false;

            if (hadOwnership && nativeMoveCaptured && nativeMove != null)
                nativeMove.Enabled = nativeMoveOriginalEnabled;
            bool restoredPosition = restoreStartPosition && hadOwnership && CanWriteOwnerScene();
            if (restoredPosition)
                pit.transform.position = p3StartPosition;
            if (waitForFreshEntry) awaitingFreshEntry = true;

            if (hadOwnership)
                OrbRadiance.Instance.Log("[P3Void] 停止并恢复原生移动：原因=" + reason +
                                         (restoredPosition ? "，已还原本轮起点" : "，保留当前位置"));

            // 释放本轮捕获值；尤其P4交接后，后续销毁不能再访问或回拉旧起点。
            ownerScene = default(Scene);
            p3StartPosition = Vector3.zero;
            if (hadOwnership)
            {
                nativeMove = null;
                nativeMoveCaptured = false;
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OrbRadiance
{
    internal sealed class HiddenRadianceStatue : MonoBehaviour { }

    internal static class WorkshopStatueLifecycle
    {
        internal const string HiddenStatueObjectName = "Wind_GG_Statue_Radiance_HARD";
        internal const string HiddenReturnGateName = "door_dreamReturnGG_" + HiddenStatueObjectName;
        private const string OriginalStatueName = "GG_Statue_Radiance";
        private const string TargetScene = "GG_Radiance";
        private static bool initialized;
        private static string pendingTargetScene;
        private static readonly FieldInfo UiStatueField = typeof(BossChallengeUI).GetField("bossStatue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo LastSelectedButtonField = typeof(BossChallengeUI).GetField("lastSelectedButton", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly Dictionary<BossChallengeUI, ChallengeUiState> UiStates = new Dictionary<BossChallengeUI, ChallengeUiState>();
        private static HeroController deathHookHero;

        internal static bool? HardModeOverride => BattleModeState.Current.HasValue ? BattleModeState.Current == BattleMode.Hard : (bool?)null;
        internal static bool EffectiveHardMode(bool persistentSetting) => BattleModeState.Resolve(persistentSetting) == BattleMode.Hard;

        private enum UiPage { Mode, Tier }
        private sealed class ChallengeUiState
        {
            internal BossChallengeUI Owner;
            internal BossStatue Statue;
            internal On.BossChallengeUI.orig_Setup NativeSetup;
            internal string NameSheet, NameKey, DescriptionSheet, DescriptionKey;
            internal object NativeLastSelected;
            internal UiPage Page;
            internal BattleMode SelectedMode;
            internal string NativeTitle, NativeDescription;
            internal ButtonState Tier1, Tier2, Tier3;
            internal GameObject UnlockEffect;
            internal bool UnlockEffectActive;
            internal BossChallengeUI.HideEvent CancelHandler;
            internal readonly List<SuppressedHideListener> SuppressedHideListeners = new List<SuppressedHideListener>();
        }

        private sealed class SuppressedHideListener
        {
            internal UnityEventBase Event;
            internal int Index;
            internal UnityEventCallState State;
        }

        private sealed class ButtonState
        {
            internal MenuSelectable Button;
            internal bool Active, Interactable;
            internal Navigation Navigation;
            internal CanvasGroup CanvasGroup;
            internal float Alpha;
            internal Text Label;
            internal string LabelText;
            internal Image Complete, Incomplete;
            internal bool CompleteActive, IncompleteActive;
        }

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            On.BossChallengeUI.LoadBoss_int_bool += OnLoadBoss;
            On.BossChallengeUI.Setup += OnSetup;
            On.GameManager.EnterHero += OnEnterHero;
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
            OrbRadiance.Instance?.LogDebug("HiddenRoom: 雕像挑战生命周期与模式选择界面挂钩已注册");
        }

        private static bool IsOriginalRadiance(BossStatue statue) => statue != null && statue.GetComponent<HiddenRadianceStatue>() == null &&
            statue.gameObject.scene.name == "GG_Workshop" && statue.gameObject.name == OriginalStatueName;

        private static bool IsHidden(BossStatue statue) => statue != null && statue.GetComponent<HiddenRadianceStatue>() != null;

        private static void OnSetup(On.BossChallengeUI.orig_Setup orig, BossChallengeUI self, BossStatue bossStatue,
            string bossNameSheet, string bossNameKey, string descriptionSheet, string descriptionKey)
        {
            if (UiStates.TryGetValue(self, out var staleState))
            {
                UiStates.Remove(self);
                DetachCancel(self, staleState);
                RestoreAllUi(self, staleState);
            }
            if (!OrbRadiance.Settings.Enabled || !IsOriginalRadiance(bossStatue))
            {
                orig(self, bossStatue, bossNameSheet, bossNameKey, descriptionSheet, descriptionKey);
                if (OrbRadiance.Settings.Enabled && IsHidden(bossStatue) && self.bossNameText != null)
                    self.bossNameText.text = "any天国hard重明3.0";
                if (OrbRadiance.Settings.Enabled && IsHidden(bossStatue) && self.descriptionText != null)
                
                    self.descriptionText.text = "绝对的蛾出，由此而生的无解，教会你出招的是：";
                return;
            }

            var state = new ChallengeUiState
            {
                Owner = self,
                Statue = bossStatue,
                NativeSetup = orig,
                NameSheet = bossNameSheet,
                NameKey = bossNameKey,
                DescriptionSheet = descriptionSheet,
                DescriptionKey = descriptionKey,
                Page = UiPage.Mode,
                NativeLastSelected = LastSelectedButtonField?.GetValue(null)
            };
            UiStates[self] = state;
            SubscribeCancel(self, state);

            GameObject unlock = self.tier3UnlockEffect;
            MenuSelectable firstSelected = self.firstSelected;
            object lastSelected = LastSelectedButtonField?.GetValue(null);
            // StartCoroutine 会同步执行 SetFirstSelected，直到首次 yield；
            // 此次 Setup 暂将目标固定为第一档，之后立即恢复共享选择索引。
            if (LastSelectedButtonField != null) LastSelectedButtonField.SetValue(null, 0);
            if (self.tier1Button.button != null) self.firstSelected = self.tier1Button.button;
            if (self.tier3Button.button != null) self.tier3Button.button.interactable = false;
            self.tier3UnlockEffect = null;
            try { orig(self, bossStatue, bossNameSheet, bossNameKey, descriptionSheet, descriptionKey); }
            finally
            {
                self.tier3UnlockEffect = unlock;
                self.firstSelected = firstSelected;
                if (LastSelectedButtonField != null) LastSelectedButtonField.SetValue(null, lastSelected);
            }

            state.NativeTitle = self.bossNameText == null ? null : self.bossNameText.text;
            state.NativeDescription = self.descriptionText == null ? null : self.descriptionText.text;
            state.Tier1 = Capture(self.tier1Button);
            state.Tier2 = Capture(self.tier2Button);
            state.Tier3 = Capture(self.tier3Button);
            state.UnlockEffect = self.tier3UnlockEffect;
            state.UnlockEffectActive = state.UnlockEffect != null && state.UnlockEffect.activeSelf;
            ApplyModePage(self, state);
        }

        private static ButtonState Capture(BossChallengeUI.ButtonDisplay display)
        {
            var button = display.button;
            var result = new ButtonState { Button = button };
            if (button == null) return result;
            result.Active = button.gameObject.activeSelf;
            result.Interactable = button.interactable;
            result.Navigation = button.navigation;
            result.CanvasGroup = button.GetComponent<CanvasGroup>();
            if (result.CanvasGroup != null) result.Alpha = result.CanvasGroup.alpha;
            result.Label = button.GetComponentInChildren<Text>(true);
            if (result.Label != null) result.LabelText = result.Label.text;
            result.Complete = display.completeImage;
            result.Incomplete = display.incompleteImage;
            result.CompleteActive = result.Complete != null && result.Complete.gameObject.activeSelf;
            result.IncompleteActive = result.Incomplete != null && result.Incomplete.gameObject.activeSelf;
            return result;
        }

        private static void Restore(ButtonState state)
        {
            if (state?.Button == null) return;
            state.Button.gameObject.SetActive(state.Active);
            state.Button.interactable = state.Interactable;
            state.Button.navigation = state.Navigation;
            if (state.CanvasGroup != null) state.CanvasGroup.alpha = state.Alpha;
            if (state.Label != null) state.Label.text = state.LabelText;
            if (state.Complete != null) state.Complete.gameObject.SetActive(state.CompleteActive);
            if (state.Incomplete != null) state.Incomplete.gameObject.SetActive(state.IncompleteActive);
        }

        private static void ApplyModePage(BossChallengeUI self, ChallengeUiState state)
        {
            SetLabel(state.Tier1, "EASY");
            SetLabel(state.Tier2, "NORMAL");
            HideCompletion(state.Tier1);
            HideCompletion(state.Tier2);
            if (state.Tier1.Button != null) { state.Tier1.Button.gameObject.SetActive(true); state.Tier1.Button.interactable = true; }
            if (state.Tier2.Button != null) { state.Tier2.Button.gameObject.SetActive(true); state.Tier2.Button.interactable = true; }
            if (state.Tier3.Button != null) { state.Tier3.Button.interactable = false; state.Tier3.Button.gameObject.SetActive(false); }
            SetVerticalNavigation(state.Tier1.Button, state.Tier2.Button, state.Tier2.Button);
            SetVerticalNavigation(state.Tier2.Button, state.Tier1.Button, state.Tier1.Button);
            SuppressModePageHideListeners(self, state);
            if (self.bossNameText != null) self.bossNameText.text = "重明：选择模式";
            if (self.descriptionText != null) self.descriptionText.text = "先选择 EASY 或 NORMAL，下一步选择原版难度。";
        }

        private static void ApplyTierTitle(BossChallengeUI self, ChallengeUiState state)
        {
            RestoreModePageHideListeners(state);
            Restore(state.Tier1); Restore(state.Tier2); Restore(state.Tier3);
            if (state.UnlockEffect != null) state.UnlockEffect.SetActive(state.UnlockEffectActive);
            if (self.bossNameText != null) self.bossNameText.text = state.SelectedMode == BattleMode.Easy ? "重明（EASY）" : "重明（NORMAL）";
            if (self.descriptionText != null) self.descriptionText.text = state.NativeDescription;
        }

        private static void SetLabel(ButtonState state, string text) { if (state?.Label != null) state.Label.text = text; }
        private static void HideCompletion(ButtonState state)
        {
            if (state?.Complete != null) state.Complete.gameObject.SetActive(false);
            if (state?.Incomplete != null) state.Incomplete.gameObject.SetActive(false);
        }
        private static void SetVerticalNavigation(MenuSelectable button, MenuSelectable up, MenuSelectable down)
        {
            if (button == null) return;
            var navigation = button.navigation;
            navigation.selectOnUp = up;
            navigation.selectOnDown = down;
            button.navigation = navigation;
        }

        private static void SubscribeCancel(BossChallengeUI self, ChallengeUiState state)
        {
            if (state.CancelHandler != null) return;
            state.CancelHandler = () => OnUiCancelled(self, state);
            self.OnCancel += state.CancelHandler;
        }

        private static void DetachCancel(BossChallengeUI self, ChallengeUiState state)
        {
            if (state.CancelHandler == null) return;
            self.OnCancel -= state.CancelHandler;
            state.CancelHandler = null;
        }

        private static void OnUiCancelled(BossChallengeUI self, ChallengeUiState state)
        {
            // LoadBoss 会在 Hide 前清空 OnCancel；若回调仍在执行，说明玩家取消，
            // 或原版载入尚未开始就失败，此时只结束待启动的选择。
            if (pendingTargetScene == null)
            {
                UiStates.Remove(self);
                DetachCancel(self, state);
                BattleModeState.Reset();
                RestoreAllUi(self, state);
                OrbRadiance.Instance?.LogDebug("HiddenRoom: 取消雕像模式/难度选择，清除尚未启动的本次模式");
            }
        }

        private static void RestoreAllUi(BossChallengeUI self, ChallengeUiState state)
        {
            RestoreModePageHideListeners(state);
            Restore(state.Tier1); Restore(state.Tier2); Restore(state.Tier3);
            if (state.UnlockEffect != null) state.UnlockEffect.SetActive(state.UnlockEffectActive);
            if (self.bossNameText != null) self.bossNameText.text = state.NativeTitle;
            if (self.descriptionText != null) self.descriptionText.text = state.NativeDescription;
        }

        private static void SuppressModePageHideListeners(BossChallengeUI self, ChallengeUiState state)
        {
            RestoreModePageHideListeners(state);
            SuppressButtonHideListeners(self, state, state.Tier1.Button);
            SuppressButtonHideListeners(self, state, state.Tier2.Button);
            OrbRadiance.Instance?.LogDebug("HiddenRoom UI: 模式页暂时禁用 " + state.SuppressedHideListeners.Count + " 个确认 Hide 回调，保留原生取消");
        }

        private static void SuppressButtonHideListeners(BossChallengeUI self, ChallengeUiState state, MenuSelectable button)
        {
            if (button == null) return;
            foreach (var trigger in button.GetComponents<EventTrigger>())
            {
                if (trigger?.triggers == null) continue;
                foreach (var entry in trigger.triggers)
                {
                    if (entry?.callback == null) continue;
                    // Cancel 有独立的 Hide 回调，模式页必须保留它。
                    if (entry.eventID != EventTriggerType.Submit && entry.eventID != EventTriggerType.PointerClick) continue;
                    SuppressHideListeners(self, state, entry.callback);
                }
            }
        }

        private static void SuppressHideListeners(BossChallengeUI self, ChallengeUiState state, UnityEventBase evt)
        {
            for (int i = 0; i < evt.GetPersistentEventCount(); i++)
            {
                if (evt.GetPersistentTarget(i) != self || evt.GetPersistentMethodName(i) != "Hide") continue;
                if (!TryGetPersistentListenerState(evt, i, out UnityEventCallState originalState))
                {
                    OrbRadiance.Instance?.LogWarn("HiddenRoom UI: 无法读取确认回调原状态，保留该回调");
                    continue;
                }
                state.SuppressedHideListeners.Add(new SuppressedHideListener { Event = evt, Index = i, State = originalState });
                evt.SetPersistentListenerState(i, UnityEventCallState.Off);
            }
        }

        private static bool TryGetPersistentListenerState(UnityEventBase evt, int index, out UnityEventCallState state)
        {
            state = UnityEventCallState.Off;
            object calls = typeof(UnityEventBase).GetField("m_PersistentCalls", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(evt);
            object list = calls?.GetType().GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(calls);
            if (list is IList persistentCalls && index >= 0 && index < persistentCalls.Count)
            {
                object call = persistentCalls[index];
                object value = call?.GetType().GetField("m_CallState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(call);
                if (value is UnityEventCallState callState) { state = callState; return true; }
            }
            return false;
        }

        private static void RestoreModePageHideListeners(ChallengeUiState state)
        {
            if (state == null) return;
            foreach (var listener in state.SuppressedHideListeners)
                if (listener?.Event != null) listener.Event.SetPersistentListenerState(listener.Index, listener.State);
            state.SuppressedHideListeners.Clear();
        }

        private static void OnLoadBoss(On.BossChallengeUI.orig_LoadBoss_int_bool orig, BossChallengeUI self, int level, bool doHideAnim)
        {
            var statue = UiStatueField == null ? null : UiStatueField.GetValue(self) as BossStatue;
            string target = GetTierScene(statue, level);
            if (OrbRadiance.Settings.Enabled && IsOriginalRadiance(statue))
            {
                if (UiStates.TryGetValue(self, out var state) && state.Page == UiPage.Mode)
                {
                    if (level != 0 && level != 1) return;
                    state.SelectedMode = level == 0 ? BattleMode.Easy : BattleMode.Normal;
                    state.Page = UiPage.Tier;
                    object modeButtonSelection = LastSelectedButtonField?.GetValue(null);
                    if (LastSelectedButtonField != null) LastSelectedButtonField.SetValue(null, state.NativeLastSelected);
                    try { state.NativeSetup(self, state.Statue, state.NameSheet, state.NameKey, state.DescriptionSheet, state.DescriptionKey); }
                    finally { if (LastSelectedButtonField != null) LastSelectedButtonField.SetValue(null, state.NativeLastSelected ?? modeButtonSelection); }
                    ApplyTierTitle(self, state);
                    OrbRadiance.Instance?.LogDebug("HiddenRoom: 模式界面选择 " + state.SelectedMode + "，显示原版难度按钮");
                    return;
                }

                BattleMode selected = UiStates.TryGetValue(self, out state) ? state.SelectedMode : BattleMode.Normal;
                if (target != TargetScene)
                {
                    BattleModeState.Reset();
                    pendingTargetScene = null;
                    if (state != null)
                    {
                        RestoreAllUi(self, state);
                        UiStates.Remove(self);
                        DetachCancel(self, state);
                    }
                    OrbRadiance.Instance?.LogWarn("HiddenRoom: 原雕像 tier 目标不是 GG_Radiance，保留原版挑战流程：" + (target ?? "<null>"));
                    orig(self, level, doHideAnim);
                    return;
                }
                if (string.IsNullOrEmpty(target) || !Application.CanStreamedLevelBeLoaded(target))
                {
                    BattleModeState.Reset();
                    pendingTargetScene = null;
                    if (state != null) { UiStates.Remove(self); RestoreAllUi(self, state); }
                    orig(self, level, doHideAnim);
                    return;
                }

                if (selected == BattleMode.Easy)
                {
                    ArenaSpikes.RestorePatched();
                    ArenaSpikes.RestoreSpikesOuter();
                }
                BattleModeState.Set(selected);
                pendingTargetScene = target;
                if (state != null)
                {
                    RestoreAllUi(self, state);
                    UiStates.Remove(self);
                    DetachCancel(self, state);
                }
                OrbRadiance.Instance?.LogDebug("HiddenRoom: 原雕像选择 " + selected + "，保留原版 tier=" + level + "，目标=" + target);
                try
                {
                    orig(self, level, doHideAnim);
                    WrapBattleLifecycle();
                }
                catch
                {
                    BattleModeState.Reset();
                    pendingTargetScene = null;
                    if (state != null) RestoreAllUi(self, state);
                    throw;
                }
                return;
            }

            if (OrbRadiance.Settings.Enabled && IsHidden(statue) && target == TargetScene && Application.CanStreamedLevelBeLoaded(target))
            {
                BattleModeState.Set(BattleMode.Hard);
                pendingTargetScene = target;
                try { orig(self, level, doHideAnim); WrapBattleLifecycle(); }
                catch { BattleModeState.Reset(); pendingTargetScene = null; throw; }
                return;
            }

            orig(self, level, doHideAnim);
        }

        private static void WrapBattleLifecycle()
        {
            var previousSetup = BossSceneController.SetupEvent;
            if (previousSetup == null) return;
            BossSceneController.SetupEvent = self =>
            {
                previousSetup(self);
                AttachBattleEndHandlers(self);
            };
        }

        private static void AttachBattleEndHandlers(BossSceneController controller)
        {
            if (controller != null) controller.OnBossSceneComplete += ClearBattleMode;
            if (deathHookHero != null) deathHookHero.OnDeath -= ClearBattleMode;
            deathHookHero = HeroController.instance;
            if (deathHookHero != null) deathHookHero.OnDeath += ClearBattleMode;
        }

        private static void ClearBattleMode()
        {
            BattleModeState.Reset();
            pendingTargetScene = null;
            if (deathHookHero != null) deathHookHero.OnDeath -= ClearBattleMode;
            deathHookHero = null;
        }

        private static string GetTierScene(BossStatue statue, int level)
        {
            if (statue == null) return null;
            BossScene scene = statue.UsingDreamVersion ? statue.dreamBossScene : statue.bossScene;
            if (scene == null) return null;
            switch (level)
            {
                case 0: return scene.Tier1Scene;
                case 1: return scene.Tier2Scene;
                case 2: return scene.Tier3Scene;
                default: return scene.sceneName;
            }
        }

        private static void OnEnterHero(On.GameManager.orig_EnterHero orig, GameManager self, bool additiveGateSearch)
        {
            try
            {
                if (self != null && self.entryGateName == "door_dreamReturn" && self.playerData != null &&
                    self.nextSceneName == "GG_Workshop")
                {
                    string requestedGate = self.playerData.GetString("bossReturnEntryGate");
                    if (requestedGate == HiddenReturnGateName)
                    {
                        Scene target = UnityEngine.SceneManagement.SceneManager.GetSceneByName(self.nextSceneName);
                        bool ready = OrbRadiance.Settings.Enabled && requestedGate == HiddenReturnGateName &&
                            target.IsValid() && target.isLoaded && WorkshopHiddenRoom.InstallForReturn(target, requestedGate);
                        if (!ready)
                        {
                            string fallback = OriginalReturnGateName(target);
                            if (!string.IsNullOrEmpty(fallback))
                            {
                                self.playerData.SetString("bossReturnEntryGate", fallback);
                                OrbRadiance.Instance?.LogDebug("HiddenRoom: 专属返回门不可用，返回原辐光雕像门 " + fallback);
                            }
                            else OrbRadiance.Instance?.LogError("HiddenRoom: 原辐光雕像门也不可用；保留游戏当前返回值");
                        }
                    }
                }
            }
            catch (Exception e) { OrbRadiance.Instance?.LogError("HiddenRoom: EnterHero 前准备专属返回门失败：" + e); }
            orig(self, additiveGateSearch);
        }

        private static string OriginalReturnGateName(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            Transform root = WorkshopTerrainPatch.FindPath(scene, "GG_Statue_Radiance");
            BossStatue original = root == null ? null : root.GetComponent<BossStatue>();
            GameObject gate = original == null ? null : original.dreamReturnGate;
            return gate == null ? null : gate.name;
        }

        private static void OnSceneChanged(Scene oldScene, Scene newScene)
        {
            if (newScene.name == pendingTargetScene) pendingTargetScene = null;
            else if (pendingTargetScene != null && newScene.name != "GG_Workshop") ClearBattleMode();
            if (oldScene.name == TargetScene && newScene.name != TargetScene) ClearBattleMode();
            ClearUiStates();
        }

        private static void ClearUiStates()
        {
            foreach (var pair in new List<KeyValuePair<BossChallengeUI, ChallengeUiState>>(UiStates))
            {
                if (pair.Key != null && pair.Value != null)
                {
                    DetachCancel(pair.Key, pair.Value);
                    RestoreAllUi(pair.Key, pair.Value);
                }
            }
            UiStates.Clear();
        }

        internal static void OnModDisabled()
        {
            ClearBattleMode();
            pendingTargetScene = null;
            ClearUiStates();
        }
    }
}

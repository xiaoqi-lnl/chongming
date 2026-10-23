using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Modding;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OrbRadiance
{
    // P2.5 的本场临时万神殿束缚；不修改 BossSequenceController 的序列数据。
    internal sealed class BluePhaseBindings : MonoBehaviour
    {
        private const float SoulLockAt = 10f;
        private const float ShellLockAt = 20f;
        private const float NailLockAt = 30f;
        private const float CharmLockAt = 40f;
        private const float OrbSpawnAt = 45f;
        // resources.assets 中 Boss Sequence Tier 1..5 (PathId 19561..19565) 参数一致：13 / 0.8 / 4。
        private const int RadianceSequenceNailDamage = 13;
        private const float RadianceSequenceLowerNailDamage = 0.8f;
        private const int RadianceSequenceMaxHealth = 4;
        private const string P3State = "Ascend Tele";
        private const string P4State = "Scream";
        private static BluePhaseBindings active;

        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private HealthManager health;
        private bool controlSubscribed;
        private bool arenaSubscribed;
        private bool hooksInstalled;
        private bool running;
        private bool soulLocked;
        private bool shellLocked;
        private bool nailLocked;
        private bool charmsLocked;
        private bool nativeBoundSoul;
        private bool nativeBoundShellAtStart;
        private bool nativeBoundNail;
        private bool nativeBoundCharms;
        private bool shellCapApplied;
        private bool shellWhiteHealthLost;
        private bool derivedStateCaptured;
        private bool orbSpawned;
        private bool radianceCryPlayed;
        private bool soulGainBlockLogged;
        private bool soulBindingEventLogged;
        private float elapsed;
        private float charmsAppliedAt;
        private int originalMaxMp;
        private int shellHealthBeforeLock;
        private int originalMaxHealth;
        private int originalHealthBlue;
        private int originalJoniHealthBlue;
        private readonly List<int> virtualEquippedCharms = new List<int>();
        private readonly List<Hook> localHooks = new List<Hook>();
        private GameObject permanentOrb;
        private BluePhaseCountdownHud countdownHud;

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
            health = fsm != null ? fsm.GetComponent<HealthManager>() : null;
            active = this;
            Subscribe();
        }

        internal static void BeginActivePhase()
        {
            if (active != null) active.Begin();
        }

        internal static void ClearActivePhase()
        {
            if (active != null) active.Clear();
        }

        internal static bool IsPermanentOrb(GameObject obj)
        {
            return obj != null && obj.GetComponent<PermanentRadiantOrb>() != null;
        }

        internal static void RestoreAll()
        {
            if (active != null) active.Clear();
        }

        private void Begin()
        {
            if (running || !OrbRadiance.Settings.Enabled || !OrbRadiance.EffectiveHardMode ||
                control == null || control.gameObject.scene.name != "GG_Radiance") return;
            running = true;
            elapsed = 0f;
            radianceCryPlayed = false;
            countdownHud = BluePhaseCountdownHud.Create(control.gameObject.scene);
            InstallHooks();
            if (OrbRadiance.Instance != null)
                OrbRadiance.Instance.Log("[blue-phase-locks] P2.5 计时开始，10/20/30/40/45 秒依次生效");
        }

        private void OnEnable() => Subscribe();

        private void Subscribe()
        {
            if (!controlSubscribed && control != null && control.Fsm != null)
            {
                control.Fsm.StateChanged += OnControlStateChanged;
                controlSubscribed = true;
            }
            if (arenaSubscribed || control == null || control.transform.parent == null) return;
            foreach (PlayMakerFSM fsm in control.transform.parent.GetComponents<PlayMakerFSM>())
            {
                if (fsm == null || fsm.Fsm == null || fsm.FsmName != "Control") continue;
                arena = fsm;
                arena.Fsm.StateChanged += OnArenaStateChanged;
                arenaSubscribed = true;
                break;
            }
        }

        private void OnDisable()
        {
            if (controlSubscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnControlStateChanged;
            controlSubscribed = false;
            if (arenaSubscribed && arena != null && arena.Fsm != null)
                arena.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
            Clear();
        }

        private void OnDestroy()
        {
            OnDisable();
            if (active == this) active = null;
        }

        private void OnControlStateChanged(FsmState state)
        {
            if (state == null) return;
            if (state.Name == P3State || state.Name == P4State || IsResetState(state.Name)) Clear();
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state != null && IsResetState(state.Name)) Clear();
        }

        private static bool IsResetState(string state)
        {
            return state == "Battle Start" || state == "Refight" || state == "Init" || state == "Setup";
        }

        private void Update()
        {
            if (!running) return;
            if (!CanContinue())
            {
                Clear();
                return;
            }

            elapsed += Time.deltaTime;
            // 按顺序补齐卡帧跨过的多个阈值；只由 Update 累计时间。
            if (!soulLocked && elapsed >= SoulLockAt) LockSoul();
            if (!shellLocked && elapsed >= ShellLockAt) LockShell();
            if (!nailLocked && elapsed >= NailLockAt) LockNail();
            if (!charmsLocked && elapsed >= CharmLockAt) LockCharms();
            if (!orbSpawned && charmsLocked && elapsed >= OrbSpawnAt &&
                elapsed - charmsAppliedAt >= 5f && CanContinue()) SpawnPermanentOrb();
            UpdateCountdownHud();
        }

        private void UpdateCountdownHud()
        {
            if (countdownHud == null) return;
            if (permanentOrb != null)
            {
                countdownHud.SetVisible(false);
                return;
            }

            float startAt;
            float duration;
            if (!soulLocked) { startAt = 0f; duration = 10f; }
            else if (!shellLocked) { startAt = SoulLockAt; duration = 10f; }
            else if (!nailLocked) { startAt = ShellLockAt; duration = 10f; }
            else if (!charmsLocked) { startAt = NailLockAt; duration = 10f; }
            else { startAt = charmsAppliedAt; duration = 5f; }

            float elapsedPart = Mathf.Clamp(elapsed - startAt, 0f, duration);
            float remaining = 1f - elapsedPart / duration;
            countdownHud.SetVisible(true);
            countdownHud.SetRemaining(remaining);
        }

        private bool CanContinue()
        {
            HeroController hero = HeroController.instance;
            return OrbRadiance.Settings.Enabled && OrbRadiance.EffectiveHardMode && control != null &&
                   control.gameObject.scene.name == "GG_Radiance" && control.Fsm != null && health != null &&
                   health.hp > BossHP.AscendHP_H && control.Fsm.ActiveStateName != P3State &&
                   control.Fsm.ActiveStateName != P4State && (hero == null || !hero.cState.dead);
        }

        private void InstallHooks()
        {
            if (hooksInstalled) return;
            Type type = typeof(BossSequenceController);
            localHooks.Add(new Hook(type.GetProperty("BoundSoul").GetGetMethod(),
                (Func<Func<bool>, bool>)(orig => orig() || soulLocked)));
            localHooks.Add(new Hook(type.GetProperty("BoundShell").GetGetMethod(),
                (Func<Func<bool>, bool>)(orig => orig() || shellLocked)));
            localHooks.Add(new Hook(type.GetProperty("BoundNail").GetGetMethod(),
                (Func<Func<bool>, bool>)(orig => orig() || nailLocked)));
            localHooks.Add(new Hook(type.GetProperty("BoundCharms").GetGetMethod(),
                (Func<Func<bool>, bool>)(orig => orig() || charmsLocked)));
            localHooks.Add(new Hook(type.GetProperty("BoundMaxHealth").GetGetMethod(),
                (Func<Func<int>, int>)BoundMaxHealth));
            localHooks.Add(new Hook(type.GetProperty("BoundNailDamage").GetGetMethod(),
                (Func<Func<int>, int>)BoundNailDamage));
            localHooks.Add(new Hook(typeof(PlayerData).GetMethod("AddMPCharge"),
                (Func<Func<PlayerData, int, bool>, PlayerData, int, bool>)AddMPCharge));
            localHooks.Add(new Hook(typeof(PlayerData).GetMethod("TakeHealth"),
                (Action<Action<PlayerData, int>, PlayerData, int>)TrackShellWhiteHealthLoss));
            localHooks.Add(new Hook(typeof(GGCheckBoundSoul).GetMethod("OnEnter"),
                (Action<Action<GGCheckBoundSoul>, GGCheckBoundSoul>)CheckBoundSoul));
            On.BossSequenceController.RestoreBindings += BeforeRestoreBindings;
            ModHooks.GetPlayerBoolHook += GetPlayerBool;
            ModHooks.GetPlayerIntHook += GetPlayerInt;
            ModHooks.GetPlayerVariableHook += GetPlayerVariable;
            ModHooks.BeforeSavegameSaveHook += BeforeSave;
            ModHooks.ApplicationQuitHook += Clear;
            hooksInstalled = true;
        }

        private void RemoveHooks()
        {
            if (!hooksInstalled) return;
            foreach (Hook hook in localHooks) hook.Dispose();
            localHooks.Clear();
            On.BossSequenceController.RestoreBindings -= BeforeRestoreBindings;
            ModHooks.GetPlayerBoolHook -= GetPlayerBool;
            ModHooks.GetPlayerIntHook -= GetPlayerInt;
            ModHooks.GetPlayerVariableHook -= GetPlayerVariable;
            ModHooks.BeforeSavegameSaveHook -= BeforeSave;
            ModHooks.ApplicationQuitHook -= Clear;
            hooksInstalled = false;
        }

        private int BoundMaxHealth(Func<int> orig)
        {
            if (!shellLocked || BossSequenceController.BoundShell && BossSequenceController.IsInSequence)
                return orig();
            GameManager gm = GameManager.instance;
            int heart = gm != null && gm.playerData != null && gm.playerData.GetBool("equippedCharm_23") &&
                        !gm.playerData.GetBool("brokenCharm_23") ? 2 : 0;
            return RadianceSequenceMaxHealth + heart;
        }

        private int BoundNailDamage(Func<int> orig)
        {
            if (!nailLocked || BossSequenceController.BoundNail && BossSequenceController.IsInSequence)
                return orig();
            if (GameManager.instance == null || GameManager.instance.playerData == null)
                return RadianceSequenceNailDamage;
            int damage = GameManager.instance.playerData.GetInt("nailDamage");
            return damage <= RadianceSequenceNailDamage
                ? Mathf.RoundToInt(damage * RadianceSequenceLowerNailDamage)
                : RadianceSequenceNailDamage;
        }

        private bool AddMPCharge(Func<PlayerData, int, bool> orig, PlayerData pd, int amount)
        {
            if (!soulLocked || pd == null) return orig(pd, amount);
            pd.maxMP = 33;
            if (amount > 0)
            {
                pd.MPCharge = Mathf.Clamp(pd.MPCharge, 0, 33);
                int previousCharge = pd.MPCharge;
                int previousReserve = pd.MPReserve;
                orig(pd, amount);
                pd.maxMP = 33;
                pd.MPCharge = Mathf.Clamp(pd.MPCharge, 0, 33);
                pd.MPReserve = previousReserve;
                if (!soulGainBlockLogged)
                {
                    soulGainBlockLogged = true;
                    OrbRadiance.Instance?.Log("[blue-phase-locks] Soul 束缚期间储魂上限为33，储备槽不增长");
                }
                return pd.MPCharge > previousCharge;
            }

            bool result = orig(pd, amount);
            pd.maxMP = 33;
            pd.MPCharge = Mathf.Min(pd.MPCharge, 33);
            return result;
        }

        private void TrackShellWhiteHealthLoss(Action<PlayerData, int> orig, PlayerData pd, int amount)
        {
            GameManager gm = GameManager.instance;
            if (!shellLocked || pd == null || gm == null || gm.playerData != pd)
            {
                orig(pd, amount);
                return;
            }

            int before = pd.GetInt("health");
            try
            {
                orig(pd, amount);
            }
            finally
            {
                if (shellLocked && GameManager.instance != null && GameManager.instance.playerData == pd)
                {
                    int after = pd.GetInt("health");
                    if (after < before && !shellWhiteHealthLost)
                    {
                        shellWhiteHealthLost = true;
                        OrbRadiance.Instance?.Log("[blue-phase-locks] Shell期间白血实际下降 " + before + "->" + after);
                    }
                }
            }
        }

        private void CheckBoundSoul(Action<GGCheckBoundSoul> orig, GGCheckBoundSoul action)
        {
            GameManager gm = GameManager.instance;
            PlayMakerFSM soulOrb = gm != null ? gm.soulOrb_fsm : null;
            if (soulLocked && action != null && soulOrb != null && action.Fsm == soulOrb.Fsm &&
                action.boundEvent != null)
            {
                // 雕像战不处于万神殿序列；本魂槽全部绑定判断仍走各自原生事件。
                bool bindingState = soulOrb.ActiveStateName == "Binding?";
                action.Fsm.Event(action.boundEvent);
                action.Finish();
                if (bindingState && !soulBindingEventLogged)
                {
                    soulBindingEventLogged = true;
                    OrbRadiance.Instance?.Log("[blue-phase-locks] Soul Orb FSM 已进入原生 Bound 状态");
                }
                return;
            }
            orig(action);
        }

        private int GetPlayerInt(string name, int original)
        {
            if (soulLocked && name == "maxMP") return 33;
            if (soulLocked && name == "MPCharge") return Mathf.Min(original, 33);
            return original;
        }

        private bool GetPlayerBool(string name, bool original)
        {
            if (!charmsLocked) return original;
            if (name == "overcharmed" || name.StartsWith("equippedCharm_", StringComparison.Ordinal)) return false;
            return original;
        }

        private object GetPlayerVariable(Type type, string name, object original)
        {
            if (charmsLocked && name == "equippedCharms" && type == typeof(List<int>))
            {
                virtualEquippedCharms.Clear();
                return virtualEquippedCharms;
            }
            return original;
        }

        private void BeforeSave(SaveGameData data)
        {
            if ((!soulLocked && !shellLocked && !charmsLocked) || data == null || data.playerData == null) return;
            PlayerData copy = (PlayerData)typeof(object).GetMethod("MemberwiseClone",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(data.playerData, null);
            if (soulLocked) copy.maxMP = originalMaxMp;
            if ((shellLocked || charmsLocked) && derivedStateCaptured)
            {
                copy.maxHealth = originalMaxHealth;
                copy.healthBlue = originalHealthBlue;
                copy.joniHealthBlue = originalJoniHealthBlue;
            }
            data.playerData = copy;
        }

        private void LockSoul()
        {
            GameManager gm = GameManager.instance;
            if (gm == null || gm.playerData == null) return;
            PlayerData pd = gm.playerData;
            nativeBoundSoul = BossSequenceController.BoundSoul;
            originalMaxMp = pd.maxMP;
            soulLocked = true;
            soulGainBlockLogged = false;
            soulBindingEventLogged = false;
            if (!nativeBoundSoul)
            {
                pd.maxMP = 33;
                pd.ClearMP();
                if (gm.soulOrb_fsm != null) gm.soulOrb_fsm.SendEvent("MP LOSE");
                if (gm.soulVessel_fsm != null) gm.soulVessel_fsm.SendEvent("MP RESERVE DOWN");
                EventRegister.SendEvent("BIND VESSEL ORB");
            }
            if (OrbRadiance.Instance != null) OrbRadiance.Instance.Log("[blue-phase-locks] 10s Soul 束缚");
            BluePhaseBindingAudio.PlaySoul(transform);
        }

        private void CaptureDerivedState(PlayerData pd)
        {
            if (derivedStateCaptured || pd == null) return;
            originalMaxHealth = pd.maxHealth;
            originalHealthBlue = pd.healthBlue;
            originalJoniHealthBlue = pd.joniHealthBlue;
            derivedStateCaptured = true;
        }

        private void LockShell()
        {
            GameManager gm = GameManager.instance;
            if (gm == null || gm.playerData == null) return;
            nativeBoundShellAtStart = BossSequenceController.BoundShell;
            shellHealthBeforeLock = gm.playerData.GetInt("health");
            shellCapApplied = false;
            shellWhiteHealthLost = false;
            shellLocked = true;
            PlayerData pd = gm.playerData;
            CaptureDerivedState(pd);
            int currentHealth = shellHealthBeforeLock;
            int currentBlue = pd.healthBlue;
            if (HeroController.instance != null) HeroController.instance.CharmUpdate();
            pd.health = Mathf.Min(currentHealth, pd.CurrentMaxHealth);
            shellCapApplied = !nativeBoundShellAtStart && currentHealth > pd.GetInt("health");
            pd.healthBlue = Mathf.Min(currentBlue, pd.healthBlue);
            EventRegister.SendEvent("UPDATE BLUE HEALTH");
            PlayMakerFSM.BroadcastEvent("HUD IN");
            PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK");
            if (OrbRadiance.Instance != null)
                OrbRadiance.Instance.Log("[blue-phase-locks] 20s Shell 束缚 health=" + currentHealth + "->" + pd.GetInt("health") +
                    " capApplied=" + shellCapApplied + " nativeBound=" + nativeBoundShellAtStart);
            BluePhaseBindingAudio.PlayShell(transform);
        }

        private void LockNail()
        {
            nativeBoundNail = BossSequenceController.BoundNail;
            nailLocked = true;
            if (!nativeBoundNail) EventRegister.SendEvent("SHOW BOUND NAIL");
            if (OrbRadiance.Instance != null) OrbRadiance.Instance.Log("[blue-phase-locks] 30s Nail 束缚");
            BluePhaseBindingAudio.PlayNail(transform);
        }

        private void LockCharms()
        {
            GameManager gm = GameManager.instance;
            if (gm == null || gm.playerData == null) return;
            nativeBoundCharms = BossSequenceController.BoundCharms;
            int currentHealth = gm.playerData.GetInt("health");
            CaptureDerivedState(gm.playerData);
            charmsLocked = true;
            UpdateCharmPresentation();
            gm.playerData.health = Mathf.Min(currentHealth, gm.playerData.CurrentMaxHealth);
            charmsAppliedAt = elapsed;
            if (OrbRadiance.Instance != null) OrbRadiance.Instance.Log("[blue-phase-locks] 40s Charms 束缚");
            BluePhaseBindingAudio.PlayCharms(transform);
            if (!radianceCryPlayed && soulLocked && shellLocked && nailLocked && charmsLocked)
            {
                radianceCryPlayed = true;
                BluePhaseBindingAudio.PlayRadianceCry(control, transform);
            }
        }

        private void BeforeRestoreBindings(On.BossSequenceController.orig_RestoreBindings orig)
        {
            // 原版 RestoreBindings 在 BoundCharms 为真时会访问 currentData；先清除本组件的虚拟束缚。
            Clear();
            orig();
        }

        private void UpdateCharmPresentation()
        {
            if (HeroController.instance != null) HeroController.instance.CharmUpdate();
            PlayMakerFSM.BroadcastEvent("CHARM EQUIP CHECK");
            PlayMakerFSM.BroadcastEvent("HUD IN");
            EventRegister.SendEvent("SHOW BOUND CHARMS");
            PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK");
        }

        private void RestoreCharmPresentation()
        {
            if (HeroController.instance != null) HeroController.instance.CharmUpdate();
        }

        private void SpawnPermanentOrb()
        {
            orbSpawned = true;
            HeroController hero = HeroController.instance;
            GameObject prefab = GetOrbPrefab();
            if (hero == null || prefab == null) return;
            GameObject staging = new GameObject("Blue Phase Orb Staging");
            staging.SetActive(false);
            GameObject orb = Instantiate(prefab, hero.transform.position, Quaternion.identity, staging.transform);
            if (orb == null) return;
            PermanentRadiantOrb marker = orb.AddComponent<PermanentRadiantOrb>();
            permanentOrb = orb;
            orb.SetActive(false);
            orb.transform.position = hero.transform.position;
            foreach (DamageHero damage in orb.GetComponentsInChildren<DamageHero>(true))
            { damage.resetOnEnable = false; damage.damageDealt = 9999; }
            foreach (EnemyBullet bullet in orb.GetComponentsInChildren<EnemyBullet>(true)) bullet.enabled = false;
            foreach (AutoRecycleSelf recycle in orb.GetComponentsInChildren<AutoRecycleSelf>(true)) recycle.enabled = false;
            foreach (RecycleAfter2dtkAnimation recycle in orb.GetComponentsInChildren<RecycleAfter2dtkAnimation>(true)) recycle.enabled = false;
            foreach (PlayAudioAndRecycle recycle in orb.GetComponentsInChildren<PlayAudioAndRecycle>(true)) recycle.enabled = false;
            foreach (ParticleSystemAutoRecycle recycle in orb.GetComponentsInChildren<ParticleSystemAutoRecycle>(true)) recycle.enabled = false;
            foreach (Collider2D collider in orb.GetComponentsInChildren<Collider2D>(true))
            { collider.enabled = true; collider.isTrigger = true; }
            Rigidbody2D body = orb.GetComponent<Rigidbody2D>();
            if (body != null) body.isKinematic = false;
            if (!marker.Configure())
            {
                permanentOrb = null;
                Destroy(orb);
                Destroy(staging);
                return;
            }
            foreach (PlayMakerFSM fsm in orb.GetComponentsInChildren<PlayMakerFSM>(true))
                if (fsm != null) fsm.enabled = false;
            orb.transform.SetParent(null, true);
            Destroy(staging);
            orb.SetActive(true);
            tk2dSpriteAnimator animator = orb.GetComponentInChildren<tk2dSpriteAnimator>(true);
            if (animator != null) animator.Play("Idle");
            marker.ApplyRedTint();
            if (OrbRadiance.Instance != null) OrbRadiance.Instance.Log("[blue-phase-locks] 45s 已生成独立永久贴合光球");
        }

        private GameObject GetOrbPrefab()
        {
            if (control == null) return null;
            foreach (PlayMakerFSM fsm in control.gameObject.GetComponents<PlayMakerFSM>())
            {
                if (fsm == null || fsm.Fsm == null || fsm.FsmName != "Attack Commands") continue;
                FsmState state = fsm.Fsm.GetState("Spawn Fireball");
                SpawnObjectFromGlobalPool spawn = state != null
                    ? state.Actions.OfType<SpawnObjectFromGlobalPool>().FirstOrDefault() : null;
                return spawn != null && spawn.gameObject != null ? spawn.gameObject.Value : null;
            }
            return null;
        }

        private static void RestoreEmptyHealthSlots(PlayerData pd)
        {
            if (pd == null) return;
            if (pd.health <= 0)
            {
                OrbRadiance.Instance?.Log("[blue-phase-locks] 清理时HP<=0，跳过空血格HUD修复");
                return;
            }

            GameCameras cameras = GameCameras.instance;
            GameObject hudCanvas = cameras != null ? cameras.hudCanvas : null;
            Transform healthRoot = hudCanvas != null ? hudCanvas.transform.Find("Health") : null;
            if (healthRoot == null)
            {
                OrbRadiance.Instance?.LogWarn("[blue-phase-locks] 找不到HUD Health根节点，跳过空血格HUD修复");
                return;
            }

            int maxHealth = Mathf.Min(pd.CurrentMaxHealth, 9);
            int restored = 0;
            for (int healthNumber = pd.health + 1; healthNumber <= maxHealth; healthNumber++)
            {
                Transform slot = healthRoot.Find("Health " + healthNumber);
                if (slot == null) continue;
                PlayMakerFSM fsm = slot.GetComponent<PlayMakerFSM>();
                MeshRenderer selfRenderer = slot.GetComponent<MeshRenderer>();
                if (fsm == null || fsm.FsmName != "health_display" || selfRenderer == null || selfRenderer.enabled)
                    continue;
                selfRenderer.enabled = true;
                restored++;
            }

            if (restored > 0)
                OrbRadiance.Instance?.Log("[blue-phase-locks] 已恢复空白生命槽根Renderer " + restored + " 个；health=" + pd.health + " max=" + maxHealth);
        }

        private void Clear()
        {
            bool hadSoulLock = soulLocked;
            bool hadNailLock = nailLocked;
            bool hadShellLock = shellLocked;
            bool hadCharmsLock = charmsLocked;
            GameManager gm = GameManager.instance;
            PlayerData pd = gm != null ? gm.playerData : null;
            int hp = pd != null ? pd.GetInt("health") : 0;
            int blue = pd != null ? pd.healthBlue : 0;
            int mp = pd != null ? pd.GetInt("MPCharge") : 0;
            int reserve = pd != null ? pd.GetInt("MPReserve") : 0;
            HeroController hero = HeroController.instance;
            bool heroDead = hero != null && hero.cState.dead;
            bool restoreShellHealth = hadShellLock && shellCapApplied && !shellWhiteHealthLost &&
                                      !nativeBoundShellAtStart && shellHealthBeforeLock > 0 && hp > 0 && !heroDead;
            string shellRestoreReason = !hadShellLock ? "no temporary Shell" :
                hp <= 0 || heroDead ? "dead" : nativeBoundShellAtStart ? "native Shell preserved" :
                shellWhiteHealthLost ? "white health damage preserved" : !shellCapApplied ? "no temporary cap loss" :
                "restore pre-lock health";

            shellLocked = soulLocked = nailLocked = charmsLocked = false;
            soulBindingEventLogged = false;
            RemoveHooks();
            if (hadSoulLock && pd != null) pd.maxMP = originalMaxMp;
            if (hadShellLock || hadCharmsLock) RestoreCharmPresentation();
            if (pd != null)
            {
                if (hadSoulLock || hadShellLock || hadCharmsLock)
                {
                    int requestedHealth = restoreShellHealth ? shellHealthBeforeLock : hp;
                    pd.health = Mathf.Min(requestedHealth, pd.CurrentMaxHealth);
                    pd.healthBlue = blue;
                    pd.MPCharge = Mathf.Min(mp, pd.maxMP);
                    pd.MPReserve = Mathf.Min(reserve, pd.MPReserveMax);
                }
                if (hadShellLock)
                    OrbRadiance.Instance?.Log("[blue-phase-locks] Shell解除 health=" + hp + "->" + pd.GetInt("health") +
                        " restore=" + restoreShellHealth + " reason=" + shellRestoreReason);
                if (hadShellLock || hadCharmsLock) RestoreEmptyHealthSlots(pd);
            }
            if (permanentOrb != null)
            {
                permanentOrb.SetActive(false);
                Destroy(permanentOrb);
            }
            permanentOrb = null;
            BluePhaseBindingAudio.StopAll();
            if (countdownHud != null)
            {
                countdownHud.SetVisible(false);
                Destroy(countdownHud.gameObject);
                countdownHud = null;
            }
            if (hadSoulLock && !nativeBoundSoul) EventRegister.SendEvent("UNBIND VESSEL ORB");
            if (hadSoulLock && gm != null)
            {
                PlayMakerFSM soulOrb = gm.soulOrb_fsm;
                bool nativeSoulStillBound = BossSequenceController.BoundSoul;
                if (soulOrb == null || soulOrb.Fsm == null)
                {
                    OrbRadiance.Instance?.LogWarn("[blue-phase-locks] Soul解除时找不到Soul Orb FSM");
                }
                else if (!nativeBoundSoul && !nativeSoulStillBound)
                {
                    FsmGameObject capVariable = soulOrb.FsmVariables.GetFsmGameObject("Binding Cap");
                    FsmGameObject capFullVariable = soulOrb.FsmVariables.GetFsmGameObject("Binding Cap Full");
                    GameObject cap = capVariable != null ? capVariable.Value : null;
                    GameObject capFull = capFullVariable != null ? capFullVariable.Value : null;
                    string beforeState = soulOrb.ActiveStateName;
                    OrbRadiance.Instance?.Log("[blue-phase-locks] Soul解绑刷新前 state=" + beforeState +
                        " cap=" + DescribeSoulCap(cap) + " capFull=" + DescribeSoulCap(capFull));

                    if (soulOrb.Fsm.GetState("Not Bound") != null)
                    {
                        soulOrb.SetState("Not Bound");
                        OrbRadiance.Instance?.Log("[blue-phase-locks] Soul解绑刷新后 state=" + soulOrb.ActiveStateName +
                            " cap=" + DescribeSoulCap(cap) + " capFull=" + DescribeSoulCap(capFull));
                    }
                    else
                    {
                        OrbRadiance.Instance?.LogWarn("[blue-phase-locks] Soul Orb FSM缺少Not Bound状态，回退MP LOSE事件");
                        soulOrb.SendEvent("MP LOSE");
                    }
                }
                else
                {
                    OrbRadiance.Instance?.Log("[blue-phase-locks] Soul解绑保留原生束缚 displayState=" +
                        soulOrb.ActiveStateName + " nativeBound=" + nativeSoulStillBound);
                    soulOrb.SendEvent("MP LOSE");
                }
                if (gm.soulVessel_fsm != null) gm.soulVessel_fsm.SendEvent("MP RESERVE DOWN");
            }
            if (hadNailLock && !nativeBoundNail) EventRegister.SendEvent("HIDE BOUND NAIL");
            if (hadCharmsLock && !nativeBoundCharms) EventRegister.SendEvent("HIDE BOUND CHARMS");
            if (hadShellLock || hadCharmsLock) EventRegister.SendEvent("UPDATE BLUE HEALTH");
            if (hadCharmsLock) PlayMakerFSM.BroadcastEvent("CHARM EQUIP CHECK");
            if (hadShellLock || hadCharmsLock) PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK");
            if (hadSoulLock || hadShellLock || hadCharmsLock) PlayMakerFSM.BroadcastEvent("HUD IN");
            running = false;
            elapsed = 0f;
            orbSpawned = false;
            radianceCryPlayed = false;
            derivedStateCaptured = false;
            nativeBoundShellAtStart = false;
            shellCapApplied = false;
            shellWhiteHealthLost = false;
            shellHealthBeforeLock = 0;
        }

        private static string DescribeSoulCap(GameObject cap)
        {
            return cap == null ? "missing" : (cap.activeSelf + "/" + cap.activeInHierarchy);
        }
    }

    internal sealed class PermanentRadiantOrb : MonoBehaviour
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int FlashColorId = Shader.PropertyToID("_FlashColor");
        private Rigidbody2D body;
        private HeroController hero;
        private float displayZ;

        internal void ApplyRedTint()
        {
            int tk2dCount = 0;
            foreach (tk2dBaseSprite sprite in GetComponentsInChildren<tk2dBaseSprite>(true))
            {
                if (sprite == null) continue;
                sprite.color = MultiplyByRed(sprite.color);
                tk2dCount++;
            }

            int spriteRendererCount = 0;
            foreach (SpriteRenderer sprite in GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sprite == null) continue;
                sprite.color = MultiplyByRed(sprite.color);
                spriteRendererCount++;
            }

            int meshRendererCount = 0;
            foreach (MeshRenderer renderer in GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer == null) continue;
                TintMeshRenderer(renderer);
                meshRendererCount++;
            }

            int particleSystemCount = 0;
            foreach (ParticleSystem particles in GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particles == null) continue;
                TintParticleSystem(particles);
                particleSystemCount++;
            }

            OrbRadiance.Instance?.Log("[blue-phase-orb] 红色滤镜已应用：tk2d=" + tk2dCount +
                " SpriteRenderer=" + spriteRendererCount + " MeshRenderer=" + meshRendererCount +
                " ParticleSystem=" + particleSystemCount);
        }

        private static Color MultiplyByRed(Color color)
        {
            return new Color(color.r, 0f, 0f, color.a);
        }

        private static void TintMeshRenderer(MeshRenderer renderer)
        {
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null) continue;
                renderer.GetPropertyBlock(block, i);
                bool changed = false;
                if (material.HasProperty(ColorId))
                {
                    Color original = block.GetColor(ColorId);
                    if (original == Color.clear) original = material.GetColor(ColorId);
                    block.SetColor(ColorId, MultiplyByRed(original));
                    changed = true;
                }
                if (material.HasProperty(FlashColorId))
                {
                    Color original = block.GetColor(FlashColorId);
                    if (original == Color.clear) original = material.GetColor(FlashColorId);
                    block.SetColor(FlashColorId, MultiplyByRed(original));
                    changed = true;
                }
                if (changed) renderer.SetPropertyBlock(block, i);
                block.Clear();
            }
        }

        private static void TintParticleSystem(ParticleSystem particles)
        {
            ParticleSystem.MainModule main = particles.main;
            ParticleSystem.MinMaxGradient startColor = main.startColor;
            switch (startColor.mode)
            {
                case ParticleSystemGradientMode.Color:
                    startColor.color = MultiplyByRed(startColor.color);
                    break;
                case ParticleSystemGradientMode.TwoColors:
                    startColor.colorMin = MultiplyByRed(startColor.colorMin);
                    startColor.colorMax = MultiplyByRed(startColor.colorMax);
                    break;
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor:
                    startColor.gradient = CloneRedGradient(startColor.gradient);
                    break;
                case ParticleSystemGradientMode.TwoGradients:
                    startColor.gradientMin = CloneRedGradient(startColor.gradientMin);
                    startColor.gradientMax = CloneRedGradient(startColor.gradientMax);
                    break;
            }
            main.startColor = startColor;

            int capacity = main.maxParticles;
            if (capacity <= 0) return;
            ParticleSystem.Particle[] active = new ParticleSystem.Particle[capacity];
            int count = particles.GetParticles(active);
            for (int i = 0; i < count; i++)
                active[i].startColor = MultiplyByRed(active[i].startColor);
            if (count > 0) particles.SetParticles(active, count);
        }

        private static Gradient CloneRedGradient(Gradient source)
        {
            if (source == null) return null;
            GradientColorKey[] colors = source.colorKeys;
            for (int i = 0; i < colors.Length; i++) colors[i].color = MultiplyByRed(colors[i].color);
            Gradient copy = new Gradient();
            copy.SetKeys(colors, source.alphaKeys);
            copy.mode = source.mode;
            return copy;
        }

        internal bool Configure()
        {
            body = GetComponent<Rigidbody2D>();
            hero = HeroController.instance;
            if (body == null || hero == null) return false;
            displayZ = transform.position.z;
            body.interpolation = RigidbodyInterpolation2D.None;
            body.gravityScale = 0f;
            body.velocity = Vector2.zero;
            body.angularVelocity = 0f;
            SyncToHero();
            OrbRadiance.Instance?.Log("[blue-phase-orb] position lock enabled; Rigidbody2D interpolation=None, gravityScale=0");
            return true;
        }

        private void SyncToHero()
        {
            if (hero == null || body == null) return;
            Vector3 heroPosition = hero.transform.position;
            body.position = new Vector2(heroPosition.x, heroPosition.y);
            body.velocity = Vector2.zero;
            body.angularVelocity = 0f;
            transform.position = new Vector3(heroPosition.x, heroPosition.y, displayZ);
        }

        private void FixedUpdate() { SyncToHero(); }
        private void LateUpdate() { SyncToHero(); }
    }
}

using System;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    internal sealed class HiddenRoomDoor : MonoBehaviour, IHitResponder
    {
        private PlayMakerFSM wallFsm;
        private BoxCollider2D attackCollider;
        private FsmInt hitsRemaining;
        private readonly int[] nativeFacingValues = new int[4];

        internal bool Initialize(GameObject wallPrefab)
        {
            attackCollider = GetComponent<BoxCollider2D>();
            if (attackCollider == null || wallPrefab == null) return false;

            var staging = new GameObject("Native hidden wall staging");
            staging.transform.SetParent(transform, false);
            staging.SetActive(false);
            GameObject clone = UnityEngine.Object.Instantiate(wallPrefab, staging.transform, false);
            clone.name = "Native breakable hidden wall";
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            BoxCollider2D sourceBox = clone.GetComponentInChildren<BoxCollider2D>(true);
            if (sourceBox != null && sourceBox.size.x > 0f && sourceBox.size.y > 0f)
                clone.transform.localScale = new Vector3(.8f / sourceBox.size.x, 3.8f / sourceBox.size.y, 1f);

            foreach (Collider2D collider in clone.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
            Transform cameraLocks = clone.transform.Find("Camera Locks");
            if (cameraLocks != null) cameraLocks.gameObject.SetActive(false);
            foreach (PersistentBoolItem persistent in clone.GetComponentsInChildren<PersistentBoolItem>(true))
            {
                persistent.enabled = false;
                UnityEngine.Object.Destroy(persistent);
            }

            PlayMakerFSM[] fsms = clone.GetComponentsInChildren<PlayMakerFSM>(true);
            wallFsm = fsms.FirstOrDefault(f => f.FsmName == "breakable_wall_v2");
            if (wallFsm == null) return false;
            foreach (PlayMakerFSM fsm in fsms)
            {
                if (!PreparePrivateFsm(fsm)) return false;
                foreach (FsmState state in fsm.FsmStates)
                {
                    foreach (FsmStateAction action in state.Actions)
                    {
                        if (action is PlayerDataBoolTest || action is SetPlayerDataBool ||
                            action is PlayParticleEmitter || action is SpawnFromPool || action is SetParent)
                            action.Enabled = false;
                    }
                }
            }

            FsmVariables variables = wallFsm.FsmVariables;
            FsmInt hits = variables.GetFsmInt("Hits");
            FsmFloat direction = variables.GetFsmFloat("Attack Direction");
            FsmInt facing = variables.GetFsmInt("Facing");
            FsmString playerDataBool = variables.GetFsmString("PlayerData Bool");
            FsmBool activated = variables.GetFsmBool("Activated");
            FsmBool ruinLift = variables.GetFsmBool("Ruin Lift");
            if (hits == null || direction == null || facing == null || playerDataBool == null || activated == null || ruinLift == null) return false;
            FsmState checkDirection = wallFsm.Fsm.GetState("Check Direction");
            IntSwitch directionSwitch = checkDirection == null ? null : checkDirection.Actions.OfType<IntSwitch>().FirstOrDefault();
            if (directionSwitch == null || directionSwitch.compareTo == null || directionSwitch.sendEvent == null ||
                directionSwitch.compareTo.Length != directionSwitch.sendEvent.Length)
            {
                OrbRadiance.Instance?.LogError("HiddenRoom: 原生 Check Direction IntSwitch 映射缺失或不匹配");
                return false;
            }
            bool[] foundFacing = new bool[4];
            for (int i = 0; i < directionSwitch.sendEvent.Length; i++)
            {
                FsmInt compare = directionSwitch.compareTo[i];
                FsmEvent send = directionSwitch.sendEvent[i];
                if (compare == null || send == null) continue;
                int cardinal;
                switch (send.Name)
                {
                    case "RIGHT": cardinal = DirectionUtils.Right; break;
                    case "UP": cardinal = DirectionUtils.Up; break;
                    case "LEFT": cardinal = DirectionUtils.Left; break;
                    case "DOWN": cardinal = DirectionUtils.Down; break;
                    default: continue;
                }
                nativeFacingValues[cardinal] = compare.Value;
                foundFacing[cardinal] = true;
            }
            if (foundFacing.Any(found => !found))
            {
                OrbRadiance.Instance?.LogError("HiddenRoom: 原生 Check Direction IntSwitch 未提供完整四向映射");
                return false;
            }
            hits.Value = 4;
            hitsRemaining = hits;
            direction.Value = 0f;
            facing.Value = nativeFacingValues[DirectionUtils.Right];
            playerDataBool.Value = string.Empty;
            activated.Value = false;
            ruinLift.Value = false;

            FsmState idle = wallFsm.Fsm.GetState("Idle");
            SetPosition setPosition = idle == null ? null : idle.Actions.OfType<SetPosition>().FirstOrDefault();
            if (setPosition == null) return false;
            setPosition.space = Space.World;

            FsmState breakState = wallFsm.Fsm.GetState("Break");
            if (breakState == null) return false;
            FsmStateAction[] actions = breakState.Actions;
            var extended = new FsmStateAction[actions.Length + 1];
            extended[0] = new OpenWorkshopRoomOnBreak { Door = this };
            Array.Copy(actions, 0, extended, 1, actions.Length);
            breakState.Actions = extended;

            clone.SetActive(true);
            staging.SetActive(true);
            return true;
        }

        internal static bool PreparePrivateFsm(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                OrbRadiance.Instance?.LogError("HiddenRoom: clone FSM missing; cannot prepare private actions");
                return false;
            }
            if (fsm.UsesTemplate)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                MethodInfo initTemplate = fsm.GetType().GetMethod("InitTemplate", flags);
                FieldInfo templateField = fsm.GetType().GetField("fsmTemplate", flags);
                if (initTemplate == null || templateField == null)
                {
                    OrbRadiance.Instance?.LogError("HiddenRoom: PlayMaker template detach members missing; refusing to edit shared template");
                    return false;
                }
                try
                {
                    // InitTemplate 会创建私有 FSM 副本，并保留当前克隆体的实例变量。
                    initTemplate.Invoke(fsm, null);
                    templateField.SetValue(fsm, null);
                }
                catch (Exception e)
                {
                    OrbRadiance.Instance?.LogError("HiddenRoom: could not detach cloned FSM template: " + e);
                    return false;
                }
                if (fsm.UsesTemplate || fsm.Fsm == null)
                {
                    OrbRadiance.Instance?.LogError("HiddenRoom: cloned FSM template did not detach cleanly");
                    return false;
                }
            }
            try
            {
                // 预加载克隆可能尚未激活，Awake 还没有装入序列化动作。
                // 先用 InitData 加载私有 FSM 再修改，防止后续 Awake 从模板重建并覆盖改动。
                fsm.Fsm.InitData();
                return true;
            }
            catch (Exception e)
            {
                OrbRadiance.Instance?.LogError("HiddenRoom: clone FSM InitData failed: " + e);
                return false;
            }
        }

        public void Hit(HitInstance hit)
        {
            if (!isActiveAndEnabled || (hit.AttackType != AttackTypes.Nail && hit.AttackType != AttackTypes.Spell) ||
                !OrbRadiance.Settings.Enabled || wallFsm == null ||
                WorkshopHiddenRoom.IsOpen || !WorkshopHiddenRoom.IsCurrentScene ||
                gameObject.scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene() ||
                wallFsm.ActiveStateName != "Idle") return;

            if (hit.AttackType == AttackTypes.Spell)
            {
                // 保留原版法术破坏特效和破坏回调，但不触发骨钉击退，
                // 也不消耗墙壁规定的四次骨钉命中次数。
                if (wallFsm.Fsm.GetState("Spell Destroy") == null)
                {
                    OrbRadiance.Instance?.LogWarn("HiddenRoom: 原生 Spell Destroy 状态缺失，忽略法术命中");
                    return;
                }
                wallFsm.SetState("Spell Destroy");
                OrbRadiance.Instance?.LogDebug("HiddenRoom: acceptedSpell native Spell Destroy path");
                return;
            }

            FsmFloat direction = wallFsm.FsmVariables.GetFsmFloat("Attack Direction");
            FsmInt facing = wallFsm.FsmVariables.GetFsmInt("Facing");
            if (direction == null || facing == null) return;
            direction.Value = hit.Direction;
            int cardinal = DirectionUtils.GetCardinalDirection(hit.Direction);
            facing.Value = nativeFacingValues[cardinal];
            RecoilHero(cardinal);
            OrbRadiance.Instance?.LogDebug("HiddenRoom: acceptedNail cardinal=" + cardinal + " facing=" + facing.Value + " remainingHits=" + Mathf.Max(0, hitsRemaining.Value - 1));
            wallFsm.SetState("Hit");
        }

        private static void RecoilHero(int cardinal)
        {
            HeroController hero = HeroController.instance;
            if (hero == null) return;
            switch (cardinal)
            {
                case DirectionUtils.Right: hero.RecoilLeft(); break;
                case DirectionUtils.Up: hero.RecoilDown(); break;
                case DirectionUtils.Left: hero.RecoilRight(); break;
                // 向下的骨钉攻击由 NailSlash 自行处理原版下劈反弹。
                case DirectionUtils.Down: break;
            }
        }

        internal bool TryOpenFromBreak()
        {
            if (!WorkshopHiddenRoom.OpenDoor())
            {
                OrbRadiance.Instance?.LogWarn("HiddenRoom: nativeBreak callback failed; native wall returns to Idle");
                return false;
            }
            WorkshopHiddenRoom.RecordWallBroken();
            if (attackCollider != null) attackCollider.enabled = false;
            OrbRadiance.Instance?.LogDebug("HiddenRoom: nativeBreak callback succeeded");
            return true;
        }
    }

    internal sealed class OpenWorkshopRoomOnBreak : FsmStateAction
    {
        internal HiddenRoomDoor Door;
        public override void OnEnter()
        {
            if (Door == null || !Door.TryOpenFromBreak()) Fsm.SetState("Idle");
            Finish();
        }
    }
}

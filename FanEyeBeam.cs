using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P2 脸刺配套激光：Z 的 4 枚、N 的 18 枚剑刺分别配对私有光束；H 每扇使用原束及 5 道私有束。
    // 私有光束必须独立于原版眼激光链，避免影响其他攻击及取消时残留。


    internal class FanEyeBeam : MonoBehaviour
    {
        // ---------------- 时间点（状态名，与 P2NailFanRing / RageEyeBeam 同一套）----------------

        private const string StateStart = "Arena 2 Start";   // 进 P2
        private const string StateStop = "Ascend Tele";      // 出 P2
        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };
        private const string ArenaFsmOwner = "Boss Control";

        // ---------------- 脸刺 / 眼激光 ----------------

        private const string GlowName = "Eye Beam Glow";
        private const string BurstName = "Burst 1";
        private const string BeamPrefix = "Radiant Beam";    // Burst 1 下的光束子物体（含基础那个）
        private const string WindupA = "Nail Fan";           // 起手（θ 当帧写好）
        private const string WindupB = "Eb Extra Wait 2";    // Nail Fan 之后，兜底识别起手
        private static readonly string[] SpawnStates = { "CW Spawn", "CCW Spawn" };
        private static readonly string[] RestartStates = { "CW Restart", "CCW Restart" };

        private const float SectorCenter = 36f;   // 扇面半角 = (360/5)/2
        // 校准（2026-10-05 用户实测）：按 θ∓36 瞄准后整体再顶这个角，光束才落在扇心。
        //用户自己改成了现在的数值，没下指令不准调回去
        private const float AimOffset = 89f;
        private const float FireDelay = 0.2f;     
        private const float EndDelay = 0.35f;      
        private const float NormalCleanupDelay = 0.6f;

        private static readonly string[] BurstLiveStates = { "Antic", "Fire" };

        // ---------------- 运行时 ----------------

        private PlayMakerFSM control;               // Absolute Radiance/Control：阶段
        private PlayMakerFSM commands;              // Absolute Radiance/Attack Commands：脸刺链 + θ

        private PlayMakerFSM arenaFsm;              // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private Transform burst;                    
        private Transform[] beams;                  // Burst 1 下的一排 Radiant Beam，每扇占一道
        private AudioSource audio;
        private AudioClip prepareClip;
        private AudioClip burstClip;

        private bool inP2;
        private string lastState;
        private float theta;
        private bool firedFirstSector;
        private int ringSeq;                        // 本招已点亮的扇序号（0-based）
        private readonly List<Coroutine> shots = new List<Coroutine>();
        // 只清理本组件实际驱动过的原生光束；其它 Mod 可能正在使用 Burst 1。
        private readonly HashSet<Transform> ownedNativeBeams = new HashSet<Transform>();

        private sealed class NormalBeamSet
        {
            internal GameObject root;
            internal PlayMakerFSM[] fsms;
            internal bool[] anticipated;
            internal bool[] fired;
            internal int nailsPerWave;
            internal int endsSent;
            internal readonly List<Coroutine> routines = new List<Coroutine>();
        }

        private NormalBeamSet normalBeamSet;
        private readonly List<NormalBeamSet> normalBeamSets = new List<NormalBeamSet>();

        private sealed class HardExtraBeamSet
        {
            internal GameObject root;
            internal PlayMakerFSM[] fsms;
            // H 模式只处理当前攻击实际占用的 5 道原版光束，不遍历整个 Burst 1。
            internal PlayMakerFSM[] originalFsms;
            internal bool[] anticipated;
            internal bool[] fired;
            internal int endsSent;
        }

        private HardExtraBeamSet hardExtraBeamSet;
        private static readonly float[] HardExtraOffsets = { 180f, 170f, 190f, 155f, 205f };

        private static FanEyeBeam active;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
            active = this;
        }

        // 总开关关掉时 OrbRadiance.RestorePatched 走这里收链
        internal static void Restore()
        {
            if (active != null) active.Disarm();
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // 总开关关掉：收链（脸刺恢复，激光不再补射）
            if (!OrbRadiance.Settings.Enabled)
            {
                Disarm();
                inP2 = false;
                lastState = null;
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同场景重来，Control 不会自己回 Init
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) { Disarm(); inP2 = false; }
            }

            // P2 窗口（与 P2NailFanRing 同一套）
            string ctrl = control.Fsm.ActiveStateName;
            if (ctrl == StateStart) inP2 = true;
            else if (ctrl == StateStop) { inP2 = false; Disarm(); }

            if (!inP2) { lastState = null; return; }
            if (IsNZVolleyMode()) { lastState = null; return; }
            if (!EnsureCommands()) return;

            string cur = commands.Fsm.ActiveStateName;
            bool edge = cur != lastState;
            lastState = cur;

            // 起手：记下脸刺起始角 θ（Nail Fan 里当帧写好；Eb Extra Wait 2 兜底），并重置本招计数
            if (edge && (cur == WindupA || cur == WindupB))
            {
                if (cur == WindupA || !OrbRadiance.EffectiveHardMode) StopShots();
                firedFirstSector = false;
                ringSeq = 0;
                theta = Rotation();
                if (cur == WindupA && OrbRadiance.EffectiveHardMode) PrepareHardExtraBeams();
            }

            // 第 1 扇：第一次进 CW/CCW Spawn（此时 Rotation 已由 Nail Fan 写好 = θ）
            if (!firedFirstSector && edge && SpawnStates.Contains(cur))
            {
                firedFirstSector = true;
                Fire(ringSeq++, theta, IsCw(cur));
            }

            // 第 2..n 扇：每进一次 CW/CCW Restart（= 下一扇起手）就补一道，
            // 扇心按 Restart 当刻的 Rotation（该扇起始角）算
            if (edge && RestartStates.Contains(cur))
                Fire(ringSeq++, Rotation(), IsCw(cur));
        }

        private static bool IsCw(string state) => state.StartsWith("CW");

        private static bool IsNZVolleyMode() => NFaceVolleySchedule.IsNZVolleyMode(
            OrbRadiance.EffectiveHardMode);

        // Z 的 4 枚、N 的 18 枚剑刺各配一束私有克隆光束。
        // H 模式直接驱动原版光束；N/Z 只把原版 Burst 光束作为克隆模板。
        internal bool PrepareNormalBeams(int count)
        {
            int nailsPerWave = OrbRadiance.EffectiveEasyMode ? 4 : 6;
            if ((count != 4 && count != 18) || count % nailsPerWave != 0 ||
                !EnsureCommands() || !ResolveBurst() || beams == null || beams.Length == 0) return false;
            EnsureAudio();
            GameObject root = null;
            try
            {
                string modeName = OrbRadiance.EffectiveEasyMode ? "EASY" : "NORMAL";
                root = new GameObject(modeName + " P2 private face beams");
                root.transform.SetParent(burst, false);
                root.SetActive(false);
                var fsms = new PlayMakerFSM[count];
                var set = new NormalBeamSet
                {
                    root = root,
                    fsms = fsms,
                    anticipated = new bool[count],
                    fired = new bool[count],
                    nailsPerWave = nailsPerWave
                };
                for (int i = 0; i < count; i++)
                {
                    GameObject clone = Instantiate(beams[0].gameObject, root.transform, false);
                    clone.name = modeName + " private Radiant Beam " + (i + 1).ToString("00");
                    clone.SetActive(true); // 根对象保持未激活，避免触发原始 OnEnable 状态和自行发射。
                    PlayMakerFSM found = null;
                    foreach (PlayMakerFSM fsm in clone.GetComponentsInChildren<PlayMakerFSM>(true))
                    {
                        if (fsm == null || fsm.Fsm == null) continue;
                        if (!HiddenRoomDoor.PreparePrivateFsm(fsm)) throw new InvalidOperationException("beam FSM detach failed");
                        fsm.Fsm.SetState("Inert");
                        if (found == null && fsm.Fsm.GetState("Inert") != null && fsm.Fsm.GetState("Fire") != null)
                            found = fsm;
                    }
                    if (found == null) throw new InvalidOperationException("private beam Control FSM missing");
                    fsms[i] = found;
                }
                root.SetActive(true);
                for (int i = 0; i < count; i++) fsms[i].Fsm.SetState("Inert");
                normalBeamSet = set;
                normalBeamSets.Add(set);
                return true;
            }
            catch (Exception e)
            {
                if (root != null) Destroy(root);
                OrbRadiance.Instance.LogWarn("[nface] N/Z 私有脸激光初始化失败：" + e.Message);
                return false;
            }
        }

        internal bool NormalBeamReady(int index)
        {
            if (normalBeamSet == null || index < 0 || index >= normalBeamSet.fsms.Length) return false;
            PlayMakerFSM fsm = normalBeamSet.fsms[index];
            return fsm != null && fsm.Fsm != null && fsm.Fsm.Started && fsm.Fsm.ActiveStateName == "Inert";
        }

        internal void AnticipateNormalBeam(int index, float anticipatedAngle)
        {
            if (normalBeamSet == null || index < 0 || index >= normalBeamSet.fsms.Length)
                throw new InvalidOperationException("private face beam is unavailable for ANTIC index " + index);
            if (normalBeamSet.anticipated[index]) return;
            PlayMakerFSM fsm = normalBeamSet.fsms[index];
            if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Started || fsm.Fsm.ActiveStateName != "Inert")
                throw new InvalidOperationException("private face beam is not started in Inert for ANTIC index " + index);
            normalBeamSet.anticipated[index] = true;
            // 在预警出现前，把每束激光沿对应剑刺的方向排布。
            fsm.transform.rotation = Quaternion.Euler(0f, 0f, anticipatedAngle);
            if (index % normalBeamSet.nailsPerWave == 0) PlayOneShot(prepareClip);
            fsm.SendEvent("ANTIC");
            if (fsm.Fsm.ActiveStateName != "Antic")
                throw new InvalidOperationException("private face beam rejected ANTIC at index " + index);
        }

        internal void FireNormalBeam(int index, float velocityAngle)
        {
            NormalBeamSet set = normalBeamSet;
            if (set == null || index < 0 || index >= set.fsms.Length)
                throw new InvalidOperationException("private face beam is unavailable for FIRE index " + index);
            if (set.fired[index]) return;
            PlayMakerFSM fsm = set.fsms[index];
            if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Started || fsm.Fsm.ActiveStateName != "Antic")
                throw new InvalidOperationException("private face beam is not started in Antic for FIRE index " + index);
            set.fired[index] = true;
            fsm.transform.rotation = Quaternion.Euler(0f, 0f, velocityAngle);
            if (index % set.nailsPerWave == 0)
            {
                PlayOneShot(burstClip);
                if (GameCameras.instance != null && GameCameras.instance.cameraShakeFSM != null)
                    GameCameras.instance.cameraShakeFSM.SendEvent("EnemyKillShake");
            }
            fsm.SendEvent("FIRE");
            if (fsm.Fsm.ActiveStateName != "Fire")
                throw new InvalidOperationException("private face beam rejected FIRE at index " + index);
            set.routines.Add(StartCoroutine(EndNormalBeamAfter(set, fsm)));
        }

        private IEnumerator EndNormalBeamAfter(NormalBeamSet set, PlayMakerFSM fsm)
        {
            yield return new WaitForSeconds(EndDelay);
            if (fsm != null && fsm.Fsm != null && fsm.Fsm.ActiveStateName == "Fire") fsm.SendEvent("END");
            set.endsSent++;
            if (set.endsSent == set.fsms.Length) set.routines.Add(StartCoroutine(DestroyNormalSetAfter(set)));
        }

        private IEnumerator DestroyNormalSetAfter(NormalBeamSet set)
        {
            yield return new WaitForSeconds(NormalCleanupDelay);
            if (normalBeamSet == set) normalBeamSet = null;
            normalBeamSets.Remove(set);
            if (set.root != null) Destroy(set.root);
        }

        internal void CompleteNormalPattern()
        {
            // 各私有激光的 END 计时彼此独立，不复用于后续齐射。
        }

        internal void CancelNormalPattern()
        {
            foreach (NormalBeamSet set in normalBeamSets.ToArray())
            {
                foreach (Coroutine routine in set.routines)
                    if (routine != null) StopCoroutine(routine);
                foreach (PlayMakerFSM fsm in set.fsms)
                {
                    if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Started) continue;
                    string state = fsm.Fsm.ActiveStateName;
                    if (state == "Antic" || state == "Fire") fsm.SendEvent("END");
                }
                if (set.root != null) Destroy(set.root);
            }
            normalBeamSets.Clear();
            normalBeamSet = null;
        }

        // ---------------- 点火 ----------------

        private void Fire(int ring, float ringStartAngle, bool cw)
        {
            if (!ResolveBurst()) return;
            Transform beam = BeamFor(ring);
            if (beam == null) return;

            // 世界 z 旋转，与脸刺钉子（SetRotation space=World, z=float Rotation）同口径
            float angle = ringStartAngle + (cw ? -SectorCenter : SectorCenter) + AimOffset;
            beam.rotation = Quaternion.Euler(0f, 0f, angle);

            EnsureAudio();
            if (OrbRadiance.EffectiveHardMode)
            {
                if (hardExtraBeamSet == null) PrepareHardExtraBeams();
                shots.Add(StartCoroutine(FireHardGroupWhenReady(beam, ring, angle)));
            }
            else
            {
                PlayOneShot(prepareClip);
                ownedNativeBeams.Add(beam);
                SendTo(beam, "ANTIC");
                shots.Add(StartCoroutine(Sequence(beam)));
            }

            OrbRadiance.Instance.Log("[faneye] P2 脸刺第 " + (ring + 1) + " 扇 → 原束 " +
                                     angle.ToString("0.#") + "°" + (OrbRadiance.EffectiveHardMode ? "；同组追加五道精确反向私有束" : "；保留单束"));
        }

        // HARD 每招独立准备 5×5 私有光束，不借用 Burst 1 其余原生对象；每扇包含原束及五条反向束。
        private void PrepareHardExtraBeams()
        {
            if (hardExtraBeamSet != null && hardExtraBeamSet.root != null) return;
            CancelHardExtraBeams();
            if (!ResolveBurst() || beams == null || beams.Length == 0) return;
            GameObject root = new GameObject("HARD P2 private extra face beams");
            root.transform.SetParent(burst, false);
            root.SetActive(false);
            var set = new HardExtraBeamSet
            {
                root = root,
                fsms = new PlayMakerFSM[5 * HardExtraOffsets.Length],
                originalFsms = new PlayMakerFSM[5],
                anticipated = new bool[5 * HardExtraOffsets.Length],
                fired = new bool[5 * HardExtraOffsets.Length]
            };
            try
            {
                for (int i = 0; i < set.fsms.Length; i++)
                {
                    GameObject clone = Instantiate(beams[0].gameObject, root.transform, false);
                    if (clone == null) throw new InvalidOperationException("private HARD beam instantiate returned null");
                    clone.name = "HARD private extra beam " + (i + 1).ToString("00");
                    clone.SetActive(true);
                    PlayMakerFSM found = null;
                    foreach (PlayMakerFSM fsm in clone.GetComponentsInChildren<PlayMakerFSM>(true))
                    {
                        if (fsm == null || fsm.Fsm == null) continue;
                        if (!HiddenRoomDoor.PreparePrivateFsm(fsm)) throw new InvalidOperationException("HARD beam FSM detach failed");
                        fsm.Fsm.SetState("Inert");
                        if (found == null && fsm.Fsm.GetState("Inert") != null && fsm.Fsm.GetState("Fire") != null)
                            found = fsm;
                    }
                    if (found == null) throw new InvalidOperationException("private HARD beam Control FSM missing");
                    set.fsms[i] = found;
                }
                root.SetActive(true);
                foreach (PlayMakerFSM fsm in set.fsms) fsm.Fsm.SetState("Inert");
                hardExtraBeamSet = set;
                OrbRadiance.Instance.Log("[faneye] HARD 每招准备25束独立副本（5扇×5反向角）；不占用原 Burst 1 池");
            }
            catch (Exception e)
            {
                Destroy(root);
                OrbRadiance.Instance.LogWarn("[faneye] HARD 私有反向光束初始化失败：" + e.Message);
            }
        }

        private IEnumerator FireHardGroupWhenReady(Transform original, int ring, float originalWorldAngle)
        {
            HardExtraBeamSet set = hardExtraBeamSet;
            if (set == null) yield break;
            int baseIndex = ring * HardExtraOffsets.Length;
            if (baseIndex < 0 || baseIndex + HardExtraOffsets.Length > set.fsms.Length) yield break;
            PlayMakerFSM originalFsm = original == null ? null : original.GetComponent<PlayMakerFSM>();
            if (originalFsm == null || originalFsm.Fsm == null || !originalFsm.Fsm.Started || originalFsm.Fsm.ActiveStateName != "Inert")
            {
                OrbRadiance.Instance.LogWarn("[faneye] HARD 原始光束未在 Inert/Started；整组池取消");
                CancelHardExtraBeams();
                yield break;
            }
            int waitedFrames = 0;
            while (set == hardExtraBeamSet && waitedFrames < 120 && !HardGroupReady(set, baseIndex))
            {
                waitedFrames++;
                yield return null;
            }
            if (set != hardExtraBeamSet) yield break;
            if (!HardGroupReady(set, baseIndex))
            {
                OrbRadiance.Instance.LogWarn("[faneye] HARD 扇 " + (ring + 1) + " 私有束在120帧内未全部Started/Inert；整组池取消");
                CancelHardExtraBeams();
                yield break;
            }
            float[] angles = new float[HardExtraOffsets.Length];
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++) angles[slot] = originalWorldAngle + HardExtraOffsets[slot];
            original.rotation = Quaternion.Euler(0f, 0f, originalWorldAngle);
            for (int slot = 0; slot < angles.Length; slot++) set.fsms[baseIndex + slot].transform.rotation = Quaternion.Euler(0f, 0f, angles[slot]);

            // H 模式下本扇面独占这一束原版光束，以及对应的五束私有光束；
            // 取消攻击时只重置这些引用，不影响 Burst 1 的其他使用者。
            set.originalFsms[ring] = originalFsm;
            ownedNativeBeams.Add(original);
            originalFsm.SendEvent("ANTIC");
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++) set.fsms[baseIndex + slot].SendEvent("ANTIC");
            bool allAntic = originalFsm.Fsm.ActiveStateName == "Antic";
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++) allAntic &= set.fsms[baseIndex + slot].Fsm.ActiveStateName == "Antic";
            if (!allAntic)
            {
                ResetFailedHardGroup(set, baseIndex, originalFsm);
                OrbRadiance.Instance.LogWarn("[faneye] HARD 扇 " + (ring + 1) + " 未能整组进入 ANTIC；已清理并跳过");
                yield return MarkHardGroupComplete(set);
                yield break;
            }
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++) set.anticipated[baseIndex + slot] = true;
            PlayOneShot(prepareClip);

            yield return new WaitForSeconds(FireDelay);
            if (set != hardExtraBeamSet) yield break;
            bool allReadyToFire = originalFsm.Fsm.Started && originalFsm.Fsm.ActiveStateName == "Antic";
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++)
            {
                PlayMakerFSM fsm = set.fsms[baseIndex + slot];
                allReadyToFire &= fsm != null && fsm.Fsm != null && fsm.Fsm.Started && fsm.Fsm.ActiveStateName == "Antic";
            }
            if (!allReadyToFire)
            {
                ResetFailedHardGroup(set, baseIndex, originalFsm);
                OrbRadiance.Instance.LogWarn("[faneye] HARD 扇 " + (ring + 1) + " 在FIRE前有束离开ANTIC；整组复位");
                yield return MarkHardGroupComplete(set);
                yield break;
            }
            originalFsm.SendEvent("FIRE");
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++) set.fsms[baseIndex + slot].SendEvent("FIRE");
            bool allFire = originalFsm.Fsm.ActiveStateName == "Fire";
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++)
            {
                int index = baseIndex + slot;
                allFire &= set.fsms[index].Fsm.ActiveStateName == "Fire";
                set.fired[index] = set.fsms[index].Fsm.ActiveStateName == "Fire";
            }
            if (!allFire)
            {
                ResetFailedHardGroup(set, baseIndex, originalFsm);
                OrbRadiance.Instance.LogWarn("[faneye] HARD 扇 " + (ring + 1) + " 有束拒绝FIRE；整组六束已复位");
                yield return MarkHardGroupComplete(set);
                yield break;
            }
            PlayOneShot(burstClip);
            if (GameCameras.instance != null && GameCameras.instance.cameraShakeFSM != null) GameCameras.instance.cameraShakeFSM.SendEvent("EnemyKillShake");

            yield return new WaitForSeconds(Mathf.Max(0f, EndDelay - FireDelay));
            if (set != hardExtraBeamSet) yield break;
            if (originalFsm.Fsm.ActiveStateName == "Fire") originalFsm.SendEvent("END");
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++)
            {
                PlayMakerFSM fsm = set.fsms[baseIndex + slot];
                if (fsm.Fsm.ActiveStateName == "Fire") fsm.SendEvent("END");
            }
            bool anyStillFiring = originalFsm.Fsm.ActiveStateName == "Fire" ||
                set.fsms.Skip(baseIndex).Take(HardExtraOffsets.Length).Any(f => f.Fsm.ActiveStateName == "Fire");
            if (anyStillFiring) OrbRadiance.Instance.LogWarn("[faneye] HARD 扇 " + (ring + 1) + " 有束未接受END；销毁本次私有副本");
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++)
                if (set.fsms[baseIndex + slot].Fsm.ActiveStateName == "Fire") set.fsms[baseIndex + slot].Fsm.SetState("Inert");
            yield return MarkHardGroupComplete(set);
        }

        private IEnumerator MarkHardGroupComplete(HardExtraBeamSet set)
        {
            set.endsSent += HardExtraOffsets.Length;
            if (set.endsSent != set.fsms.Length) yield break;
            yield return new WaitForSeconds(NormalCleanupDelay);
            if (set != hardExtraBeamSet) yield break;
            hardExtraBeamSet = null;
            if (set.root != null) Destroy(set.root);
        }

        private static bool HardGroupReady(HardExtraBeamSet set, int baseIndex)
        {
            if (set == null || set.fsms == null) return false;
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++)
            {
                PlayMakerFSM fsm = set.fsms[baseIndex + slot];
                if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Started || fsm.Fsm.ActiveStateName != "Inert") return false;
            }
            return true;
        }

        private static void ResetFailedHardGroup(HardExtraBeamSet set, int baseIndex, PlayMakerFSM original)
        {
            // 这六个 Control FSM 仅属于当前脸激光组；直接切到 Inert 可取消
            
            if (original != null && original.Fsm != null) original.Fsm.SetState("Inert");
            for (int slot = 0; slot < HardExtraOffsets.Length; slot++)
            {
                PlayMakerFSM fsm = set.fsms[baseIndex + slot];
                if (fsm == null || fsm.Fsm == null) continue;
                fsm.Fsm.SetState("Inert");
                set.anticipated[baseIndex + slot] = false;
                set.fired[baseIndex + slot] = false;
            }
        }

        private void CancelHardExtraBeams()
        {
            HardExtraBeamSet set = hardExtraBeamSet;
            hardExtraBeamSet = null;
            if (set == null) return;
            if (set.originalFsms != null)
            {
                foreach (PlayMakerFSM fsm in set.originalFsms)
                {
                    if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Started) continue;
                    fsm.Fsm.SetState("Inert");
                }
            }
            foreach (PlayMakerFSM fsm in set.fsms)
            {
                if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Started) continue;
                fsm.Fsm.SetState("Inert");
            }
            if (set.root != null) Destroy(set.root);
        }

        private IEnumerator Sequence(Transform beam)
        {
            yield return new WaitForSeconds(FireDelay);
            PlayOneShot(burstClip);
            if (GameCameras.instance != null && GameCameras.instance.cameraShakeFSM != null)
                GameCameras.instance.cameraShakeFSM.SendEvent("EnemyKillShake");
            SendTo(beam, "FIRE");

            yield return new WaitForSeconds(Mathf.Max(0f, EndDelay - FireDelay));
            SendTo(beam, "END");
        }

        // 只驱动**这一道** beam 自己的 FSM（该物体上没有子 FSM），不广播
        private void SendTo(Transform beam, string evt)
        {
            if (beam == null) return;
            foreach (PlayMakerFSM f in beam.GetComponents<PlayMakerFSM>())
                if (f != null && f.Fsm != null) f.SendEvent(evt);
        }

        private Transform BeamFor(int ring)
        {
            if (beams == null || beams.Length == 0) return null;
            return beams[ring % beams.Length];
        }

        // ---------------- 收尾 / 复位 ----------------

        private void StopShots()
        {
            foreach (Coroutine c in shots)
                if (c != null) StopCoroutine(c);
            shots.Clear();
            CancelHardExtraBeams();
        }

        private void Disarm()
        {
            StopShots();
            CancelNormalPattern();
            ResetBursts();
            firedFirstSector = false;
        }

        private void OnDisable()
        {
            Disarm();
        }

        private void OnDestroy()
        {
            StopShots();
            CancelNormalPattern();
        }

        // 补发 FIRE+END：Antic→Fire→End→Inert，避免停在蓄力帧（同 RageEyeBeam）
        private void ResetBursts()
        {
            // 从未执行过本 Mod 的射击，就不得探测/结束 Burst 1 的任何原生光束。
            // 原实现每帧在 Off 状态调用此方法，并向其它 Mod 的 Antic/Fire 光束发送 END。
            foreach (Transform beam in ownedNativeBeams)
            {
                if (beam == null) continue;
                foreach (PlayMakerFSM f in beam.GetComponents<PlayMakerFSM>())
                {
                    if (f == null || f.Fsm == null) continue;
                    if (!BurstLiveStates.Contains(f.Fsm.ActiveStateName)) continue;
                    f.SendEvent("FIRE");
                    f.SendEvent("END");
                }
            }
            ownedNativeBeams.Clear();
        }

        // ---------------- 音效 ----------------

        private void EnsureAudio()
        {
            if (audio != null) return;

            GameObject go = new GameObject("FanEyeBeam Audio");
            go.transform.SetParent(transform, false);
            audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f;    // 全局 2D，与原版 Audio Player Actor 2D 一致

            // clip 从 EB 1 的动作里取（同 AnyRadiance）
            FsmState eb1 = commands.Fsm.GetState("EB 1");
            if (eb1 == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 EB 1，脸刺眼激光无声");
                return;
            }
            AudioPlaySimple prep = eb1.Actions.OfType<AudioPlaySimple>().FirstOrDefault();
            if (prep != null && prep.oneShotClip != null) prepareClip = prep.oneShotClip.Value as AudioClip;
            AudioPlayerOneShotSingle shot = eb1.Actions.OfType<AudioPlayerOneShotSingle>().FirstOrDefault();
            if (shot != null && shot.audioClip != null) burstClip = shot.audioClip.Value as AudioClip;
        }

        private void PlayOneShot(AudioClip clip)
        {
            if (clip != null && audio != null) audio.PlayOneShot(clip);
        }

        // ---------------- 找对象 ----------------

        private float Rotation()
        {
            FsmFloat r = commands.Fsm.Variables.GetFsmFloat("Rotation");
            return r != null ? r.Value : 0f;
        }

        private bool ResolveBurst()
        {
            if (burst != null && beams != null && beams.Length > 0) return true;

            Transform glow = transform.Find(GlowName);
            if (glow == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + GlowName + "，脸刺眼激光放不出");
                return false;
            }
            burst = glow.Find(BurstName);
            if (burst == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + GlowName + "/" + BurstName +
                                             "，脸刺眼激光放不出");
                return false;
            }

            List<Transform> list = new List<Transform>();
            for (int i = 0; i < burst.childCount; i++)
            {
                Transform c = burst.GetChild(i);
                if (c != null && c.name.StartsWith(BeamPrefix)) list.Add(c);
            }
            if (list.Count == 0)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + BurstName + " 下找不到 " + BeamPrefix +
                                             "，脸刺眼激光放不出");
                return false;
            }
            beams = list.ToArray();
            return true;
        }

        private bool EnsureCommands()
        {
            if (commands != null && commands.Fsm != null) return true;

            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Attack Commands") continue;
                commands = f;
                break;
            }

            if (commands == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands FSM，脸刺眼激光放不出");
            return commands != null;
        }

        // ---------------- 重打复位 ----------------

        private string ArenaState()
        {
            if (!arenaTried)
            {
                arenaTried = true;
                Transform parent = transform.parent;
                if (parent != null)
                    foreach (PlayMakerFSM f in parent.GetComponents<PlayMakerFSM>())
                        if (f != null && f.Fsm != null && f.FsmName == "Control") { arenaFsm = f; break; }

                if (arenaFsm == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + ArenaFsmOwner +
                                                 " 的 Control FSM，重打时脸刺眼激光不会自动收");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

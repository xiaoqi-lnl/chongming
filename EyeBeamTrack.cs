using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P1/P2 正式眼激光招式改为连续追踪射击，不影响 RageEyeBeam 和 FanEyeBeam。
    // P4 的额外间歇眼激光仅在 HARD 生效；保存原版 FSM 参数以便恢复。


    internal class EyeBeamTrack : MonoBehaviour
    {
        // ---------------- 时间点（状态名）----------------

        private static readonly string[] ResetStates =
            { "Battle Start", "Refight", "Init", "Setup" };
        private const string ArenaFsmOwner = "Boss Control";


        private const string TriggerState = "Eye Beam";       // 正式眼激光招式（发 EYE BEAMS 后卡在这里）
        private const string FinalPauseState = "FinalOrb Pause"; // P4 每颗光球之间的暂停状态，等待值由 P4OrbInterval 按难度调整。
        private const string EyeBeamsEvent = "EYE BEAMS";
        private const string BypassEvent = "EYE BEAMS BYPASS"; // Attack Commands 不认识 → 原版链不启动
        private const string AttackEndEvent = "ATTACK END";

        // ---------------- 眼激光 ----------------

        private const string GlowName = "Eye Beam Glow";
        private const string BurstName = "Burst 2";
        private const string BeamPrefix = "Radiant Beam";
        private const string BeamStartState = "Init";     // 起始态：收到 ANTIC 会丢
        private static readonly string[] BurstLiveStates = { "Antic", "Fire" };
//用户改了激光的数值，没命令严禁改
        private const float FirstAimTime = 0.45f; // 第 1 发预瞄时长
        private const float AimTime = 0.6f;       // 第 2/3 发预瞄时长
        private const float FireTime = 0.2f;      // 每发激光时长
        private const int Shots = 3;              // 一次招式几连发
        private const float GlowFadeTime = 0.3f;  // END 之后等光束淡出（同原版 EB Glow End 的 0.3）
        private const float StartTimeout = 1f;    // 等光束子 FSM 起步的上限

        // 光束视觉沿本地 +X 伸出 → 世界角 = 目标方向 + 0（碰撞盒 offset 0.883 在 +X 侧可证）
        private const float BeamAimOffset = 0f;

        // 第 2/3 发预瞄「朝小骑士转动」的逼近速率（1/s）：角度差每秒收敛到 e⁻λ。
        // λ 越大追得越紧（越像第 1 发的瞬时锁定）；λ 越小转得越慢、越看得出「转动」。
        //用户自己设定的数值，没命令别改
        private const float TurnLambda = 4f;

        // ---------------- 运行时 ----------------

        private PlayMakerFSM control;     
        private PlayMakerFSM choices;     // Absolute Radiance/Attack Choices：招式触发 + ATTACK END
        private PlayMakerFSM commands;    // Absolute Radiance/Attack Commands：只为取音效

        private PlayMakerFSM arenaFsm;    // Boss Control/Control：重打复位
        private bool arenaTried;
        private string lastArenaState;

        private GameObject glow;          
        private Transform beam;           // 我们这一道 Radiant Beam
        private PlayMakerFSM beamFsm;     // 它的 Control FSM

        private AudioSource audio;
        private AudioClip prepareClip;
        private AudioClip burstClip;
        private bool audioBuilt;

        private Coroutine running;
        private bool aiming;              // 预瞄中（Antic 蓄力）：每帧对准小骑士；Fire 期间 false → 方向冻结
        private bool smoothAim;           // 本次预瞄是「转动逼近」（第 2/3 发）还是「瞬时锁定」（第 1 发）
        private bool disarmed = true;
        private bool lastInBeam;
        private bool lastInFinalPause;    // P4 光球环边沿检测
        private bool commandsTried;       // Attack Commands 只找一次（找不到不刷警告）

        private static EyeBeamTrack active;

        internal void Init(PlayMakerFSM f)
        {
            control = f;
            active = this;
        }

        // 总开关关掉时 OrbRadiance.RestorePatched 走这里收尾
        internal static void Restore()
        {
            if (active != null) active.Disarm();
        }

        // ---------------- 改 Attack Choices 的发事件名（只原位改字段值）----------------

        internal static void PatchEyeBeamEvent(Fsm fsm)
        {
            FsmState st = fsm.GetState(TriggerState);
            if (st == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Choices 里没有 " + TriggerState +
                                             " 状态，眼激光追踪未接管");
                return;
            }

            SendEventByName send = st.Actions.OfType<SendEventByName>()
                .FirstOrDefault(a => a != null && a.sendEvent != null &&
                                     a.sendEvent.Value == EyeBeamsEvent);
            if (send == null) send = st.Actions.OfType<SendEventByName>().FirstOrDefault();
            if (send == null || send.sendEvent == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + TriggerState +
                                             " 里没有带 sendEvent 的 SendEventByName");
                return;
            }

            if (!OrbSpawner.OriginalSendEvent.ContainsKey(send))
                OrbSpawner.OriginalSendEvent[send] = send.sendEvent.Value;
            send.sendEvent.Value = BypassEvent;
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // 总开关关掉：收尾（眼激光放不出，Attack Choices 恢复原版链）
            if (!OrbRadiance.Settings.Enabled)
            {
                Disarm();
                lastArenaState = null;
                return;
            }

            // 重打复位：死亡重来是同场景重来，Control 不会自己回 Init
            string arena = ArenaState();
            if (arena != null && arena != lastArenaState)
            {
                lastArenaState = arena;
                if (ResetStates.Contains(arena)) Disarm();
            }

            // 触发一：P1/P2 正式眼激光招式（Attack Choices 卡在 `Eye Beam`）
            bool inBeam = EnsureChoices() && choices.Fsm.ActiveStateName == TriggerState;
            // 触发二：P4 光球环——每颗光球之间的 `FinalOrb Pause` 空档插一发
            bool inFinalPause = FinalPauseActive();

            if (running == null)
            {
                if (inBeam && !lastInBeam) Begin();
                else if (inFinalPause && !lastInFinalPause) Begin();
            }
            lastInBeam = inBeam;
            lastInFinalPause = inFinalPause;

            if (aiming) Aim(smoothAim);
        }

        // ---------------- 一整道眼激光 ----------------

        private void Begin()
        {
            if (!ResolveGlow()) return;
            if (!EnsureCommands()) return;
            disarmed = false;
            running = StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            glow.SetActive(true);
            ResetBeam();
            EnsureAudio();

            // 等光束子 FSM 起步并离开 Init（同帧发的事件会被 Init 吃掉）
            float deadline = Time.time + StartTimeout;
            while (Time.time < deadline)
            {
                if (beamFsm != null && beamFsm.Fsm != null && beamFsm.Fsm.Started &&
                    beamFsm.Fsm.ActiveStateName != BeamStartState)
                    break;
                yield return null;
            }

            OrbRadiance.Instance.Log("[eyebeam] 眼激光三连发起手：第 1 发预瞄 " + FirstAimTime +
                                     "s，之后预瞄 " + AimTime + "s，每发激光 " + FireTime + "s");

            for (int i = 0; i < Shots; i++)
            {
                // 预瞄：第 1 发瞬时锁定；第 2/3 发从上一发冻结角出发，朝小骑士按比例逼近
                smoothAim = i > 0;
                PlayOneShot(prepareClip);
                aiming = true;
                Aim(smoothAim);
                SendTo(beam, "ANTIC");
                yield return new WaitForSeconds(i == 0 ? FirstAimTime : AimTime);

                // 发射：方向冻结在最后预瞄到的角度，发射期间不再追踪
                Aim(smoothAim);
                aiming = false;
                PlayOneShot(burstClip);
                Shake();
                SendTo(beam, "FIRE");
                yield return new WaitForSeconds(FireTime);

                SendTo(beam, "END");
                // 让 END 落地：下一发的 ANTIC 在下一帧才发，免得同帧抢状态
                yield return null;
            }

            // 收束：三发都打完才放 boss 走
            aiming = false;
            SendDownToGlow();
            yield return new WaitForSeconds(GlowFadeTime);
            if (glow != null) glow.SetActive(false);

            if (choices != null && choices.Fsm != null && choices.Fsm.ActiveStateName == TriggerState)
                choices.SendEvent(AttackEndEvent);

            OrbRadiance.Instance.Log("[eyebeam] 眼激光收束（三连发完毕），已放行 Attack Choices");
            running = null;
        }

        // 对准小骑士：目标角 = atan2(骑士 - 眼睛) + BeamAimOffset。
        //   smooth=false（第 1 发）：瞬时锁定到目标角。
        //   smooth=true （第 2/3 发）：从**当前角**出发按比例朝目标角逼近（差越大转越多）。
        private void Aim(bool smooth)
        {
            if (beam == null) return;
            HeroController hc = HeroController.instance;
            if (hc == null) return;

            Vector2 origin = beam.position;
            Vector2 d = (Vector2)hc.transform.position - origin;
            if (d.sqrMagnitude < 0.0001f) return;

            float target = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

            float z;
            if (smooth)
            {
                float alpha = 1f - Mathf.Exp(-TurnLambda * Time.deltaTime);
                z = Mathf.LerpAngle(beam.eulerAngles.z - BeamAimOffset, target, alpha);
            }
            else
            {
                z = target;
            }

            beam.rotation = Quaternion.Euler(0f, 0f, z + BeamAimOffset);
        }

        // ---------------- 收尾 / 复位 ----------------

        // 只驱动**这一道** beam 自己的 FSM（不广播）
        private void SendTo(Transform b, string evt)
        {
            if (b == null) return;
            foreach (PlayMakerFSM f in b.GetComponents<PlayMakerFSM>())
                if (f != null && f.Fsm != null) f.SendEvent(evt);
        }

        // 补发 FIRE+END：Antic→Fire→End，避免停在蓄力/开火帧（同 §18、RageEyeBeam）
        private void ResetBeam()
        {
            if (beamFsm == null || beamFsm.Fsm == null || !beamFsm.Fsm.Started) return;
            if (!BurstLiveStates.Contains(beamFsm.Fsm.ActiveStateName)) return;
            beamFsm.SendEvent("FIRE");
            beamFsm.SendEvent("END");
        }

        // 同原版 `EB Glow End`：给 Eye Beam Glow 子树发 DOWN 收眼晕
        private void SendDownToGlow()
        {
            if (glow == null) return;
            foreach (PlayMakerFSM f in glow.GetComponentsInChildren<PlayMakerFSM>(true))
                if (f != null && f.Fsm != null) f.SendEvent("DOWN");
        }

        private void Disarm()
        {
            if (disarmed) return;
            disarmed = true;

            if (running != null) { StopCoroutine(running); running = null; }
            aiming = false;

            ResetBeam();
            if (glow != null) glow.SetActive(false);

            if (choices != null && choices.Fsm != null && choices.Fsm.ActiveStateName == TriggerState)
                choices.SendEvent(AttackEndEvent);

            lastInBeam = false;
            lastInFinalPause = false;
        }

        // ---------------- 音效 / 震屏 ----------------

        private void EnsureAudio()
        {
            if (audioBuilt) return;
            audioBuilt = true;

            GameObject go = new GameObject("EyeBeamTrack Audio");
            go.transform.SetParent(transform, false);
            audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f;    // 全局 2D，与原版 Audio Player Actor 2D 一致

            FsmState eb1 = commands.Fsm.GetState("EB 1");
            if (eb1 == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 EB 1，眼激光无声");
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

        // P1 环绕剑环附带的竖向光束沿用原版眼激光链 EB 1 的预警与发射音效。
        internal static void PlayP1ColumnSound(bool firing)
        {
            EyeBeamTrack track = active;
            if (track == null || !track.EnsureCommands()) return;
            track.EnsureAudio();
            track.PlayOneShot(firing ? track.burstClip : track.prepareClip);
        }

        private void Shake()
        {
            if (GameCameras.instance != null && GameCameras.instance.cameraShakeFSM != null)
                GameCameras.instance.cameraShakeFSM.SendEvent("EnemyKillShake");
        }

        // ---------------- 找对象 ----------------

        private bool ResolveGlow()
        {
            if (glow != null && beam != null && beamFsm != null) return true;

            Transform g = transform.Find(GlowName);
            if (g == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + GlowName + "，眼激光放不出");
                return false;
            }
            glow = g.gameObject;

            Transform burst = g.Find(BurstName);
            if (burst == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + GlowName + "/" + BurstName +
                                             "，眼激光放不出");
                return false;
            }

            beam = burst.Find(BeamPrefix);
            if (beam == null)
                for (int i = 0; i < burst.childCount; i++)
                {
                    Transform c = burst.GetChild(i);
                    if (c != null && c.name.StartsWith(BeamPrefix)) { beam = c; break; }
                }
            if (beam == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + BurstName + " 下找不到 " + BeamPrefix +
                                             "，眼激光放不出");
                return false;
            }

            beamFsm = beam.GetComponents<PlayMakerFSM>()
                .FirstOrDefault(f => f != null && f.Fsm != null && f.FsmName == "Control");
            if (beamFsm == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + beam.name +
                                             " 上没有 Control FSM，眼激光放不出");
                return false;
            }
            return true;
        }

        private bool EnsureChoices()
        {
            if (choices != null && choices.Fsm != null) return true;

            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Attack Choices") continue;
                choices = f;
                break;
            }

            if (choices == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Choices FSM，眼激光追踪放不出");
            return choices != null;
        }

        private bool EnsureCommands()
        {
            if (commands != null && commands.Fsm != null) return true;
            if (commandsTried) return false;
            commandsTried = true;

            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || f.FsmName != "Attack Commands") continue;
                commands = f;
                break;
            }

            if (commands == null)
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands FSM，眼激光无声");
            return commands != null;
        }

        // P4：Attack Commands 停在 `FinalOrb Pause`（每颗光球之间的空档）时为 true
        private bool FinalPauseActive()
        {
            // P4 在暂停阶段附加的眼激光仍仅限 HARD。
            if (!OrbRadiance.EffectiveHardMode) return false;
            if (!EnsureCommands()) return false;
            return commands.Fsm.ActiveStateName == FinalPauseState;
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
                                                 " 的 Control FSM，重打时眼激光不会自动收");
            }

            if (arenaFsm == null || arenaFsm.Fsm == null) return null;
            return arenaFsm.Fsm.ActiveStateName;
        }
    }
}

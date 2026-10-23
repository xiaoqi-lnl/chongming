using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P1/P2 光球改为后台并行发射，不阻塞 Boss 使用其他招式。
    // 仅修改光球生成与存活流程，不接管 P4 原版最终光球循环。


    internal class OrbSpawner : MonoBehaviour
    {
        // Attack Commands 的事件表里没有这个名字 → 它收不到、不响应
        internal const string OrbBypassEvent = "ORBS BYPASS";

        private const string FireEvent = "FIRE";

        // 原版一颗球的节奏：Orb Summon(0.75 蓄力) + Orb Pause(0.25 间隔)
        // 前摇压到 0.4、间隔压到 0.01（2026-09-19 用户定；对标天国余晖 —— 天国 P2 是 0.1/0.1、P1 是 0.05/0.05）。
        private const float ChargeTime = 0.4f;
        private const float GapTime = 0.01f;

        // H 一次球招的球数：随机 6~8（2026-09-19 用户定）。
        // 原版是 Attack Commands 的 RandomInt(min 3, max 5, inclusiveMax=false) → 3~4。
        private const int OrbCountMin = 6;
        private const int OrbCountMax = 8;

        // 原版 Orb Pos 的约束：与 boss 距离落在 3~11 才用，否则重选
        private const float MinDistFromSelf = 3f;
        private const float MaxDistFromSelf = 11f;
        private const int MaxPosTries = 20;

        private PlayMakerFSM choices;

        // prefab 延迟解析：Attack Choices / Attack Commands 挂在同一个物体上，
        // 两者 OnEnable 的先后没有保证，所以等真要放球时再去读。
        private GameObject prefab;
        private bool prefabTried;

        // 蓄力特效物体，同样延迟解析
        private GameObject[] fx;
        private bool fxTried;

        // 音效：直接持有原版那两个音频动作，播放时照着它的字段取值（音效引用延迟解析）
        private AudioPlayerOneShotSingle chargeAudio;   // Orb Summon 的蓄力音
        private AudioPlaySimple shootAudio;             // Spawn Fireball 的出球音
        private AudioSource shootSource;
        private bool audioTried;

        private string lastState;
        private int remaining;
        private int total;              // 本次球招的球数（进 Orbs 时按 N/Z/H 模式确定）

        // 一次球招的节奏：蓄力 → 出球 → 间隔 → 下一颗蓄力 …
        private bool charging;
        private float chargeEndsAt;
        private float nextChargeAt;
        private Vector3 pendingPos;     // 正在蓄力的那颗球的目标点

        internal void Init(PlayMakerFSM choicesFsm)
        {
            choices = choicesFsm;
        }

        private void Update()
        {
            if (choices == null || choices.Fsm == null) return;

            if (!OrbRadiance.Settings.Enabled)
            {
                remaining = 0;      // 关掉时停手，别再往场上放球
                charging = false;
                lastState = choices.Fsm.ActiveStateName;
                return;
            }

            string cur = choices.Fsm.ActiveStateName;
            bool changed = cur != lastState;
            lastState = cur;

            if (changed && cur == "Orbs")
            {
                P2OrbPlatforms.TrySpawnAtOrbAttack(gameObject);
                string mode;
                string countPolicy;
                if (OrbRadiance.EffectiveHardMode)
                {
                    mode = "H";
                    countPolicy = "随机6~8";
                    // Random.Range(int, int) 上限不含，所以 +1 才是闭区间 6~8。
                    total = UnityEngine.Random.Range(OrbCountMin, OrbCountMax + 1);
                }
                else if (OrbRadiance.EffectiveEasyMode)
                {
                    mode = "Z";
                    countPolicy = "固定3";
                    total = 3;
                }
                else
                {
                    mode = "N";
                    countPolicy = "固定5";
                    total = 5;
                }
                remaining = total;
                charging = false;
                nextChargeAt = Time.time;   // 下一帧就在目标点亮出蓄力特效
                OrbRadiance.Instance.Log("[orbspawn] 进 Orbs → 模式 " + mode + "（" + countPolicy +
                                         "）开始放 " + total +
                                         " 颗球（每颗蓄力 " + ChargeTime + "s、间隔 " + GapTime +
                                         "s；预计完全消失约 " + (OrbRadiance.ChaseHeroWait +
                                         OrbRadiance.EffectiveOrbDissipateWait + 0.8f).ToString("0.##") + "s）");
                return;
            }

            if (remaining <= 0) return;

            if (charging)
            {
                if (Time.time < chargeEndsAt) return;
                Release();
                return;
            }

            if (Time.time < nextChargeAt) return;
            StartCharge();
        }

        // 蓄力：把 Shot Charge / Shot Charge 2 摆到目标点并打开粒子 ——
        // 这就是原版 Orb Summon 那 0.75s 的预警，玩家靠它反应
        private void StartCharge()
        {
            charging = true;
            chargeEndsAt = Time.time + ChargeTime;
            pendingPos = NextPosition();
            SetChargeFx(pendingPos, true);
            PlayChargeSound(pendingPos);
        }

        // 蓄力结束：先关特效、再出球。第一颗出来的这一刻才把 boss 交给 Attack Choices，
        // 所以蓄力期间 boss 还保持着施法姿势，之后第 2、3 颗球才和下一招同时在场。
        private void Release()
        {
            charging = false;
            remaining--;
            nextChargeAt = Time.time + GapTime;

            // 顺序照原版 Spawn Fireball：先出球 + 出球音，再把蓄力特效/粒子关掉
            SpawnOne(pendingPos);
            PlayShootSound();
            SetChargeFx(pendingPos, false);

            if (remaining == total - 1)
            {
                choices.SendEvent("ATTACK END");
                OrbRadiance.Instance.Log("[orbspawn] 第一颗球已出 → 发 ATTACK END，boss 开始接下一招");
            }
        }

        private void SpawnOne(Vector3 pos)
        {
            GameObject p = Prefab();
            if (p == null)
            {
                OrbRadiance.Instance.LogWarn(
                    "OrbRadiance: 拿不到 Radiant Orb prefab，本次球招只让 boss 接了下一招、没有球");
                remaining = 0;
                return;
            }

            GameObject orb = null;
            try
            {
                orb = ObjectPool.Spawn(p, pos, Quaternion.identity);
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: Spawn 光球抛异常 " + e);
            }

            if (orb == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: ObjectPool.Spawn 返回 null，这颗球没出来");
                return;
            }

            try
            {
                // 对应原版 Spawn Fireball 的 SetIsKinematic2d（物化后才受物理影响）
                Rigidbody2D rb = orb.GetComponent<Rigidbody2D>();
                if (rb != null) rb.isKinematic = false;
                // 对应原版 Spawn Fireball 里发给 Projectile 的 "FIRE"
                FSMUtility.SendEventToGameObject(orb, FireEvent, false);
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: 初始化光球抛异常 " + e);
            }

            OrbRadiance.Instance.Log("[orbspawn] 放出光球 pos=(" + pos.x.ToString("F2") + "," +
                                     pos.y.ToString("F2") + ") rbKinematic=" +
                                     (orb.GetComponent<Rigidbody2D>() == null ? "(无Rigidbody2D)" :
                                      orb.GetComponent<Rigidbody2D>().isKinematic.ToString()) +
                                     " 剩余=" + remaining);
        }

        // ---- 音效：从原版的音频动作上取引用 ----
        
        // 蓄力音（Orb Summon 的 AudioPlayerOneShotSingle）：Spawn 一个 Audio Player 预制体到蓄力点上，
        //   再 PlayOneShot dream_ghost_appear。这里复刻它的取法，连 pitch/volume 都读原动作的字段。
        // 出球音（Spawn Fireball 的 AudioPlaySimple）：在 FSM Owner（就是本物体）的 AudioSource 上
        

        private void ResolveAudio()
        {
            if (audioTried) return;
            audioTried = true;

            PlayMakerFSM commands = Commands();
            if (commands == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands，光球没有音效");
                return;
            }

            FsmState summon = commands.Fsm.GetState("Orb Summon");
            if (summon != null)
            {
                chargeAudio = summon.Actions.OfType<AudioPlayerOneShotSingle>().FirstOrDefault();
                if (chargeAudio == null)
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Orb Summon 里没有 AudioPlayerOneShotSingle，蓄力音效缺失");
            }
            else
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 Orb Summon 状态，蓄力音效缺失");
            }

            FsmState spawn = commands.Fsm.GetState("Spawn Fireball");
            if (spawn != null)
            {
                shootAudio = spawn.Actions.OfType<AudioPlaySimple>().FirstOrDefault();
                if (shootAudio == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Spawn Fireball 里没有 AudioPlaySimple，出球音效缺失");
                }
                else
                {
                    // 原动作打在 FSM Owner（Attack Commands 挂在本物体上）身上
                    shootSource = GetComponent<AudioSource>();
                    if (shootSource == null)
                        OrbRadiance.Instance.LogWarn("OrbRadiance: 本物体上没有 AudioSource，出球音效缺失");
                }
            }
            else
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 Spawn Fireball 状态，出球音效缺失");
            }

            OrbRadiance.Instance.Log("[orbspawn] 音效引用 蓄力=" + (chargeAudio == null ? "无" : "有") +
                                     " 出球=" + (shootAudio == null || shootSource == null ? "无" : "有"));
        }

        private void PlayChargeSound(Vector3 pos)
        {
            ResolveAudio();
            if (chargeAudio == null) return;

            try
            {
                GameObject prefabPlayer = chargeAudio.audioPlayer == null ? null : chargeAudio.audioPlayer.Value;
                AudioClip clip = chargeAudio.audioClip == null ? null : chargeAudio.audioClip.Value as AudioClip;
                if (prefabPlayer == null || clip == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 蓄力音效的 Audio Player 或 clip 没解析出来，这颗球没声音");
                    return;
                }

                GameObject player = ObjectPool.Spawn(prefabPlayer, pos, Quaternion.Euler(Vector3.up));
                if (player == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 蓄力音效 Spawn Audio Player 返回 null");
                    return;
                }

                AudioSource src = player.GetComponent<AudioSource>();
                if (src == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Audio Player 上没有 AudioSource，蓄力音效没出来");
                    return;
                }

                src.pitch = UnityEngine.Random.Range(chargeAudio.pitchMin.Value, chargeAudio.pitchMax.Value);
                src.volume = chargeAudio.volume.Value;
                src.PlayOneShot(clip);
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: 播放蓄力音效抛异常 " + e);
            }
        }

        private void PlayShootSound()
        {
            ResolveAudio();
            if (shootAudio == null || shootSource == null) return;

            try
            {
                AudioClip clip = shootAudio.oneShotClip == null ? null : shootAudio.oneShotClip.Value as AudioClip;
                if (clip == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: 出球音效的 clip 没解析出来，出球没声音");
                    return;
                }

                if (shootAudio.volume == null || shootAudio.volume.IsNone) shootSource.PlayOneShot(clip);
                else shootSource.PlayOneShot(clip, shootAudio.volume.Value);
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogError("OrbRadiance: 播放出球音效抛异常 " + e);
            }
        }

        // ---- 共用：取挂在一起的那个 Attack Commands ----

        private PlayMakerFSM Commands()
        {
            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
                if (f.Fsm != null && f.FsmName == "Attack Commands") return f;
            return null;
        }

        // ---- 蓄力特效：从 Attack Commands 的 Orb Summon 里读 ----
        
        // 原版 Orb Summon 的动作是：把 Shot Charge / Shot Charge 2 摆到 Vector3 Fireball Pos
        // → 等 0.75s → 出球（Spawn Fireball 里再把 emission 关掉）。
        // 那两个物体就是预警特效本体，靠 FsmOwnerDefault 引用，所以这里用
        // Fsm.GetOwnerDefaultTarget 解析 —— 跟原版 SetParticleEmission 取目标的方式一致。

        private void SetChargeFx(Vector3 pos, bool on)
        {
            GameObject[] fxs = ChargeFx();
            if (fxs == null) return;

            foreach (GameObject go in fxs)
            {
                if (go == null) continue;   // 战斗结束/物体被销毁时跳过
                try
                {
                    go.transform.position = pos;
                    ParticleSystem ps = go.GetComponent<ParticleSystem>();
                    if (ps == null)
                    {
                        OrbRadiance.Instance.LogWarn("OrbRadiance: 蓄力物体 " + go.name + " 上没有 ParticleSystem");
                        continue;
                    }
                    ParticleSystem.EmissionModule em = ps.emission;
                    em.enabled = on;
                }
                catch (Exception e)
                {
                    OrbRadiance.Instance.LogError("OrbRadiance: 操作蓄力特效抛异常 " + e);
                }
            }
        }

        private GameObject[] ChargeFx()
        {
            if (fxTried) return fx;
            fxTried = true;

            PlayMakerFSM commands = Commands();
            if (commands == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 Attack Commands，光球没有预警特效");
                return null;
            }

            FsmState summon = commands.Fsm.GetState("Orb Summon");
            if (summon == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 Orb Summon 状态，光球没有预警特效");
                return null;
            }

            List<GameObject> found = new List<GameObject>();
            foreach (SetParticleEmission act in summon.Actions.OfType<SetParticleEmission>())
            {
                if (act.gameObject == null) continue;
                GameObject go = commands.Fsm.GetOwnerDefaultTarget(act.gameObject);
                if (go == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Orb Summon 里的蓄力物体引用没解析出来");
                    continue;
                }
                if (!found.Contains(go)) found.Add(go);
            }

            if (found.Count == 0)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Orb Summon 里没找到蓄力物体，光球没有预警特效");
                return null;
            }

            fx = found.ToArray();
            OrbRadiance.Instance.Log("[orbspawn] 蓄力特效物体 = " + string.Join(", ", fx.Select(g => g.name).ToArray()));
            return fx;
        }

        // ---- prefab：从 Attack Commands 的 Spawn Fireball 里读 ----

        private GameObject Prefab()
        {
            if (prefabTried) return prefab;
            prefabTried = true;

            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f.Fsm == null || f.FsmName != "Attack Commands") continue;
                FsmState st = f.Fsm.GetState("Spawn Fireball");
                if (st == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 里没有 Spawn Fireball 状态");
                    continue;
                }
                SpawnObjectFromGlobalPool act =
                    st.Actions.OfType<SpawnObjectFromGlobalPool>().FirstOrDefault();
                if (act == null || act.gameObject == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Spawn Fireball 里没有 SpawnObjectFromGlobalPool");
                    continue;
                }
                prefab = act.gameObject.Value;
                OrbRadiance.Instance.Log("[orbspawn] Spawn Fireball 的 prefab 引用 = " +
                                         (prefab == null ? "(null！Global 变量没解析出来)" : prefab.name) +
                                         "  action.gameObject.IsNone=" + act.gameObject.IsNone);
                break;
            }
            return prefab;
        }

        // ---- 位置：复刻原版 Orb Pos（在 FSM 变量给的矩形里随机 + 与 boss 距离 3~11）----

        private Vector3 NextPosition()
        {
            float minX = Var("Orb Min X", 50f);
            float maxX = Var("Orb Max X", 72f);
            float minY = Var("Orb Min Y", 22f);
            float maxY = Var("Orb Max Y", 34f);
            Vector3 self = transform.position;

            for (int i = 0; i < MaxPosTries; i++)
            {
                Vector3 p = new Vector3(UnityEngine.Random.Range(minX, maxX),
                                        UnityEngine.Random.Range(minY, maxY), -0.1f);
                float d = Vector2.Distance(p, self);
                if (d < MinDistFromSelf || d > MaxDistFromSelf) continue;
                return p;
            }
            // 20 次都没落进 3~11：退回矩形中点，别让球放不出来
            OrbRadiance.Instance.LogWarn("OrbRadiance: 光球位置 20 次都没落进 3~11，改用矩形中点");
            return new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, -0.1f);
        }

        private float Var(string name, float fallback)
        {
            foreach (PlayMakerFSM f in GetComponents<PlayMakerFSM>())
            {
                if (f.Fsm == null || f.FsmName != "Attack Commands") continue;
                FsmFloat v = f.Fsm.GetFsmFloat(name);
                if (v != null) return v.Value;
            }
            OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Commands 缺变量 " + name + "，用兜底值 " + fallback);
            return fallback;
        }

        // ---- 改 Attack Choices 的 Orbs 发的事件名（只原位改字段值）----

        internal static void PatchBypass(Fsm fsm)
        {
            FsmState orbs = fsm.GetState("Orbs");
            if (orbs == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Attack Choices 里没有 Orbs 状态");
                return;
            }
            SendEventByName send = orbs.Actions.OfType<SendEventByName>().FirstOrDefault();
            if (send == null || send.sendEvent == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Orbs 里没有带 sendEvent 的 SendEventByName");
                return;
            }
            if (!OriginalSendEvent.ContainsKey(send)) OriginalSendEvent[send] = send.sendEvent.Value;
            send.sendEvent.Value = OrbBypassEvent;
        }

        // 关开关时写回（和 OrbRadiance.OriginalWait 同一套思路：记一次改前的值，不硬编）
        internal static readonly System.Collections.Generic.Dictionary<SendEventByName, string> OriginalSendEvent =
            new System.Collections.Generic.Dictionary<SendEventByName, string>();
    }
}

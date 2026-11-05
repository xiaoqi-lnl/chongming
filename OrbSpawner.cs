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
        // Z/N 专用：光球在实际出现的瞬间，距玩家至少 3 个 XY 世界单位。
        private const float MinDistFromHero = 3f;
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

        private sealed class OrbEntry
        {
            internal PlayMakerFSM Control;
            internal OrbSpawnMarker Marker;
            internal int Serial;
        }

        private sealed class OrbGroup
        {
            internal string Name;
            internal float FirstSpawnTime;
            internal float Deadline;
            internal bool HasFirst;
            internal readonly List<OrbEntry> Members = new List<OrbEntry>();
        }

        // P1/P2 按球招分组；P4 是原版连续出球，按首颗存活窗口分组。
        private readonly List<OrbGroup> groups = new List<OrbGroup>();
        private OrbGroup currentAttackGroup;
        private OrbGroup currentFinalGroup;
        private int spawnSerial;

        internal void Init(PlayMakerFSM choicesFsm)
        {
            choices = choicesFsm;
        }

        private void Update()
        {
            if (!OrbRadiance.Settings.Enabled)
            {
                remaining = 0;      // 关掉时停手，别再往场上放球
                charging = false;
                groups.Clear();     // 关闭 MOD 时不再操纵已生成的光球
                currentAttackGroup = null;
                currentFinalGroup = null;
                if (choices != null && choices.Fsm != null) lastState = choices.Fsm.ActiveStateName;
                return;
            }

            // 即使这一帧不再放球，也必须照常检查之前各轮光球的同步消散时间。
            ExpireGroups();
            if (choices == null || choices.Fsm == null) return;

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
                if (currentAttackGroup != null && !currentAttackGroup.HasFirst)
                    groups.Remove(currentAttackGroup); // 上一轮没有放出任何球
                currentAttackGroup = null;
                if (!OrbRadiance.EffectiveHardMode)
                {
                    currentAttackGroup = new OrbGroup { Name = mode + " P1/P2" };
                    groups.Add(currentAttackGroup);
                }
                nextChargeAt = Time.time;   // 下一帧就在目标点亮出蓄力特效
                OrbRadiance.Instance.Log("[orbspawn] 进 Orbs → 模式 " + mode + "（" + countPolicy +
                                         "）开始放 " + total +
                                         " 颗球（每颗蓄力 " + ChargeTime + "s、间隔 " + GapTime +
                                         "s；首颗开始消散约 " + (OrbRadiance.ChaseHeroWait +
                                         OrbRadiance.EffectiveOrbDissipateWait).ToString("0.##") +
                                         "s，此轮同步消散）");
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
            // 玩家可能在 0.4s 蓄力期间靠近预定生成点。若此时距离不足 3，
            // 重新选点并给出完整蓄力提示，避免警告显示在旧点却突然贴脸出球。
            if (!OrbRadiance.EffectiveHardMode && IsTooCloseToHero(pendingPos))
            {
                OrbRadiance.Instance.Log("[orbspawn] 玩家进入蓄力点 3 单位内，重新选安全出球点并重播蓄力");
                StartCharge();
                return;
            }

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

            if (!OrbRadiance.EffectiveHardMode && currentAttackGroup != null)
                RegisterOrb(currentAttackGroup, orb.GetComponents<PlayMakerFSM>()
                    .FirstOrDefault(f => f != null && f.FsmName == "Orb Control"));

            HeroController heroNow = HeroController.instance;
            string heroDistance = heroNow == null ? "unknown" :
                Vector2.Distance((Vector2)pos, (Vector2)heroNow.transform.position).ToString("F2");
            OrbRadiance.Instance.Log("[orbspawn] 放出光球 pos=(" + pos.x.ToString("F2") + "," +
                                     pos.y.ToString("F2") + ") distHero=" + heroDistance + " rbKinematic=" +
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
            Vector2 self = transform.position;
            HeroController hero = HeroController.instance;
            bool needHeroDistance = !OrbRadiance.EffectiveHardMode && hero != null;
            Vector2 heroPos = needHeroDistance ? (Vector2)hero.transform.position : Vector2.zero;

            for (int i = 0; i < MaxPosTries; i++)
            {
                Vector3 p = new Vector3(UnityEngine.Random.Range(minX, maxX),
                                        UnityEngine.Random.Range(minY, maxY), -0.1f);
                float d = Vector2.Distance((Vector2)p, self);
                if (d < MinDistFromSelf || d > MaxDistFromSelf) continue;
                if (needHeroDistance && Vector2.Distance((Vector2)p, heroPos) < MinDistFromHero) continue;
                return p;
            }

            if (!needHeroDistance)
            {
                // H 模式沿用原来的位置策略（包括原有兜底）。
                OrbRadiance.Instance.LogWarn("OrbRadiance: 光球位置 20 次都没落进 3~11，改用矩形中点");
                return new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, -0.1f);
            }

            // 随机失败不再无条件退回矩形中点：扫描矩形，优先保留原版与 Boss 的距离约束。
            // 只有两项约束无法同时满足时，才让玩家最小距离优先。
            Vector2 best = Vector2.zero;
            bool haveHeroSafe = false;
            float bestScore = float.NegativeInfinity;
            for (int iy = 0; iy <= 16; iy++)
            for (int ix = 0; ix <= 16; ix++)
            {
                Vector2 candidate = new Vector2(Mathf.Lerp(minX, maxX, ix / 16f),
                                                 Mathf.Lerp(minY, maxY, iy / 16f));
                float heroDist = Vector2.Distance(candidate, heroPos);
                if (heroDist < MinDistFromHero) continue;
                float bossDist = Vector2.Distance(candidate, self);
                if (bossDist >= MinDistFromSelf && bossDist <= MaxDistFromSelf)
                    return new Vector3(candidate.x, candidate.y, -0.1f);
                float score = -Mathf.Abs(bossDist - Mathf.Clamp(bossDist, MinDistFromSelf, MaxDistFromSelf));
                if (!haveHeroSafe || score > bestScore)
                {
                    haveHeroSafe = true;
                    bestScore = score;
                    best = candidate;
                }
            }
            if (haveHeroSafe)
            {
                OrbRadiance.Instance.LogWarn("[orbspawn] 3~11 Boss 距离与玩家距离约束无法同时满足，优先保证玩家距离>=3");
                return new Vector3(best.x, best.y, -0.1f);
            }

            // 极端情况下矩形都被玩家半径 3 覆盖；把位置推到半径外，不允许近距离兜底。
            Vector2 center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            Vector2 away = center - heroPos;
            if (away.sqrMagnitude < 0.0001f) away = self - heroPos;
            if (away.sqrMagnitude < 0.0001f) away = Vector2.right;
            center = heroPos + away.normalized * (MinDistFromHero + 0.05f);
            OrbRadiance.Instance.LogWarn("[orbspawn] 出球矩形无法满足玩家3单位安全半径，使用矩形外安全点");
            return new Vector3(center.x, center.y, -0.1f);
        }

        private static bool IsTooCloseToHero(Vector3 pos)
        {
            HeroController hero = HeroController.instance;
            return hero != null && Vector2.Distance((Vector2)pos, (Vector2)hero.transform.position) < MinDistFromHero;
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

        // P1/P2 由本组件直接放球；P4 走原版 Spawn Fireball，在 Orb Control OnEnable 时登记。
        internal static void RegisterNativeFinalOrb(PlayMakerFSM orbControl)
        {
            if (orbControl == null || OrbRadiance.EffectiveHardMode || !OrbRadiance.Settings.Enabled) return;
            GameObject boss = GameObject.Find("Absolute Radiance");
            OrbSpawner spawner = boss != null ? boss.GetComponent<OrbSpawner>() : null;
            if (spawner == null) return;

            // 先在激活时修正一次；等待光球实际进入追踪状态后再登记到同步组，
            // 避免对象池启用过早造成“第一颗消散时间”提前。
            EnsureHeroSpawnDistance(orbControl.gameObject);
            OrbP4SpawnSafety safety = orbControl.GetComponent<OrbP4SpawnSafety>();
            if (safety == null) safety = orbControl.gameObject.AddComponent<OrbP4SpawnSafety>();
            safety.Arm(orbControl);
        }

        internal static void EnrollNativeFinalOrb(PlayMakerFSM orbControl)
        {
            if (orbControl == null || OrbRadiance.EffectiveHardMode || !OrbRadiance.Settings.Enabled) return;
            GameObject boss = GameObject.Find("Absolute Radiance");
            OrbSpawner spawner = boss != null ? boss.GetComponent<OrbSpawner>() : null;
            if (spawner == null) return;
            if (spawner.currentFinalGroup == null ||
                (spawner.currentFinalGroup.HasFirst && Time.time >= spawner.currentFinalGroup.Deadline))
            {
                spawner.currentFinalGroup = new OrbGroup { Name = OrbRadiance.EffectiveEasyMode ? "Z P4" : "N P4" };
                spawner.groups.Add(spawner.currentFinalGroup);
            }
            spawner.RegisterOrb(spawner.currentFinalGroup, orbControl);
        }

        // 不干预光球后续追踪，只在出生检查过近的坐标。
        internal static void EnsureHeroSpawnDistance(GameObject orb)
        {
            if (orb == null || !OrbRadiance.Settings.Enabled || OrbRadiance.EffectiveHardMode) return;
            HeroController hero = HeroController.instance;
            if (hero == null) return;
            Vector2 heroPos = hero.transform.position;
            Vector2 delta = (Vector2)orb.transform.position - heroPos;
            float dist = delta.magnitude;
            if (dist >= MinDistFromHero) return;
            if (dist < 0.0001f) delta = Vector2.right;
            Vector2 safe = heroPos + delta.normalized * (MinDistFromHero + 0.05f);
            Vector3 at = orb.transform.position;
            orb.transform.position = new Vector3(safe.x, safe.y, at.z);
            OrbRadiance.Instance.Log("[orbspawn] P4 原版光球出生安全距离校正 dist=" + dist.ToString("F2") + " -> " +
                Vector2.Distance(safe, heroPos).ToString("F2"));
        }

        private void RegisterOrb(OrbGroup group, PlayMakerFSM orbControl)
        {
            if (group == null || orbControl == null || orbControl.Fsm == null) return;
            OrbSpawnMarker marker = orbControl.GetComponent<OrbSpawnMarker>();
            if (marker == null) marker = orbControl.gameObject.AddComponent<OrbSpawnMarker>();
            int serial = ++spawnSerial;
            marker.Serial = serial;
            if (!group.HasFirst)
            {
                group.HasFirst = true;
                group.FirstSpawnTime = Time.time;
                group.Deadline = Time.time + OrbRadiance.ChaseHeroWait + OrbRadiance.EffectiveOrbDissipateWait;
                OrbRadiance.Instance.Log("[orb-sync] " + group.Name + " FIRST_FIRE t=" + Time.time.ToString("F3") +
                    " dissipateAt=" + group.Deadline.ToString("F3"));
            }
            group.Members.Add(new OrbEntry { Control = orbControl, Marker = marker, Serial = serial });
        }

        private void ExpireGroups()
        {
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                OrbGroup group = groups[i];
                if (!group.HasFirst || Time.time < group.Deadline) continue;
                int started = 0;
                int alreadyGone = 0;
                foreach (OrbEntry entry in group.Members)
                {
                    if (entry.Control == null || entry.Control.Fsm == null || entry.Marker == null ||
                        entry.Marker.Serial != entry.Serial || !entry.Control.gameObject.activeInHierarchy)
                    {
                        alreadyGone++;
                        continue;
                    }
                    string state = entry.Control.Fsm.ActiveStateName;
                    // 两个追踪状态都有 DISSIPATE 事件，避免给已进入消散阶段的球重复发送。
                    if (state == "Chase Hero" || state == "Chase Hero 2")
                    {
                        entry.Control.SendEvent("DISSIPATE");
                        started++;
                    }
                    else alreadyGone++;
                }
                OrbRadiance.Instance.Log("[orb-sync] " + group.Name + " GROUP_DISSIPATE t=" +
                    Time.time.ToString("F3") + " firstElapsed=" + (Time.time - group.FirstSpawnTime).ToString("F3") +
                    " members=" + group.Members.Count + " sent=" + started + " skipped=" + alreadyGone);
                // 如果玩家反复贴近蓄力点，尚未放出的球不能在统一截止时间后继续补发。
                if (currentAttackGroup == group && remaining > 0)
                {
                    remaining = 0;
                    charging = false;
                    SetChargeFx(pendingPos, false);
                    OrbRadiance.Instance.LogWarn("[orb-sync] 本轮同步消散截止，取消未完成的剩余出球");
                }
                groups.RemoveAt(i);
                if (currentAttackGroup == group) currentAttackGroup = null;
                if (currentFinalGroup == group) currentFinalGroup = null;
            }
        }

        private void OnDisable()
        {
            groups.Clear();
            currentAttackGroup = null;
            currentFinalGroup = null;
            remaining = 0;
            charging = false;
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

    // 使用对象池时需区分同一个 GameObject 的不同“出生”，防止旧批次误消散新球。
    internal sealed class OrbSpawnMarker : MonoBehaviour
    {
        internal int Serial;
        private void OnDisable() { Serial = 0; }
    }

    // P4 原版 Spawn Fireball 沿用其自身 FSM，首帧补查安全距离，不改变追踪逻辑。
    internal sealed class OrbP4SpawnSafety : MonoBehaviour
    {
        private PlayMakerFSM orbControl;
        private bool pending;

        internal void Arm(PlayMakerFSM fsm)
        {
            orbControl = fsm;
            pending = true;
        }

        private void LateUpdate()
        {
            if (!pending || orbControl == null || orbControl.Fsm == null) return;
            string state = orbControl.Fsm.ActiveStateName;
            if (state != "Chase Hero" && state != "Chase Hero 2") return;
            pending = false;
            OrbSpawner.EnsureHeroSpawnDistance(gameObject);
            OrbSpawner.EnrollNativeFinalOrb(orbControl);
        }

        private void OnDisable() { pending = false; }
    }
}

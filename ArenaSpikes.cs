using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P1 地刺：Boss Control 的 Control FSM 一进 `Battle Start`（= 入场动画播完、开局那一帧）
    // 就把五处地面地刺全部升起并常驻。
    //
    // 为什么是开局当帧起，而不是等场地移除同刻（2026-09-19 用户定）：
    //   原版单根刺从收到 UP 到真正立起来要走完 `Down → Downed → Floor Antic` 2s → `Spike Up`，
    //   而那段动画是**收在地板下面**做的（刺 world y 18.5，地板顶面 y=20，被地面贴图挡着看不见）。
    //   等场地移除那一刻才起刺，玩家会先掉进虚空（`VoidKill` 直接 9999），来不及踩上刺。
    //
    // 首批升起和后续恢复**不同档**（2026-09-19 用户定：「开局地刺的生成和原版一致，
    // 后续就和天国余晖一样」）：
    //   · 首批升起 = **原版流程**，一个字不改：`Downed →(UP)→ Floor Antic` 2s 预备 → `Spike Up`。
    //     所以 `Rise()` 里不碰任何一根刺的自己人状态，只 `SetState("Spike Waves")` 让 0.1s
    //     刷条把 UP 发下去。
    //   · 之后（被刺碰掉后的恢复）= 照天国余晖 P2 的节奏：`PatchAnticWait()` 把 24 根刺各自
    //     `Floor Antic` 的 Wait 从 2s 压到 `SpikeAnticWait`(0.5s)，全程 ≈1s（原版 ≈2.5s）。
    //     **压的时机是「首批 24 根全部进 `Up`」那一刻**（`PollSpikesUp` 里调），不是起刺那一帧
    //     —— 同一段 `Wait` 首次升起和后续恢复共用，早压就等于把开局也改了。
    //     （走过一条弯路：曾照天国余晖 `PlatformSpikes.cs:116` 直接 `SetState("Spike Up")` 让
    //     首批秒起，用户不要这个，撤掉了。）
    //
    // 地板撤除不再只看 `FloorRemoveDelay`（2026-09-19 用户定「地板等刺立好再撤」）：
    //   `ArenaFloor` 的条件 AND 上这里的 `GroundReady` —— 每帧数五处地面组下停在 `Up` 的刺
    //   （`Up` 里才 SetPolygonCollider(true)，之前踩不到），数够 24 根才放地板走。
    //   首批走原版 → 真正撤除时刻是 ≈2.3~2.7s（`FloorRemoveDelay` 的 1.5s 先到，
    //   但闸门会等刺），超过 `SpikeUpTimeout` 还没齐也放行，免得地板永远卡住。
    //   实际值看日志里那句「地板可以撤了（自起刺 x.xxs）」。
    //
    // 手法照 ANY2（AnyRAD2.0（Any2.0辐光）.decompiled.cs:730-757）：
    //   把 Spike Control 的 4 个状态 —— Spikes Left / Spikes Right / Wave L / Wave R ——
    //   里全部 SendEventByName 原位改成 "UP"，再把 Wave L / Wave R 的 WaitRandom 从 6~8s
    //   压到 0.1s，最后 SetState("Spike Waves")。
    //
    // 为什么是这套（2026-09-19 用户定，从原来的 Spikes Outer 换过来）：
    //   · Spike Waves →(SendRandomEvent)→ Wave L / Wave R →(各自自环)→ 回另一侧；
    //     WaitRandom 压到 0.1s 后这个环变成「每 0.1 秒把五处全 UP 重播一遍」的常驻刷条。
    //   · 原来停在 Spikes Outer（它只有一条 SPIKES DOWN → Respike Pause 转移），补发 UP 只在
    //     收到 SPIKES DOWN 时发生一次；ANY2 这套是无条件每 0.1s 刷，任何把某处刺打下去的事件
    //     都会在 0.1s 内被盖回来 —— 更抗干扰。
    //   · Spikes Left / Spikes Right 本身一条转移都没有（收了事件什么都不做），一并改成全 UP，
    //     是为了兜住 SPIKES LEFT / SPIKES RIGHT 这两条全局转移。
    //   · Wave L / Wave R 里各有 2 个 SetFsmBool 会写 Beam Sweeper 的 Force Left / Force Right
    //     （它俩确实被梁扫的 Control FSM 读去决定扫哪边）。ANY2 不动这两个，这里也不动
    //     —— 代价是 P1 梁扫方向会被这个 0.1s 循环持续改写（2026-09-19 用户知情后选了照抄）。
    //
    // 别指望「刷得快」能让刺更快立起来：单根刺的 `UP` 只在 `Downed` 状态有效
    // （…__Control__fsm_3403.md:88-92；`Floor Antic` / `Spike Up` / `Up` 都没有 UP 转移），
    // 所以 0.1s 刷条只是「尽早把刺推回原版流程」，真正决定快慢的是 `Floor Antic` 那 2s
    // —— 那一段靠 PatchAnticWait 压（见上），不是靠刷得快。
    //
    // 「整排一起收一起升」是原版机制，不是这里的 bug（2026-09-19 用户实测确认）：
    //   英雄 HeroBox 碰到刺 → 那根刺的 `Hero Saver` 进场 → BroadcastAll 发 `SPIKES DOWN`
    //   （…__Hero_Saver__fsm_3454.md:54-64）→ 24 根刺同时收 → 这里 0.1s 内补 UP，每根再走
    //   `Down → Downed → Floor Antic(SpikeAnticWait) → Spike Up` 重新立起来。
    //   ANY2 全文没有 saver / SPIKES DOWN，也没处理这个 —— 它的刺被英雄碰到一样整排收。
    //   要消掉这条只能去掐 Hero Saver 的触发器（tag 式 Trigger2dEvent，collideTag="HeroBox"），
    //   代价是站刺里持续掉血、不再被弹开，目前不做。
    //
    // 不做血线恢复（2026-09-19 用户定，照 ANY2）：P2 的 `SPIKE WAVES` 广播落到的正是被我们
    // 改过的 Spike Waves / Wave L / Wave R，所以 P2 依然全场常驻；ANY2 是另外把三个相位阈值
    // 抬高来推后 P3 的 `SPIKES OUTER`，我们不抬（那会把整场血量节奏改掉）。
    //
    // 无限剑雨阶段也保持常驻（2026-09-19 用户定，照天国余晖）：
    //   刷条唯一会被打断的地方 = `Absolute Radiance/Phase Control` 的 `Set Phase 3` 状态里那发
    //   `SPIKES OUTER`（BroadcastAll）→ Spike Control 全局转移 `SPIKES OUTER → Spikes Outer`，
    //   master 被拽出 0.1s 刷条，此后整段无限剑雨都是原版「Far L/Far R 上、其余下」的固定图案。
    //   天国余晖的处理 = `RemoveAction(fsm, "Set Phase 3", 0)` 把这发送信删掉（RadianceBoss.cs:616），
    //   同状态第 2 个动作 `SetFsmBool(Rage Ready)` 保留 → 无限剑雨照常触发（RAGE START 那条链没动）。
    //   这里做同一件事，但**不删动作元素**：只把那一个 SendEventByName 的 `Enabled` 设成 false。
    //   PlayMaker 的 `FsmState.ActivateActions` 会跳过 Enabled=false 的动作
    //   （`参考代码/playmaker_src/HutongGames.PlayMaker.FsmState.decompiled.cs:292`）。
    //   删元素会挪动 `Set Phase 3` 里后续动作的下标，而 ManyRadiances 只有 DLL 没有源码、无法确认
    //   它是否按下标读这个 FSM —— 按本 mod 一贯约束（原位改、不增删）关 Enabled，效果等价、下标不动。
    //
    // Boss 倒地阶段也保持常驻（2026-09-19 用户定）：
    //   倒地那一段有两处 BroadcastAll 发 `SPIKES NONE`（→ Spike Control 全局转移 `SPIKES NONE → None`，
    //   `None` 会给五处地面组全发 `DOWN`）：
    //     · `Absolute Radiance/Control` 的 `Stun1 Start`（倒地动画本体：BigShake / Halo DOWN /
    //       NAIL END / SPIKES NONE / ActivateGameObject）
    //     · `Absolute Radiance/Phase Control` 的 `Stun 1`（相位记账那一步）
    //   两处都掐掉（同 `Set Phase 3` 那发一样只关 `Enabled`）。倒地本身不受影响 —— 其余动作
    //   以及紧随其后的 `STUN 1` 事件链都没动，只是刺不再被收走。
    //
    // 只原位改字段、不增删动作（同 OrbRadiance.cs 的约束），改前的值记在
    // OriginalSendEvent / OriginalWaitRandom / OriginalSendEnabled 里供关开关时写回。
    //
    // 下沉：起刺时把五处地面组整体下移 2.68（刺 world y 21.18 → 18.5，2026-09-19 用户定）。
    // 动的是五个组容器（Centre / Mid L / Mid R / Far L / Far R）的 transform —— 它们都在
    // world (0,0,0)，24 根刺是它们的子物体、local y 就等于世界 y，父物体一动子物体全跟着。
    // 不碰任何 FSM 字段，所以「起刺/收刺」的原版逻辑完全不变；关开关时写回原值。
    internal class ArenaSpikes : MonoBehaviour
    {
        private const string SpikePath = "Boss Control/Spike Control";

        // 入口状态：它自环出 Wave L / Wave R，0.1s 一圈
        private const string DriveState = "Spike Waves";

        // 全部 SendEventByName 改成 UP 的 4 个状态（照 ANY2 :733-754）。
        // Spikes Left / Spikes Right 里没有 WaitRandom，所以下场一并扫 WaitRandom 不会误伤。
        private static readonly string[] PatchStates =
            { "Spikes Left", "Spikes Right", "Wave L", "Wave R" };

        // Wave L / Wave R 自环的间隔（原版 WaitRandom 6~8s），照 ANY2 压成 0.1s
        private const float WaveWait = 0.1f;

        // 五处地面组的容器名（都是 Spike Control 的直接子物体，全在 world (0,0,0)）
        private static readonly string[] Groups =
            { "Centre", "Mid L", "Mid R", "Far L", "Far R" };

        // 整体下沉量：24 根刺 world y 全是 21.18，减 2.68 正好落到 18.5
        private const float SpikeDrop = 2.68f;

        // 24 根刺全都进 `Up` 才算「托得住人了」：`Up` 里才 SetPolygonCollider(true)，
        // 在此之前（Init/Downed/Floor Antic/Spike Up）碰撞体是关的，人掉下去踩不到。
        private const int SpikeTotal = 24;

        // 每根刺 `Floor Antic` 的 Wait（原版 2s）压到多少。这条管的是**被刺碰掉之后的恢复**：
        // SPIKES DOWN → Down → Downed → 0.1s 刷条发 UP → Floor Antic(本值) → Spike Up，
        // 全程 ≈1s（原版 ≈2.5s）。照天国余晖 P2 的 0.5s 预备。
        private const float SpikeAnticWait = 0.5f;

        // 等刺立起来的兜底上限：到点还没齐也放行（宁可撤了再说，别把地板永久卡住）
        private const float SpikeUpTimeout = 8f;

        // 地板等的就是这个旗标（`ArenaFloor` 里 AND 进撤除条件）。只在起刺成功后置 false，
        // 其余情况（没启用 / 起刺失败 / 已收回）一律 true，免得地板永远撤不掉。
        internal static bool GroundReady = true;

        private PlayMakerFSM control;
        private string lastState;
        private bool risen;
        private float riseAt;

        // 本轮 `Floor Antic` 的 Wait 有没有被我们压过（压过就不重复压、不重复打日志）。
        // 每轮收回时清掉：`Retract()` 里 `RestorePatched()` 会把 Wait 写回 2s，
        // 下一轮的首批升起要重新走原版流程。
        private bool anticPatched;

        // 下沉过的组 + 它们原本的 local y（按同一顺序对应），收回时写回
        private readonly List<Transform> droppedGroups = new List<Transform>();
        private readonly List<float> droppedY = new List<float>();

        internal void Init(PlayMakerFSM f)
        {
            control = f;
        }

        private void Update()
        {
            if (control == null || control.Fsm == null) return;

            // NORMAL / EASY 完全交给原版阶段性刺事件；只有 HARD 使用常驻刺改动。
            if (!OrbRadiance.EffectiveHardMode)
            {
                lastState = control.Fsm.ActiveStateName;
                if (risen) Retract();
                else GroundReady = true;
                RestoreSpikesOuter();
                return;
            }

            // 总开关关掉：把地刺收回去（只在我们起过的时候动）
            if (!OrbRadiance.Settings.Enabled)
            {
                lastState = null;
                Retract();
                return;
            }

            string cur = control.Fsm.ActiveStateName;
            if (cur != lastState)
            {
                lastState = cur;

                // 重打先收回，让下一轮照常重新起刺
                if (cur == "Battle Start" || cur == "Refight" || cur == "Init" || cur == "Setup")
                    Retract();

                // 开局当帧就起刺：上升动画用掉场地还在的这几秒，场地一消失刺已经立好
                if (cur == "Battle Start") Rise();
            }

            // 每帧查（不能只在状态变化时查）：地板就卡在这个旗标上，立好当帧要立刻放行
            if (risen && !GroundReady) PollSpikesUp();
        }

        // 数够 SpikeTotal 根刺停在 `Up`（只有这个状态里 SetPolygonCollider 是开的）就放地板走
        private void PollSpikesUp()
        {
            int up = CountUpSpikes();
            if (up >= SpikeTotal)
            {
                GroundReady = true;
                OrbRadiance.Instance.Log("[spikes] " + up + "/" + SpikeTotal + " 根刺已进 Up（碰撞体已开），" +
                                         "地板可以撤了（自起刺 " + (Time.time - riseAt).ToString("F2") + "s）");
                // 首批已经照原版走完，从这里开始压预备：被刺碰掉之后的恢复才变快
                PatchAnticWait();
                return;
            }

            if (Time.time - riseAt >= SpikeUpTimeout)
            {
                GroundReady = true;
                OrbRadiance.Instance.LogWarn("OrbRadiance: 起刺 " + SpikeUpTimeout + "s 后只数到 " + up + "/" +
                                             SpikeTotal + " 根刺在 Up，不再等，放地板撤除（玩家可能掉虚空）");
                PatchAnticWait();
            }
        }

        private static int CountUpSpikes()
        {
            int up = 0;
            foreach (PlayMakerFSM f in SpikeFsms())
                if (f.Fsm.ActiveStateName == "Up") up++;
            return up;
        }

        // 五处地面组下的 24 根刺，每根一个 `Control` FSM（组容器自己不带 FSM）
        private static IEnumerable<PlayMakerFSM> SpikeFsms()
        {
            GameObject root = GameObject.Find(SpikePath);
            if (root == null) yield break;

            foreach (string name in Groups)
            {
                Transform t = root.transform.Find(name);
                if (t == null) continue;
                foreach (PlayMakerFSM f in t.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f.Fsm != null && f.FsmName == "Control") yield return f;
            }
        }

        private void Rise()
        {
            // 先假定起不成功：下面任何一条提前返回（找不到 Spike Control / 没有可改的事件）
            // 都不能把地板永久卡在「等刺」上
            GroundReady = true;

            PlayMakerFSM spike = SpikeFsm();
            if (spike == null) return;

            int total = 0, changed = 0, waits = 0;
            foreach (string name in PatchStates)
            {
                FsmState st = spike.Fsm.GetState(name);
                if (st == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: Spike Control 里没有 " + name +
                                                 " 状态，这一处没改");
                    continue;
                }

                foreach (SendEventByName se in st.Actions.OfType<SendEventByName>())
                {
                    if (se.sendEvent == null) continue;
                    total++;
                    if (se.sendEvent.Value == "UP") continue;
                    if (!OriginalSendEvent.ContainsKey(se)) OriginalSendEvent[se] = se.sendEvent.Value;
                    se.sendEvent.Value = "UP";
                    changed++;
                }

                foreach (WaitRandom wr in st.Actions.OfType<WaitRandom>())
                {
                    if (wr.timeMin == null || wr.timeMax == null) continue;
                    if (!OriginalWaitRandom.ContainsKey(wr))
                        OriginalWaitRandom[wr] =
                            new KeyValuePair<float, float>(wr.timeMin.Value, wr.timeMax.Value);
                    wr.timeMin.Value = WaveWait;
                    wr.timeMax.Value = WaveWait;
                    waits++;
                }
            }

            if (total == 0)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: " + string.Join(" / ", PatchStates) +
                                             " 里没有带 sendEvent 的 SendEventByName，地刺未升起");
                return;
            }

            spike.SetState(DriveState);
            risen = true;

            // 首批刺**不加速**：走原版 `Downed →(UP)→ Floor Antic` 2s 预备 → `Spike Up`（2026-09-19
            // 用户定「开局地刺的生成和原版一致」）。压预备放在全部立好之后（见 PollSpikesUp）。

            // 从这一刻起地板要等我们（PollSpikesUp 数够 24 根进 Up 才把旗标放回去）
            riseAt = Time.time;
            GroundReady = false;

            DropGroups();
            OrbRadiance.Instance.Log("[spikes] 开局当帧 → 五处地面地刺开始升起（" + total +
                                     " 个事件现在全是 UP，本次改写 " + changed + " 个；" + waits +
                                     " 个 WaitRandom 压到 " + WaveWait + "s，停在 " + DriveState + "）");
        }

        // 把 24 根刺各自 `Floor Antic` 的 Wait 从原版 2s 压到 SpikeAnticWait（原位改字段值）。
        // 只在首批刺全部立好之后调用（PollSpikesUp），所以开局的首次升起仍是原版 2s。
        private void PatchAnticWait()
        {
            if (anticPatched) return;
            anticPatched = true;

            int ok = 0;
            foreach (PlayMakerFSM f in SpikeFsms())
            {
                FsmState antic = f.Fsm.GetState("Floor Antic");
                if (antic == null) continue;

                Wait w = antic.Actions.OfType<Wait>()
                    .FirstOrDefault(x => x.finishEvent != null && x.finishEvent.Name == "FINISHED");
                if (w == null || w.time == null) continue;

                if (!OriginalSpikeWait.ContainsKey(w)) OriginalSpikeWait[w] = w.time.Value;
                w.time.Value = SpikeAnticWait;
                ok++;
            }

            OrbRadiance.Instance.Log("[spikes] " + ok + "/" + SpikeTotal + " 根刺的预备压到 " +
                                     SpikeAnticWait + "s（原版 2s；首批已走完原版流程，" +
                                     "此后被刺碰掉的恢复 ≈1s）");
        }

        // 把五处地面组整体下沉 SpikeDrop（21.18 → 18.5），子物体（24 根刺）自动跟着
        private void DropGroups()
        {
            droppedGroups.Clear();
            droppedY.Clear();

            GameObject root = GameObject.Find(SpikePath);
            if (root == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + SpikePath + "，地刺没下沉");
                return;
            }

            foreach (string name in Groups)
            {
                Transform t = root.transform.Find(name);
                if (t == null)
                {
                    OrbRadiance.Instance.LogWarn("OrbRadiance: " + SpikePath + " 下没有 " + name +
                                                 " 组，这一处没下沉");
                    continue;
                }
                droppedGroups.Add(t);
                droppedY.Add(t.localPosition.y);
                Vector3 p = t.localPosition;
                t.localPosition = new Vector3(p.x, p.y - SpikeDrop, p.z);
            }

            if (droppedGroups.Count > 0)
                OrbRadiance.Instance.Log("[spikes] 五处地面组下沉 " + SpikeDrop + "（刺 21.18 → 18.5），" +
                                         "共 " + droppedGroups.Count + " 组");
        }

        // 写回下沉前的 local y
        private void UndropGroups()
        {
            for (int i = 0; i < droppedGroups.Count; i++)
            {
                Transform t = droppedGroups[i];
                if (t == null) continue;
                Vector3 p = t.localPosition;
                t.localPosition = new Vector3(p.x, droppedY[i], p.z);
            }
            droppedGroups.Clear();
            droppedY.Clear();
        }

        private void Retract()
        {
            // 收回 = 不再有刺要等，地板立刻恢复原来的固定计时（不管这次起没起过）
            GroundReady = true;

            if (!risen) return;
            risen = false;
            anticPatched = false;

            RestorePatched();
            UndropGroups();

            PlayMakerFSM spike = SpikeFsm();
            if (spike == null) return;

            // 全局转移 SPIKE NONE → None：给五处子地刺发 DOWN，也把 master 从 0.1s 刷条里拉出来
            spike.SendEvent("SPIKES NONE");
            OrbRadiance.Instance.Log("[spikes] 地刺收回");
        }

        // 关开关时写回改过的值（和 OrbRadiance.OriginalWait 同一套思路：只记改前的值，不硬编）
        internal static readonly Dictionary<SendEventByName, string> OriginalSendEvent =
            new Dictionary<SendEventByName, string>();

        // 值 = (timeMin, timeMax)
        internal static readonly Dictionary<WaitRandom, KeyValuePair<float, float>> OriginalWaitRandom =
            new Dictionary<WaitRandom, KeyValuePair<float, float>>();

        // 24 根刺各自那份 `Floor Antic` 的 Wait 改前的值（每根刺一个独立的 Wait 实例）
        private static readonly Dictionary<Wait, float> OriginalSpikeWait = new Dictionary<Wait, float>();

        internal static void RestorePatched()
        {
            foreach (KeyValuePair<SendEventByName, string> kv in OriginalSendEvent)
                if (kv.Key != null && kv.Key.sendEvent != null) kv.Key.sendEvent.Value = kv.Value;

            foreach (KeyValuePair<WaitRandom, KeyValuePair<float, float>> kv in OriginalWaitRandom)
            {
                if (kv.Key == null) continue;
                if (kv.Key.timeMin != null) kv.Key.timeMin.Value = kv.Value.Key;
                if (kv.Key.timeMax != null) kv.Key.timeMax.Value = kv.Value.Value;
            }

            foreach (KeyValuePair<Wait, float> kv in OriginalSpikeWait)
                if (kv.Key != null && kv.Key.time != null) kv.Key.time.Value = kv.Value;
        }

        // ---- 无限剑雨 / Boss 倒地期保持常驻：关掉那几发会把刺收走的广播（见文件头）----

        private const string PhaseOuterState = "Set Phase 3";
        private const string SpikesOuterEvent = "SPIKES OUTER";
        private const string SpikesNoneEvent = "SPIKES NONE";

        // 倒地那两处 SPIKES NONE 的宿主状态：Control 的倒地动画、Phase Control 的相位记账
        internal const string StunStartState = "Stun1 Start";
        internal const string PhaseStunState = "Stun 1";

        internal static readonly Dictionary<SendEventByName, bool> OriginalSendEnabled =
            new Dictionary<SendEventByName, bool>();

        // 只关 Enabled、不删动作元素：`FsmState.ActivateActions` 会跳过 Enabled=false 的动作，
        // 停在 Set Phase 3 里也不会发 SPIKES OUTER 了；同状态的 SetFsmBool(Rage Ready) 照跑。
        internal static void PatchSpikesOuter(Fsm phase)
        {
            if (!OrbRadiance.EffectiveHardMode) return;

            FsmState st = phase.GetState(PhaseOuterState);
            if (st == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: Phase Control 里没有 " + PhaseOuterState +
                                             " 状态，无限剑雨阶段地刺会退回原版");
                return;
            }

            foreach (SendEventByName se in st.Actions.OfType<SendEventByName>())
            {
                if (se.sendEvent == null || se.sendEvent.Value != SpikesOuterEvent) continue;
                if (!OriginalSendEnabled.ContainsKey(se)) OriginalSendEnabled[se] = se.Enabled;
                se.Enabled = false;
                OrbRadiance.Instance.Log("[spikes] 关掉 " + PhaseOuterState + " 的 " + SpikesOuterEvent +
                                         " 送信 → 无限剑雨阶段地刺保持常驻");
                return;
            }

            OrbRadiance.Instance.LogWarn("OrbRadiance: " + PhaseOuterState + " 里没有发 " + SpikesOuterEvent +
                                         " 的 SendEventByName，无限剑雨阶段地刺会退回原版");
        }

        // 倒地期保持常驻：关掉指定状态里那发 BroadcastAll 的 SPIKES NONE
        // （`SPIKES NONE → None`，`None` 会给五处地面组全发 DOWN → 24 根刺一起收）。
        internal static void PatchSpikesNone(Fsm fsm, string stateName)
        {
            if (!OrbRadiance.EffectiveHardMode) return;

            FsmState st = fsm.GetState(stateName);
            if (st == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 没有 " + stateName +
                                             " 状态，倒地时地刺会被收走");
                return;
            }

            foreach (SendEventByName se in st.Actions.OfType<SendEventByName>())
            {
                if (se.sendEvent == null || se.sendEvent.Value != SpikesNoneEvent) continue;
                if (!OriginalSendEnabled.ContainsKey(se)) OriginalSendEnabled[se] = se.Enabled;
                se.Enabled = false;
                OrbRadiance.Instance.Log("[spikes] 关掉 " + stateName + " 的 " + SpikesNoneEvent +
                                         " 送信 → Boss 倒地时地刺保持常驻");
                return;
            }

            OrbRadiance.Instance.LogWarn("OrbRadiance: " + stateName + " 里没有发 " + SpikesNoneEvent +
                                         " 的 SendEventByName，倒地时地刺会被收走");
        }

        // 只在总开关关掉时写回：Retract() 走的是 RestorePatched()，不动这一项 ——
        // 死亡重打（Refight → Battle Start）是同一个场景内重来、这几个 FSM 实例
        // 不会重新 OnEnable，那会儿要是把送信放回去就再没人关了。
        internal static void RestoreSpikesOuter()
        {
            foreach (KeyValuePair<SendEventByName, bool> kv in OriginalSendEnabled)
                if (kv.Key != null) kv.Key.Enabled = kv.Value;
        }

        private PlayMakerFSM SpikeFsm()
        {
            GameObject go = GameObject.Find(SpikePath);
            if (go == null)
            {
                OrbRadiance.Instance.LogWarn("OrbRadiance: 找不到 " + SpikePath + "，地刺没动");
                return null;
            }
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
                if (f.Fsm != null && f.FsmName == "Control") return f;
            OrbRadiance.Instance.LogWarn("OrbRadiance: " + SpikePath + " 上没有 Control FSM，地刺没动");
            return null;
        }
    }
}

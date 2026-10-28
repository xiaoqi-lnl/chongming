using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace OrbRadiance
{
    // P1 的 Z/N/H 与 P2 的 Z/N 共用双向光墙预警；H P2 的随机光墙由 HardBeamWall 独立管理。
    // 本版不再依赖原版 atlas0 的 UV 裁切，而是直接使用用户提供的右侧 PNG 作为细光柱贴图。
    internal sealed class P1BeamWallWarning : MonoBehaviour
    {
        // 【可调参数】P1 和 Z/N P2 光墙共用前摇时间；修改后各自的 FSM 攻击窗口同步延长。
        private const float WarningSeconds = 0.5f;

        // 【可调参数】两根预警柱位于中心线左右，两根细光柱之间不显示整面光墙。
        private const float CenterX = 61f;
        private const float EdgeOffset = 0.95f;       // 左右细光柱离光墙中线的距离（世界单位）
        private const float EdgeWidth = 0.2f;           // 每根细光柱的宽度（世界单位）
        private const float ExtraHeight = 20f;        // 超出相机可视高度的额外覆盖范围
        private const float BlinkPeriod = 0.18f;      // 闪烁周期
        private const float BlinkBrightTime = 0.105f; // 每周期亮起时间

        private const string BypassEvent = "BEAM SWEEP P1 WARNING BYPASS";
        private static readonly Dictionary<string, string> Events = new Dictionary<string, string>
        {
            { "Beam Sweep L", "BEAM SWEEP L" },
            { "Beam Sweep R", "BEAM SWEEP R" },
            { "Beam Sweep L 2", "BEAM SWEEP L2" },
            { "Beam Sweep R 2", "BEAM SWEEP R2" }
        };
        private static readonly HashSet<P1BeamWallWarning> Instances = new HashSet<P1BeamWallWarning>();
        private static readonly Dictionary<Wait, float> OriginalWait = new Dictionary<Wait, float>();

        // 来自用户提供的“右边那张 PNG”，已裁掉上下透明边并在运行时旋转成竖向光柱。
        private static readonly string EmbeddedWarningPngBase64 =
            @"iVBORw0KGgoAAAANSUhEUgAAAHEAAAAhCAYAAADu8EQnAAAWv0lEQVR42u2by65kR3aev39F7Mxzq1MXsshmNVtNEC3Z7gIEQT2RR9ZLtF5H9AP4Aeyhh24DhgBPbE84MuwBbRgWG7ZEUSU2u4vFIut6Lpm5I9bvQexzqeqi1JIN2IB1gJO5d0TsiNjrvlb8Kf7P/un1Bvtv8bAwf//3v0/07xxj/o4U/u6nPvroo19f/6NXvv4ahv/T/Hv2LbSwfxNG/uaM+Oij33zxn/3s57++9k/Hx0+/45mPP/70r93vH/7hff8GC19dXiz0szcP/elPf+z/14VGf+w/jt9E8r+DhXz88Ufx8cfXibh8A5/cePhGgv/kJ8Anr7Z9+vnTMfbHlx8A3L9/MWB8/fkXT97MxB/BjwDt7lwxcXn2009fG/vzn3/nG93/8Lb5yXLzydjmJ5/AT4CXv/Oe/yaB+dl3XFwIw9+Out/NmI8++uhyvnqf+68S5eNPdcGTG9eY8JNrL3bFjX/B3bvop/evtT2G+wsPjlfry+en6Y548AEAD4AP3hrtv3g4mHL8/jLwGN7nS74E3n//feB/6lefvAdA1CPduHf05rc6g4ftxLwHPBxN9341Lm7fB34FDz95j3fePjbv/8F3Eu4X7wC/gB/0O/bxC//+j+D462Vr559dyMv4+PTTXxOYHy8CcyEmPwbu3/1x8vGweB//IfDxb2Y97l+b99fF5Iq7+lP/q9Xl2GsS++dfPNTYLfzoRwC/zV/91X9WeXh8jemD8u//wZfwJTz8+sVV33tw7949/N9e6BEQ9URw97I73j0dY78BlTPdAf7yxUa3gafA7Q8BbqPn57oFPH+5vZz7OTevvdTrdwvB+9o+3r8kzu3bT6/e7c/23ki02zcPrtrfhkd/eua3bh9dG/sVAO8Cj34X8uGRLwRmERsevvcr3nnv2P2v7pgHDy57PgD8/TvmR/DZZ9cNyGeX1uN1g8HPYfPhvdf2OrToJy+vrIJ++ct/fgAQj/cFj8YOYVz+7rs8fvxC8A4RL8Q38NY/GN1PPjt9RYNf/vJJcAtufXCLW8/gi5cb6WQljuH4/Rccv4BfnazFyxtwA+59fyHLX7wIgKPvwdHRBZ2OOD3biSM4fHcngNOv4ex8t6x5941adHj4eFycwilwd+8oOQQW5X30CDgBOOHwYOVLnT4a/199dYRfzAY4+u21X/7y4uXGRx5u/YP39/wceP7F6PqtwzsJn8NteMpt8ptFcO4APOHx/zjwnVtnBnhn0ZavP4W7bx35EcCjr8i7R+79+FVmfQk/eO+O+QAeXGrugzf7xCd/8R9u6s6Xkla6Ls4vXp4J4P0LM3ey0cnJSi854sa9zULYRTtO4Oh7O519U8f9IbxzFx5/cUH0A3R3ls534hz2gfPNrP07+0hV5+ew2c4aGrNZFtxj82yW7tRXhGVvA5tt01ZFe4Ddr15+Xb23XG72rp5Ze/Kzp+ewgVu3Ji/Tj3HA+ZWScuumEs45fwLsTd7Plc+Ag7e/wY9vvqKphwcrf/3gDA7h8ABevjxKTrhUBL98ak7g6N21j47WfvGLnQFu3tj3F4vJOL6xNbfg2QPIvvXNxXrcufOEbx8fvMLYty7X/ga9/cPR9+lj9PLln7yr8xacnQFwriKAg8OdYB/O4Hyz0/6dZYInsN2dxd69hdAPB/H378xiW7TdNgGsb6558c15rNdr1sdbdrsmdgfaAut1k+YmtsC6izXACrawm0Prdb9k3O60aXVUrhi5gnnu4hSYrr3hDFMt5rD44p4JmHfAzO5kghVMVZdR5u4EYMdqqmYFrFfsvt3hqXgN+KibF3XMt96yOtpLgO0LWB93b58DnszeBp9Xb4C9m5P9pBs37703+fwJ7Hky++ecn628D/DWPmePm+Gcg7fh5NHY090frn369WJKDuHkq5UBDg/W5ugEFitxaUCOduYF6Pyrf/3h9sW8EHLN+mbRbjcPAgOsT4X2pJMi1rNgB7s9zXPX6ghYhXZPd6wOB4Xns02sVqG5dbGD6XZXazU4m5kOJua5iRlYdUGImqaB5KClmFZqL7vq/li+9zlqvcatCq1pMLVde6PWqDVNvZ4ZLQxlZt4AexOVMB4a0dqYr9ZiGswzTPtpXzw3h5lgqvZ8BkzpWrvnRWDmM+AgPbl4B0xe5+50x+pW9e4kPR0uVmJbDTtgCIs3StbAphpesLqxl9vnsD7OIRg3N6ytfPYMbt6cjJs3T2HvvcWKcI7PJg+TBjp59O9+bz4bkj8d7pjpkkITC8HpkjKWGBM0xrbzoXH1INRedk370FoXpOo0qbUu9RS1C1ZCXRdElxwgNTpVKYBO1cV16w7oVKD3FLWqrIDegTLaAHZ5paHl9RrRaCjFpnR6i0spLiVMhbZdBKE2aMXjkQZNSYVS0m0TruuFqSVNB5fBdGaSPWBje52mFUODsnKtPed5CEhrM7UWO93bOdQbac6Kh6Wwvd/NaXo6qJ7ZMYQiPU3p3cnEarXx7mTF6rDa07nZrWA1/NjuZIVefP3v/8mQ+Awa1NrVqFR19ZYakt2FIuipWqD3rlIrfTtHOYDdy0HMUgsdR60pelenQjoowG4OSiHTKiXVZ8dqgl2mQCor0OwAiKkwb1sAqPQhQB1IoCxM7xOkr5jYOxQo8YZCXwwGRunQh8aNdcIUyF0nyhjTd+Fp3ZMCuRmyYNsQhvBqsnu3C7DrEEHizLIu0OxOmN5xkGDThiA1wrUoW5uNw6WM+UafTUvjnsOAXBeIcCXdGtS99DwX11rc5nNqXXsYp3m6PV4+g1XHKAoFlKIUypTBrsIUgaBlp4TUsqochvomh5YgAVL2cCuwmlRmqzuFJZWqnoMxESuYWvRilQRKD3pgB1LTPEuZEaVChJStKAqkUjAYV3TxAVEhW5Bp5TUW1ipntlFwqnJmJQ1lpWVUQkIpq+W+U2vaqrAbX0au2D3DUeXeuqmip11KNaWbHuRG6cmm2yioVe49kwinO7LNSr24OhN6KldTGrzMD80lS2RWJnfZIFqXKWFq2J5dG26tQ5m83e4oJVxdy+1CJ1OBJvWlVlpqKBKcCKUgld1RYqJnU5mgz6jUSZkpOsTkyF0oVtA2qVhL4KAit6TEKkjISGWrERWSjKqiDFBYmZPUoKwiANJWBEqkiEVOEixEjhilz5cFdMql++xLwbBe8gtAMXhakPPCBuvCDAcUm27SMQqSXc6QVRIbK0qSMSaUklaMZEriHUkUZLLTDdVjvmKVcM6ZEXLBdmnZCZNhIpMV1G5D8TyTUXB3da3NeLJ7c+8FIocpynStslu6zmdf/KMZiejCSDUjt0SLHhiiWCR4JnKXEViEI3eSu9WxskuE5XNLBebzDCRFT9wiVAS2HBZdYttxD7G1SNOnxSwuSktaakFwYbrBlmaXYdqBwICQ5D6EFijk3BeGhS8r95KHhtrhoYUtcjzbNZiHLcmQRIkkbDcZ5HkDpUZSMLLVIp22IaPKMu5dLoVEOLsdjbToJPRZFnbImZM6FWOSeU7AysjEbk0Ziqy1295lsZMsJna2ZWNPhd4zyQaBMhuubfv5HzkJsGwX2wEpQSCECJyypZDVkczwY9nh0rwFAmGEWx80qWKh9Ss1dFtEHYTPJeAFiKEROAGVS+WYzwAZRTjKOE5JhO0xWwhJZIeMq5V6Z/GFxg6PlU32JboqGGRSuUhBOmwpEkcKpdTtHukuA2mpq2DPpEpk70rbvRS8PVcieqzIvlNmuqNIBSmpZydp7iYyKqmgZUYKOr33KHSSnFMdSkrRce9q2dPqQM6dHsWdiLQzYx1dL7/9r/6uQ6k3H+/5bz7pcF5r1RvbL8dfuCdf616Y9+oeDCS+DEiFhsFdmHk9OB2CoFh00b52kKbLLWlZOxdmS2l7Ya5kGy/r24iAtI2K7Y6H3A95QrKcNjaKxM4RWGSC0j2tkkm6IzLldHNK6pITOSUS1JF79t4xXSidI+oZY9yBzKSH3JX0WlY3X+PF3+bExa9d67V2XZvO33lafEHMVwRpaXMffgwb28vYWOZ9g0Apv+NM1Asz0cU67hcphy/mUoRJC2GMiGuS7LwSSdXLd7j80LK206C0InE3zrRqYhIXDT6rWzVBibsdpN3TyGCnNOQgxzsbJ+7d3WkysdMZNu7UoHwRNehN9FySaPqyrbH/wLrSGEFMUjHqO9kxvA4dp7FTaJjYYR2XiNIeggk4U7YlUk6jQNhkN8hSTeU2L2ys0BB0NORR6tiWBmt0KQ+2NKwm189JFTlGdssYheEiaFvmd1vWuLQCQ83EsmcLSRrWwJcar3hdAIdlGMrfR9YQFxal41wsh4wu5pJBHbelPZZsQ8buy3q5uAXjQSecHZzU3ebJn0R4Cd1zrFUsd6QCQVefqwgEfTC0b9TdokdRiKC10u1wnyMii4Jwc9guaYeiFewAh3GQrQw/nJGZRZExyjc9bILo4Z6yFNhhZwRdiLAdZA8CuWc4EfRALL4ayYTKeB/bYSNMQFP2lERIkpRyokHmXMyGY9DiNSiDFouRw0VoEfIrg+I34CH8qmvJRTH0qkVyXwRHXM57EUjYvuYt0ooRqDlHqCZwFbsTZ4C7FgkMeiFqpzdHEgvzEsLqHRGTYFIkkF3UomLLsRrJHl1aEUEr6lbUKeitOIjcIU8K0SPcIsnAlq1wz4AezgyKJFmkRwISqd4UUbouwhcXa7Gtsoe8L75TUYZg9l0Ko1IsFStHgWAILAHZFZkjMiojOnKRIqy+uSKmysWlIRc1JEeAPFzlNb+QpBcz25fMVXKZxsMjZpV7H7YyJmUUskTPTrj3xUSZlEhNaUk5wh5nppNVsZSp5l77zBmxU5DUkLp7BCl6UDzMYipkTRES9qxMZGaRIWzRc2hoxKLRVmBFSIFGBFIiCES1SERUItahuYclaQXuswZcKqFJGVYUDRseUKrlPkzayPcWs6245s/l63pxlTfmq9W5vMhTRqrhvmhCBDmno8hZEopMyoTH3GljXCY5neDhCDOdpByTTDZn11CjcEZRsoxCOJutYKj9UKqEblmusdyPZH+4Po0abNTIbN2om7RtMtdQPX+5jlVq7pSWGTlnkVqAIk24zwXPJSbLPYOeEWUEucS4784YZO4R4eIWwi52RoQD97AzCAcsJtEKgnBLKQhnDwnhCOOwHaYFSKUkKhFtk4tfgigX0ZGsGNFA7yQqVpIqNkmialVMH04ok8QyUkZZCNWVKrLCmV1Jyii6hrvP4QhlRB8SRM6bcKnqzpFiKGSp9N7UU05ypBVkqDUIqdtKrOHIGN8u6kiZVpOiD+ZlRrozHJAJJYRzaxPNjmoYjjFIapb+rJ1bEVmytxIlCxp+iJ6lwPBjc4ZGPFR67yX7XNQJVSLUi3cuacKR0VsPooRNdCsExVYhHXTJorj3sFRi7COcEpEl3cOdEA5JAVbrigHqklQklEtuOZhHGIUTh+k9pbSiJoHtlrRIup2Wywor1UmydZYoiSTlUR+LEXEVklY6GlpTUJZCz5Y5FE89d1qYuuSVnrvKyOeQOlanRg+7Z48+EpPocdGv2vq2J46uWlpx746exNTtzL6zV3vFnUy3ZiabFiNSzVGu6h1qXb/3Z1RJSyWk1Dl2XaPQvUuVMgyWlNF7it4ppQCp3ZwqK6kUyO5RHNhZXakSnVKa+nxR4G4xn1mlxKiVpkRPMeUomOUodvc5F194kRBamRIzxLQYxXmcK8aUjHLcKxZziWo9Qors1CgkGrHURTfdLNUbRzjbeCZCznkpjruN0hn23HH2kmUP3OVpCvcO9MyyV3DP7F2eamYmuGVSJ2O77Ie97dn70CpjR1wU1dMuyrpStrMOORnPLqWma4cyOzfhul4YuLbbplGrmOdwRKMqedp6095+V9t09ayQXQqoU1drlVq72ECZUlRBtfrWWpdQ06Iv2aUugSjRhkzUibJK9S7tdiutD1O9p3pfqc9dq9Vy1ESl952KK9RUn0NlNYRoUDzFupAdeubwjytG5aUsvFuu+w5Wk0d1pkiUcdpA1yiVlr7we2KU1aAMv2QStAorOiZsJtPDrDr0Ql2N3EFryBZWTUOQc89SwlHs1jROHqbwbLnWzHmDaYVSemJoti0lpFvC3mpOevFuC9N+UqsMs2vtSR8B7LzpYDxRvb83e7crrmWpDredX0y3gs2zLiioboIO2puY51E3DWZRYN42TQcTZ8+bDo5hbk2cTPSDLjVg6mJbYbJYQ29btSZNtUKm+g4aEtooCrTel2OlcW7ZGtSeI7fvukzBek81GnUNQaVtu3IpetfsV0eKvWbQ8RK9bDeFdV3Ot5YAp22gLsdO44DZbg1KhKHYbQO1MpK5Ti3NbQurdeamQXV6nidqxa1tqXXPnMltf4fPVskBMKe9X+wZkLPYnnMm1utkt3FZLQfFq5kyFavIu21Sy4HNc2uvevtyx3pVvdlUr1fdMLHZpNnD8xZ00Nk8AvaCqnr+8vTJrL2bIZ6v2T4Pre9C23bml03r40ZbrWE7S2WirGClc7WXx8SNnYjtAKrcmAWhnRqrhPnJcvLfZ83nwKppHmgItG5CIV0k/33WNA1Kzw1NE8zz9pI5BzeGCW0tNc+Fcs0smn6Jz2gLJsMvxiHuev/ceDmxP6jePRv4n90uB1SD4qyCeUeZqiHZnRamGkY75lPwYTUTzGdkWe2Y1tXe7sZR33ay6ZDVnDZWRz23L2D1dvX261Ov707ePN9jfXzq1XYypy9Zvzt58xD2vnea509g/zY+/+U+3DlHu9nMB1S99G63z24Hh281n37dBkxjf+2zqPKLMzuX47PNKXr0l//2e4d3B6zj7HzB17y9gAC+HUCoA2D/7TPOv93pEq5zcAZPBhBqoyPt3xkAp82z0bZ3GzabWWz22LsF2+0sLjFQA5fDFrbbpr2b63G7nbW+BnDabssVTGT7Wi69N5AFq1X19sX1njFwfbe+kn1vHo18bb2q1v541qvmzfM99m4usIfNMzbP9livJnN7efB8MsvGfbaMuw17ewM/w/45+/srn38L+3dWPvtmYJX275Q8fQwH+80cwuHh2icP5wUrs0Amb4CfD6jIjX+45y9/DvCC4/cHWOr5F3Dc933rhzt//vmbMJbw9POn6Olf/ptb17BevDz9Oo7fB64R5uRko3vfv4DuwUtucHJyLoB79661ny4ouLild9895ZQqvr5G+KNr6O2zC5zhTnAwAFnn82X/hSDZ02ulkEPOz7fXah7fsr9/Nebsm4NrY7/hYH9lDi6RjGPMgwMODsHZfHiwupr/8HRBqS1t3xtIvpcv9gxHcGMBKD1/Mvq/D8fHd/KL/36BXNtdItcAbv7w0Hw+oHS3Pjjwkwvr8Xj7CmqutVUOPOsjHr0L+V+OHXVgePPusbn3K/ovfh3SeAH91cOH//Lw3XffvcSaPv72+Rth8u/cB76Gb5680Ftvw7e8/Wtj3noL4KX47NX2Zy/O9PT27cv7Dz9cLj6/6B8Y1lu/9+zaQ7d4/ovzv+Z3F8+vLm9eNfnGnuEW3HptDw8ecPP43jUw8fh++jkLXHlINsDTp0+5ebxn7sCTz66AxdkP/Q7w9X3Ir14l6qNHj7h7d2lbAMX992+Z/7RQfMF+/qC/M8Z88OASW/zBg4/x93//VWDxZ7D7rQuA8KdXaPBP7vmTv8Ovov6//vviP/6z/R/844HQfvAadnfcP+D8fPT/1kL0+/c/fFVrPvnkNfz2xU8jfudy3Mfj10J/I7Rf+qPO3zPx/86f/a/K5S8pfnb9V1c/Xc4y9Xf+1dX/AnPjLE+UHZAHAAAAAElFTkSuQmCC";

        private PlayMakerFSM choices;
        private PlayMakerFSM arena;
        private string lastArenaState;
        private bool subscribed;
        private int generation;
        private Coroutine running;

        private GameObject warningRoot;
        private readonly SpriteRenderer[] sideRenderers = new SpriteRenderer[2];
        private readonly Transform[] sideTransforms = new Transform[2];
        private Material warningMaterial;
        private Texture2D warningTexture;
        private Sprite warningSprite;
        private bool warningActive;
        private bool visualLogged;
        private float warningStartTime;

        internal static void PatchBypass(Fsm fsm)
        {
            foreach (KeyValuePair<string, string> entry in Events)
            {
                // Hard P2 已被 HardBeamWall 改为私有随机激光，不能碰它的广播。
                if (IsP2State(entry.Key) && OrbRadiance.EffectiveHardMode) continue;
                string label = IsP2State(entry.Key) ? "p2beam" : "p1beam";
                FsmState state = fsm.GetState(entry.Key);
                if (state == null)
                {
                    OrbRadiance.Instance.LogWarn("[" + label + "] 缺少光墙状态 " + entry.Key);
                    continue;
                }

                int patched = 0;
                foreach (SendEventByName send in state.Actions.OfType<SendEventByName>())
                {
                    if (send == null || send.sendEvent == null) continue;
                    string current = send.sendEvent.Value;
                    if (current != entry.Value && current != BypassEvent) continue;
                    if (!OrbSpawner.OriginalSendEvent.ContainsKey(send))
                        OrbSpawner.OriginalSendEvent[send] = current;
                    send.sendEvent.Value = BypassEvent;
                    patched++;
                }
                if (patched == 0)
                {
                    OrbRadiance.Instance.LogWarn("[" + label + "] " + entry.Key + " 未找到原版广播：不接管该状态");
                    continue;
                }
                OrbRadiance.Instance.Log("[" + label + "] " + entry.Key + " 禁止原版直发，拦截动作数=" + patched);

                Wait wait = state.Actions.OfType<Wait>().FirstOrDefault(a => a != null && a.time != null);
                if (wait != null)
                {
                    if (!OriginalWait.ContainsKey(wait)) OriginalWait[wait] = wait.time.Value;
                    wait.time.Value = OriginalWait[wait] + WarningSeconds;
                }
                else OrbRadiance.Instance.LogWarn("[" + label + "] " + entry.Key + " 无 Wait，原攻击窗口未延长");
            }
        }

        private static bool IsP2State(string stateName)
        {
            return stateName == "Beam Sweep L 2" || stateName == "Beam Sweep R 2";
        }

        private static bool IsP2Event(string sweepEvent)
        {
            return sweepEvent == "BEAM SWEEP L2" || sweepEvent == "BEAM SWEEP R2";
        }

        internal void Init(PlayMakerFSM fsm)
        {
            choices = fsm;
            Instances.Add(this);
            lastArenaState = ArenaState();
            Subscribe();
        }

        private void OnEnable() { Subscribe(); }

        private void Subscribe()
        {
            if (subscribed || choices == null || choices.Fsm == null) return;
            choices.Fsm.StateChanged += OnStateChanged;
            subscribed = true;
        }

        private void OnStateChanged(FsmState state)
        {
            if (!OrbRadiance.Settings.Enabled || state == null) return;
            string sweepEvent;
            if (!Events.TryGetValue(state.Name, out sweepEvent)) return;
            if (IsP2State(state.Name) && OrbRadiance.EffectiveHardMode) return;
            if (!state.Actions.OfType<SendEventByName>().Any(a => a != null && a.sendEvent != null && a.sendEvent.Value == BypassEvent))
                return;

            CancelPending();
            lastArenaState = ArenaState();
            int ticket = generation;
            running = StartCoroutine(WarnThenSweep(sweepEvent, ticket));
        }

        private void Update()
        {
            if (warningActive) UpdateWarningVisual();
            if (running == null) return;

            if (!OrbRadiance.Settings.Enabled || (HeroController.instance != null && HeroController.instance.cState.dead))
            {
                CancelPending();
                return;
            }

            string arenaState = ArenaState();
            if (arenaState == null || arenaState == lastArenaState) return;
            lastArenaState = arenaState;
            if (arenaState == "Battle Start" || arenaState == "Refight" || arenaState == "Init" ||
                arenaState == "Setup" || arenaState == "Arena 2 Start" || arenaState == "Ascend Tele")
                CancelPending();
        }

        private string ArenaState()
        {
            if (arena == null)
            {
                Transform root = transform.parent;
                if (root != null)
                    arena = root.GetComponents<PlayMakerFSM>()
                        .FirstOrDefault(f => f != null && f.Fsm != null && f.FsmName == "Control");
            }
            return arena != null && arena.Fsm != null ? arena.Fsm.ActiveStateName : null;
        }

        private IEnumerator WarnThenSweep(string sweepEvent, int ticket)
        {
            yield return null;
            if (ticket != generation || !OrbRadiance.Settings.Enabled) yield break;

            string label = IsP2Event(sweepEvent) ? "p2beam" : "p1beam";
            float readyWaitStart = Time.time;
            string stateInfo;
            if (!BeamSweeperDual.CanTriggerSweep(out stateInfo))
                OrbRadiance.Instance.Log("[" + label + "] WAIT_IDLE t=" + Time.time.ToString("F3") +
                                         " pending=" + sweepEvent + " " + stateInfo);

            while (!BeamSweeperDual.CanTriggerSweep(out stateInfo))
            {
                if (ticket != generation || !OrbRadiance.Settings.Enabled) yield break;
                if (Time.time - readyWaitStart > 8f)
                {
                    OrbRadiance.Instance.LogWarn("[" + label + "] WAIT_IDLE_TIMEOUT " + sweepEvent +
                        " " + stateInfo + "，本次取消以免出现无对应攻击的假预警");
                    if (ticket == generation) running = null;
                    yield break;
                }
                yield return null;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (ticket != generation || !OrbRadiance.Settings.Enabled) yield break;

                if (EnsureWarningVisual())
                {
                    warningStartTime = Time.time;
                    warningActive = true;
                    visualLogged = false;
                    warningRoot.SetActive(true);
                    UpdateWarningVisual();
                    LogVisualDiagnostics();
                }
                else
                {
                    OrbRadiance.Instance.LogWarn("[" + label + "] 预警渲染创建失败，仍维持完整发射延迟");
                }

                OrbRadiance.Instance.Log("[" + label + "] WARNING_START t=" + Time.time.ToString("F3") +
                    " event=" + sweepEvent + " duration=" + WarningSeconds.ToString("F2") +
                    " visual=" + (warningActive ? "active" : "missing") + " source=embedded-png");
                EyeBeamTrack.PlayP1ColumnSound(false);

                yield return new WaitForEndOfFrame();
                if (ticket != generation || !OrbRadiance.Settings.Enabled) yield break;
                float visibleStart = Time.time;
                yield return new WaitForSeconds(WarningSeconds);
                if (ticket != generation || !OrbRadiance.Settings.Enabled) yield break;
                StopWarningVisual();

                if (BeamSweeperDual.TriggerSweep(sweepEvent))
                {
                    OrbRadiance.Instance.Log("[" + label + "] WARNING_FINISH t=" + Time.time.ToString("F3") +
                        " event=" + sweepEvent + " elapsed=" + (Time.time - visibleStart).ToString("F3") +
                        "s; 双向光墙已启动");
                    if (ticket == generation) running = null;
                    yield break;
                }

                OrbRadiance.Instance.LogWarn("[" + label + "] PREEMPTED，发射器被抢占，重新等待空闲后预警");
                float preemptedAt = Time.time;
                while (!BeamSweeperDual.CanTriggerSweep(out stateInfo))
                {
                    if (ticket != generation || !OrbRadiance.Settings.Enabled) yield break;
                    if (Time.time - preemptedAt > 8f)
                    {
                        OrbRadiance.Instance.LogWarn("[" + label + "] PREEMPTED_TIMEOUT " + sweepEvent +
                            " " + stateInfo + "，本次取消");
                        if (ticket == generation) running = null;
                        yield break;
                    }
                    yield return null;
                }
            }

            OrbRadiance.Instance.LogWarn("[" + label + "] 多次重试后仍无法发射：" + sweepEvent);
            if (ticket == generation) running = null;
        }

        private bool EnsureWarningVisual()
        {
            if (warningRoot != null && sideRenderers[0] != null && sideRenderers[1] != null && warningSprite != null)
                return true;

            DestroyWarningVisual();
            try
            {
                CreateWarningSprite();
                CreateWarningRoot();
                OrbRadiance.Instance.Log("[p1beam] WARNING_VISUAL_READY nativeAtlas=False source=embedded-right-png texture=" +
                    (warningTexture != null ? warningTexture.width + "x" + warningTexture.height : "<null>") +
                    " rotatedSprite=" + (warningSprite != null ? warningSprite.bounds.size.ToString() : "<null>"));
                return true;
            }
            catch (Exception e)
            {
                OrbRadiance.Instance.LogWarn("[p1beam] 创建嵌入 PNG 预警失败：" + e);
                DestroyWarningVisual();
                return false;
            }
        }

        private void CreateWarningSprite()
        {
            byte[] pngBytes = Convert.FromBase64String(EmbeddedWarningPngBase64);
            Texture2D sourceTexture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            sourceTexture.name = "P1 warning source strip";
            if (!sourceTexture.LoadImage(pngBytes, false))
                throw new InvalidOperationException("LoadImage 返回 false");
            // 提升有颜色的像素的透明度，避免红色背景透出；RGB 不变，全透明像素仍透明。
            Color[] pixels = sourceTexture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a <= 0f) continue;
                pixels[i].a = Mathf.Sqrt(pixels[i].a);
            }
            sourceTexture.SetPixels(pixels);
            sourceTexture.Apply(false, false);
            sourceTexture.wrapMode = TextureWrapMode.Clamp;
            sourceTexture.filterMode = FilterMode.Bilinear;

            warningTexture = RotateTextureClockwise(sourceTexture);
            warningTexture.name = "P1 warning vertical strip";
            Color sampleColor = warningTexture.GetPixel(warningTexture.width / 2, warningTexture.height / 2);
            OrbRadiance.Instance.Log("[p1beam] PNG_COLOR_CHECK rgba=" + sampleColor +
                " size=" + warningTexture.width + "x" + warningTexture.height +
                " materialTint=white");
            warningTexture.wrapMode = TextureWrapMode.Clamp;
            warningTexture.filterMode = FilterMode.Bilinear;
            Destroy(sourceTexture);

            warningSprite = Sprite.Create(
                warningTexture,
                new Rect(0f, 0f, warningTexture.width, warningTexture.height),
                new Vector2(0.5f, 0.5f),
                32f,
                0,
                SpriteMeshType.FullRect);
            warningSprite.name = "P1 warning vertical strip sprite";
        }

        private void CreateWarningRoot()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            if (shader == null) throw new InvalidOperationException("找不到可用的透明精灵 Shader");

            warningMaterial = new Material(shader);
            warningMaterial.name = "P1 warning embedded png material";
            warningMaterial.mainTexture = warningTexture;
            warningMaterial.color = Color.white;
            warningMaterial.renderQueue = 3100;

            warningRoot = new GameObject("P1 beam warning columns (embedded PNG)");
            warningRoot.layer = 0;
            warningRoot.SetActive(false);

            for (int i = 0; i < 2; i++)
            {
                GameObject child = new GameObject(i == 0 ? "left warning column" : "right warning column");
                child.layer = 0;
                child.transform.SetParent(warningRoot.transform, false);

                SpriteRenderer sr = child.AddComponent<SpriteRenderer>();
                sr.sprite = warningSprite;
                sr.sharedMaterial = warningMaterial;
                SortingLayer[] layers = SortingLayer.layers;
                if (layers != null && layers.Length > 0)
                    sr.sortingLayerID = layers[layers.Length - 1].id;
                sr.sortingOrder = 3000 + i;
                sr.enabled = false;

                sideTransforms[i] = child.transform;
                sideRenderers[i] = sr;
            }
        }

        private void UpdateWarningVisual()
        {
            if (!warningActive || warningRoot == null || warningSprite == null) return;

            Camera cam = GameCameras.instance != null ? GameCameras.instance.mainCamera : Camera.main;
            float centerY = cam != null ? cam.transform.position.y :
                (HeroController.instance != null ? HeroController.instance.transform.position.y : 30f);
            float height = cam != null && cam.orthographic ? cam.orthographicSize * 2f + ExtraHeight : 32f;
            warningRoot.transform.position = new Vector3(CenterX, centerY, -2f);

            Vector2 spriteSize = warningSprite.bounds.size;
            float baseWidth = Mathf.Max(0.01f, spriteSize.x);
            float baseHeight = Mathf.Max(0.01f, spriteSize.y);
            bool bright = Mathf.Repeat(Time.time - warningStartTime, BlinkPeriod) < BlinkBrightTime;
            float alpha = bright ? 1f : 0.16f;

            for (int i = 0; i < 2; i++)
            {
                if (sideTransforms[i] == null || sideRenderers[i] == null) continue;
                sideTransforms[i].localPosition = new Vector3(i == 0 ? -EdgeOffset : EdgeOffset, 0f, 0f);
                sideTransforms[i].localRotation = Quaternion.identity;
                sideTransforms[i].localScale = new Vector3(EdgeWidth / baseWidth, height / baseHeight, 1f);
                sideRenderers[i].enabled = true;
                sideRenderers[i].color = new Color(1f, 1f, 1f, alpha);
            }
        }

        private void LogVisualDiagnostics()
        {
            if (visualLogged || warningRoot == null) return;
            visualLogged = true;
            Camera cam = GameCameras.instance != null ? GameCameras.instance.mainCamera : Camera.main;
            if (cam == null)
            {
                OrbRadiance.Instance.LogWarn("[p1beam] VISUAL_CHECK camera=<null>");
                return;
            }

            Vector3 left = cam.WorldToViewportPoint(new Vector3(CenterX - EdgeOffset, warningRoot.transform.position.y, -2f));
            Vector3 right = cam.WorldToViewportPoint(new Vector3(CenterX + EdgeOffset, warningRoot.transform.position.y, -2f));
            bool isVisibleLayer = (cam.cullingMask & 1) != 0;
            bool inside = left.z > 0f && right.z > 0f &&
                          left.x >= 0f && left.x <= 1f && right.x >= 0f && right.x <= 1f &&
                          left.y >= 0f && left.y <= 1f;
            OrbRadiance.Instance.Log("[p1beam] VISUAL_CHECK cam=" + cam.name +
                " cameraZ=" + cam.transform.position.z.ToString("F2") +
                " layer0Visible=" + isVisibleLayer + " inViewport=" + inside +
                " leftX=" + left.x.ToString("F3") + " rightX=" + right.x.ToString("F3") +
                " pngWidth=" + (warningTexture != null ? warningTexture.width.ToString() : "<null>") +
                " pngHeight=" + (warningTexture != null ? warningTexture.height.ToString() : "<null>") +
                " rootActive=" + warningRoot.activeInHierarchy +
                " rendererEnabled=" + (sideRenderers[0] != null && sideRenderers[0].enabled));
        }

        private static Texture2D RotateTextureClockwise(Texture2D src)
        {
            int width = src.width;
            int height = src.height;
            Texture2D dst = new Texture2D(height, width, TextureFormat.ARGB32, false);
            Color[] srcPixels = src.GetPixels();
            Color[] dstPixels = new Color[srcPixels.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = y * width + x;
                    int nx = height - 1 - y;
                    int ny = x;
                    int dstIndex = ny * height + nx;
                    dstPixels[dstIndex] = srcPixels[srcIndex];
                }
            }
            dst.SetPixels(dstPixels);
            dst.Apply(false, false);
            return dst;
        }

        private void StopWarningVisual()
        {
            warningActive = false;
            if (warningRoot != null) warningRoot.SetActive(false);
        }

        private void DestroyWarningVisual()
        {
            StopWarningVisual();
            if (warningRoot != null) Destroy(warningRoot);
            warningRoot = null;
            for (int i = 0; i < sideRenderers.Length; i++)
            {
                sideRenderers[i] = null;
                sideTransforms[i] = null;
            }
            if (warningMaterial != null) Destroy(warningMaterial);
            warningMaterial = null;
            if (warningSprite != null) Destroy(warningSprite);
            warningSprite = null;
            if (warningTexture != null) Destroy(warningTexture);
            warningTexture = null;
            visualLogged = false;
        }

        private void CancelPending()
        {
            generation++;
            if (running != null)
            {
                StopCoroutine(running);
                running = null;
            }
            StopWarningVisual();
        }

        internal static void RestoreAll()
        {
            foreach (KeyValuePair<Wait, float> pair in OriginalWait)
                if (pair.Key != null && pair.Key.time != null) pair.Key.time.Value = pair.Value;
            OriginalWait.Clear();
            foreach (P1BeamWallWarning instance in Instances.ToArray())
            {
                if (instance == null) continue;
                instance.CancelPending();
                instance.DestroyWarningVisual();
            }
        }

        private void OnDisable()
        {
            if (subscribed && choices != null && choices.Fsm != null)
                choices.Fsm.StateChanged -= OnStateChanged;
            subscribed = false;
            CancelPending();
            DestroyWarningVisual();
        }

        private void OnDestroy()
        {
            OnDisable();
            Instances.Remove(this);
        }
    }
}

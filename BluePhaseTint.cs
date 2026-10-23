using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OrbRadiance
{
    // H 难度 P2 已开始且实际 HP < 1100 时，只给 GG_Radiance 场景布景加深蓝滤镜。
    internal sealed class BluePhaseTint : MonoBehaviour
    {
        private const string P2StartState = "Arena 2 Start";
        private const string P3StartState = "Ascend Tele";
        private const string P4StartState = "Scream";
        private static readonly string[] ResetStates = { "Battle Start", "Refight", "Init", "Setup" };
        private const float FadeInSeconds = 2f;
        private const float FadeOutSeconds = 2f;
        // 修改这里的 RGB/255 分量即可改色；当前 (0, 74, 255) 对应 #004AFF，alpha 保持 1。
        private static readonly Color Blue = new Color(0f, 74f / 255f, 255f / 255f, 1f);
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly string[] EnvironmentPaths =
        {
            "GG_Arena_Prefab",
            "TileMap Render Data/Scenemap",
            "gg_aerial", "gg_aerial (1)", "gg_aerial (2)", "gg_aerial (3)", "gg_aerial (4)",
            "gg_pillar_corner_large", "gg_pillar_corner_large (1)", "GG_pillar_top",
            "Boss Control/Plat Sets",
            "Boss Control/H P2 Orb Attack Platforms",
            "Boss Control/Ascend Respawns/Shades",
            "Boss Control/Godseeker Crowd",
            "Boss Control/Sun",
            "_Scenery", "_Props"
        };

        private sealed class RendererState
        {
            internal Renderer renderer;
            internal MaterialPropertyBlock originalBlock;
            internal MaterialPropertyBlock workingBlock;
            internal readonly Dictionary<int, Color> originalColors = new Dictionary<int, Color>();
            internal MeshFilter meshFilter;
            internal Mesh originalMesh;
            internal Mesh tintedMesh;
            internal Color[] originalVertexColors;
            internal Color[] tintedVertexColors;
        }

        private readonly Dictionary<Renderer, RendererState> saved = new Dictionary<Renderer, RendererState>();
        private PlayMakerFSM control;
        private PlayMakerFSM arena;
        private HealthManager health;
        private bool controlSubscribed;
        private bool arenaSubscribed;
        private bool p2Started;
        private bool p3FadeOut;
        private bool targetsCaptured;
        private float tintIntensity;
        private static BluePhaseTint active;

        internal void Init(PlayMakerFSM fsm)
        {
            control = fsm;
            health = fsm != null ? fsm.GetComponent<HealthManager>() : null;
            active = this;
            Subscribe();
            HandleControlState(control != null && control.Fsm != null ? control.Fsm.ActiveStateName : null);
        }

        internal static void Restore()
        {
            if (active != null) active.RestoreTint();
        }

        private void OnEnable() => Subscribe();

        private void Subscribe()
        {
            if (!controlSubscribed && control != null && control.Fsm != null)
            {
                control.Fsm.StateChanged += OnControlStateChanged;
                controlSubscribed = true;
            }

            if (arenaSubscribed || control == null) return;
            Transform bossControl = control.transform.parent;
            if (bossControl == null) return;
            foreach (PlayMakerFSM fsm in bossControl.GetComponents<PlayMakerFSM>())
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
            p2Started = false;
            p3FadeOut = false;
            if (controlSubscribed && control != null && control.Fsm != null)
                control.Fsm.StateChanged -= OnControlStateChanged;
            controlSubscribed = false;
            if (arenaSubscribed && arena != null && arena.Fsm != null)
                arena.Fsm.StateChanged -= OnArenaStateChanged;
            arenaSubscribed = false;
            RestoreTint();
        }

        private void OnDestroy()
        {
            OnDisable();
            if (active == this) active = null;
        }

        private void OnControlStateChanged(FsmState state)
        {
            if (state != null) HandleControlState(state.Name);
        }

        private void HandleControlState(string state)
        {
            if (state == P2StartState)
            {
                p2Started = true;
                p3FadeOut = false;
            }
            else if (state == P3StartState)
            {
                BluePhaseBindings.ClearActivePhase();
                p2Started = false;
                p3FadeOut = true;
            }
            else if (state == P4StartState || Array.IndexOf(ResetStates, state) >= 0)
            {
                BluePhaseBindings.ClearActivePhase();
                p2Started = false;
                p3FadeOut = false;
                RestoreTint();
            }
        }

        private void OnArenaStateChanged(FsmState state)
        {
            if (state == null) return;
            if (Array.IndexOf(ResetStates, state.Name) >= 0)
            {
                BluePhaseBindings.ClearActivePhase();
                p2Started = false;
                p3FadeOut = false;
                RestoreTint();
            }
        }

        private void Update()
        {
            HeroController hero = HeroController.instance;
            if ((health != null && health.hp <= 0) || (hero != null && hero.cState.dead))
            {
                p2Started = false;
                p3FadeOut = false;
                RestoreTint();
                return;
            }
            UpdateTintState();
        }

        private void UpdateTintState()
        {
            bool shouldTint = OrbRadiance.Settings.Enabled && OrbRadiance.EffectiveHardMode && p2Started &&
                              control != null && control.Fsm != null && control.Fsm.ActiveStateName != P3StartState &&
                              control.Fsm.ActiveStateName != P4StartState && health != null && health.hp > 0 && health.hp < 1100 &&
                              control.gameObject.scene.name == "GG_Radiance";
            if (shouldTint)
            {
                p3FadeOut = false;
                if (!targetsCaptured) CaptureEnvironment();
                float nextIntensity = Mathf.MoveTowards(tintIntensity, 1f, Time.deltaTime / FadeInSeconds);
                if (nextIntensity != tintIntensity)
                {
                    tintIntensity = nextIntensity;
                    ApplyIntensity(tintIntensity);
                }
                return;
            }

            if (p3FadeOut)
            {
                if (!targetsCaptured)
                {
                    p3FadeOut = false;
                    return;
                }
                float nextIntensity = Mathf.MoveTowards(tintIntensity, 0f, Time.deltaTime / FadeOutSeconds);
                if (nextIntensity != tintIntensity)
                {
                    tintIntensity = nextIntensity;
                    ApplyIntensity(tintIntensity);
                }
                if (tintIntensity <= 0f)
                {
                    p3FadeOut = false;
                    RestoreTint();
                }
                return;
            }

            RestoreTint();
        }

        private void CaptureEnvironment()
        {
            // 蓝调计时与淡入第一帧同一触发点开始。
            BluePhaseBindings.BeginActivePhase();
            int changed = 0;
            var scene = control.gameObject.scene;
            GameObject[] sceneRoots = scene.GetRootGameObjects();
            foreach (string path in EnvironmentPaths)
            {
                GameObject root = FindEnvironmentRoot(path, sceneRoots);
                if (root == null) continue;
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || renderer.gameObject.scene != scene || IsExcluded(renderer)) continue;
                    RendererState state = Capture(renderer);
                    if (state == null) continue;
                    changed++;
                }
            }
            targetsCaptured = true;
            if (OrbRadiance.Instance != null)
                OrbRadiance.Instance.Log("[blue-phase] H P2.5 深蓝渐变已开始，环境Renderer=" + changed);
        }

        private static GameObject FindEnvironmentRoot(string path, GameObject[] sceneRoots)
        {
            string[] parts = path.Split('/');
            foreach (GameObject sceneRoot in sceneRoots)
            {
                if (sceneRoot == null || sceneRoot.name != parts[0]) continue;
                Transform node = sceneRoot.transform;
                for (int i = 1; i < parts.Length && node != null; i++)
                    node = node.Find(parts[i]);
                if (node != null) return node.gameObject;
            }
            return null;
        }

        private RendererState Capture(Renderer renderer)
        {
            RendererState state;
            if (saved.TryGetValue(renderer, out state)) return state;

            state = new RendererState
            {
                renderer = renderer,
                originalBlock = new MaterialPropertyBlock(),
                workingBlock = new MaterialPropertyBlock()
            };
            renderer.GetPropertyBlock(state.originalBlock);
            renderer.GetPropertyBlock(state.workingBlock);
            Material[] materials = renderer.sharedMaterials;
            bool hasColor = false;
            foreach (Material material in materials)
            {
                if (material == null) continue;
                if (material.HasProperty(ColorId))
                {
                    hasColor = true;
                    if (!state.originalColors.ContainsKey(ColorId)) state.originalColors[ColorId] = material.GetColor(ColorId);
                }
                if (material.HasProperty(BaseColorId))
                {
                    hasColor = true;
                    if (!state.originalColors.ContainsKey(BaseColorId)) state.originalColors[BaseColorId] = material.GetColor(BaseColorId);
                }
            }
            if (!hasColor)
            {
                MeshRenderer meshRenderer = renderer as MeshRenderer;
                MeshFilter filter = meshRenderer != null ? meshRenderer.GetComponent<MeshFilter>() : null;
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh != null)
                {
                    state.meshFilter = filter;
                    state.originalMesh = mesh;
                    state.originalVertexColors = mesh.colors;
                    if (state.originalVertexColors.Length == 0)
                    {
                        state.originalVertexColors = new Color[mesh.vertexCount];
                        for (int i = 0; i < state.originalVertexColors.Length; i++)
                            state.originalVertexColors[i] = Color.white;
                    }
                    else if (state.originalVertexColors.Length != mesh.vertexCount)
                    {
                        state.meshFilter = null;
                        state.originalMesh = null;
                        state.originalVertexColors = null;
                        return null;
                    }
                    state.tintedVertexColors = new Color[state.originalVertexColors.Length];
                    state.tintedMesh = Instantiate(mesh);
                }
            }

            if (state.originalColors.Count == 0 && state.tintedMesh == null) return null;
            saved.Add(renderer, state);
            return state;
        }

        private void ApplyIntensity(float intensity)
        {
            foreach (RendererState state in saved.Values)
                ApplyIntensity(state, intensity);
        }

        private void ApplyIntensity(RendererState state, float intensity)
        {
            Renderer renderer = state.renderer;
            if (renderer == null) return;
            Color tint = Color.Lerp(Color.white, Blue, intensity);
            foreach (KeyValuePair<int, Color> color in state.originalColors)
            {
                Color original = color.Value;
                Color blockColor = state.originalBlock.GetColor(color.Key);
                if (blockColor != Color.clear) original = blockColor;
                state.workingBlock.SetColor(color.Key, original * tint);
            }
            if (state.originalColors.Count > 0) renderer.SetPropertyBlock(state.workingBlock);
            if (state.tintedMesh != null && state.meshFilter != null)
            {
                for (int i = 0; i < state.originalVertexColors.Length; i++)
                    state.tintedVertexColors[i] = state.originalVertexColors[i] * tint;
                state.tintedMesh.colors = state.tintedVertexColors;
                state.meshFilter.sharedMesh = state.tintedMesh;
            }
        }

        internal static void RegisterTemporaryTerrain(Renderer renderer)
        {
            if (active == null || renderer == null || !active.targetsCaptured || active.control == null ||
                renderer.gameObject.scene != active.control.gameObject.scene || active.IsExcluded(renderer)) return;
            RendererState state = active.Capture(renderer);
            if (state != null && active.tintIntensity > 0f) active.ApplyIntensity(state, active.tintIntensity);
        }

        internal static void UnregisterTemporaryTerrain(Renderer renderer)
        {
            if (active == null || renderer == null) return;
            RendererState state;
            if (!active.saved.TryGetValue(renderer, out state)) return;
            active.RestoreState(state);
            active.saved.Remove(renderer);
        }

        private void RestoreState(RendererState state)
        {
            if (state.renderer != null) state.renderer.SetPropertyBlock(state.originalBlock);
            if (state.meshFilter != null && state.originalMesh != null) state.meshFilter.sharedMesh = state.originalMesh;
            if (state.tintedMesh != null) Destroy(state.tintedMesh);
        }

        private bool IsExcluded(Renderer renderer)
        {
            if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer) return true;
            for (Transform node = renderer.transform; node != null; node = node.parent)
            {
                if (node.GetComponent<DamageHero>() != null || node.GetComponent<HeroController>() != null ||
                    node.GetComponent<ParticleSystem>() != null || node.GetComponent<Canvas>() != null)
                    return true;
            }
            if (control != null && renderer.transform.IsChildOf(control.transform)) return true;
            HeroController hero = HeroController.instance;
            if (hero != null && renderer.transform.IsChildOf(hero.transform)) return true;
            return false;
        }

        private void RestoreTint()
        {
            p3FadeOut = false;
            if (!targetsCaptured && saved.Count == 0 && tintIntensity <= 0f) return;
            foreach (RendererState state in saved.Values)
                RestoreState(state);
            int restored = saved.Count;
            saved.Clear();
            targetsCaptured = false;
            tintIntensity = 0f;
            if (restored > 0 && OrbRadiance.Instance != null)
                OrbRadiance.Instance.Log("[blue-phase] 环境原色已恢复，Renderer=" + restored);
        }
    }
}

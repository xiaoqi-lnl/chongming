using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OrbRadiance
{
    internal static class WorkshopRoomAssets
    {
        internal const string StatuePath = "GG_Statue_Radiance";
        internal const string VisibleStatuePath = "GG_statues_0006_5 (1)";
        internal const string AmbientParticleGroupPath = "gg_particles_standard/white_palace_particles (1)";
        internal const string LeftWallArchPath = "gg_arch (1)/GameObject (1)/GG_scenery_0000_2 (29)";
        internal const string BlurPlanePath = "BlurPlane";
        internal const float StatueSpotlightIntensity = 6f;
        private static readonly string[] DecorationPaths =
        {
            "gg_arch (1)/GameObject (1)/GG_scenery_0000_2 (28)", LeftWallArchPath, "gg_ascent_backing_0000_2 (15)", "GG_scenery_0014_7 (121)",
            "gg_ascent_backing_0001_1 (66)", "GG_scenery_0014_7 (72)", "gg_ascent_backing_0000_2 (13)", "gg_radiant_gate (28)",
            "GG_scene_arena_extra_0000_3_short (13)", "gg_arch (1)/GameObject (1)/GG_scenery_0001_1 (30)", "gg_ascent_backing_0001_1 (61)",
            "gg_ascent_backing_0001_1 (67)", "gg_ascent_backing_0001_1 (63)", "GG_gold_wall (28)", "gg_arch (1)/GameObject (1)/GG_scenery_0001_1 (31)",
            "GG_scenery_0014_7 (119)", "gg_ascent_backing_0000_2 (18)", "gg_radiant_gate (20)", "gg_ascent_backing_0001_1 (60)",
            "gg_ascent_backing_0001_1 (65)", "gg_radiant_gate (23)", "GG_scenery_0014_7 (118)", "GG_scenery_0017_4 (67)",
            "gg_radiant_gate (19)", "gg_arch (1)/GameObject/GG_scenery_0000_2 (25)", "gg_radiant_gate (17)", "GG_gold_wall (32)",
            "gg_radiant_gate (24)", "gg_radiant_gate (29)", "GG_gold_wall (29)", "gg_ascent_backing_0000_2 (14)", "GG_scenery_0014_7 (25)",
            "gg_arch (1)/GameObject/GG_scenery_0000_2 (29)", "gg_arch (1)/GameObject/GG_scenery_0001_1 (32)", "GG_gold_wall (31)",
            "GG_scenery_0014_7 (117)", "gg_ascent_backing_0000_2 (17)", "gg_ascent_backing_0000_2 (19)", "gg_radiant_gate (16)",
            "gg_ascent_backing_0001_1 (62)", "gg_radiant_gate (27)", "GG_scenery_0014_7 (75)", "GG_gold_wall (30)",
            "gg_ascent_backing_0000_2 (12)", "gg_radiant_gate (22)", "GG_scenery_0014_7 (122)", "gg_ascent_backing_0001_1 (64)",
            "gg_arch (1)/GameObject/GG_scenery_0001_1 (29)", "gg_ascent_backing_0000_2 (16)", "GG_scenery_0014_7 (120)",
            "GG_scene_arena_extra_0000_3_short (5)", "GG_gold_wall (33)", "GG_scenery_0011_10 (8)", "GG_scenery_0009_12 (1)", "GG_scenery_0010_11 (1)", "haze2", "black_fader (6)",
            "dream_beam_animation/dream_nail_base", "dream_beam_animation/dream_beam",
            "dream_beam_animation/dream_beam (1)", "dream_beam_animation/cd_room_beam_glow"
        };

        private static Dictionary<string, GameObject> objects;
        private static readonly List<string> availableDecorationPaths = new List<string>();
        internal static GameObject WallPrefab { get; private set; }
        internal static GameObject StatuePrefab { get; private set; }
        internal static SpriteRenderer VisibleStatueRenderer { get; private set; }
        internal static Mesh BlurPlaneMesh { get; private set; }
        internal static Material BlurPlaneMaterial { get; private set; }
        internal static GameObject BlurPlaneSource { get; private set; }
        internal static GameObject AmbientParticleGroup { get; private set; }
        internal static string AmbientParticleGroupLabel { get; private set; }
        internal static string BlurPlaneSourceLabel { get; private set; }
        internal static bool Ready { get; private set; }
        internal static readonly float[] SourceAnchor = { 250.05000305f, 36.49000168f, .47999999f };
        internal static readonly float[] NewAnchor = { 309.91125f, 36.49000168f, .47999999f };
        internal static float DecorationOffsetX => NewAnchor[0] - SourceAnchor[0];
        internal static int AvailableDecorationCount => availableDecorationPaths.Count;
        internal static IEnumerable<string> RequiredDecorationPaths => availableDecorationPaths;
        internal static IEnumerable<string> RequiredPaths
        {
            get
            {
                yield return "Breakable Wall_Silhouette";
                yield return StatuePath;
                yield return VisibleStatuePath;
                yield return BlurPlanePath;
                yield return "gg_particles_standard";
                var yieldedDecorationRoots = new HashSet<string>(StringComparer.Ordinal);
                foreach (string path in DecorationPaths)
                {
                    int slash = path.IndexOf('/');
                    string preloadPath = slash < 0 ? path : path.Substring(0, slash);
                    if (yieldedDecorationRoots.Add(preloadPath)) yield return preloadPath;
                }
            }
        }

        internal static bool Configure(Dictionary<string, Dictionary<string, GameObject>> preloaded, Action<string> warn)
        {
            availableDecorationPaths.Clear();
            objects = preloaded != null && preloaded.TryGetValue("GG_Workshop", out var scene) ? scene : null;
            WallPrefab = Find("Breakable Wall_Silhouette");
            StatuePrefab = Find(StatuePath);
            VisibleStatueRenderer = GetRenderer(VisibleStatuePath);
            BindBlurPlaneSource(Find(BlurPlanePath), "preload", warn);
            AmbientParticleGroup = Find(AmbientParticleGroupPath);
            ParticleSystem[] ambientSystems = AmbientParticleGroup == null ? Array.Empty<ParticleSystem>() : AmbientParticleGroup.GetComponentsInChildren<ParticleSystem>(true);
            bool coreReady = WallPrefab != null && StatuePrefab != null && StatuePrefab.GetComponent<BossStatue>() != null &&
                VisibleStatueRenderer != null && VisibleStatueRenderer.sprite != null;
            AmbientParticleGroupLabel = AmbientParticleGroup == null ? null : "preload";
            if (ambientSystems.Length != 3) warn?.Invoke("神居区域环境粒子预加载缺失或系统数量异常：" + ambientSystems.Length);
            foreach (string path in DecorationPaths)
            {
                GameObject item = Find(path);
                if (item == null || item.GetComponentInChildren<SpriteRenderer>(true) == null || item.GetComponentInChildren<SpriteRenderer>(true).sprite == null)
                { warn?.Invoke("神居静态装饰不可用，略过：" + path); continue; }
                availableDecorationPaths.Add(path);
            }
            if (WallPrefab == null) warn?.Invoke("辐光雕像房间隐藏墙预加载缺失");
            if (StatuePrefab == null || StatuePrefab.GetComponent<BossStatue>() == null) warn?.Invoke("GG_Statue_Radiance 雕像预加载缺失 BossStatue");
            if (VisibleStatueRenderer == null || VisibleStatueRenderer.sprite == null) warn?.Invoke("GG_statues_0006_5 (1) 可见雕像贴图预加载缺失");
            Ready = coreReady;
            return Ready;
        }

        internal static bool ResolveBlurPlaneForScene(Scene scene, Action<string> warn)
        {
            if (HasUsableBlurPlane())
            {
                BindBlurPlaneSource(BlurPlaneSource, "preload-preserved", warn);
                return true;
            }
            Transform liveSource = scene.IsValid() ? WorkshopTerrainPatch.FindPath(scene, BlurPlanePath) : null;
            BindBlurPlaneSource(liveSource == null ? null : liveSource.gameObject, "live-exact-scene-path", warn);
            return HasUsableBlurPlane();
        }

        internal static GameObject ResolveAmbientParticleGroupForScene(Scene scene, Action<string> warn)
        {
            if (AmbientParticleGroup != null && AmbientParticleGroup.GetComponentsInChildren<ParticleSystem>(true).Length == 3)
                return AmbientParticleGroup;
            Transform root = scene.IsValid() ? WorkshopTerrainPatch.FindPath(scene, "gg_particles_standard") : null;
            Transform live = root == null ? null : root.Find("white_palace_particles (1)");
            GameObject candidate = live == null ? null : live.gameObject;
            int count = candidate == null ? 0 : candidate.GetComponentsInChildren<ParticleSystem>(true).Length;
            if (count == 3)
            {
                AmbientParticleGroup = candidate;
                AmbientParticleGroupLabel = "live-exact-scene-path";
                OrbRadiance.Instance?.Log("HiddenRoom: 已从当前场景精确路径恢复原生环境粒子组");
                return candidate;
            }
            warn?.Invoke("HiddenRoom: 神居环境粒子组不可用，房间仍可安装；系统数量=" + count);
            AmbientParticleGroup = null;
            AmbientParticleGroupLabel = null;
            return null;
        }

        private static bool HasUsableBlurPlane() => BlurPlaneSource != null && BlurPlaneMesh != null && BlurPlaneMaterial != null;

        private static void BindBlurPlaneSource(GameObject source, string label, Action<string> warn)
        {
            BlurPlaneSource = source;
            BlurPlaneSourceLabel = label;
            MeshFilter filter = source == null ? null : source.GetComponent<MeshFilter>();
            MeshRenderer renderer = source == null ? null : source.GetComponent<MeshRenderer>();
            BlurPlane nativeBlur = source == null ? null : source.GetComponent<BlurPlane>();
            BlurPlaneMesh = filter == null ? null : filter.sharedMesh;
            BlurPlaneMaterial = nativeBlur != null && nativeBlur.OriginalMaterial != null
                ? nativeBlur.OriginalMaterial
                : (renderer == null ? null : renderer.sharedMaterial);
            string detail = DescribeBlurPlaneSource(label, source, filter, renderer, nativeBlur);
            OrbRadiance.Instance?.Log("HiddenRoomProbe: " + detail);
            if (!HasUsableBlurPlane())
                warn?.Invoke("HiddenRoomProbe: BlurPlane source 缺少 MeshFilter/sharedMesh 或可用材质；" + detail);
        }

        private static string DescribeBlurPlaneSource(string label, GameObject source, MeshFilter filter, MeshRenderer renderer, BlurPlane nativeBlur)
        {
            string currentMaterial = DescribeMaterial(renderer == null ? null : renderer.sharedMaterial);
            string originalMaterial = DescribeMaterial(nativeBlur == null ? null : nativeBlur.OriginalMaterial);
            return "BlurPlane source=" + label + " exists=" + (source != null) +
                " preloadKeyPresent=" + (objects != null && objects.ContainsKey(BlurPlanePath)) +
                " managedReferenceNull=" + object.ReferenceEquals(source, null) +
                " name=" + (source == null ? "<null>" : source.name) +
                " layer=" + (source == null ? -1 : source.layer) +
                " active=" + (source != null && source.activeInHierarchy) +
                " MeshFilter=" + (filter != null) +
                " sharedMesh=" + (filter == null || filter.sharedMesh == null ? "<null>" : filter.sharedMesh.name) +
                " MeshRenderer=" + (renderer != null) + " currentMaterial=" + currentMaterial +
                " BlurPlane=" + (nativeBlur != null) + " OriginalMaterial=" + originalMaterial +
                " selectedMaterial=" + DescribeMaterial(BlurPlaneMaterial);
        }

        private static string DescribeMaterial(Material material)
        {
            if (material == null) return "<null>";
            Shader shader = material.shader;
            return material.name + "[shader=" + (shader == null ? "<null>" : shader.name) +
                ",supported=" + (shader != null && shader.isSupported) + ",queue=" + material.renderQueue +
                ",mainTexture=" + DescribeTexture(material.mainTexture) + "]";
        }

        private static string DescribeTexture(Texture texture)
        {
            if (texture == null) return "<null>";
            RenderTexture renderTexture = texture as RenderTexture;
            return texture.GetType().Name + ":" + texture.name + "[" + texture.width + "x" + texture.height +
                (renderTexture == null ? "" : ",created=" + renderTexture.IsCreated()) + "]";
        }

        internal static GameObject Find(string path)
        {
            if (objects == null || string.IsNullOrEmpty(path)) return null;
            int slash = path.IndexOf('/');
            if (slash < 0) return objects.TryGetValue(path, out GameObject direct) ? direct : null;
            string rootPath = path.Substring(0, slash);
            if (!objects.TryGetValue(rootPath, out GameObject root) || root == null) return null;
            Transform nested = root.transform.Find(path.Substring(slash + 1));
            return nested == null ? null : nested.gameObject;
        }
        internal static SpriteRenderer GetRenderer(string path)
        {
            GameObject go = Find(path);
            return go == null ? null : go.GetComponent<SpriteRenderer>();
        }
    }
}

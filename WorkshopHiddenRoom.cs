using System;
using System.Collections.Generic;
using GlobalEnums;
using Modding;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace OrbRadiance
{
    internal static class WorkshopHiddenRoom
    {
        private const string SceneName = "GG_Workshop";
        private const string LeftArchCropPath = "gg_arch (1)/GameObject (1)/GG_scenery_0001_1 (30)";
        private static readonly Rect Opening = Rect.MinMaxRect(259.99f, 35f, 280.01f, 39f);
        private const float ArchWindowCenterX = (WorkshopArchFacade.WindowLeft + WorkshopArchFacade.WindowRight) * .5f;
        private const float ArchWindowWidth = WorkshopArchFacade.WindowRight - WorkshopArchFacade.WindowLeft;
        private static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private static WorkshopTerrainPatch terrain;
        private static Scene installedScene;
        private static bool installed, open, subscribed, waitingForReturnScene;
        private static float retryAt;
        private static float originX, roomLeft, roomRight;
        private static CameraController camera;
        private static GameManager gameManager;
        private static float oldSceneWidth, oldXLimit;
        private static float oldGameSceneWidth;
        private static bool boundsCaptured;
        private static GameObject roomRoot;
        private static GameObject statueContainer;
        private static GameObject ambientParticlesCopy;
        private static ParticleSystem[] ambientParticleSystems = Array.Empty<ParticleSystem>();
        private static SpriteRenderer dreamBaseSource, dreamBaseCopy;
        private static SpriteRenderer leftWallArchSource, leftWallArchCopy;
        private static GameObject copiedDecorRoot;
        private static SpriteRenderer ascentBacking0000Source, ascentBacking0000Copy;
        private static SpriteRenderer ascentBacking0001Source, ascentBacking0001Copy;
        private static MeshRenderer blurPlaneDiagnostic;
        private static bool openedDiagnosticsLogged;
        private static BossStatue hiddenStatue;
        private static bool heroProbeLogged, inspectStateCaptured, lastInspectCameraLockActive;
        private static float heroStableSince = -1f;
        private static string lastWaitLog;
        internal static bool IsOpen => open;
        internal static bool IsCurrentScene => installed && installedScene == UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        internal static void Install()
        {
            if (!subscribed)
            {
                subscribed = true;
                ModHooks.HeroUpdateHook += Update;
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
                OrbRadiance.Instance?.LogDebug("HiddenRoom: 场景与玩家更新挂钩已注册");
            }
            if (OrbRadiance.Settings.Enabled) TryInstall();
        }

        private static void Update()
        {
            if (!OrbRadiance.Settings.Enabled) { if (installed) Cleanup(true); return; }
            if (!installed && Time.unscaledTime >= retryAt) TryInstall();
            if (installed && installedScene != UnityEngine.SceneManagement.SceneManager.GetActiveScene() && !waitingForReturnScene) Cleanup(false);
            if (installed && open) MaintainBounds();
            UpdateVisualProbe();
        }

        private static void OnSceneChanged(Scene oldScene, Scene newScene)
        {
            if (installed && oldScene == installedScene) Cleanup(false);
            if (installed && newScene == installedScene) waitingForReturnScene = false;
            if (OrbRadiance.Settings.Enabled && newScene.name == SceneName) retryAt = 0f;
            if (newScene.name == SceneName) lastWaitLog = null;
        }

        private static void TryInstall()
        {
            if (!OrbRadiance.Settings.Enabled || installed || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != SceneName) return;
            Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            try
            {
                if (!WorkshopRoomAssets.Ready)
                { WaitFor("GG_Workshop 原生隐藏墙或装饰预加载不完整；隐藏入口保持关闭"); retryAt = Time.unscaledTime + 1f; return; }
                CameraController readyCamera = GameCameras.instance == null ? null : GameCameras.instance.cameraController;
                if (readyCamera == null || readyCamera.tilemap == null || readyCamera.tilemap.gameObject.scene != scene)
                { WaitFor("GG_Workshop camera tilemap 尚未就绪"); retryAt = Time.unscaledTime + 1f; return; }
                if (GameManager.UnsafeInstance == null)
                { WaitFor("GameManager 尚未就绪"); retryAt = Time.unscaledTime + 1f; return; }
                if (!BuildIntoScene(scene, false)) { retryAt = Time.unscaledTime + 1f; return; }
                lastWaitLog = null;
                OrbRadiance.Instance?.LogDebug("HiddenRoom: GG_Workshop 场景对象、相机与 GameManager 均已就绪；骨钉入口已挂接");
                OrbRadiance.Instance?.Log("HiddenRoom: GG_Workshop 房间已安装，入口基准 x=" + originX.ToString("F3"));
            }
            catch (Exception e)
            {
                OrbRadiance.Instance?.LogError("HiddenRoom: 初始化失败，将在 2 秒后重试：" + e);
                Cleanup(false); retryAt = Time.unscaledTime + 2f;
            }
        }

        private static bool BuildIntoScene(Scene scene, bool returnScene)
        {
            if (!OrbRadiance.Settings.Enabled || !WorkshopRoomAssets.Ready || !scene.IsValid() || !scene.isLoaded || scene.name != SceneName) return false;
            if (GameManager.UnsafeInstance == null) return false;
            Transform wallTransform = WorkshopTerrainPatch.FindPath(scene, "wall collider (1)");
            BoxCollider2D wall = wallTransform == null ? null : wallTransform.GetComponent<BoxCollider2D>();
            if (wall == null) return false;
            originX = wall.bounds.min.x;
            if (Mathf.Abs(originX - 259.91125f) > .4f) throw new InvalidOperationException("wall collider (1) bounds.min.x unexpected: " + originX);
            roomLeft = 302f; roomRight = 319f;
            GameObject rootObject = new GameObject("Wind GG_Workshop Hidden Room");
            owned.Add(rootObject); roomRoot = rootObject;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(rootObject, scene);
            BuildRoom(rootObject, scene);
            installedScene = scene; installed = true;
            waitingForReturnScene = returnScene && scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return true;
        }

        internal static bool InstallForReturn(Scene targetScene, string expectedGateName)
        {
            if (installed && installedScene == targetScene && open)
            {
                Transform alreadyBrokenSeam = roomRoot == null ? null : roomRoot.transform.Find("Hidden nail seam");
                if (alreadyBrokenSeam != null) alreadyBrokenSeam.gameObject.SetActive(false);
                return FindTransition(targetScene, expectedGateName) != null;
            }
            if (installed) Cleanup(false);
            if (!OrbRadiance.Settings.Enabled || !WorkshopRoomAssets.Ready) return false;
            try
            {
                if (!BuildIntoScene(targetScene, true)) return false;
                if (!OpenDoorCore(true)) { Cleanup(false); return false; }
                Transform seam = roomRoot == null ? null : roomRoot.transform.Find("Hidden nail seam");
                if (seam != null) seam.gameObject.SetActive(false);
                TransitionPoint point = FindTransition(targetScene, expectedGateName);
                if (point == null || !point.gameObject.activeInHierarchy) { Cleanup(false); return false; }
                OrbRadiance.Instance?.LogDebug("HiddenRoom: 返回目标场景已同步准备并开放专属门 " + expectedGateName);
                return true;
            }
            catch (Exception e)
            {
                OrbRadiance.Instance?.LogError("HiddenRoom: 返还场景准备失败，使用原生门回退：" + e);
                Cleanup(false);
                return false;
            }
        }

        private static TransitionPoint FindTransition(Scene scene, string gateName)
        {
            if (string.IsNullOrEmpty(gateName)) return null;
            if (statueContainer != null && installedScene == scene)
                foreach (TransitionPoint point in statueContainer.GetComponentsInChildren<TransitionPoint>(true))
                    if (point != null && point.gameObject.name == gateName) return point;
            return null;
        }

        private static void WaitFor(string reason)
        {
            if (lastWaitLog == reason) return;
            lastWaitLog = reason;
            OrbRadiance.Instance?.LogDebug("HiddenRoom: 等待初始化：" + reason);
        }

        private static void BuildRoom(GameObject root, Scene scene)
        {
            // Concealed attack target sits just inside the existing wall; only Nail hits open the space.
            GameObject door = new GameObject("Hidden nail seam"); door.layer = 19; door.transform.SetParent(root.transform, false); door.transform.position = new Vector3(originX + .18f + 1.2f, 37f, 0f);
            BoxCollider2D hit = door.AddComponent<BoxCollider2D>(); hit.isTrigger = true; hit.size = new Vector2(.8f, 3.8f);
            HiddenRoomDoor doorController = door.AddComponent<HiddenRoomDoor>();
            if (!doorController.Initialize(WorkshopRoomAssets.WallPrefab)) throw new InvalidOperationException("无法安全克隆并配置原生隐藏墙 FSM");

            // Bridge and room surfaces; order 0 lets the black fill cover rear haze while z<0 foreground stones remain visible.
            MakeBlock(root, "Passage floor", new Vector2((originX + roomLeft) * .5f, 34.85f), new Vector2(roomLeft - originX, .3f), Color.black, true, true);
            GameObject passageCeiling = MakeBlock(root, "Passage ceiling", new Vector2((originX + roomLeft) * .5f, 39.15f), new Vector2(roomLeft - originX, .3f), Color.black, true, true);
            GameObject passageBack = MakeBlock(root, "Passage backdrop", new Vector2((originX + WorkshopArchFacade.WindowLeft) * .5f, 37f), new Vector2(WorkshopArchFacade.WindowLeft - originX, 4f), Color.black, false, true);
            MakeBlock(root, "Room floor", new Vector2((roomLeft + roomRight) * .5f, 34.85f), new Vector2(17f, .3f), Color.black, true, true);
            GameObject blackFloorBacking = MakeBlock(root, "Room black floor backing", new Vector2((roomLeft + roomRight) * .5f, 25f), new Vector2(40f, 20f), Color.black, false, true);
            GameObject roomCeiling = MakeBlock(root, "Room ceiling", new Vector2((roomLeft + roomRight) * .5f, 47.15f), new Vector2(17f, .3f), Color.black, true, true);
            GameObject roomLeftWall = MakeBlock(root, "Room left wall", new Vector2(roomLeft - .15f, 43f), new Vector2(.3f, 8f), Color.black, true, true);
            GameObject roomRightWall = MakeBlock(root, "Room right wall", new Vector2(roomRight + .35f, 41f), new Vector2(.3f, 12f), Color.black, true, true);
            roomCeiling.GetComponent<SpriteRenderer>().enabled = false;
            roomLeftWall.GetComponent<SpriteRenderer>().enabled = false;
            roomRightWall.GetComponent<SpriteRenderer>().enabled = false;
            passageCeiling.GetComponent<SpriteRenderer>().enabled = false;
            GameObject back = MakeBlock(root, "Room backdrop", new Vector2(ArchWindowCenterX, 41f), new Vector2(ArchWindowWidth, 12f), new Color(.11f, .09f, .18f), false, true);
            back.GetComponent<SpriteRenderer>().sortingOrder = -5;
            WorkshopRoomAssets.ResolveBlurPlaneForScene(scene, reason => OrbRadiance.Instance?.LogWarn(reason));
            if (BuildCopiedBlurPlane(root)) back.GetComponent<SpriteRenderer>().enabled = false;
            BuildCopiedDecor(root);
            BuildCopiedAmbientParticles(root, scene);
            WorkshopArchFacade.Create(root, owned, leftWallArchSource == null ? null : leftWallArchSource.sharedMaterial);
            BuildCopiedStatue(root, scene);
            float passageStart = originX + .35f;
            CreateCameraLock(root, "Passage camera lock", new Vector2((passageStart + roomLeft) * .5f, 37f), new Vector2(roomLeft - passageStart, 4f), passageStart + 10f, roomLeft - 10f, 37f, 37f);
            CreateCameraLock(root, "Room camera lock", new Vector2((roomLeft + roomRight) * .5f, 41f), new Vector2(17f, 12f), 310.5f, 310.5f, 39.3f, 42.7f);
            foreach (Transform t in root.transform)
            {
                if (t.name == "Hidden nail seam") continue;
                if (t.GetComponent<CameraLockArea>() != null) continue;
                t.gameObject.SetActive(false);
            }
        }

        private static void BuildCopiedAmbientParticles(GameObject root, Scene scene)
        {
            GameObject source = WorkshopRoomAssets.ResolveAmbientParticleGroupForScene(scene, reason => OrbRadiance.Instance?.LogWarn(reason));
            ParticleSystem[] sourceSystems = source == null ? Array.Empty<ParticleSystem>() : source.GetComponentsInChildren<ParticleSystem>(true);
            if (sourceSystems.Length != 3)
            {
                OrbRadiance.Instance?.LogWarn("HiddenRoom: 未复制辐光房间环境粒子；房间其他部分继续安装");
                ambientParticleSystems = Array.Empty<ParticleSystem>();
                return;
            }

            var copy = new GameObject("Private Radiance room ambient particles");
            copy.SetActive(false);
            copy.transform.SetParent(root.transform, false);
            ambientParticlesCopy = copy;

            // Instantiate below an inactive owner so the source ReduceParticleEffects scripts and PlayOnAwake systems never run.
            GameObject stagingClone = UnityEngine.Object.Instantiate(source, copy.transform, false);
            stagingClone.SetActive(false);
            foreach (MonoBehaviour script in stagingClone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                script.enabled = false;
                UnityEngine.Object.DestroyImmediate(script);
            }
            ParticleSystem[] copiedSystems = stagingClone.GetComponentsInChildren<ParticleSystem>(true);
            if (copiedSystems.Length != sourceSystems.Length) throw new InvalidOperationException("神居原生区域环境粒子副本不完整：" + copiedSystems.Length);

            float targetWidth = roomRight + .2f - roomLeft;
            float targetHeight = 12f;
            float targetX = (roomLeft + roomRight + .2f) * .5f;
            float targetY = 41f;
            ambientParticleSystems = new ParticleSystem[sourceSystems.Length];
            for (int i = 0; i < sourceSystems.Length; i++)
            {
                ParticleSystem sourceSystem = sourceSystems[i];
                ParticleSystem system = Array.Find(copiedSystems, candidate => candidate.gameObject.name == sourceSystem.gameObject.name);
                if (system == null) throw new InvalidOperationException("神居原生粒子节点缺失：" + sourceSystem.gameObject.name);
                Transform sourceTransform = sourceSystem.transform;
                Transform copyTransform = system.transform;
                copyTransform.gameObject.SetActive(false);
                copyTransform.SetParent(copy.transform, true);
                copyTransform.rotation = sourceTransform.rotation;
                copyTransform.localScale = sourceTransform.lossyScale;

                float sourceWidth, sourceHeight, targetZ;
                if (sourceSystem.gameObject.name == "Particle System BG")
                { sourceWidth = 22.022933f; sourceHeight = 12.467538f; targetZ = 4.715503f; }
                else if (sourceSystem.gameObject.name == "Particle System FG")
                { sourceWidth = 18.0455f; sourceHeight = 11.9735f; targetZ = 2f; }
                else if (sourceSystem.gameObject.name == "dream_particle_03 (3)")
                { sourceWidth = 28.408f; sourceHeight = 18.965f; targetZ = 10f; }
                else throw new InvalidOperationException("未知神居环境粒子节点：" + sourceSystem.gameObject.name);

                copyTransform.position = new Vector3(targetX, targetY, targetZ);
                ParticleSystem.ShapeModule shape = system.shape;
                Vector3 shapeScale = shape.scale;
                float scaleX = targetWidth / sourceWidth;
                float scaleY = targetHeight / sourceHeight;
                shapeScale.x *= scaleX;
                shapeScale.z *= scaleY;
                shape.scale = shapeScale;

                float areaRatio = scaleX * scaleY;
                ParticleSystem.EmissionModule emission = system.emission;
                ParticleSystem.MinMaxCurve rate = emission.rateOverTime;
                rate.constantMin *= areaRatio;
                rate.constantMax *= areaRatio;
                emission.rateOverTime = rate;
                ParticleSystem.MainModule main = system.main;
                main.maxParticles = Math.Max(1, Mathf.RoundToInt(main.maxParticles * areaRatio));
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ambientParticleSystems[i] = system;

                OrbRadiance.Instance?.LogDebug("HiddenRoom: 环境粒子 " + sourceSystem.gameObject.name +
                    " 屏幕发射范围=" + targetWidth.ToString("F1") + "x" + targetHeight.ToString("F1") +
                    " 面积率=" + areaRatio.ToString("F3") + " z=" + targetZ.ToString("F2"));
            }
            foreach (ParticleSystem system in ambientParticleSystems)
                if (system != null) system.gameObject.SetActive(true);
            UnityEngine.Object.DestroyImmediate(stagingClone);
            OrbRadiance.Instance?.Log("HiddenRoom: 原生环境粒子组已克隆3层；父级=" + WorkshopRoomAssets.AmbientParticleGroupLabel +
                "，范围X=" + roomLeft.ToString("F1") + ".." + (roomRight + .2f).ToString("F1") + ",Y=35.0..47.0");
        }

        private static void StartAmbientParticles()
        {
            if (ambientParticlesCopy == null) return;
            foreach (ParticleSystem system in ambientParticleSystems)
            {
                if (system == null) continue;
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Play(true);
            }
        }

        private static void StopAmbientParticles()
        {
            if (ambientParticlesCopy == null) return;
            foreach (ParticleSystem system in ambientParticleSystems)
                if (system != null) system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private static bool BuildCopiedBlurPlane(GameObject root)
        {
            GameObject source = WorkshopRoomAssets.BlurPlaneSource;
            if (source == null || WorkshopRoomAssets.BlurPlaneMesh == null || WorkshopRoomAssets.BlurPlaneMaterial == null)
            {
                OrbRadiance.Instance?.Log("HiddenRoom: 原生 BlurPlane 不完整，保留临时背景底色");
                return false;
            }

            var plane = new GameObject("Room native blur plane");
            plane.SetActive(false);
            plane.transform.SetParent(root.transform, false);
            plane.layer = source.layer;
            Vector3 position = source.transform.position;
            plane.transform.position = new Vector3(ArchWindowCenterX, 41f, position.z);
            plane.transform.rotation = source.transform.rotation;
            plane.transform.localScale = new Vector3(1.2f, 1f, 1.7f * (ArchWindowWidth / (roomRight - roomLeft)));

            MeshFilter filter = plane.AddComponent<MeshFilter>();
            filter.sharedMesh = WorkshopRoomAssets.BlurPlaneMesh;
            MeshRenderer renderer = plane.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = WorkshopRoomAssets.BlurPlaneMaterial;
            plane.AddComponent<BlurPlane>();
            blurPlaneDiagnostic = renderer;
            return true;
        }

        private static void BuildCopiedDecor(GameObject root)
        {
            dreamBaseSource = dreamBaseCopy = null;
            leftWallArchSource = leftWallArchCopy = null;
            copiedDecorRoot = null;
            ascentBacking0000Source = ascentBacking0000Copy = null;
            ascentBacking0001Source = ascentBacking0001Copy = null;
            var decor = new GameObject("Copied GG_Workshop statue decoration");
            copiedDecorRoot = decor;
            decor.transform.SetParent(root.transform, false);
            var leftPillarMirror = new GameObject("Decor left mirrored pillar group");
            leftPillarMirror.transform.SetParent(decor.transform, false);
            leftPillarMirror.transform.position = new Vector3(301.1f, 40.4f, 0f);
            leftPillarMirror.transform.rotation = Quaternion.identity;
            leftPillarMirror.transform.localScale = new Vector3(-1f, 1f, 1f);
            foreach (string path in WorkshopRoomAssets.RequiredDecorationPaths)
            {
                GameObject source = WorkshopRoomAssets.Find(path);
                SpriteRenderer sourceRenderer = source == null ? null : source.GetComponentInChildren<SpriteRenderer>(true);
                if (sourceRenderer == null || sourceRenderer.sprite == null) throw new InvalidOperationException("精确神居装饰缺少 SpriteRenderer：" + path);
                int slash = path.LastIndexOf('/');
                string leafName = slash < 0 ? path : path.Substring(slash + 1);
                GameObject item = new GameObject("Decor " + leafName);
                item.transform.SetParent(decor.transform, false);
                Transform src = sourceRenderer.transform;
                Vector3 sourceAnchor = new Vector3(WorkshopRoomAssets.SourceAnchor[0], WorkshopRoomAssets.SourceAnchor[1], WorkshopRoomAssets.SourceAnchor[2]);
                Vector3 targetAnchor = new Vector3(WorkshopRoomAssets.NewAnchor[0], WorkshopRoomAssets.NewAnchor[1], WorkshopRoomAssets.NewAnchor[2]);
                Vector3 world = src.position + (targetAnchor - sourceAnchor);
                if (path == WorkshopRoomAssets.LeftWallArchPath) { world.x = 309f; world.y = 46.5f; }
                item.transform.position = world;
                item.transform.rotation = src.rotation;
                item.transform.localScale = src.lossyScale;
                if (path == LeftArchCropPath)
                {
                    CreateClippedArchCopy(item, sourceRenderer, 302f);
                    continue;
                }
                SpriteRenderer renderer = CopyDecorationRenderer(item, sourceRenderer);
                if (IsRightPillarPiece(path))
                {
                    var mirrored = new GameObject("Decor left mirror " + leafName);
                    mirrored.transform.SetParent(leftPillarMirror.transform, false);
                    // One parent-level negative X scale mirrors the native three-piece group's offsets and art.
                    mirrored.transform.localPosition = new Vector3(world.x - 320.3504f,
                                                                    world.y - 37.989998f,
                                                                    world.z);
                    mirrored.transform.localRotation = src.rotation;
                    mirrored.transform.localScale = src.lossyScale;
                    CopyDecorationRenderer(mirrored, sourceRenderer);
                }
                if (path == WorkshopRoomAssets.LeftWallArchPath) { leftWallArchSource = sourceRenderer; leftWallArchCopy = renderer; }
                else if (path == "dream_beam_animation/dream_nail_base") { dreamBaseSource = sourceRenderer; dreamBaseCopy = renderer; }
                else if (path.StartsWith("gg_ascent_backing_0000_2", StringComparison.Ordinal) && ascentBacking0000Source == null) { ascentBacking0000Source = sourceRenderer; ascentBacking0000Copy = renderer; }
                else if (path.StartsWith("gg_ascent_backing_0001_1", StringComparison.Ordinal) && ascentBacking0001Source == null) { ascentBacking0001Source = sourceRenderer; ascentBacking0001Copy = renderer; }
            }
            OrbRadiance.Instance?.LogDebug("HiddenRoom: 已复制原生静态装饰 " + WorkshopRoomAssets.AvailableDecorationCount + " 个，包含梦境底座/光柱和两种铺墙背景");
        }

        private static bool IsRightPillarPiece(string path)
        {
            return path == "GG_scenery_0011_10 (8)" || path == "GG_scenery_0009_12 (1)" ||
                   path == "GG_scenery_0010_11 (1)";
        }

        private static SpriteRenderer CopyDecorationRenderer(GameObject item, SpriteRenderer source)
        {
            SpriteRenderer renderer = item.AddComponent<SpriteRenderer>();
            renderer.sprite = source.sprite;
            renderer.sharedMaterial = source.sharedMaterial;
            renderer.color = source.color;
            renderer.sortingLayerID = source.sortingLayerID;
            renderer.sortingOrder = source.sortingOrder;
            renderer.flipX = source.flipX;
            renderer.flipY = source.flipY;
            renderer.maskInteraction = source.maskInteraction;
            renderer.drawMode = source.drawMode;
            renderer.size = source.size;
            renderer.enabled = source.enabled;
            return renderer;
        }

        private struct CroppedSpriteVertex
        {
            internal Vector3 position;
            internal Vector2 uv;
            internal CroppedSpriteVertex(Vector3 position, Vector2 uv) { this.position = position; this.uv = uv; }
        }

        private static void CreateClippedArchCopy(GameObject item, SpriteRenderer source, float minimumWorldX)
        {
            Sprite sprite = source.sprite;
            Vector2[] sourceVertices = sprite.vertices;
            Vector2[] sourceUvs = sprite.uv;
            ushort[] sourceTriangles = sprite.triangles;
            if (sourceVertices == null || sourceUvs == null || sourceTriangles == null || sourceVertices.Length == 0 ||
                sourceUvs.Length != sourceVertices.Length || sourceTriangles.Length < 3 || sourceTriangles.Length % 3 != 0)
                throw new InvalidOperationException("左侧拱门原生 Sprite 几何/UV 不完整，无法安全裁剪");
            if (source.sharedMaterial == null) throw new InvalidOperationException("左侧拱门 Sprite 缺少原生材质");

            var outputVertices = new List<Vector3>();
            var outputUvs = new List<Vector2>();
            var outputColors = new List<Color>();
            var outputTriangles = new List<int>();
            for (int i = 0; i < sourceTriangles.Length; i += 3)
            {
                var polygon = new List<CroppedSpriteVertex>(3);
                for (int j = 0; j < 3; j++)
                {
                    int index = sourceTriangles[i + j];
                    Vector2 point = sourceVertices[index];
                    if (source.flipX) point.x = -point.x;
                    if (source.flipY) point.y = -point.y;
                    polygon.Add(new CroppedSpriteVertex(new Vector3(point.x, point.y, 0f), sourceUvs[index]));
                }
                polygon = ClipSpritePolygonAtWorldX(polygon, item.transform, minimumWorldX);
                if (polygon.Count < 3) continue;

                int first = outputVertices.Count;
                foreach (CroppedSpriteVertex vertex in polygon)
                {
                    outputVertices.Add(vertex.position);
                    outputUvs.Add(vertex.uv);
                    outputColors.Add(source.color);
                }
                for (int j = 1; j < polygon.Count - 1; j++)
                {
                    outputTriangles.Add(first);
                    outputTriangles.Add(first + j);
                    outputTriangles.Add(first + j + 1);
                }
            }
            if (outputTriangles.Count == 0) throw new InvalidOperationException("左侧拱门在 X=302 裁剪后没有可见几何");

            var mesh = new Mesh { name = "X302-clipped native left arch sprite", hideFlags = HideFlags.HideAndDontSave };
            owned.Add(mesh);
            mesh.vertices = outputVertices.ToArray();
            mesh.uv = outputUvs.ToArray();
            mesh.colors = outputColors.ToArray();
            mesh.SetTriangles(outputTriangles.ToArray(), 0);
            mesh.RecalculateBounds();

            var material = new Material(source.sharedMaterial)
            {
                name = "Private clipped native left arch material",
                hideFlags = HideFlags.HideAndDontSave
            };
            owned.Add(material);
            material.mainTexture = sprite.texture;
            if (sprite.associatedAlphaSplitTexture != null)
            {
                if (material.HasProperty("_AlphaTex")) material.SetTexture("_AlphaTex", sprite.associatedAlphaSplitTexture);
                if (material.HasProperty("_EnableExternalAlpha")) material.SetFloat("_EnableExternalAlpha", 1f);
                material.EnableKeyword("ETC1_EXTERNAL_ALPHA");
            }

            SpriteRenderer retainedSettings = item.AddComponent<SpriteRenderer>();
            retainedSettings.sprite = source.sprite;
            retainedSettings.sharedMaterial = source.sharedMaterial;
            retainedSettings.color = source.color;
            retainedSettings.sortingLayerID = source.sortingLayerID;
            retainedSettings.sortingOrder = source.sortingOrder;
            retainedSettings.flipX = source.flipX;
            retainedSettings.flipY = source.flipY;
            retainedSettings.maskInteraction = source.maskInteraction;
            retainedSettings.drawMode = source.drawMode;
            retainedSettings.size = source.size;
            retainedSettings.enabled = false;

            var meshObject = new GameObject(item.name + " clipped mesh renderer");
            meshObject.layer = item.layer;
            meshObject.transform.SetParent(item.transform, false);
            meshObject.transform.localPosition = Vector3.zero;
            meshObject.transform.localRotation = Quaternion.identity;
            meshObject.transform.localScale = Vector3.one;
            meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = meshObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.sortingLayerID = source.sortingLayerID;
            renderer.sortingOrder = source.sortingOrder;
            renderer.enabled = source.enabled;
            OrbRadiance.Instance?.LogDebug("HiddenRoom: 左拱门tight Sprite沿worldX=" + minimumWorldX.ToString("F1") +
                "裁剪，mesh=" + outputVertices.Count + " vertices/" + (outputTriangles.Count / 3) + " triangles，texture=" +
                (sprite.texture == null ? "<null>" : sprite.texture.name) + "，shader=" +
                (material.shader == null ? "<null>" : material.shader.name));
        }

        private static List<CroppedSpriteVertex> ClipSpritePolygonAtWorldX(List<CroppedSpriteVertex> input, Transform transform, float minimumWorldX)
        {
            var output = new List<CroppedSpriteVertex>(input.Count + 1);
            if (input.Count == 0) return output;
            CroppedSpriteVertex previous = input[input.Count - 1];
            float previousX = transform.TransformPoint(previous.position).x;
            bool previousInside = previousX >= minimumWorldX;
            foreach (CroppedSpriteVertex current in input)
            {
                float currentX = transform.TransformPoint(current.position).x;
                bool currentInside = currentX >= minimumWorldX;
                if (currentInside != previousInside)
                {
                    float t = (minimumWorldX - previousX) / (currentX - previousX);
                    output.Add(new CroppedSpriteVertex(
                        new Vector3(
                            previous.position.x + (current.position.x - previous.position.x) * t,
                            previous.position.y + (current.position.y - previous.position.y) * t,
                            previous.position.z + (current.position.z - previous.position.z) * t),
                        previous.uv + (current.uv - previous.uv) * t));
                }
                if (currentInside) output.Add(current);
                previous = current;
                previousX = currentX;
                previousInside = currentInside;
            }
            return output;
        }

        private static void BuildCopiedStatue(GameObject room, Scene scene)
        {
            // Keep the whole copy under the hidden room's inactive staging child until the room opens.
            statueContainer = new GameObject("Private Radiance statue staging");
            statueContainer.transform.SetParent(room.transform, false);
            statueContainer.SetActive(false);
            GameObject clone = UnityEngine.Object.Instantiate(WorkshopRoomAssets.StatuePrefab, statueContainer.transform, false);
            clone.name = WorkshopStatueLifecycle.HiddenStatueObjectName;
            clone.transform.position = new Vector3(WorkshopRoomAssets.NewAnchor[0], WorkshopRoomAssets.NewAnchor[1], WorkshopRoomAssets.NewAnchor[2]);
            clone.transform.rotation = WorkshopRoomAssets.StatuePrefab.transform.rotation;
            clone.transform.localScale = WorkshopRoomAssets.StatuePrefab.transform.lossyScale;
            HiddenRadianceStatue marker = clone.GetComponent<HiddenRadianceStatue>() ?? clone.AddComponent<HiddenRadianceStatue>();

            BossStatue statue = clone.GetComponent<BossStatue>();
            if (statue == null) throw new InvalidOperationException("GG_Statue_Radiance 克隆缺少 BossStatue");
            hiddenStatue = statue;
            GameObject returnGate = statue.dreamReturnGate;
            if (returnGate == null || returnGate.GetComponent<TransitionPoint>() == null)
                throw new InvalidOperationException("克隆辐光雕像缺少原生 dreamReturnGate TransitionPoint");
            returnGate.name = "door_dreamReturnGG";

            PlayMakerFSM[] fsms = clone.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms.Length < 6) throw new InvalidOperationException("辐光雕像六个原生FSM不完整：" + fsms.Length);
            foreach (PlayMakerFSM fsm in fsms)
                if (!HiddenRoomDoor.PreparePrivateFsm(fsm)) throw new InvalidOperationException("雕像私有FSM初始化失败：" + fsm.FsmName);

            foreach (CameraLockArea lockArea in clone.GetComponentsInChildren<CameraLockArea>(true))
            {
                lockArea.cameraXMin = 310.5f;
                lockArea.cameraXMax = 310.5f;
                lockArea.maxPriority = true;
            }
            Transform statueDisplay = clone.transform.Find("Base/Statue/GG_statues_0006_5");
            SpriteRenderer displayRenderer = statueDisplay == null ? null : statueDisplay.GetComponent<SpriteRenderer>();
            SpriteRenderer sourceRenderer = WorkshopRoomAssets.VisibleStatueRenderer;
            if (displayRenderer == null || sourceRenderer == null || sourceRenderer.sprite == null)
                throw new InvalidOperationException("辐光雕像可见贴图源或克隆占位 SpriteRenderer 缺失");
            displayRenderer.sprite = sourceRenderer.sprite;
            displayRenderer.sharedMaterial = sourceRenderer.sharedMaterial;
            displayRenderer.color = sourceRenderer.color;
            displayRenderer.sortingLayerID = sourceRenderer.sortingLayerID;
            displayRenderer.sortingOrder = sourceRenderer.sortingOrder;
            displayRenderer.flipX = sourceRenderer.flipX;
            displayRenderer.flipY = sourceRenderer.flipY;
            displayRenderer.maskInteraction = sourceRenderer.maskInteraction;
            displayRenderer.enabled = sourceRenderer.enabled;

            // The copied statue must remain fully visible without running native first-visit or proximity fades.
            SpriteFadeMaterial fadeMaterial = clone.GetComponentInChildren<SpriteFadeMaterial>(true);
            if (fadeMaterial != null) fadeMaterial.enabled = false;
            BossStatueFlashEffect flash = clone.GetComponentInChildren<BossStatueFlashEffect>(true);
            if (flash != null)
            {
                flash.enabled = false;
                flash.gameObject.SetActive(false);
            }
            foreach (GlowResponse glow in clone.GetComponentsInChildren<GlowResponse>(true))
            {
                if (glow.FadeSprites != null)
                    foreach (SpriteRenderer sprite in glow.FadeSprites)
                    {
                        if (sprite == null) continue;
                        Color color = sprite.color;
                        color.a = 1f;
                        sprite.color = color;
                    }
                if (glow.light != null)
                {
                    glow.light.intensity = WorkshopRoomAssets.StatueSpotlightIntensity;
                    glow.light.enabled = true;
                }
                glow.enabled = false;
                glow.FadeSprites = new List<SpriteRenderer>();
                glow.light = null;
                glow.particles = null;
            }
            clone.SetActive(true);
        }

        private static GameObject MakeBlock(GameObject parent, string name, Vector2 center, Vector2 size, Color color, bool collider, bool initiallyHidden)
        {
            var go = new GameObject(name); go.transform.SetParent(parent.transform, false); go.transform.position = center; go.transform.localScale = new Vector3(size.x, size.y, 1f); go.layer = collider ? 8 : 0;
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false); owned.Add(tex); tex.SetPixel(0, 0, color); tex.Apply(); tex.hideFlags = HideFlags.HideAndDontSave;
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(.5f, .5f), 1f); owned.Add(sprite); sprite.hideFlags = HideFlags.HideAndDontSave;
            var renderer = go.AddComponent<SpriteRenderer>(); renderer.sprite = sprite; renderer.sortingOrder = 0;
            if (collider) { var box = go.AddComponent<BoxCollider2D>(); box.size = Vector2.one; }
            go.SetActive(!initiallyHidden); return go;
        }

        private static void CreateCameraLock(GameObject root, string name, Vector2 center, Vector2 size, float xmin, float xmax, float ymin, float ymax)
        {
            GameObject area = new GameObject(name); area.layer = 13; area.transform.SetParent(root.transform, false);
            area.transform.position = center;
            BoxCollider2D box = area.AddComponent<BoxCollider2D>(); box.isTrigger = true; box.size = size;
            CameraLockArea lockArea = area.AddComponent<CameraLockArea>(); lockArea.maxPriority = true; lockArea.preventLookUp = true;
            lockArea.cameraXMin = xmin; lockArea.cameraXMax = xmax; lockArea.cameraYMin = ymin; lockArea.cameraYMax = ymax;
            // CameraLockArea.Start must run before the opening hit can enable its trigger.
            area.SetActive(true);
            box.enabled = false;
        }

        internal static bool OpenDoor()
        {
            return OpenDoorCore(false);
        }

        private static bool OpenDoorCore(bool allowInactiveScene)
        {
            if (open) return true;
            if (!installed || !OrbRadiance.Settings.Enabled || (!allowInactiveScene && !IsCurrentScene)) return false;
            try
            {
                terrain = new WorkshopTerrainPatch();
                terrain.Apply(installedScene, originX, Opening);
                open = true;
                if (roomRoot != null)
                    foreach (Transform child in roomRoot.transform)
                    {
                        if (child.name == "Hidden nail seam") continue;
                        child.gameObject.SetActive(true);
                        if (child.GetComponent<CameraLockArea>() != null)
                        {
                            Collider2D area = child.GetComponent<Collider2D>();
                            if (area != null) area.enabled = true;
                        }
                    }
                StartAmbientParticles();
                if (statueContainer != null) statueContainer.SetActive(true);
                if (IsCurrentScene) MaintainBounds();
                LogOpenedVisualDiagnostics();
            }
            catch (Exception e)
            {
                SetRoomClosed();
                if (boundsCaptured && installedScene == UnityEngine.SceneManagement.SceneManager.GetActiveScene())
                {
                    if (camera != null) { camera.sceneWidth = oldSceneWidth; camera.xLimit = oldXLimit; }
                    if (gameManager != null) gameManager.sceneWidth = oldGameSceneWidth;
                }
                camera = null; gameManager = null; boundsCaptured = false;
                if (terrain != null) terrain.Restore(); terrain = null; open = false;
                OrbRadiance.Instance?.LogError("HiddenRoom: 开门失败，地形已回滚：" + e);
                return false;
            }
            OrbRadiance.Instance?.Log("HiddenRoom: 骨钉命中，入口已打开");
            return true;
        }

        private static void LogOpenedVisualDiagnostics()
        {
            if (openedDiagnosticsLogged) return;
            openedDiagnosticsLogged = true;
            LogRendererDiagnostic("dream_nail_base", dreamBaseCopy);
            LogRendererDiagnostic("gg_ascent_backing_0000_2", ascentBacking0000Copy);
            LogRendererDiagnostic("gg_ascent_backing_0001_1", ascentBacking0001Copy);
            LogBlurPlaneDiagnostic();
            Camera mainCamera = GameCameras.instance == null ? null : GameCameras.instance.mainCamera;
            if (mainCamera == null) OrbRadiance.Instance?.Log("HiddenRoom INFO: GameCameras.mainCamera=<null>");
            else
            {
                Vector3 cameraPosition = mainCamera.transform.position;
                OrbRadiance.Instance?.Log("HiddenRoom INFO: mainCamera pos=(" + F(cameraPosition.x) + "," + F(cameraPosition.y) + "," + F(cameraPosition.z) +
                    ") near=" + F(mainCamera.nearClipPlane) + " far=" + F(mainCamera.farClipPlane) + " cullingMask=0x" + mainCamera.cullingMask.ToString("X8"));
            }
            CaptureVisualProbe("opening-before-room-camera-arrival");
        }

        private static void LogBlurPlaneDiagnostic()
        {
            if (blurPlaneDiagnostic == null)
            {
                OrbRadiance.Instance?.Log("HiddenRoom INFO: native BlurPlane=<missing>");
                return;
            }
            Vector3 position = blurPlaneDiagnostic.transform.position;
            Bounds bounds = blurPlaneDiagnostic.bounds;
            OrbRadiance.Instance?.Log("HiddenRoom INFO: native BlurPlane active=" + blurPlaneDiagnostic.gameObject.activeInHierarchy +
                " rendererEnabled=" + blurPlaneDiagnostic.enabled + " layer=" + blurPlaneDiagnostic.gameObject.layer +
                " posZ=" + F(position.z) + " boundsMin=(" + F(bounds.min.x) + "," + F(bounds.min.y) + "," + F(bounds.min.z) +
                ") boundsMax=(" + F(bounds.max.x) + "," + F(bounds.max.y) + "," + F(bounds.max.z) + ")");
        }

        private static void LogRendererDiagnostic(string label, SpriteRenderer renderer)
        {
            if (renderer == null)
            {
                OrbRadiance.Instance?.Log("HiddenRoom INFO: " + label + "=<missing>");
                return;
            }
            Vector3 position = renderer.transform.position;
            Material material = renderer.sharedMaterial;
            Shader shader = material == null ? null : material.shader;
            OrbRadiance.Instance?.Log("HiddenRoom INFO: " + label + " pos=(" + F(position.x) + "," + F(position.y) + "," + F(position.z) +
                ") sprite=" + (renderer.sprite == null ? "<null>" : renderer.sprite.name) + " alpha=" + F(renderer.color.a) +
                " enabled=" + renderer.enabled + " active=" + renderer.gameObject.activeInHierarchy + " layer=" + renderer.gameObject.layer +
                " sortingLayer=" + renderer.sortingLayerID + " sortingLayerName=" + SortingLayer.IDToName(renderer.sortingLayerID) + " sortingOrder=" + renderer.sortingOrder +
                " shader=" + (shader == null ? "<null>" : shader.name) + " queue=" + (material == null ? "<null>" : material.renderQueue.ToString()));
        }

        private static void UpdateVisualProbe()
        {
            if (!installed || !open || !IsCurrentScene || installedScene != UnityEngine.SceneManagement.SceneManager.GetActiveScene())
            {
                heroStableSince = -1f;
                inspectStateCaptured = false;
                return;
            }
            try
            {
                HeroController hero = HeroController.instance;
                Vector3 heroPosition = hero == null ? Vector3.zero : hero.transform.position;
                bool heroInRoom = hero != null && heroPosition.x >= roomLeft && heroPosition.x <= roomRight && heroPosition.y > 34.5f && heroPosition.y < 47.5f;
                if (!heroInRoom) heroStableSince = -1f;
                else if (!heroProbeLogged)
                {
                    if (heroStableSince < 0f) heroStableSince = Time.unscaledTime;
                    else if (Time.unscaledTime - heroStableSince >= .5f)
                    {
                        CaptureVisualProbe("hero-room-stable-0.5s");
                        heroProbeLogged = true;
                        heroStableSince = -1f;
                    }
                }

                CameraLockArea lockArea = hiddenStatue == null ? null : hiddenStatue.cameraLock;
                bool lockActive = lockArea != null && lockArea.isActiveAndEnabled;
                if (!inspectStateCaptured)
                {
                    lastInspectCameraLockActive = lockActive;
                    inspectStateCaptured = true;
                }
                else if (lockActive != lastInspectCameraLockActive)
                {
                    lastInspectCameraLockActive = lockActive;
                    pendingInspectProbeLabel = lockActive ? "InspectEnter" : "InspectExit";
                }
                if (!string.IsNullOrEmpty(pendingInspectProbeLabel) && Time.unscaledTime >= nextInspectProbeAt)
                {
                    string label = pendingInspectProbeLabel;
                    pendingInspectProbeLabel = null;
                    nextInspectProbeAt = Time.unscaledTime + .35f;
                    CaptureVisualProbe(label);
                }
            }
            catch (Exception e) { OrbRadiance.Instance?.LogWarn("HiddenRoomProbe: event probe failed safely: " + e); }
        }

        private static string pendingInspectProbeLabel;
        private static float nextInspectProbeAt;

        private static void CaptureVisualProbe(string label)
        {
            try
            {
                HeroController hero = HeroController.instance;
                Vector3 heroPosition = hero == null ? Vector3.zero : hero.transform.position;
                ProbeLog("SNAPSHOT " + label + " scene=" + installedScene.name + " roomOpen=" + open +
                    " heroPos=(" + F(heroPosition.x) + "," + F(heroPosition.y) + "," + F(heroPosition.z) + ")");
                GameCameras gameCameras = GameCameras.instance;
                Camera mainCamera = gameCameras == null ? null : gameCameras.mainCamera;
                Camera sceneCamera = gameCameras == null || gameCameras.tk2dCam == null ? null : gameCameras.tk2dCam.GetComponent<Camera>();
                Transform blurTransform = sceneCamera == null ? null : sceneCamera.transform.Find("BlurCamera");
                Camera blurCamera = blurTransform == null ? null : blurTransform.GetComponent<Camera>();
                LogInspectCameraTargetProbe();
                LogCameraProbe("mainCamera", mainCamera);
                LogCameraProbe("tk2dCamCamera", sceneCamera);
                LogCameraProbe("BlurCamera", blurCamera);
                ProbeLog("LeftWallArch exactPath=" + WorkshopRoomAssets.LeftWallArchPath + " copiedDecorExists=" + (copiedDecorRoot != null) + " sourceFound=" + (leftWallArchSource != null) + " copyCreated=" + (leftWallArchCopy != null));
                LogSpritePairProbe("LeftWallArch", leftWallArchSource, leftWallArchCopy, mainCamera, sceneCamera, blurCamera);
                LogSceneSpriteScreenOverlaps(leftWallArchCopy, installedScene, mainCamera);
                LogSpritePairProbe("dream_nail_base", dreamBaseSource, dreamBaseCopy, mainCamera, sceneCamera, blurCamera);
                LogSpritePairProbe("gg_ascent_backing_0000_2", ascentBacking0000Source, ascentBacking0000Copy, mainCamera, sceneCamera, blurCamera);
                LogSpritePairProbe("gg_ascent_backing_0001_1", ascentBacking0001Source, ascentBacking0001Copy, mainCamera, sceneCamera, blurCamera);
                LogBlurPlaneProbe();
                LightBlurredBackground blurredBackground = gameCameras == null ? null : gameCameras.GetComponent<LightBlurredBackground>();
                ProbeLog("LightBlurredBackground exists=" + (blurredBackground != null) +
                    " enabled=" + (blurredBackground != null && blurredBackground.enabled) +
                    " active=" + (blurredBackground != null && blurredBackground.gameObject.activeInHierarchy));
            }
            catch (Exception e) { OrbRadiance.Instance?.LogWarn("HiddenRoomProbe: snapshot failed safely (" + label + "): " + e); }
        }

        private static void LogSceneSpriteScreenOverlaps(SpriteRenderer target, Scene scene, Camera cameraToTest)
        {
            if (target == null || !scene.IsValid() || cameraToTest == null)
            {
                ProbeLog("LeftWallArch screenOverlapProbe skipped target=" + (target != null) + " sceneValid=" + scene.IsValid() + " camera=" + (cameraToTest != null));
                return;
            }
            Rect targetViewport = ViewportBounds(cameraToTest, target.bounds);
            Plane[] cameraPlanes = GeometryUtility.CalculateFrustumPlanes(cameraToTest);
            var overlaps = new List<(SpriteRenderer renderer, Vector3 viewport, Rect screenBounds)>();
            foreach (GameObject sceneRoot in scene.GetRootGameObjects())
            {
                foreach (SpriteRenderer candidate in sceneRoot.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (candidate == null || candidate == target || !candidate.enabled || !candidate.gameObject.activeInHierarchy) continue;
                    if (candidate.gameObject.layer < 0 || candidate.gameObject.layer >= 32 ||
                        (cameraToTest.cullingMask & (1 << candidate.gameObject.layer)) == 0) continue;
                    if (!GeometryUtility.TestPlanesAABB(cameraPlanes, candidate.bounds)) continue;
                    Rect candidateViewport = ViewportBounds(cameraToTest, candidate.bounds);
                    if (!targetViewport.Overlaps(candidateViewport)) continue;
                    Vector3 viewportCenter = cameraToTest.WorldToViewportPoint(candidate.bounds.center);
                    if (viewportCenter.z <= cameraToTest.nearClipPlane || viewportCenter.z >= cameraToTest.farClipPlane) continue;
                    overlaps.Add((candidate, viewportCenter, candidateViewport));
                }
            }
            overlaps.Sort((a, b) => a.viewport.z.CompareTo(b.viewport.z));
            int limit = Math.Min(overlaps.Count, 24);
            for (int i = 0; i < limit; i++)
            {
                var hit = overlaps[i];
                SpriteRenderer candidate = hit.renderer;
                Bounds bounds = candidate.bounds;
                Material material = candidate.sharedMaterial;
                ProbeLog("LeftWallArch sameScreenRenderer rankByCameraDepth=" + i + " name=" + candidate.gameObject.name +
                    " sprite=" + (candidate.sprite == null ? "<null>" : candidate.sprite.name) +
                    " viewportDepth=" + F(hit.viewport.z) + " viewportRect=(" + F(hit.screenBounds.xMin) + "," + F(hit.screenBounds.yMin) + "," + F(hit.screenBounds.width) + "," + F(hit.screenBounds.height) +
                    ") worldBounds=" + BoundsText(bounds) + " sortingLayer=" + candidate.sortingLayerID +
                    " sortingLayerName=" + SortingLayer.IDToName(candidate.sortingLayerID) + " sortingOrder=" + candidate.sortingOrder +
                    " colorAlpha=" + F(candidate.color.a) + " material=" + (material == null ? "<null>" : material.name) +
                    " renderQueue=" + (material == null ? -1 : material.renderQueue));
            }
            ProbeLog("LeftWallArch sameScreenRendererCount=" + overlaps.Count + " logged=" + limit + " targetViewportRect=(" +
                F(targetViewport.xMin) + "," + F(targetViewport.yMin) + "," + F(targetViewport.width) + "," + F(targetViewport.height) + ")");
        }

        private static Rect ViewportBounds(Camera cameraToTest, Bounds bounds)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    for (int z = 0; z < 2; z++)
                    {
                        Vector3 corner = new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
                        Vector3 viewport = cameraToTest.WorldToViewportPoint(corner);
                        minX = Mathf.Min(minX, viewport.x); minY = Mathf.Min(minY, viewport.y);
                        maxX = Mathf.Max(maxX, viewport.x); maxY = Mathf.Max(maxY, viewport.y);
                    }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static void LogInspectCameraTargetProbe()
        {
            if (hiddenStatue == null)
            {
                ProbeLog("InspectCameraTarget statue=<missing>");
                return;
            }
            Vector3 statuePosition = hiddenStatue.transform.position;
            CameraLockArea lockArea = hiddenStatue.cameraLock;
            if (lockArea == null)
            {
                ProbeLog("InspectCameraTarget inspectCameraHeight=" + F(hiddenStatue.inspectCameraHeight) +
                    " statueWorldY=" + F(statuePosition.y) + " targetY=" + F(statuePosition.y + hiddenStatue.inspectCameraHeight) + " cameraLock=<missing>");
                return;
            }
            ProbeLog("InspectCameraTarget inspectCameraHeight=" + F(hiddenStatue.inspectCameraHeight) +
                " statueWorldY=" + F(statuePosition.y) + " targetY=" + F(statuePosition.y + hiddenStatue.inspectCameraHeight) +
                " cameraLockX=(" + F(lockArea.cameraXMin) + "," + F(lockArea.cameraXMax) +
                ") cameraLockY=(" + F(lockArea.cameraYMin) + "," + F(lockArea.cameraYMax) +
                ") maxPriority=" + lockArea.maxPriority + " active=" + lockArea.gameObject.activeInHierarchy + " enabled=" + lockArea.enabled);
        }

        private static void LogCameraProbe(string label, Camera cameraToLog)
        {
            if (cameraToLog == null) { ProbeLog(label + "=<missing>"); return; }
            Vector3 position = cameraToLog.transform.position;
            Rect viewport = cameraToLog.rect;
            ProbeLog(label + " pos=(" + F(position.x) + "," + F(position.y) + "," + F(position.z) +
                ") near=" + F(cameraToLog.nearClipPlane) + " far=" + F(cameraToLog.farClipPlane) +
                " cullingMask=0x" + cameraToLog.cullingMask.ToString("X8") + " transparencySortMode=" + cameraToLog.transparencySortMode +
                " transparencySortAxis=(" + F(cameraToLog.transparencySortAxis.x) + "," + F(cameraToLog.transparencySortAxis.y) + "," + F(cameraToLog.transparencySortAxis.z) + ") viewport=(" + F(viewport.x) + "," + F(viewport.y) + "," + F(viewport.width) + "," + F(viewport.height) + ")");
        }

        private static void LogSpritePairProbe(string label, SpriteRenderer source, SpriteRenderer copy, Camera mainCamera, Camera sceneCamera, Camera blurCamera)
        {
            LogSpriteRendererProbe(label + ".source", source, mainCamera, sceneCamera, blurCamera);
            LogSpriteRendererProbe(label + ".copy", copy, mainCamera, sceneCamera, blurCamera);
        }

        private static void LogSpriteRendererProbe(string label, SpriteRenderer renderer, Camera mainCamera, Camera sceneCamera, Camera blurCamera)
        {
            if (renderer == null) { ProbeLog(label + "=<missing>"); return; }
            Vector3 position = renderer.transform.position;
            Bounds bounds = renderer.bounds;
            Color color = renderer.color;
            Sprite sprite = renderer.sprite;
            Texture spriteTexture = sprite == null ? null : sprite.texture;
            Material material = renderer.sharedMaterial;
            Shader shader = material == null ? null : material.shader;
            ProbeLog(label + " pos=(" + F(position.x) + "," + F(position.y) + "," + F(position.z) + ") bounds=" + BoundsText(bounds) +
                " colorRGBA=(" + F(color.r) + "," + F(color.g) + "," + F(color.b) + "," + F(color.a) + ") enabled=" + renderer.enabled +
                " activeSelf=" + renderer.gameObject.activeSelf + " active=" + renderer.gameObject.activeInHierarchy + " layer=" + renderer.gameObject.layer +
                " sortingLayer=" + renderer.sortingLayerID + " sortingOrder=" + renderer.sortingOrder +
                " sprite=" + (sprite == null ? "<null>" : sprite.name) + " spriteTexture=" + TextureText(spriteTexture) +
                " material=" + (material == null ? "<null>" : material.name) + " shader=" + (shader == null ? "<null>" : shader.name) +
                " shaderSupported=" + (shader != null && shader.isSupported) + " queue=" + (material == null ? -1 : material.renderQueue) +
                " mainTexture=" + TextureText(material == null ? null : material.mainTexture));
            LogRendererCameraProbe(label, renderer, mainCamera);
            LogRendererCameraProbe(label, renderer, sceneCamera);
            LogRendererCameraProbe(label, renderer, blurCamera);
        }

        private static void LogRendererCameraProbe(string label, SpriteRenderer renderer, Camera cameraToTest)
        {
            if (cameraToTest == null) return;
            int layer = renderer.gameObject.layer;
            bool layerIncluded = layer >= 0 && layer < 32 && (cameraToTest.cullingMask & (1 << layer)) != 0;
            Vector3 viewport = cameraToTest.WorldToViewportPoint(renderer.bounds.center);
            bool inFrustumBounds = GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(cameraToTest), renderer.bounds);
            ProbeLog(label + " camera=" + cameraToTest.name + " layerIncluded=" + layerIncluded +
                " boundsCenterViewport=(" + F(viewport.x) + "," + F(viewport.y) + "," + F(viewport.z) + ") frustumBoundsCheck=" + inFrustumBounds);
        }

        private static void LogBlurPlaneProbe()
        {
            if (blurPlaneDiagnostic == null) ProbeLog("copiedBlurPlane=<missing>");
            else ProbeLog("copiedBlurPlane active=" + blurPlaneDiagnostic.gameObject.activeInHierarchy + " enabled=" + blurPlaneDiagnostic.enabled +
                " layer=" + blurPlaneDiagnostic.gameObject.layer + " planeZ=" + F(blurPlaneDiagnostic.transform.position.z) + " bounds=" + BoundsText(blurPlaneDiagnostic.bounds) +
                " currentMaterial=" + MaterialText(blurPlaneDiagnostic.sharedMaterial));
            int count = BlurPlane.BlurPlaneCount;
            ProbeLog("BlurPlane.BlurPlaneCount=" + count);
            for (int i = 0; i < count && i < 8; i++)
            {
                BlurPlane plane = BlurPlane.GetBlurPlane(i);
                if (plane == null) { ProbeLog("BlurPlane[" + i + "]=<null>"); continue; }
                MeshRenderer renderer = plane.GetComponent<MeshRenderer>();
                ProbeLog("BlurPlane[" + i + "] name=" + plane.name + " active=" + plane.gameObject.activeInHierarchy +
                    " layer=" + plane.gameObject.layer + " planeZ=" + F(plane.PlaneZ) + " bounds=" + (renderer == null ? "<no-renderer>" : BoundsText(renderer.bounds)) +
                    " originalMaterial=" + MaterialText(plane.OriginalMaterial) + " currentMaterial=" + MaterialText(renderer == null ? null : renderer.sharedMaterial));
            }
        }

        private static string MaterialText(Material material)
        {
            if (material == null) return "<null>";
            Shader shader = material.shader;
            return material.name + "[shader=" + (shader == null ? "<null>" : shader.name) + ",supported=" + (shader != null && shader.isSupported) +
                ",queue=" + material.renderQueue + ",mainTexture=" + TextureText(material.mainTexture) + "]";
        }

        private static string TextureText(Texture texture)
        {
            if (texture == null) return "<null>";
            RenderTexture renderTexture = texture as RenderTexture;
            return texture.GetType().Name + ":" + texture.name + "[" + texture.width + "x" + texture.height +
                (renderTexture == null ? "" : ",created=" + renderTexture.IsCreated()) + "]";
        }

        private static string BoundsText(Bounds bounds)
        {
            return "min=(" + F(bounds.min.x) + "," + F(bounds.min.y) + "," + F(bounds.min.z) + ") max=(" +
                F(bounds.max.x) + "," + F(bounds.max.y) + "," + F(bounds.max.z) + ")";
        }

        private static string F(float value) => value.ToString("F3");

        private static void ProbeLog(string message)
        {
            OrbRadiance.Instance?.Log("HiddenRoomProbe: " + message);
        }

        private static void SetRoomClosed()
        {
            StopAmbientParticles();
            if (statueContainer != null) statueContainer.SetActive(false);
            if (roomRoot == null) return;
            foreach (Transform child in roomRoot.transform)
            {
                if (child.name == "Hidden nail seam") continue;
                if (child.GetComponent<CameraLockArea>() != null)
                {
                    Collider2D area = child.GetComponent<Collider2D>();
                    if (area != null) area.enabled = false;
                    child.gameObject.SetActive(true);
                }
                else child.gameObject.SetActive(false);
            }
        }

        private static void MaintainBounds()
        {
            CameraController candidate = GameCameras.instance == null ? null : GameCameras.instance.cameraController;
            if (candidate == null || candidate.tilemap == null || candidate.tilemap.gameObject.scene != installedScene) return;
            if (!boundsCaptured)
            {
                camera = candidate; gameManager = GameManager.UnsafeInstance;
                oldSceneWidth = camera.sceneWidth; oldXLimit = camera.xLimit;
                oldGameSceneWidth = gameManager == null ? 0f : gameManager.sceneWidth;
                boundsCaptured = true;
            }
            float wantedWidth = roomRight + 14.6f;
            camera.sceneWidth = Mathf.Max(oldSceneWidth, wantedWidth);
            camera.xLimit = Mathf.Max(oldXLimit, camera.sceneWidth - 14.6f);
            if (gameManager != null) gameManager.sceneWidth = Mathf.Max(oldGameSceneWidth, wantedWidth);
        }

        internal static void Cleanup(bool returnPlayer)
        {
            if (returnPlayer && installed && open)
            {
                HeroController hero = HeroController.instance;
                Vector3 position = hero == null ? Vector3.zero : hero.transform.position;
                bool inAddedArea = position.x > originX - .3f && position.x < roomRight + .3f &&
                    position.y > 34.5f && position.y < (position.x < roomLeft ? 39.5f : 47.5f);
                if (hero != null && installedScene == UnityEngine.SceneManagement.SceneManager.GetActiveScene() && inAddedArea)
                {
                    hero.transform.position = new Vector3(257f, 36.5f, hero.transform.position.z);
                    Rigidbody2D body = hero.GetComponent<Rigidbody2D>(); if (body != null) body.velocity = Vector2.zero;
                }
            }
            StopAmbientParticles();
            if (roomRoot != null) roomRoot.SetActive(false);
            bool stillActive = installedScene == UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (stillActive && boundsCaptured && camera != null) { camera.sceneWidth = oldSceneWidth; camera.xLimit = oldXLimit; }
            if (stillActive && boundsCaptured && gameManager != null) gameManager.sceneWidth = oldGameSceneWidth;
            if (terrain != null) terrain.Restore(); terrain = null;
            foreach (UnityEngine.Object item in owned)
            {
                if (item == null) continue;
                if (item is GameObject go) { go.SetActive(false); UnityEngine.Object.Destroy(go); }
                else UnityEngine.Object.Destroy(item);
            }
            owned.Clear(); camera = null; gameManager = null; boundsCaptured = false; roomRoot = null; statueContainer = null; ambientParticlesCopy = null;
            dreamBaseSource = dreamBaseCopy = null; leftWallArchSource = leftWallArchCopy = null; copiedDecorRoot = null; ascentBacking0000Source = ascentBacking0000Copy = null; ascentBacking0001Source = ascentBacking0001Copy = null;
            blurPlaneDiagnostic = null; hiddenStatue = null; openedDiagnosticsLogged = false; heroProbeLogged = false; inspectStateCaptured = false;
            lastInspectCameraLockActive = false; heroStableSince = -1f; pendingInspectProbeLabel = null; nextInspectProbeAt = 0f;
            open = false; installed = false; installedScene = default(Scene); waitingForReturnScene = false;
        }
    }
}


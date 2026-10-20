using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace OrbRadiance
{
    internal sealed class WorkshopTerrainPatch
    {
        private BoxCollider2D wall;
        private bool wallWasEnabled;
        private MeshFilter filter;
        private Mesh originalMesh, cloneMesh;
        private EdgeCollider2D originalEdge;
        private bool edgeWasEnabled;
        private bool edgeCaptured, edgeModified, wallCaptured, wallModified, meshChanged;
        private readonly List<GameObject> owned = new List<GameObject>();
        private bool active;

        internal bool Apply(Scene scene, float wallX, Rect opening)
        {
            if (active) return true;
            try
            {
                Transform root = FindPath(scene, "Template-TileMap (1) Render Data/Scenemap/Chunk 1 8");
                if (root == null) throw new InvalidOperationException("Exact Scenemap path missing");
                originalEdge = root.GetComponent<EdgeCollider2D>();
                filter = root.GetComponent<MeshFilter>();
                if (originalEdge == null) throw new InvalidOperationException("Chunk 1 8 EdgeCollider missing");
                edgeWasEnabled = originalEdge.enabled;
                edgeCaptured = true;
                if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("Chunk 1 8 MeshFilter/sharedMesh missing");
                var edgeHost = new GameObject("Wind Hidden Room clipped edge");
                owned.Add(edgeHost);
                edgeHost.layer = 8;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(edgeHost, scene);
                edgeHost.SetActive(edgeWasEnabled);
                edgeHost.transform.position = Vector3.zero;
                var localPoints = originalEdge.points;
                var worldPoints = new Vector2[localPoints.Length];
                for (int i = 0; i < localPoints.Length; i++) worldPoints[i] = root.TransformPoint(localPoints[i] + originalEdge.offset);
                foreach (Vector2[] path in HiddenRoomGeometry.ClipEdge(worldPoints, opening))
                {
                    var edge = edgeHost.AddComponent<EdgeCollider2D>();
                    edge.sharedMaterial = originalEdge.sharedMaterial;
                    edge.isTrigger = originalEdge.isTrigger;
                    edge.points = Array.ConvertAll(path, p => (Vector2)edgeHost.transform.InverseTransformPoint(p));
                }
                if (edgeWasEnabled) { originalEdge.enabled = false; edgeModified = true; }

                originalMesh = filter.sharedMesh;
                cloneMesh = UnityEngine.Object.Instantiate(originalMesh);
                cloneMesh.name = "Wind Hidden Room clipped scenemap mesh";
                int subCount = originalMesh.subMeshCount;
                for (int s = 0; s < subCount; s++)
                {
                    int[] source = originalMesh.GetTriangles(s);
                    var worldVertices = new Vector3[originalMesh.vertexCount];
                    Vector3[] verts = originalMesh.vertices;
                    for (int i = 0; i < verts.Length; i++) worldVertices[i] = root.TransformPoint(verts[i]);
                    cloneMesh.SetTriangles(HiddenRoomGeometry.KeepTriangles(worldVertices, source, opening), s, false);
                }
                cloneMesh.RecalculateBounds();
                filter.sharedMesh = cloneMesh; meshChanged = true;

                Transform wallTransform = FindPath(scene, "wall collider (1)");
                wall = wallTransform == null ? null : wallTransform.GetComponent<BoxCollider2D>();
                if (wall == null) throw new InvalidOperationException("wall collider (1) BoxCollider2D missing");
                Bounds bounds = wall.bounds;
                if (Mathf.Abs(bounds.min.x - wallX) > 0.2f) throw new InvalidOperationException("wall bounds did not match confirmed origin");
                wallWasEnabled = wall.enabled;
                wallCaptured = true;
                // 保留原版墙体组件；使用独立的上下 BoxCollider 形成四单位高的入口。
                if (wallWasEnabled) { wall.enabled = false; wallModified = true; }
                AddBox(scene, "Wind Hidden Room wall upper", new Vector2(bounds.center.x, (opening.yMax + bounds.max.y) * .5f), new Vector2(bounds.size.x, Mathf.Max(.01f, bounds.max.y - opening.yMax)), wallWasEnabled);
                AddBox(scene, "Wind Hidden Room wall lower", new Vector2(bounds.center.x, (bounds.min.y + opening.yMin) * .5f), new Vector2(bounds.size.x, Mathf.Max(.01f, opening.yMin - bounds.min.y)), wallWasEnabled);
                active = true;
                return true;
            }
            catch
            {
                Restore();
                throw;
            }
        }

        private void AddBox(Scene scene, string name, Vector2 center, Vector2 size, bool enabled)
        {
            GameObject go = new GameObject(name); owned.Add(go); go.layer = 8; UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            go.transform.position = center; var box = go.AddComponent<BoxCollider2D>(); box.size = size; box.enabled = enabled;
        }
        internal void Restore()
        {
            if (wallCaptured && wallModified && wall != null) wall.enabled = wallWasEnabled;
            if (edgeCaptured && edgeModified && originalEdge != null) originalEdge.enabled = edgeWasEnabled;
            if (meshChanged && filter != null && originalMesh != null) filter.sharedMesh = originalMesh;
            if (cloneMesh != null) UnityEngine.Object.Destroy(cloneMesh);
            cloneMesh = null;
            foreach (GameObject go in owned) if (go != null) { go.SetActive(false); UnityEngine.Object.Destroy(go); }
            owned.Clear(); active = false; wall = null; filter = null; originalEdge = null; originalMesh = null;
            edgeCaptured = edgeModified = wallCaptured = wallModified = meshChanged = false;
        }

        internal static Transform FindPath(Scene scene, string path)
        {
            string[] parts = path.Split('/');
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                Transform t = root.transform; bool match = true;
                for (int i = 1; i < parts.Length; i++) { t = t.Find(parts[i]); if (t == null) { match = false; break; } }
                if (match) return t;
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace OrbRadiance
{
    internal static class WorkshopArchFacade
    {
        internal const float WindowLeft = 297.95f;
        internal const float WindowRight = 320.81f;
        internal const float WallLeft = 290.5f;
        internal const float WallRight = 330.5f;
        private const float TopY = 60f;
        private static readonly Vector2[] InnerProfile =
        {
            new Vector2(297.9550f, 42.0850f),
            new Vector2(298.0550f, 41.1850f),
            new Vector2(298.1950f, 40.8350f),
            new Vector2(298.2850f, 40.7250f),
            new Vector2(298.4150f, 40.6550f),
            new Vector2(298.6250f, 40.6550f),
            new Vector2(298.9450f, 40.7350f),
            new Vector2(299.2550f, 40.8650f),
            new Vector2(299.3650f, 40.9550f),
            new Vector2(299.4250f, 41.4450f),
            new Vector2(299.4950f, 41.7250f),
            new Vector2(299.6250f, 42.0350f),
            new Vector2(299.8250f, 42.3250f),
            new Vector2(300.2350f, 42.7850f),
            new Vector2(300.7050f, 43.1450f),
            new Vector2(300.9450f, 43.2650f),
            new Vector2(301.3150f, 43.3950f),
            new Vector2(301.8150f, 43.4750f),
            new Vector2(302.2050f, 43.4550f),
            new Vector2(302.7650f, 43.3050f),
            new Vector2(302.9650f, 43.3350f),
            new Vector2(303.1350f, 43.4050f),
            new Vector2(303.1850f, 43.5250f),
            new Vector2(303.5750f, 44.0250f),
            new Vector2(303.6350f, 43.5450f),
            new Vector2(303.7050f, 43.3950f),
            new Vector2(303.8350f, 43.2450f),
            new Vector2(304.1650f, 43.2050f),
            new Vector2(304.7350f, 43.3250f),
            new Vector2(304.8650f, 43.4250f),
            new Vector2(304.9950f, 43.9750f),
            new Vector2(305.2350f, 44.4550f),
            new Vector2(305.6950f, 44.9450f),
            new Vector2(306.2250f, 45.3250f),
            new Vector2(306.6850f, 45.5150f),
            new Vector2(307.1250f, 45.6050f),
            new Vector2(307.5050f, 45.6150f),
            new Vector2(307.9350f, 45.5450f),
            new Vector2(308.4650f, 45.3150f),
            new Vector2(308.7350f, 45.3150f),
            new Vector2(308.8650f, 45.3550f),
            new Vector2(308.9150f, 45.4150f),
            new Vector2(309.0550f, 45.8250f),
            new Vector2(309.0950f, 45.2750f),
            new Vector2(309.1850f, 45.1450f),
            new Vector2(309.2750f, 45.1250f),
            new Vector2(309.5850f, 45.1450f),
            new Vector2(310.1750f, 45.4650f),
            new Vector2(310.4550f, 45.5450f),
            new Vector2(310.7450f, 45.5850f),
            new Vector2(310.9950f, 45.5950f),
            new Vector2(311.4750f, 45.5450f),
            new Vector2(311.7450f, 45.4750f),
            new Vector2(312.0350f, 45.3550f),
            new Vector2(312.4650f, 45.1150f),
            new Vector2(313.0050f, 44.6650f),
            new Vector2(313.2250f, 44.3450f),
            new Vector2(313.4650f, 43.7450f),
            new Vector2(313.6050f, 43.6650f),
            new Vector2(314.1950f, 43.6150f),
            new Vector2(314.4850f, 43.6750f),
            new Vector2(314.5950f, 43.7750f),
            new Vector2(314.6750f, 43.9450f),
            new Vector2(314.7050f, 44.1150f),
            new Vector2(315.1550f, 43.5550f),
            new Vector2(315.1950f, 43.5150f),
            new Vector2(315.3350f, 43.4750f),
            new Vector2(315.6250f, 43.4650f),
            new Vector2(316.1950f, 43.6650f),
            new Vector2(316.4850f, 43.7050f),
            new Vector2(317.1050f, 43.6750f),
            new Vector2(317.7950f, 43.4750f),
            new Vector2(318.2450f, 43.2150f),
            new Vector2(318.7650f, 42.7750f),
            new Vector2(319.0550f, 42.4450f),
            new Vector2(319.2250f, 42.1250f),
            new Vector2(319.4150f, 41.4150f),
            new Vector2(319.5350f, 41.3350f),
            new Vector2(319.8950f, 41.2250f),
            new Vector2(320.1350f, 41.1850f),
            new Vector2(320.4550f, 41.1950f),
            new Vector2(320.5550f, 41.2450f),
            new Vector2(320.6550f, 41.3650f),
            new Vector2(320.7150f, 41.4750f),
            new Vector2(320.7850f, 41.7350f),
            new Vector2(320.8100f, 42.3100f)
        };

        internal static GameObject Create(GameObject parent, List<UnityEngine.Object> owned, Material fallbackMaterial)
        {
            var fill = new GameObject("Arch facade contour fill");
            fill.transform.SetParent(parent.transform, false);
            fill.transform.position = new Vector3(310.5f, 0f, 0f);
            fill.layer = 0;

            var mesh = new Mesh { name = "Native arch contour facade fill", hideFlags = HideFlags.HideAndDontSave };
            BuildMesh(mesh);
            owned.Add(mesh);

            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "Arch facade white texel", hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            owned.Add(texture);

            Shader shader = Shader.Find("Sprites/Default");
            Material material = shader != null ? new Material(shader) : (fallbackMaterial != null ? new Material(fallbackMaterial) : null);
            if (material == null) throw new InvalidOperationException("No usable sprite shader/material for the arch facade fill");
            material.name = "Arch facade contour black";
            material.color = Color.black;
            material.mainTexture = texture;
            material.hideFlags = HideFlags.HideAndDontSave;
            owned.Add(material);

            fill.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = fill.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.sortingOrder = 0;
            return fill;
        }

        private static void BuildMesh(Mesh mesh)
        {
            float leftArchY = InterpolateProfileY(302f);
            var profile = new List<Vector2>(InnerProfile.Length + 3)
            {
                new Vector2(WallLeft, 39f),
                new Vector2(302f, 39f),
                new Vector2(302f, leftArchY)
            };
            foreach (Vector2 point in InnerProfile)
                if (point.x > 302f) profile.Add(point);
            profile.Add(new Vector2(WindowRight, 35f));
            profile.Add(new Vector2(320.90f, 35f));
            profile.Add(new Vector2(WallRight, 35f));

            var vertices = new Vector3[profile.Count * 2];
            var triangles = new int[(profile.Count - 1) * 6];
            var uv = new Vector2[vertices.Length];
            var colors = new Color[vertices.Length];
            for (int i = 0; i < profile.Count; i++)
            {
                float x = profile[i].x - 310.5f;
                vertices[i * 2] = new Vector3(x, profile[i].y, 0f);
                vertices[i * 2 + 1] = new Vector3(x, TopY, 0f);
                uv[i * 2] = uv[i * 2 + 1] = new Vector2(.5f, .5f);
                colors[i * 2] = colors[i * 2 + 1] = Color.white;
                if (i == profile.Count - 1) continue;
                int next = (i + 1) * 2;
                int at = i * 6;
                triangles[at] = i * 2;
                triangles[at + 1] = next;
                triangles[at + 2] = i * 2 + 1;
                triangles[at + 3] = next;
                triangles[at + 4] = next + 1;
                triangles[at + 5] = i * 2 + 1;
            }
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors = colors;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
        }

        private static float InterpolateProfileY(float x)
        {
            for (int i = 1; i < InnerProfile.Length; i++)
            {
                Vector2 a = InnerProfile[i - 1];
                Vector2 b = InnerProfile[i];
                if (x < a.x || x > b.x) continue;
                float t = (x - a.x) / (b.x - a.x);
                return a.y + (b.y - a.y) * t;
            }
            throw new InvalidOperationException("Left wall X falls outside the arch profile");
        }
    }
}

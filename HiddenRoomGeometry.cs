using System;
using System.Collections.Generic;
using UnityEngine;

namespace OrbRadiance
{
    internal static class HiddenRoomGeometry
    {
        // 返回折线位于矩形严格内部以外的片段，支持闭合或开放折线。
        internal static List<Vector2[]> ClipEdge(Vector2[] points, Rect opening)
        {
            var paths = new List<Vector2[]>();
            if (points == null || points.Length < 2) return paths;
            var current = new List<Vector2>();
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector2 a = points[i], b = points[i + 1];
                List<Vector2[]> pieces = ClipSegment(a, b, opening);
                foreach (Vector2[] piece in pieces)
                {
                    if (piece.Length < 2 || (piece[0] - piece[1]).sqrMagnitude < 1e-10f) continue;
                    if (current.Count == 0) current.Add(piece[0]);
                    else if ((current[current.Count - 1] - piece[0]).sqrMagnitude > 1e-8f)
                    {
                        if (current.Count > 1) paths.Add(current.ToArray());
                        current.Clear(); current.Add(piece[0]);
                    }
                    if ((current[current.Count - 1] - piece[1]).sqrMagnitude > 1e-8f) current.Add(piece[1]);
                }
                if (pieces.Count == 0 || (pieces.Count > 0 && (pieces[pieces.Count - 1][1] - b).sqrMagnitude > 1e-8f))
                {
                    if (current.Count > 1) paths.Add(current.ToArray());
                    current.Clear();
                }
            }
            if (current.Count > 1) paths.Add(current.ToArray());
            return paths;
        }

        private static List<Vector2[]> ClipSegment(Vector2 a, Vector2 b, Rect r)
        {
            var result = new List<Vector2[]>();
            if ((a.x <= r.xMin && b.x <= r.xMin) || (a.x >= r.xMax && b.x >= r.xMax) ||
                (a.y <= r.yMin && b.y <= r.yMin) || (a.y >= r.yMax && b.y >= r.yMax))
            {
                result.Add(new[] { a, b });
                return result;
            }
            Vector2 d = b - a;
            float t0 = 0f, t1 = 1f;
            // 严格内部不包括矩形边界；恰好贴在边界上的线段保留。
            float left = r.xMin + 1e-5f, right = r.xMax - 1e-5f;
            float bottom = r.yMin + 1e-5f, top = r.yMax - 1e-5f;
            bool intersects = LiangBarsky(a, d, left, right, bottom, top, out t0, out t1);
            if (!intersects || t1 <= t0) { result.Add(new[] { a, b }); return result; }
            Vector2 p = a + d * t0, q = a + d * t1;
            if (t0 > 1e-6f) result.Add(new[] { a, p });
            if (t1 < 1f - 1e-6f) result.Add(new[] { q, b });
            return result;
        }

        private static bool LiangBarsky(Vector2 p, Vector2 d, float xmin, float xmax, float ymin, float ymax, out float lo, out float hi)
        {
            lo = 0f; hi = 1f;
            return Clip(-d.x, p.x - xmin, ref lo, ref hi) && Clip(d.x, xmax - p.x, ref lo, ref hi) &&
                   Clip(-d.y, p.y - ymin, ref lo, ref hi) && Clip(d.y, ymax - p.y, ref lo, ref hi) && hi >= lo;
        }
        private static bool Clip(float p, float q, ref float lo, ref float hi)
        {
            if (Mathf.Abs(p) < 1e-8f) return q >= 0f;
            float t = q / p;
            if (p < 0f) { if (t > hi) return false; if (t > lo) lo = t; }
            else { if (t < lo) return false; if (t < hi) hi = t; }
            return true;
        }

        internal static int[] KeepTriangles(Vector3[] vertices, int[] triangles, Rect opening)
        {
            if (vertices == null || triangles == null) return new int[0];
            var kept = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) continue;
                if (!TriangleIntersectsRect(vertices[a], vertices[b], vertices[c], opening))
                { kept.Add(a); kept.Add(b); kept.Add(c); }
            }
            return kept.ToArray();
        }
        private static bool TriangleIntersectsRect(Vector3 a, Vector3 b, Vector3 c, Rect r)
        {
            var polygon = new List<Vector2> { a, b, c };
            polygon = ClipPolygon(polygon, 0, r.xMin, true);
            polygon = ClipPolygon(polygon, 0, r.xMax, false);
            polygon = ClipPolygon(polygon, 1, r.yMin, true);
            polygon = ClipPolygon(polygon, 1, r.yMax, false);
            if (polygon.Count < 3) return false;
            double twiceArea = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 p = polygon[i], q = polygon[(i + 1) % polygon.Count];
                twiceArea += (double)p.x * q.y - (double)q.x * p.y;
            }
            // 仅接触开口边缘的三角形裁剪面积为零，原样保留。
            return Math.Abs(twiceArea) > 1e-8;
        }
        private static List<Vector2> ClipPolygon(List<Vector2> input, int axis, float boundary, bool greater)
        {
            var output = new List<Vector2>();
            if (input.Count == 0) return output;
            Vector2 previous = input[input.Count - 1];
            float prevValue = axis == 0 ? previous.x : previous.y;
            bool prevInside = greater ? prevValue >= boundary : prevValue <= boundary;
            foreach (Vector2 current in input)
            {
                float value = axis == 0 ? current.x : current.y;
                bool inside = greater ? value >= boundary : value <= boundary;
                if (inside != prevInside)
                {
                    float denom = value - prevValue;
                    if (Mathf.Abs(denom) > 1e-12f)
                    {
                        float t = (boundary - prevValue) / denom;
                        output.Add(previous + (current - previous) * t);
                    }
                }
                if (inside) output.Add(current);
                previous = current; prevValue = value; prevInside = inside;
            }
            return output;
        }
    }
}

using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OrbRadiance
{
    internal sealed class BluePhaseCountdownHud : MonoBehaviour
    {
        private BluePhaseCountdownGraphic ring;

        internal static BluePhaseCountdownHud Create(Scene scene)
        {
            GameObject canvasObject = new GameObject("Blue Phase Countdown HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(canvasObject, scene);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32000;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            GameObject ringObject = new GameObject("Countdown Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(BluePhaseCountdownGraphic));
            ringObject.transform.SetParent(canvasObject.transform, false);
            RectTransform rect = ringObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(80f, 80f);
            rect.sizeDelta = new Vector2(64f, 64f);

            BluePhaseCountdownHud hud = canvasObject.AddComponent<BluePhaseCountdownHud>();
            hud.ring = ringObject.GetComponent<BluePhaseCountdownGraphic>();
            hud.ring.color = new Color32(216, 180, 106, 255);
            hud.ring.raycastTarget = false;
            hud.ring.SetRemaining(1f);
            return hud;
        }

        internal void SetRemaining(float amount)
        {
            if (ring != null) ring.SetRemaining(amount);
        }

        internal void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
        }
    }

    internal sealed class BluePhaseCountdownGraphic : Graphic
    {
        private const int FullCircleSegments = 96;
        private float remaining = 1f;

        internal void SetRemaining(float amount)
        {
            float next = Mathf.Clamp01(amount);
            if (Mathf.Abs(remaining - next) < .0001f) return;
            remaining = next;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vertexHelper)
        {
            vertexHelper.Clear();
            if (remaining <= 0f) return;

            Rect rect = rectTransform.rect;
            float radius = Mathf.Min(rect.width, rect.height) * .5f;
            int segments = FullCircleSegments;
            float startAngle = 90f - (1f - remaining) * 360f;
            float sweep = -360f * remaining;
            Color32 vertexColor = color;

            vertexHelper.AddVert(Vector3.zero, vertexColor, Vector2.zero);
            int perimeterStart = vertexHelper.currentVertCount;
            for (int i = 0; i <= segments; i++)
            {
                float angle = (startAngle + sweep * i / segments) * Mathf.Deg2Rad;
                Vector3 point = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
                vertexHelper.AddVert(point, vertexColor, Vector2.zero);
            }

            for (int i = 0; i < segments; i++)
                vertexHelper.AddTriangle(0, perimeterStart + i, perimeterStart + i + 1);
        }
    }
}

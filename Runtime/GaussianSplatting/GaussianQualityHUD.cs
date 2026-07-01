using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static GaussianSplatting.Runtime.GaussianSplatAsset;

namespace GaussianSplatting.Runtime
{
    [AddComponentMenu("Gaussian Splatting/Quality HUD")]
    public class GaussianQualityHUD : MonoBehaviour
    {
        [Tooltip("Font size for the tier name headline")]
        public int headlineFontSize = 18;
        [Tooltip("Font size for the format detail lines")]
        public int detailFontSize = 13;
        [Tooltip("Margin from the right edge of the screen (pixels)")]
        public float rightMargin = 16f;
        [Tooltip("Margin from the top of the screen (pixels)")]
        public float topMargin = 16f;
        [Tooltip("Vertical gap between label boxes (pixels)")]
        public float boxGap = 8f;

        const float k_BoxW = 170f;
        const float k_BoxH = 84f;
        const float k_DotRadius = 14f;
        const float k_DotBorder = 3f;

        // Renderers self-register here on OnEnable, deregister on OnDisable.
        // Slot index = index in this list. No per-frame scene search needed.
        static readonly List<GaussianSplatRenderer> s_Registered = new();

        public static void Register(GaussianSplatRenderer r)
        {
            if (!s_Registered.Contains(r)) s_Registered.Add(r);
        }

        public static void Unregister(GaussianSplatRenderer r)
        {
            s_Registered.Remove(r);
        }

        GUIStyle m_HeadlineStyle;
        GUIStyle m_DetailStyle;
        GUIStyle m_BoxStyle;
        readonly StringBuilder m_Sb = new();

        Texture2D m_CircleTex;
        Texture2D m_CircleBorderTex;

        void OnGUI()
        {
            EnsureStyles();

            var cam = Camera.main;
            if (cam == null) return;
            if (Event.current.type != EventType.Repaint) return;


            int slot = 0;
            foreach (var r in s_Registered)
            {
                if (r == null || !r.HasValidRenderSetup || r.asset == null) continue;

                var asset = r.asset;
                Vector3 worldCenter = r.transform.TransformPoint((asset.boundsMin + asset.boundsMax) * 0.5f);
                Vector3 sp = cam.WorldToScreenPoint(worldCenter);
                if (sp.z <= 0) continue;


                float splatGuiY = Screen.height - sp.y;
                var splatGuiPos = new Vector2(sp.x, splatGuiY);

                string tier = GetTierName(asset.posFormat, asset.scaleFormat, asset.colorFormat, asset.shFormat);
                Color tierColor = GetTierColor(tier);

                float boxX = Screen.width - rightMargin - k_BoxW;
                float boxY = topMargin + slot * (k_BoxH + boxGap);
                var boxRect = new Rect(boxX, boxY, k_BoxW, k_BoxH);

                GUI.Box(boxRect, GUIContent.none, m_BoxStyle);

                var headRect = new Rect(boxRect.x + 4, boxRect.y + 4, boxRect.width - 8, 24);
                GUI.Label(headRect, tier, m_HeadlineStyle);

                m_Sb.Clear();
                m_Sb.AppendLine($"pos:{asset.posFormat}");
                m_Sb.AppendLine($"scl:{asset.scaleFormat}");
                m_Sb.AppendLine($"col:{asset.colorFormat}");
                m_Sb.Append($"sh:{asset.shFormat}");
                var detailRect = new Rect(boxRect.x + 4, headRect.yMax + 2, boxRect.width - 8, boxRect.height - headRect.height - 10);
                GUI.Label(detailRect, m_Sb.ToString(), m_DetailStyle);

                var boxAnchor = new Vector2(boxRect.x, boxRect.y + boxRect.height * 0.5f);
                var clampedSplat = new Vector2(
                    Mathf.Clamp(splatGuiPos.x, k_DotRadius, Screen.width  - k_DotRadius),
                    Mathf.Clamp(splatGuiPos.y, k_DotRadius, Screen.height - k_DotRadius));
                DrawLine(boxAnchor, clampedSplat, tierColor, 2f);

                EnsureCircleTextures();
                float outer = k_DotRadius + k_DotBorder;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(clampedSplat.x - outer, clampedSplat.y - outer, outer * 2, outer * 2), m_CircleBorderTex);
                GUI.color = tierColor;
                GUI.DrawTexture(new Rect(clampedSplat.x - k_DotRadius, clampedSplat.y - k_DotRadius, k_DotRadius * 2, k_DotRadius * 2), m_CircleTex);
                GUI.color = Color.white;

                slot++;
            }
        }

        static void DrawLine(Vector2 from, Vector2 to, Color color, float width)
        {
            if (Event.current.type != EventType.Repaint) return;
            var savedMatrix = GUI.matrix;
            var savedColor = GUI.color;
            GUI.color = color;
            float angle = Mathf.Atan2(to.y - from.y, to.x - from.x) * Mathf.Rad2Deg;
            float length = (to - from).magnitude;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, length, width), Texture2D.whiteTexture);
            GUI.matrix = savedMatrix;
            GUI.color = savedColor;
        }

        static Color GetTierColor(string tier) => tier switch
        {
            "VeryHigh" => new Color(0.05f, 0.55f, 0.05f),
            "High"     => new Color(0.45f, 0.75f, 0.10f),
            "Medium"   => new Color(0.95f, 0.85f, 0.05f),
            "Low"      => new Color(0.95f, 0.50f, 0.05f),
            _          => new Color(0.85f, 0.10f, 0.05f),
        };

        static string GetTierName(VectorFormat pos, VectorFormat scale, ColorFormat color, SHFormat sh)
        {
            if (pos == VectorFormat.Float32 && scale == VectorFormat.Float32 &&
                color == ColorFormat.Float32x4 && sh == SHFormat.Float32)
                return "VeryHigh";
            if (pos == VectorFormat.Norm16 && scale == VectorFormat.Norm16 &&
                color == ColorFormat.Float16x4 && sh == SHFormat.Norm11)
                return "High";
            if (pos == VectorFormat.Norm11 && scale == VectorFormat.Norm11 &&
                color == ColorFormat.Norm8x4 && sh == SHFormat.Norm6)
                return "Medium";
            if (pos == VectorFormat.Norm6 && scale == VectorFormat.Norm6 &&
                color == ColorFormat.Norm8x4 && sh == SHFormat.Cluster16k)
                return "Low";
            if (pos == VectorFormat.Norm6 && scale == VectorFormat.Norm6 &&
                color == ColorFormat.BC7 && sh == SHFormat.Cluster4k)
                return "VeryLow";
            return "Custom";
        }

        void EnsureStyles()
        {
            if (m_HeadlineStyle != null) return;
            m_BoxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTex(2, 2, new Color(0, 0, 0, 0.72f)) }
            };
            m_HeadlineStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = headlineFontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
                normal = { textColor = Color.white }
            };
            m_DetailStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = detailFontSize,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };
        }

        void EnsureCircleTextures()
        {
            if (m_CircleTex != null) return;
            m_CircleTex = MakeCircleTex(64);
            m_CircleBorderTex = MakeCircleTex(64);
        }

        static Texture2D MakeCircleTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave };
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - r + 0.5f, dy = y - r + 0.5f;
                float alpha = Mathf.Clamp01((r - Mathf.Sqrt(dx * dx + dy * dy)) / 1.5f);
                tex.SetPixel(x, y, new Color(1, 1, 1, alpha));
            }
            tex.Apply();
            return tex;
        }

        static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        void OnDestroy()
        {
            if (m_CircleTex != null) Destroy(m_CircleTex);
            if (m_CircleBorderTex != null) Destroy(m_CircleBorderTex);
        }
    }
}

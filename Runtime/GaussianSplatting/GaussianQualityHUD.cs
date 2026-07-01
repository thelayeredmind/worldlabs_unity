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

        GUIStyle m_HeadlineStyle;
        GUIStyle m_DetailStyle;
        GUIStyle m_BoxStyle;
        readonly StringBuilder m_Sb = new();

        void OnGUI()
        {
            EnsureStyles();
            var renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
            var cam = Camera.main;
            if (cam == null || renderers == null) return;

            foreach (var r in renderers)
            {
                if (!r.HasValidRenderSetup || r.asset == null) continue;

                var asset = r.asset;
                Vector3 worldCenter = (asset.boundsMin + asset.boundsMax) * 0.5f;
                worldCenter = r.transform.TransformPoint(worldCenter);

                Vector3 screenPos = cam.WorldToScreenPoint(worldCenter);
                if (screenPos.z <= 0) continue;

                // Flip Y: GUI origin is top-left, screen is bottom-left
                float x = screenPos.x;
                float y = Screen.height - screenPos.y;

                string tier = GetTierName(asset.posFormat, asset.scaleFormat, asset.colorFormat, asset.shFormat);
                m_Sb.Clear();
                m_Sb.AppendLine($"pos:{asset.posFormat}");
                m_Sb.AppendLine($"scl:{asset.scaleFormat}");
                m_Sb.AppendLine($"col:{asset.colorFormat}");
                m_Sb.Append($"sh:{asset.shFormat}");
                string detail = m_Sb.ToString();

                // Measure and draw background box
                float boxW = 160f;
                float boxH = 80f;
                Rect boxRect = new Rect(x - boxW * 0.5f, y - 8f, boxW, boxH);
                GUI.Box(boxRect, GUIContent.none, m_BoxStyle);

                Rect headRect = new Rect(boxRect.x + 4, boxRect.y + 4, boxRect.width - 8, 24);
                GUI.Label(headRect, tier, m_HeadlineStyle);

                Rect detailRect = new Rect(boxRect.x + 4, headRect.yMax + 2, boxRect.width - 8, boxRect.height - headRect.height - 10);
                GUI.Label(detailRect, detail, m_DetailStyle);
            }
        }

        static string GetTierName(VectorFormat pos, VectorFormat scale, ColorFormat color, SHFormat sh)
        {
            // Match against the known quality presets from GaussianSplatAssetCreator.ApplyQualityLevel
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
                normal = { background = MakeTex(2, 2, new Color(0, 0, 0, 0.65f)) }
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

        static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }
    }
}

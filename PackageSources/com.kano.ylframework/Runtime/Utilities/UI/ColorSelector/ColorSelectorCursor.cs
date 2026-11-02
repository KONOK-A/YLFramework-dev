using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.UI
{
    /// <summary>自绘黑白双线游标，不依赖未授权图片。</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ColorSelectorCursor : MaskableGraphic
    {
        /// <summary>按矩形短边绘制外黑内白的双环游标，并用 Graphic.color 调制顶点颜色。</summary>
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var rect = rectTransform.rect;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f;
            for (int band = 0; band < 2; band++)
            {
                for (int i = 0; i < 32; i++)
                {
                    float a = i * Mathf.PI / 16;
                    float b = (i + 1) * Mathf.PI / 16;
                    var p = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    var q = new Vector2(Mathf.Cos(b), Mathf.Sin(b));
                    float outer = radius * (band == 0 ? 1 : 0.8f);
                    float inner = radius * (band == 0 ? 0.8f : 0.6f);
                    var tint = (band == 0 ? Color.black : Color.white) * color;
                    int start = mesh.currentVertCount;
                    mesh.AddVert(rect.center + p * outer, tint, Vector2.zero);
                    mesh.AddVert(rect.center + p * inner, tint, Vector2.zero);
                    mesh.AddVert(rect.center + q * inner, tint, Vector2.zero);
                    mesh.AddVert(rect.center + q * outer, tint, Vector2.zero);
                    mesh.AddTriangle(start, start + 1, start + 2);
                    mesh.AddTriangle(start, start + 2, start + 3);
                }
            }
        }
    }
}

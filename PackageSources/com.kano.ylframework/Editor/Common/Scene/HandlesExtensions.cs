using UnityEditor;
using UnityEngine;


namespace YLFramework.Editor.Common
{

    /// <summary>补充 Scene 视图的二维轮廓绘制，不创建或修改场景对象。</summary>
    public static class HandlesExtensions
    {
        // Fix编码

        /// <summary>
        /// 在Scene视图绘制2D胶囊体
        /// </summary>
        /// <param name="center">当前 Handles.matrix 坐标系内的中心点。</param>
        /// <param name="size">该坐标系内的宽高；先取绝对值，再把主轴长度补到至少等于直径。</param>
        /// <param name="dir">胶囊主轴方向。</param>
        /// <remarks>沿用当前 Handles 的矩阵与颜色。竖向绘制时，两条直线的第二端点当前未叠加 center。</remarks>
        public static void DrawCapsule2D(Vector3 center, Vector2 size, CapsuleDirection2D dir)
        {
            size.Set(Mathf.Abs(size.x), Mathf.Abs(size.y));

            if (dir == CapsuleDirection2D.Vertical)
            {
                if (size.y < size.x)
                {
                    size.Set(size.x, size.x);
                }

                Handles.DrawWireArc(new Vector3(0, (size.y - size.x) / 2) + center, -Vector3.forward, Vector3.left, 180, size.x / 2);

                Handles.DrawLine(new Vector3(-size.x / 2, -(size.y - size.x) / 2) + center, new Vector3(-size.x / 2, (size.y - size.x) / 2));
                Handles.DrawLine(new Vector3(size.x / 2, -(size.y - size.x) / 2) + center, new Vector3(size.x / 2, (size.y - size.x) / 2));

                Handles.DrawWireArc(new Vector3(0, -(size.y - size.x) / 2) + center, Vector3.forward, Vector3.left, 180, size.x / 2);
            }
            else
            {
                if (size.x < size.y)
                {
                    size.Set(size.y, size.y);
                }

                Handles.DrawWireArc(new Vector3(-(size.x - size.y) / 2, 0) + center, -Vector3.forward, Vector3.down, 180, size.y / 2);

                Handles.DrawLine(new Vector3(-(size.x - size.y) / 2, -size.y / 2) + center, center + new Vector3((size.x - size.y) / 2, -size.y / 2));
                Handles.DrawLine(new Vector3(-(size.x - size.y) / 2, size.y / 2) + center, center + new Vector3((size.x - size.y) / 2, size.y / 2));

                Handles.DrawWireArc(new Vector3((size.x - size.y) / 2, 0) + center, Vector3.forward, Vector3.down, 180, size.y / 2);
            }
        }

    }
}

using UnityEditor;
using UnityEngine;
using YLFramework.Platform2D;

namespace YLFramework.Editor.Platform2D
{
    /// <summary>绘制移动平台的路径与捕获线段，仅供 Scene 视图预览，不修改路径或执行捕获。</summary>
    internal static class MoveableGizmos
    {
        /// <summary>按所选路径模式连接相邻世界坐标点或有效 Transform，绘制后恢复 Handles 颜色。</summary>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawPath(Moveable moveable, GizmoType type)
        {
            var oldColor = Handles.color;
            try
            {
                Handles.color = Color.yellow;
                if (moveable.PositionType == PositionType.Positions && moveable.Positions != null)
                {
                    for (int i = 0; i + 1 < moveable.Positions.Count; i++)
                    {
                        Handles.DrawDottedLine(moveable.Positions[i], moveable.Positions[i + 1], 4);
                    }
                }

                if (moveable.PositionType == PositionType.Transforms && moveable.Transforms != null)
                {
                    for (int i = 0; i + 1 < moveable.Transforms.Count; i++)
                    {
                        if (moveable.Transforms[i] && moveable.Transforms[i + 1])
                        {
                            Handles.DrawDottedLine(moveable.Transforms[i].position, moveable.Transforms[i + 1].position, 4);
                        }
                    }
                }
            }
            finally
            {
                Handles.color = oldColor;
            }
        }

        /// <summary>选中捕获器时，把各捕获区域的局部端点转换到世界坐标后绘制线段。</summary>
        [DrawGizmo(GizmoType.Selected)]
        private static void DrawCatcher(PlatformCatcher2D catcher, GizmoType type)
        {
            if (catcher.areas == null)
            {
                return;
            }

            var oldColor = Handles.color;
            try
            {
                Handles.color = Color.yellow;
                foreach (var area in catcher.areas)
                {
                    Handles.DrawLine(catcher.transform.TransformPoint(area.from), catcher.transform.TransformPoint(area.to));
                }
            }
            finally
            {
                Handles.color = oldColor;
            }
        }
    }
}

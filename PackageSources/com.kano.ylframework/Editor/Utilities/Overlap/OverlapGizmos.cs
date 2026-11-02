using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Common;
using YLFramework.Overlap;

namespace YLFramework.Editor.Overlap
{
    /// <summary>根据检测组件配置绘制 Scene 范围示意，不发起检测或持有结果对象。</summary>
    internal static class OverlapGizmos
    {
        /// <summary>绘制二维矩形、胶囊、圆或区域端点连线，结束后恢复 Handles 矩阵与颜色。</summary>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void Draw2D(OverlapBase2D overlap, GizmoType gizmoType)
        {
            var oldMatrix = Handles.matrix;
            var oldColor = Handles.color;
            try
            {
                Handles.color = Color.green;
                var transform = overlap.transform;
                if (overlap is OverlapBox2D box)
                {
                    var center = transform.localToWorldMatrix.MultiplyPoint(box.Offset);
                    // 中心和旋转交给 Handles.matrix，尺寸单独乘缩放，避免在矩阵与尺寸中重复缩放。
                    Handles.matrix = Matrix4x4.TRS(center, Quaternion.Euler(0, 0, transform.eulerAngles.z), Vector3.one);
                    var size = box.Size * (Vector2)transform.lossyScale;
                    if (overlap is OverlapCapsule2D capsule)
                    {
                        HandlesExtensions.DrawCapsule2D(Vector3.zero, size, capsule.Direction);
                    }
                    else
                    {
                        Handles.DrawWireCube(Vector3.zero, size);
                    }
                }
                else if (overlap is OverlapCircle2D circle)
                {
                    var scale = transform.lossyScale;
                    Handles.DrawWireDisc(transform.position, Vector3.forward, circle.Radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)));
                }
                else if (overlap is OverlapArea2D area)
                {
                    if (area.Mode == OverlapAreaMode.Position && area.Positions != null)
                    {
                        for (int i = 0; i + 1 < area.Positions.Count; i++)
                        {
                            Handles.DrawLine(transform.TransformPoint(area.Positions[i]), transform.TransformPoint(area.Positions[i + 1]));
                        }
                    }

                    if (area.Mode == OverlapAreaMode.Transform && area.Transforms != null)
                    {
                        for (int i = 0; i + 1 < area.Transforms.Count; i++)
                        {
                            if (area.Transforms[i] && area.Transforms[i + 1])
                            {
                                Handles.DrawLine(area.Transforms[i].position, area.Transforms[i + 1].position);
                            }
                        }
                    }
                }
            }
            finally
            {
                Handles.matrix = oldMatrix;
                Handles.color = oldColor;
            }
        }

        /// <summary>以缩放后的尺寸绘制三维盒体或球体线框，结束后恢复 Handles 状态。</summary>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void Draw3D(OverlapBase3D overlap, GizmoType gizmoType)
        {
            var oldMatrix = Handles.matrix;
            var oldColor = Handles.color;
            try
            {
                Handles.color = Color.green;
                var transform = overlap.transform;
                var scale = transform.lossyScale;
                if (overlap is OverlapBox3D box)
                {
                    Handles.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
                    Handles.DrawWireCube(Vector3.zero, Vector3.Scale(box.Size, scale));
                }
                else if (overlap is OverlapSphere3D sphere)
                {
                    float radius = sphere.Radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    Handles.DrawWireDisc(transform.position, Vector3.up, radius);
                    Handles.DrawWireDisc(transform.position, Vector3.forward, radius);
                    Handles.DrawWireDisc(transform.position, Vector3.right, radius);
                    Handles.DrawWireDisc(transform.position, Vector3.forward + Vector3.right, radius);
                    Handles.DrawWireDisc(transform.position, Vector3.forward - Vector3.right, radius);
                }
            }
            finally
            {
                Handles.matrix = oldMatrix;
                Handles.color = oldColor;
            }
        }
    }
}

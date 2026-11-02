using UnityEditor;
using UnityEngine;
using YLFramework.Character2D;

namespace YLFramework.Editor.Character2D
{
    /// <summary>在 Scene 中可视化角色运行时已经求得的接触信息，不参与物理检测。</summary>
    internal static class CharacterControllerGizmos
    {
        /// <summary>仅在运行且启用调试显示时绘制已有接触的角度与法线，结束后恢复 Handles 颜色。</summary>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void Draw(CharacterController2D motor, GizmoType gizmoType)
        {
            if (!motor.ShowDebugLine || !Application.isPlaying)
            {
                return;
            }

            var oldColor = Handles.color;
            try
            {
                if (motor.IsGrounded)
                {
                    DrawHit(motor.GroundInfo, Vector2.up, Color.yellow, "地面角度");
                }

                if (motor.IsTouchingWall)
                {
                    DrawHit(motor.WallInfo, motor.WallInfo.normal.x > 0 ? Vector2.right : Vector2.left, Color.red, "墙面角度");
                }

                if (motor.IsTouchingCeiling)
                {
                    DrawHit(motor.TopInfo, Vector2.down, Color.green, "头顶角度");
                }
            }
            finally
            {
                Handles.color = oldColor;
            }
        }

        /// <summary>在命中点标注法线相对参考方向的夹角，并绘制短法线。</summary>
        private static void DrawHit(RaycastHit2D hit, Vector2 direction, Color color, string label)
        {
            Handles.color = color;
            Handles.Label(hit.point, $"{label}:{Vector2.Angle(hit.normal, direction)}");
            Handles.DrawLine(hit.point, hit.point + hit.normal * 0.2f);
        }
    }
}

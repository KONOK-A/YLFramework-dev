using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>提供图绘制所需的元素局部变换矩阵，隔离不同 Unity 版本的变换读取方式。</summary>
    public static class VisualElementExtension
    {

        /// <summary>取得元素局部变换；新版本由样式及反射读取的旋转轴重建，旧版本使用元素变换矩阵。</summary>
        /// <param name="visualElement">待查询的非空元素。</param>
        /// <returns>用于将节点局部坐标转换到图绘制坐标的矩阵。</returns>
        public static Matrix4x4 Matrix(this VisualElement visualElement)
        {
#if UNITY_6000_2_OR_NEWER

            Vector3 value = visualElement.resolvedStyle.scale.value;
            if (visualElement.panel != null )
            {
                var property = visualElement.panel.GetType().GetProperty("isFlat");
                if ((bool)property.GetValue(visualElement.panel))
                {
                    value.z = 1f;
                }
            }

            var m_Axis = visualElement.resolvedStyle.rotate.GetType().GetField("m_Axis", BindingFlags.Instance | BindingFlags.NonPublic);
            var axis = (Vector3)m_Axis.GetValue(visualElement.resolvedStyle.rotate);
            var quaternion = Quaternion.AngleAxis(visualElement.resolvedStyle.rotate.angle.ToDegrees(), axis);
            return Matrix4x4.TRS(visualElement.resolvedStyle.translate, quaternion, value);

#else
            return visualElement.transform.matrix;
#endif
        }


    }

}

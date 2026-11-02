using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Overlap;

namespace YLFramework.Editor.Overlap
{
    /// <summary>在矩形编辑基础上提供胶囊主轴尺寸约束。</summary>
    [CustomEditor(typeof(OverlapCapsule2D))]
    public class OverlapCapsule2DInspector : OverlapBox2DInspector
    {
        private OverlapCapsule2D _overlapCapsule;

        /// <summary>初始化矩形编辑属性后缓存胶囊检测器，以便读取其主轴方向。</summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            _overlapCapsule = target as OverlapCapsule2D;
        }


        /// <summary>完成基类绘制与尺寸校正后，按胶囊方向补长主轴并应用修改。</summary>
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            switch (_overlapCapsule.Direction)
            {
                case CapsuleDirection2D.Vertical:
                    if (Size.vector2Value.y < Size.vector2Value.x)
                    {
                        Size.vector2Value = new Vector2(Size.vector2Value.x, Size.vector2Value.x);
                        Size.serializedObject.ApplyModifiedProperties();
                    }

                    break;
                case CapsuleDirection2D.Horizontal:
                    if (Size.vector2Value.x < Size.vector2Value.y)
                    {
                        Size.vector2Value = new Vector2(Size.vector2Value.y, Size.vector2Value.y);
                        Size.serializedObject.ApplyModifiedProperties();
                    }
                    break;
            }


        }


        //protected override bool LimitSize(Vector2 size)
        //{
        //    switch (overlapCapsule.Direction)
        //    {
        //        case CapsuleDirection2D.Vertical:
        //            if (size.y < size.x)
        //                return false;

        //            break;
        //        case CapsuleDirection2D.Horizontal:
        //            if (size.x < size.y)
        //                return false;
        //            break;
        //    }


        //    return base.LimitSize(size);
        //}

    }
}

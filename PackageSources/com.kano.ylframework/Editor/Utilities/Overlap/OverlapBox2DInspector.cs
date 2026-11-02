using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Overlap;

namespace YLFramework.Editor.Overlap
{
    /// <summary>在通用二维检测 Inspector 上补充矩形尺寸校正及四侧操作柄，通过序列化属性提交编辑。</summary>
    /// <remarks>四侧调节直接采用世界轴位移，只修正负缩放的符号，不作完整的旋转和缩放换算。</remarks>
    [CustomEditor(typeof(OverlapBox2D))]
    public class OverlapBox2DInspector : OverlapBase2DInspector
    {

        private OverlapBox2D _box2D;

        /// <summary>矩形中心相对检测器的局部偏移属性，供派生 Inspector 共用。</summary>
        protected SerializedProperty Offset;

        /// <summary>矩形在局部坐标中的宽高属性，供派生 Inspector 校正与编辑。</summary>
        protected SerializedProperty Size;


        /// <summary>初始化通用属性后，缓存矩形检测器及其偏移、尺寸属性。</summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            _box2D = target as OverlapBox2D;

            Offset = serializedObject.FindProperty("offset");
            Size = serializedObject.FindProperty("size");
        }


        /// <summary>绘制通用配置和结果，再将尺寸的负分量归零并应用。</summary>
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if (Size.vector2Value.x <= 0)
            {
                Size.vector2Value = new Vector2(0, Size.vector2Value.y);
                Size.serializedObject.ApplyModifiedProperties();
            }

            if (Size.vector2Value.y <= 0)
            {
                Size.vector2Value = new Vector2(Size.vector2Value.x, 0);
                Size.serializedObject.ApplyModifiedProperties();
            }


        }

        /// <summary>显示矩形局部四侧对应的世界操作柄，并将世界 X/Y 位移交给各侧调整入口。</summary>
        protected virtual void OnSceneGUI()
        {
            Vector3 top = _box2D.transform.localToWorldMatrix.MultiplyPoint(_box2D.Offset + new Vector2(0, _box2D.Size.y / 2));

            float size = HandleUtility.GetHandleSize(top) * 0.1f;
            Handles.color = Color.green;
            Vector3 currentTop = Handles.FreeMoveHandle(top, size, Vector3.one * size, Handles.CubeHandleCap);

            SetTop(currentTop.y - top.y);

            Vector3 down = _box2D.transform.localToWorldMatrix.MultiplyPoint(_box2D.Offset - new Vector2(0, _box2D.Size.y / 2));

            size = HandleUtility.GetHandleSize(down) * 0.1f;
            Handles.color = Color.green;
            Vector3 currentDown = Handles.FreeMoveHandle(down, size, Vector3.one * size, Handles.CubeHandleCap);

            SetDown(currentDown.y - down.y);

            Vector3 left = _box2D.transform.localToWorldMatrix.MultiplyPoint(_box2D.Offset - new Vector2(_box2D.Size.x / 2, 0));

            size = HandleUtility.GetHandleSize(left) * 0.1f;
            Handles.color = Color.green;
            Vector3 currentLeft = Handles.FreeMoveHandle(left, size, Vector3.one * size, Handles.CubeHandleCap);

            SetLeft(currentLeft.x - left.x);


            Vector3 right = _box2D.transform.localToWorldMatrix.MultiplyPoint(_box2D.Offset + new Vector2(_box2D.Size.x / 2, 0));

            size = HandleUtility.GetHandleSize(right) * 0.1f;
            Handles.color = Color.green;
            Vector3 currentRight = Handles.FreeMoveHandle(right, size, Vector3.one * size, Handles.CubeHandleCap);

            SetRight(currentRight.x - right.x);
        }


        /// <summary>按上侧位移调整中心 Y 与高度，候选尺寸通过校验后成对应用。</summary>
        /// <param name="detalY">上侧操作柄的世界 Y 位移。</param>
        public void SetTop(float detalY)
        {

            if (_box2D.transform.lossyScale.y < 0)
            {
                detalY = -detalY;
            }

            Vector2 offsetV = new Vector2(_box2D.Offset.x, _box2D.Offset.y + detalY / 2);
            Vector2 sizeV = new Vector2(_box2D.Size.x, _box2D.Size.y + detalY);

            if (!LimitSize(sizeV))
            {
                return;
            }

            Offset.vector2Value = offsetV;
            Size.vector2Value = sizeV;

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>按下侧位移调整中心 Y 与高度，候选尺寸通过校验后成对应用。</summary>
        /// <param name="detalY">下侧操作柄的世界 Y 位移。</param>
        public void SetDown(float detalY)
        {
            if (_box2D.transform.lossyScale.y < 0)
            {
                detalY = -detalY;
            }

            Vector2 offsetV = new Vector2(_box2D.Offset.x, _box2D.Offset.y + detalY / 2);
            Vector2 sizeV = new Vector2(_box2D.Size.x, _box2D.Size.y - detalY);

            if (!LimitSize(sizeV))
            {
                return;
            }

            Offset.vector2Value = offsetV;
            Size.vector2Value = sizeV;

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>按左侧位移调整中心 X 与宽度，候选尺寸通过校验后成对应用。</summary>
        /// <param name="detalX">左侧操作柄的世界 X 位移。</param>
        public void SetLeft(float detalX)
        {
            if (_box2D.transform.lossyScale.x < 0)
            {
                detalX = -detalX;
            }

            Vector2 offsetV = new Vector2(_box2D.Offset.x + detalX / 2, _box2D.Offset.y);
            Vector2 sizeV = new Vector2(_box2D.Size.x - detalX, _box2D.Size.y);

            if (!LimitSize(sizeV))
            {
                return;
            }

            Offset.vector2Value = offsetV;
            Size.vector2Value = sizeV;

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>按右侧位移调整中心 X 与宽度，候选尺寸通过校验后成对应用。</summary>
        /// <param name="detalX">右侧操作柄的世界 X 位移。</param>
        public void SetRight(float detalX)
        {
            if (_box2D.transform.lossyScale.x < 0)
            {
                detalX = -detalX;
            }

            Vector2 offsetV = new Vector2(_box2D.Offset.x + detalX / 2, _box2D.Offset.y);
            Vector2 sizeV = new Vector2(_box2D.Size.x + detalX, _box2D.Size.y);

            if (!LimitSize(sizeV))
            {
                return;
            }

            Offset.vector2Value = offsetV;
            Size.vector2Value = sizeV;

            serializedObject.ApplyModifiedProperties();
        }


        /// <summary>校验调边后的候选尺寸，默认只允许宽高均严格为正。</summary>
        /// <param name="size">尚未提交的局部宽高。</param>
        /// <returns>是否允许应用该尺寸及配套偏移。</returns>
        protected virtual bool LimitSize(Vector2 size)
        {

            if (size.x <= 0)
            {
                return false;
            }

            if (size.y <= 0)
            {
                return false;
            }

            return true;
        }


    }

}

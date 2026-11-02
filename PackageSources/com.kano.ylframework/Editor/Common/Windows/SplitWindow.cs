using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace YLFramework.Editor.Common
{
    /// <summary>提供可拖动分隔线的左右分栏窗口框架，派生窗口分别绘制两侧内容。</summary>
    [MovedFrom(true, sourceNamespace: "YLFramework.Editor.Utilities")]
    public class SplitWindow : EditorWindow
    {
        const int PanePadding = 1; // 内边距

        /// <summary>随窗口序列化的左栏宽度比例，拖动时限制在 0.1 至 0.8 之间。</summary>
        [UnityEngine.Serialization.FormerlySerializedAs("horizontal_split_percent")]
        [SerializeField]
        protected float horizontalSplitPercent = 0.3f;

        private Rect _horizontalSplitRect;       // 水平分割的矩形

        private bool _isResizingHorizontal;      // 是不是正在调整 水平的比例

        /// <summary>是否在左右内容区域外绘制边框。</summary>
        protected bool ShowBorder = false;

        private bool _isLayout;

        /// <summary>读写左栏宽度比例；直接赋值不经过拖动时的范围限制，也不记录 Undo。</summary>
        public float HorizontalSplitPercent
        {
            get
            {
                return horizontalSplitPercent;
            }
            set
            {
                horizontalSplitPercent = value;
            }
        }


        /// <summary>处理分隔线的鼠标按下、拖动和释放，更新比例并在拖动期间请求重绘。</summary>
        private void HorizontalResize()
        {
            _horizontalSplitRect.Set(position.width * horizontalSplitPercent - PanePadding - 1, 0, PanePadding * 2 + 1, position.height);

            if (Event.current != null)
            {

                EditorGUIUtility.AddCursorRect(_horizontalSplitRect, MouseCursor.ResizeHorizontal);

                if (Event.current.type == EventType.MouseDown && _horizontalSplitRect.Contains(Event.current.mousePosition))
                {
                    _isResizingHorizontal = true;
                }

                if (_isResizingHorizontal)
                {
                    horizontalSplitPercent = Mathf.Clamp(Event.current.mousePosition.x / position.width, 0.1f, 0.8f);
                }

                if (Event.current.type == EventType.MouseUp)
                {
                    _isResizingHorizontal = false;
                }

                if (_isResizingHorizontal)
                {
                    Repaint();
                }
            }
        }

        /// <summary>处理分栏布局及拖动，调用两侧绘制钩子并按需绘制边框。</summary>
        protected virtual void OnGUI()
        {
            // 仅接受紧随 Layout 的 Repaint，避免在本窗口尚无对应布局时进入内容绘制。
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                if (!_isLayout)
                {
                    return;
                }
            }

            _isLayout = Event.current != null && Event.current.type == EventType.Layout;

            Rect left = new Rect(0, 0, position.width * horizontalSplitPercent - PanePadding, position.height);
            Rect right = new Rect(left.width + _horizontalSplitRect.width, 0, position.width * (1 - horizontalSplitPercent) - PanePadding - 1, position.height);

            HorizontalResize();
            DrawGuiLeft(left);
            DrawGuiRight(right);

            if (ShowBorder)
            {
                GUI.Box(left, string.Empty, "GroupBox");
                GUI.Box(right, string.Empty, "GroupBox");
            }


            Vector2 half = Vector2.up * _horizontalSplitRect.height / 2;

            Color origin = Handles.color;
            Handles.color = _isResizingHorizontal ? Color.green : Color.white;
            Handles.DrawLine(_horizontalSplitRect.center - half, _horizontalSplitRect.center + half);

            Handles.color = origin;
        }

        /// <summary>绘制左侧内容，默认留空供派生窗口实现。</summary>
        /// <param name="rect">窗口坐标系内分配给左栏的区域。</param>
        protected virtual void DrawGuiLeft(Rect rect)
        {
        }

        /// <summary>绘制右侧内容，默认留空供派生窗口实现。</summary>
        /// <param name="rect">窗口坐标系内分配给右栏的区域。</param>
        protected virtual void DrawGuiRight(Rect rect)
        {
        }

    }

}

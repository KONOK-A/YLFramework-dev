using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>图窗口 IMGUI 子区域的基础层，保存绘制区域并提供事件、更新及失焦扩展入口。</summary>
    internal class GraphLayer
    {

        #region 字段

        /// <summary>最近一次绘制时传入的区域，用于本层事件命中判断。</summary>
        protected Rect Position;

        #endregion

        #region 属性

        /// <summary>拥有此绘制层的编辑器窗口。</summary>
        public EditorWindow EditorWindow
        {
            get; private set;
        }


        #endregion


        #region 方法

        /// <summary>绑定宿主窗口，不创建独立窗口或配置数据。</summary>
        /// <param name="editorWindow">拥有此层的窗口。</param>
        public GraphLayer(EditorWindow editorWindow)
        {
            this.EditorWindow = editorWindow;
        }

        /// <summary>保存本轮绘制区域并调用变换更新入口。</summary>
        /// <param name="rect">当前子区域的绘制范围。</param>
        public virtual void OnGUI(Rect rect)
        {
            Position = rect;
            UpdateTransformationMatrix();
        }

        /// <summary>处理子区域输入的扩展入口，基类不消费事件。</summary>
        public virtual void ProcessEvents()
        {

        }

        /// <summary>供宿主转发编辑器更新的扩展入口，基类无操作。</summary>
        public virtual void Update()
        {
        }

        /// <summary>保留图层变换更新位置，当前不执行矩阵计算。</summary>
        private void UpdateTransformationMatrix()
        {
            //this.transormMatrix = Matrix4x4.TRS(position.center + this.Context.DragOffset  , Quaternion.identity,Vector3.one * this.Context.ZoomFactor);
        }

        /// <summary>失焦时清除图选择；鼠标转入 Inspector 时保留选择以便继续编辑。</summary>
        public virtual void OnLostFocus()
        {
            if (UnityEditor.EditorWindow.mouseOverWindow != null && UnityEditor.EditorWindow.mouseOverWindow.GetType().ToString().Equals("UnityEditor.InspectorWindow"))
            {
                return;
            }

            Context.Instance.ClearSelections();
        }

        #endregion
    }

}

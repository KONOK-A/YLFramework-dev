using UnityEditor;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>将普通节点数据包装为 Unity 可选择对象，为节点 Inspector 提供当前配置与图引用。</summary>
    public class FsmStateInspectorHelper : ScriptableObjectSingleton<FsmStateInspectorHelper>
    {
        //private static FSMStateInspectorHelper _instance;
        //public static FSMStateInspectorHelper Instance {
        //    get {
        //        if (_instance == null) {
        //            _instance = ScriptableObject.CreateInstance<FSMStateInspectorHelper>();
        //        }
        //        return _instance;
        //    }
        //}

        /// <summary>当前检查的节点；选择无效或清除时为 null。</summary>
        public FsmStateNodeData node;
        /// <summary>拥有当前节点的配置。</summary>
        public RuntimeFsmController controller;

        /// <summary>用于同步节点改名的图视图，清除节点后为空。</summary>
        public FsmStateGraphView grap;

        /// <summary>校验节点归属并更新检查目标；有效时选中此代理，清除时仅取消对此代理的活动选择。</summary>
        /// <param name="controller">预期拥有节点的配置。</param>
        /// <param name="node">要检查的节点，null 表示清除。</param>
        /// <param name="grap">显示该节点的图视图。</param>
        public void Inspect(RuntimeFsmController controller, FsmStateNodeData node, FsmStateGraphView grap)
        {

            if (node != null && (!controller || !controller.states.Contains(node)))
            {
                node = null;
            }

            this.node = node;
            this.controller = controller;
            this.grap = node != null ? grap : null;
            if (node == null)
            {
                if (Selection.activeObject == this)
                {
                    Selection.activeObject = null;
                }

                return;
            }

            Selection.activeObject = this;

        }

    }

}

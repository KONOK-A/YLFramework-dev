using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>记录配置 Undo 并维护过渡；创建和删除立即保存，自动切换开关只标脏。</summary>
    internal class FsmTransitionFactory
    {
        /// <summary>修改无条件自动切换标记，值不变时不记录 Undo。</summary>
        /// <param name="controller">拥有过渡的配置。</param>
        /// <param name="transition">目标过渡定义。</param>
        /// <param name="value">新的自动切换标记。</param>
        public static void SetAutoSwitch(RuntimeFsmController controller, FsmTransitionData transition, bool value)
        {
            if (transition.autoSwitch == value)
            {
                return;
            }

            UnityEditor.Undo.RecordObject(controller, "Change FSM AutoSwitch");
            transition.autoSwitch = value;
            controller.Save();
        }

        /// <summary>创建端点存在的非自环过渡，拒绝内置目标及重复边；父层兼容性仍由配置验证检查。</summary>
        /// <param name="controller">接收过渡的配置。</param>
        /// <param name="fromStateName">来源节点名称。</param>
        /// <param name="toStateName">普通目标节点名称。</param>
        public static void CreateTransition(RuntimeFsmController controller, string fromStateName, string toStateName)
        {

            FsmStateNodeData toNode = controller.GetStateNodeData(toStateName);
            if (toNode == null || controller.GetStateNodeData(fromStateName) == null)
            {
                return;
            }

            if (toNode.IsAnyState || toNode.IsEntryState || toNode.IsUpState)
            {
                string message = string.Format("状态:{0}不能添加过渡!", toNode.DisplayName);
                Debug.LogWarning(message);
                FsmEditorWindow.ShowNotification(message);
                return;
            }

            if (fromStateName.Equals(toStateName))
            {
                return;
            }

            foreach (var item in controller.transitions)
            {
                if (item.fromStateName.Equals(fromStateName) && item.toStateName.Equals(toStateName))
                {
                    string message = string.Format("过渡 {0} -> {1} 已经存在,请勿重复添加!", fromStateName, toStateName);

                    Debug.LogError(message);
                    FsmEditorWindow.ShowNotification(message);
                    return;
                }
            }

            FsmTransitionData transition = new FsmTransitionData();
            UnityEditor.Undo.RecordObject(controller, "Create FSM Transition");
            transition.fromStateName = fromStateName;
            transition.toStateName = toStateName;
            controller.AddTransition(transition);
            UnityEditor.EditorUtility.SetDirty(controller);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(controller);

        }

        /// <summary>记录 Undo 后移除过渡，清理查询缓存并保存配置资产。</summary>
        /// <param name="controller">拥有过渡的配置。</param>
        /// <param name="transition">要移除的过渡定义。</param>
        public static void DeleteTransition(RuntimeFsmController controller, FsmTransitionData transition)
        {
            UnityEditor.Undo.RecordObject(controller, "Delete FSM Transition");
            controller.RemoveTransition(transition);
            UnityEditor.EditorUtility.SetDirty(controller);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(controller);
        }

    }

}

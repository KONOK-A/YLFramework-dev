using UnityEditor;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>将普通过渡数据包装为 Unity 可选择对象，使 Inspector 能检查所属配置中的过渡。</summary>
    public class FsmTransitionInspectorHelper : ScriptableObjectSingleton<FsmTransitionInspectorHelper>
    {

        /// <summary>当前检查的过渡，清除或归属无效时为空。</summary>
        public FsmTransitionData transition;
        /// <summary>拥有当前过渡的配置，作为序列化编辑和 Undo 的实际目标。</summary>
        public RuntimeFsmController controller;

        /// <summary>校验过渡归属并更新检查目标；清除时只取消对此代理的活动选择。</summary>
        /// <param name="controller">预期拥有过渡的配置。</param>
        /// <param name="transition">要检查的过渡，null 表示清除。</param>
        public void Inspect(RuntimeFsmController controller, FsmTransitionData transition)
        {
            if (transition != null && (!controller || !controller.transitions.Contains(transition)))
            {
                transition = null;
            }

            this.transition = transition;
            this.controller = controller;
            if (transition == null)
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

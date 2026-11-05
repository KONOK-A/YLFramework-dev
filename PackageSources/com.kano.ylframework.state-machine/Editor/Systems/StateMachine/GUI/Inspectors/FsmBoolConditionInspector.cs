using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{

    /// <summary>以真假菜单编辑布尔条件目标值，保留条件既有比较方式。</summary>
    public class FsmBoolConditionInspector : FsmConditionInspector
    {

        /// <summary>绘制真假下拉菜单，选项经条件工厂记录 Undo 并写入零或一。</summary>
        /// <param name="rect">菜单按钮区域。</param>
        /// <param name="condition">待编辑布尔条件。</param>
        /// <param name="controller">拥有条件的配置。</param>
        public override void OnGUI(Rect rect, FsmConditionData condition, RuntimeFsmController controller)
        {
            string text = condition.targetValue == 1 ? "True" : "False";
            if (EditorGUI.DropdownButton(rect, new GUIContent(text), FocusType.Keyboard))
            {

                GenericMenu menu = new GenericMenu();

                menu.AddItem(new GUIContent("True"), condition.targetValue == 1, () =>
                {
                    FsmConditionFactory.SetTargetValue(controller, condition, 1);
                });

                //tempContent.text = "False";
                menu.AddItem(new GUIContent("False"), condition.targetValue == 0, () =>
                {
                    FsmConditionFactory.SetTargetValue(controller, condition, 0);
                });

                menu.ShowAsContext();
            }

        }
    }

}

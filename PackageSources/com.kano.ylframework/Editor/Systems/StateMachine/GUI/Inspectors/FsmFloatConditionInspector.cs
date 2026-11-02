using System;
using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>为浮点条件提供大小比较及浮点目标输入，界面不提供相等或不等选项。</summary>
    public class FsmFloatConditionInspector : FsmConditionInspector
    {
        private Rect _leftRect;
        private Rect _rightRect;

        //private GUIContent tempContent = new GUIContent();

        /// <summary>绘制浮点比较菜单和目标字段，通过条件工厂提交变化，不限制运行时已有比较类型。</summary>
        /// <param name="rect">比较和目标输入的组合区域。</param>
        /// <param name="condition">待编辑浮点条件。</param>
        /// <param name="controller">拥有条件的配置。</param>
        public override void OnGUI(Rect rect, FsmConditionData condition, RuntimeFsmController controller)
        {

            _leftRect.Set(rect.x, rect.y, rect.width / 2, rect.height);
            _rightRect.Set(rect.x + rect.width / 2, rect.y, rect.width / 2, rect.height);

            //tempContent.text = condition.compareType.ToString();
            if (EditorGUI.DropdownButton(_leftRect, new GUIContent(condition.compareType.ToString()), FocusType.Keyboard))
            {

                GenericMenu menu = new GenericMenu();

                for (int i = 0; i < Enum.GetValues(typeof(CompareType)).Length; i++)
                {
                    CompareType v = (CompareType)Enum.GetValues(typeof(CompareType)).GetValue(i);

                    if (v == CompareType.Equal || v == CompareType.NotEqual)
                    {
                        continue;
                    }

                    menu.AddItem(new GUIContent(v.ToString()), condition.compareType == v, () =>
                    {
                        FsmConditionFactory.SetComparison(controller, condition, v);
                    });

                }

                menu.ShowAsContext();

            }

            FsmConditionFactory.SetTargetValue(controller, condition, EditorGUI.FloatField(_rightRect, condition.targetValue));

        }
    }

}

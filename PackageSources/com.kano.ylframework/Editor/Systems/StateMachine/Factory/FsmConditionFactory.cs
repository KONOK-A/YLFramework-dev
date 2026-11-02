using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{

    /// <summary>条件与条件组的作者操作入口，将 Undo 记录归到所属配置而非普通数据对象。</summary>
    internal class FsmConditionFactory
    {
        /// <summary>记录配置 Undo 并修改条件目标值，变化后标脏。</summary>
        /// <param name="controller">拥有条件的配置。</param>
        /// <param name="condition">待修改条件。</param>
        /// <param name="value">新的目标值。</param>
        public static void SetTargetValue(RuntimeFsmController controller, FsmConditionData condition, float value)
        {
            if (condition.targetValue == value)
            {
                return;
            }

            UnityEditor.Undo.RecordObject(controller, "Change FSM Condition Value");
            condition.targetValue = value;
            controller.Save();
        }

        /// <summary>记录配置 Undo 并修改比较方式，变化后标脏。</summary>
        /// <param name="controller">拥有条件的配置。</param>
        /// <param name="condition">待修改条件。</param>
        /// <param name="value">新的比较关系。</param>
        public static void SetComparison(RuntimeFsmController controller, FsmConditionData condition, CompareType value)
        {
            if (condition.compareType == value)
            {
                return;
            }

            UnityEditor.Undo.RecordObject(controller, "Change FSM Condition Comparison");
            condition.compareType = value;
            controller.Save();
        }

        /// <summary>向过渡追加一个空的 OR 条件组，记录 Undo 并标脏。</summary>
        /// <param name="controller">拥有过渡的配置。</param>
        /// <param name="transition">接收条件组的过渡。</param>
        /// <returns>新追加的条件组。</returns>
        public static GroupCondition AddGroup(RuntimeFsmController controller, FsmTransitionData transition)
        {
            UnityEditor.Undo.RecordObject(controller, "Add FSM Condition Group");
            var group = new GroupCondition();
            transition.groupConditions.Add(group);
            controller.Save();
            return group;
        }

        /// <summary>从过渡移除指定附加条件组，记录 Undo 并标脏。</summary>
        /// <param name="controller">拥有过渡的配置。</param>
        /// <param name="transition">待修改的过渡。</param>
        /// <param name="group">要移除的条件组。</param>
        public static void RemoveGroup(RuntimeFsmController controller, FsmTransitionData transition, GroupCondition group)
        {
            UnityEditor.Undo.RecordObject(controller, "Remove FSM Condition Group");
            transition.groupConditions.Remove(group);
            controller.Save();
        }

        /// <summary>向附加组追加默认条件，记录配置 Undo 并标脏。</summary>
        /// <param name="controller">提供参数默认选择并拥有该组的配置。</param>
        /// <param name="group">接收条件的附加组。</param>
        public static void AddGroupCondition(RuntimeFsmController controller, GroupCondition group)
        {
            UnityEditor.Undo.RecordObject(controller, "Add FSM Group Condition");
            group.conditions.Add(CreateCondition(controller));
            controller.Save();
        }

        /// <summary>按组内下标移除条件，记录 Undo 并标脏；下标无效时不处理。</summary>
        /// <param name="controller">拥有条件组的配置。</param>
        /// <param name="group">待修改的附加组。</param>
        /// <param name="index">组内条件下标。</param>
        public static void RemoveGroupCondition(RuntimeFsmController controller, GroupCondition group, int index)
        {
            if (index < 0 || index >= group.conditions.Count)
            {
                return;
            }

            UnityEditor.Undo.RecordObject(controller, "Remove FSM Group Condition");
            group.conditions.RemoveAt(index);
            controller.Save();
        }

        /// <summary>仅创建未挂接的条件数据，优先引用首个参数并按其类型选择初始比较方式。</summary>
        /// <param name="controller">提供参数列表的配置。</param>
        /// <returns>目标值为零的新条件；没有参数时名称为空，需继续配置后才能通过验证。</returns>
        public static FsmConditionData CreateCondition(RuntimeFsmController controller)
        {
            FsmConditionData condition = new FsmConditionData();

            string parameterName = string.Empty;

            FsmParameterData parameter = null;

            if (controller.parameters.Count > 0)
            {
                parameter = controller.parameters[0];
                parameterName = parameter.name;
            }

            if (parameter != null)
            {
                switch (parameter.parameterType)
                {
                    case ParameterType.Float:
                        condition.compareType = CompareType.Greater;
                        break;
                    case ParameterType.Int:
                        condition.compareType = CompareType.Greater;
                        break;
                    case ParameterType.Bool:
                        condition.compareType = CompareType.Equal;
                        break;

                }
            }
            else
            {
                condition.compareType = CompareType.Greater;
            }


            condition.targetValue = 0;
            condition.parameterName = parameterName;
            return condition;
        }

        /// <summary>向过渡基础组追加默认条件，记录 Undo 并立即保存配置资产。</summary>
        /// <param name="controller">拥有过渡并提供参数的配置。</param>
        /// <param name="transition">接收基础条件的过渡。</param>
        public static void CreateCondition(RuntimeFsmController controller, FsmTransitionData transition)
        {
            UnityEditor.Undo.RecordObject(controller, "Create FSM Condition");
            FsmConditionData condition = CreateCondition(controller);
            transition.conditions.Add(condition);
            UnityEditor.EditorUtility.SetDirty(controller);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(controller);
        }

        /// <summary>按基础组下标移除条件，记录 Undo 并立即保存；下标无效时不处理。</summary>
        /// <param name="controller">拥有过渡的配置。</param>
        /// <param name="transition">待修改的过渡。</param>
        /// <param name="index">基础条件组中的下标。</param>
        public static void DeleteCondition(RuntimeFsmController controller, FsmTransitionData transition, int index)
        {
            if (index < 0 || index >= transition.conditions.Count)
            {
                return;
            }

            UnityEditor.Undo.RecordObject(controller, "Delete FSM Condition");
            transition.conditions.RemoveAt(index);


            UnityEditor.EditorUtility.SetDirty(controller);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(controller);
        }

    }

}

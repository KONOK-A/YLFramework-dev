using System.Collections.Generic;
using System.Text;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>维护参数定义及所有条件组中的名称引用，修改前记录配置 Undo，完成后标脏。</summary>
    internal class FsmParameterFactory
    {

        /// <summary>生成未占用的参数名称，并向配置追加指定类型、初值为零的参数。</summary>
        /// <param name="controller">接收参数的配置。</param>
        /// <param name="type">参数语义类型。</param>
        public static void CreateParamter(RuntimeFsmController controller, ParameterType type)
        {
            UnityEditor.Undo.RecordObject(controller, "Create FSM Parameter");

            FsmParameterData parameter = new FsmParameterData();
            parameter.name = GetDefualtName(controller, type);
            parameter.parameterType = type;
            parameter.value = 0;

            controller.AddParameters(parameter);
        }

        /// <summary>以参数类型为默认前缀，递增序号直到名称未被配置占用。</summary>
        private static string GetDefualtName(RuntimeFsmController controller, ParameterType type)
        {
            string name = string.Format("New {0}", type.ToString());
            string tempName = name;

            int i = 1;
            while (controller.GetParameterData(tempName) != null)
            {
                tempName = string.Format("{0}{1}", name, i);
                i++;
            }

            return tempName;
        }

        /// <summary>非运行期移除参数；存在条件引用时经用户确认一并删除这些条件，不删除过渡本身。</summary>
        /// <param name="controller">拥有参数及条件引用的配置。</param>
        /// <param name="index">参数列表中的下标。</param>
        public static void RemoveParamter(RuntimeFsmController controller, int index)
        {

            if (Application.isPlaying)
            {
                return;
            }

            if (controller == null || index < 0 || index >= controller.parameters.Count)
            {
                return;
            }

            FsmParameterData parameter = controller.parameters[index];

            List<FsmTransitionData> transitions = new List<FsmTransitionData>();

            // 基础组和附加组都按参数名关联，删除时必须一并收集，避免残留失效条件。
            foreach (var item in controller.transitions)
            {
                foreach (var condition in item.conditions)
                {
                    if (condition.parameterName != null && condition.parameterName.Equals(parameter.name))
                    {
                        transitions.Add(item);
                        break;
                    }

                }
                foreach (var group in item.groupConditions)
                {
                    foreach (var condition in group.conditions)
                    {
                        if (condition.parameterName == parameter.name && !transitions.Contains(item))
                        {
                            transitions.Add(item);
                        }
                    }
                }
            }

            if (transitions.Count == 0)
            {
                UnityEditor.Undo.RecordObject(controller, "Delete FSM Parameter");
                controller.RemoveParameters(parameter);
            }
            else
            {

                StringBuilder content = new StringBuilder();
                content.Append("确定删除参数:").Append(parameter.name).Append("吗？").Append("\n");
                content.Append("有以下过渡引用此参数!\n");

                foreach (var item in transitions)
                {
                    content.Append(item.fromStateName).Append(" -> ").Append(item.toStateName);
                }

                if (UnityEditor.EditorUtility.DisplayDialog("删除参数", content.ToString(), "确定", "取消"))
                {
                    UnityEditor.Undo.RecordObject(controller, "Delete FSM Parameter And Conditions");
                    controller.RemoveParameters(parameter);

                    foreach (var item in transitions)
                    {
                        foreach (var group in item.groupConditions)
                        {
                            group.conditions.RemoveAll(condition => condition.parameterName == parameter.name);
                        }

                        for (int i = item.conditions.Count - 1; i >= 0; i--)
                        {
                            FsmConditionData condition = item.conditions[i];
                            if (condition.parameterName != null && condition.parameterName.Equals(parameter.name))
                            {
                                item.conditions.RemoveAt(i);
                            }
                        }

                    }
                    controller.Save();

                }
            }

        }


        /// <summary>校验新名称后同步基础组、附加组中的所有引用，再清理参数索引并标脏。</summary>
        /// <param name="controller">拥有参数和过渡的配置。</param>
        /// <param name="parameter">待重命名参数。</param>
        /// <param name="newName">配置内尚未使用的非空名称。</param>
        public static void RenameParamter(RuntimeFsmController controller, FsmParameterData parameter, string newName)
        {

            if (string.IsNullOrEmpty(newName))
            {
                FsmEditorWindow.ShowNotification("参数名称不能为空!");
                Debug.LogError("参数名称不能为空!");
                return;
            }
            if (controller.GetParameterData(newName) != null)
            {
                FsmEditorWindow.ShowNotification("参数名称已经存在!");
                Debug.LogError("参数名称已经存在!");
                return;
            }

            UnityEditor.Undo.RecordObject(controller, "Rename FSM Parameter");
            foreach (var item in controller.transitions)
            {
                foreach (var condition in item.conditions)
                {
                    if (condition.parameterName != null && condition.parameterName.Equals(parameter.name))
                    {
                        condition.parameterName = newName;
                    }
                }

                foreach (var group in item.groupConditions)
                {
                    foreach (var condition in group.conditions)
                    {
                        if (condition.parameterName != null && condition.parameterName.Equals(parameter.name))
                        {
                            condition.parameterName = newName;
                        }
                    }
                }

            }



            parameter.name = newName;
            controller.ClearCache();
            controller.Save();
        }

    }

}

using System;
using System.Collections.Generic;

namespace YLFramework.StateMachine
{
    /// <summary>只读检查节点层级、默认状态、参数及过渡引用，不修复数据或构造状态脚本。</summary>
    internal static class FsmConfigurationValidator
    {
        /// <summary>按节点、参数、过渡顺序检查配置结构，返回首个诊断。</summary>
        /// <param name="states">包含内置节点的完整节点列表。</param>
        /// <param name="parameters">参数定义及默认值。</param>
        /// <param name="transitions">过渡及其所有条件组。</param>
        /// <returns>首个错误描述；结构有效时返回 null，脚本类型另行验证。</returns>
        public static string Validate(IList<FsmStateNodeData> states, IList<FsmParameterData> parameters, IList<FsmTransitionData> transitions)
        {
            if (states == null || parameters == null || transitions == null)
            {
                return "states/parameters/transitions collection is null.";
            }

            if (states.Count == 0)
            {
                return "states has no default state.";
            }

            var names = new Dictionary<string, FsmStateNodeData>(StringComparer.Ordinal);
            var defaults = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in states)
            {
                if (state == null || string.IsNullOrWhiteSpace(state.name))
                {
                    return "states contains a null node or an empty name.";
                }

                if (names.ContainsKey(state.name))
                {
                    return $"state '{state.name}' has a duplicate name.";
                }

                names.Add(state.name, state);
                if (state.stateScripts == null)
                {
                    return $"state '{state.name}'.StateScripts is null.";
                }

                if (state.isDefaultState && !defaults.Add(state.Parent))
                {
                    return $"state '{state.name}': duplicate default for parent '{state.Parent}'.";
                }
            }
            foreach (var state in states)
            {
                if (state.parents != null)
                {
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < state.parents.Count; i++)
                    {
                        string parent = state.parents[i];
                        if (string.IsNullOrEmpty(parent) || parent == state.name || !visited.Add(parent) || !names.TryGetValue(parent, out var node) || !node.isSubStateMachine)
                        {
                            return $"state '{state.name}'.parents[{i}] '{parent}' is missing, cyclic or not a sub-state machine.";
                        }

                        var prefix = state.parents.GetRange(0, i);
                        if (!node.CompareParent(prefix))
                        {
                            return $"state '{state.name}'.parents[{i}] '{parent}' has inconsistent ancestry.";
                        }
                    }
                }
                if (!state.isBuiltInState && !defaults.Contains(state.Parent))
                {
                    return $"state '{state.name}': parent '{state.Parent}' has no default state.";
                }
            }
            if (!defaults.Contains(string.Empty))
            {
                return "states has no default state for the base layer.";
            }

            var parameterNames = new Dictionary<string, FsmParameterData>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                if (parameter == null || string.IsNullOrWhiteSpace(parameter.name))
                {
                    return "parameters contains a null item or an empty name.";
                }

                if (parameterNames.ContainsKey(parameter.name))
                {
                    return $"parameter '{parameter.name}' is duplicated.";
                }

                if (!Enum.IsDefined(typeof(ParameterType), parameter.parameterType) || float.IsNaN(parameter.Value) || float.IsInfinity(parameter.Value))
                {
                    return $"parameter '{parameter.name}' has an invalid type/value.";
                }

                parameterNames.Add(parameter.name, parameter);
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transition in transitions)
            {
                if (transition == null)
                {
                    return "transitions contains a null item.";
                }

                if (transition.fromStateName == null || !names.TryGetValue(transition.fromStateName, out var from))
                {
                    return $"transition '{transition.Key}'.fromStateName is missing.";
                }

                if (transition.toStateName == null || !names.TryGetValue(transition.toStateName, out var to))
                {
                    return $"transition '{transition.Key}'.toStateName is missing.";
                }

                if (!from.CompareParent(to))
                {
                    return $"transition '{transition.Key}' crosses incompatible parent layers.";
                }

                if (!keys.Add(transition.Key))
                {
                    return $"transition '{transition.Key}' is duplicated.";
                }

                string error = ValidateConditions(transition.conditions, transition.Key, parameterNames);
                if (error != null)
                {
                    return error;
                }

                if (transition.groupConditions == null)
                {
                    return $"transition '{transition.Key}'.group_conditions is null.";
                }

                foreach (var group in transition.groupConditions)
                {
                    if (group == null)
                    {
                        return $"transition '{transition.Key}' contains a null condition group.";
                    }

                    error = ValidateConditions(group.conditions, transition.Key, parameterNames);
                    if (error != null)
                    {
                        return error;
                    }
                }
            }
            return null;
        }

        /// <summary>检查一组条件的参数引用、比较类型和有限目标值，保留所属过渡标识用于诊断。</summary>
        private static string ValidateConditions(IList<FsmConditionData> conditions, string transition, Dictionary<string, FsmParameterData> parameters)
        {
            if (conditions == null)
            {
                return $"transition '{transition}'.conditions is null.";
            }

            foreach (var condition in conditions)
            {
                if (condition == null || condition.parameterName == null || !parameters.ContainsKey(condition.parameterName))
                {
                    return $"transition '{transition}'.condition parameter '{condition?.parameterName}' is missing.";
                }

                if (!Enum.IsDefined(typeof(CompareType), condition.compareType) || float.IsNaN(condition.targetValue) || float.IsInfinity(condition.targetValue))
                {
                    return $"transition '{transition}'.condition '{condition.parameterName}' has an invalid comparison/value.";
                }
            }
            return null;
        }
    }
}

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>维护节点及相关过渡、祖先引用的作者操作，修改配置前记录 Undo。</summary>
    internal class FsmStateNodeFactory
    {
        /// <summary>以唯一名称创建节点，设为默认时取消同层旧默认，并保存配置资产。</summary>
        /// <param name="controller">接收节点的配置。</param>
        /// <param name="name">配置内唯一的节点名称。</param>
        /// <param name="rect">图中的位置和尺寸。</param>
        /// <param name="defaultState">是否设为该层默认节点。</param>
        /// <param name="isSubStateMachine">是否为子状态机容器；此方法不创建内部节点。</param>
        /// <param name="parent">完整祖先列表，直接赋给节点而非复制。</param>
        /// <param name="isBuildInState">是否标记为内置节点。</param>
        /// <param name="buildInStateName">内置节点种类标识。</param>
        /// <returns>新增节点；名称重复时为 null。</returns>
        public static FsmStateNodeData CreateStateNode(RuntimeFsmController controller, string name, Rect rect, bool defaultState, bool isSubStateMachine = false, List<string> parent = null, bool isBuildInState = false, string buildInStateName = "")
        {

            if (controller.GetStateNodeData(name) != null)
            {
                string message = string.Format("创建状态节点失败,名称:{0}重复!", name);
                Debug.LogError(message);
                FsmEditorWindow.ShowNotification(message);
                return null;
            }

            FsmStateNodeData node = new FsmStateNodeData();
            Undo.RecordObject(controller, "Create FSM State");
            node.name = name;
            node.rect = rect;
            node.isSubStateMachine = isSubStateMachine;
            node.parents = parent;
            node.isBuiltInState = isBuildInState;
            node.builtInStateName = buildInStateName;
            if (defaultState)
            {
                foreach (var item in controller.states)
                {
                    if (!item.CompareParent(node))
                    {
                        continue;
                    }

                    item.isDefaultState = false;
                }
            }

            node.isDefaultState = defaultState;
            controller.AddState(node);
            AssetDatabase.SaveAssetIfDirty(controller);
            return node;
        }

        /// <summary>生成配置内未占用的节点名称，再按指定层级与身份创建节点。</summary>
        /// <param name="controller">接收节点的配置。</param>
        /// <param name="rect">图中的位置和尺寸。</param>
        /// <param name="defaultState">是否替换同层默认节点。</param>
        /// <param name="isSubStateMachine">是否为子状态机容器。</param>
        /// <param name="parent">完整祖先列表，直接赋给节点。</param>
        /// <param name="isBuildInState">是否标记为内置节点。</param>
        /// <param name="buildInStateName">内置节点种类标识。</param>
        /// <returns>新增节点。</returns>
        public static FsmStateNodeData CreateStateNode(RuntimeFsmController controller, Rect rect, bool defaultState, bool isSubStateMachine = false, List<string> parent = null, bool isBuildInState = false, string buildInStateName = "")
        {

            return CreateStateNode(controller, GetStateNodeName(controller), rect, defaultState, isSubStateMachine, parent, isBuildInState, buildInStateName);
        }

        /// <summary>从默认前缀起递增序号，查找配置内未占用的全局节点名称。</summary>
        private static string GetStateNodeName(RuntimeFsmController controller)
        {

            string name = null;
            int i = 1;
            do
            {
                name = string.Format("New State{0}", i);
                i++;
            } while (controller.GetStateNodeData(name) != null);



            return name;
        }

        /// <summary>移除节点、相连过渡及容器的后代，并在同层选择替代默认节点；基础层内置节点保留。</summary>
        /// <param name="controller">拥有节点的配置，删除后标脏但此入口不立即落盘。</param>
        /// <param name="state">要移除的节点。</param>
        public static void DeleteState(RuntimeFsmController controller, FsmStateNodeData state)
        {
            Undo.RecordObject(controller, "Delete FSM State");

            if (state.IsAnyState || state.IsEntryState || state.IsUpState)
            {
                if (state.BaseLayer())
                {
                    string message = string.Format("状态:{0}不能删除!", state.DisplayName);
                    Debug.LogWarning(message);
                    FsmEditorWindow.ShowNotification(message);
                    return;
                }
            }

            for (int i = 0; i < controller.transitions.Count; i++)
            {
                FsmTransitionData transitionData = controller.transitions[i];
                if (transitionData.fromStateName.Equals(state.name) || transitionData.toStateName.Equals(state.name))
                {
                    controller.RemoveTransition(transitionData);
                    i--;
                }
            }

            controller.RemoveState(state);
            if (state.isDefaultState)
            {
                foreach (var item in controller.states)
                {
                    if (item.IsAnyState || item.IsEntryState || item.IsUpState)
                    {
                        continue;
                    }

                    if (!item.CompareParent(state))
                    {
                        continue;
                    }

                    item.isDefaultState = true;
                    break;
                }
            }

            // 先收集直接子节点再递归删除，避免遍历时修改原列表，并由递归继续处理更深层级。
            if (state.isSubStateMachine)
            {

                List<FsmStateNodeData> subStates = new List<FsmStateNodeData>();
                foreach (var item in controller.states)
                {
                    if (item.BelongToParent(state.name))
                    {
                        subStates.Add(item);
                    }
                }

                foreach (var item in subStates)
                {
                    DeleteState(controller, item);
                }

            }

        }

        /// <summary>重命名节点并同步过渡端点；容器改名还更新后代祖先链及内置节点路径，最后清缓存并标脏。</summary>
        /// <param name="controller">拥有节点和关联引用的配置。</param>
        /// <param name="node">要重命名的节点。</param>
        /// <param name="newName">配置内尚未占用的非空名称。</param>
        /// <returns>更新成功时为 true；受限内置名称、空名称或名称冲突时为 false。</returns>
        public static bool Rename(RuntimeFsmController controller, FsmStateNodeData node, string newName)
        {

            if (node.name.Equals(FsmConst.EntryState) || node.name.Equals(FsmConst.AnyState))
            {
                return false;
            }

            if (string.IsNullOrEmpty(newName))
            {
                FsmEditorWindow.ShowNotification("名称不能为空!");
                return false;
            }

            if (controller.GetStateNodeData(newName) != null)
            {
                string message = string.Format("状态重命名失败,名称:{0}已经存在,请使用其他的名称!", newName);
                FsmEditorWindow.ShowNotification(message);
                return false;
            }

            Undo.RecordObject(controller, "Rename FSM State");
            foreach (var item in controller.transitions)
            {
                if (item.fromStateName.Equals(node.name))
                {
                    item.fromStateName = newName;
                }

                if (item.toStateName.Equals(node.name))
                {
                    item.toStateName = newName;
                }
            }

            string oldName = node.name;
            node.name = newName;

            if (node.isSubStateMachine)
            {
                // 后代通过祖先名称关联容器，容器改名必须同步整条祖先链而不只更新显示标题。
                List<FsmStateNodeData> subStates = new List<FsmStateNodeData>();
                foreach (var item in controller.states)
                {
                    if (item.ContainsParent(oldName))
                    {
                        subStates.Add(item);
                    }
                }

                foreach (var item in subStates)
                {
                    item.RenameParent(oldName, newName);

                    if (item.isBuiltInState)
                    {
                        string itemNewName = string.Format("{0}/{1}", item.ParentPath, item.builtInStateName);
                        Rename(controller, item, itemNewName);
                    }

                }
            }

            controller.ClearCache();
            EditorUtility.SetDirty(controller);

            return true;
        }

    }

}

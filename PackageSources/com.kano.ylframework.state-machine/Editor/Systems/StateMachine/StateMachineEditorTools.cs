using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>实现运行时数据的编辑器桥接，集中处理标脏、脚本类型筛选与 GUID 绑定刷新。</summary>
    [InitializeOnLoad]
    internal static class StateMachineEditorTools
    {
        /// <summary>编辑器程序集加载时安装桥接，保存请求仅标脏，克隆通知仅记录源资产 GUID。</summary>
        static StateMachineEditorTools()
        {
            FsmEditorHooks.Save = controller =>
            {
                if (controller)
                {
                    EditorUtility.SetDirty(controller);
                }
            };
            FsmEditorHooks.RefreshNode = RefreshNode;
            FsmEditorHooks.AddScript = AddScript;
            FsmEditorHooks.RemoveScript = RemoveScript;
            FsmEditorHooks.CloneCreated = (source, clone) => clone.originGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
        }

        /// <summary>从已导入的运行时脚本中筛选可反射创建的具体状态类型。</summary>
        /// <returns>具有公开无参构造器的具体 FsmState 脚本资产列表。</returns>
        public static List<MonoScript> GetAllStatesType()
        {
            var scripts = new List<MonoScript>();
            foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
            {
                if (script && IsStateType(script.GetClass()))
                {
                    scripts.Add(script);
                }
            }

            return scripts;
        }

        /// <summary>判断类型是否为非抽象、非开放泛型且具有公开无参构造器的 FsmState。</summary>
        private static bool IsStateType(Type type) => type != null && !type.IsAbstract && !type.ContainsGenericParameters && typeof(FsmState).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null;

        /// <summary>校验已导入的状态脚本并按 GUID 去重后追加绑定，Undo 与保存交由作者入口处理。</summary>
        private static void AddScript(FsmStateNodeData node, UnityEngine.Object value)
        {
            var script = value as MonoScript;
            if (!script || !IsStateType(script.GetClass()))
            {
                throw new ArgumentException("State script must be a concrete FSMState with a public parameterless constructor.", nameof(value));
            }

            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
            if (string.IsNullOrEmpty(guid))
            {
                throw new ArgumentException("State script has not been imported.", nameof(value));
            }

            if (node.stateScripts.Exists(info => info.guid == guid))
            {
                throw new InvalidOperationException("State script is already attached.");
            }

            node.stateScripts.Add(new FsmStateScriptInfo { className = script.GetClass().FullName, guid = guid });
        }

        /// <summary>按资产 GUID 移除节点中的全部匹配绑定，不删除脚本资产。</summary>
        private static void RemoveScript(FsmStateNodeData node, UnityEngine.Object value)
        {
            if (!value)
            {
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value));
            node.stateScripts.RemoveAll(info => info.guid == guid);
        }

        /// <summary>仅用既有 GUID 解析脚本，类型名变化时记录配置 Undo 并标脏，不扫描迁移无 GUID 的旧记录。</summary>
        private static void RefreshNode(FsmStateNodeData node, RuntimeFsmController controller)
        {
            foreach (var info in node.stateScripts)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(info.guid));
                // 编译中暂时失效的绑定不删除；运行前验证会报告状态和类型，保留可修复数据。
                if (!script || !IsStateType(script.GetClass()))
                {
                    continue;
                }

                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
                string className = script.GetClass().FullName;
                if (info.guid == guid && info.className == className)
                {
                    continue;
                }

                if (controller)
                {
                    Undo.RecordObject(controller, "Refresh FSM Script Binding");
                }

                info.guid = guid;
                info.className = className;
                if (controller)
                {
                    controller.Save();
                }
            }
        }
    }
}

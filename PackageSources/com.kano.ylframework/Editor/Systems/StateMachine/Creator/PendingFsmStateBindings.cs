using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>用当前编辑器会话数据跨程序集重载保留新脚本绑定请求，待导入及类型解析就绪后完成绑定。</summary>
    [InitializeOnLoad]
    internal static class PendingFsmStateBindings
    {
        /// <summary>一次绑定所需的配置身份、节点名称和脚本路径。</summary>
        [Serializable]
        private sealed class Binding
        {
            /// <summary>已保存配置的资产 GUID。</summary>
            public string assetGuid;
            /// <summary>登记时的目标节点名称。</summary>
            public string node;
            /// <summary>等待导入的脚本项目路径。</summary>
            public string scriptPath;
        }
        /// <summary>供会话 JSON 序列化使用的待绑定列表外壳。</summary>
        [Serializable]
        private sealed class Pending
        {
            /// <summary>尚未完成或因编译未就绪而保留的绑定请求。</summary>
            public List<Binding> items = new List<Binding>();
        }
        private static string Key => "YLFramework.StateMachine.PendingBindings/" + Application.dataPath;
        /// <summary>从当前工程的会话键读取待绑定记录，不持久化为项目资产。</summary>
        private static Pending Load() => JsonUtility.FromJson<Pending>(SessionState.GetString(Key, "{\"items\":[]}"));
        /// <summary>将当前待绑定列表写回工程对应的编辑器会话键。</summary>
        private static void Save(Pending pending) => SessionState.SetString(Key, JsonUtility.ToJson(pending));

        /// <summary>在程序集重载和编译完成后安排检查，并恢复本会话已有的等待请求。</summary>
        static PendingFsmStateBindings()
        {
            AssemblyReloadEvents.afterAssemblyReload += WaitForImport;
            CompilationPipeline.compilationFinished += _ => WaitForImport();
            WaitForImport();
        }

        /// <summary>以配置 GUID 和节点名称登记请求；只安排就绪检查，不立即绑定未编译脚本。</summary>
        /// <param name="controller">已保存为资产的配置。</param>
        /// <param name="node">当前存在的目标节点。</param>
        /// <param name="scriptPath">待导入的脚本项目路径。</param>
        public static void Schedule(RuntimeFsmController controller, FsmStateNodeData node, string scriptPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(controller));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("Save the FSM asset before creating and attaching a state script.");
            }

            var pending = Load();
            pending.items.Add(new Binding { assetGuid = guid, node = node.name, scriptPath = scriptPath });
            Save(pending);
            WaitForImport();
        }

        /// <summary>有待处理请求时去重订阅编辑器更新，等待导入及编译结束。</summary>
        private static void WaitForImport()
        {
            if (Load().items.Count == 0)
            {
                return;
            }

            EditorApplication.update -= CompleteWhenReady;
            EditorApplication.update += CompleteWhenReady;
        }

        /// <summary>就绪后按 GUID 解析配置、按名定位节点，以 Undo 绑定并落盘；无类型结果的请求留待下次编译。</summary>
        private static void CompleteWhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            EditorApplication.update -= CompleteWhenReady;
            var pending = Load();
            for (int i = pending.items.Count - 1; i >= 0; i--)
            {
                var item = pending.items[i];
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(AssetDatabase.GUIDToAssetPath(item.assetGuid));
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(item.scriptPath);
                if (!controller)
                {
                    pending.items.RemoveAt(i);
                    continue;
                }
                var node = controller.GetStateNodeData(item.node);
                if (node == null)
                {
                    Debug.LogWarning($"YL Asset FSM: pending script '{item.scriptPath}' targets removed state '{item.node}'.", controller);
                    pending.items.RemoveAt(i);
                    continue;
                }
                // 编译失败或仍在重载时保留记录；下一次编译完成再试，不逐帧忙轮询。
                var type = script ? script.GetClass() : null;
                if (type == null)
                {
                    continue;
                }

                if (!typeof(FsmState).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                {
                    Debug.LogError($"YL Asset FSM: '{item.scriptPath}' must define a concrete FSMState with a public parameterless constructor.", controller);
                    pending.items.RemoveAt(i);
                    continue;
                }
                string guid = AssetDatabase.AssetPathToGUID(item.scriptPath);
                if (!node.stateScripts.Exists(info => info.guid == guid))
                {
                    Undo.RecordObject(controller, "Attach Compiled FSM State Script");
                    node.AddStateScript(script);
                    controller.Save();
                    AssetDatabase.SaveAssetIfDirty(controller);
                }
                pending.items.RemoveAt(i);
                EditorGUIUtility.PingObject(script);
            }
            Save(pending);
        }
    }
}

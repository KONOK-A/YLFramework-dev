using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>为配置资产提供图窗口入口及只读配置诊断。</summary>
    [CustomEditor(typeof(RuntimeFsmController))]
    internal sealed class FsmConfigurationInspector : UnityEditor.Editor
    {
        /// <summary>绘制图窗口入口与默认属性，并显示首个配置错误；打开图前刷新脚本绑定。</summary>
        public override void OnInspectorGUI()
        {
            var controller = (RuntimeFsmController)target;
            if (GUILayout.Button("Open YL Asset FSM Graph"))
            {
                controller.RefreshStateScripts();
                Context.Instance.RuntimeFsmControllerGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(controller));
                Context.Instance.FsmControllerInstanceId = 0;
                EditorWindow.GetWindow<FsmEditorWindow>().Show();
            }
            DrawDefaultInspector();
            if (!controller.ValidateConfiguration(out string error))
            {
                EditorGUILayout.HelpBox($"Asset '{controller.name}': {error}", MessageType.Error);
            }
        }
    }
}

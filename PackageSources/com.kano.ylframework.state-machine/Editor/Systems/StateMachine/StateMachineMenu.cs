using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>提供配置、状态脚本和图窗口的编辑器菜单入口。</summary>
    internal class StateMachineMenu
    {
        /// <summary>启动配置资产命名流程，由专用创建器补齐内置节点而非仅创建空资产。</summary>
        [MenuItem("Assets/Create/YLFramework/Asset FSM/FSMController")]
        static void CreateFsm()
        {

            RuntimeFsmControllerCreator runtimeFsmControllerCreator = ScriptableObject.CreateInstance<RuntimeFsmControllerCreator>();

            string fileName = GetName();

            GUIContent content = EditorGUIUtility.IconContent("d_ScriptableObject Icon");

            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(runtimeFsmControllerCreator.GetInstanceID(), runtimeFsmControllerCreator, fileName, (Texture2D)content.image, null);
        }

        /// <summary>按资产数据库搜索结果为默认名称追加序号，不替代最终文件创建时的占用检查。</summary>
        /// <param name="templateName">不带扩展名的基础名称。</param>
        /// <param name="suffix">不带句点的扩展名。</param>
        /// <returns>带扩展名的候选名称。</returns>
        internal static string GetName(string templateName = "New FSMController", string suffix = "asset")
        {

            string name;
            string[] files;

            int i = 0;

            do
            {
                name = string.Format("{0}{1}", templateName, i == 0 ? string.Empty : i.ToString());
                files = AssetDatabase.FindAssets(name);

                i++;
            } while (files != null && files.Length != 0);

            return string.Format("{0}.{1}", name, suffix);
        }

        /// <summary>打开或显示状态机图编辑窗口。</summary>
        [MenuItem("Window/YLFramework/Asset FSM/StateMachineWindow", false, 4000)]
        static void StateMachineWindow()
        {
            FsmEditorWindow window = EditorWindow.GetWindow<FsmEditorWindow>();
            window.Show();
        }

        /// <summary>在 Project 窗口中启动状态脚本命名及模板创建流程。</summary>
        [MenuItem("Assets/Create/YLFramework/Asset FSM/FSMState")]
        static void CreateFsmState()
        {
            FsmStateCreator stateCreator = ScriptableObject.CreateInstance<FsmStateCreator>();

            string fileName = GetName("NewState", "cs");

            GUIContent content = EditorGUIUtility.IconContent("icons/processed/cs script icon.asset");

            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(stateCreator.GetInstanceID(), stateCreator, fileName, (Texture2D)content.image, string.Empty);
        }

        /// <summary>打开状态机模块的关于窗口。</summary>
        [MenuItem("Window/YLFramework/Asset FSM/About", false, 5000)]
        static void About()
        {
            //Debug.Log("About");
            Rect rect = new Rect(0, 0, 550, 350);
            FsmAboutWindow window = EditorWindow.GetWindowWithRect<FsmAboutWindow>(rect, true, "About YL Asset FSM");
            window.Show();
        }

    }

}

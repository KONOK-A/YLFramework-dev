using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using UnityEngine;
using YLFramework.StateMachine;


namespace YLFramework.Editor.StateMachine
{
    /// <summary>校验新脚本路径和类型名称，以模板创建普通状态类，并可登记编译后的节点绑定。</summary>
    public class FsmStateCreator : EndNameEditAction
    {

        static string _regex = "^[a-zA-Z][a-zA-Z0-9_]*$";

        /// <summary>响应 Project 窗口命名完成，按最终路径创建状态脚本。</summary>
        /// <param name="instanceId">命名流程提供的实例标识，此实现不使用。</param>
        /// <param name="pathName">用户确认的脚本路径。</param>
        /// <param name="resourceFile">命名流程提供的模板参数，此实现使用固定模板。</param>
        public override void Action(int instanceId, string pathName, string resourceFile)
        {
            CreateFsmState(pathName);
        }


        /// <summary>拒绝已占用路径及已导入的同名类型，替换模板类名并以只新建方式写入，再请求导入。</summary>
        /// <param name="pathName">目标脚本路径，父目录须已存在。</param>
        /// <param name="errorTip">是否对文件或类型占用、写入冲突显示对话框；非法名称始终提示。</param>
        /// <param name="controller">可选的待绑定配置，须已保存为资产。</param>
        /// <param name="node">可选的目标节点，与配置同时提供时登记延迟绑定。</param>
        /// <returns>文件已成功新建且已请求导入时为 true，不表示编译和节点绑定已完成。</returns>
        internal static bool CreateFsmState(string pathName, bool errorTip = true, RuntimeFsmController controller = null, FsmStateNodeData node = null)
        {

            string fileName = Path.GetFileNameWithoutExtension(pathName);
            // 文件尚未被 Unity 导入或其中类型与文件名不一致，也不能授权覆盖。
            if (File.Exists(pathName))
            {
                if (errorTip)
                {
                    EditorUtility.DisplayDialog("提示", $"Script file already exists: {pathName}", "确定");
                }

                return false;
            }

            string[] guids = AssetDatabase.FindAssets(fileName);

            foreach (var item in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(item);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
                if (script == null || script.GetClass() == null)
                {
                    continue;
                }

                if (script.GetClass().Name.Equals(fileName))
                {
                    if (errorTip)
                    {
                        string message = string.Format("脚本名称:{0}已经存在!", fileName);
                        //FSMEditorWindow.ShowNotification(string.Format("脚本名称:{0}已经存在!", fileName));
                        EditorUtility.DisplayDialog("提示", message, "确定");
                    }
                    return false;
                }
            }

            if (!Regex.Match(fileName, _regex).Success)
            {
                EditorUtility.DisplayDialog("提示", string.Format("文件名:{0}不可用!", fileName), "确定");
                return false;
            }

            TextAsset template = AssetDatabase.LoadAssetAtPath<TextAsset>(global::YLFramework.Editor.Common.FrameworkPackagePaths.AssetPath(
                typeof(FsmStateCreator).Assembly, "Editor/Systems/StateMachine/Template/FSMStateTemplate.txt"));
            if (!template)
            {
                Debug.LogError("YL Asset FSM: FSMStateTemplate.txt is missing from the Editor/Systems/StateMachine/Template directory.");
                return false;
            }
            string content = template.text;


            content = content.Replace("{0}", fileName);

            if (!FsmStateFileWriter.TryWriteNew(pathName, content, out string error))
            {
                if (errorTip)
                {
                    EditorUtility.DisplayDialog("提示", error, "确定");
                }

                return false;
            }
            if (controller && node != null)
            {
                PendingFsmStateBindings.Schedule(controller, node, pathName);
            }

            AssetDatabase.Refresh();
            return true;
        }

    }

}

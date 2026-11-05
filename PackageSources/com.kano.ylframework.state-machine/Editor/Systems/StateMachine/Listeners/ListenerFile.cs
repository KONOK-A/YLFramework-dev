using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>将 Project 和层级选择同步到图上下文，并响应配置资产双击及编辑器退出。</summary>
    internal class ListenerFile
    {


        /// <summary>加载编辑器程序集时去重注册选择变化与退出回调。</summary>
        [InitializeOnLoadMethod]
        private static void InitOnLoad()
        {
            //Debug.Log("Init");
            Selection.selectionChanged -= OnSelectionChange;
            Selection.selectionChanged += OnSelectionChange;


            EditorApplication.quitting -= OnEditorApplicationQuitting;
            EditorApplication.quitting += OnEditorApplicationQuitting;
        }

        /// <summary>优先选择配置资产，否则解析宿主的首个有效配置层，并保留源资产身份。</summary>
        private static void OnSelectionChange()
        {
            RuntimeFsmController controller = Selection.activeObject as RuntimeFsmController;
            if (controller != null)
            {
                // 直接选择资产时解除宿主绑定，使图切回资产编辑而非活动克隆。
                Context.Instance.FsmControllerInstanceId = 0;
                string assetPath = AssetDatabase.GetAssetPath(controller);
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                Context.Instance.RuntimeFsmControllerGuid = guid;
            }
            else
            {
                GameObject gameObj = Selection.activeGameObject;

                if (gameObj == null)
                {
                    return;
                }

                if (gameObj.activeInHierarchy == false)
                {
                    string path = AssetDatabase.GetAssetPath(gameObj);
                    if (string.IsNullOrEmpty(path)) // 路径不为空说明是预制体
                    {
                        return;
                    }
                }
                FsmController fsmController = gameObj.GetComponent<FsmController>();
                if (FsmControllerIsEmpty(fsmController))
                {
                    return;
                }

                for (int i = 0; i < fsmController.RuntimeFsmController.Count; i++)
                {
                    if (fsmController.RuntimeFsmController[i] != null)
                    {
                        Context.Instance.FsmControllerIndex = i;
                        break;
                    }
                }

                Context.Instance.FsmControllerInstanceId = gameObj.GetInstanceID();
            }

            Context.Instance.RefreshRuntimeFsmControllerGuid();
        }


        /// <summary>判断宿主是否缺失或其配置列表中没有任何有效配置。</summary>
        private static bool FsmControllerIsEmpty(FsmController fsmController)
        {
            if (fsmController == null)
            {
                return true;
            }

            if (fsmController.RuntimeFsmController == null)
            {
                return true;
            }

            if (fsmController.RuntimeFsmController.Count == 0)
            {
                return true;
            }

            for (int i = 0; i < fsmController.RuntimeFsmController.Count; i++)
            {
                if (fsmController.RuntimeFsmController[i] != null)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>编辑器退出时清除宿主实例选择和层索引，避免下次沿用失效实例标识。</summary>
        private static void OnEditorApplicationQuitting()
        {
            Context.Instance.FsmControllerIndex = 0;
            Context.Instance.FsmControllerInstanceId = 0;
        }

        /// <summary>拦截配置资产双击，刷新已有脚本绑定并显示图窗口。</summary>
        [UnityEditor.Callbacks.OnOpenAsset(0)]
        private static bool OnOpenAsset(int insId, int line)
        {
#if UNITY_6000_2_OR_NEWER
            RuntimeFsmController obj = EditorUtility.EntityIdToObject(insId) as RuntimeFsmController;
#else
            RuntimeFsmController obj = EditorUtility.InstanceIDToObject(insId) as RuntimeFsmController;
#endif


            if (obj != null)
            {
                obj.RefreshStateScripts();
                EditorWindow.GetWindow<FsmEditorWindow>().Show();
            }
            return obj != null;
        }
    }
}

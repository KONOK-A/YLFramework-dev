using UnityEditor;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{
    /// <summary>在任务配表删除或打开时接入任务条目目录清理和专用窗口。</summary>
    public class ListenerFileEvent : UnityEditor.AssetModificationProcessor
    {
        /// <summary>删除任务配表前请求删除其 GUID 对应条目目录，再让 Unity 继续处理原资产删除。</summary>
        /// <remarks>返回 DidNotDelete 不表示目录未删除；本回调不检查目录删除结果，也不提供整批回滚。</remarks>
        public static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions option)
        {
            MissionsConfig missionsConfig = AssetDatabase.LoadAssetAtPath<MissionsConfig>(assetPath);

            if (missionsConfig != null)
            {

                string dir = MissionAuthoring.GeneratedDirectory(missionsConfig);
                AssetDatabase.DeleteAsset(dir);
            }

            return AssetDeleteResult.DidNotDelete;
        }

        /// <summary>打开任务配表时转到专用编辑窗口，并返回是否已处理该资产。</summary>
        [UnityEditor.Callbacks.OnOpenAsset(0)]
        private static bool OnOpenAsset(int insId, int line)
        {
            MissionsConfig obj = EditorUtility.InstanceIDToObject(insId) as MissionsConfig;
            if (obj != null)
            {
                MissionConfigInspector.OpenMissionsConfig(obj);
            }
            return obj != null;
        }

    }
}

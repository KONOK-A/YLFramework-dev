using UnityEditor;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;

namespace YLFramework.Editor.Buff
{
    /// <summary>把 Buff 配表的打开与删除接入作者窗口及其约定数据目录。</summary>
    public class ListenerFileEvent : UnityEditor.AssetModificationProcessor
    {
        /// <summary>删除 Buff 配表前请求删除其 GUID 对应数据目录，配表自身仍交由 Unity 删除。</summary>
        /// <param name="assetPath">Unity 请求删除的资产路径。</param>
        /// <param name="option">Unity 传入的删除选项，此回调不按它分支。</param>
        /// <returns>始终返回 DidNotDelete，不接管目标资产的实际删除。</returns>
        public static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions option)
        {
            BuffsConfig skillsConfig = AssetDatabase.LoadAssetAtPath<BuffsConfig>(assetPath);

            if (skillsConfig != null)
            {
                string dir = BuffsConfigController.GetBuffConfigDataPath(skillsConfig);
                AssetDatabase.DeleteAsset(dir);
            }

            return AssetDeleteResult.DidNotDelete;
        }

        /// <summary>双击 Buff 配表时打开对应作者窗口，并标记该打开请求已处理。</summary>
        [UnityEditor.Callbacks.OnOpenAsset(0)]
        private static bool OnOpenAsset(int insId, int line)
        {
            BuffsConfig obj = EditorUtility.InstanceIDToObject(insId) as BuffsConfig;
            if (obj != null)
            {
                BuffsConfigInspector.OpenBuffsConfig(obj);
            }
            return obj != null;
        }

    }
}

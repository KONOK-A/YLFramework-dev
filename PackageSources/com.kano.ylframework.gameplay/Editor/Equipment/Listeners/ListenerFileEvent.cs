using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>把装备配表的打开与删除请求接入作者窗口和按归属清理的流程。</summary>
    public sealed class ListenerFileEvent : AssetModificationProcessor
    {
        /// <summary>删除配表前逐项处理定义，只删除已空的数据目录，配表本身仍交给 Unity 删除。</summary>
        private static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            var root = AssetDatabase.LoadAssetAtPath<EquipmentsConfig>(path);
            if (!root)
            {
                return AssetDeleteResult.DidNotDelete;
            }

            var folder = EquipmentsConfigController.GetEquipmentConfigDataPath(root);
            foreach (var data in root.Equipments.ToArray())
            {
                if (data)
                {
                    EquipmentsConfigController.DeleteEquipmentConfigData(data, root);
                }
            }

            if (AssetDatabase.IsValidFolder(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                AssetDatabase.DeleteAsset(folder);
            }

            return AssetDeleteResult.DidNotDelete;
        }

        /// <summary>双击装备配表时打开对应窗口，其他资产继续交由 Unity 处理。</summary>
        [OnOpenAsset]
        private static bool OnOpen(int id, int line)
        {
            if (!(EditorUtility.InstanceIDToObject(id) is EquipmentsConfig root))
            {
                return false;
            }

            EquipmentsConfigInspector.OpenEquipmentsConfig(root);
            return true;
        }
    }
}

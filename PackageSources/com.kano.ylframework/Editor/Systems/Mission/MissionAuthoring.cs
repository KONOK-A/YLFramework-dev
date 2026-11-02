using System;
using UnityEditor;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{
    /// <summary>任务作者工具的类型绑定和条目资产身份入口，不参与运行时任务更新。</summary>
    internal static class MissionAuthoring
    {
        /// <summary>按配表 GUID 分目录保存任务条目的生成根目录。</summary>
        internal const string GeneratedRoot = "Assets/YLFrameworkData/Mission";
        /// <summary>由配表资产 GUID 推导其条目目录，避免目录随配表名称变化。</summary>
        internal static string GeneratedDirectory(MissionsConfig config) => GeneratedRoot + "/" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(config));
        /// <summary>解析配表绑定的非抽象任务数据类型；未绑定用基础类型，失效绑定直接抛错。</summary>
        internal static Type GetDataType(this MissionsConfig config)
        {
            if (string.IsNullOrEmpty(config.typeScriptGuid))
            {
                return typeof(MissionConfigData);
            }

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(config.typeScriptGuid));
            var type = script ? script.GetClass() : null;
            if (type == null)
            {
                throw new InvalidOperationException($"任务数据类型绑定失效: {config.name}, GUID={config.typeScriptGuid}");
            }

            if (!typeof(MissionConfigData).IsAssignableFrom(type) || type.IsAbstract)
            {
                throw new InvalidOperationException($"任务数据类型无效: {config.name}, {type.FullName}");
            }

            return type;
        }
        /// <summary>按条目 ID 请求重命名受配表拥有的对象或文件，随后清除源定义缓存。</summary>
        /// <remarks>同文件子资产通过对象 Undo 改名；独立受管文件走资产改名，外部借用对象不被改名。</remarks>
        internal static void Rename(this MissionConfigData data)
        {
            YLFramework.Editor.Domain.DomainAssetOwnership.RenameEntry(data.parent, data, data.id.ToString(), GeneratedDirectory(data.parent));
            data.parent.ClearCache();
        }
    }
}

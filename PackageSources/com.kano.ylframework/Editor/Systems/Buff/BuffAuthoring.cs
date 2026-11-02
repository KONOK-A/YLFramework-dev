using System;
using UnityEditor;
using YLFramework.Buff;

namespace YLFramework.Editor.Buff
{
    /// <summary>在 Editor 中解析 Buff 数据类型绑定，并按配表资产归属处理条目命名。</summary>
    internal static class BuffAuthoring
    {
        /// <summary>解析配表指定的具体数据类型；未绑定时使用 BuffConfigData，失效绑定抛错。</summary>
        internal static Type GetDataType(this BuffsConfig config)
        {
            if (string.IsNullOrEmpty(config.typeScriptGuid))
            {
                return typeof(BuffConfigData);
            }

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(config.typeScriptGuid));
            var type = script ? script.GetClass() : null;
            if (type == null)
            {
                throw new InvalidOperationException($"Buff数据类型绑定失效: {config.name}, GUID={config.typeScriptGuid}");
            }

            if (!typeof(BuffConfigData).IsAssignableFrom(type) || type.IsAbstract)
            {
                throw new InvalidOperationException($"Buff数据类型无效: {config.name}, {type.FullName}");
            }

            return type;
        }
        /// <summary>按 ID 重命名配表拥有的条目并清除查询缓存，不改名外部借用资产。</summary>
        internal static void Rename(this BuffConfigData data)
        {
            YLFramework.Editor.Domain.DomainAssetOwnership.RenameEntry(data.parent, data, data.id.ToString(), BuffsConfigController.GetBuffConfigDataPath(data.parent));
            data.parent.ClearCache();
        }
    }
}

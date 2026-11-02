using System;
using UnityEditor;
using YLFramework.Editor.Domain;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{
    /// <summary>技能作者工具的类型绑定与资产改名入口，不改变宿主的运行技能。</summary>
    internal static class SkillAuthoring
    {
        /// <summary>解析配表绑定的非抽象技能数据类型；未绑定用基础类型，绑定失效时抛错。</summary>
        internal static Type GetDataType(this SkillsConfig config)
        {
            if (string.IsNullOrEmpty(config.typeScriptGuid))
            {
                return typeof(SkillConfigData);
            }

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(config.typeScriptGuid));
            var type = script ? script.GetClass() : null;
            if (type == null)
            {
                throw new InvalidOperationException($"技能数据类型绑定失效: {config.name}, GUID={config.typeScriptGuid}");
            }

            if (!typeof(SkillConfigData).IsAssignableFrom(type) || type.IsAbstract)
            {
                throw new InvalidOperationException($"技能配表数据类型无效: {config.name}, {type.FullName}");
            }

            return type;
        }

        /// <summary>按技能 ID 请求重命名归属配表的条目对象或文件，并清除配表查找缓存。</summary>
        /// <remarks>同文件子资产记录对象 Undo；独立受管文件使用资产改名，外部借用对象不改名。</remarks>
        internal static void Rename(this SkillConfigData data)
        {
            DomainAssetOwnership.RenameEntry(data.parent, data, data.id.ToString(), SkillsConfigController.GetSkillConfigDataPath(data.parent));
            data.parent.ClearCache();
        }
    }

}

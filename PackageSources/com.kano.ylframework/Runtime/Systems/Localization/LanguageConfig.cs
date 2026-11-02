using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Localization
{
    /// <summary>保存单个语言的条目定义，并以可失效索引提供精确 ID 查询。</summary>
    public class LanguageConfig : ScriptableObject
    {
        /// <summary>此资产承载的语言，登记时不允许 Unknown 或未定义枚举值。</summary>
        public SystemLanguage language;
        /// <summary>语言选择显示名；管理器汇总时为空则使用语言枚举名。</summary>
        public string displayName;
        /// <summary>本语言的有序条目列表；查询会校验空项和重复 ID。</summary>
        public List<LanguageInfo> languagesInfo = new();
        private readonly LocalizationEntryIndex _index = new();

        /// <summary>借读本语言指定 ID 的条目，缺失时返回空引用，不执行语言回退。</summary>
        public LanguageInfo GetLanguageInfo(int id) => _index.Find(languagesInfo, id, name + "/" + language);
        /// <summary>清除条目查询缓存，不修改翻译内容或列表。</summary>
        public void ClearCache() => _index.Clear();
        /// <summary>编辑器校验资产时使条目索引失效。</summary>
        private void OnValidate() => ClearCache();
        /// <summary>资产停用时丢弃索引持有的条目快照。</summary>
        private void OnDisable() => ClearCache();
    }
}

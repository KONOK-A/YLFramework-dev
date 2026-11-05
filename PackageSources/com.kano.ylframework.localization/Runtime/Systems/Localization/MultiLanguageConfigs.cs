using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Localization
{
    /// <summary>保存有序语言资产引用的本地化根；显式登记后参与查询，不主动加载或登记自身。</summary>
    [CreateAssetMenu(menuName = "YLFramework/Localization/Multi Language Configs")]
    public class MultiLanguageConfigs : ScriptableObject
    {
        /// <summary>供作者识别的根显示名称，不是管理器查询所用的资产对象名。</summary>
        public string displayName = string.Empty;
        [SerializeField] private List<LanguageConfig> languages = new();
        /// <summary>可由作者直接编辑的语言引用列表，其顺序参与登记后的显式回退顺序。</summary>
        public List<LanguageConfig> Languages => languages;

        // 列表仍可由作者修改；不持有按数量失效的语言字典，也不在getter扫描/保存资产。
        /// <summary>校验完整语言列表后按语言精确查找，不执行语言回退，缺失时返回空引用。</summary>
        /// <param name="language">需要查找的语言枚举值。</param>
        public LanguageConfig GetLanguageConfigData(SystemLanguage language)
        {
            if (languages == null)
            {
                throw new InvalidOperationException($"Localization '{name}': language list is null.");
            }

            LanguageConfig result = null;
            var seen = new HashSet<SystemLanguage>();
            for (var i = 0; i < languages.Count; i++)
            {
                var item = languages[i];
                if (!item)
                {
                    throw new InvalidOperationException($"Localization '{name}': missing language asset at index {i}.");
                }

                if (!seen.Add(item.language))
                {
                    throw new InvalidOperationException($"Localization '{name}': duplicate language {item.language} at index {i}.");
                }

                if (item.language == language)
                {
                    result = item;
                }
            }
            return result;
        }

        /// <summary>清除所有存活语言资产的条目索引，不改写语言引用或条目内容。</summary>
        public void ClearCache()
        {
            if (languages == null)
            {
                return;
            }

            foreach (var item in languages)
            {
                if (item)
                {
                    item.ClearCache();
                }
            }
        }
    }
}

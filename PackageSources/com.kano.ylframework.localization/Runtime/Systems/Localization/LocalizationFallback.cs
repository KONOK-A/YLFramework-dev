using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Localization
{
    /// <summary>语言选择与条目查询共享的有限回退顺序，不依赖Dictionary的枚举顺序。</summary>
    public static class LocalizationFallback
    {
        /// <summary>依候选语言顺序查询，返回首个非空结果；所有候选均缺失时返回空引用。</summary>
        /// <typeparam name="T">由查询委托返回的引用类型。</typeparam>
        /// <param name="requested">本次希望优先使用的语言。</param>
        /// <param name="registered">按显式顺序提供的补充候选语言，可为空引用。</param>
        /// <param name="lookup">按语言查询结果的委托，以 null 表示继续回退。</param>
        public static T Find<T>(SystemLanguage requested, IEnumerable<SystemLanguage> registered, Func<SystemLanguage, T> lookup) where T : class
        {
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }

            foreach (var candidate in GetCandidates(requested, registered))
            {
                var value = lookup(candidate);
                if (value != null)
                {
                    return value;
                }
            }
            return null;
        }

        /// <summary>生成请求语言、适用的中文变体、英语及显式语言序列，去重并跳过 Unknown。</summary>
        /// <param name="requested">排在首位的请求语言；Unknown 不加入结果。</param>
        /// <param name="registered">在内置候选之后追加的语言，保留传入枚举顺序。</param>
        /// <returns>有序候选快照，可能包含未实际登记的请求语言或英语。</returns>
        public static IReadOnlyList<SystemLanguage> GetCandidates(SystemLanguage requested, IEnumerable<SystemLanguage> registered)
        {
            var result = new List<SystemLanguage>();
            Add(requested);
            if (requested == SystemLanguage.ChineseSimplified)
            {
                Add(SystemLanguage.Chinese);
                Add(SystemLanguage.ChineseTraditional);
            }
            else if (requested == SystemLanguage.ChineseTraditional)
            {
                Add(SystemLanguage.Chinese);
                Add(SystemLanguage.ChineseSimplified);
            }
            else if (requested == SystemLanguage.Chinese)
            {
                Add(SystemLanguage.ChineseSimplified);
                Add(SystemLanguage.ChineseTraditional);
            }
            Add(SystemLanguage.English);
            if (registered != null)
            {
                foreach (var language in registered)
                {
                    Add(language);
                }
            }

            return result;

            // 只追加非 Unknown 且尚未出现的候选，保留前面已确定的优先级。
            void Add(SystemLanguage language)
            {
                if (language != SystemLanguage.Unknown && !result.Contains(language))
                {
                    result.Add(language);
                }
            }
        }
    }
}

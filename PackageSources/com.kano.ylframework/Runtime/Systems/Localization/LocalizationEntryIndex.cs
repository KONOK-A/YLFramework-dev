using System;
using System.Collections.Generic;

namespace YLFramework.Localization
{
    // 比较真实引用和键而不是数量/hash；列表同长度替换、原条目改ID均使缓存失效。
    /// <summary>为单语言条目维护 ID 查询索引，只有完整校验成功后才将快照标记为有效。</summary>
    internal sealed class LocalizationEntryIndex
    {
        private readonly Dictionary<int, LanguageInfo> _index = new();
        private LanguageInfo[] _entries = Array.Empty<LanguageInfo>();
        private int[] _ids = Array.Empty<int>();
        private bool _isValid;

        /// <summary>核对快照并按需重建后借读指定 ID 的条目，不存在时返回空引用。</summary>
        /// <param name="source">当前语言的可变条目列表。</param>
        /// <param name="id">要查询的条目 ID。</param>
        /// <param name="context">校验失败时用于定位配置和语言的上下文。</param>
        public LanguageInfo Find(IList<LanguageInfo> source, int id, string context)
        {
            if (!Matches(source))
            {
                Rebuild(source, context);
            }

            return _index.TryGetValue(id, out var value) ? value : null;
        }

        /// <summary>使索引失效并清除引用及 ID 快照，不修改源条目列表。</summary>
        public void Clear()
        {
            _isValid = false;
            _index.Clear();
            _entries = Array.Empty<LanguageInfo>();
            _ids = Array.Empty<int>();
        }

        /// <summary>逐槽核对列表长度、条目引用与 ID，判断现有索引能否继续使用。</summary>
        private bool Matches(IList<LanguageInfo> source)
        {
            if (!_isValid || source == null || _entries.Length != source.Count)
            {
                return false;
            }

            for (var i = 0; i < _entries.Length; i++)
            {
                if (!ReferenceEquals(_entries[i], source[i]) || source[i] == null || _ids[i] != source[i].id)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>拒绝列表空引用、空条目和重复 ID，完成全部校验后才启用新快照。</summary>
        private void Rebuild(IList<LanguageInfo> source, string context)
        {
            Clear();
            if (source == null)
            {
                throw new InvalidOperationException($"Localization '{context}': entry list is null.");
            }

            var next = new LanguageInfo[source.Count];
            var keys = new int[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                var entry = source[i];
                if (entry == null)
                {
                    throw new InvalidOperationException($"Localization '{context}': null entry at index {i}.");
                }

                if (_index.ContainsKey(entry.id))
                {
                    throw new InvalidOperationException($"Localization '{context}': duplicate ID {entry.id} at index {i}.");
                }

                _index.Add(entry.id, entry);
                next[i] = entry;
                keys[i] = entry.id;
            }
            _entries = next;
            _ids = keys;
            _isValid = true;
        }
    }
}

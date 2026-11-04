using System;
using System.Collections.Generic;

namespace YLFramework.DataAssets
{
    // 保留原配表的ID查询/范围算法；先完整校验，再发布不可变索引。
    /// <summary>将有效 ID 映射到原列表槽位，同时记录范围并拒绝重复 ID。</summary>
    internal sealed class DataAssetIdIndex
    {
        private readonly Dictionary<int, int> _rows = new Dictionary<int, int>();
        private readonly int _min;
        private readonly int _max;

        /// <summary>从允许空槽的 ID 快照建立索引，错误信息使用所属根名称定位冲突。</summary>
        internal DataAssetIdIndex(IReadOnlyList<int?> ids, string owner)
        {
            if (ids == null)
            {
                throw new ArgumentNullException(nameof(ids));
            }

            var first = true;
            for (var i = 0; i < ids.Count; i++)
            {
                if (!ids[i].HasValue)
                {
                    continue;
                }

                var id = ids[i].Value;
                if (_rows.TryGetValue(id, out var previous))
                {
                    throw new InvalidOperationException($"DataAssets '{owner}': duplicate ID {id} at rows {previous} and {i}.");
                }

                _rows.Add(id, i);
                if (first || id < _min)
                {
                    _min = id;
                }

                if (first || id > _max)
                {
                    _max = id;
                }

                first = false;
            }
        }

        /// <summary>查找业务 ID 对应的原始列表槽位。</summary>
        internal bool TryGetRow(int id, out int row) => _rows.TryGetValue(id, out row);

        /// <summary>返回有效 ID 范围；没有有效条目时返回假且两个端点均为 0。</summary>
        internal bool TryGetRange(out int minimum, out int maximum)
        {
            minimum = _min;
            maximum = _max;
            return _rows.Count != 0;
        }

        /// <summary>优先取最大 ID 后的正数；到达整数上限时从 1 查找未占用的正 ID。</summary>
        internal int NextId()
        {
            if (_max < int.MaxValue)
            {
                return Math.Max(1, _max + 1);
            }

            for (var id = 1; id < int.MaxValue; id++)
            {
                if (!_rows.ContainsKey(id))
                {
                    return id;
                }
            }

            throw new InvalidOperationException("No positive DataAssets ID is available.");
        }
    }
}

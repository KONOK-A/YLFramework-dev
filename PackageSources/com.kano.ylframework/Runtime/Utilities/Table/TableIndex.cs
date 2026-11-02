using System;
using System.Collections.Generic;
using YLFramework.Pooling;

namespace YLFramework.Tables
{
    /// <summary>
    /// 为数据项建立按指定键查询的索引。
    /// 同一键可以关联多个数据项（multi-set：允许重复项，并保持添加顺序）。
    /// <para>
    /// 键由构造时传入的选择器在 Add/Remove 时实时计算，不单独保存每个数据项添加时的键；
    /// 因此修改数据项的键属性后，Remove 无法定位旧索引项，会留下陈旧数据。
    /// 契约：索引期间用于生成键的值必须保持稳定；需要变更时先按原状态 Remove，再修改并 Add。
    /// </para>
    /// <para>
    /// 内部列表来自 <see cref="ListPool{T}"/>，字典来自 <see cref="DictionaryPool{TKey,TValue}"/>，
    /// Clear/Dispose 会正确归还；null 键不受支持（抛 <see cref="ArgumentNullException"/>）。
    /// 数据项仅以引用或值保存在列表中，清空与释放不会调用数据项的 Dispose。
    /// </para>
    /// <para>
    /// 本类型非线程安全，且不提供嵌套操作保护：键选择器是用户委托，若选择器内 Dispose 索引，
    /// 返回后会 fail-fast 抛 <see cref="ObjectDisposedException"/>；若选择器内 Add/Remove/Clear，则先按调用顺序完成。
    /// </para>
    /// </summary>
    /// <typeparam name="TKey">索引键类型。</typeparam>
    /// <typeparam name="TDataItem">数据项类型。</typeparam>
    public class TableIndex<TKey, TDataItem> : IDisposable
    {
        private Dictionary<TKey, List<TDataItem>> _index;

        private readonly Func<TDataItem, TKey> _getKeyByDataItem;

        private bool _isDisposed;

        /// <summary>
        /// 创建索引。
        /// </summary>
        /// <param name="keyGetterByDataItem">从数据项计算索引键的委托；不能为 null。</param>
        /// <exception cref="ArgumentNullException"><paramref name="keyGetterByDataItem"/> 为 null。</exception>
        public TableIndex(Func<TDataItem, TKey> keyGetterByDataItem)
        {
            _getKeyByDataItem = keyGetterByDataItem ??
                                throw new ArgumentNullException(nameof(keyGetterByDataItem));
            _index = DictionaryPool<TKey, List<TDataItem>>.Allocate();
        }

        /// <summary>
        /// 获取内部字典视图（键 → 数据项列表）。
        /// 返回内部字典本身（无拷贝），仅供遍历查询；修改结构或列表内容会绕过 Pool 回收并破坏索引不变量。
        /// 旧引用生命周期：Clear 后同一字典仍有效但内容已清空；Dispose 后字典已归还 <see cref="DictionaryPool{TKey,TValue}"/>，旧引用失效。
        /// </summary>
        /// <exception cref="ObjectDisposedException">索引已释放。</exception>
        public IDictionary<TKey, List<TDataItem>> Dictionary
        {
            get
            {
                ThrowIfDisposed();
                return _index;
            }
        }

        /// <summary>
        /// 添加数据项：按键追加到对应列表；键不存在时从 <see cref="ListPool{T}"/> 租用新列表。
        /// 允许同一个键下存在重复数据项。
        /// </summary>
        /// <param name="dataItem">要添加的数据项。</param>
        /// <exception cref="ObjectDisposedException">索引已释放（含键选择器内释放的情况）。</exception>
        /// <exception cref="ArgumentNullException">键选择器返回了 null 键。</exception>
        public void Add(TDataItem dataItem)
        {
            ThrowIfDisposed();

            var key = _getKeyByDataItem(dataItem);

            // keySelector 是用户委托，可能在执行期间 Dispose 索引；返回后必须重新校验，避免继续访问已归还的字典。
            ThrowIfDisposed();
            ThrowIfKeyNull(key);

            if (_index.TryGetValue(key, out var list))
            {
                list.Add(dataItem);
                return;
            }

            list = ListPool<TDataItem>.Allocate();
            try
            {
                list.Add(dataItem);
                _index.Add(key, list);
            }
            catch
            {
                list.RecycleToPool();
                throw;
            }
        }

        /// <summary>
        /// 移除数据项：只移除该键下第一个匹配项（与 List.Remove 一致）。
        /// 键或数据项不存在时静默返回 false、不抛异常；
        /// 移除后列表为空会立即删除该键并把列表归还 <see cref="ListPool{T}"/>。
        /// </summary>
        /// <param name="dataItem">要移除的数据项。</param>
        /// <returns>实际移除成功返回 true；键或项不存在返回 false。</returns>
        /// <exception cref="ObjectDisposedException">索引已释放（含键选择器内释放的情况）。</exception>
        /// <exception cref="ArgumentNullException">键选择器返回了 null 键。</exception>
        public bool Remove(TDataItem dataItem)
        {
            ThrowIfDisposed();

            var key = _getKeyByDataItem(dataItem);

            // 键选择器执行期间索引可能已被 Dispose，返回后重新校验。
            ThrowIfDisposed();
            ThrowIfKeyNull(key);

            if (!_index.TryGetValue(key, out var list))
            {
                return false;
            }

            if (!list.Remove(dataItem))
            {
                return false;
            }

            if (list.Count == 0)
            {
                // 空列表不再保留：及时移除空 key，并把列表归还 ListPool，避免空 key 残留与池泄漏。
                _index.Remove(key);
                list.RecycleToPool();
            }

            return true;
        }

        /// <summary>
        /// 按键查询数据项；键不存在时返回空集合（不会返回 null）。
        /// 命中时返回内部实时列表（借用引用，不做拷贝）：其生命周期不超过索引的下一次结构修改、Clear 或 Dispose；
        /// 请勿修改该集合或长期缓存，否则会绕过 Pool ownership 并破坏索引不变量。
        /// </summary>
        /// <param name="key">查询键；不能为 null。</param>
        /// <returns>该键下的数据项集合；键不存在时为空集合。</returns>
        /// <exception cref="ObjectDisposedException">索引已释放。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 null。</exception>
        public IEnumerable<TDataItem> Get(TKey key)
        {
            ThrowIfDisposed();
            ThrowIfKeyNull(key);

            return _index.TryGetValue(key, out var retList) ? retList : Array.Empty<TDataItem>();
        }

        /// <summary>
        /// 清空所有索引数据：先把全部内部列表归还 <see cref="ListPool{T}"/>，再清空字典。
        /// 清空后索引可以继续使用；可重复调用，不会重复回收。
        /// </summary>
        /// <exception cref="ObjectDisposedException">索引已释放。</exception>
        public void Clear()
        {
            ThrowIfDisposed();

            foreach (var value in _index.Values)
            {
                value.RecycleToPool();
            }

            _index.Clear();
        }

        /// <summary>
        /// 释放索引：归还全部内部列表与字典，并置为不可用状态。
        /// 幂等，可重复调用；释放后 Add/Remove/Get/Clear/Dictionary 都会抛 <see cref="ObjectDisposedException"/>。
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            foreach (var value in _index.Values)
            {
                value.RecycleToPool();
            }

            // DictionaryPool.Recycle 会先清空字典再入池。
            _index.RecycleToPool();
            _index = null;
        }

        /// <summary>拒绝访问已归还内部字典的索引，防止继续使用池中可能被他人借走的容器。</summary>
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(TableIndex<TKey, TDataItem>));
            }
        }

        /// <summary>在访问字典前拒绝 null 键，为键选择器或查询参数提供统一错误上下文。</summary>
        private static void ThrowIfKeyNull(TKey key)
        {
            // Dictionary 对 null 键会抛 ArgumentNullException，这里提前给出更明确的上下文；
            // 对值类型键该判断恒为 false，不影响正常路径。
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key), "TableIndex 不支持 null 键，请保证键选择器返回非空键。");
            }
        }
    }
}

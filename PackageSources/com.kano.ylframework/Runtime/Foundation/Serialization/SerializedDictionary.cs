using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Serialization
{

#if UNITY_2020_1_OR_NEWER

    /// <summary>承载 Unity 序列化列表中的一行键值；成员名称属于既有序列化布局。</summary>
    [Serializable]
    internal class DictionaryItem<TKey, TValue>
    {
        /// <summary>原始行的键；无效键仍保留在序列化数据中。</summary>
        public TKey Key;
        /// <summary>原始行的值，是否参与查询由该行的键决定。</summary>
        public TValue Value;
    }

    /// <summary>
    /// 以 datas 条目列表为事实源的可序列化字典；运行时使用默认键比较器，拒绝 CLR null 键。
    /// Unity 原始无效行会保留，仅首个有效键参与查询、视图和枚举；不保证线程安全。
    /// </summary>
    /// <typeparam name="TKey">使用默认相等比较器的键类型；存入 Unity 资产时须满足其序列化支持范围。</typeparam>
    /// <typeparam name="TValue">值类型；存入 Unity 资产时须满足其序列化支持范围。</typeparam>
    /// <remarks>
    /// Unity 使用 datas、Key、Value 字段布局；运行时修改直接写入原始行，不依靠序列化前回写。
    /// 接收键的 API 对 CLR null 抛出 ArgumentNullException；Unity 已销毁对象的托管引用不按 CLR null 排除。
    /// </remarks>
    [Serializable]
    public class SerializedDictionary<TKey, TValue> : IDictionary<TKey, TValue>,
        IReadOnlyDictionary<TKey, TValue>, ISerializationCallbackReceiver
    {
        [SerializeField]
        private List<DictionaryItem<TKey, TValue>> datas;

        [NonSerialized] private Dictionary<TKey, int> _index;
        [NonSerialized] private int _invalidEntryCount;
        [NonSerialized] private int _version;
        [NonSerialized] private DictionaryView<TKey> _keys;
        [NonSerialized] private DictionaryView<TValue> _values;

        /// <summary>创建没有原始行的空字典，首次查询或写入时再准备索引。</summary>
        public SerializedDictionary()
        {
        }

        /// <summary>复制输入序列的键值对，不借用外部集合；null 来源或重复键会抛出异常。</summary>
        /// <param name="source">要逐项复制的键值对序列，不允许 CLR null 键。</param>
        /// <exception cref="ArgumentNullException">source 或其中某个键为 CLR null。</exception>
        /// <exception cref="ArgumentException">source 包含按默认比较器相等的重复键。</exception>
        public SerializedDictionary(IEnumerable<KeyValuePair<TKey, TValue>> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            foreach (var pair in source)
            {
                Add(pair.Key, pair.Value);
            }
        }

        /// <summary>有效且唯一的键数量，不包含保留在原始数据中的无效行。</summary>
        public int Count => Index.Count;

        /// <summary>原始序列化行中被忽略的 null 行、null 键及重复键数量；读取不会删除行。</summary>
        public int InvalidEntryCount
        {
            get
            {
                EnsureIndex();
                return _invalidEntryCount;
            }
        }

        /// <summary>有效键的只读实时视图，顺序与首个有效序列化行一致。</summary>
        public ICollection<TKey> Keys => _keys ?? (_keys = new DictionaryView<TKey>(this, pair => pair.Key));
        /// <summary>有效行中值的只读实时视图，与 Keys 顺序对应，不包含隐藏重复行的值。</summary>
        public ICollection<TValue> Values => _values ?? (_values = new DictionaryView<TValue>(this, pair => pair.Value));
        /// <inheritdoc />
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        /// <inheritdoc />
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;
        /// <summary>字典支持写入；Keys 与 Values 视图仍为只读。</summary>
        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

        /// <summary>读取缺失键抛出 KeyNotFoundException；写入更新首个有效行或追加新行。</summary>
        /// <param name="key">要读取或写入的键，不能为 CLR null。</param>
        /// <exception cref="KeyNotFoundException">读取时没有对应的有效键。</exception>
        public TValue this[TKey key]
        {
            get
            {
                if (TryGetValue(key, out var value))
                {
                    return value;
                }

                throw new KeyNotFoundException();
            }
            set
            {
                ValidateKey(key);
                if (Index.TryGetValue(key, out var row))
                {
                    datas[row].Value = value;
                    _version++;
                }
                else
                {
                    Add(key, value);
                }
            }
        }

        /// <summary>向原始行列表追加键值对；键已存在时抛出 ArgumentException，不覆盖已有值。</summary>
        /// <param name="key">新增键，不能为 CLR null。</param>
        /// <param name="value">与键关联的值。</param>
        public void Add(TKey key, TValue value)
        {
            if (!TryAdd(key, value))
            {
                throw new ArgumentException("已存在相同的键。", nameof(key));
            }
        }

        /// <summary>合法新键写穿序列化列表；重复键返回 false，null 键抛出异常。</summary>
        public bool TryAdd(TKey key, TValue value)
        {
            ValidateKey(key);
            var current = Index;
            if (current.ContainsKey(key))
            {
                return false;
            }

            if (datas == null)
            {
                datas = new List<DictionaryItem<TKey, TValue>>();
            }

            current.Add(key, datas.Count);
            datas.Add(new DictionaryItem<TKey, TValue> { Key = key, Value = value });
            _version++;
            return true;
        }

        /// <summary>按默认键比较器查询有效投影中是否存在该键。</summary>
        public bool ContainsKey(TKey key)
        {
            ValidateKey(key);
            return Index.ContainsKey(key);
        }

        /// <summary>读取首个有效行的值；键缺失时返回 false，并将 value 设为 default。</summary>
        /// <param name="key">待查询的键，不能为 CLR null。</param>
        /// <param name="value">找到的值；可能本身就是 default，是否存在须以返回值判断。</param>
        public bool TryGetValue(TKey key, out TValue value)
        {
            ValidateKey(key);
            if (Index.TryGetValue(key, out var row))
            {
                value = datas[row].Value;
                return true;
            }
            value = default;
            return false;
        }

        /// <summary>以默认值比较器搜索有效行，不搜索隐藏重复行中的值。</summary>
        public bool ContainsValue(TValue value)
        {
            foreach (var pair in this)
            {
                if (EqualityComparer<TValue>.Default.Equals(pair.Value, value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>删除该键的全部原始行，防止隐藏的重复值重新成为有效值。</summary>
        /// <returns>存在有效键并完成删除时为 true；键缺失时为 false，不修改原始行。</returns>
        public bool Remove(TKey key)
        {
            ValidateKey(key);
            if (!Index.ContainsKey(key))
            {
                return false;
            }

            datas.RemoveAll(item => item != null && !(item.Key is null) &&
                EqualityComparer<TKey>.Default.Equals(item.Key, key));
            Invalidate();
            return true;
        }

        /// <summary>清空全部原始行，包括不参与运行时投影的无效行。</summary>
        public void Clear()
        {
            if (datas == null || datas.Count == 0)
            {
                return;
            }

            datas.Clear();
            Invalidate();
        }

        /// <summary>添加键值对，沿用按键新增的 null 键与重复键检查。</summary>
        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

        /// <summary>检查有效键及其当前值是否同时匹配；值使用默认相等比较器。</summary>
        public bool Contains(KeyValuePair<TKey, TValue> item) =>
            TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

        /// <summary>仅在有效键值对匹配时删除该键的全部原始行；不匹配时返回 false。</summary>
        public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);

        /// <summary>按有效序列化行的顺序复制键值对，不包含被忽略的原始行。</summary>
        /// <param name="array">接收键值对的目标数组。</param>
        /// <param name="arrayIndex">从 0 开始的写入位置。</param>
        /// <exception cref="ArgumentNullException">array 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException">arrayIndex 小于 0 或大于数组长度。</exception>
        /// <exception cref="ArgumentException">目标数组的剩余空间不足。</exception>
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ValidateCopyTo(array, arrayIndex, Count);
            foreach (var pair in this)
            {
                array[arrayIndex++] = pair;
            }
        }

        /// <summary>按有效原始行的顺序枚举；字典写入或反序列化后继续迭代会抛出 InvalidOperationException。</summary>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() =>
            new DictionaryEnumerator<KeyValuePair<TKey, TValue>>(this, pair => pair);

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>序列化前不做回写，原始行已随运行时操作同步更新。</summary>
        /// <remarks>序列化回调可能在工作线程执行：两处回调均不遍历键、不查询 Unity 对象、不重写原始行。</remarks>
        public void OnBeforeSerialize()
        {
        }
        /// <summary>反序列化后只使缓存和旧枚举器失效，下一次数据查询再重建索引。</summary>
        public void OnAfterDeserialize() => Invalidate();

        private Dictionary<TKey, int> Index
        {
            get
            {
                EnsureIndex();
                return _index;
            }
        }

        /// <summary>按原始行顺序建立首个有效键到行号的索引，同时统计被忽略的行。</summary>
        private void EnsureIndex()
        {
            if (_index != null)
            {
                return;
            }

            var rebuilt = new Dictionary<TKey, int>(datas?.Count ?? 0);
            var invalid = 0;
            if (datas != null)
            {
                for (var row = 0; row < datas.Count; row++)
                {
                    var item = datas[row];
                    if (item == null || item.Key is null || rebuilt.ContainsKey(item.Key))
                    {
                        invalid++;
                    }
                    else
                    {
                        rebuilt.Add(item.Key, row);
                    }
                }
            }
            // 比较器抛错时，不发布半建缓存。
            _invalidEntryCount = invalid;
            _index = rebuilt;
        }

        /// <summary>丢弃派生缓存并推进版本，使后续查询重建索引、旧枚举器拒绝继续。</summary>
        private void Invalidate()
        {
            _index = null;
            _invalidEntryCount = 0;
            _version++;
        }

        /// <summary>在枚举继续或重置前检查版本，拒绝使用修改或反序列化前的游标。</summary>
        private void CheckVersion(int expectedVersion)
        {
            if (_version != expectedVersion)
            {
                throw new InvalidOperationException("枚举期间字典已修改或反序列化。");
            }
        }

        /// <summary>只排除 CLR null 键，保留 Unity fake-null 托管对象作为键的既有语义。</summary>
        private static void ValidateKey(TKey key)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }
        }

        /// <summary>在复制前一次校验目标、起始位置和容量，避免写入一部分后才发现空间不足。</summary>
        private static void ValidateCopyTo<T>(T[] array, int arrayIndex, int count)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (arrayIndex < 0 || arrayIndex > array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }

            if (array.Length - arrayIndex < count)
            {
                throw new ArgumentException("目标数组空间不足。", nameof(array));
            }
        }

        /// <summary>从字典有效行投影键或值的只读实时视图，不单独持有数据快照。</summary>
        private sealed class DictionaryView<T> : ICollection<T>, IReadOnlyCollection<T>
        {
            private readonly SerializedDictionary<TKey, TValue> _owner;
            private readonly Func<KeyValuePair<TKey, TValue>, T> _select;

            /// <summary>绑定所属字典与投影委托，后续查询始终读取所属字典的当前有效行。</summary>
            public DictionaryView(SerializedDictionary<TKey, TValue> owner, Func<KeyValuePair<TKey, TValue>, T> select)
            {
                this._owner = owner;
                this._select = select;
            }

            /// <summary>所属字典当前的有效条目数。</summary>
            public int Count => _owner.Count;
            /// <summary>始终为 true，视图不允许直接修改所属字典。</summary>
            public bool IsReadOnly => true;
            /// <summary>拒绝通过视图新增元素，始终抛出 NotSupportedException。</summary>
            public void Add(T item) => throw new NotSupportedException();
            /// <summary>拒绝通过视图清空数据，始终抛出 NotSupportedException。</summary>
            public void Clear() => throw new NotSupportedException();
            /// <summary>拒绝通过视图移除元素，始终抛出 NotSupportedException。</summary>
            public bool Remove(T item) => throw new NotSupportedException();

            /// <summary>按投影结果的默认相等比较器查找元素。</summary>
            public bool Contains(T item)
            {
                foreach (var value in this)
                {
                    if (EqualityComparer<T>.Default.Equals(value, item))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>按有效行顺序复制投影值；目标为 null、位置越界或容量不足时抛出对应参数异常。</summary>
            public void CopyTo(T[] array, int arrayIndex)
            {
                ValidateCopyTo(array, arrayIndex, Count);
                foreach (var item in this)
                {
                    array[arrayIndex++] = item;
                }
            }

            /// <summary>创建共享所属字典版本检查的投影枚举器，不复制元素。</summary>
            public IEnumerator<T> GetEnumerator() => new DictionaryEnumerator<T>(_owner, _select);
            /// <inheritdoc />
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>按原始行游标跳过无效与重复行，并检测所属字典的版本变化。</summary>
        private sealed class DictionaryEnumerator<T> : IEnumerator<T>
        {
            private readonly SerializedDictionary<TKey, TValue> _owner;
            private readonly Func<KeyValuePair<TKey, TValue>, T> _select;
            private readonly int _expectedVersion;
            private int _row = -1;
            private bool _hasCurrent;

            /// <summary>确保有效行索引已准备，并捕获字典版本供后续迭代检查。</summary>
            public DictionaryEnumerator(SerializedDictionary<TKey, TValue> owner, Func<KeyValuePair<TKey, TValue>, T> select)
            {
                owner.EnsureIndex();
                this._owner = owner;
                this._select = select;
                _expectedVersion = owner._version;
            }

            /// <summary>当前投影值；开始前或结束后为 default，读取本身不校验版本。</summary>
            public T Current
            {
                get; private set;
            }
            /// <summary>非泛型当前值；未定位到有效条目时抛出 InvalidOperationException。</summary>
            object IEnumerator.Current
            {
                get
                {
                    if (!_hasCurrent)
                    {
                        throw new InvalidOperationException("枚举器不在有效条目上。");
                    }

                    return Current;
                }
            }

            /// <summary>推进到下一有效行；结束时返回 false，版本已变化时抛出 InvalidOperationException。</summary>
            public bool MoveNext()
            {
                // 即使已枚举结束，也必须检查后续修改；yield 的结束状态会跳过此检查。
                _owner.CheckVersion(_expectedVersion);
                while (_row + 1 < (_owner.datas?.Count ?? 0))
                {
                    var item = _owner.datas[++_row];
                    if (item == null || item.Key is null || !_owner._index.TryGetValue(item.Key, out var first) || first != _row)
                    {
                        continue;
                    }

                    Current = _select(new KeyValuePair<TKey, TValue>(item.Key, item.Value));
                    _hasCurrent = true;
                    return true;
                }
                Current = default;
                _hasCurrent = false;
                return false;
            }

            /// <summary>将游标恢复到首项之前；字典版本已变化时拒绝重置并抛出 InvalidOperationException。</summary>
            public void Reset()
            {
                _owner.CheckVersion(_expectedVersion);
                _row = -1;
                Current = default;
                _hasCurrent = false;
            }

            /// <summary>不持有需释放的外部资源；释放不改变字典或游标。</summary>
            public void Dispose()
            {
            }
        }
    }
#endif
}

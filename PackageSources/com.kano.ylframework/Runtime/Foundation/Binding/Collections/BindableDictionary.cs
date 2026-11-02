using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using YLFramework.Events;

namespace YLFramework.Binding
{
    /// <summary>
    /// 可监听变化的字典，包装内部 <see cref="Dictionary{TKey,TValue}"/>：
    /// 索引器赋值、Add、Remove、Clear 等操作会触发对应事件，并支持批量合并通知（语义与 <see cref="BindableList{T}"/> 一致）。
    /// 通过构造传入的字典会被直接使用（不复制），绕过本类修改它不会触发任何事件。
    /// 同时实现泛型与非泛型 IDictionary 接口，非泛型入口会做类型转换。
    /// 非线程安全；通知同步执行，回调异常中断后续通知，但不回滚已完成的数据修改。
    /// 批量作用域只合并通知，不提供集合快照、事务回滚或重入隔离。
    /// </summary>
    /// <typeparam name="TKey">键类型。</typeparam>
    /// <typeparam name="TValue">值类型。</typeparam>
    [Serializable]
    public class BindableDictionary<TKey, TValue> : IDictionary<TKey, TValue>,
        IReadOnlyDictionary<TKey, TValue>, IDictionary, ISerializable, IDeserializationCallback
    {
        private readonly Dictionary<TKey, TValue> _inner;

        /// <summary>
        /// 创建空字典（使用默认键相等比较器）。
        /// </summary>
        public BindableDictionary() => _inner = new Dictionary<TKey, TValue>();

        /// <summary>
        /// 创建空字典并指定键比较器。
        /// </summary>
        /// <param name="comparer">键相等比较器。</param>
        public BindableDictionary(IEqualityComparer<TKey> comparer) => _inner = new Dictionary<TKey, TValue>(comparer);

        /// <summary>
        /// 直接使用已有字典作为内部存储（不复制）；之后绕过本类修改该字典不会触发事件。
        /// </summary>
        /// <param name="innerDictionary">内部字典，调用方必须保证非 null；本构造不做校验。</param>
        /// <remarks>传入 null 不会创建空字典，后续访问内部存储时会抛出 NullReferenceException。</remarks>
        public BindableDictionary(Dictionary<TKey, TValue> innerDictionary) => _inner = innerDictionary;

        #region 监听事件

        [NonSerialized] private EasyEvent<int> _onCountChanged;
        /// <summary>
        /// 数量变化事件，参数为变化后的数量
        /// </summary>
        public EasyEvent<int> OnCountChanged => _onCountChanged ??= new EasyEvent<int>();

        [NonSerialized] private EasyEvent _onClear;
        /// <summary>
        /// 非批量模式下清空完成后的无参通知；空字典再次清空也会发送。
        /// </summary>
        public EasyEvent OnClear => _onClear ??= new EasyEvent();

        [NonSerialized] private EasyEvent<TKey, TValue> _onAdd;
        /// <summary>
        /// 添加事件，参数依次为 Key、Value。
        /// </summary>
        public EasyEvent<TKey, TValue> OnAdd => _onAdd ??= new EasyEvent<TKey, TValue>();

        [NonSerialized] private EasyEvent<TKey, TValue> _onRemove;
        /// <summary>
        /// 移除事件，参数依次为 Key、被移除的 Value。
        /// </summary>
        public EasyEvent<TKey, TValue> OnRemove => _onRemove ??= new EasyEvent<TKey, TValue>();

        [NonSerialized] private EasyEvent<TKey, TValue, TValue> _onReplace;
        /// <summary>
        /// 三个参数依次为 Key、OldValue、NewValue
        /// </summary>
        public EasyEvent<TKey, TValue, TValue> OnReplace => _onReplace ??= new EasyEvent<TKey, TValue, TValue>();

        [NonSerialized] private EasyEvent<BindableDictionaryChange<TKey, TValue>> _onChanged;
        /// <summary>
        /// 统一变化事件：Add/Remove/Replace/Clear 都会触发；批量通知结束时以 Reset 触发一次
        /// </summary>
        public EasyEvent<BindableDictionaryChange<TKey, TValue>> OnChanged =>
            _onChanged ??= new EasyEvent<BindableDictionaryChange<TKey, TValue>>();

        #endregion

        #region 批量通知

        [NonSerialized] private int _batchDepth;
        [NonSerialized] private int _batchStartCount;
        [NonSerialized] private bool _isBatchDirty;

        /// <summary>
        /// 是否处于批量通知状态，期间不会触发任何事件
        /// </summary>
        public bool IsNotificationSuspended => _batchDepth > 0;

        /// <summary>
        /// 开始批量修改。期间所有事件被抑制，EndBatch 时最多触发一次 OnCountChanged 和一次 OnChanged(Reset)
        /// </summary>
        public void BeginBatch()
        {
            if (_batchDepth == 0)
            {
                _batchStartCount = Count;
                _isBatchDirty = false;
            }
            _batchDepth++;
        }

        /// <summary>
        /// 结束一层批量修改：仍在嵌套中时只减少层级；到达最外层且期间发生过变化时，
        /// 数量变化则补发一次 OnCountChanged，随后补发一次 OnChanged(Reset)。
        /// </summary>
        /// <exception cref="InvalidOperationException">调用次数多于 BeginBatch（当前不在批量状态）。</exception>
        public void EndBatch()
        {
            if (_batchDepth == 0)
            {
                throw new InvalidOperationException("EndBatch 调用次数多于 BeginBatch。");
            }

            _batchDepth--;
            if (_batchDepth > 0 || !_isBatchDirty)
            {
                return;
            }

            _isBatchDirty = false;
            if (Count != _batchStartCount)
            {
                _onCountChanged?.Trigger(Count);
            }
            _onChanged?.Trigger(BindableDictionaryChange<TKey, TValue>.Reset());
        }

        /// <summary>
        /// 配合 using 使用的批量作用域：即使中途抛异常，退出作用域时也会结束批量并补发合并通知
        /// </summary>
        /// <returns>释放时结束本层批量通知的句柄；不撤销作用域内已完成的数据修改。</returns>
        public IDisposable BeginBatchScope()
        {
            BeginBatch();
            return new BatchScope(EndBatch);
        }

        #endregion

        #region 字典操作

        /// <summary>
        /// 按键读写值：写入时 key 不存在为新增（OnAdd + OnCountChanged），已存在为替换（OnReplace，数量不变）；
        /// 读取时 key 不存在抛出 KeyNotFoundException（与 Dictionary 一致）。
        /// </summary>
        /// <param name="key">键。</param>
        /// <exception cref="KeyNotFoundException">读取时 key 不存在。</exception>
        /// <exception cref="ArgumentNullException">key 为 null。</exception>
        /// <remarks>替换不比较新旧值，即使值相等也发送替换通知；批量期间按合并规则通知。</remarks>
        public TValue this[TKey key]
        {
            get => _inner[key];
            set
            {
                if (TryGetValue(key, out var oldValue))
                {
                    _inner[key] = value;
                    NotifyReplace(key, oldValue, value);
                }
                else
                {
                    _inner[key] = value;
                    NotifyAdd(key, value);
                }
            }
        }

        /// <summary>
        /// 当前键值对数量。
        /// </summary>
        public int Count => _inner.Count;

        /// <summary>
        /// 内部字典的键集合（实时视图，非快照）。
        /// </summary>
        public Dictionary<TKey, TValue>.KeyCollection Keys => _inner.Keys;

        /// <summary>
        /// 内部字典的值集合（实时视图，非快照）。
        /// </summary>
        public Dictionary<TKey, TValue>.ValueCollection Values => _inner.Values;

        /// <summary>
        /// 添加键值对，成功后触发 OnAdd/OnCountChanged/OnChanged。
        /// </summary>
        /// <param name="key">键。</param>
        /// <param name="value">值。</param>
        /// <exception cref="ArgumentException">key 已存在。</exception>
        /// <exception cref="ArgumentNullException">key 为 null。</exception>
        public void Add(TKey key, TValue value)
        {
            _inner.Add(key, value);

            NotifyAdd(key, value);
        }

        /// <summary>
        /// 仅在 key 不存在时添加，返回是否添加成功；key 已存在时不触发任何事件
        /// </summary>
        public bool TryAdd(TKey key, TValue value)
        {
            if (_inner.ContainsKey(key))
            {
                return false;
            }

            Add(key, value);
            return true;
        }

        /// <summary>
        /// key 不存在时添加（OnAdd），已存在时覆盖（OnReplace），返回是否为新增
        /// </summary>
        public bool AddOrUpdate(TKey key, TValue value)
        {
            var isAdded = !_inner.ContainsKey(key);
            this[key] = value;
            return isAdded;
        }

        /// <summary>
        /// 清空字典；与 BindableList 一致，本来为空时不触发 OnCountChanged，但仍触发 OnClear/OnChanged。
        /// </summary>
        public void Clear()
        {
            var beforeCount = Count;
            _inner.Clear();

            NotifyClear(beforeCount);
        }

        /// <summary>
        /// 按键移除，返回是否移除成功（key 不存在时返回 false 且不触发事件）。
        /// </summary>
        /// <param name="key">键。</param>
        public bool Remove(TKey key) => Remove(key, out _);

        /// <summary>
        /// 按键移除并输出被移除的值；key 不存在时返回 false 且 value 为 default，不触发事件。
        /// </summary>
        /// <param name="key">键。</param>
        /// <param name="value">被移除的值；失败时为 default。</param>
        public bool Remove(TKey key, out TValue value)
        {
            if (_inner.TryGetValue(key, out value))
            {
                _inner.Remove(key);
                NotifyRemove(key, value);
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// 按键取值；key 不存在时返回 default。
        /// </summary>
        public TValue GetValueOrDefault(TKey key) =>
            _inner.TryGetValue(key, out var value) ? value : default;

        /// <summary>
        /// 按键取值；key 不存在时返回 <paramref name="defaultValue"/>。
        /// </summary>
        public TValue GetValueOrDefault(TKey key, TValue defaultValue) =>
            _inner.TryGetValue(key, out var value) ? value : defaultValue;

        /// <summary>
        /// 是否包含指定 key。
        /// </summary>
        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);

        /// <summary>
        /// 尝试按键取值；key 不存在时返回 false 且 value 为 default。
        /// </summary>
        public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value);

        /// <summary>
        /// 返回内部字典的枚举器（实时枚举，非快照）。
        /// </summary>
        public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _inner.GetEnumerator();

        #endregion

        #region 变化通知

        /// <summary>将已完成的新增广播给操作、数量和统一变化监听。</summary>
        private void NotifyAdd(TKey key, TValue value)
        {
            if (_batchDepth > 0)
            {
                _isBatchDirty = true;
                return;
            }

            _onAdd?.Trigger(key, value);
            _onCountChanged?.Trigger(Count);
            _onChanged?.Trigger(BindableDictionaryChange<TKey, TValue>.Add(key, value));
        }

        /// <summary>携带被移除的键值对，广播删除、数量和统一变化通知。</summary>
        private void NotifyRemove(TKey key, TValue value)
        {
            if (_batchDepth > 0)
            {
                _isBatchDirty = true;
                return;
            }

            _onRemove?.Trigger(key, value);
            _onCountChanged?.Trigger(Count);
            _onChanged?.Trigger(BindableDictionaryChange<TKey, TValue>.Remove(key, value));
        }

        /// <summary>广播同一键的新旧值，不发送数量变化通知。</summary>
        private void NotifyReplace(TKey key, TValue oldValue, TValue newValue)
        {
            if (_batchDepth > 0)
            {
                _isBatchDirty = true;
                return;
            }

            _onReplace?.Trigger(key, oldValue, newValue);
            _onChanged?.Trigger(BindableDictionaryChange<TKey, TValue>.Replace(key, oldValue, newValue));
        }

        /// <summary>广播清空结果，并根据操作前数量决定是否发送数量变化通知。</summary>
        private void NotifyClear(int beforeCount)
        {
            if (_batchDepth > 0)
            {
                _isBatchDirty = true;
                return;
            }

            _onClear?.Trigger();
            if (beforeCount > 0)
            {
                // 本来为空时清空不改变数量，不触发 OnCountChanged
                _onCountChanged?.Trigger(Count);
            }
            _onChanged?.Trigger(BindableDictionaryChange<TKey, TValue>.Clear());
        }

        #endregion

        #region 显式接口实现

        /// <summary>将非泛型键值强制转换后沿用带通知的索引器；读取缺失键同样抛出 KeyNotFoundException。</summary>
        /// <remarks>键值类型不匹配时不会静默忽略，转换异常直接向上传播。</remarks>
        object IDictionary.this[object key]
        {
            get => this[(TKey)key];
            set => this[(TKey)key] = (TValue)value;
        }

        /// <summary>沿用内部字典的非固定大小标记。</summary>
        bool IDictionary.IsFixedSize => ((IDictionary)_inner).IsFixedSize;

        /// <summary>内部字典是否只读，当前实现返回 false。</summary>
        bool IDictionary.IsReadOnly => ((IDictionary)_inner).IsReadOnly;

        /// <summary>不提供同步访问保证；内部字典的同步标记不会保护包装器的通知状态。</summary>
        bool ICollection.IsSynchronized => ((ICollection)_inner).IsSynchronized;

        /// <summary>以非泛型接口返回内部键集合的实时视图。</summary>
        ICollection IDictionary.Keys => ((IDictionary)_inner).Keys;

        /// <summary>返回内部字典的同步根，不自动为数据操作、通知或批量状态加锁。</summary>
        object ICollection.SyncRoot => ((ICollection)_inner).SyncRoot;

        /// <summary>以非泛型接口返回内部值集合的实时视图。</summary>
        ICollection IDictionary.Values => ((IDictionary)_inner).Values;

        /// <summary>内部键值对集合是否只读，当前实现返回 false。</summary>
        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_inner).IsReadOnly;

        /// <inheritdoc />
        ICollection<TKey> IDictionary<TKey, TValue>.Keys => _inner.Keys;

        /// <inheritdoc />
        ICollection<TValue> IDictionary<TKey, TValue>.Values => _inner.Values;

        /// <inheritdoc />
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _inner.Keys;

        /// <inheritdoc />
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _inner.Values;

        /// <summary>转换非泛型键值后走新增入口，保留重复键异常与变化通知。</summary>
        void IDictionary.Add(object key, object value) => Add((TKey)key, (TValue)value);

        /// <summary>按内部非泛型字典规则查询键，不触发变化通知。</summary>
        bool IDictionary.Contains(object key) => ((IDictionary)_inner).Contains(key);

        /// <inheritdoc />
        void ICollection.CopyTo(Array array, int index) => ((IDictionary)_inner).CopyTo(array, index);

        /// <summary>
        /// 委托内部字典向序列化信息写入其数据，不包含本包装器的监听和批量通知状态。
        /// </summary>
        /// <param name="info">接收内部字典序列化信息的目标。</param>
        /// <param name="context">原样转交内部字典的序列化上下文。</param>
        public void GetObjectData(SerializationInfo info, StreamingContext context) =>
            ((ISerializable)_inner).GetObjectData(info, context);

        /// <summary>
        /// 反序列化完成后委托内部字典重建查找结构。
        /// </summary>
        /// <param name="sender">原样转交内部字典的回调来源。</param>
        public void OnDeserialization(object sender) => ((IDeserializationCallback)_inner).OnDeserialization(sender);

        /// <summary>转换非泛型键后按键移除，成功时沿用变化通知。</summary>
        void IDictionary.Remove(object key) => Remove((TKey)key);

        /// <summary>将键值对交给新增入口，保留键校验、重复键异常和通知。</summary>
        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

        /// <summary>查询内部字典是否同时包含指定键及相等的值。</summary>
        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Contains(item);

        /// <inheritdoc />
        void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_inner).CopyTo(array, arrayIndex);

        /// <inheritdoc />
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_inner).GetEnumerator();

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();

        /// <summary>仅在键存在且值按默认值比较器相等时移除并通知，否则返回 false。</summary>
        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
        {
            if (TryGetValue(item.Key, out var v))
            {
                if (EqualityComparer<TValue>.Default.Equals(v, item.Value))
                {
                    Remove(item.Key);
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        IDictionaryEnumerator IDictionary.GetEnumerator() => ((IDictionary)_inner).GetEnumerator();

        #endregion
    }

    /// <summary>
    /// <see cref="BindableDictionary{TKey,TValue}"/> 的创建扩展。
    /// </summary>
    public static class BindableDictionaryExtensions
    {
        /// <summary>
        /// 直接包装已有字典创建 BindableDictionary（不复制，内部使用同一个字典实例）。
        /// </summary>
        /// <param name="self">要包装的字典。</param>
        public static BindableDictionary<TKey, TValue> ToBindableDictionary<TKey, TValue>(
            this Dictionary<TKey, TValue> self) =>
            new(self);
    }
}

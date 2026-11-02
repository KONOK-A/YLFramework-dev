namespace YLFramework.Binding
{
    /// <summary>
    /// 集合变化类型
    /// </summary>
    public enum BindableCollectionChangeType
    {
        /// <summary>新增元素 / 键值对。</summary>
        Add,
        /// <summary>移除元素 / 键值对。</summary>
        Remove,
        /// <summary>替换已有元素 / 键的值（数量不变）。</summary>
        Replace,
        /// <summary>移动元素位置（仅 BindableList）。</summary>
        Move,
        /// <summary>清空集合。</summary>
        Clear,
        /// <summary>
        /// 批量通知结束时的合并通知，调用方应整体刷新
        /// </summary>
        Reset
    }

    /// <summary>
    /// BindableList 的统一变化数据
    /// Add: Index/NewValue；Remove: Index/OldValue；Replace: Index/OldValue/NewValue；
    /// Move: Index=OldIndex、NewIndex、NewValue=被移动元素；Clear/Reset: 其余字段无意义
    /// </summary>
    /// <typeparam name="T">列表元素类型。</typeparam>
    public readonly struct BindableListChange<T>
    {
        /// <summary>
        /// 直接构造变化数据；一般应使用静态工厂方法以保证字段组合正确。
        /// </summary>
        /// <param name="changeType">变化类型。</param>
        /// <param name="index">Index 字段值。</param>
        /// <param name="newIndex">NewIndex 字段值。</param>
        /// <param name="oldValue">OldValue 字段值。</param>
        /// <param name="newValue">NewValue 字段值。</param>
        public BindableListChange(BindableCollectionChangeType changeType, int index, int newIndex, T oldValue, T newValue)
        {
            ChangeType = changeType;
            Index = index;
            NewIndex = newIndex;
            OldValue = oldValue;
            NewValue = newValue;
        }

        /// <summary>
        /// 本次变化的类型，决定其余字段的有效性。
        /// </summary>
        public BindableCollectionChangeType ChangeType
        {
            get;
        }

        /// <summary>
        /// Add/Remove/Replace 为元素下标；Move 为移动前下标；Clear/Reset 为 -1。
        /// </summary>
        public int Index
        {
            get;
        }

        /// <summary>
        /// 仅 Move 有效：移动后的目标下标；其余类型为 -1。
        /// </summary>
        public int NewIndex
        {
            get;
        }

        /// <summary>
        /// 旧值：Remove 为被移除值，Replace 为替换前值；其余类型为 default。
        /// </summary>
        public T OldValue
        {
            get;
        }

        /// <summary>
        /// 新值：Add 为新增值，Replace 为替换后值，Move 为被移动元素；其余类型为 default。
        /// </summary>
        public T NewValue
        {
            get;
        }

        /// <summary>
        /// 创建 Add 变化（Index 与 NewValue 有效）。
        /// </summary>
        public static BindableListChange<T> Add(int index, T value) =>
            new(BindableCollectionChangeType.Add, index, -1, default, value);

        /// <summary>
        /// 创建 Remove 变化（Index 与 OldValue 有效）。
        /// </summary>
        public static BindableListChange<T> Remove(int index, T value) =>
            new(BindableCollectionChangeType.Remove, index, -1, value, default);

        /// <summary>
        /// 创建 Replace 变化（Index、OldValue 与 NewValue 有效）。
        /// </summary>
        public static BindableListChange<T> Replace(int index, T oldValue, T newValue) =>
            new(BindableCollectionChangeType.Replace, index, -1, oldValue, newValue);

        /// <summary>
        /// 创建 Move 变化（Index=旧下标、NewIndex=新下标、NewValue=被移动元素）。
        /// </summary>
        public static BindableListChange<T> Move(int oldIndex, int newIndex, T value) =>
            new(BindableCollectionChangeType.Move, oldIndex, newIndex, default, value);

        /// <summary>
        /// 创建 Clear 变化（无附带数据）。
        /// </summary>
        public static BindableListChange<T> Clear() =>
            new(BindableCollectionChangeType.Clear, -1, -1, default, default);

        /// <summary>
        /// 创建 Reset 变化（批量结束的合并通知，无附带数据）。
        /// </summary>
        public static BindableListChange<T> Reset() =>
            new(BindableCollectionChangeType.Reset, -1, -1, default, default);

        /// <summary>
        /// 返回 "ChangeType(Index:.., NewIndex:..)" 形式的调试字符串。
        /// </summary>
        public override string ToString() => $"{ChangeType}(Index:{Index}, NewIndex:{NewIndex})";
    }

    /// <summary>
    /// BindableDictionary 的统一变化数据
    /// Add: Key/NewValue；Remove: Key/OldValue；Replace: Key/OldValue/NewValue；Clear/Reset: 其余字段无意义
    /// </summary>
    /// <typeparam name="TKey">键类型。</typeparam>
    /// <typeparam name="TValue">值类型。</typeparam>
    public readonly struct BindableDictionaryChange<TKey, TValue>
    {
        /// <summary>
        /// 直接构造变化数据；一般应使用静态工厂方法以保证字段组合正确。
        /// </summary>
        /// <param name="changeType">变化类型。</param>
        /// <param name="key">Key 字段值。</param>
        /// <param name="oldValue">OldValue 字段值。</param>
        /// <param name="newValue">NewValue 字段值。</param>
        public BindableDictionaryChange(BindableCollectionChangeType changeType, TKey key, TValue oldValue, TValue newValue)
        {
            ChangeType = changeType;
            Key = key;
            OldValue = oldValue;
            NewValue = newValue;
        }

        /// <summary>
        /// 本次变化的类型，决定其余字段的有效性。
        /// </summary>
        public BindableCollectionChangeType ChangeType
        {
            get;
        }

        /// <summary>
        /// 发生变化的键；Clear/Reset 为 default。
        /// </summary>
        public TKey Key
        {
            get;
        }

        /// <summary>
        /// 旧值：Remove 为被移除值，Replace 为替换前值；其余类型为 default。
        /// </summary>
        public TValue OldValue
        {
            get;
        }

        /// <summary>
        /// 新值：Add 为新增值，Replace 为替换后值；其余类型为 default。
        /// </summary>
        public TValue NewValue
        {
            get;
        }

        /// <summary>
        /// 创建 Add 变化（Key 与 NewValue 有效）。
        /// </summary>
        public static BindableDictionaryChange<TKey, TValue> Add(TKey key, TValue value) =>
            new(BindableCollectionChangeType.Add, key, default, value);

        /// <summary>
        /// 创建 Remove 变化（Key 与 OldValue 有效）。
        /// </summary>
        public static BindableDictionaryChange<TKey, TValue> Remove(TKey key, TValue value) =>
            new(BindableCollectionChangeType.Remove, key, value, default);

        /// <summary>
        /// 创建 Replace 变化（Key、OldValue 与 NewValue 有效）。
        /// </summary>
        public static BindableDictionaryChange<TKey, TValue> Replace(TKey key, TValue oldValue, TValue newValue) =>
            new(BindableCollectionChangeType.Replace, key, oldValue, newValue);

        /// <summary>
        /// 创建 Clear 变化（无附带数据）。
        /// </summary>
        public static BindableDictionaryChange<TKey, TValue> Clear() =>
            new(BindableCollectionChangeType.Clear, default, default, default);

        /// <summary>
        /// 创建 Reset 变化（批量结束的合并通知，无附带数据）。
        /// </summary>
        public static BindableDictionaryChange<TKey, TValue> Reset() =>
            new(BindableCollectionChangeType.Reset, default, default, default);

        /// <summary>
        /// 返回 "ChangeType(Key:..)" 形式的调试字符串。
        /// </summary>
        public override string ToString() => $"{ChangeType}(Key:{Key})";
    }
}

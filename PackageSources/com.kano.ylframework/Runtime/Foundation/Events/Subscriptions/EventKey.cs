using System;

namespace YLFramework.Events
{
    /// <summary>
    /// 保留完整身份的事件键；string、int、enum 与 Type 分属独立键域。
    /// string 使用 Ordinal 比较，enum 保留枚举类型及完整底层值；default(EventKey) 不是有效键。
    /// </summary>
    public readonly struct EventKey : IEquatable<EventKey>
    {
        /// <summary>区分互不混用的键域，并识别未初始化的默认值。</summary>
        private enum KeyKind : byte
        {
            /// <summary>尚未构造的无效键域。</summary>
            Invalid,
            /// <summary>按 Ordinal 比较的字符串键域。</summary>
            String,
            /// <summary>整数键域。</summary>
            Int,
            /// <summary>保留枚举类型与底层值的键域。</summary>
            Enum,
            /// <summary>按声明类型识别消息的键域。</summary>
            Type
        }

        private readonly KeyKind _kind;
        private readonly object _value;
        private readonly int _intValue;

        /// <summary>创建区分大小写的字符串键，不与数值或类型键互换。</summary>
        /// <param name="value">非 null 的键值，允许空字符串。</param>
        public EventKey(string value)
        {
            _kind = KeyKind.String;
            _value = value ?? throw new ArgumentNullException(nameof(value));
            _intValue = 0;
        }

        /// <summary>创建整数键，保留整个 int 值而不转换为字符串。</summary>
        public EventKey(int value)
        {
            _kind = KeyKind.Int;
            _value = null;
            _intValue = value;
        }

        /// <summary>创建枚举键，枚举类型不同则属于不同身份，即使底层数值相同。</summary>
        /// <param name="value">非 null 的枚举值。</param>
        public EventKey(Enum value)
        {
            _kind = KeyKind.Enum;
            _value = value ?? throw new ArgumentNullException(nameof(value));
            _intValue = 0;
        }

        /// <summary>创建类型键，用于按指定声明类型路由消息。</summary>
        /// <param name="value">非 null 的类型身份。</param>
        public EventKey(Type value)
        {
            _kind = KeyKind.Type;
            _value = value ?? throw new ArgumentNullException(nameof(value));
            _intValue = 0;
        }

        /// <summary>以泛型声明类型构造 payload 类型键，不读取对象的运行时类型。</summary>
        public static EventKey ForType<T>() => new EventKey(typeof(T));

        /// <summary>将非 null 字符串转换为字符串域的事件键。</summary>
        public static implicit operator EventKey(string value) => new EventKey(value);
        /// <summary>将整数转换为整数域的事件键。</summary>
        public static implicit operator EventKey(int value) => new EventKey(value);
        /// <summary>将非 null 枚举转换为保留枚举类型的事件键。</summary>
        public static implicit operator EventKey(Enum value) => new EventKey(value);
        /// <summary>将非 null 类型转换为类型域的事件键。</summary>
        public static implicit operator EventKey(Type value) => new EventKey(value);

        /// <summary>拒绝 default(EventKey)，防止未初始化的键进入通道映射。</summary>
        internal void Validate()
        {
            if (_kind == KeyKind.Invalid)
            {
                throw new ArgumentException("default(EventKey) is not a valid event key.");
            }
        }

        /// <summary>比较键域及域内值；字符串按 Ordinal 比较，不进行跨域数值转换。</summary>
        public bool Equals(EventKey other)
        {
            if (_kind != other._kind)
            {
                return false;
            }

            switch (_kind)
            {
                case KeyKind.String:
                    return StringComparer.Ordinal.Equals((string)_value, (string)other._value);
                case KeyKind.Int:
                    return _intValue == other._intValue;
                default:
                    return Equals(_value, other._value);
            }
        }

        /// <summary>仅与另一个 EventKey 比较键域和值，其他对象均不相等。</summary>
        public override bool Equals(object obj) => obj is EventKey other && Equals(other);

        /// <summary>组合键域和值的哈希，保持与键相等判定一致。</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = _kind == KeyKind.Int ? _intValue
                    : _kind == KeyKind.String ? StringComparer.Ordinal.GetHashCode((string)_value)
                    : _value?.GetHashCode() ?? 0;
                return ((int)_kind * 397) ^ hash;
            }
        }

        /// <summary>判断两个键的键域和值是否相等。</summary>
        public static bool operator ==(EventKey left, EventKey right) => left.Equals(right);
        /// <summary>判断两个键的键域或值是否不同。</summary>
        public static bool operator !=(EventKey left, EventKey right) => !left.Equals(right);

        /// <summary>生成包含键域的诊断文本，枚举同时显示类型名与完整十进制值。</summary>
        public override string ToString()
        {
            switch (_kind)
            {
                case KeyKind.String:
                    return $"string:\"{_value}\"";
                case KeyKind.Int:
                    return $"int:{_intValue}";
                case KeyKind.Enum:
                    return $"enum:{_value.GetType().FullName}:{((Enum)_value).ToString("D")}";
                case KeyKind.Type:
                    return $"type:{((Type)_value).FullName}";
                default:
                    return "invalid";
            }
        }
    }
}

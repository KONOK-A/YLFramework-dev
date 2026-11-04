using System;
using System.Collections.Generic;

namespace YLFramework.DataAssets
{
    /// <summary>按精确名称和哈希登记共享根，同一根的多个持有者独立计数。</summary>
    /// <typeparam name="T">只按引用身份区分的根对象类型。</typeparam>
    internal sealed class DataAssetRegistry<T> where T : class
    {
        private readonly Dictionary<string, Entry> _names = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<int, Entry> _hashes = new Dictionary<int, Entry>();
        private readonly Action<T> _removed;

        /// <summary>保存一组固定名称、哈希与根引用及其登记计数。</summary>
        private sealed class Entry
        {
            internal string Name;
            internal int Hash;
            internal T Value;
            internal int Owners;
        }

        /// <summary>将单次登记的归还回调限制为最多执行一次。</summary>
        private sealed class Registration : IDisposable
        {
            private Action _release;
            /// <summary>保存本次登记的归还回调。</summary>
            internal Registration(Action release) => this._release = release;
            /// <summary>消费本句柄的归还回调，重复释放不再减少计数。</summary>
            public void Dispose()
            {
                var action = _release;
                _release = null;
                action?.Invoke();
            }
        }

        /// <summary>创建登记表，并接收根离开登记表时执行的可选回调。</summary>
        internal DataAssetRegistry(Action<T> removed = null) => this._removed = removed;

        /// <summary>验证名称、哈希和引用身份后新增一份登记，拒绝冲突及未释放的根改名。</summary>
        internal IDisposable Register(string name, int hash, T value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A root name is required.", nameof(name));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (_hashes.TryGetValue(hash, out var collision) && !StringComparer.Ordinal.Equals(collision.Name, name))
            {
                throw new InvalidOperationException($"DataAssets name hash collision: '{collision.Name}' and '{name}' ({hash}).");
            }

            if (_names.TryGetValue(name, out var entry))
            {
                if (!ReferenceEquals(entry.Value, value) || entry.Hash != hash)
                {
                    throw new InvalidOperationException($"Another DataAssets root is already registered as '{name}'.");
                }
            }
            else
            {
                foreach (var existing in _names.Values)
                {
                    if (ReferenceEquals(existing.Value, value))
                    {
                        throw new InvalidOperationException($"Release DataAssets root '{existing.Name}' before registering its new name '{name}'.");
                    }
                }

                entry = new Entry { Name = name, Hash = hash, Value = value };
                _names.Add(name, entry);
                _hashes.Add(hash, entry);
            }
            entry.Owners++;
            var owned = entry;
            return new Registration(() => Release(owned));
        }

        /// <summary>仅归还仍属于当前表项的登记，最后一份归还时移除双索引并通知。</summary>
        private void Release(Entry entry)
        {
            if (!_names.TryGetValue(entry.Name, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            if (--entry.Owners != 0)
            {
                return;
            }

            _names.Remove(entry.Name);
            _hashes.Remove(entry.Hash);
            _removed?.Invoke(entry.Value);
        }

        /// <summary>按精确名称借读已登记根，不存在时返回空引用。</summary>
        internal T Get(string name) => name != null && _names.TryGetValue(name, out var entry) ? entry.Value : null;
        /// <summary>按登记哈希借读根，不存在时返回空引用。</summary>
        internal T Get(int hash) => _hashes.TryGetValue(hash, out var entry) ? entry.Value : null;

        /// <summary>先清空身份索引，再逐根通知移除；旧登记不会移除后续同名的新根。</summary>
        internal void Clear()
        {
            var values = new List<Entry>(_names.Values);
            _names.Clear();
            _hashes.Clear();
            foreach (var entry in values)
            {
                _removed?.Invoke(entry.Value);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace YLFramework.Audio
{
    // 缓存只持有真实后端租约；等待者/播放保活与闲置计时不依赖 Source 池。
    /// <summary>按 Provider 实例和完整键共享在途取得与 Clip 保活，使用者归还后再决定闲置缓存或释放。</summary>
    internal sealed class AudioClipCache : IDisposable
    {
        /// <summary>以 Provider 引用身份和完整资源键比较的缓存索引，不按 Clip 对象合并责任。</summary>
        private readonly struct Key : IEquatable<Key>
        {
            internal readonly AudioClipAddress Address;
            internal Key(AudioClipAddress address)
            {
                Address = address;
            }
            /// <inheritdoc />
            public bool Equals(Key other) => ReferenceEquals(Address.Provider, other.Address.Provider) && Address.Key == other.Address.Key;
            /// <inheritdoc />
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            /// <inheritdoc />
            public override int GetHashCode() => RuntimeHelpers.GetHashCode(Address.Provider) * 397 ^ Address.Key.GetHashCode();
        }
        /// <summary>一份后端取得记录；Users 包含等待者预留及已交付租约的未归还责任，归零后才考虑闲置淘汰。</summary>
        private sealed class Entry
        {
            internal Key Key;
            internal AudioClipLease Resource;
            internal Exception Error;
            internal bool IsDone;
            internal int Users;
            internal float Progress;
            internal double IdleAt;
            internal readonly List<Waiter> Waiters = new List<Waiter>();
        }
        /// <summary>已交付 Clip 租约对应的一份缓存使用责任。</summary>
        private sealed class Use : IDisposable
        {
            private AudioClipCache _cache;
            private readonly Entry _entry;
            /// <summary>绑定已有用户计数，留待外层 Clip 租约归还时结算。</summary>
            internal Use(AudioClipCache cache, Entry entry)
            {
                _cache = cache;
                _entry = entry;
            }
            /// <summary>先撤销自身身份，再恰一次归还条目使用计数。</summary>
            public void Dispose()
            {
                var cache = _cache;
                _cache = null;
                cache?.Return(_entry);
            }
        }
        /// <summary>一次可逻辑取消的异步等待，成功时把保留的用户计数移交给 Clip 租约。</summary>
        private sealed class Waiter : IDisposable
        {
            private AudioClipCache _cache;
            private readonly Entry _entry;
            private Action<AudioClipLease, Exception> _completed;
            internal readonly Action<float> Progress;
            /// <summary>记录此等待者的完成与进度接收方，不启动额外的后端加载。</summary>
            internal Waiter(AudioClipCache cache, Entry entry, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                _cache = cache;
                _entry = entry;
                _completed = completed;
                Progress = progress;
            }
            /// <summary>先摘除等待者，再交付独立使用租约或错误；回调抛错时归还已交付的租约。</summary>
            internal void Deliver()
            {
                if (_cache == null)
                {
                    return;
                }

                var cache = _cache;
                var callback = _completed;
                _cache = null;
                _completed = null;
                _entry.Waiters.Remove(this);
                ValidateResource(_entry);
                var lease = _entry.Error == null ? cache.HandOff(_entry) : null;
                if (lease == null)
                {
                    cache.Return(_entry);
                }

                try
                {
                    callback(lease, _entry.Error);
                }
                catch
                {
                    lease?.ReleaseSafely();
                    throw;
                }
            }
            /// <summary>撤销本等待者并归还预留计数，不物理取消共享加载。</summary>
            public void Dispose()
            {
                if (_cache == null)
                {
                    return;
                }

                var cache = _cache;
                _cache = null;
                _completed = null;
                _entry.Waiters.Remove(this);
                cache.Return(_entry);
            }
        }
        private readonly Dictionary<Key, Entry> _entries = new Dictionary<Key, Entry>();
        private bool _isClosed;
        /// <summary>同步复用已完成条目或发起后端取得；同键仍在异步加载时明确拒绝同步读取。</summary>
        internal AudioClipLease Load(AudioClipAddress address)
        {
            var key = new Key(address);
            if (_entries.TryGetValue(key, out var existing))
            {
                if (!existing.IsDone)
                {
                    throw new InvalidOperationException("This resource is loading asynchronously; use an asynchronous waiter.");
                }

                ValidateResource(existing);
                if (existing.Error != null)
                {
                    if (existing.Users == 0)
                    {
                        Remove(existing);
                    }
                    throw existing.Error;
                }
                existing.Users++;
                return HandOff(existing);
            }
            var entry = new Entry { Key = key, Users = 1 };
            _entries.Add(key, entry);
            try
            {
                Complete(entry, address.Provider.Load(address.Key), null);
                if (entry.Error != null)
                {
                    throw entry.Error;
                }

                return HandOff(entry);
            }
            catch (Exception exception)
            {
                Complete(entry, null, exception);
                Return(entry);
                throw;
            }
        }
        /// <summary>为同键共享条目增加独立等待者，首次请求才启动 Provider；允许同步完成。</summary>
        internal IDisposable LoadAsync(AudioClipAddress address, Action<AudioClipLease, Exception> completed, Action<float> progress)
        {
            var key = new Key(address);
            var fresh = !_entries.TryGetValue(key, out var entry);
            if (fresh)
            {
                entry = new Entry { Key = key };
                _entries.Add(key, entry);
            }
            entry.Users++;
            var waiter = new Waiter(this, entry, completed, progress);
            entry.Waiters.Add(waiter);
            if (entry.IsDone)
            {
                waiter.Deliver();
            }
            else if (fresh)
            {
                try
                {
                    address.Provider.LoadAsync(address.Key, (lease, error) => Complete(entry, lease, error), value => Report(entry, value));
                }
                catch (Exception exception)
                {
                    Complete(entry, null, exception);
                }
            }
            else
            {
                progress?.Invoke(entry.Progress);
            }

            return waiter;
        }
        /// <summary>将加载进度限于 [0,1] 并通知等待者快照，关闭或已完成的条目不再投递。</summary>
        private void Report(Entry entry, float value)
        {
            if (_isClosed || entry.IsDone)
            {
                return;
            }

            entry.Progress = Mathf.Clamp01(value);
            foreach (var waiter in entry.Waiters.ToArray())
            {
                waiter.Progress?.Invoke(entry.Progress);
            }
        }
        /// <summary>记录后端结果并交付当前等待者；迟到或重复结果只按原条目责任归还。</summary>
        private void Complete(Entry entry, AudioClipLease lease, Exception error)
        {
            if (entry.IsDone)
            {
                if (!ReferenceEquals(lease, entry.Resource))
                {
                    lease?.ReleaseSafely();
                }

                return;
            }
            entry.IsDone = true;
            entry.Resource = lease;
            entry.Error = error ?? (lease == null || lease.IsReleased || !lease.Clip ? new InvalidOperationException("Provider returned no live resource lease.") : null);
            if (_isClosed)
            {
                if (entry.Users == 0)
                {
                    lease?.ReleaseSafely();
                }

                return;
            }
            foreach (var waiter in entry.Waiters.ToArray())
            {
                waiter.Deliver();
            }

            if (entry.Users == 0)
            {
                RetireOrCache(entry);
            }
        }
        /// <summary>把已经计入 Users 的一份责任包装为新租约，不重复增加使用计数。</summary>
        private AudioClipLease HandOff(Entry entry) => new AudioClipLease(entry.Resource.Clip, new Use(this, entry));
        /// <summary>复用前检查后端租约仍可交付，并将失效原因保存为条目错误。</summary>
        private static void ValidateResource(Entry entry)
        {
            if (entry.Error != null)
            {
                return;
            }

            try
            {
                if (entry.Resource == null)
                {
                    throw new InvalidOperationException("The acquired AudioClip is no longer available: " + entry.Key.Address.Key);
                }

                entry.Resource.ValidateAcquisition();
            }
            catch (Exception error)
            {
                entry.Error = error;
            }
        }
        /// <summary>归还一份等待或使用责任，最后一份归还时记录闲置起点并尝试退役。</summary>
        private void Return(Entry entry)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                entry.IdleAt = Time.realtimeSinceStartupAsDouble;
                if (entry.IsDone)
                {
                    RetireOrCache(entry);
                }
            }
        }
        /// <summary>仅将可缓存的成功后端责任保留为闲置条目，其余归还后端。</summary>
        private void RetireOrCache(Entry entry)
        {
            if (_isClosed || entry.Error != null || !entry.Key.Address.Provider.CanCache || AudioKit.ClipCacheCapacity == 0 || AudioKit.ClipCacheIdleSeconds == 0)
            {
                Remove(entry);
            }
            else
            {
                Tick(Time.realtimeSinceStartupAsDouble);
            }
        }
        /// <summary>只移除无人使用的原条目并归还其后端租约，不触碰重入创建的同键条目。</summary>
        private void Remove(Entry entry)
        {
            if (entry.Users != 0)
            {
                return;
            }

            if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(entry, current))
            {
                _entries.Remove(entry.Key);
            }

            entry.Resource?.ReleaseSafely();
        }
        /// <summary>按闲置时长和容量淘汰最早闲置的条目，不淘汰仍有等待者或使用者的资源。</summary>
        internal void Tick(double now)
        {
            if (_isClosed || _entries.Count == 0)
            {
                return;
            }

            var count = 0;
            var expired = false;
            foreach (var entry in _entries.Values)
            {
                if (entry.IsDone && entry.Users == 0)
                {
                    count++;
                    expired |= now - entry.IdleAt >= AudioKit.ClipCacheIdleSeconds;
                }
            }

            if (!expired && count <= AudioKit.ClipCacheCapacity)
            {
                return;
            }

            var idle = new List<Entry>();
            foreach (var entry in _entries.Values)
            {
                if (entry.IsDone && entry.Users == 0)
                {
                    idle.Add(entry);
                }
            }

            idle.Sort((a, b) => a.IdleAt.CompareTo(b.IdleAt));
            var excess = idle.Count - AudioKit.ClipCacheCapacity;
            for (var i = 0; i < idle.Count; i++)
            {
                if (i < excess || now - idle[i].IdleAt >= AudioKit.ClipCacheIdleSeconds)
                {
                    Remove(idle[i]);
                }
            }
        }
        /// <summary>显式归还当前全部闲置且已完成的条目。</summary>
        internal void ClearIdle()
        {
            foreach (var entry in new List<Entry>(_entries.Values))
            {
                if (entry.IsDone && entry.Users == 0)
                {
                    Remove(entry);
                }
            }
        }
        /// <summary>关闭缓存并归还闲置资源；在用与在途条目仍由各自责任完成归还。</summary>
        public void Dispose()
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            // 在途 producer 不能被物理取消；迟到完成仍捕获原 Entry 并归还它自己的责任。
            foreach (var entry in new List<Entry>(_entries.Values))
            {
                if (entry.IsDone && entry.Users == 0)
                {
                    Remove(entry);
                }
            }

            _entries.Clear();
        }
    }
}

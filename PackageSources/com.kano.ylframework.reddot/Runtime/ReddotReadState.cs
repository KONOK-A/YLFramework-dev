using System;
using System.Collections.Generic;
using System.IO;

namespace YLFramework.RedDots
{
    /// <summary>Instance-owned confirmed string key/version sets for one explicit account/slot namespace.</summary>
    /// <remarks>
    /// All operations, including reads, binding and disposal, require main-thread serial use like ReddotManager.
    /// No global instance, session callback or automatic persistence is registered. IO callers must explicitly
    /// Import on the main thread. Reset/session invalidates bindings, not this caller-owned confirmed set.
    /// </remarks>
    public sealed class ReddotReadState : IDisposable
    {
        private Dictionary<string, HashSet<string>> _confirmed = new(StringComparer.Ordinal);
        private readonly List<ReddotUnreadSource> _bindings = new();
        private bool _isDisposed;

        /// <summary>Immutable explicit identity; changing accounts/slots requires another state instance.</summary>
        public string Namespace
        {
            get;
        }

        /// <summary>Create empty memory state without loading storage or modifying Manager state.</summary>
        public ReddotReadState(string readNamespace)
        {
            RequireText(readNamespace, nameof(readNamespace));
            Namespace = readNamespace;
        }

        /// <summary>Whether this exact Ordinal key/version pair has been explicitly confirmed.</summary>
        public bool IsRead(string key, string version)
        {
            RequireActive();
            RequireText(key, nameof(key));
            RequireText(version, nameof(version));
            return Contains(_confirmed, key, version);
        }

        /// <summary>Idempotently confirm only this version and batch changes to currently owned bindings.</summary>
        public void MarkRead(string key, string version)
        {
            if (IsRead(key, version))
            {
                return;
            }
            var next = CopyConfirmed();
            if (!next.TryGetValue(key, out var versions))
            {
                versions = new HashSet<string>(StringComparer.Ordinal);
                next.Add(key, versions);
            }
            versions.Add(version);
            ReplaceConfirmed(next);
        }

        /// <summary>Remove only the specified confirmation; return false when it was not present.</summary>
        public bool Forget(string key, string version)
        {
            if (!IsRead(key, version))
            {
                return false;
            }
            var next = CopyConfirmed();
            var versions = next[key];
            versions.Remove(version);
            if (versions.Count == 0)
            {
                next.Remove(key);
            }
            ReplaceConfirmed(next);
            return true;
        }

        /// <summary>Explicitly remove confirmations for keys absent from liveKeys; return the removed key count.</summary>
        /// <remarks>Validate the entire enumeration before mutation. Binding/version changes never prune implicitly.</remarks>
        public int PruneRemovedKeys(IEnumerable<string> liveKeys)
        {
            RequireActive();
            if (liveKeys == null)
            {
                throw new ArgumentNullException(nameof(liveKeys));
            }
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in liveKeys)
            {
                RequireText(key, nameof(liveKeys));
                live.Add(key);
            }
            var next = CopyConfirmed();
            foreach (var key in _confirmed.Keys)
            {
                if (!live.Contains(key))
                {
                    next.Remove(key);
                }
            }
            var removed = _confirmed.Count - next.Count;
            if (removed != 0)
            {
                ReplaceConfirmed(next);
            }
            return removed;
        }

        /// <summary>Deep-copy only confirmed records into schema 1, sorted Ordinal for deterministic output.</summary>
        public ReddotReadSnapshot Export()
        {
            RequireActive();
            var keys = new List<string>(_confirmed.Keys);
            keys.Sort(StringComparer.Ordinal);
            var records = new ReddotReadRecord[keys.Count];
            for (var index = 0; index < keys.Count; index++)
            {
                var key = keys[index];
                var versions = new List<string>(_confirmed[key]);
                versions.Sort(StringComparer.Ordinal);
                records[index] = new ReddotReadRecord { key = key, versions = versions.ToArray() };
            }
            return new ReddotReadSnapshot { schemaVersion = 1, readNamespace = Namespace, records = records };
        }

        /// <summary>Fully validate and deep-copy a same-namespace snapshot before replacing any confirmed state.</summary>
        /// <remarks>Numeric failure restores bindings and old records; observer errors propagate after commit.</remarks>
        public void Import(ReddotReadSnapshot snapshot)
        {
            RequireActive();
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            var next = snapshot.CopyRecords();
            if (!string.Equals(Namespace, snapshot.readNamespace, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Reddot read snapshot namespace does not match this state.");
            }
            ReplaceConfirmed(next);
        }

        /// <summary>Register exactly one business source contributing 0 when read, otherwise 1.</summary>
        /// <remarks>UI must subscribe, not rebind the same path/sourceId. A failed handoff releases this binding.</remarks>
        public ReddotUnreadSource BindUnreadSource(string path, string sourceId, string key, string version)
        {
            var value = IsRead(key, version) ? 0 : 1;
            RequireText(sourceId, nameof(sourceId));
            path = ReddotManager.NormalizePath(path);
            RequireText(path, nameof(path));
            ReddotUnreadSource binding = null;
            try
            {
                using var batch = ReddotManager.BeginBatch();
                var source = ReddotManager.RegisterSource(path, sourceId, value);
                binding = new ReddotUnreadSource(this, source, key, version);
                _bindings.Add(binding);
            }
            catch (Exception error) when (binding != null)
            {
                // A throwing observer must not leave a source whose handle was never returned.
                try
                {
                    binding.Dispose();
                }
                catch (Exception correction)
                {
                    throw new AggregateException("Reddot unread registration and rollback notifications failed.", error, correction);
                }
                throw;
            }
            return binding;
        }

        /// <summary>Release this instance's bindings in one batch; never release another instance's sources.</summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
            _confirmed.Clear();
            if (_bindings.Count == 0)
            {
                return;
            }
            using var batch = ReddotManager.BeginBatch();
            while (_bindings.Count != 0)
            {
                _bindings[_bindings.Count - 1].Dispose();
            }
        }

        internal void Unbind(ReddotUnreadSource binding) => _bindings.Remove(binding);

        internal static void RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Reddot identity/version must be nonblank.", parameterName);
            }
        }

        private void RequireActive()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ReddotReadState));
            }
        }

        private static bool Contains(Dictionary<string, HashSet<string>> records, string key, string version)
            => records.TryGetValue(key, out var versions) && versions.Contains(version);

        private Dictionary<string, HashSet<string>> CopyConfirmed()
        {
            var next = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var pair in _confirmed)
            {
                next.Add(pair.Key, new HashSet<string>(pair.Value, StringComparer.Ordinal));
            }
            return next;
        }

        private void ReplaceConfirmed(Dictionary<string, HashSet<string>> next)
        {
            var changes = new List<SourceChange>();
            foreach (var binding in _bindings)
            {
                var source = binding.Source;
                if (!source.IsCurrent)
                {
                    continue;
                }
                var value = Contains(next, binding.Key, binding.Version) ? 0 : 1;
                if (source.Value != value)
                {
                    changes.Add(new SourceChange(source, value));
                }
            }
            if (changes.Count == 0)
            {
                _confirmed = next;
                return;
            }
            // Reductions first allow valid redistributions even near checked aggregate limits.
            changes.Sort((first, second) => first.NextValue.CompareTo(second.NextValue));
            using var batch = ReddotManager.BeginBatch();
            var applied = 0;
            try
            {
                for (; applied < changes.Count; applied++)
                {
                    var change = changes[applied];
                    change.Source.SetValue(change.NextValue);
                }
            }
            catch (OverflowException error)
            {
                // No observers run inside this batch. Reverse the successful monotone writes before flushing.
                for (var index = applied - 1; index >= 0; index--)
                {
                    var change = changes[index];
                    change.Source.SetValue(change.PreviousValue);
                }
                try
                {
                    batch.Dispose();
                }
                catch (Exception notification)
                {
                    throw new AggregateException("Reddot read update and rollback notification failed.", error, notification);
                }
                throw;
            }
            _confirmed = next;
        }

        private readonly struct SourceChange
        {
            internal readonly ReddotSource Source;
            internal readonly long PreviousValue;
            internal readonly long NextValue;

            internal SourceChange(ReddotSource source, long nextValue)
            {
                Source = source;
                PreviousValue = source.Value;
                NextValue = nextValue;
            }
        }
    }
}

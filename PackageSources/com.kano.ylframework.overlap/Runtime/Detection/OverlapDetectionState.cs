using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace YLFramework.Overlap
{
    public enum OverlapSamplingMode
    {
        Manual,
        Update,
        FixedUpdate
    }

    public enum OverlapExitReason
    {
        Separated,
        TargetDestroyed,
        TargetDisabled,
        FilterChanged,
        Reconfigured,
        Stopped,
        Disabled,
        SceneChanged
    }

    /// <summary>Borrowed objects with identity captured while alive. Target is the collider or its grouped body.</summary>
    public readonly struct OverlapContact<T>
    {
        public T Collider
        {
            get;
        }
        public UnityEngine.Object Target
        {
            get;
        }
        public int Identity
        {
            get;
        }
        public int ColliderInstanceId
        {
            get;
        }

        internal OverlapContact(T collider, UnityEngine.Object target, int identity, int colliderInstanceId)
        {
            Collider = collider;
            Target = target;
            Identity = identity;
            ColliderInstanceId = colliderInstanceId;
        }
    }

    // Pure indexed snapshots. Clearing a baseline never mutates the lists being dispatched.
    internal sealed class OverlapContactTracker<T>
    {
        private List<T> _baseline = new List<T>();
        private List<T> _retired = new List<T>();
        private ReadOnlyCollection<T> _view;
        private ReadOnlyCollection<T> _retiredView;
        private readonly HashSet<int> _baselineIds = new HashSet<int>();
        private readonly List<int> _pendingIds = new List<int>();
        private readonly HashSet<int> _pendingSet = new HashSet<int>();
        internal readonly List<T> Pending = new List<T>();
        internal readonly List<T> Enters = new List<T>();
        internal readonly List<T> Stays = new List<T>();
        internal readonly List<T> Exits = new List<T>();
        internal ReadOnlyCollection<T> View => _view;
        private readonly List<int> _identities = new List<int>();

        internal OverlapContactTracker()
        {
            _view = _baseline.AsReadOnly();
            _retiredView = _retired.AsReadOnly();
        }

        internal bool Add(int identity, T contact, int capacity)
        {
            if (_pendingSet.Contains(identity))
            {
                return true;
            }
            if (Pending.Count == capacity)
            {
                return false;
            }
            _pendingSet.Add(identity);
            _pendingIds.Add(identity);
            Pending.Add(contact);
            return true;
        }

        internal void Begin()
        {
            Pending.Clear();
            _pendingIds.Clear();
            _pendingSet.Clear();
        }

        internal void Commit()
        {
            for (int i = 0; i < _baseline.Count; i++)
            {
                if (!_pendingSet.Contains(_identities[i]))
                {
                    Exits.Add(_baseline[i]);
                }
            }
            for (int i = 0; i < Pending.Count; i++)
            {
                (_baselineIds.Contains(_pendingIds[i]) ? Stays : Enters).Add(Pending[i]);
            }
            _baseline.Clear();
            _baseline.AddRange(Pending);
            _identities.Clear();
            _identities.AddRange(_pendingIds);
            _baselineIds.Clear();
            _baselineIds.UnionWith(_pendingSet);
        }

        internal void Release(List<T> destination)
        {
            destination.AddRange(_baseline);
            // The callback may still be enumerating the old view after its nested Stop returns.
            var old = _baseline;
            _baseline = _retired;
            _retired = old;
            var oldView = _view;
            _view = _retiredView;
            _retiredView = oldView;
            _baselineIds.Clear();
            _identities.Clear();
        }

        internal void End()
        {
            Begin();
            Enters.Clear();
            Stays.Clear();
            Exits.Clear();
            _retired.Clear();
        }
    }

    internal static class OverlapDetectionGeometry
    {
        internal static void Basis(Matrix4x4 matrix, Quaternion rotation, out Vector3 x, out Vector3 y,
            out Vector3 z, out Vector3 scale)
        {
            x = matrix.MultiplyVector(rotation * Vector3.right);
            y = matrix.MultiplyVector(rotation * Vector3.up);
            z = matrix.MultiplyVector(rotation * Vector3.forward);
            scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            OverlapShape3D.ValidateVector(scale, "scale");
            if (scale.x <= 0 || scale.y <= 0 || scale.z <= 0)
            {
                throw new InvalidOperationException("The local-to-world transform must be nonsingular.");
            }
            x /= scale.x;
            y /= scale.y;
            z /= scale.z;
            if (Mathf.Abs(Vector3.Dot(x, y)) > 0.00001f || Mathf.Abs(Vector3.Dot(x, z)) > 0.00001f ||
                Mathf.Abs(Vector3.Dot(y, z)) > 0.00001f)
            {
                throw new InvalidOperationException("Sheared local geometry cannot be represented by an exact physics shape.");
            }
        }

        internal static bool EqualScale(float a, float b) => Mathf.Abs(a - b) <= Mathf.Max(a, b) * 0.00001f;

        internal static void ValidateFrequency(float frequency)
        {
            if (!OverlapShape2D.IsFinite(frequency) || frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency), "A finite positive sampling frequency is required.");
            }
        }

        internal static void ValidateMode(OverlapSamplingMode mode)
        {
            if (mode < OverlapSamplingMode.Manual || mode > OverlapSamplingMode.FixedUpdate)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        internal static void ValidateCapacity(int initialCapacity, int maxCapacity)
        {
            if (initialCapacity <= 0 || maxCapacity < initialCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(initialCapacity), "Require 0 < initialCapacity <= maxCapacity.");
            }
        }
    }
}

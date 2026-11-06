using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>
    /// Synchronous main-thread queries into caller-owned arrays, never grown or retained.
    /// A saturated raw buffer is not a complete snapshot, even after filtering or grouping.
    /// Unity globally suppresses 2D triggers when queriesHitTriggers is false, even with an explicit filter.
    /// Such an unsatisfiable trigger-including request is Invalidated, never a claimed-complete miss.
    /// </summary>
    public static class OverlapQuery2D
    {
        public static OverlapQueryResult2D Query(in OverlapShape2D shape, in OverlapFilter2D filter,
            Collider2D[] results) => Query(Physics2D.defaultPhysicsScene, shape, filter, results);

        public static OverlapQueryResult2D Query(PhysicsScene2D scene, in OverlapShape2D shape,
            in OverlapFilter2D filter, Collider2D[] results) => Query(scene, shape, filter, results, null);

        public static OverlapQueryResult2D Query(in OverlapShape2D shape, in OverlapFilter2D filter,
            Collider2D[] results, OverlapQueryWorkspace2D workspace) =>
            Query(Physics2D.defaultPhysicsScene, shape, filter, results, workspace);

        public static OverlapQueryResult2D Query(PhysicsScene2D scene, in OverlapShape2D shape,
            in OverlapFilter2D filter, Collider2D[] results, OverlapQueryWorkspace2D workspace)
        {
            ValidateBuffer(results);
            shape.Validate();
            filter.Validate();
            workspace?.Begin();
            try
            {
                return QueryCore(scene, shape, filter, results, workspace);
            }
            finally
            {
                workspace?.End();
            }
        }

        private static OverlapQueryResult2D QueryCore(PhysicsScene2D scene, in OverlapShape2D shape,
            in OverlapFilter2D filter, Collider2D[] results, OverlapQueryWorkspace2D workspace)
        {
            bool sector = shape.Kind == OverlapShapeKind2D.Sector;
            if (sector && workspace == null)
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult2D(0, 0, OverlapQueryStatus.Invalidated,
                    OverlapQueryInvalidationReason.WorkspaceRequired);
            }
            bool validScene = scene.IsValid();
            if (!validScene || (filter.ContactFilter.useTriggers && !Physics2D.queriesHitTriggers))
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult2D(0, 0, OverlapQueryStatus.Invalidated,
                    validScene ? OverlapQueryInvalidationReason.TriggerQueriesDisabled : OverlapQueryInvalidationReason.InvalidScene);
            }
            float broadRadius = 0;
            if (sector && !workspace.TryBroadRadius(shape.OuterRadius, out broadRadius))
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult2D(0, 0, OverlapQueryStatus.Invalidated,
                    OverlapQueryInvalidationReason.InvalidGeometry);
            }
            int rawCount = sector ? RawCircle(scene, shape.Center, broadRadius, filter.ContactFilter, results) :
                QueryRaw(scene, shape, filter.ContactFilter, results);
            var status = rawCount == results.Length ? OverlapQueryStatus.MayBeTruncated : OverlapQueryStatus.Complete;
            int count = 0;
            for (int i = 0; i < rawCount; i++)
            {
                if (filter.Allows(results[i]))
                {
                    if (sector)
                    {
                        var reason = workspace.Intersects(results[i], shape, out bool hit);
                        if (reason != OverlapQueryInvalidationReason.None)
                        {
                            Array.Clear(results, 0, results.Length);
                            return new OverlapQueryResult2D(0, rawCount, OverlapQueryStatus.Invalidated, reason);
                        }
                        if (!hit)
                        {
                            continue;
                        }
                    }
                    results[count++] = results[i];
                }
            }
            // A single native shape query already returns distinct colliders; only body grouping needs a set operation.
            if (filter.GroupByRigidbody)
            {
                Sort(results, count, new ColliderComparer(shape.Center, true));
                int unique = 0;
                int previous = 0;
                for (int i = 0; i < count; i++)
                {
                    int identity = Identity(results[i]);
                    if (unique == 0 || identity != previous)
                    {
                        results[unique++] = results[i];
                        previous = identity;
                    }
                }
                count = unique;
            }
            if (filter.SortByDistance)
            {
                Sort(results, count, new ColliderComparer(shape.Center, false));
            }
            Array.Clear(results, count, results.Length - count);
            return new OverlapQueryResult2D(count, rawCount, status);
        }

        public static OverlapQueryResult2D Cast(in OverlapShape2D shape, Vector2 direction, float distance,
            in OverlapFilter2D filter, RaycastHit2D[] results) =>
            Cast(Physics2D.defaultPhysicsScene, shape, direction, distance, filter, results);

        /// <summary>Point casts are rays. Starting-inside behavior follows Physics2D.queriesStartInColliders.</summary>
        public static OverlapQueryResult2D Cast(PhysicsScene2D scene, in OverlapShape2D shape,
            Vector2 direction, float distance, in OverlapFilter2D filter, RaycastHit2D[] results)
        {
            ValidateBuffer(results);
            shape.Validate();
            filter.Validate();
            direction = ValidateCast(direction, distance);
            if (shape.Kind == OverlapShapeKind2D.Sector)
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult2D(0, 0, OverlapQueryStatus.Invalidated,
                    OverlapQueryInvalidationReason.UnsupportedCast);
            }
            bool validScene = scene.IsValid();
            if (!validScene || (filter.ContactFilter.useTriggers && !Physics2D.queriesHitTriggers))
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult2D(0, 0, OverlapQueryStatus.Invalidated,
                    validScene ? OverlapQueryInvalidationReason.TriggerQueriesDisabled : OverlapQueryInvalidationReason.InvalidScene);
            }
            int rawCount = CastRaw(scene, shape, direction, distance, filter.ContactFilter, results);
            var status = rawCount == results.Length ? OverlapQueryStatus.MayBeTruncated : OverlapQueryStatus.Complete;
            int count = 0;
            for (int i = 0; i < rawCount; i++)
            {
                if (filter.Allows(results[i].collider))
                {
                    results[count++] = results[i];
                }
            }
            if (filter.GroupByRigidbody)
            {
                Sort(results, count, new HitComparer(true));
                int unique = 0;
                int previous = 0;
                for (int i = 0; i < count; i++)
                {
                    int identity = Identity(results[i].collider);
                    if (unique == 0 || identity != previous)
                    {
                        results[unique++] = results[i];
                        previous = identity;
                    }
                }
                count = unique;
            }
            if (filter.SortByDistance)
            {
                Sort(results, count, new HitComparer(false));
            }
            Array.Clear(results, count, results.Length - count);
            return new OverlapQueryResult2D(count, rawCount, status);
        }

        // Kept as the single native dispatch for later legacy-component reuse, without changing legacy geometry here.
        internal static int QueryRaw(PhysicsScene2D scene, in OverlapShape2D shape,
            ContactFilter2D contactFilter, Collider2D[] results)
        {
            switch (shape.Kind)
            {
                case OverlapShapeKind2D.Point:
                    return RawPoint(scene, shape.Center, contactFilter, results);
                case OverlapShapeKind2D.Circle:
                    return RawCircle(scene, shape.Center, shape.Radius, contactFilter, results);
                case OverlapShapeKind2D.Box:
                    return RawBox(scene, shape.Center, shape.Size, shape.Angle, contactFilter, results);
                case OverlapShapeKind2D.Capsule:
                    return RawCapsule(scene, shape.Center, shape.Size, shape.CapsuleDirection, shape.Angle, contactFilter, results);
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        // Raw arguments deliberately bypass the positive-volume factories for legacy serialized geometry.
        internal static int RawPoint(PhysicsScene2D scene, Vector2 point, ContactFilter2D filter, Collider2D[] results) =>
            scene.OverlapPoint(point, filter, results);

        internal static int RawCircle(PhysicsScene2D scene, Vector2 point, float radius,
            ContactFilter2D filter, Collider2D[] results) => scene.OverlapCircle(point, radius, filter, results);

        internal static int RawBox(PhysicsScene2D scene, Vector2 point, Vector2 size, float angle,
            ContactFilter2D filter, Collider2D[] results) => scene.OverlapBox(point, size, angle, filter, results);

        internal static int RawCapsule(PhysicsScene2D scene, Vector2 point, Vector2 size,
            CapsuleDirection2D direction, float angle, ContactFilter2D filter, Collider2D[] results) =>
            scene.OverlapCapsule(point, size, direction, angle, filter, results);

        internal static int RawArea(PhysicsScene2D scene, Vector2 start, Vector2 end,
            ContactFilter2D filter, Collider2D[] results)
        {
            Vector2 min = Vector2.Min(start, end);
            Vector2 max = Vector2.Max(start, end);
            return RawBox(scene, (min + max) * 0.5f, max - min, 0, filter, results);
        }

        internal static int CastRaw(PhysicsScene2D scene, in OverlapShape2D shape, Vector2 direction,
            float distance, ContactFilter2D contactFilter, RaycastHit2D[] results)
        {
            switch (shape.Kind)
            {
                case OverlapShapeKind2D.Point:
                    return scene.Raycast(shape.Center, direction, distance, contactFilter, results);
                case OverlapShapeKind2D.Circle:
                    return scene.CircleCast(shape.Center, shape.Radius, direction, distance, contactFilter, results);
                case OverlapShapeKind2D.Box:
                    return scene.BoxCast(shape.Center, shape.Size, shape.Angle, direction, distance, contactFilter, results);
                case OverlapShapeKind2D.Capsule:
                    return scene.CapsuleCast(shape.Center, shape.Size, shape.CapsuleDirection, shape.Angle,
                        direction, distance, contactFilter, results);
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        private static void ValidateBuffer<T>(T[] results)
        {
            if (results == null)
            {
                throw new ArgumentNullException(nameof(results));
            }
            if (results.Length == 0)
            {
                throw new ArgumentException("A nonempty caller buffer is required.", nameof(results));
            }
        }

        private static Vector2 ValidateCast(Vector2 direction, float distance)
        {
            OverlapShape2D.ValidateVector(direction, nameof(direction));
            if (!OverlapShape2D.IsFinite(distance) || distance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }
            float max = Mathf.Max(Mathf.Abs(direction.x), Mathf.Abs(direction.y));
            if (max == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }
            // Scale first: finite very large/small directions must not overflow/underflow during normalization.
            return new Vector2(direction.x / max, direction.y / max).normalized;
        }

        private static int Identity(Collider2D collider)
        {
            if (!collider)
            {
                return 0;
            }
            var body = collider.attachedRigidbody;
            return body ? body.GetInstanceID() : collider.GetInstanceID();
        }

        private readonly struct ColliderComparer : IComparer<Collider2D>
        {
            private readonly Vector2 _center;
            private readonly bool _byIdentity;

            public ColliderComparer(Vector2 center, bool byIdentity)
            {
                _center = center;
                _byIdentity = byIdentity;
            }

            public int Compare(Collider2D left, Collider2D right)
            {
                if (_byIdentity)
                {
                    int identity = Identity(left).CompareTo(Identity(right));
                    if (identity != 0)
                    {
                        return identity;
                    }
                }
                if (!left || !right)
                {
                    return (left ? 1 : 0).CompareTo(right ? 1 : 0);
                }
                int distance = DistanceSquared(left.bounds.ClosestPoint(_center)).CompareTo(DistanceSquared(right.bounds.ClosestPoint(_center)));
                return distance != 0 ? distance : left.GetInstanceID().CompareTo(right.GetInstanceID());
            }

            private double DistanceSquared(Vector2 point)
            {
                double x = (double)point.x - _center.x;
                double y = (double)point.y - _center.y;
                return x * x + y * y;
            }
        }

        private readonly struct HitComparer : IComparer<RaycastHit2D>
        {
            private readonly bool _byIdentity;

            public HitComparer(bool byIdentity) => _byIdentity = byIdentity;

            public int Compare(RaycastHit2D left, RaycastHit2D right)
            {
                var a = left.collider;
                var b = right.collider;
                if (_byIdentity)
                {
                    int identity = Identity(a).CompareTo(Identity(b));
                    if (identity != 0)
                    {
                        return identity;
                    }
                }
                int distance = left.distance.CompareTo(right.distance);
                return distance != 0 ? distance : (a ? a.GetInstanceID() : 0).CompareTo(b ? b.GetInstanceID() : 0);
            }
        }

        // In-place heapsort bounds sorting/grouping by caller capacity without a shared workspace or comparer boxing.
        private static void Sort<T, TComparer>(T[] results, int count, TComparer comparer)
            where TComparer : struct, IComparer<T>
        {
            for (int start = count / 2 - 1; start >= 0; start--)
            {
                Sift(results, start, count, comparer);
            }
            for (int end = count - 1; end > 0; end--)
            {
                T value = results[0];
                results[0] = results[end];
                results[end] = value;
                Sift(results, 0, end, comparer);
            }
        }

        private static void Sift<T, TComparer>(T[] results, int root, int count, TComparer comparer)
            where TComparer : struct, IComparer<T>
        {
            while (root < count / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < count && comparer.Compare(results[child], results[child + 1]) < 0)
                {
                    child++;
                }
                if (comparer.Compare(results[root], results[child]) >= 0)
                {
                    break;
                }
                T value = results[root];
                results[root] = results[child];
                results[child] = value;
                root = child;
            }
        }
    }
}

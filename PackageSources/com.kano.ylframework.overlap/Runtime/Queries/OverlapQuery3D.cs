using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>Synchronous main-thread queries using caller-owned arrays; no result storage is shared or retained.</summary>
    public static class OverlapQuery3D
    {
        public static OverlapQueryResult3D Query(in OverlapShape3D shape, in OverlapFilter3D filter,
            Collider[] results) => Query(Physics.defaultPhysicsScene, shape, filter, results);

        public static OverlapQueryResult3D Query(PhysicsScene scene, in OverlapShape3D shape,
            in OverlapFilter3D filter, Collider[] results)
        {
            ValidateBuffer(results);
            shape.Validate();
            if (!scene.IsValid())
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult3D(0, 0, OverlapQueryStatus.Invalidated, OverlapQueryInvalidationReason.InvalidScene);
            }
            int rawCount = QueryRaw(scene, shape, filter, results);
            var status = rawCount == results.Length ? OverlapQueryStatus.MayBeTruncated : OverlapQueryStatus.Complete;
            int count = 0;
            for (int i = 0; i < rawCount; i++)
            {
                if (filter.Allows(results[i]))
                {
                    results[count++] = results[i];
                }
            }
            // One native shape returns distinct colliders; only actual rigidbody grouping needs deduplication.
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
            return new OverlapQueryResult3D(count, rawCount, status);
        }

        public static OverlapQueryResult3D Cast(in OverlapShape3D shape, Vector3 direction, float distance,
            in OverlapFilter3D filter, RaycastHit[] results) =>
            Cast(Physics.defaultPhysicsScene, shape, direction, distance, filter, results);

        /// <summary>Preserves native contacts, including native starting-overlap limitations; no contacts are synthesized.</summary>
        public static OverlapQueryResult3D Cast(PhysicsScene scene, in OverlapShape3D shape,
            Vector3 direction, float distance, in OverlapFilter3D filter, RaycastHit[] results)
        {
            ValidateBuffer(results);
            shape.Validate();
            direction = ValidateCast(direction, distance);
            if (!scene.IsValid())
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult3D(0, 0, OverlapQueryStatus.Invalidated, OverlapQueryInvalidationReason.InvalidScene);
            }
            return ProcessHits(CastRaw(scene, shape, direction, distance, filter, results), filter, results);
        }

        public static OverlapQueryResult3D Raycast(Vector3 origin, Vector3 direction, float distance,
            in OverlapFilter3D filter, RaycastHit[] results) =>
            Raycast(Physics.defaultPhysicsScene, origin, direction, distance, filter, results);

        public static OverlapQueryResult3D Raycast(PhysicsScene scene, Vector3 origin, Vector3 direction,
            float distance, in OverlapFilter3D filter, RaycastHit[] results)
        {
            ValidateBuffer(results);
            OverlapShape3D.ValidateVector(origin, nameof(origin));
            direction = ValidateCast(direction, distance);
            if (!scene.IsValid())
            {
                Array.Clear(results, 0, results.Length);
                return new OverlapQueryResult3D(0, 0, OverlapQueryStatus.Invalidated, OverlapQueryInvalidationReason.InvalidScene);
            }
            int rawCount = scene.Raycast(origin, direction, results, distance, filter.LayerMask, filter.TriggerInteraction);
            return ProcessHits(rawCount, filter, results);
        }

        private static OverlapQueryResult3D ProcessHits(int rawCount, in OverlapFilter3D filter, RaycastHit[] results)
        {
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
            return new OverlapQueryResult3D(count, rawCount, status);
        }

        internal static int QueryRaw(PhysicsScene scene, in OverlapShape3D shape,
            in OverlapFilter3D filter, Collider[] results)
        {
            switch (shape.Kind)
            {
                case OverlapShapeKind3D.Sphere:
                    return RawSphere(scene, shape.Center, shape.Radius, results, filter.LayerMask, filter.TriggerInteraction);
                case OverlapShapeKind3D.Box:
                    return RawBox(scene, shape.Center, shape.Size * 0.5f, results, shape.Orientation,
                        filter.LayerMask, filter.TriggerInteraction);
                case OverlapShapeKind3D.Capsule:
                    return RawCapsule(scene, shape.Point1, shape.Point2, shape.Radius, results,
                        filter.LayerMask, filter.TriggerInteraction);
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        internal static int RawSphere(PhysicsScene scene, Vector3 center, float radius, Collider[] results,
            int layerMask, QueryTriggerInteraction triggerInteraction) =>
            scene.OverlapSphere(center, radius, results, layerMask, triggerInteraction);

        internal static int RawBox(PhysicsScene scene, Vector3 center, Vector3 halfExtents, Collider[] results,
            Quaternion orientation, int layerMask, QueryTriggerInteraction triggerInteraction) =>
            scene.OverlapBox(center, halfExtents, results, orientation, layerMask, triggerInteraction);

        internal static int RawCapsule(PhysicsScene scene, Vector3 point1, Vector3 point2, float radius,
            Collider[] results, int layerMask, QueryTriggerInteraction triggerInteraction) =>
            scene.OverlapCapsule(point1, point2, radius, results, layerMask, triggerInteraction);

        internal static int CastRaw(PhysicsScene scene, in OverlapShape3D shape, Vector3 direction,
            float distance, in OverlapFilter3D filter, RaycastHit[] results)
        {
            switch (shape.Kind)
            {
                case OverlapShapeKind3D.Sphere:
                    return scene.SphereCast(shape.Center, shape.Radius, direction, results, distance,
                        filter.LayerMask, filter.TriggerInteraction);
                case OverlapShapeKind3D.Box:
                    return scene.BoxCast(shape.Center, shape.Size * 0.5f, direction, results, shape.Orientation,
                        distance, filter.LayerMask, filter.TriggerInteraction);
                case OverlapShapeKind3D.Capsule:
                    return scene.CapsuleCast(shape.Point1, shape.Point2, shape.Radius, direction, results, distance,
                        filter.LayerMask, filter.TriggerInteraction);
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

        private static Vector3 ValidateCast(Vector3 direction, float distance)
        {
            OverlapShape3D.ValidateVector(direction, nameof(direction));
            if (!OverlapShape3D.IsFinite(distance) || distance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(distance));
            }
            float max = Mathf.Max(Mathf.Abs(direction.x), Mathf.Max(Mathf.Abs(direction.y), Mathf.Abs(direction.z)));
            if (max == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }
            return new Vector3(direction.x / max, direction.y / max, direction.z / max).normalized;
        }

        private static int Identity(Collider collider)
        {
            if (!collider)
            {
                return 0;
            }
            var body = collider.attachedRigidbody;
            return body ? body.GetInstanceID() : collider.GetInstanceID();
        }

        private readonly struct ColliderComparer : IComparer<Collider>
        {
            private readonly Vector3 _center;
            private readonly bool _byIdentity;

            public ColliderComparer(Vector3 center, bool byIdentity)
            {
                _center = center;
                _byIdentity = byIdentity;
            }

            public int Compare(Collider left, Collider right)
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
                // Geometry ClosestPoint rejects nonconvex meshes and terrain; world AABB distance works for every collider.
                int distance = DistanceSquared(left.bounds.ClosestPoint(_center)).CompareTo(DistanceSquared(right.bounds.ClosestPoint(_center)));
                return distance != 0 ? distance : left.GetInstanceID().CompareTo(right.GetInstanceID());
            }

            private double DistanceSquared(Vector3 point)
            {
                double x = (double)point.x - _center.x;
                double y = (double)point.y - _center.y;
                double z = (double)point.z - _center.z;
                return x * x + y * y + z * z;
            }
        }

        private readonly struct HitComparer : IComparer<RaycastHit>
        {
            private readonly bool _byIdentity;

            public HitComparer(bool byIdentity) => _byIdentity = byIdentity;

            public int Compare(RaycastHit left, RaycastHit right)
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

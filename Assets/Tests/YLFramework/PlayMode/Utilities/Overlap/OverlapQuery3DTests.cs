using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Overlap;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class OverlapQuery3DTests
    {
        private Scene _scene;
        private PhysicsScene _physics;

        [SetUp]
        public void CreateScene()
        {
            _scene = SceneManager.CreateScene("YL.Query3D." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            _physics = _scene.GetPhysicsScene();
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
        }

        private GameObject NewObject(Vector3 position, Transform parent = null)
        {
            var obj = new GameObject("YL.Query3D.Target");
            SceneManager.MoveGameObjectToScene(obj, _scene);
            obj.transform.position = position;
            obj.transform.SetParent(parent, true);
            return obj;
        }

        private BoxCollider Target(Vector3 position, Transform parent = null)
        {
            var collider = NewObject(position, parent).AddComponent<BoxCollider>();
            collider.size = Vector3.one * 0.5f;
            return collider;
        }

        private static OverlapFilter3D Filter(QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore,
            Collider ignoredCollider = null, Transform ignoredRoot = null,
            bool groupByRigidbody = false, bool sortByDistance = false)
        {
            return new OverlapFilter3D(1, triggers, ignoredCollider, ignoredRoot, groupByRigidbody, sortByDistance);
        }

        private static OverlapShape3D Shape(int kind, Vector3 center)
        {
            switch (kind)
            {
                case 0:
                    return OverlapShape3D.Sphere(center, 0.5f);
                case 1:
                    return OverlapShape3D.Box(center, new Vector3(1, 2, 1), Quaternion.Euler(0, 0, 37));
                default:
                    return OverlapShape3D.Capsule(center - Vector3.up * 0.5f, center + Vector3.up * 0.5f, 0.5f);
            }
        }

        private int NativeCast(in OverlapShape3D shape, Vector3 direction, float distance, RaycastHit[] results)
        {
            switch (shape.Kind)
            {
                case OverlapShapeKind3D.Sphere:
                    return _physics.SphereCast(shape.Center, shape.Radius, direction, results, distance, 1, QueryTriggerInteraction.Ignore);
                case OverlapShapeKind3D.Box:
                    return _physics.BoxCast(shape.Center, shape.Size * 0.5f, direction, results,
                        shape.Orientation, distance, 1, QueryTriggerInteraction.Ignore);
                default:
                    return _physics.CapsuleCast(shape.Point1, shape.Point2, shape.Radius, direction,
                        results, distance, 1, QueryTriggerInteraction.Ignore);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void EveryShapeQueriesWorldGeometryAndClearsTail(int kind)
        {
            var target = Target(new Vector3(2, 3, 4));
            Target(new Vector3(20, 3, 4));
            var results = new Collider[] { target, target, target, target };
            Physics.SyncTransforms();
            var result = OverlapQuery3D.Query(scene: _physics, shape: Shape(kind, new Vector3(2, 3, 4)),
                filter: Filter(), results: results);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.RawCount, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(results, Is.EqualTo(new Collider[] { target, null, null, null }));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryCastAndRayPreservesNativeVector3HitsAndDistanceOrder(int kind)
        {
            var origin = new Vector3(0, 0, 7);
            var near = Target(origin + Vector3.right * 2);
            var far = Target(origin + Vector3.right * 4);
            var hits = new RaycastHit[8];
            var native = new RaycastHit[8];
            Physics.SyncTransforms();
            int raw = kind == 3
                ? _physics.Raycast(origin, Vector3.right, native, 6, 1, QueryTriggerInteraction.Ignore)
                : NativeCast(Shape(kind, origin), Vector3.right, 6, native);
            var result = kind == 3
                ? OverlapQuery3D.Raycast(scene: _physics, origin: origin, direction: Vector3.right * 7,
                    distance: 6, filter: Filter(sortByDistance: true), results: hits)
                : OverlapQuery3D.Cast(scene: _physics, shape: Shape(kind, origin), direction: Vector3.right * 7,
                    distance: 6, filter: Filter(sortByDistance: true), results: hits);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(hits[0].collider, Is.SameAs(near));
            Assert.That(hits[1].collider, Is.SameAs(far));
            Assert.That(hits[0].distance, Is.GreaterThan(0));
            Assert.That(hits[0].distance, Is.LessThan(hits[1].distance));
            Assert.That(result.Count, Is.EqualTo(raw));
            for (int i = 0; i < raw; i++)
            {
                int index = Array.FindIndex(native, hit => hit.collider == hits[i].collider);
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                Assert.That(hits[i].point, Is.EqualTo(native[index].point));
                Assert.That(hits[i].normal, Is.EqualTo(native[index].normal));
                Assert.That(hits[i].distance, Is.EqualTo(native[index].distance));
            }
            if (kind == 3)
            {
                Assert.That(hits[0].point.z, Is.EqualTo(7).Within(0.001f));
                Assert.That(hits[0].point.x, Is.EqualTo(1.75f).Within(0.001f));
            }
            Assert.That(hits[2].collider, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FilterDoesNotMutateGlobalPhysics(bool globalTriggers)
        {
            bool oldTriggers = Physics.queriesHitTriggers;
            var oldSimulation = Physics.simulationMode;
            bool oldLayer = Physics.GetIgnoreLayerCollision(0, 2);
            try
            {
                Physics.queriesHitTriggers = globalTriggers;
                var solid = Target(Vector3.zero);
                var trigger = Target(Vector3.zero);
                trigger.isTrigger = true;
                var otherLayer = Target(Vector3.zero);
                otherLayer.gameObject.layer = 2;
                var results = new Collider[8];
                var hits = new RaycastHit[8];
                Physics.SyncTransforms();
                var result = OverlapQuery3D.Query(_physics, Shape(0, Vector3.zero), Filter(), results);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(results[0], Is.SameAs(solid));
                result = OverlapQuery3D.Query(_physics, Shape(0, Vector3.zero),
                    Filter(QueryTriggerInteraction.Collide), results);
                Assert.That(result.Count, Is.EqualTo(2));
                Assert.That(new[] { results[0], results[1] }, Is.EquivalentTo(new[] { solid, trigger }));
                result = OverlapQuery3D.Raycast(_physics, Vector3.left * 2, Vector3.right, 4, Filter(), hits);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(hits[0].collider, Is.SameAs(solid));
                result = OverlapQuery3D.Cast(_physics, Shape(0, Vector3.left * 2), Vector3.right, 4,
                    Filter(QueryTriggerInteraction.Collide), hits);
                Assert.That(result.Count, Is.EqualTo(2));
                Assert.That(Physics.queriesHitTriggers, Is.EqualTo(globalTriggers));
                Assert.That(Physics.simulationMode, Is.EqualTo(oldSimulation));
                Assert.That(Physics.GetIgnoreLayerCollision(0, 2), Is.EqualTo(oldLayer));
            }
            finally
            {
                Physics.queriesHitTriggers = oldTriggers;
            }
        }

        [Test]
        public void FullBufferNeverClaimsCompleteAfterRootOrColliderFiltering()
        {
            var root = NewObject(Vector3.zero);
            var self = root.AddComponent<BoxCollider>();
            Target(Vector3.zero, root.transform);
            Physics.SyncTransforms();
            var results = new Collider[2];
            var shape = OverlapShape3D.Sphere(Vector3.zero, 2);
            var result = OverlapQuery3D.Query(_physics, shape, Filter(), results);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            result = OverlapQuery3D.Query(_physics, shape, Filter(ignoredRoot: root.transform), results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(results, Is.EqualTo(new Collider[2]));
            var hits = new RaycastHit[2];
            result = OverlapQuery3D.Cast(_physics, Shape(0, Vector3.left * 3), Vector3.right, 6,
                Filter(ignoredCollider: self), hits);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(hits[1].collider, Is.Null);
            result = OverlapQuery3D.Raycast(_physics, Vector3.left * 3, Vector3.right, 6,
                Filter(ignoredRoot: root.transform), hits);
            Assert.That(result.Count, Is.Zero);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Target(Vector3.zero, root.transform);
            Physics.SyncTransforms();
            result = OverlapQuery3D.Query(_physics, shape, Filter(ignoredRoot: root.transform), results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
        }

        [Test]
        public void BodyGroupingUsesActualBodyAndKeepsBodylessCollidersIndependent()
        {
            var root = NewObject(Vector3.zero);
            root.AddComponent<Rigidbody>().isKinematic = true;
            var far = Target(new Vector3(3, 0), root.transform);
            var near = Target(new Vector3(1, 0), root.transform);
            var independentA = Target(new Vector3(2, 0));
            var independentB = Target(new Vector3(4, 0));
            var results = new Collider[8];
            var hits = new RaycastHit[8];
            Physics.SyncTransforms();
            var shape = OverlapShape3D.Sphere(Vector3.zero, 8);
            var result = OverlapQuery3D.Query(_physics, shape, Filter(), results);
            Assert.That(result.Count, Is.EqualTo(4));
            result = OverlapQuery3D.Query(_physics, shape,
                Filter(groupByRigidbody: true, sortByDistance: true), results);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(results, Is.EqualTo(new Collider[] { near, independentA, independentB, null, null, null, null, null }));
            result = OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, 6,
                Filter(groupByRigidbody: true, sortByDistance: true), hits);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(hits[0].collider, Is.SameAs(near));
            Assert.That(hits[1].collider, Is.SameAs(independentA));
            Assert.That(hits[2].collider, Is.SameAs(independentB));
            result = OverlapQuery3D.Cast(_physics, Shape(0, Vector3.left * 2), Vector3.right, 8,
                Filter(groupByRigidbody: true, sortByDistance: true), hits);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(hits[0].collider, Is.SameAs(near));
            Assert.That(far.attachedRigidbody, Is.SameAs(near.attachedRigidbody));
            result = OverlapQuery3D.Query(_physics, shape, Filter(groupByRigidbody: true), new Collider[4]);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
        }

        [TestCase(0, 0)]
        [TestCase(0, 5)]
        [TestCase(1, 0)]
        [TestCase(1, 5)]
        [TestCase(2, 0)]
        [TestCase(2, 5)]
        [TestCase(3, 0)]
        [TestCase(3, 5)]
        public void CastInitialOverlapMatchesNativeWithoutManufacturedContacts(int kind, float distance)
        {
            Target(Vector3.zero);
            Target(new Vector3(3, 0));
            var hits = new RaycastHit[8];
            var native = new RaycastHit[8];
            Physics.SyncTransforms();
            int raw = kind == 3
                ? _physics.Raycast(Vector3.zero, Vector3.right, native, distance, 1, QueryTriggerInteraction.Ignore)
                : NativeCast(Shape(kind, Vector3.zero), Vector3.right, distance, native);
            var result = kind == 3
                ? OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, distance, Filter(), hits)
                : OverlapQuery3D.Cast(_physics, Shape(kind, Vector3.zero), Vector3.right, distance, Filter(), hits);
            Assert.That(result.Count, Is.EqualTo(raw));
            Assert.That(result.RawCount, Is.EqualTo(raw));
            for (int i = 0; i < raw; i++)
            {
                Assert.That(hits[i].collider, Is.SameAs(native[i].collider));
                Assert.That(hits[i].point, Is.EqualTo(native[i].point));
                Assert.That(hits[i].normal, Is.EqualTo(native[i].normal));
                Assert.That(hits[i].distance, Is.EqualTo(native[i].distance));
            }
            TestContext.WriteLine("Native initial overlap kind=" + kind + " distance=" + distance + " count=" + raw +
                (raw > 0 ? " firstDistance=" + native[0].distance + " firstPoint=" + native[0].point : string.Empty));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OrientedBoxAndCapsuleUseFullDimensionsAndWorldEndpoints(bool capsule)
        {
            var inside = Target(new Vector3(0, 1.5f, 3));
            Target(new Vector3(1.5f, 0, 3));
            var shape = capsule
                ? OverlapShape3D.Capsule(new Vector3(0, -1.5f, 3), new Vector3(0, 1.5f, 3), 0.5f)
                : OverlapShape3D.Box(new Vector3(0, 0, 3), new Vector3(4, 0.5f, 1), Quaternion.Euler(0, 0, 90));
            var results = new Collider[4];
            Physics.SyncTransforms();
            var result = OverlapQuery3D.Query(_physics, shape, Filter(), results);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(results[0], Is.SameAs(inside));
        }

        [Test]
        public void CastValidationLeavesExistingNativeHitUntouchedAndNormalizesFiniteDirections()
        {
            var target = Target(new Vector3(2, 0));
            var shape = Shape(0, Vector3.zero);
            var filter = Filter();
            var hits = new RaycastHit[4];
            Physics.SyncTransforms();
            var snapshot = OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, 4, filter, hits);
            var saved = hits[0];
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Cast(_physics, shape, Vector3.zero, 1, filter, hits));
            Assert.That(hits[0], Is.EqualTo(saved));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Raycast(_physics,
                new Vector3(0, float.NaN, 0), Vector3.right, 1, filter, hits));
            Assert.That(hits[0], Is.EqualTo(saved));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Cast(_physics, default,
                Vector3.right, 1, filter, hits));
            Assert.That(hits[0], Is.EqualTo(saved));
            OverlapQuery3D.Raycast(_physics, Vector3.zero, new Vector3(float.MaxValue, 0, 0), 4, filter, hits);
            Assert.That(hits[0], Is.EqualTo(saved));
            OverlapQuery3D.Raycast(_physics, Vector3.zero, new Vector3(float.Epsilon, 0, 0), 4, filter, hits);
            Assert.That(hits[0], Is.EqualTo(saved));
            Object.DestroyImmediate(target.gameObject);
            Physics.SyncTransforms();
            Assert.That(OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, 4, filter, hits).Count, Is.Zero);
            Assert.That(hits, Is.EqualTo(new RaycastHit[4]));
            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(snapshot.Status, Is.EqualTo(OverlapQueryStatus.Complete));
        }

        [Test]
        public void SameDistanceTiesUseSessionColliderIdAndDestroyedIgnoresAreSafe()
        {
            var first = Target(new Vector3(2, 0));
            var second = Target(new Vector3(2, 0));
            var ignored = Target(new Vector3(10, 0));
            var filter = Filter(ignoredCollider: ignored, ignoredRoot: ignored.transform, sortByDistance: true);
            Object.DestroyImmediate(ignored.gameObject);
            Physics.SyncTransforms();
            var results = new Collider[4];
            var hits = new RaycastHit[4];
            var result = OverlapQuery3D.Query(_physics, OverlapShape3D.Sphere(Vector3.zero, 4), filter, results);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(results[0].GetInstanceID(), Is.EqualTo(Math.Min(first.GetInstanceID(), second.GetInstanceID())));
            result = OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, 4, filter, hits);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(hits[0].collider, Is.SameAs(results[0]));
            Object.DestroyImmediate(first.gameObject);
            Object.DestroyImmediate(second.gameObject);
            result = OverlapQuery3D.Query(_physics, OverlapShape3D.Sphere(Vector3.zero, 4), filter, results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(results, Is.EqualTo(new Collider[4]));
            OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, 4, filter, hits);
            Assert.That(hits[0].collider, Is.Null);
        }

        [Test]
        public void DistanceOrderingSupportsNonconvexMeshColliders()
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(-1, 0, -1), new Vector3(-1, 0, 1),
                    new Vector3(1, 0, 1), new Vector3(1, 0, -1)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            var collider = NewObject(Vector3.zero).AddComponent<MeshCollider>();
            try
            {
                collider.sharedMesh = mesh;
                var far = Target(Vector3.up * 2);
                var results = new Collider[4];
                Physics.SyncTransforms();
                var result = OverlapQuery3D.Query(_physics, OverlapShape3D.Sphere(Vector3.up * 0.25f, 3),
                    Filter(sortByDistance: true), results);
                Assert.That(result.Count, Is.EqualTo(2));
                Assert.That(results[0], Is.SameAs(collider));
                Assert.That(results[1], Is.SameAs(far));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(collider.gameObject);
                Object.DestroyImmediate(mesh);
            }
        }

        [UnityTest]
        public IEnumerator ScenesAndCallerBuffersStayIndependentAndUnloadedSceneInvalidates()
        {
            var target = Target(Vector3.zero);
            var other = SceneManager.CreateScene("YL.Query3D.Other." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            try
            {
                var otherTarget = new GameObject("YL.Query3D.OtherTarget").AddComponent<BoxCollider>();
                SceneManager.MoveGameObjectToScene(otherTarget.gameObject, other);
                var first = new Collider[4];
                var second = new Collider[4];
                var shape = Shape(0, Vector3.zero);
                Physics.SyncTransforms();
                OverlapQuery3D.Query(_physics, shape, Filter(), first);
                var otherPhysics = other.GetPhysicsScene();
                OverlapQuery3D.Query(otherPhysics, shape, Filter(), second);
                Assert.That(first[0], Is.SameAs(target));
                Assert.That(second[0], Is.SameAs(otherTarget));
                var firstHits = new RaycastHit[4];
                var secondHits = new RaycastHit[4];
                OverlapQuery3D.Raycast(_physics, Vector3.left * 2, Vector3.right, 4, Filter(), firstHits);
                OverlapQuery3D.Raycast(otherPhysics, Vector3.left * 2, Vector3.right, 4, Filter(), secondHits);
                Assert.That(firstHits[0].collider, Is.SameAs(target));
                Assert.That(secondHits[0].collider, Is.SameAs(otherTarget));
                yield return SceneManager.UnloadSceneAsync(other);
                var result = OverlapQuery3D.Query(otherPhysics, shape, Filter(), first);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.InvalidScene));
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.Zero);
                Assert.That(first, Is.EqualTo(new Collider[4]));
                result = OverlapQuery3D.Cast(otherPhysics, shape, Vector3.right, 1, Filter(), firstHits);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.InvalidScene));
                Assert.That(firstHits, Is.EqualTo(new RaycastHit[4]));
                result = OverlapQuery3D.Raycast(otherPhysics, Vector3.zero, Vector3.right, 1, Filter(), firstHits);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.InvalidScene));
                var emptyFilter = new OverlapFilter3D(0);
                Assert.That(OverlapQuery3D.Query(shape, emptyFilter, first).Count, Is.Zero);
                Assert.That(OverlapQuery3D.Cast(shape, Vector3.right, 1, emptyFilter, firstHits).Count, Is.Zero);
                Assert.That(OverlapQuery3D.Raycast(Vector3.zero, Vector3.right, 1, emptyFilter, firstHits).Count, Is.Zero);
            }
            finally
            {
                if (other.IsValid() && other.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(other);
                }
            }
        }

        [Test]
        public void InvalidGeometryAndArgumentsAreRejectedBeforeOutputMutation()
        {
            var target = Target(Vector3.zero);
            var results = new Collider[] { target };
            var shape = Shape(0, Vector3.zero);
            var filter = Filter();
            Assert.Throws<ArgumentNullException>(() => OverlapQuery3D.Query(_physics, shape, filter, null));
            Assert.Throws<ArgumentException>(() => OverlapQuery3D.Query(_physics, shape, filter, new Collider[0]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Query(_physics, default, filter, results));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.Sphere(Vector3.zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.Sphere(Vector3.zero, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.Sphere(new Vector3(float.NaN, 0), 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.Box(Vector3.zero, Vector3.zero, Quaternion.identity));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.Box(Vector3.zero, Vector3.one, new Quaternion()));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.Capsule(Vector3.zero, Vector3.one, float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OverlapFilter3D(1, QueryTriggerInteraction.UseGlobal));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OverlapFilter3D(1, (QueryTriggerInteraction)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Cast(_physics, shape, Vector3.zero, 1, filter, new RaycastHit[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Cast(_physics, shape, Vector3.right, -1, filter, new RaycastHit[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Raycast(_physics, Vector3.zero, new Vector3(float.PositiveInfinity, 0), 1, filter, new RaycastHit[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, float.NaN, filter, new RaycastHit[1]));
            Assert.Throws<ArgumentNullException>(() => OverlapQuery3D.Cast(_physics, shape, Vector3.right, 1, filter, null));
            Assert.Throws<ArgumentException>(() => OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, 1, filter, new RaycastHit[0]));
            Assert.That(results[0], Is.SameAs(target));
            Physics.SyncTransforms();
            Assert.DoesNotThrow(() => OverlapQuery3D.Query(_physics,
                OverlapShape3D.Capsule(Vector3.zero, Vector3.zero, 1), filter, results));
            Assert.DoesNotThrow(() => OverlapQuery3D.Raycast(_physics, Vector3.zero, Vector3.right, 0, filter, new RaycastHit[4]));
        }
    }
}

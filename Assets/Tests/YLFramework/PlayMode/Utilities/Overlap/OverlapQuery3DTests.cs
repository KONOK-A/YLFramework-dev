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
        private Scene _gatePrivateScene;
        private Scene _gateSecondScene;

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
            foreach (var scene in new[] { _gatePrivateScene, _gateSecondScene })
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }
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

        [TestCase(false)]
        [TestCase(true)]
        public void InactiveDisabledConvexFirstCookGate(bool defaultScene)
        {
            var scene = defaultScene ? SceneManager.GetActiveScene() : _scene;
            var physics = defaultScene ? Physics.defaultPhysicsScene : _physics;
            var origin = new Vector3(500, 700, 900);
            var owner = new GameObject("YL.ConvexGate.Inactive");
            owner.SetActive(false);
            SceneManager.MoveGameObjectToScene(owner, scene);
            owner.transform.position = origin;
            var mesh = GateMesh();
            var helper = owner.AddComponent<MeshCollider>();
            helper.enabled = false;
            helper.convex = true;
            var target = new GameObject("YL.ConvexGate.Target");
            SceneManager.MoveGameObjectToScene(target, scene);
            target.transform.position = origin;
            // A different, never-disabled collider is the positive engine control, not a warm-up of the helper.
            var controlObject = new GameObject("YL.ConvexGate.ActiveControl");
            SceneManager.MoveGameObjectToScene(controlObject, scene);
            controlObject.transform.position = origin + Vector3.right * 20;
            var controlMesh = GateMesh();
            var control = controlObject.AddComponent<MeshCollider>();
            control.convex = true;
            control.sharedMesh = controlMesh;
            bool triggers = Physics.queriesHitTriggers;
            bool backfaces = Physics.queriesHitBackfaces;
            var simulation = Physics.simulationMode;
            var gravity = Physics.gravity;
            bool layers = Physics.GetIgnoreLayerCollision(0, 2);
            try
            {
                Physics.BakeMesh(mesh.GetInstanceID(), true, helper.cookingOptions);
                helper.sharedMesh = mesh;
                Assert.That(mesh.isReadable && !helper.enabled && !owner.activeInHierarchy, Is.True);
                var box = target.AddComponent<BoxCollider>();
                box.size = Vector3.one * 4;
                bool beforeSync = Physics.ComputePenetration(helper, origin, Quaternion.identity, box,
                    origin, Quaternion.identity, out _, out _);
                bool activeBeforeSync = Physics.ComputePenetration(control, origin, Quaternion.identity, box,
                    origin, Quaternion.identity, out _, out _);
                Physics.SyncTransforms();
                bool afterSync = Physics.ComputePenetration(helper, origin, Quaternion.identity, box,
                    origin, Quaternion.identity, out _, out _);
                bool activeAfterSync = Physics.ComputePenetration(control, origin, Quaternion.identity, box,
                    origin, Quaternion.identity, out _, out _);
                TestContext.WriteLine("ConvexGate initialized control default=" + defaultScene + " inactiveBeforeSync=" +
                    beforeSync + " inactiveAfterSync=" + afterSync + " activeBeforeSync=" + activeBeforeSync +
                    " activeAfterSync=" + activeAfterSync + " helperEnabled=" + helper.enabled + " helperActive=" + owner.activeSelf);
                Assert.That(activeAfterSync, Is.True, "independent active convex vs box control");
                var inactiveHits = new bool[6];
                var activeHits = new bool[6];
                inactiveHits[0] = afterSync;
                activeHits[0] = activeAfterSync;
                box.size = new Vector3(4, 0.2f, 0.2f);
                inactiveHits[1] = Physics.ComputePenetration(helper, origin, Quaternion.identity, box,
                    origin, Quaternion.Euler(0, 30, 0), out _, out _);
                activeHits[1] = Physics.ComputePenetration(control, origin, Quaternion.identity, box,
                    origin, Quaternion.Euler(0, 30, 0), out _, out _);
                Object.DestroyImmediate(box);
                var sphere = target.AddComponent<SphereCollider>();
                sphere.radius = 3;
                inactiveHits[2] = Physics.ComputePenetration(helper, origin, Quaternion.identity, sphere,
                    origin, Quaternion.identity, out _, out _);
                activeHits[2] = Physics.ComputePenetration(control, origin, Quaternion.identity, sphere,
                    origin, Quaternion.identity, out _, out _);
                sphere.radius = 0.5f;
                inactiveHits[3] = Physics.ComputePenetration(helper, origin, Quaternion.identity, sphere,
                    origin + Vector3.right * 0.8f, Quaternion.identity, out _, out _);
                activeHits[3] = Physics.ComputePenetration(control, origin, Quaternion.identity, sphere,
                    origin + Vector3.right * 0.8f, Quaternion.identity, out _, out _);
                Object.DestroyImmediate(sphere);
                var convex = target.AddComponent<MeshCollider>();
                convex.convex = true;
                convex.sharedMesh = controlMesh;
                inactiveHits[4] = Physics.ComputePenetration(helper, origin, Quaternion.identity, convex,
                    origin, Quaternion.identity, out _, out _);
                activeHits[4] = Physics.ComputePenetration(control, origin, Quaternion.identity, convex,
                    origin, Quaternion.identity, out _, out _);
                inactiveHits[5] = Physics.ComputePenetration(helper, origin + Vector3.right * 0.75f, Quaternion.identity,
                    convex, origin, Quaternion.identity, out _, out _);
                activeHits[5] = Physics.ComputePenetration(control, origin + Vector3.right * 0.75f, Quaternion.identity,
                    convex, origin, Quaternion.identity, out _, out _);
                Assert.That(Physics.ComputePenetration(helper, origin + Vector3.right * 10, Quaternion.identity,
                    convex, origin, Quaternion.identity, out _, out _), Is.False, "separation control");
                var buffer = new Collider[16];
                Physics.SyncTransforms();
                int count = physics.OverlapSphere(origin, 3, buffer, ~0, QueryTriggerInteraction.Collide);
                Assert.That(Array.IndexOf(buffer, helper), Is.EqualTo(-1));
                Assert.That(Array.IndexOf(buffer, convex), Is.GreaterThanOrEqualTo(0));
                Assert.That(count, Is.EqualTo(1));
                var observer = target.AddComponent<ConvexGateObserver>();
                var body = target.AddComponent<Rigidbody>();
                body.useGravity = false;
                int controlContacts = -1;
                if (!defaultScene)
                {
                    var contactTarget = new GameObject("YL.ConvexGate.ContactControl");
                    SceneManager.MoveGameObjectToScene(contactTarget, scene);
                    contactTarget.transform.position = controlObject.transform.position;
                    contactTarget.AddComponent<BoxCollider>().size = Vector3.one * 4;
                    var contactObserver = controlObject.AddComponent<ConvexGateObserver>();
                    controlObject.AddComponent<Rigidbody>().useGravity = false;
                    Physics.SyncTransforms();
                    physics.Simulate(0.02f);
                    convex.isTrigger = true;
                    physics.Simulate(0.02f);
                    Assert.That(observer.Contacts, Is.Zero);
                    controlContacts = contactObserver.Contacts;
                    Object.DestroyImmediate(contactTarget);
                    Assert.That(controlContacts, Is.GreaterThan(0), "actual local simulation contact positive control");
                }
                Assert.That(owner.transform.position, Is.EqualTo(origin));
                Assert.That(owner.transform.rotation, Is.EqualTo(Quaternion.identity));
                Assert.That(target.transform.position, Is.EqualTo(origin));
                Assert.That(Physics.queriesHitTriggers, Is.EqualTo(triggers));
                Assert.That(Physics.queriesHitBackfaces, Is.EqualTo(backfaces));
                Assert.That(Physics.simulationMode, Is.EqualTo(simulation));
                Assert.That(Physics.gravity, Is.EqualTo(gravity));
                Assert.That(Physics.GetIgnoreLayerCollision(0, 2), Is.EqualTo(layers));
                TestContext.WriteLine("ConvexGate firstInactive=true disabled=true readable=true default=" + defaultScene +
                    " inactiveHits=" + string.Join(",", inactiveHits) + " activeHits=" + string.Join(",", activeHits) +
                    " negative=true helperInOverlap=false localSimulated=" + !defaultScene + " contacts=" + observer.Contacts +
                    " controlContacts=" + controlContacts);
                CollectionAssert.AreEqual(new[] { true, true, true, true, true, true }, activeHits, "active geometry controls");
                CollectionAssert.AreEqual(new bool[6], inactiveHits, "known unsupported first inactive/disabled route");
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(controlObject);
                Object.DestroyImmediate(owner);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(controlMesh);
            }
        }

        private static Mesh GateMesh() => new Mesh
        {
            vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f)
            },
            triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 }
        };

        [UnityTest]
        public IEnumerator IsolatedSceneEnabledConvexGateAcrossBothTargetWorldsAndAsyncUnload()
        {
            int before = SceneManager.sceneCount;
            var active = SceneManager.GetActiveScene();
            var privateScene = SceneManager.CreateScene("YL.Gate.Private." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var secondScene = SceneManager.CreateScene("YL.Gate.SecondOwner." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            _gatePrivateScene = privateScene;
            _gateSecondScene = secondScene;
            var mesh = GateMesh();
            var targetMesh = GateMesh();
            try
            {
                Assert.That(SceneManager.sceneCount, Is.EqualTo(before + 2));
                Assert.That(privateScene.GetPhysicsScene(), Is.Not.EqualTo(secondScene.GetPhysicsScene()));
                var helperObject = new GameObject("IsolatedConvexGate.Helper");
                helperObject.SetActive(false);
                SceneManager.MoveGameObjectToScene(helperObject, privateScene);
                var helper = helperObject.AddComponent<MeshCollider>();
                helper.convex = true;
                Physics.BakeMesh(mesh.GetInstanceID(), true, helper.cookingOptions);
                helper.sharedMesh = mesh;
                helperObject.SetActive(true);
                var buffer = new Collider[16];
                Physics.SyncTransforms();
                Assert.That(privateScene.GetPhysicsScene().OverlapSphere(Vector3.zero, 2, buffer, ~0, QueryTriggerInteraction.Collide), Is.EqualTo(1));
                Assert.That(buffer[0], Is.SameAs(helper));
                Assert.That(secondScene.GetPhysicsScene().OverlapSphere(Vector3.zero, 2, buffer, ~0, QueryTriggerInteraction.Collide), Is.Zero);
                foreach (bool useDefault in new[] { false, true })
                {
                    var hits = new bool[6];
                    var reversedHits = new bool[6];
                    var scene = useDefault ? active : _scene;
                    var physics = scene.GetPhysicsScene();
                    var origin = new Vector3(321, 654, 987);
                    var target = new GameObject("IsolatedConvexGate.Target");
                    SceneManager.MoveGameObjectToScene(target, scene);
                    target.transform.position = origin;
                    var box = target.AddComponent<BoxCollider>();
                    box.size = Vector3.one * 4;
                    bool triggers = Physics.queriesHitTriggers;
                    var simulation = Physics.simulationMode;
                    var gravity = Physics.gravity;
                    int positives = 0;
                    void Check(Collider collider, Vector3 pose, Quaternion rotation)
                    {
                        hits[positives] = Physics.ComputePenetration(helper, origin, Quaternion.identity, collider,
                            pose, rotation, out _, out _);
                        reversedHits[positives] = Physics.ComputePenetration(collider, pose, rotation, helper,
                            origin, Quaternion.identity, out _, out _);
                        positives++;
                    }
                    try
                    {
                        bool preSync = Physics.ComputePenetration(helper, origin, Quaternion.identity, box,
                            origin, Quaternion.identity, out _, out _);
                        Physics.SyncTransforms();
                        bool postSync = Physics.ComputePenetration(helper, origin, Quaternion.identity, box,
                            origin, Quaternion.identity, out _, out _);
                        helper.sharedMesh = null;
                        helper.sharedMesh = mesh;
                        bool assignedWhileActive = Physics.ComputePenetration(helper, origin, Quaternion.identity, box,
                            origin, Quaternion.identity, out _, out _);
                        bool zeroPoses = Physics.ComputePenetration(helper, Vector3.zero, Quaternion.identity, box,
                            Vector3.zero, Quaternion.identity, out _, out _);
                        TestContext.WriteLine("IsolatedConvexGate firstBox default=" + useDefault + " preSync=" + preSync +
                            " postSync=" + postSync + " assignedWhileActive=" + assignedWhileActive + " zeroPoses=" + zeroPoses);
                        Check(box, origin, Quaternion.identity);
                        box.size = new Vector3(4, 0.2f, 0.2f);
                        Check(box, origin, Quaternion.Euler(0, 30, 0));
                        Object.DestroyImmediate(box);
                        var sphere = target.AddComponent<SphereCollider>();
                        sphere.radius = 3;
                        Check(sphere, origin, Quaternion.identity);
                        sphere.radius = 0.5f;
                        Check(sphere, origin + Vector3.right * 0.8f, Quaternion.identity);
                        Object.DestroyImmediate(sphere);
                        var convex = target.AddComponent<MeshCollider>();
                        convex.convex = true;
                        convex.sharedMesh = targetMesh;
                        Check(convex, origin, Quaternion.identity);
                        Check(convex, origin + Vector3.right * 0.75f, Quaternion.identity);
                        Physics.SyncTransforms();
                        Array.Clear(buffer, 0, buffer.Length);
                        Assert.That(physics.OverlapSphere(origin, 3, buffer, ~0, QueryTriggerInteraction.Collide), Is.EqualTo(1));
                        Assert.That(buffer[0], Is.SameAs(convex));
                        Assert.That(physics.OverlapSphere(Vector3.zero, 2, buffer, ~0, QueryTriggerInteraction.Collide), Is.Zero);
                        var observer = target.AddComponent<ConvexGateObserver>();
                        target.AddComponent<Rigidbody>().useGravity = false;
                        int controlEvents = -1;
                        if (!useDefault)
                        {
                            var control = new GameObject("IsolatedConvexGate.ContactControl");
                            SceneManager.MoveGameObjectToScene(control, scene);
                            control.transform.position = origin + Vector3.right * 20;
                            control.AddComponent<BoxCollider>();
                            var mover = new GameObject("IsolatedConvexGate.ContactMover");
                            SceneManager.MoveGameObjectToScene(mover, scene);
                            mover.transform.position = control.transform.position;
                            mover.AddComponent<BoxCollider>();
                            mover.AddComponent<Rigidbody>().useGravity = false;
                            var contactObserver = mover.AddComponent<ConvexGateObserver>();
                            Physics.SyncTransforms();
                            physics.Simulate(0.02f);
                            convex.isTrigger = true;
                            physics.Simulate(0.02f);
                            controlEvents = contactObserver.Contacts;
                            Object.DestroyImmediate(mover);
                            Object.DestroyImmediate(control);
                            Assert.That(controlEvents, Is.GreaterThan(0));
                        }
                        Assert.That(observer.Contacts, Is.Zero);
                        Assert.That(target.transform.position, Is.EqualTo(origin));
                        Assert.That(helperObject.transform.position, Is.EqualTo(Vector3.zero));
                        Assert.That(Physics.queriesHitTriggers, Is.EqualTo(triggers));
                        Assert.That(Physics.simulationMode, Is.EqualTo(simulation));
                        Assert.That(Physics.gravity, Is.EqualTo(gravity));
                        Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));
                        TestContext.WriteLine("IsolatedConvexGate default=" + useDefault + " hits=" + string.Join(",", hits) +
                            " reversedHits=" + string.Join(",", reversedHits) + " checks=" + positives +
                            " privateOverlap=1 targetOverlap=1 helperInTarget=false targetEvents=0 controlEvents=" + controlEvents);
                        bool supported = Application.unityVersion.StartsWith("2022.3.", StringComparison.Ordinal);
                        CollectionAssert.AreEqual(new[] { supported, supported, supported, supported, supported, supported }, hits,
                            "recorded direct cross-scene limitation; this is not the selected proxy backend");
                    }
                    finally
                    {
                        Object.DestroyImmediate(target);
                    }
                }
                helper.enabled = false;
                Assert.That(privateScene.GetPhysicsScene().OverlapSphere(Vector3.zero, 2, buffer, ~0, QueryTriggerInteraction.Collide), Is.Zero);
            }
            finally
            {
                Object.Destroy(mesh);
                Object.Destroy(targetMesh);
            }
            yield return SceneManager.UnloadSceneAsync(privateScene);
            Assert.That(privateScene.IsValid(), Is.False);
            Assert.That(secondScene.IsValid() && secondScene.isLoaded, Is.True);
            yield return SceneManager.UnloadSceneAsync(secondScene);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(before));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));
        }

        [UnityTest]
        public IEnumerator TargetProxyCopiesPrimitiveHierarchyAndReleasesItsOwnScene()
        {
            int sceneCount = SceneManager.sceneCount;
            var activeScene = SceneManager.GetActiveScene();
            var workspace = new OverlapQueryWorkspace3D();
            var other = new OverlapQueryWorkspace3D();
            var privateScene = workspace.OwnedScene;
            var otherScene = other.OwnedScene;
            try
            {
                Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount + 2));
                Assert.That(privateScene.GetPhysicsScene(), Is.Not.EqualTo(otherScene.GetPhysicsScene()));
                var root = new GameObject("ProxyGate.Root");
                SceneManager.MoveGameObjectToScene(root, _scene);
                root.transform.SetPositionAndRotation(new Vector3(30, 50, 70), Quaternion.Euler(17, 23, 11));
                root.transform.localScale = new Vector3(-1.25f, 0.8f, 1.5f);
                var targetObject = new GameObject("ProxyGate.Target");
                targetObject.transform.SetParent(root.transform, false);
                targetObject.transform.localPosition = new Vector3(0.3f, -0.4f, 0.2f);
                targetObject.transform.localRotation = Quaternion.Euler(12, 31, 19);
                var query = new GameObject("ProxyGate.Control");
                SceneManager.MoveGameObjectToScene(query, _scene);
                var control = query.AddComponent<BoxCollider>();
                control.size = Vector3.one * 0.08f;
                var privateQueryObject = new GameObject("ProxyGate.PrivateControl");
                privateQueryObject.SetActive(false);
                SceneManager.MoveGameObjectToScene(privateQueryObject, privateScene);
                var privateQuery = privateQueryObject.AddComponent<BoxCollider>();
                privateQuery.size = control.size;
                privateQueryObject.SetActive(true);
                int positives = 0, negatives = 0;
                for (int kind = 0; kind < 5; kind++)
                {
                    Collider source;
                    if (kind == 0)
                    {
                        var box = targetObject.AddComponent<BoxCollider>();
                        box.center = new Vector3(0.1f, 0.2f, -0.1f);
                        box.size = new Vector3(1.5f, 2, 1);
                        source = box;
                    }
                    else if (kind == 1)
                    {
                        var sphere = targetObject.AddComponent<SphereCollider>();
                        sphere.center = new Vector3(-0.2f, 0.1f, 0.3f);
                        sphere.radius = 0.75f;
                        source = sphere;
                    }
                    else
                    {
                        var capsule = targetObject.AddComponent<CapsuleCollider>();
                        capsule.center = new Vector3(0.1f, -0.2f, 0.2f);
                        capsule.radius = 0.4f;
                        capsule.height = 2.5f;
                        capsule.direction = kind - 2;
                        source = capsule;
                    }
                    for (int body = 0; body < 3; body++)
                    {
                        var rigidbody = body > 0 ? root.AddComponent<Rigidbody>() : null;
                        if (rigidbody)
                        {
                            rigidbody.isKinematic = body == 1;
                            rigidbody.useGravity = false;
                        }
                        for (int revision = 0; revision < 2; revision++)
                        {
                            root.transform.position += new Vector3(0.2f, -0.1f, 0.3f);
                            targetObject.transform.localPosition += new Vector3(0.05f, 0.01f, -0.02f);
                            if (source is BoxCollider box)
                                box.size += Vector3.one * 0.1f;
                            if (source is SphereCollider sphere)
                                sphere.radius += 0.05f;
                            if (source is CapsuleCollider capsule)
                                capsule.height += 0.1f;
                            Physics.SyncTransforms();
                            var method = typeof(OverlapQueryWorkspace3D).GetMethod("CopyTarget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            object[] arguments = { source, null };
                            Assert.That((OverlapQueryInvalidationReason)method.Invoke(workspace, arguments), Is.EqualTo(OverlapQueryInvalidationReason.None));
                            var proxy = (Collider)arguments[1];
                            Assert.That(proxy.gameObject.scene, Is.EqualTo(privateScene));
                            Assert.That(proxy.attachedRigidbody, Is.Null);
                            Assert.That(proxy.transform.localToWorldMatrix, Is.EqualTo(source.transform.localToWorldMatrix));
                            for (int point = -4; point <= 4; point++)
                            {
                                Vector3 pose = source.bounds.center + new Vector3(point * 0.7f, point * 0.11f, point * -0.17f);
                                bool expected = Physics.ComputePenetration(control, pose, Quaternion.identity, source,
                                    source.transform.position, source.transform.rotation, out _, out _);
                                bool actual = Physics.ComputePenetration(privateQuery, pose, Quaternion.identity, proxy,
                                    proxy.transform.position, proxy.transform.rotation, out _, out _);
                                Assert.That(actual, Is.EqualTo(expected), "kind/body/revision/point=" + kind + "/" + body + "/" + revision + "/" + point);
                                if (expected)
                                    positives++;
                                else
                                    negatives++;
                            }
                            Assert.That(source.gameObject.scene, Is.EqualTo(_scene));
                        }
                        if (rigidbody)
                            Object.DestroyImmediate(rigidbody);
                    }
                    Object.DestroyImmediate(source);
                }
                Assert.That(positives, Is.GreaterThan(0));
                Assert.That(negatives, Is.GreaterThan(0));
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(activeScene));
                TestContext.WriteLine("TargetProxyGate comparisons=" + (positives + negatives) + " positives=" + positives + " negatives=" + negatives);
                Object.DestroyImmediate(query);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(privateQueryObject);
            }
            finally
            {
                workspace.Dispose();
                other.Dispose();
            }
            yield return workspace.ReleaseOperation;
            yield return other.ReleaseOperation;
            Assert.That(privateScene.IsValid() || otherScene.IsValid(), Is.False);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));
        }

        [Test]
        public void SectorConeWorldDescriptorsAndWorkspaceContracts()
        {
            Assert.That((int)OverlapShapeKind3D.Sphere, Is.Zero);
            Assert.That((int)OverlapShapeKind3D.Capsule, Is.EqualTo(2));
            var sector = OverlapShape3D.Sector(Vector3.zero, Quaternion.identity, 3, 270, 2, 1);
            var cone = OverlapShape3D.ConeFromAngle(Vector3.zero, Quaternion.identity, 4, 90);
            Assert.That(cone.Radius, Is.EqualTo(4).Within(0.000001f));
            Assert.That(sector.InnerRadius, Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.ConeFromAngle(Vector3.zero, Quaternion.identity, 2, 180));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape3D.Sector(Vector3.zero, Quaternion.identity, 1, 360, 1, 1));
            var buffer = new Collider[16];
            var noWorkspace = OverlapQuery3D.Query(_physics, sector, new OverlapFilter3D(~0), buffer);
            Assert.That(noWorkspace.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.WorkspaceRequired));
            var hits = new RaycastHit[8];
            Assert.That(OverlapQuery3D.Cast(_physics, cone, Vector3.forward, 3, new OverlapFilter3D(~0), hits).InvalidationReason,
                Is.EqualTo(OverlapQueryInvalidationReason.UnsupportedCast));
            var workspace = new OverlapQueryWorkspace3D();
            try
            {
                var target = new GameObject("SectorContractTarget");
                SceneManager.MoveGameObjectToScene(target, _scene);
                target.transform.position = new Vector3(0, 0, 2);
                target.AddComponent<BoxCollider>().size = Vector3.one * 0.2f;
                Physics.SyncTransforms();
                var result = OverlapQuery3D.Query(_physics, sector, new OverlapFilter3D(~0), buffer, workspace);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
                Assert.That(result.Count, Is.EqualTo(1));
                int cooks = workspace.CookCount;
                var moved = OverlapShape3D.Sector(Vector3.one * 100, Quaternion.Euler(20, 30, 40), 3, 270, 2, 1);
                Assert.That(OverlapQuery3D.Query(_physics, moved, new OverlapFilter3D(~0), buffer, workspace).Count, Is.Zero);
                Assert.That(workspace.CookCount, Is.EqualTo(cooks));
                Assert.That(workspace.GeneratedBoundaryError, Is.LessThanOrEqualTo(workspace.RequestedBoundaryError));
            }
            finally { workspace.Dispose(); }
            Assert.Throws<ObjectDisposedException>(() => OverlapQuery3D.Query(_physics, sector, new OverlapFilter3D(~0), buffer, workspace));
        }

        [TestCase("sector", 0, 0, 0.2f, true)]
        [TestCase("sector", 0, 0, -2, false)]
        [TestCase("sector", 0, 0, 2.94f, true)]
        [TestCase("sector", 0, 0, 3.15f, false)]
        [TestCase("sector", 0, 1.01f, 2, true)]
        [TestCase("sector", 0, 1.15f, 2, false)]
        [TestCase("annular", 0, 0, 0, false)]
        [TestCase("annular", 0, 0, 0.7f, false)]
        [TestCase("annular", 0, 0, 0.98f, true)]
        [TestCase("wide", -2, 0, -0.2f, true)]
        [TestCase("wide", 0, 0, -2, false)]
        [TestCase("ring", -0.0001f, 0, -2, true)]
        [TestCase("ring", 0.0001f, 0, -2, true)]
        [TestCase("ring", 0, 0, 0, false)]
        [TestCase("cone", 0, 0, 0, true)]
        [TestCase("cone", 0, 0, -0.2f, false)]
        [TestCase("cone", 0.5f, 0, 2, true)]
        [TestCase("cone", 1.15f, 0, 2, false)]
        [TestCase("cone", 0, 0, 4.01f, true)]
        [TestCase("cone", 0, 0, 4.15f, false)]
        public void SectorConeActualNativeRegions(string kind, float x, float y, float z, bool expected)
        {
            var shape = kind == "cone" ? OverlapShape3D.Cone(Vector3.zero, 4, 2) :
                OverlapShape3D.Sector(Vector3.zero, 3, kind == "wide" ? 270 : kind == "ring" ? 360 : 90,
                    2, kind == "sector" ? 0 : 1);
            var go = new GameObject("RegionTarget");
            SceneManager.MoveGameObjectToScene(go, _scene);
            go.transform.position = new Vector3(x, y, z);
            go.AddComponent<SphereCollider>().radius = 0.04f;
            Physics.SyncTransforms();
            using var workspace = new OverlapQueryWorkspace3D();
            var result = OverlapQuery3D.Query(_physics, shape, new OverlapFilter3D(~0), new Collider[16], workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(expected ? 1 : 0));
            Assert.That(workspace.GeneratedBoundaryError, Is.LessThanOrEqualTo(0.01f));
        }

        [Test]
        public void SectorConeContainmentCrossingAndFailClosedUnsupportedRawSaturation()
        {
            using var workspace = new OverlapQueryWorkspace3D();
            var ring = OverlapShape3D.Ring(Vector3.zero, 1, 3, 2);
            var go = new GameObject("LargeTarget");
            SceneManager.MoveGameObjectToScene(go, _scene);
            var box = go.AddComponent<BoxCollider>();
            box.size = Vector3.one * 20;
            go.transform.position = Vector3.right * 6;
            Physics.SyncTransforms();
            var buffer = new Collider[16];
            Assert.That(OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), buffer, workspace).Count, Is.EqualTo(1), "whole query contained, target center outside");
            box.size = new Vector3(8, 0.1f, 0.1f);
            go.transform.position = Vector3.zero;
            Physics.SyncTransforms();
            Assert.That(OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), buffer, workspace).Count, Is.EqualTo(1), "cross both hole walls");
            Object.DestroyImmediate(box);
            var sphere = go.AddComponent<SphereCollider>();
            sphere.radius = 0.1f;
            Physics.SyncTransforms();
            var full = OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), new Collider[1], workspace);
            Assert.That(full.Count, Is.Zero);
            Assert.That(full.RawCount, Is.EqualTo(1));
            Assert.That(full.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Object.DestroyImmediate(sphere);
            var mesh = GateMesh();
            var unknown = go.AddComponent<MeshCollider>();
            unknown.convex = true;
            unknown.sharedMesh = mesh;
            Physics.SyncTransforms();
            var invalid = OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), buffer, workspace);
            Assert.That(invalid.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.UnsupportedGeometry), "even unsupported target entirely inside the hole invalidates");
            Assert.That(invalid.Count, Is.Zero);
            Assert.That(invalid.RawCount, Is.EqualTo(1));
            Assert.That(buffer, Is.All.Null);
            Assert.That(OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0, ignoredCollider: unknown), buffer, workspace).Status,
                Is.EqualTo(OverlapQueryStatus.Complete));
            Object.DestroyImmediate(unknown);
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void SectorConePrecisionBudgetsAndLargeConeTetrahedra()
        {
            var shape = OverlapShape3D.Cone(Vector3.zero, 1, 1);
            using var insufficient = new OverlapQueryWorkspace3D(0.0002f, 256, 128);
            var buffer = new Collider[16];
            Assert.That(OverlapQuery3D.Query(_physics, shape, new OverlapFilter3D(~0), buffer, insufficient).InvalidationReason,
                Is.EqualTo(OverlapQueryInvalidationReason.GeometryBudgetExceeded));
            using var sufficient = new OverlapQueryWorkspace3D(0.0002f, 256, 256);
            Assert.That(OverlapQuery3D.Query(_physics, shape, new OverlapFilter3D(~0), buffer, sufficient).Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(sufficient.PartCount, Is.EqualTo(sufficient.SegmentCount).And.GreaterThan(128));
            using var coarseBudget = new OverlapQueryWorkspace3D(0.00001f, 4, 4);
            Assert.That(OverlapQuery3D.Query(_physics, shape, new OverlapFilter3D(~0), buffer, coarseBudget).InvalidationReason,
                Is.EqualTo(OverlapQueryInvalidationReason.GeometryBudgetExceeded));
        }

        [UnityTest]
        public IEnumerator NewGeometryDetectorInvalidationKeepsAccumulatedRawAndPoolOwnership()
        {
            var owner = new GameObject("NewGeometryDetector");
            SceneManager.MoveGameObjectToScene(owner, _scene);
            var detector = owner.AddComponent<OverlapDetector3D>();
            detector.SamplingMode = OverlapSamplingMode.Manual;
            detector.ConfigureCapacity(8, 8);
            detector.ConfigureGeometryWorkspace(0.01f, 128, 128, 64);
            detector.Filter = new OverlapFilter3D(~0);
            detector.Composite = new OverlapComposite3D(OverlapShape3D.Sphere(Vector3.zero, 3), OverlapShape3D.Ring(Vector3.zero, 1, 3, 2));
            var target = new GameObject("SupportedBaseline");
            SceneManager.MoveGameObjectToScene(target, _scene);
            var box = target.AddComponent<BoxCollider>();
            box.size = Vector3.one * 0.2f;
            target.transform.position = Vector3.forward * 2;
            int exits = 0;
            detector.Exited += (_, _) => exits++;
            Physics.SyncTransforms();
            Assert.That(detector.Sample(), Is.True);
            Assert.That(detector.LastComplete.Count, Is.EqualTo(1));
            Assert.That(detector.GeometryPartCount, Is.GreaterThan(0));
            var mesh = GateMesh();
            Object.DestroyImmediate(box);
            var unknown = target.AddComponent<MeshCollider>();
            unknown.convex = true;
            unknown.sharedMesh = mesh;
            Physics.SyncTransforms();
            Assert.That(detector.Sample(), Is.False);
            Assert.That(detector.LastQuery.Count, Is.Zero);
            Assert.That(detector.LastQuery.RawCount, Is.EqualTo(2), "first completed child + failed child raw");
            Assert.That(detector.LastQuery.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.UnsupportedGeometry));
            Assert.That(detector.LastComplete.Count, Is.EqualTo(1));
            Assert.That(exits, Is.Zero);
            detector.Stop();
            yield return null;
            yield return null;
            Assert.That(detector.HasGeometryWorkspace, Is.False);
            Object.DestroyImmediate(unknown);
            target.AddComponent<BoxCollider>();
            detector.StartDetection();
            Physics.SyncTransforms();
            Assert.That(detector.Sample(), Is.True);
            Assert.That(detector.HasGeometryWorkspace, Is.True);
            Object.DestroyImmediate(mesh);
        }

        [TestCase(-1, 1, 1)]
        [TestCase(1, -1, 1)]
        [TestCase(1, 1, -1)]
        [TestCase(-2, -2, -2)]
        public void SectorConeLocalUniformMirrorsAreRealEquivalentVolumes(float x, float y, float z)
        {
            var owner = new GameObject("MirroredGeometry");
            SceneManager.MoveGameObjectToScene(owner, _scene);
            owner.transform.SetPositionAndRotation(new Vector3(5, 7, 9), Quaternion.Euler(23, 37, 11));
            owner.transform.localScale = new Vector3(x, y, z);
            var detector = owner.AddComponent<OverlapDetector3D>();
            detector.SamplingMode = OverlapSamplingMode.Manual;
            detector.Filter = new OverlapFilter3D(1 << 11);
            var target = new GameObject("MirrorTarget") { layer = 11 };
            SceneManager.MoveGameObjectToScene(target, _scene);
            target.AddComponent<SphereCollider>().radius = 0.02f;
            foreach (bool cone in new[] { false, true })
            {
                var shape = cone ? OverlapShape3D.Cone(new Vector3(0.2f, 0.3f, 0.4f), Quaternion.Euler(12, 20, 30), 4, 2) :
                    OverlapShape3D.Sector(new Vector3(0.2f, 0.3f, 0.4f), Quaternion.Euler(12, 20, 30), 3, 90, 1, 0.5f);
                detector.Composite = new OverlapComposite3D(shape);
                target.transform.position = owner.transform.TransformPoint(shape.Center + shape.Orientation * Vector3.forward * 1.5f);
                Physics.SyncTransforms();
                Assert.That(detector.Sample(), Is.True);
                Assert.That(detector.LastComplete.Count, Is.EqualTo(1));
                target.transform.position = owner.transform.TransformPoint(shape.Center - shape.Orientation * Vector3.forward * 1.5f);
                Physics.SyncTransforms();
                Assert.That(detector.Sample(), Is.True);
                Assert.That(detector.LastComplete.Count, Is.Zero);
            }
            owner.transform.localScale = new Vector3(1, 2, 1);
            Assert.That(detector.Sample(), Is.False);
            Assert.That(detector.LastQuery.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.InvalidGeometry));
        }

        [Test]
        public void NewNarrowGroupsAfterGeometryAndConeSortUsesApexNotBroadCenter()
        {
            using var workspace = new OverlapQueryWorkspace3D();
            var root = new GameObject("GroupedTarget");
            SceneManager.MoveGameObjectToScene(root, _scene);
            root.AddComponent<Rigidbody>().isKinematic = true;
            root.AddComponent<SphereCollider>().radius = 0.1f;
            var hit = new GameObject("ActualRingHit");
            hit.transform.SetParent(root.transform, false);
            hit.transform.localPosition = Vector3.right * 2;
            var collider = hit.AddComponent<BoxCollider>();
            collider.size = Vector3.one * 0.1f;
            var buffer = new Collider[8];
            Physics.SyncTransforms();
            var result = OverlapQuery3D.Query(_physics, OverlapShape3D.Ring(Vector3.zero, 1, 3, 2),
                new OverlapFilter3D(~0, groupByRigidbody: true), buffer, workspace);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(buffer[0], Is.SameAs(collider));
            Object.DestroyImmediate(root);
            var near = new GameObject("NearApex");
            SceneManager.MoveGameObjectToScene(near, _scene);
            near.transform.position = Vector3.forward;
            var first = near.AddComponent<SphereCollider>();
            first.radius = 0.05f;
            var far = new GameObject("NearBroadCenter");
            SceneManager.MoveGameObjectToScene(far, _scene);
            far.transform.position = Vector3.forward * 4;
            far.AddComponent<SphereCollider>().radius = 0.05f;
            Physics.SyncTransforms();
            result = OverlapQuery3D.Query(_physics, OverlapShape3D.Cone(Vector3.zero, 10, 4),
                new OverlapFilter3D(~0, sortByDistance: true), buffer, workspace);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(buffer[0], Is.SameAs(first));
        }

        [Test]
        public void WorkspaceRejectsInflightDisposeReentryHierarchyExcessAndFloatDegeneration()
        {
            var workspace = new OverlapQueryWorkspace3D(maxHierarchyDepth: 1);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(OverlapQueryWorkspace3D).GetMethod("Begin", flags).Invoke(workspace, null);
            Assert.Throws<InvalidOperationException>(() => workspace.Dispose());
            var ring = OverlapShape3D.Ring(Vector3.zero, 1, 3, 2);
            Assert.Throws<InvalidOperationException>(() => OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), new Collider[8], workspace));
            var sentinel = new GameObject("ReentrySentinel");
            SceneManager.MoveGameObjectToScene(sentinel, _scene);
            sentinel.transform.position = Vector3.one * 1000;
            var sentinelCollider = sentinel.AddComponent<BoxCollider>();
            var guarded = new[] { (Collider)sentinelCollider };
            Assert.Throws<InvalidOperationException>(() => OverlapQuery3D.Query(default, ring, new OverlapFilter3D(~0), guarded, workspace));
            Assert.That(guarded[0], Is.SameAs(sentinelCollider), "invalid-scene reentry must not clear a borrowed output before the workspace guard");
            typeof(OverlapQueryWorkspace3D).GetMethod("End", flags).Invoke(workspace, null);
            try
            {
                var root = new GameObject("DepthParent");
                SceneManager.MoveGameObjectToScene(root, _scene);
                var child = new GameObject("DepthChild");
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = Vector3.forward * 2;
                child.AddComponent<BoxCollider>();
                Physics.SyncTransforms();
                var result = OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), new Collider[8], workspace);
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.GeometryBudgetExceeded));
                Assert.That(result.RawCount, Is.EqualTo(1));
                result = OverlapQuery3D.Query(_physics, OverlapShape3D.Sector(Vector3.zero, 2, 90, float.Epsilon),
                    new OverlapFilter3D(~0), new Collider[8], workspace);
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.CookingFailed), "positive requested height collapsed to zero float thickness");
                Assert.That(workspace.CookingError, Does.Contain("FloatDegenerateBounds"));
            }
            finally { workspace.Dispose(); }
            Assert.DoesNotThrow(() => workspace.Dispose());
            Assert.Throws<ObjectDisposedException>(() => OverlapQuery3D.Query(default, ring, new OverlapFilter3D(~0), guarded, workspace));
            Assert.That(guarded[0], Is.SameAs(sentinelCollider));
        }

        [Test]
        public void TessellationEnvelopeReallyAdmitsOutsideIdealButRejectsBeyondDeclaredGeometryBand()
        {
            using var workspace = new OverlapQueryWorkspace3D(0.1f);
            var ring = OverlapShape3D.Ring(Vector3.zero, 1, 3, 2);
            var buffer = new Collider[8];
            Assert.That(OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), buffer, workspace).Status, Is.EqualTo(OverlapQueryStatus.Complete));
            float error = workspace.GeneratedBoundaryError;
            Assert.That(error, Is.GreaterThan(0).And.LessThanOrEqualTo(0.1f));
            var target = new GameObject("TessellationBandTarget");
            SceneManager.MoveGameObjectToScene(target, _scene);
            target.AddComponent<SphereCollider>().radius = error * 0.05f;
            target.transform.position = Vector3.back * (3 + error * 0.5f);
            Physics.SyncTransforms();
            Assert.That(OverlapQuery3D.Query(_physics, OverlapShape3D.Sphere(Vector3.zero, 3), new OverlapFilter3D(~0), buffer).Count, Is.Zero,
                "independent ideal-radius broad control excludes this outside-ideal target");
            Assert.That(OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), buffer, workspace).Count, Is.EqualTo(1),
                "conservative shard includes its outside-ideal radial vertex band, so this is not analytic intersection");
            target.transform.position = Vector3.back * (3 + error * 2);
            Physics.SyncTransforms();
            Assert.That(OverlapQuery3D.Query(_physics, ring, new OverlapFilter3D(~0), buffer, workspace).Count, Is.Zero);
        }

        [Test]
        public void NewGeometryUnionOverflowAndInterpolatedBodyAreNeverCompleteMisses()
        {
            var owner = new GameObject("CurvedUnionOverflow");
            SceneManager.MoveGameObjectToScene(owner, _scene);
            var detector = owner.AddComponent<OverlapDetector3D>();
            detector.SamplingMode = OverlapSamplingMode.Manual;
            detector.Filter = new OverlapFilter3D(1 << 11);
            detector.ConfigureCapacity(2, 2);
            var shapes = new[] { OverlapShape3D.Sector(Vector3.left * 4, 1, 360, 1),
                OverlapShape3D.Sector(Vector3.zero, 1, 360, 1), OverlapShape3D.Sector(Vector3.right * 4, 1, 360, 1) };
            detector.Composite = new OverlapComposite3D(shapes[0]);
            var targets = new GameObject[3];
            for (int i = 0; i < 3; i++)
            {
                targets[i] = new GameObject("UnionTarget" + i) { layer = 11 };
                SceneManager.MoveGameObjectToScene(targets[i], _scene);
                targets[i].transform.position = shapes[i].Center;
                targets[i].AddComponent<SphereCollider>().radius = 0.1f;
            }
            Physics.SyncTransforms();
            Assert.That(detector.Sample(), Is.True);
            detector.Composite = new OverlapComposite3D(shapes);
            Assert.That(detector.Sample(), Is.False);
            Assert.That(detector.LastQuery.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(detector.LastQuery.RawCount, Is.EqualTo(3), "each child raw=1 Complete, only unique union overflowed");
            Assert.That(detector.LastComplete.Count, Is.EqualTo(1));
            using var workspace = new OverlapQueryWorkspace3D();
            targets[0].AddComponent<Rigidbody>().interpolation = RigidbodyInterpolation.Interpolate;
            Physics.SyncTransforms();
            var result = OverlapQuery3D.Query(_physics, shapes[0], new OverlapFilter3D(1 << 11), new Collider[8], workspace);
            Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.UnsupportedGeometry),
                "render-interpolated target poses are not certified by the current physics-snapshot proxy contract");
            Assert.That(result.RawCount, Is.EqualTo(1));
        }

        [Test]
        public void FailedDifferentGeometryCannotPoisonPreviouslyUsedBroadRadius()
        {
            var go = new GameObject("CacheRecoveryTarget");
            SceneManager.MoveGameObjectToScene(go, _scene);
            go.transform.position = Vector3.forward * 2;
            go.AddComponent<SphereCollider>().radius = 0.05f;
            Physics.SyncTransforms();
            using var workspace = new OverlapQueryWorkspace3D(maxParts: 8);
            var buffer = new Collider[8];
            var valid = OverlapShape3D.Sector(Vector3.zero, 3, 45, 1);
            Assert.That(OverlapQuery3D.Query(_physics, valid, new OverlapFilter3D(~0), buffer, workspace).Count, Is.EqualTo(1));
            Assert.That(OverlapQuery3D.Query(_physics, OverlapShape3D.Ring(Vector3.zero, 0.1f, 0.5f, 1), new OverlapFilter3D(~0), buffer, workspace).InvalidationReason,
                Is.EqualTo(OverlapQueryInvalidationReason.GeometryBudgetExceeded));
            Assert.That(OverlapQuery3D.Query(_physics, valid, new OverlapFilter3D(~0), buffer, workspace).Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CurvedCloneOwnersAndSceneRetirementNeverShareOrStrandCookedResources()
        {
            int before = SceneManager.sceneCount;
            var owner = new GameObject("CurvedCloneTemplate");
            owner.SetActive(false);
            SceneManager.MoveGameObjectToScene(owner, _scene);
            var first = owner.AddComponent<OverlapDetector3D>();
            first.Composite = new OverlapComposite3D(OverlapShape3D.Ring(Vector3.zero, 1, 3, 2));
            first.Filter = new OverlapFilter3D(1 << 11);
            first.SamplingMode = OverlapSamplingMode.Manual;
            var clone = Object.Instantiate(owner);
            SceneManager.MoveGameObjectToScene(clone, _scene);
            clone.transform.position = Vector3.right * 10;
            var second = clone.GetComponent<OverlapDetector3D>();
            var field = typeof(OverlapDetector3D).GetField("_workspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            OverlapQueryWorkspace3D Workspace(OverlapDetector3D detector) => (OverlapQueryWorkspace3D)field.GetValue(detector);
            void Target(Scene scene, Vector3 position)
            {
                var target = new GameObject("CloneTarget") { layer = 11 };
                SceneManager.MoveGameObjectToScene(target, scene);
                target.transform.position = position;
                target.AddComponent<SphereCollider>().radius = 0.1f;
            }
            Target(_scene, Vector3.forward * 2);
            Target(_scene, Vector3.right * 10 + Vector3.forward * 2);
            owner.SetActive(true);
            clone.SetActive(true);
            var destination = default(Scene);
            try
            {
                Physics.SyncTransforms();
                Assert.That(first.Sample() && second.Sample(), Is.True);
                Assert.That(first.LastComplete.Count, Is.EqualTo(1));
                Assert.That(second.LastComplete.Count, Is.EqualTo(1));
                var firstWorkspace = Workspace(first);
                var secondWorkspace = Workspace(second);
                var firstScene = firstWorkspace.OwnedScene;
                var secondScene = secondWorkspace.OwnedScene;
                Assert.That(firstScene, Is.Not.EqualTo(secondScene));
                int secondCooks = secondWorkspace.CookCount;
                first.Stop();
                yield return firstWorkspace.ReleaseOperation;
                Assert.That(firstScene.IsValid(), Is.False);
                Assert.That(secondScene.IsValid(), Is.True);
                Assert.That(second.Sample(), Is.True, "disposing one clone must not destroy another clone's mesh");
                Assert.That(secondWorkspace.CookCount, Is.EqualTo(secondCooks));
                first.StartDetection();
                Assert.That(first.Sample(), Is.True);
                Assert.That(first.LastComplete.Count, Is.EqualTo(1), "restart baseline before retirement");
                var restarted = Workspace(first);
                Assert.That(restarted, Is.Not.SameAs(firstWorkspace));
                int retirementExits = 0;
                string reasons = string.Empty;
                first.Exited += (_, reason) => { reasons += reason + ";"; retirementExits++; };
                destination = SceneManager.CreateScene("OwnedCurvedCloneDestination", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                SceneManager.MoveGameObjectToScene(owner, destination);
                Target(destination, Vector3.forward * 2);
                Physics.SyncTransforms();
                Assert.That(first.Sample(), Is.True);
                TestContext.WriteLine("Curved scene retirement reasons=" + reasons + " raw=" + first.LastQuery.RawCount + " oldDisposed=" + restarted.IsDisposed);
                Assert.That(retirementExits, Is.EqualTo(1));
                Assert.That(reasons, Is.EqualTo("Disabled;"), "actual MoveGameObjectToScene retires through OnDisable before sampling");
                Assert.That(first.LastComplete.Count, Is.EqualTo(1));
                Assert.That(restarted.IsDisposed, Is.True);
                var moved = Workspace(first);
                Assert.That(moved, Is.Not.SameAs(restarted));
                yield return restarted.ReleaseOperation;
                Assert.That(restarted.OwnedScene.IsValid(), Is.False);
                Assert.That(second.Sample(), Is.True);
                first.Stop();
                second.Stop();
                yield return moved.ReleaseOperation;
                yield return secondWorkspace.ReleaseOperation;
                Assert.That(moved.OwnedScene.IsValid() || secondScene.IsValid(), Is.False);
            }
            finally
            {
                if (first)
                    first.Stop();
                if (second)
                    second.Stop();
                if (destination.IsValid() && destination.isLoaded)
                    SceneManager.UnloadSceneAsync(destination);
            }
            yield return null;
            yield return null;
            Assert.That(SceneManager.sceneCount, Is.EqualTo(before));
        }
        [Test]
        public void ConeBroadFloatMidpointMustNotHideAnActualNativeTipIntersection()
        {
            var apex = new Vector3(0, 0, 16777216);
            var cone = OverlapShape3D.Cone(apex, Quaternion.identity, 3, 2);
            var target = new GameObject("Fix1NativeTip") { layer = 11 };
            SceneManager.MoveGameObjectToScene(target, _scene);
            target.transform.position = apex;
            var sphere = target.AddComponent<SphereCollider>();
            sphere.radius = 0.1f;
            Physics.SyncTransforms();
            using var workspace = new OverlapQueryWorkspace3D();
            var buffer = new Collider[16];
            var result = OverlapQuery3D.Query(_physics, cone, new OverlapFilter3D(1 << 11), buffer, workspace);
            var method = typeof(OverlapQueryWorkspace3D).GetMethod("Intersects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object[] args = { sphere, cone, false };
            Assert.That((OverlapQueryInvalidationReason)method.Invoke(workspace, args), Is.EqualTo(OverlapQueryInvalidationReason.None));
            Assert.That((bool)args[2], Is.True, "actual same-private-world generated cone/proxy penetration positive control");
            Assert.That(OverlapQuery3D.Query(_physics, OverlapShape3D.Sphere(apex, 5), new OverlapFilter3D(1 << 11), buffer).Count, Is.EqualTo(1),
                "independent native apex-centered candidate control");
            var old = OverlapShape3D.Box(apex + Vector3.forward * 1.5f, new Vector3(5, 5, 3), Quaternion.identity);
            int oldCount = OverlapQuery3D.Query(_physics, old, new OverlapFilter3D(1 << 11), buffer).Count;
            TestContext.WriteLine("FIX1 I1 native: apexZ=" + apex.z.ToString("R") + " oldCenterZ=" + old.Center.z.ToString("R") +
                " oldCount=" + oldCount + " privatePenetration=" + args[2] + " result=" + result.Status + "/" + result.Count + "/" + result.RawCount);
            Assert.That(oldCount, Is.Zero, "prove the old finite native broad-box miss, not merely a static volume derivation");
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(1), "strict native penetration must reach narrow phase rather than Complete/0");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void ConeBroadCertifiedEnvelopeHandlesBothRoundingDirectionsAndRotatedPoses(int variant)
        {
            var apex = variant == 1 ? new Vector3(0, 0, -16777216) : variant == 2 || variant == 3 ? Vector3.one * 16777216 :
                variant >= 4 ? new Vector3(1, 2, 3) : new Vector3(0, 0, 16777216);
            var rotation = variant == 2 || variant >= 4 ? Quaternion.Euler(15, 45, 12) : Quaternion.identity;
            float height = variant == 0 ? 1.5f : variant == 3 ? 5 : 3;
            var cone = OverlapShape3D.Cone(apex, rotation, height, 2);
            var target = new GameObject("Fix1PoseTarget") { layer = 11 };
            SceneManager.MoveGameObjectToScene(target, _scene);
            float distance = variant == 0 ? height : variant == 3 ? 4 : variant == 4 ? height : variant == 5 ? height + 0.5f : variant == 6 ? -0.5f : 0;
            target.transform.position = apex + rotation * Vector3.forward * distance;
            var sphere = target.AddComponent<SphereCollider>();
            sphere.radius = variant == 0 ? 0.6f : 0.1f;
            Physics.SyncTransforms();
            using var workspace = new OverlapQueryWorkspace3D();
            var result = OverlapQuery3D.Query(_physics, cone, new OverlapFilter3D(1 << 11), new Collider[16], workspace);
            var intersect = typeof(OverlapQueryWorkspace3D).GetMethod("Intersects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object[] args = { sphere, cone, false };
            Assert.That((OverlapQueryInvalidationReason)intersect.Invoke(workspace, args), Is.EqualTo(OverlapQueryInvalidationReason.None));
            bool expected = variant != 5 && variant != 6;
            Assert.That((bool)args[2], Is.EqualTo(expected), "independent same-world native positive/negative geometry precondition");
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(expected ? 1 : 0));
            var broad = workspace.BroadShape;
            Assert.That(broad.Center, Is.EqualTo(apex));
            Assert.That(broad.Orientation, Is.EqualTo(Quaternion.identity));
            var meshes = (System.Collections.Generic.List<Mesh>)typeof(OverlapQueryWorkspace3D).GetField("_meshes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(workspace);
            foreach (var mesh in meshes)
            {
                foreach (var vertex in mesh.vertices)
                {
                    var relative = rotation * vertex;
                    Assert.That(Math.Abs(relative.x), Is.LessThanOrEqualTo(broad.Size.x * 0.5f));
                    Assert.That(Math.Abs(relative.y), Is.LessThanOrEqualTo(broad.Size.y * 0.5f));
                    Assert.That(Math.Abs(relative.z), Is.LessThanOrEqualTo(broad.Size.z * 0.5f));
                }
            }
            int cooks = workspace.CookCount;
            var moved = OverlapShape3D.Cone(apex + Vector3.up * 100, Quaternion.Euler(31, -73, 21), height, 2);
            OverlapQuery3D.Query(_physics, moved, new OverlapFilter3D(1 << 11), new Collider[16], workspace);
            Assert.That(workspace.CookCount, Is.EqualTo(cooks), "broad certification must not recook pose-only changes");
            TestContext.WriteLine("FIX1 I1 pose=" + variant + " midpointShift=" + ((apex + rotation * Vector3.forward * (height * 0.5f)) - apex) +
                " privatePenetration=" + expected + " query=" + result.Status + "/" + result.Count + "/" + result.RawCount + " half=" + broad.Size * 0.5f);
        }

        [Test]
        public void ConeUnrepresentableCertifiedBroadFailsBeforeNativeCooking()
        {
            using var workspace = new OverlapQueryWorkspace3D();
            var result = OverlapQuery3D.Query(_physics, OverlapShape3D.Cone(Vector3.zero, float.MaxValue, 2),
                new OverlapFilter3D(1 << 11), new Collider[8], workspace);
            Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.InvalidGeometry));
            Assert.That(result.Count + result.RawCount + workspace.CookCount, Is.Zero);
        }
    }

    public sealed class ConvexGateObserver : MonoBehaviour
    {
        public int Contacts;
        private void OnCollisionEnter(Collision collision) => Contacts++;
        private void OnTriggerEnter(Collider other) => Contacts++;
    }
}

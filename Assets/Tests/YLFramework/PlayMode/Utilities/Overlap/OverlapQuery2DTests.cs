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
    public class OverlapQuery2DTests
    {
        private Scene _scene;
        private PhysicsScene2D _physics;

        [SetUp]
        public void CreateScene()
        {
            _scene = SceneManager.CreateScene("YL.Query2D." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            _physics = _scene.GetPhysicsScene2D();
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
            var obj = new GameObject("YL.Query2D.Target");
            SceneManager.MoveGameObjectToScene(obj, _scene);
            obj.transform.position = position;
            obj.transform.SetParent(parent, true);
            return obj;
        }

        private BoxCollider2D Target(Vector3 position, Transform parent = null)
        {
            var collider = NewObject(position, parent).AddComponent<BoxCollider2D>();
            collider.size = Vector2.one * 0.5f;
            return collider;
        }

        private static OverlapFilter2D Filter(bool triggers = false, Collider2D ignoredCollider = null,
            Transform ignoredRoot = null, bool groupByRigidbody = false, bool sortByDistance = false)
        {
            var contact = new ContactFilter2D { useTriggers = triggers };
            contact.SetLayerMask(1);
            return new OverlapFilter2D(contact, ignoredCollider, ignoredRoot, groupByRigidbody, sortByDistance);
        }

        private static OverlapShape2D Shape(int kind, Vector2 center)
        {
            switch (kind)
            {
                case 0:
                    return OverlapShape2D.Point(center);
                case 1:
                    return OverlapShape2D.Circle(center, 0.5f);
                case 2:
                    return OverlapShape2D.Box(center, new Vector2(1, 2), 37);
                default:
                    return OverlapShape2D.Capsule(center, new Vector2(1, 2), CapsuleDirection2D.Vertical, 37);
            }
        }

        private int NativeCast(in OverlapShape2D shape, Vector2 direction, float distance,
            ContactFilter2D contact, RaycastHit2D[] results)
        {
            switch (shape.Kind)
            {
                case OverlapShapeKind2D.Point:
                    return _physics.Raycast(shape.Center, direction, distance, contact, results);
                case OverlapShapeKind2D.Circle:
                    return _physics.CircleCast(shape.Center, shape.Radius, direction, distance, contact, results);
                case OverlapShapeKind2D.Box:
                    return _physics.BoxCast(shape.Center, shape.Size, shape.Angle, direction, distance, contact, results);
                default:
                    return _physics.CapsuleCast(shape.Center, shape.Size, shape.CapsuleDirection,
                        shape.Angle, direction, distance, contact, results);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryShapeQueriesWorldGeometryAndClearsTail(int kind)
        {
            var target = Target(new Vector3(2, 3));
            Target(new Vector3(20, 3));
            var results = new Collider2D[] { target, target, target, target };
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(scene: _physics, shape: Shape(kind, new Vector2(2, 3)),
                filter: Filter(), results: results);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.RawCount, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(results, Is.EqualTo(new Collider2D[] { target, null, null, null }));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryShapeCastsNativeHitsInDistanceOrder(int kind)
        {
            var near = Target(new Vector3(2, 0));
            var far = Target(new Vector3(4, 0));
            var results = new RaycastHit2D[8];
            var native = new RaycastHit2D[8];
            var shape = Shape(kind, Vector2.zero);
            Physics2D.SyncTransforms();
            int raw = NativeCast(shape, Vector2.right, 6, Filter().ContactFilter, native);
            var result = OverlapQuery2D.Cast(scene: _physics, shape: shape,
                direction: Vector2.right * 7, distance: 6, filter: Filter(sortByDistance: true), results: results);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(results[0].collider, Is.SameAs(near));
            Assert.That(results[1].collider, Is.SameAs(far));
            Assert.That(results[0].distance, Is.GreaterThan(0));
            Assert.That(results[0].distance, Is.LessThan(results[1].distance));
            Assert.That(results[0].fraction, Is.EqualTo(results[0].distance / 6).Within(0.0001f));
            Assert.That(result.Count, Is.EqualTo(raw));
            for (int i = 0; i < raw; i++)
            {
                Assert.That(results[i].point, Is.EqualTo(native[i].point));
                Assert.That(results[i].normal, Is.EqualTo(native[i].normal));
                Assert.That(results[i].distance, Is.EqualTo(native[i].distance));
                Assert.That(results[i].fraction, Is.EqualTo(native[i].fraction));
            }
            Assert.That(results[2].collider, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FilterDoesNotMutateGlobalPhysics(bool globalTriggers)
        {
            bool oldTriggers = Physics2D.queriesHitTriggers;
            bool oldStart = Physics2D.queriesStartInColliders;
            var oldSimulation = Physics2D.simulationMode;
            bool oldLayer = Physics2D.GetIgnoreLayerCollision(0, 2);
            try
            {
                var solid = Target(Vector3.zero);
                var trigger = Target(Vector3.zero);
                trigger.isTrigger = true;
                var otherLayer = Target(Vector3.zero);
                otherLayer.gameObject.layer = 2;
                var otherDepth = Target(new Vector3(0, 0, 10));
                Physics2D.queriesHitTriggers = globalTriggers;
                var contact = new ContactFilter2D { useTriggers = true };
                contact.SetLayerMask(1);
                contact.SetDepth(-1, 1);
                var results = new Collider2D[8];
                var hits = new RaycastHit2D[8];
                Physics2D.SyncTransforms();
                int nativeCount = _physics.OverlapCircle(Vector2.zero, 1, contact, results);
                TestContext.WriteLine("Explicit trigger filter global=" + globalTriggers + " useTriggers=" + contact.useTriggers +
                    " nativeCount=" + nativeCount);
                var noDepth = contact;
                noDepth.useDepth = false;
                TestContext.WriteLine("Native without depth=" + _physics.OverlapCircle(Vector2.zero, 1, noDepth, results) +
                    " ray=" + _physics.Raycast(Vector2.left * 2, Vector2.right, 4, contact, hits) +
                    " point=" + _physics.OverlapPoint(Vector2.zero, contact, results) +
                    " trigger=" + trigger.isTrigger + " active=" + trigger.enabled);
                _physics.Simulate(0.02f);
                TestContext.WriteLine("Native after local simulation=" + _physics.OverlapCircle(Vector2.zero, 1, contact, results));
                var broad = new ContactFilter2D
                {
                    useTriggers = true,
                    minDepth = float.NegativeInfinity,
                    maxDepth = float.PositiveInfinity,
                    maxNormalAngle = 359.9999f
                };
                TestContext.WriteLine("Native broad filter=" + _physics.OverlapCircle(Vector2.zero, 1, broad, results));
                var defaultScene = SceneManager.GetActiveScene();
                var defaultTrigger = new GameObject("YL.Query2D.DefaultTrigger").AddComponent<BoxCollider2D>();
                try
                {
                    defaultTrigger.transform.position = new Vector3(21000, 21000);
                    defaultTrigger.isTrigger = true;
                    Physics2D.SyncTransforms();
                    TestContext.WriteLine("Native default scene trigger=" + Physics2D.defaultPhysicsScene.OverlapPoint(
                        new Vector2(21000, 21000), broad, results) + " scene=" + defaultScene.name);
                }
                finally
                {
                    Object.DestroyImmediate(defaultTrigger.gameObject);
                }
                Assert.That(contact.useTriggers, Is.True, "The input to the query must explicitly include triggers.");
                Assert.That(nativeCount, Is.EqualTo(globalTriggers ? 2 : 1), "Record the supported engines' native global-trigger prerequisite independently of the wrapper.");
                var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 1),
                    new OverlapFilter2D(contact), results);
                if (globalTriggers)
                {
                    Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
                    Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.None));
                    Assert.That(result.Count, Is.EqualTo(2));
                    Assert.That(new[] { results[0], results[1] }, Is.EquivalentTo(new[] { solid, trigger }));
                }
                else
                {
                    Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                    Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.TriggerQueriesDisabled));
                    Assert.That(result.Count, Is.Zero);
                    Assert.That(result.RawCount, Is.Zero);
                    Assert.That(results, Is.EqualTo(new Collider2D[8]));
                }
                contact.useTriggers = false;
                result = OverlapQuery2D.Query(_physics, OverlapShape2D.Point(Vector2.zero),
                    new OverlapFilter2D(contact), results);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(results[0], Is.SameAs(solid));
                result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4,
                    new OverlapFilter2D(contact), hits);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(hits[0].collider, Is.SameAs(solid));
                Assert.That(Physics2D.queriesHitTriggers, Is.EqualTo(globalTriggers));
                Assert.That(Physics2D.queriesStartInColliders, Is.EqualTo(oldStart));
                Assert.That(Physics2D.simulationMode, Is.EqualTo(oldSimulation));
                Assert.That(Physics2D.GetIgnoreLayerCollision(0, 2), Is.EqualTo(oldLayer));
                Assert.That(otherDepth, Is.Not.Null);
            }
            finally
            {
                Physics2D.queriesHitTriggers = oldTriggers;
            }
        }

        [Test]
        public void FullBufferNeverClaimsCompleteAfterRootOrColliderFiltering()
        {
            var root = NewObject(Vector3.zero);
            var self = root.AddComponent<BoxCollider2D>();
            Target(Vector3.zero, root.transform);
            Physics2D.SyncTransforms();
            var results = new Collider2D[2];
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 2), Filter(), results);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 2),
                Filter(ignoredRoot: root.transform), results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(results, Is.EqualTo(new Collider2D[2]));
            var hits = new RaycastHit2D[2];
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4,
                Filter(ignoredCollider: self), hits);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(hits[1].collider, Is.Null);
            Target(Vector3.zero, root.transform);
            Physics2D.SyncTransforms();
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 2),
                Filter(ignoredRoot: root.transform), results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
        }

        [Test]
        public void GloballySuppressedTriggerRequestIsInvalidatedRatherThanACompleteMiss()
        {
            bool oldTriggers = Physics2D.queriesHitTriggers;
            try
            {
                var trigger = Target(new Vector3(2, 0));
                trigger.isTrigger = true;
                Target(new Vector3(3, 0));
                Physics2D.queriesHitTriggers = false;
                Physics2D.SyncTransforms();
                var results = new Collider2D[] { trigger, trigger, trigger, trigger };
                var shape = OverlapShape2D.Circle(new Vector2(2, 0), 2);
                var result = OverlapQuery2D.Query(_physics, shape, Filter(triggers: true), results);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.TriggerQueriesDisabled));
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.Zero);
                Assert.That(results, Is.EqualTo(new Collider2D[4]));
                var hits = new RaycastHit2D[4];
                OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 5, Filter(), hits);
                Assert.That(hits[0].collider, Is.Not.Null);
                result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 5,
                    Filter(triggers: true), hits);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.Zero);
                Assert.That(hits, Is.EqualTo(new RaycastHit2D[4]));
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.TriggerQueriesDisabled));
                Assert.That(Physics2D.queriesHitTriggers, Is.False);
            }
            finally
            {
                Physics2D.queriesHitTriggers = oldTriggers;
            }
        }

        [Test]
        public void BodyGroupingUsesActualBodyAndKeepsBodylessCollidersIndependent()
        {
            var root = NewObject(Vector3.zero);
            root.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            var far = Target(new Vector3(3, 0), root.transform);
            var near = Target(new Vector3(1, 0), root.transform);
            var independentA = Target(new Vector3(2, 0));
            var independentB = Target(new Vector3(4, 0));
            var results = new Collider2D[8];
            var hits = new RaycastHit2D[8];
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 8), Filter(), results);
            Assert.That(result.Count, Is.EqualTo(4));
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 8),
                Filter(groupByRigidbody: true, sortByDistance: true), results);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(results, Is.EqualTo(new Collider2D[] { near, independentA, independentB, null, null, null, null, null }));
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 6,
                Filter(groupByRigidbody: true, sortByDistance: true), hits);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(hits[0].collider, Is.SameAs(near));
            Assert.That(hits[1].collider, Is.SameAs(independentA));
            Assert.That(hits[2].collider, Is.SameAs(independentB));
            Assert.That(far.attachedRigidbody, Is.SameAs(near.attachedRigidbody));
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 8),
                Filter(groupByRigidbody: true), new Collider2D[4]);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        [TestCase(3, false)]
        [TestCase(3, true)]
        public void CastInitialOverlapAndDistanceOrderFollowsQueriesStartInColliders(int kind, bool startInside)
        {
            bool oldStart = Physics2D.queriesStartInColliders;
            try
            {
                Physics2D.queriesStartInColliders = startInside;
                var inside = Target(Vector3.zero);
                var next = Target(new Vector3(2, 0));
                var hits = new RaycastHit2D[8];
                var native = new RaycastHit2D[8];
                Physics2D.SyncTransforms();
                var contact = Filter().ContactFilter;
                var shape = Shape(kind, Vector2.zero);
                int count = NativeCast(shape, Vector2.right, 4, contact, native);
                var result = OverlapQuery2D.Cast(_physics, shape, Vector2.right, 4,
                    Filter(sortByDistance: true), hits);
                Assert.That(result.Count, Is.EqualTo(count));
                if (kind == 0)
                {
                    Assert.That(result.Count, Is.EqualTo(startInside ? 2 : 1));
                    Assert.That(hits[0].collider, Is.SameAs(startInside ? inside : next));
                }
                for (int i = 0; i < count; i++)
                {
                    Assert.That(hits[i], Is.EqualTo(native[i]));
                }
                count = NativeCast(shape, Vector2.right, 0, contact, native);
                result = OverlapQuery2D.Cast(_physics, shape, Vector2.right, 0, Filter(), hits);
                Assert.That(result.Count, Is.EqualTo(count));
                if (kind == 0)
                {
                    Assert.That(result.Count, Is.EqualTo(startInside ? 1 : 0));
                }
                for (int i = 0; i < count; i++)
                {
                    Assert.That(hits[i], Is.EqualTo(native[i]));
                }
                Assert.That(hits[count], Is.EqualTo(default(RaycastHit2D)));
                TestContext.WriteLine("Native 2D initial kind=" + kind + " startInside=" + startInside +
                    " zeroDistanceCount=" + count);
                Assert.That(Physics2D.queriesStartInColliders, Is.EqualTo(startInside));
            }
            finally
            {
                Physics2D.queriesStartInColliders = oldStart;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OrientedBoxAndCapsuleUseFullDimensionsAndWorldAngle(bool capsule)
        {
            var inside = Target(new Vector3(1.5f, 0));
            Target(new Vector3(0, 1.5f));
            var shape = capsule
                ? OverlapShape2D.Capsule(Vector2.zero, new Vector2(1, 4), CapsuleDirection2D.Vertical, 90)
                : OverlapShape2D.Box(Vector2.zero, new Vector2(0.5f, 4), 90);
            var results = new Collider2D[4];
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(_physics, shape, Filter(), results);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(results[0], Is.SameAs(inside));
            if (capsule)
            {
                shape = OverlapShape2D.Capsule(Vector2.zero, new Vector2(4, 1), CapsuleDirection2D.Horizontal);
                Assert.That(OverlapQuery2D.Query(_physics, shape, Filter(), results).Count, Is.EqualTo(1));
                Assert.That(results[0], Is.SameAs(inside));
            }
        }

        [Test]
        public void MultiplePolygonPathsUseColliderIdentityWithoutMergingBodylessTargets()
        {
            var polygon = NewObject(Vector3.zero).AddComponent<PolygonCollider2D>();
            polygon.pathCount = 2;
            polygon.SetPath(0, new[] { new Vector2(1, -1), new Vector2(2, -1), new Vector2(2, 1), new Vector2(1, 1) });
            polygon.SetPath(1, new[] { new Vector2(3, -1), new Vector2(4, -1), new Vector2(4, 1), new Vector2(3, 1) });
            var independent = Target(new Vector3(5, 0));
            var results = new Collider2D[8];
            var hits = new RaycastHit2D[8];
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Box(new Vector2(3, 0), new Vector2(8, 4)),
                Filter(), results);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(new[] { results[0], results[1] }, Is.EquivalentTo(new Collider2D[] { polygon, independent }));
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 6,
                Filter(sortByDistance: true), hits);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(hits[0].collider, Is.SameAs(polygon));
            Assert.That(hits[1].collider, Is.SameAs(independent));
        }

        [Test]
        public void CastValidationLeavesExistingNativeHitUntouchedAndNormalizesFiniteDirections()
        {
            var target = Target(new Vector3(2, 0));
            var shape = OverlapShape2D.Point(Vector2.zero);
            var filter = Filter();
            var hits = new RaycastHit2D[4];
            Physics2D.SyncTransforms();
            var snapshot = OverlapQuery2D.Cast(_physics, shape, Vector2.right, 4, filter, hits);
            var saved = hits[0];
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.zero, 1, filter, hits));
            Assert.That(hits[0], Is.EqualTo(saved));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, float.PositiveInfinity, filter, hits));
            Assert.That(hits[0], Is.EqualTo(saved));
            OverlapQuery2D.Cast(_physics, shape, new Vector2(float.MaxValue, 0), 4, filter, hits);
            Assert.That(hits[0], Is.EqualTo(saved));
            OverlapQuery2D.Cast(_physics, shape, new Vector2(float.Epsilon, 0), 4, filter, hits);
            Assert.That(hits[0], Is.EqualTo(saved));
            Object.DestroyImmediate(target.gameObject);
            Physics2D.SyncTransforms();
            Assert.That(OverlapQuery2D.Cast(_physics, shape, Vector2.right, 4, filter, hits).Count, Is.Zero);
            Assert.That(hits, Is.EqualTo(new RaycastHit2D[4]));
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
            Physics2D.SyncTransforms();
            var results = new Collider2D[4];
            var hits = new RaycastHit2D[4];
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 4), filter, results);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(results[0].GetInstanceID(), Is.EqualTo(Math.Min(first.GetInstanceID(), second.GetInstanceID())));
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 4, filter, hits);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(hits[0].collider, Is.SameAs(results[0]));
            Object.DestroyImmediate(first.gameObject);
            Object.DestroyImmediate(second.gameObject);
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 4), filter, results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(results, Is.EqualTo(new Collider2D[4]));
            OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 4, filter, hits);
            Assert.That(hits[0].collider, Is.Null);
        }

        [UnityTest]
        public IEnumerator ScenesAndCallerBuffersStayIndependentAndUnloadedSceneInvalidates()
        {
            var target = Target(Vector3.zero);
            var other = SceneManager.CreateScene("YL.Query2D.Other." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            try
            {
                var otherTarget = new GameObject("YL.Query2D.OtherTarget").AddComponent<BoxCollider2D>();
                SceneManager.MoveGameObjectToScene(otherTarget.gameObject, other);
                var first = new Collider2D[4];
                var second = new Collider2D[4];
                var shape = OverlapShape2D.Point(Vector2.zero);
                Physics2D.SyncTransforms();
                OverlapQuery2D.Query(_physics, shape, Filter(), first);
                var otherPhysics = other.GetPhysicsScene2D();
                OverlapQuery2D.Query(otherPhysics, shape, Filter(), second);
                Assert.That(first[0], Is.SameAs(target));
                Assert.That(second[0], Is.SameAs(otherTarget));
                var firstHits = new RaycastHit2D[4];
                var secondHits = new RaycastHit2D[4];
                OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4, Filter(), firstHits);
                OverlapQuery2D.Cast(otherPhysics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4, Filter(), secondHits);
                Assert.That(firstHits[0].collider, Is.SameAs(target));
                Assert.That(secondHits[0].collider, Is.SameAs(otherTarget));
                yield return SceneManager.UnloadSceneAsync(other);
                var result = OverlapQuery2D.Query(otherPhysics, shape, Filter(), first);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.Zero);
                Assert.That(first, Is.EqualTo(new Collider2D[4]));
                result = OverlapQuery2D.Cast(otherPhysics, shape, Vector2.right, 1, Filter(), firstHits);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(firstHits, Is.EqualTo(new RaycastHit2D[4]));
                result = OverlapQuery2D.Query(shape, new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 0 }), first);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
                Assert.That(result.Count, Is.Zero);
                result = OverlapQuery2D.Cast(shape, Vector2.right, 1,
                    new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 0 }), firstHits);
                Assert.That(result.Count, Is.Zero);
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
            var results = new Collider2D[] { target };
            var shape = OverlapShape2D.Point(Vector2.zero);
            var filter = Filter();
            Assert.Throws<ArgumentNullException>(() => OverlapQuery2D.Query(_physics, shape, filter, null));
            Assert.Throws<ArgumentException>(() => OverlapQuery2D.Query(_physics, shape, filter, new Collider2D[0]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Circle(Vector2.zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Circle(Vector2.zero, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Point(new Vector2(float.NaN, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Box(Vector2.zero, Vector2.zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Box(Vector2.zero, Vector2.one, float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Capsule(Vector2.zero, Vector2.one, (CapsuleDirection2D)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Capsule(Vector2.zero, new Vector2(2, 1), CapsuleDirection2D.Vertical));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.zero, 1, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, -1, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, new Vector2(float.PositiveInfinity, 0), 1, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, float.NaN, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentNullException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, 1, filter, null));
            Assert.Throws<ArgumentException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, 1, filter, new RaycastHit2D[0]));
            var contact = new ContactFilter2D { useDepth = true, minDepth = float.NaN, maxDepth = 1 };
            Assert.Throws<ArgumentOutOfRangeException>(() => new OverlapFilter2D(contact));
            Assert.That(results[0], Is.SameAs(target));
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using YLFramework.Overlap;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class OverlapComponentTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();
        private GameObject NewObject(string name, Vector3 position)
        {
            var obj = new GameObject("YL.Behavior." + name);
            obj.transform.position = position;
            _owned.Add(obj);
            return obj;
        }

        private OverlapCircle2D Circle(Vector3 center)
        {
            var circle = NewObject("Query", center).AddComponent<OverlapCircle2D>();
            circle.Radius = 3;
            circle.LayerMask = 1;
            circle.IsRunning = false;
            circle.onColliderEnter = new OverlapEvent2D();
            circle.onColliderExit = new OverlapEvent2D();
            return circle;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var obj in _owned)
            {
                if (obj)
                {
                    Object.DestroyImmediate(obj);
                }
            }

            _owned.Clear();
        }

        [Test]
        public void ResultsEnterExitAndCrossInstanceCallbackQueriesRemainIsolated()
        {
            var center = new Vector3(12000, 12000);
            var first = Circle(center);
            var second = Circle(center + Vector3.right * 20);
            var firstCollider = NewObject("First", center).AddComponent<BoxCollider2D>();
            var secondCollider = NewObject("Second", second.transform.position).AddComponent<BoxCollider2D>();
            int enters = 0, exits = 0;
            first.onColliderEnter.AddListener(hit =>
            {
                enters++;
                first.CheckOverlap();
                second.CheckOverlap();
            });
            first.onColliderExit.AddListener(hit => exits++);
            Physics2D.SyncTransforms();
            first.CheckOverlap();
            Assert.That(first.results.Count, Is.EqualTo(1));
            Assert.That(first.results[0].collider, Is.SameAs(firstCollider));
            Assert.That(second.results[0].collider, Is.SameAs(secondCollider));
            first.CheckOverlap();
            Assert.That(enters, Is.EqualTo(1));
            firstCollider.transform.position += Vector3.up * 20;
            Physics2D.SyncTransforms();
            first.CheckOverlap();
            Assert.That(first.results, Is.Empty);
            Assert.That(exits, Is.EqualTo(1));
        }

        [Test]
        public void DisableDuringEnterClearsSnapshotAndEnableStartsFresh()
        {
            var center = new Vector3(12000, 12100);
            var circle = Circle(center);
            NewObject("TargetA", center).AddComponent<BoxCollider2D>();
            NewObject("TargetB", center + Vector3.right).AddComponent<BoxCollider2D>();
            int enters = 0;
            circle.onColliderEnter.AddListener(hit =>
            {
                enters++;
                circle.enabled = false;
            });
            Physics2D.SyncTransforms();
            circle.CheckOverlap();
            Assert.That(enters, Is.EqualTo(1));
            Assert.That(circle.results, Is.Empty);
            circle.onColliderEnter.RemoveAllListeners();
            circle.enabled = true;
            circle.CheckOverlap();
            Assert.That(circle.results.Count, Is.EqualTo(2));
        }

        [Test]
        public void DestroyedColliderAndEmptyAreaEmitExitWithoutStaleResults()
        {
            var center = new Vector3(12000, 12200);
            var area = NewObject("Area", center).AddComponent<OverlapArea2D>();
            area.IsRunning = false;
            area.LayerMask = 1;
            area.Positions.Add(new Vector3(-2, -2));
            area.Positions.Add(new Vector3(2, 2));
            var collider = NewObject("Target", center).AddComponent<BoxCollider2D>();
            int exits = 0;
            area.onColliderExit = new OverlapEvent2D();
            area.onColliderExit.AddListener(hit => exits++);
            Physics2D.SyncTransforms();
            area.CheckOverlap();
            Assert.That(area.results.Count, Is.EqualTo(1));
            Object.DestroyImmediate(collider);
            area.Positions.Clear();
            area.CheckOverlap();
            Assert.That(area.results, Is.Empty);
            Assert.That(exits, Is.EqualTo(1));
        }

        [TestCase(typeof(OverlapPoint2D))]
        [TestCase(typeof(OverlapCircle2D))]
        [TestCase(typeof(OverlapBox2D))]
        [TestCase(typeof(OverlapCapsule2D))]
        [TestCase(typeof(OverlapArea2D))]
        public void Shapes2DUseWorldGeometryAndExcludeTriggersOtherLayersAndDistantColliders(System.Type shape)
        {
            var center = new Vector3(12000, 12300);
            var obj = NewObject(shape.Name, center);
            obj.transform.localScale = new Vector3(-2, 1, 1);
            obj.transform.rotation = Quaternion.Euler(0, 0, 90);
            var query = (OverlapBase2D)obj.AddComponent(shape);
            query.IsRunning = false;
            query.LayerMask = 1;
            var hitPosition = center;
            if (query is OverlapCapsule2D capsule)
            {
                capsule.Size = new Vector2(3, 1);
                capsule.Offset = Vector2.right;
                capsule.Direction = CapsuleDirection2D.Horizontal;
                hitPosition += Vector3.down * 3.5f;
            }
            else if (query is OverlapBox2D box)
            {
                box.Size = new Vector2(2, 1);
                box.Offset = Vector2.right;
                hitPosition += Vector3.down * 3.25f;
            }
            else if (query is OverlapCircle2D circle)
            {
                circle.Radius = 0.75f;
                hitPosition += Vector3.up * 1.1f;
            }
            else if (query is OverlapArea2D area)
            {
                area.Positions.Add(new Vector3(0, -1));
                area.Positions.Add(new Vector3(2, 1));
                area.Positions.Add(new Vector3(0, -1));
                hitPosition += Vector3.down * 3.25f;
            }

            var target = NewObject("Solid", hitPosition).AddComponent<BoxCollider2D>();
            target.size = Vector2.one * 0.2f;
            var trigger = NewObject("Trigger", hitPosition).AddComponent<BoxCollider2D>();
            trigger.isTrigger = true;
            var excluded = NewObject("ExcludedLayer", hitPosition);
            excluded.layer = 2;
            excluded.AddComponent<BoxCollider2D>();
            NewObject("Outside", center + Vector3.right * 10).AddComponent<BoxCollider2D>();

            Physics2D.SyncTransforms();
            query.CheckOverlap();

            Assert.That(query.results.ConvertAll(hit => hit.collider), Is.EquivalentTo(new[] { target }));
        }

        [TestCase(typeof(OverlapBox3D))]
        [TestCase(typeof(OverlapSphere3D))]
        public void Shapes3DUseWorldGeometryAndExcludeTriggersOtherLayersAndDistantColliders(System.Type shape)
        {
            var center = new Vector3(12000, 12400, 100);
            var obj = NewObject(shape.Name, center);
            obj.transform.localScale = new Vector3(-2, 1, 3);
            obj.transform.rotation = Quaternion.Euler(0, 0, 90);
            var query = (OverlapBase3D)obj.AddComponent(shape);
            query.IsRunning = false;
            query.LayerMask = 1;
            Vector3 hitPosition;
            if (query is OverlapBox3D box)
            {
                box.Size = new Vector3(2, 1, 1);
                hitPosition = center + Vector3.up * 1.5f + Vector3.forward;
            }
            else
            {
                ((OverlapSphere3D)query).Radius = 0.5f;
                hitPosition = center + Vector3.forward * 1.2f;
            }

            var target = NewObject("Solid3D", hitPosition).AddComponent<BoxCollider>();
            target.size = Vector3.one * 0.2f;
            var trigger = NewObject("Trigger3D", hitPosition).AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            var excluded = NewObject("ExcludedLayer3D", hitPosition);
            excluded.layer = 2;
            excluded.AddComponent<BoxCollider>();
            NewObject("Outside3D", center + Vector3.right * 10).AddComponent<BoxCollider>();

            Physics.SyncTransforms();
            query.CheckOverlap();

            Assert.That(query.results.ConvertAll(hit => hit.collider), Is.EquivalentTo(new[] { target }));
        }

        [Test]
        public void Capacity2DWarnsOncePerEnableAt100And101AndDisableClearsSnapshot()
        {
            var query = Circle(new Vector3(12000, 12500));
            AssertCapacityLifecycle(query, obj => obj.AddComponent<BoxCollider2D>(), Physics2D.SyncTransforms);
        }

        [Test]
        public void Capacity3DWarnsOncePerEnableAt100And101AndDisableClearsSnapshot()
        {
            var query = NewObject("Capacity3D", new Vector3(12000, 12600, 100)).AddComponent<OverlapSphere3D>();
            query.Radius = 3;
            query.LayerMask = 1;
            query.IsRunning = false;
            AssertCapacityLifecycle(query, obj => obj.AddComponent<BoxCollider>(), Physics.SyncTransforms);
        }

        private void AssertCapacityLifecycle<T, TEvent>(OverlapBase<T, TEvent> query,
            System.Func<GameObject, T> addCollider, System.Action syncTransforms)
            where T : Component
            where TEvent : UnityEvent<OverlapHit<T>>, new()
        {
            var colliders = new List<T>();
            for (int i = 0; i < 100; i++)
            {
                colliders.Add(addCollider(NewObject("CapacityTarget" + i, query.transform.position)));
            }

            int warnings = 0, enters = 0, exits = 0;
            query.onColliderEnter = new TEvent();
            query.onColliderExit = new TEvent();
            query.onColliderEnter.AddListener(_ => enters++);
            query.onColliderExit.AddListener(_ => exits++);
            var warningPattern = new System.Text.RegularExpressions.Regex(query.GetType().Name + ":.*capacity 100.*may be truncated");
            Application.LogCallback recordWarning = (message, trace, type) =>
            {
                if (type == LogType.Warning && warningPattern.IsMatch(message))
                {
                    warnings++;
                }
            };
            Application.logMessageReceived += recordWarning;
            try
            {
                syncTransforms();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                LogAssert.Expect(LogType.Warning, warningPattern);
#endif
                query.CheckOverlap();
                Assert.That(query.results.ConvertAll(hit => hit.collider), Is.EquivalentTo(colliders));

                var extra = NewObject("CapacityTarget100", query.transform.position);
                colliders.Add(addCollider(extra));
                syncTransforms();
                query.CheckOverlap();
                query.CheckOverlap();
                var truncated = query.results.ConvertAll(hit => hit.collider);
                Assert.That(truncated.Count, Is.EqualTo(100));
                Assert.That(new HashSet<T>(truncated).Count, Is.EqualTo(100));
                Assert.That(truncated, Is.SubsetOf(colliders));

                extra.SetActive(false);
                colliders[0].gameObject.SetActive(false);
                syncTransforms();
                query.CheckOverlap();
                Assert.That(query.results.Count, Is.EqualTo(99));
                extra.SetActive(true);
                colliders[0].gameObject.SetActive(true);
                syncTransforms();
                query.CheckOverlap();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Assert.That(warnings, Is.EqualTo(1), "Dropping below capacity must not reset the per-enable warning limit.");
#else
                Assert.That(warnings, Is.Zero);
#endif

                int exitsBeforeDisable = exits;
                int entersBeforeDisable = enters;
                query.enabled = false;
                Assert.That(query.results, Is.Empty);
                Assert.That(exits, Is.EqualTo(exitsBeforeDisable), "Disable must not synthesize exits.");
                Assert.That(query.IsRunning, Is.False);
                query.enabled = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                LogAssert.Expect(LogType.Warning, warningPattern);
#endif
                query.CheckOverlap();
                Assert.That(query.results.Count, Is.EqualTo(100));
                Assert.That(enters - entersBeforeDisable, Is.EqualTo(100), "Re-enable must start a fresh enter snapshot.");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Assert.That(warnings, Is.EqualTo(2));
#else
                Assert.That(warnings, Is.Zero);
#endif
            }
            finally
            {
                Application.logMessageReceived -= recordWarning;
            }
        }
    }
}

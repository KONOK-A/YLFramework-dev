using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Overlap;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class OverlapLegacyReliabilityTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();

        private GameObject NewObject(Vector3 position)
        {
            var obj = new GameObject("YL.LegacyReliability");
            obj.transform.position = position;
            _owned.Add(obj);
            return obj;
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

        [TestCase(false)]
        [TestCase(true)]
        public void TruncatedSnapshotDoesNotEmitFalseExit(bool threeD)
        {
            var center = new Vector3(17000, 18000, 0);
            int enters = 0;
            int exits = 0;
            Action sample;
            Func<int> count;
            Type queryType;
            if (threeD)
            {
                var query = NewObject(center).AddComponent<OverlapSphere3D>();
                query.IsRunning = false;
                query.LayerMask = 1;
                query.Radius = 4;
                query.onColliderEnter = new OverlapEvent3D();
                query.onColliderExit = new OverlapEvent3D();
                query.onColliderEnter.AddListener(_ => enters++);
                query.onColliderExit.AddListener(_ => exits++);
                sample = query.CheckOverlap;
                count = () => query.results.Count;
                queryType = typeof(OverlapSphere3D);
            }
            else
            {
                var query = NewObject(center).AddComponent<OverlapCircle2D>();
                query.IsRunning = false;
                query.LayerMask = 1;
                query.Radius = 4;
                query.onColliderEnter = new OverlapEvent2D();
                query.onColliderExit = new OverlapEvent2D();
                query.onColliderEnter.AddListener(_ => enters++);
                query.onColliderExit.AddListener(_ => exits++);
                sample = query.CheckOverlap;
                count = () => query.results.Count;
                queryType = typeof(OverlapCircle2D);
            }
            var original = NewObject(center);
            AddCollider(original, threeD);
            Sync(threeD);
            sample();
            Assert.That(enters, Is.EqualTo(1));
            original.transform.position += Vector3.up * 20;
            for (int i = 0; i < 101; i++)
            {
                AddCollider(NewObject(center), threeD);
            }
            Sync(threeD);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(queryType.Name + ":.*capacity 100"));
            sample();
            Assert.That(count(), Is.EqualTo(100), "Partial presentation is retained separately.");
            Assert.That(exits, Is.Zero, "A saturated observation cannot prove departure.");
            Assert.That(enters, Is.EqualTo(1), "Incomplete observations cannot commit a contact diff.");
            for (int i = 3; i < _owned.Count; i++)
            {
                _owned[i].SetActive(false);
            }
            Sync(threeD);
            sample();
            Assert.That(exits, Is.EqualTo(1));
            Assert.That(enters, Is.EqualTo(2));
        }

        [Test]
        public void AreaPerCallCapacityDoesNotImposeGlobalHundredLimit()
        {
            var center = new Vector3(17000, 18500, 0);
            var area = NewObject(center).AddComponent<OverlapArea2D>();
            area.IsRunning = false;
            area.LayerMask = 1;
            area.Positions.Add(new Vector3(-1, -1));
            area.Positions.Add(new Vector3(1, 1));
            area.Positions.Add(new Vector3(19, -1));
            area.Positions.Add(new Vector3(21, 1));
            int enters = 0;
            area.onColliderEnter = new OverlapEvent2D();
            area.onColliderEnter.AddListener(_ => enters++);
            for (int i = 0; i < 60; i++)
            {
                AddCollider(NewObject(center), false);
                AddCollider(NewObject(center + Vector3.right * 20), false);
            }
            Physics2D.SyncTransforms();
            area.CheckOverlap();
            Assert.That(area.results.Count, Is.EqualTo(120));
            Assert.That(enters, Is.EqualTo(120));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NewDetectorSamplesOneCompleteContactInItsOwnerScene(bool threeD)
        {
            var center = new Vector3(17000, 19000);
            var type = typeof(OverlapCircle2D).Assembly.GetType("YLFramework.Overlap.OverlapDetector" + (threeD ? "3D" : "2D"));
            Assert.That(type, Is.Not.Null, "The continuous detector implementation is missing.");
            var detector = NewObject(center).AddComponent(type);
            AddCollider(NewObject(center), threeD);
            Sync(threeD);
            type.GetMethod("Sample").Invoke(detector, null);
            var contacts = (System.Collections.ICollection)type.GetProperty("LastComplete").GetValue(detector);
            Assert.That(contacts.Count, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SparseCompleteRecoveryCommitsExactlyOneHundredEntersAndOneExit(bool threeD)
        {
            var center = new Vector3(17000, 19500);
            int enters = 0;
            int exits = 0;
            Action sample;
            if (threeD)
            {
                var query = NewObject(center).AddComponent<OverlapSphere3D>();
                query.IsRunning = false;
                query.LayerMask = 1;
                query.Radius = 4;
                query.onColliderEnter = new OverlapEvent3D();
                query.onColliderExit = new OverlapEvent3D();
                query.onColliderEnter.AddListener(_ => enters++);
                query.onColliderExit.AddListener(_ => exits++);
                sample = query.CheckOverlap;
            }
            else
            {
                var query = NewObject(center).AddComponent<OverlapCircle2D>();
                query.IsRunning = false;
                query.LayerMask = 1;
                query.Radius = 4;
                query.onColliderEnter = new OverlapEvent2D();
                query.onColliderExit = new OverlapEvent2D();
                query.onColliderEnter.AddListener(_ => enters++);
                query.onColliderExit.AddListener(_ => exits++);
                sample = query.CheckOverlap;
            }
            var first = NewObject(center);
            AddCollider(first, threeD);
            for (int i = 1; i < 99; i++)
            {
                AddCollider(NewObject(center), threeD);
            }
            Sync(threeD);
            sample();
            Assert.That(enters, Is.EqualTo(99));
            first.SetActive(false);
            AddCollider(NewObject(center), threeD);
            Sync(threeD);
            sample();
            Assert.That(enters, Is.EqualTo(100));
            Assert.That(exits, Is.EqualTo(1));
        }

        private static void AddCollider(GameObject obj, bool threeD)
        {
            if (threeD)
            {
                obj.AddComponent<BoxCollider>();
            }
            else
            {
                obj.AddComponent<BoxCollider2D>();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PublicMutableResultsAreResynchronizedAtEachCompleteQueryBoundary(bool threeD)
        {
            var center = new Vector3(17000, 19700);
            var targetObject = NewObject(center);
            AddCollider(targetObject, threeD);
            int enters = 0;
            if (threeD)
            {
                var query = NewObject(center).AddComponent<OverlapSphere3D>();
                query.LayerMask = 1;
                query.IsRunning = false;
                query.onColliderEnter = new OverlapEvent3D();
                query.onColliderEnter.AddListener(_ => enters++);
                Sync(true);
                query.CheckOverlap();
                query.results.Clear();
                query.CheckOverlap();
            }
            else
            {
                var query = NewObject(center).AddComponent<OverlapCircle2D>();
                query.LayerMask = 1;
                query.IsRunning = false;
                query.onColliderEnter = new OverlapEvent2D();
                query.onColliderEnter.AddListener(_ => enters++);
                Sync(false);
                query.CheckOverlap();
                query.results.Clear();
                query.CheckOverlap();
            }
            Assert.That(enters, Is.EqualTo(2), "The legacy public mutable list is not an immutable hash-set cache.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ZeroLegacyDimensionsReachNativeWithoutNewPositiveFactoryExceptions(bool threeD)
        {
            var center = new Vector3(17000, 19800);
            AddCollider(NewObject(center), threeD);
            if (threeD)
            {
                var query = NewObject(center).AddComponent<OverlapSphere3D>();
                query.LayerMask = 1;
                query.IsRunning = false;
                query.Radius = 0;
                Physics.SyncTransforms();
                var native = new Collider[100];
                int count = Physics.defaultPhysicsScene.OverlapSphere(center, 0, native, 1, QueryTriggerInteraction.UseGlobal);
                Assert.DoesNotThrow(query.CheckOverlap);
                Assert.That(query.results.Count, Is.EqualTo(count));
            }
            else
            {
                var query = NewObject(center).AddComponent<OverlapCapsule2D>();
                query.LayerMask = 1;
                query.IsRunning = false;
                query.Size = new Vector2(0, 3);
                query.Direction = CapsuleDirection2D.Horizontal;
                Physics2D.SyncTransforms();
                var native = new Collider2D[100];
                var filter = new ContactFilter2D { useTriggers = Physics2D.queriesHitTriggers };
                filter.SetLayerMask(1);
                int count = Physics2D.defaultPhysicsScene.OverlapCapsule(center, query.Size, query.Direction, 0, filter, native);
                Assert.DoesNotThrow(query.CheckOverlap);
                Assert.That(query.results.Count, Is.EqualTo(count));
            }
        }

        [Test]
        public void NewPoint2DTransformsTheLocalPointWithoutInventingVolume()
        {
            var center = new Vector3(17000, 19900);
            var query = NewObject(center).AddComponent<OverlapDetector2D>();
            query.transform.localScale = new Vector3(-2, 1, 1);
            query.transform.rotation = Quaternion.Euler(0, 0, 90);
            query.Composite = new OverlapComposite2D(OverlapShape2D.Point(Vector2.right));
            var target = NewObject(center + Vector3.down * 2).AddComponent<BoxCollider2D>();
            NewObject(center + Vector3.up * 2).AddComponent<BoxCollider2D>();
            Physics2D.SyncTransforms();
            Assert.That(query.Sample(), Is.True);
            Assert.That(query.LastComplete.Count, Is.EqualTo(1));
            Assert.That(query.LastComplete[0].Collider, Is.SameAs(target));
        }

        [Test]
        public void New3DDetectorDefaultFilterUsesExplicitIgnoreAndNoLayers()
        {
            var center = new Vector3(17000, 19950);
            var query = NewObject(center).AddComponent<OverlapDetector3D>();
            AddCollider(NewObject(center), true);
            Physics.SyncTransforms();
            Assert.That(query.Sample(), Is.True);
            Assert.That(query.LastComplete.Count, Is.EqualTo(1));
            Assert.DoesNotThrow(() => query.Filter = default);
            Assert.That(query.Filter.TriggerInteraction, Is.EqualTo(QueryTriggerInteraction.Ignore));
            Assert.That(query.Sample(), Is.True);
            Assert.That(query.LastComplete.Count, Is.EqualTo(0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyRawCapacityIncludesTriggersBeforeTheirPresentationFilter(bool threeD)
        {
            var center = new Vector3(17000, 20000);
            bool old = threeD ? Physics.queriesHitTriggers : Physics2D.queriesHitTriggers;
            try
            {
                if (threeD)
                {
                    Physics.queriesHitTriggers = true;
                }
                else
                {
                    Physics2D.queriesHitTriggers = true;
                }
                for (int i = 0; i < 100; i++)
                {
                    var obj = NewObject(center);
                    if (threeD)
                    {
                        obj.AddComponent<BoxCollider>().isTrigger = true;
                    }
                    else
                    {
                        obj.AddComponent<BoxCollider2D>().isTrigger = true;
                    }
                }
                if (threeD)
                {
                    var query = NewObject(center).AddComponent<OverlapSphere3D>();
                    query.LayerMask = 1;
                    query.IsRunning = false;
                    Sync(true);
                    LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("OverlapSphere3D:.*capacity 100"));
                    query.CheckOverlap();
                    Assert.That(query.results, Is.Empty);
                    Assert.That(query.LastQueryStatus, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
                }
                else
                {
                    var query = NewObject(center).AddComponent<OverlapCircle2D>();
                    query.LayerMask = 1;
                    query.IsRunning = false;
                    Sync(false);
                    LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("OverlapCircle2D:.*capacity 100"));
                    query.CheckOverlap();
                    Assert.That(query.results, Is.Empty);
                    Assert.That(query.LastQueryStatus, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
                }
            }
            finally
            {
                if (threeD)
                {
                    Physics.queriesHitTriggers = old;
                }
                else
                {
                    Physics2D.queriesHitTriggers = old;
                }
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Legacy2DOwnerLocalSceneStillQueriesDefaultScene() => LegacyDefaultScene(false);

        [UnityTest]
        public System.Collections.IEnumerator Legacy3DOwnerLocalSceneStillQueriesDefaultScene() => LegacyDefaultScene(true);

        private System.Collections.IEnumerator LegacyDefaultScene(bool threeD)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.CreateScene("YL.Legacy.Default." + Guid.NewGuid().ToString("N"),
                new UnityEngine.SceneManagement.CreateSceneParameters(threeD ?
                    UnityEngine.SceneManagement.LocalPhysicsMode.Physics3D : UnityEngine.SceneManagement.LocalPhysicsMode.Physics2D));
            var center = new Vector3(17000, 20100);
            try
            {
                var defaultTarget = NewObject(center);
                AddCollider(defaultTarget, threeD);
                var localTarget = NewObject(center);
                AddCollider(localTarget, threeD);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(localTarget, scene);
                var owner = NewObject(center);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
                if (threeD)
                {
                    var query = owner.AddComponent<OverlapSphere3D>();
                    query.LayerMask = 1;
                    query.IsRunning = false;
                    Sync(true);
                    query.CheckOverlap();
                    Assert.That(query.results.Count, Is.EqualTo(1));
                    Assert.That(query.results[0].collider, Is.SameAs(defaultTarget.GetComponent<Collider>()));
                }
                else
                {
                    var query = owner.AddComponent<OverlapCircle2D>();
                    query.LayerMask = 1;
                    query.IsRunning = false;
                    Sync(false);
                    query.CheckOverlap();
                    Assert.That(query.results.Count, Is.EqualTo(1));
                    Assert.That(query.results[0].collider, Is.SameAs(defaultTarget.GetComponent<Collider2D>()));
                }
                yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
                }
            }
        }

        private static void Sync(bool threeD)
        {
            if (threeD)
            {
                Physics.SyncTransforms();
            }
            else
            {
                Physics2D.SyncTransforms();
            }
        }
    }
}

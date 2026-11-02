using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Pooling;
using Object = UnityEngine.Object;

namespace YLFramework.Tests
{
    public sealed class GameObjectPoolLifecycleProbe : MonoBehaviour
    {
        public Action Enabled;
        public Action Disabled;
        public Action ParentChanged;
        public Action ChildrenChanged;
        [NonSerialized] public int EnableCalls;
        private void OnEnable()
        {
            EnableCalls++;
            Enabled?.Invoke();
        }
        private void OnDisable() => Disabled?.Invoke();
        private void OnTransformParentChanged() => ParentChanged?.Invoke();
        private void OnTransformChildrenChanged() => ChildrenChanged?.Invoke();
    }

    public class GameObjectPoolPlayModeTests
    {
        private GameObjectPool _pool;
        private readonly List<GameObject> _objects = new();
        private readonly List<Scene> _scenes = new();
        private Scene _originalScene;

        [SetUp]
        public void SetUp()
        {
            _originalScene = SceneManager.GetActiveScene();
            _pool = NewObject("YL.GO.TestHost").AddComponent<GameObjectPool>();
        }

        private GameObject NewObject(string name = "YL.GO.TestObject")
        {
            var obj = new GameObject(name);
            _objects.Add(obj);
            return obj;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_pool)
            {
                _pool.ClearAll();
            }

            foreach (var obj in _objects)
            {
                if (obj)
                {
                    Object.Destroy(obj);
                }
            }

            _objects.Clear();
            yield return null;
            if (_originalScene.IsValid() && _originalScene.isLoaded)
            {
                SceneManager.SetActiveScene(_originalScene);
            }

            foreach (var scene in _scenes)
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }

            _scenes.Clear();
        }

        [UnityTest]
        public IEnumerator WrongKeyCannotOwnOrDestroyAnotherBucketsInstance()
        {
            var obj = NewObject();
            Assert.That(_pool.Recycle("first", obj), Is.True);
            Assert.Throws<InvalidOperationException>(() => _pool.Recycle("second", obj));
            var otherHost = NewObject("YL.GO.OtherHost").AddComponent<GameObjectPool>();
            Assert.Throws<InvalidOperationException>(() => otherHost.Recycle("first", obj));
            _pool.Clear("second");
            yield return null;
            Assert.That(obj != null, Is.True);
            Assert.That(_pool.Allocate("first"), Is.SameAs(obj));
            obj.name = "renamed";
            _pool.Recycle(obj);
            Assert.That(_pool.Allocate("first"), Is.SameAs(obj));
        }

        [UnityTest]
        public IEnumerator InitializationDoesNotDestroyExistingOrRepeatedInputs()
        {
            var existing = NewObject();
            var added = NewObject();
            _pool.Recycle("array", existing);
            _pool.InitOneGoData("array", -1, new[] { existing });
            yield return null;
            Assert.That(existing != null, Is.True);
            _pool.InitOneGoData("array", -1, new[] { added, added });
            yield return null;
            Assert.That(added != null, Is.True);
            Assert.That(_pool.Allocate("array"), Is.SameAs(existing));
            Assert.That(_pool.Allocate("array"), Is.SameAs(added));
            Assert.That(_pool.Allocate("array"), Is.Null);

            _pool.Recycle("array", existing);
            var tail = NewObject();
            _pool.InitOneGoData("array", -1, new[] { null, existing, tail });
            Assert.That(_pool.Allocate("array"), Is.SameAs(existing));
            Assert.That(_pool.Allocate("array"), Is.SameAs(tail));
        }

        [UnityTest]
        public IEnumerator OnEnableCannotReturnTheInstanceBeingAllocated()
        {
            var obj = NewObject();
            var probe = obj.AddComponent<GameObjectPoolLifecycleProbe>();
            _pool.Recycle("enable", obj);
            var nestedResult = true;
            probe.Enabled = () => nestedResult = _pool.Recycle("enable", obj);
            Assert.That(_pool.Allocate("enable"), Is.SameAs(obj));
            Assert.That(nestedResult, Is.False);
            Assert.That(obj.activeSelf, Is.True);
            Assert.That(_pool.Allocate("enable"), Is.Null);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ReturnCallbacksCannotDoubleReturnOrExceedCapacity()
        {
            _pool.InitOneGoData("return", 1);
            var outer = NewObject();
            var peer = NewObject();
            var probe = outer.AddComponent<GameObjectPoolLifecycleProbe>();
            var duplicate = true;
            var nested = false;
            probe.Disabled = () =>
            {
                duplicate = _pool.Recycle("return", outer);
                nested = _pool.Recycle("return", peer);
            };
            Assert.That(_pool.Recycle("return", outer), Is.False);
            Assert.That(duplicate, Is.False);
            Assert.That(nested, Is.True);
            Assert.That(_pool.GetCachedCount("return"), Is.EqualTo(1));
            Assert.That(_pool.Recycle("return", outer), Is.False);
            yield return null;
            Assert.That(outer == null, Is.True);
            Assert.That(_pool.Allocate("return"), Is.SameAs(peer));

            var reentrant = peer.AddComponent<GameObjectPoolLifecycleProbe>();
            reentrant.Disabled = () => _pool.InitOneGoData("return", 0);
            Assert.That(_pool.Recycle("return", peer), Is.False);
            Assert.That(_pool.GetCachedCount("return"), Is.Zero);
            yield return null;
            Assert.That(peer == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ParentChangedReturnIsRejectedAndClearCannotPublishOldAllocation()
        {
            var obj = NewObject();
            var replacement = NewObject();
            var parent = NewObject("YL.GO.Parent").transform;
            var probe = obj.AddComponent<GameObjectPoolLifecycleProbe>();
            _pool.Recycle("generation", obj);
            var nested = true;
            probe.ParentChanged = () => nested = _pool.Recycle("generation", obj);
            Assert.That(_pool.Allocate("generation", parent), Is.SameAs(obj));
            Assert.That(nested, Is.False);
            Assert.That(obj.transform.parent, Is.SameAs(parent));
            _pool.Recycle("generation", obj);
            probe.Enabled = () =>
            {
                _pool.Clear("generation");
                _pool.Recycle("generation", replacement);
            };
            Assert.That(_pool.Allocate("generation"), Is.Null);
            Assert.That(_pool.GetCachedCount("generation"), Is.EqualTo(1));
            Assert.That(_pool.Allocate("generation"), Is.SameAs(replacement));
            Assert.That(_pool.Recycle("generation", obj), Is.False);
            yield return null;
            Assert.That(obj == null, Is.True);
        }

        [UnityTest]
        public IEnumerator FakeNullDoesNotOccupyCapacityAndShrinkRetiresImmediately()
        {
            _pool.InitOneGoData("capacity", 2);
            var dead = NewObject();
            var live = NewObject();
            var next = NewObject();
            _pool.Recycle("capacity", dead);
            _pool.Recycle("capacity", live);
            Object.Destroy(dead);
            yield return null;
            Assert.That(_pool.GetCachedCount("capacity"), Is.EqualTo(1));
            Assert.That(_pool.Recycle("capacity", next), Is.True);
            _pool.InitOneGoData("capacity", 1);
            Assert.That(_pool.GetCachedCount("capacity"), Is.EqualTo(1));
            Assert.That(_pool.Recycle("capacity", live), Is.False);
            Assert.That(_pool.Allocate("capacity"), Is.SameAs(next));
            yield return null;
            Assert.That(live == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ClearAndHostDestructionLeaveBorrowedObjectsToTheirOwner()
        {
            var borrowed = NewObject();
            var idle = NewObject();
            _pool.Recycle("clear", borrowed);
            _pool.Recycle("clear", idle);
            _pool.Allocate("clear");
            _pool.ClearAll();
            Assert.That(_pool.Recycle("clear", idle), Is.False);
            yield return null;
            Assert.That(idle == null, Is.True);
            Assert.That(borrowed != null, Is.True);
            var replacement = NewObject();
            _pool.Recycle("clear", replacement);
            Assert.That(_pool.Recycle("clear", borrowed), Is.False);
            Assert.That(_pool.GetCachedCount("clear"), Is.EqualTo(1));
            _pool.Allocate("clear");
            Object.Destroy(_pool.gameObject);
            yield return null;
            Assert.That(borrowed == null, Is.True);
            Assert.That(replacement != null, Is.True);
            Assert.That(_pool.Recycle("clear", replacement), Is.False);
            yield return null;
            Assert.That(replacement == null, Is.True);
        }

        [UnityTest]
        public IEnumerator PrewarmIsInactiveTargetBasedAndInvalidInputsDoNotMutateThePool()
        {
            var prefab = NewObject("YL.GO.PrefabSource");
            prefab.AddComponent<GameObjectPoolLifecycleProbe>();
            _pool.InitOneGoData("prewarm", 3, prefab, 2);
            Assert.That(_pool.GetCachedCount("prewarm"), Is.EqualTo(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => _pool.InitOneGoData("prewarm", -2));
            Assert.Throws<ArgumentOutOfRangeException>(() => _pool.InitOneGoData("prewarm", 1, prefab, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => _pool.InitOneGoData("prewarm", 3, prefab, -1));
            Assert.Throws<ArgumentNullException>(() => _pool.InitOneGoData("prewarm", 3, (GameObject)null, 1));
            Assert.Throws<ArgumentException>(() => _pool.Allocate(""));
            Assert.That(_pool.GetCachedCount("prewarm"), Is.EqualTo(2));
            var first = _pool.Allocate("prewarm");
            _objects.Add(first);
            Assert.That(first.GetComponent<GameObjectPoolLifecycleProbe>().EnableCalls, Is.EqualTo(1));
            _pool.Recycle("prewarm", first);
            var second = _pool.Allocate("prewarm");
            _objects.Add(second);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.GetComponent<GameObjectPoolLifecycleProbe>().EnableCalls, Is.EqualTo(1));
            _pool.Recycle("prewarm", second);
            _pool.InitOneGoData("prewarm", 3, prefab, 3);
            Assert.That(_pool.GetCachedCount("prewarm"), Is.EqualTo(3));
            yield return null;
        }

        [UnityTest]
        public IEnumerator CachedInstancesSurviveSceneUnloadAndAllocationReturnsToActiveScene()
        {
            var scene = SceneManager.CreateScene("YL.GO.Scene");
            _scenes.Add(scene);
            SceneManager.SetActiveScene(scene);
            var cached = NewObject();
            _pool.Recycle("scene", cached);
            Assert.That(cached.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            SceneManager.SetActiveScene(_originalScene);
            yield return SceneManager.UnloadSceneAsync(scene);
            Assert.That(cached != null && _pool != null, Is.True);
            Assert.That(_pool.Allocate("scene"), Is.SameAs(cached));
            Assert.That(cached.scene, Is.EqualTo(_originalScene));
            Assert.That(cached.transform.parent, Is.Null);

            var secondScene = SceneManager.CreateScene("YL.GO.BorrowedScene");
            _scenes.Add(secondScene);
            SceneManager.SetActiveScene(secondScene);
            _pool.Recycle("scene", cached);
            _pool.Allocate("scene");
            SceneManager.SetActiveScene(_originalScene);
            yield return SceneManager.UnloadSceneAsync(secondScene);
            Assert.That(cached == null, Is.True);
            Assert.That(_pool.Recycle("scene", cached), Is.False);
            var next = NewObject();
            Assert.That(_pool.Recycle("scene", next), Is.True);
        }

        [UnityTest]
        public IEnumerator UnityCallbackExceptionDoesNotDuplicateTheCachedInstance()
        {
            var obj = NewObject();
            var probe = obj.AddComponent<GameObjectPoolLifecycleProbe>();
            _pool.Recycle("error", obj);
            probe.Enabled = () => throw new InvalidOperationException("YL.GO.ExpectedCallbackError");
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("YL.GO.ExpectedCallbackError"));
            // Unity 消息异常由引擎记录，不从 SetActive 向调用方传播。
            Assert.That(_pool.Allocate("error"), Is.SameAs(obj));
            Assert.That(_pool.GetCachedCount("error"), Is.Zero);
            probe.Enabled = null;
            Assert.That(_pool.Recycle("error", obj), Is.True);
            Assert.That(_pool.Recycle("error", obj), Is.False);
            Assert.That(_pool.Allocate("error"), Is.SameAs(obj));
            yield return null;
        }

        [UnityTest]
        public IEnumerator DataRootLossAndSessionResetKeepOriginalCacheOwnership()
        {
            var obj = NewObject();
            _pool.Recycle("session", obj);
            var borrowed = _pool.Allocate("session");
            var idle = NewObject();
            _pool.Recycle("session", idle);
            var dataPool = SafeObjectPool<GameObjectPoolData>.Instance;
            dataPool.Dispose();
            typeof(GameObjectPool).GetMethod("ResetHosts", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.That(_pool.GetCachedCount("session"), Is.Zero);
            Assert.That(borrowed != null, Is.True);
            var replacement = NewObject();
            Assert.That(_pool.Recycle("session", replacement), Is.True);
            Assert.That(_pool.Recycle("session", borrowed), Is.False);
            Object.Destroy(replacement.transform.parent.gameObject);
            yield return null;
            Assert.That(idle == null && borrowed == null && replacement == null, Is.True);
            var next = NewObject();
            Assert.That(_pool.Recycle("session", next), Is.True);
            Assert.That(_pool.Allocate("session"), Is.SameAs(next));
        }

        [UnityTest]
        public IEnumerator StandaloneDataGuardsInitializationAndReturnsToItsOriginalSafePool()
        {
            var owner = SafeObjectPool<GameObjectPoolData>.Instance;
            var data = owner.Allocate();
            var root = NewObject("YL.GO.DataRoot");
            var probe = root.AddComponent<GameObjectPoolLifecycleProbe>();
            var fired = false;
            var rejected = false;
            probe.ChildrenChanged = () =>
            {
                if (fired)
                {
                    return;
                }

                fired = true;
                try
                {
                    data.Init("nested", root.transform);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
            };
            try
            {
                data.Init("standalone", root.transform);
                Assert.That(rejected, Is.True);
                Assert.That(root.transform.childCount, Is.EqualTo(1));
                var obj = NewObject();
                data.Recycle(obj);
                owner.Dispose();
                Assert.DoesNotThrow(() => data.RecycleToCache());
                Assert.That(data.Count, Is.Zero);
                yield return null;
                Assert.That(obj == null, Is.True);
                Assert.That(root.transform.childCount, Is.Zero);
            }
            finally
            {
                data.OnRecycle();
            }

            var hostProbe = _pool.gameObject.AddComponent<GameObjectPoolLifecycleProbe>();
            fired = false;
            rejected = false;
            hostProbe.ChildrenChanged = () =>
            {
                if (fired)
                {
                    return;
                }

                fired = true;
                try
                {
                    _pool.InitOneGoData("creating", 2);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
            };
            _pool.InitOneGoData("creating", 1);
            Assert.That(rejected, Is.True);
            Assert.That(_pool.transform.childCount, Is.EqualTo(1));
            fired = false;
            hostProbe.ChildrenChanged = () =>
            {
                if (fired)
                {
                    return;
                }

                fired = true;
                _pool.Clear("cancel-creating");
            };
            Assert.Throws<ObjectDisposedException>(() => _pool.InitOneGoData("cancel-creating", 1));
            hostProbe.ChildrenChanged = null;
            _pool.Clear("creating");
            yield return null;
            Assert.That(_pool.transform.childCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator AllocationCannotPutBorrowedObjectsUnderAnotherCacheRoot()
        {
            var root = NewObject("YL.GO.StandaloneRoot");
            var first = new GameObjectPoolData();
            var second = new GameObjectPoolData();
            try
            {
                first.Init("first", root.transform);
                second.Init("second", root.transform);
                var obj = NewObject();
                first.Recycle(obj);
                Assert.Throws<ArgumentException>(() => first.Allocate(second.RootTransform));
                Assert.That(first.Count, Is.EqualTo(1));
                Assert.That(first.Allocate(), Is.SameAs(obj));
                second.OnRecycle();
                yield return null;
                Assert.That(obj != null, Is.True);
            }
            finally
            {
                first.OnRecycle();
                second.OnRecycle();
            }
        }
    }
}

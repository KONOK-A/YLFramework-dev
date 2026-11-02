using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Singletons;

namespace YLFramework.Tests
{
    public sealed class RuntimeHostProbe : MonoSingletonHost<RuntimeHostProbe>
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Releases")]
        public int releases;
        public void Release() => ReleaseHost();
        protected override void OnHostRelease() => releases++;
    }

    [MonoSingletonPath("YL.Singleton.Path/Child")]
    public sealed class PathSingletonProbe : MonoSingleton<PathSingletonProbe>
    {
    }

    public sealed class PersistentSingletonProbe : PersistentMonoSingleton<PersistentSingletonProbe>
    {
    }
    public sealed class PrefabSingletonProbe : MonoBehaviour
    {
    }

    public class SingletonHostPlayModeTests
    {
        [UnityTest]
        public IEnumerator PersistentDuplicateDestroysOnlyTheLaterGameObject()
        {
            var first = new GameObject("YL.Singleton.Persistent.First");
            var duplicate = new GameObject("YL.Singleton.Persistent.Duplicate");
            try
            {
                var current = first.AddComponent<PersistentSingletonProbe>();
                duplicate.AddComponent<PersistentSingletonProbe>();
                Assert.That(PersistentSingletonProbe.Instance, Is.SameAs(current));
                Assert.That(duplicate != null, Is.True);
                yield return null;
                Assert.That(duplicate == null, Is.True);
                Assert.That(first != null, Is.True);
                Assert.That(first.scene.name, Is.EqualTo("DontDestroyOnLoad"));
                Assert.That(PersistentSingletonProbe.Instance, Is.SameAs(current));
            }
            finally
            {
                if (first)
                {
                    Object.Destroy(first);
                }

                if (duplicate)
                {
                    Object.Destroy(duplicate);
                }
            }
        }

        [UnityTest]
        public IEnumerator PrefabDisposeSkipsTheRetiredCloneInTheSameFrame()
        {
            var template = new GameObject("YL.Singleton.Prefab.Template");
            template.SetActive(false);
            template.AddComponent<PrefabSingletonProbe>();
            GameObject firstRoot = null;
            GameObject secondRoot = null;
            try
            {
                var first = PrefabSingletonProperty<PrefabSingletonProbe>.InstanceWithLoader(_ => template);
                Assert.That(first, Is.Not.Null);
                Assert.That(first.gameObject, Is.Not.SameAs(template));
                firstRoot = first.gameObject;
                firstRoot.SetActive(true);
                PrefabSingletonProperty<PrefabSingletonProbe>.Dispose();
                var second = PrefabSingletonProperty<PrefabSingletonProbe>.Instance;
                Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
                secondRoot = second.gameObject;
                Assert.That(first != null, Is.True);
                Assert.That(firstRoot != null, Is.True);
                yield return null;
                Assert.That(first == null, Is.True);
                Assert.That(firstRoot == null, Is.True, "Dispose 必须销毁整个克隆，而不是只移除单例组件。");
                Assert.That(PrefabSingletonProperty<PrefabSingletonProbe>.Instance, Is.SameAs(second));
                Assert.That(second.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            }
            finally
            {
                PrefabSingletonProperty<PrefabSingletonProbe>.Dispose();
                PrefabSingletonProperty<PrefabSingletonProbe>.PrefabLoader = Resources.Load<GameObject>;
                if (firstRoot)
                {
                    Object.Destroy(firstRoot);
                }

                if (secondRoot)
                {
                    Object.Destroy(secondRoot);
                }

                if (template)
                {
                    Object.Destroy(template);
                }
            }
        }

        [UnityTest]
        public IEnumerator MissingPrefabComponentCleansOnlyTheFailedCloneAtFrameEnd()
        {
            var template = new GameObject("YL.Singleton.Prefab.MissingComponent");
            GameObject clone = null;
            try
            {
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("PrefabSingletonProperty<PrefabSingletonProbe>"));
                Assert.That(PrefabSingletonProperty<PrefabSingletonProbe>.InstanceWithLoader(_ => template), Is.Null);
                clone = GameObject.Find(template.name + "(Clone)");
                Assert.That(clone, Is.Not.Null);
                yield return null;
                Assert.That(clone == null, Is.True);
                Assert.That(template != null, Is.True);
                Assert.That(GameObject.Find(template.name + "(Clone)"), Is.Null);
            }
            finally
            {
                PrefabSingletonProperty<PrefabSingletonProbe>.Dispose();
                PrefabSingletonProperty<PrefabSingletonProbe>.PrefabLoader = Resources.Load<GameObject>;
                if (clone)
                {
                    Object.Destroy(clone);
                }

                if (template)
                {
                    Object.Destroy(template);
                }
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var host in Object.FindObjectsByType<RuntimeHostProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                host.Release();
            }

            MonoSingletonProperty<PathSingletonProbe>.Dispose();
            var pathRoot = GameObject.Find("YL.Singleton.Path");
            if (pathRoot)
            {
                Object.Destroy(pathRoot);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator RepeatedSameFrameReleaseDoesNotReadoptAnyRetiredHost()
        {
            var first = RuntimeHostProbe.Instance;
            first.Release();
            var second = RuntimeHostProbe.Instance;
            second.Release();
            var current = RuntimeHostProbe.Instance;
            Assert.That(current, Is.Not.SameAs(first));
            Assert.That(current, Is.Not.SameAs(second));
            Assert.That(first.releases, Is.EqualTo(1));
            yield return null;
            Assert.That(first == null && second == null, Is.True);
            Assert.That(RuntimeHostProbe.Instance, Is.SameAs(current));
            Assert.That(Object.FindObjectsByType<RuntimeHostProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator OwnedHostSurvivesSceneUnloadAndRecreatesAfterExternalDestroy()
        {
            var previous = SceneManager.GetActiveScene();
            var scene = SceneManager.CreateScene("YL.Singleton.HostScene");
            SceneManager.SetActiveScene(scene);
            var host = RuntimeHostProbe.Instance;
            SceneManager.SetActiveScene(previous);
            yield return SceneManager.UnloadSceneAsync(scene);
            Assert.That(host != null, Is.True);
            Object.Destroy(host.gameObject);
            yield return null;
            Assert.That(RuntimeHostProbe.HasInstance, Is.False);
            Assert.That(RuntimeHostProbe.Instance, Is.Not.SameAs(host));
        }

        [UnityTest]
        public IEnumerator ExistingMonoSingletonPathAndDisposeStillWork()
        {
            var first = PathSingletonProbe.Instance;
            Assert.That(first.transform.parent.name, Is.EqualTo("YL.Singleton.Path"));
            Assert.That(first.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            first.Dispose();
            var second = PathSingletonProbe.Instance;
            second.Dispose();
            var current = PathSingletonProbe.Instance;
            Assert.That(current, Is.Not.SameAs(first));
            Assert.That(current, Is.Not.SameAs(second));
            yield return null;
            Assert.That(first == null && second == null, Is.True);
            Assert.That(PathSingletonProbe.Instance, Is.SameAs(current));
        }
    }
}

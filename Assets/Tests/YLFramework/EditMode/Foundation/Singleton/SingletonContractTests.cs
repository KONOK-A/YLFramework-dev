using NUnit.Framework;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Tests
{
    public sealed class PlainSingletonProbe : Singleton<PlainSingletonProbe>
    {
        public int Initializations;
        public override void OnSingletonInit() => Initializations++;
    }

    public sealed class EditHostProbe : MonoSingletonHost<EditHostProbe>
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Initializations")]
        public int initializations;
        [global::UnityEngine.Serialization.FormerlySerializedAs("Releases")]
        public int releases;
        public static EditHostProbe Get() => GetInstance(true);
        public void Release() => ReleaseHost();
        public void SimulateQuit() => base.OnApplicationQuit();
        public override void OnSingletonInit()
        {
            initializations++;
            Assert.That(Get(), Is.SameAs(this));
        }
        protected override void OnHostRelease() => releases++;
    }

    public class SingletonContractTests
    {
        [TearDown]
        public void TearDown()
        {
            foreach (var host in Object.FindObjectsByType<EditHostProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                host.Release();
            }

            SingletonProperty<PlainSingletonProbe>.Dispose();
        }

        [Test]
        public void PlainSingletonInitializesOnceAndRecreatesAfterDispose()
        {
            var first = PlainSingletonProbe.Instance;
            Assert.That(PlainSingletonProbe.Instance, Is.SameAs(first));
            Assert.That(first.Initializations, Is.EqualTo(1));
            first.Dispose();
            Assert.That(PlainSingletonProbe.HasInstance, Is.False);
            Assert.That(PlainSingletonProbe.Instance, Is.Not.SameAs(first));
        }

        [Test]
        public void HostDefaultDoesNotCreateInEditModeButExplicitAccessCan()
        {
            Assert.That(EditHostProbe.Instance, Is.Null);
            var first = EditHostProbe.Get();
            Assert.That(first.initializations, Is.EqualTo(1));
            var root = first.gameObject;
            first.Release();
            Assert.That(root == null, Is.True);
            Assert.That(EditHostProbe.HasInstance, Is.False);
            Assert.That(EditHostProbe.Get(), Is.Not.SameAs(first));
        }

        [Test]
        public void ExplicitEditModeAccessDoesNotInheritARuntimeQuitGate()
        {
            var host = EditHostProbe.Get();
            host.SimulateQuit();
            host.Release();
            Assert.That(EditHostProbe.Get(), Is.Not.Null);
        }

        [Test]
        public void InactiveBorrowedHostIsAdoptedAndOnlyItsComponentIsReleased()
        {
            var root = new GameObject("YL.Singleton.Borrowed");
            root.SetActive(false);
            try
            {
                var host = root.AddComponent<EditHostProbe>();
                Assert.That(EditHostProbe.Get(), Is.SameAs(host));
                host.Release();
                Assert.That(host.releases, Is.EqualTo(1));
                Assert.That(root != null, Is.True);
                Assert.That(root.GetComponent<EditHostProbe>(), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DuplicateBorrowedHostsDoNotDestroyTheirGameObjects()
        {
            var firstRoot = new GameObject("YL.Singleton.First");
            var secondRoot = new GameObject("YL.Singleton.Second");
            try
            {
                firstRoot.AddComponent<EditHostProbe>();
                secondRoot.AddComponent<EditHostProbe>();
                Assert.That(EditHostProbe.Get(), Is.Not.Null);
                Assert.That(Object.FindObjectsByType<EditHostProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                    Is.EqualTo(1));
                Assert.That(firstRoot != null && secondRoot != null, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(firstRoot);
                Object.DestroyImmediate(secondRoot);
            }
        }
    }
}

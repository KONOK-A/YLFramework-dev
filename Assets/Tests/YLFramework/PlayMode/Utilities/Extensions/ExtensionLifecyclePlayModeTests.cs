using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Foundation;
using YLFramework.Extensions;

namespace YLFramework.Tests
{
    public class ExtensionLifecyclePlayModeTests
    {
        [UnityTest]
        public IEnumerator GetOrAddReusesBothOverloadsAndReplacesDestroyedComponents()
        {
            var go = new GameObject("YL.Tests.Extension.GetOrAdd");
            try
            {
                var first = go.GetOrAddComponent<BoxCollider>();
                Assert.That(go.GetOrAddComponent<BoxCollider>(), Is.SameAs(first));
                Assert.That(go.transform.GetOrAddComponent<BoxCollider>(), Is.SameAs(first));
                Assert.That(go.GetOrAddComponent(typeof(BoxCollider)), Is.SameAs(first));
                Object.Destroy(first);
                yield return null;
                Assert.That(first == null, Is.True);
                var second = go.GetOrAddComponent(typeof(BoxCollider));
                Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
                Assert.That(go.GetOrAddComponent<BoxCollider>(), Is.SameAs(second));
                Object.Destroy(second);
                yield return null;
                var third = go.transform.GetOrAddComponent<BoxCollider>();
                Assert.That(third, Is.Not.Null.And.Not.SameAs(second));
                Assert.That(go.GetComponents<BoxCollider>().Length, Is.EqualTo(1));
            }
            finally
            {
                if (go)
                {
                    Object.Destroy(go);
                }
            }
        }

        [UnityTest]
        public IEnumerator GracefulComponentDestroyReturnsTheOriginalReferenceAndKeepsItsGameObject()
        {
            var go = new GameObject("YL.Tests.Extension.ComponentDestroy");
            try
            {
                var component = go.AddComponent<BoxCollider>();
                Assert.That(component.DestroySelfGracefully(), Is.SameAs(component));
                Assert.That(component != null, Is.True);
                yield return null;
                Assert.That(component == null, Is.True);
                Assert.That(go != null, Is.True);
                Assert.That(component.DestroySelfGracefully(), Is.SameAs(component));
                Assert.That(((Object)null).DestroySelfGracefully(), Is.Null);
            }
            finally
            {
                if (go)
                {
                    Object.Destroy(go);
                }
            }
        }

        [UnityTest]
        public IEnumerator DeferredDestroyKeepsManagedReferenceButMaskAndGracefulHelpersRespectFakeNull()
        {
            var go = new GameObject("YL.Tests.ExtensionLifetime.53");
            Component component = go.transform;
            try
            {
                go.layer = 31;
                Assert.That(go.IsInLayerMask(int.MinValue), Is.True);
                Assert.That(component.IsInLayerMask(int.MinValue), Is.True);
                Assert.That(go.IsDestroy() || component.IsDestroy(), Is.False);
                component.DestroyGameObjGracefully();
                Assert.That(go != null, Is.True, "Destroy 在帧末生效");
                Assert.That(go.IsDestroy(), Is.False);
                yield return null;
                Assert.That(ReferenceEquals(go, null), Is.False);
                Assert.That(ReferenceEquals(component, null), Is.False);
                Assert.That(go == null && component == null, Is.True);
                Assert.That(go.IsDestroy() && component.IsDestroy(), Is.True);
                Assert.That(((Object)null).IsDestroy(), Is.True);
                Assert.That(go.IsInLayerMask(-1), Is.False);
                Assert.That(component.IsInLayerMask(-1), Is.False);
                Assert.That(LayerMaskUtility.IsInLayerMask(go, -1), Is.False);
                Assert.DoesNotThrow(() => go.DestroySelfGracefully());
                Assert.DoesNotThrow(() => component.DestroyGameObjGracefully());
                Assert.DoesNotThrow(() => ((Component)null).DestroyGameObjGracefully());
            }
            finally
            {
                if (go)
                {
                    Object.Destroy(go);
                }
            }
        }
    }
}

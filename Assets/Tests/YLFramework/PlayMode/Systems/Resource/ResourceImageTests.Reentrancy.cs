using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    public partial class ResourceImageTests
    {
        [UnityTest]
        public IEnumerator RestoreCallbackRebindingAReadyImageCannotBeAcquiredAgainByTheOuterRefresh()
        {
            var secondFile = _file + ".reentrant.png";
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(_file), global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            using var warm = system.AcquireImage(secondFile);
            _host = new GameObject("YL89 restore reentry", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var target = _host.GetComponent<Image>();
            var component = _host.AddComponent<ResourceImage>();
            component.SetImage(_file);
            yield return WaitUntil(() => component.Status == ResourceRequestStatus.Succeeded);
            var entered = false;
            UnityAction observer = () =>
            {
                if (entered)
                {
                    return;
                }

                entered = true;
                component.SetImage(secondFile);
            };
            try
            {
                target.RegisterDirtyVerticesCallback(observer);
                component.Refresh();
                Assert.That(entered, Is.True);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                Assert.That(target.sprite.texture, Is.SameAs(warm.Asset));
                target.UnregisterDirtyVerticesCallback(observer);
                component.Clear();
                yield return null;
                Assert.That(target.sprite, Is.Null);
                Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1), "Only the independent warm owner may survive; no overwritten UI lease.");
            }
            finally
            {
                target.UnregisterDirtyVerticesCallback(observer);
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            }
        }

        [UnityTest]
        public IEnumerator AThrowFromAnOldSetterAfterRebindingCannotReleaseOrFailTheNewGeneration()
        {
            var secondFile = _file + ".new-generation.png";
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(_file), global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            using var firstWarm = system.AcquireImage(_file);
            using var secondWarm = system.AcquireImage(secondFile);
            _host = new GameObject("YL89 old setter failure", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var target = _host.GetComponent<Image>();
            var component = _host.AddComponent<ResourceImage>();
            var entered = false;
            UnityAction observer = () =>
            {
                if (entered)
                {
                    return;
                }

                entered = true;
                component.SetImage(secondFile);
                throw new InvalidOperationException("YL89 old setter callback");
            };
            try
            {
                target.RegisterDirtyVerticesCallback(observer);
                component.SetImage(_file);
                Assert.That(entered, Is.True);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                Assert.That(component.Error, Is.Null);
                Assert.That(target.sprite.texture, Is.SameAs(secondWarm.Asset));
                target.UnregisterDirtyVerticesCallback(observer);
                component.Clear();
                yield return null;
                Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(2));
            }
            finally
            {
                target.UnregisterDirtyVerticesCallback(observer);
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            }
        }

        [UnityTest]
        public IEnumerator RestoreSetterExceptionsStillReleaseTheEscrowedLeaseAndOwnedSprite()
        {
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            _host = new GameObject("YL89 restore throw", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var target = _host.GetComponent<Image>();
            var component = _host.AddComponent<ResourceImage>();
            component.SetImage(_file);
            yield return WaitUntil(() => component.Status == ResourceRequestStatus.Succeeded);
            var sprite = target.sprite;
            var native = sprite.texture;
            UnityAction observer = () => throw new InvalidOperationException("YL89 restore setter callback");
            target.RegisterDirtyVerticesCallback(observer);
            try
            {
                Assert.Throws<InvalidOperationException>(() => component.Clear());
            }
            finally
            {
                target.UnregisterDirtyVerticesCallback(observer);
            }
            yield return null;
            Assert.That(sprite == null, Is.True, "A throwing external UI callback cannot discard the only Sprite cleanup responsibility.");
            Assert.That(native == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
        }
    }
}

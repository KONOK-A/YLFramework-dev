using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    public class DynamicTextureSourceTests
    {
        private string _file;
        private ResourceAddress _address;
        private HttpListener _server;
        private Task _serve;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Application.temporaryCachePath)), Is.True);
            _file = Path.Combine(Application.temporaryCachePath, "YL83-CaSe-" + Guid.NewGuid().ToString("N") + ".png");
            var image = new Texture2D(3, 2);
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), image.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
            _address = new ResourceAddress("texture83", "image");
        }

        [TearDown]
        public void TearDown()
        {
            typeof(ResourceSystem).GetMethod("EndSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
            typeof(ResourceSystem).GetMethod("ResetSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            if (_server != null)
            {
                _server.Stop();
                _server.Close();
                if (_serve != null)
                {
                    Assert.That(_serve.Wait(TimeSpan.FromSeconds(3)), Is.True, "Fixture server must settle its own request task.");
                }

                _server = null;
            }
        }

        private ResourceSystem Initialize()
            => Initialize(new[] { new ResourceCatalogEntry("image", _address, new ResourceLocation(ResourceSourceId.LocalTexture, _file, typeof(Texture2D))) });

        private ResourceSystem Initialize(ResourceCatalogEntry[] entries)
        {
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog("texture83", entries)
            }));
            Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
            return initialized.TakeResult();
        }

        private string StartServer(Func<HttpListenerContext, Task> handler)
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            var root = "http://127.0.0.1:" + port + "/";
            _server = new HttpListener();
            _server.Prefixes.Add(root);
            _server.Start();
            _serve = Task.Run(async () =>
            {
                try
                {
                    while (_server.IsListening)
                    {
                        var context = await _server.GetContextAsync();
                        await handler(context);
                    }
                }
                catch (HttpListenerException) when (!_server.IsListening) { }
                catch (ObjectDisposedException) when (!_server.IsListening) { }
            });
            return root;
        }

        private static IEnumerator Wait<T>(ResourceRequest<T> request)
        {
            var until = Time.realtimeSinceStartup + 8;
            while (!request.IsDone && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True, "Texture operation must terminate, including error and abort.");
        }

        [Test]
        public void DriveRelativeAndCurrentDriveRootedLocatorsAreNotExplicitAbsoluteFiles()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
            {
                Assert.Ignore("Windows rooted-path regression.");
            }

            var drive = Path.GetPathRoot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(_file))).Substring(0, 2);
            foreach (var locator in new[] { drive + Path.GetFileName(_file), "\\" + Path.GetFileName(_file) })
            {
                using var request = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("texture83", new[]
                {
                    new ResourceCatalogEntry("image", _address, new ResourceLocation(ResourceSourceId.LocalTexture, locator, typeof(Texture2D)))
                }) }));
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed), "Rooted is not necessarily fully qualified on Windows.");
                Assert.That(request.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            }
        }

        [UnityTest]
        public IEnumerator LocalTextureSharesProductionCancelsIndependentlyAndDestroysOnlyCreatedResult()
        {
            var system = Initialize();
            using var first = system.AcquireAsync<Texture2D>(_address);
            using var second = system.AcquireAsync<Texture2D>(_address);
            first.Cancel();
            yield return second;
            using var lease = second.TakeResult();
            using var other = system.Acquire<Texture2D>(_address);
            var native = lease.Asset;
            Assert.That(native.width, Is.EqualTo(3));
            Assert.That(native.height, Is.EqualTo(2));
            Assert.That(other.Asset, Is.SameAs(native));
            lease.Dispose();
            Assert.That(other.IsValid, Is.True);
            other.Dispose();
            yield return null;
            Assert.That(native == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator LocalMissingBadImageFinalCancelAndDecodedUnclaimedResultHaveNoOrphan()
        {
            var system = Initialize();
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            using (var missing = system.AcquireAsync<Texture2D>(_address))
            {
                Assert.That(missing.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
                Assert.That(missing.Error.InnerException, Is.InstanceOf<FileNotFoundException>());
            }
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), Encoding.UTF8.GetBytes("not-an-image"));
            using (var invalid = system.AcquireAsync<Texture2D>(_address))
            {
                yield return Wait(invalid);
                Assert.That(invalid.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            }
            var original = new Texture2D(2, 3);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), original.EncodeToPNG());
            Object.DestroyImmediate(original);
            using (var canceled = system.AcquireAsync<Texture2D>(_address))
            {
                canceled.Cancel();
            }

            using var closing = system.ShutdownAsync();
            yield return Wait(closing);
            Assert.That(closing.TakeResult(), Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            system = Initialize();
            var holder = system.Acquire<Texture2D>(_address);
            var native = holder.Asset;
            using (var unclaimed = system.AcquireAsync<Texture2D>(_address))
            {
                Assert.That(unclaimed.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                holder.Dispose();
                Assert.That(native == null, Is.False);
            }
            yield return null;
            Assert.That(native == null, Is.True);
        }

        [UnityTest]
        public IEnumerator UrlCaseIsPreservedSharedConsumerCancellationAndErrorDecodingAreIsolated()
        {
            var png = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            var imageGets = 0;
            var root = StartServer(async context =>
            {
                byte[] bytes;
                if (context.Request.RawUrl == "/Image?Version=AbC")
                {
                    imageGets++;
                    bytes = png;
                }
                else if (context.Request.RawUrl == "/bad")
                {
                    bytes = Encoding.UTF8.GetBytes("bad-image");
                }
                else
                {
                    context.Response.StatusCode = 404;
                    bytes = Array.Empty<byte>();
                }
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                context.Response.Close();
            });
            var lower = new ResourceAddress("texture83", "lower");
            var bad = new ResourceAddress("texture83", "bad");
            var system = Initialize(new[]
            {
                new ResourceCatalogEntry("image", _address, new ResourceLocation(ResourceSourceId.UrlTexture, root + "Image?Version=AbC", typeof(Texture2D))),
                new ResourceCatalogEntry("lower", lower, new ResourceLocation(ResourceSourceId.UrlTexture, root + "image?version=abc", typeof(Texture2D))),
                new ResourceCatalogEntry("bad", bad, new ResourceLocation(ResourceSourceId.UrlTexture, root + "bad", typeof(Texture2D)))
            });
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<Texture2D>(_address)).Kind, Is.EqualTo(ResourceError.Unsupported));
            using var first = system.AcquireAsync<Texture2D>(_address);
            using var second = system.AcquireAsync<Texture2D>(_address);
            first.Cancel();
            using var missing = system.AcquireAsync<Texture2D>(lower);
            using var corrupt = system.AcquireAsync<Texture2D>(bad);
            yield return Wait(second);
            yield return Wait(missing);
            yield return Wait(corrupt);
            Assert.That(imageGets, Is.EqualTo(1));
            Assert.That(missing.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(missing.Error.ToString(), Does.Contain("404"));
            Assert.That(corrupt.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            using var lease = second.TakeResult();
            var native = lease.Asset;
            Assert.That(native.width, Is.EqualTo(3));
            using (var ready = system.Acquire<Texture2D>(_address))
            {
                Assert.That(ready.Asset, Is.SameAs(native));
            }

            lease.Dispose();
            yield return null;
            Assert.That(native == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator UrlFinalCancellationAbortsPrivateIoAndShutdownDrainsWithoutAHost()
        {
            var root = StartServer(context =>
            {
                context.Response.Close();
                return Task.CompletedTask;
            });
            var system = Initialize(new[] { new ResourceCatalogEntry("image", _address,
                new ResourceLocation(ResourceSourceId.UrlTexture, root + "cancel", typeof(Texture2D))) });
            using var request = system.AcquireAsync<Texture2D>(_address);
            request.Cancel();
            using var closed = system.ShutdownAsync();
            yield return Wait(closed);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(closed.TakeResult(), Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator StartedUrlCancellationAndImmediateReacquireCannotInheritAnAbortedGeneration()
        {
            var png = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            var count = 0;
            var root = StartServer(async context =>
            {
                var number = System.Threading.Interlocked.Increment(ref count);
                if (number == 1)
                {
                    entered.TrySetResult(true);
                    await release.Task;
                }
                try
                {
                    context.Response.ContentLength64 = png.Length;
                    await context.Response.OutputStream.WriteAsync(png, 0, png.Length);
                    context.Response.Close();
                }
                catch (HttpListenerException) when (number == 1) { }
            });
            var system = Initialize(new[] { new ResourceCatalogEntry("image", _address,
                new ResourceLocation(ResourceSourceId.UrlTexture, root + "same-key", typeof(Texture2D))) });
            using var first = system.AcquireAsync<Texture2D>(_address);
            var until = Time.realtimeSinceStartup + 8;
            while (!entered.Task.IsCompleted && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(entered.Task.IsCompleted, Is.True, "The server must confirm native transfer started before cancel.");
            using ResourceRequest<AssetLease<Texture2D>> replacement = CancelAndReplace();
            ResourceRequest<AssetLease<Texture2D>> CancelAndReplace()
            {
                first.Cancel();
                var next = system.AcquireAsync<Texture2D>(_address);
                release.TrySetResult(true);
                return next;
            }
            yield return Wait(replacement);
            Assert.That(replacement.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), replacement.Error?.ToString());
            using var lease = replacement.TakeResult();
            var native = lease.Asset;
            Assert.That(native.width, Is.EqualTo(3));
            Assert.That(System.Threading.Volatile.Read(ref count), Is.EqualTo(2));
            lease.Dispose();
            yield return null;
            Assert.That(native == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator NewlyDecodedAsyncResultDisposedUnclaimedInObserverIsActuallyDestroyed()
        {
            var system = Initialize();
            Texture2D native = null;
            using var request = system.AcquireAsync<Texture2D>(_address);
            request.Completed += value =>
            {
                using var probe = system.Acquire<Texture2D>(_address);
                native = probe.Asset;
                value.Dispose();
            };
            yield return Wait(request);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(request.IsDisposed, Is.True);
            yield return null;
            Assert.That(!ReferenceEquals(native, null) && native == null, Is.True, "A real decoded native Texture must be physically destroyed after zero ownership.");
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }
    }
}

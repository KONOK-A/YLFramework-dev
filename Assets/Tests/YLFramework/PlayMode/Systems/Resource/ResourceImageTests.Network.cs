using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.Resource;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    public partial class ResourceImageTests
    {
        private sealed class ImageServer : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly Task _listening;
            private readonly ConcurrentBag<Task> _responses = new();
            internal readonly string Root;

            internal ImageServer(Func<HttpListenerContext, Task> handler)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                Root = "http://127.0.0.1:" + ((IPEndPoint)probe.LocalEndpoint).Port + "/";
                probe.Stop();
                _listener.Prefixes.Add(Root);
                _listener.Start();
                _listening = Task.Run(async () =>
                {
                    try
                    {
                        while (_listener.IsListening)
                        {
                            var context = await _listener.GetContextAsync();
                            _responses.Add(handler(context));
                        }
                    }
                    catch (HttpListenerException) when (!_listener.IsListening) { }
                    catch (ObjectDisposedException) when (!_listener.IsListening) { }
                });
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
                Assert.That(_listening.Wait(TimeSpan.FromSeconds(3)), Is.True);
                Assert.That(Task.WaitAll(_responses.ToArray(), TimeSpan.FromSeconds(3)), Is.True, "Fixture responses must settle; never leave background writers.");
            }
        }

        private static async Task SendImage(HttpListenerContext context, byte[] bytes, int status = 200, bool chunked = false)
        {
            try
            {
                context.Response.StatusCode = status;
                if (chunked)
                {
                    context.Response.SendChunked = true;
                }
                else
                {
                    context.Response.ContentLength64 = bytes.Length;
                }

                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (HttpListenerException) { /* 测试消费者可能已经取消自己的连接。 */ }
            catch (ObjectDisposedException) { /* 失败路径归还消费者后，响应流可能已关闭。 */ }
            finally
            {
                context.Response.Close();
            }
        }

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var until = Time.realtimeSinceStartup + 8;
            while (!condition() && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "Image IO must reach a stable observable result.");
        }

        [UnityTest]
        public IEnumerator FifoKeepsQueuedOrderWhenTheLastWaiterCancelsAndTheSameIdentityRestarts()
        {
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            var entered = new ConcurrentQueue<(string Path, TaskCompletionSource<bool> Release)>();
            var closing = 0;
            using var server = new ImageServer(async context =>
            {
                var release = new TaskCompletionSource<bool>();
                entered.Enqueue((context.Request.RawUrl, release));
                if (Volatile.Read(ref closing) != 0)
                {
                    release.TrySetResult(true);
                }

                await release.Task;
                await SendImage(context, bytes);
            });
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0, maximumConcurrentLoads: 1));
            var requests = new ResourceRequest<AssetLease<Texture2D>>[4];
            try
            {
                requests[0] = system.AcquireImageAsync(server.Root + "a");
                requests[1] = system.AcquireImageAsync(server.Root + "b");
                requests[2] = system.AcquireImageAsync(server.Root + "c");
                yield return WaitUntil(() => entered.Count == 1);
                Assert.That(Assert.Throws<ResourceException>(() => system.AcquireImage(server.Root + "b")).Kind, Is.EqualTo(ResourceError.InProgress));
                requests[0].Cancel();
                requests[3] = system.AcquireImageAsync(server.Root + "a");
                entered.ToArray()[0].Release.TrySetResult(true);
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.EqualTo(3), "Restart must share or replace its record, never duplicate authority.");
                var count = 1;
                foreach (var expected in new[] { "/b", "/c", "/a" })
                {
                    count++;
                    yield return WaitUntil(() => entered.Count >= count);
                    Assert.That(entered.Count, Is.EqualTo(count), "One slot cannot start another producer before the current one finishes.");
                    var active = entered.ToArray()[count - 1];
                    Assert.That(active.Path, Is.EqualTo(expected));
                    active.Release.TrySetResult(true);
                }
                yield return WaitUntil(() => requests.All(value => value.IsDone));
                Assert.That(requests[0].Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                Assert.That(requests.Skip(1).All(value => value.Status == ResourceRequestStatus.Succeeded), Is.True);
            }
            finally
            {
                Interlocked.Exchange(ref closing, 1);
                foreach (var request in requests)
                {
                    request?.Dispose();
                }

                foreach (var call in entered)
                {
                    call.Release.TrySetResult(true);
                }
            }
            yield return null;
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            using var shutdown = system.ShutdownAsync();
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
        }

        [UnityTest]
        public IEnumerator DistinctImageLoadsAreBoundedAndCanceledQueuedConsumersNeverOpenAConnection()
        {
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            var entered = new ConcurrentQueue<string>();
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                entered.Enqueue(context.Request.RawUrl);
                await release.Task;
                await SendImage(context, bytes);
            });
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            var requests = new ResourceRequest<AssetLease<Texture2D>>[8];
            try
            {
                for (var i = 0; i < requests.Length; i++)
                {
                    requests[i] = system.AcquireImageAsync(server.Root + i);
                }

                yield return WaitUntil(() => entered.Count >= 4);
                for (var frame = 0; frame < 8; frame++)
                {
                    yield return null;
                }

                Assert.That(entered.Count, Is.EqualTo(4), "Distinct images need a producer limit, not only same-URL sharing.");
                var report = system.GetDiagnosticsReport();
                Assert.That(report.Assets.Count(x => Resource89TestApi.Read<bool>(x, "IsQueued")), Is.EqualTo(4));
                requests[6].Cancel();
                requests[7].Dispose();
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.EqualTo(6), "Unstarted zero-waiter records must leave the queue immediately.");
                release.TrySetResult(true);
                yield return WaitUntil(() => requests.All(value => value.IsDone));
                Assert.That(entered.Count, Is.EqualTo(6));
                Assert.That(entered.Contains("/6") || entered.Contains("/7"), Is.False, "Canceled queued images must not start IO.");
                for (var i = 0; i < 6; i++)
                {
                    Assert.That(requests[i].Status, Is.EqualTo(ResourceRequestStatus.Succeeded), requests[i].Error?.ToString());
                }
            }
            finally
            {
                release.TrySetResult(true);
                foreach (var request in requests)
                {
                    request?.Dispose();
                }
            }
            yield return null;
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            using var shutdown = system.ShutdownAsync();
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
        }

        [UnityTest]
        public IEnumerator ConfiguredImageLimitSharesItsSlotAndSessionExitDrainsQueuedWorkWithoutStartingIt()
        {
            var constructor = typeof(ResourceImageOptions).GetConstructors().SingleOrDefault(value =>
                value.GetParameters().Any(parameter => parameter.Name == "maximumConcurrentLoads"));
            Assert.That(constructor, Is.Not.Null, "Applications need an explicit bounded image IO configuration.");
            var options = (ResourceImageOptions)constructor.Invoke(new object[] { 1024 * 1024, 1024 * 1024, 0L, null, 128, 1 });
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            var gets = 0;
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                Interlocked.Increment(ref gets);
                await release.Task;
                await SendImage(context, bytes);
            });
            var system = Initialize(options);
            using var first = system.AcquireImageAsync(server.Root + "first");
            using var shared = system.AcquireImageAsync(server.Root + "first");
            using var queued = system.AcquireImageAsync(server.Root + "queued");
            try
            {
                yield return WaitUntil(() => Volatile.Read(ref gets) >= 1);
                for (var frame = 0; frame < 4; frame++)
                {
                    yield return null;
                }

                first.Cancel();
                Assert.That(Volatile.Read(ref gets), Is.EqualTo(1), "A shared producer consumes one slot; canceling A must not release B's slot.");
                Assert.That(shared.IsDone, Is.False);
                Resource89TestApi.EndSession("EndSession");
                Assert.That(shared.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
                Assert.That(queued.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
                release.TrySetResult(true);
                yield return WaitUntil(() => system.GetDiagnosticsSnapshot().RecordCount == 0);
                Assert.That(Volatile.Read(ref gets), Is.EqualTo(1), "Session exit must remove unstarted work rather than start new IO while draining.");
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [UnityTest]
        public IEnumerator OneHundredConsumersShareOneGetWithIndependentCancellationAndExactUrlIdentity()
        {
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            var gets = 0;
            using var server = new ImageServer(context =>
            {
                if (context.Request.RawUrl == "/Image?Version=AbC")
                {
                    Interlocked.Increment(ref gets);
                    return SendImage(context, bytes);
                }
                return SendImage(context, Array.Empty<byte>(), 404);
            });
            var system = Initialize();
            Assert.That(Assert.Throws<ResourceException>(() => system.AcquireImage(server.Root + "cold-sync")).Kind, Is.EqualTo(ResourceError.Unsupported));
            var requests = new ResourceRequest<AssetLease<Texture2D>>[100];
            var leases = new AssetLease<Texture2D>[90];
            var frames = new int[100];
            var startedFrame = Time.frameCount;
            var elapsed = Stopwatch.StartNew();
            var memory = GC.GetTotalMemory(false);
            try
            {
                for (var i = 0; i < requests.Length; i++)
                {
                    var index = i;
                    requests[i] = system.AcquireImageAsync(server.Root + "Image?Version=AbC");
                    requests[i].Completed += _ => frames[index] = Time.frameCount - startedFrame;
                }
                Assert.That(system.GetDiagnosticsSnapshot().ProducerCount, Is.EqualTo(1));
                Assert.That(system.GetDiagnosticsSnapshot().WaiterCount, Is.EqualTo(100));
                for (var i = 0; i < 10; i++)
                {
                    requests[i].Cancel();
                }

                yield return WaitUntil(() => requests.All(value => value.IsDone));
                for (var i = 10; i < requests.Length; i++)
                {
                    Assert.That(requests[i].Status, Is.EqualTo(ResourceRequestStatus.Succeeded), requests[i].Error?.ToString());
                    leases[i - 10] = requests[i].TakeResult();
                    Assert.That(leases[i - 10].Asset, Is.SameAs(leases[0].Asset));
                }
                Assert.That(Volatile.Read(ref gets), Is.EqualTo(1));
                using (var ready = system.AcquireImage(server.Root + "Image?Version=AbC"))
                {
                    Assert.That(ready.Asset, Is.SameAs(leases[0].Asset), "Ready URLs may be acquired synchronously without another GET.");
                }

                Assert.That(Volatile.Read(ref gets), Is.EqualTo(1));
                using var different = system.AcquireImageAsync(server.Root + "image?version=abc");
                yield return different;
                Assert.That(different.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(different.Error.ToString(), Does.Contain("404"));
                var completedFrames = frames.Skip(10).OrderBy(value => value).ToArray();
                Debug.Log($"YL8.9 image measurement: consumers=100, canceled=10, GET={gets}, encodedBytes={bytes.Length}, P50={completedFrames[44]} frames, P95={completedFrames[85]} frames, elapsed={elapsed.Elapsed.TotalMilliseconds:F2} ms, managedDelta={GC.GetTotalMemory(false) - memory} bytes (whole-test sample, not native memory). ");
            }
            finally
            {
                foreach (var request in requests)
                {
                    request?.Dispose();
                }

                foreach (var lease in leases)
                {
                    lease?.Dispose();
                }

                Flush(system);
            }
            yield return null;
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RefreshCannotBeOverwrittenByAnOldSharedGenerationAndDestroyReleasesTheNewImage()
        {
            var oldBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            var nextTexture = new Texture2D(11, 4, TextureFormat.RGBA32, false);
            byte[] nextBytes;
            try
            {
                nextBytes = nextTexture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(nextTexture);
            }
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                if (context.Request.RawUrl == "/old")
                {
                    entered.TrySetResult(true);
                    await release.Task;
                    await SendImage(context, oldBytes);
                }
                else
                {
                    await SendImage(context, nextBytes);
                }
            });
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            _host = new GameObject("YL89 refresh", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var target = _host.GetComponent<RawImage>();
            var component = _host.AddComponent<ResourceImage>();
            using var independent = system.AcquireImageAsync(server.Root + "old");
            Texture oldNative = null;
            Texture newNative = null;
            try
            {
                component.SetImage(server.Root + "old");
                yield return WaitUntil(() => entered.Task.IsCompleted);
                component.SetImage(server.Root + "new");
                yield return WaitUntil(() => component.Status == ResourceRequestStatus.Succeeded);
                newNative = target.texture;
                Assert.That(newNative.width, Is.EqualTo(11));
                release.TrySetResult(true);
                yield return independent;
                using var other = independent.TakeResult();
                oldNative = other.Asset;
                Assert.That(oldNative.width, Is.EqualTo(7));
                Assert.That(target.texture, Is.SameAs(newNative));
                Object.Destroy(_host);
                _host = null;
                yield return null;
                yield return null;
                Assert.That(newNative == null, Is.True, "Destroy releases the component's current lease.");
                Assert.That(other.IsValid, Is.True, "A's refresh/destroy must not revoke B's old generation.");
            }
            finally
            {
                release.TrySetResult(true);
            }
            yield return null;
            Assert.That(oldNative == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ChunkedOversizeBadImageAndNetworkFailureDoNotProduceOrCacheTextures()
        {
            using var server = new ImageServer(context => context.Request.RawUrl == "/bad"
                ? SendImage(context, Encoding.UTF8.GetBytes("not a PNG or JPEG"))
                : context.Request.RawUrl == "/large" ? SendImage(context, new byte[4096], chunked: true)
                : SendImage(context, Array.Empty<byte>(), 404));
            var system = Initialize(new ResourceImageOptions(maximumEncodedBytes: 2048));
            using var tooLarge = system.AcquireImageAsync(server.Root + "large");
            using var invalid = system.AcquireImageAsync(server.Root + "bad");
            using var missing = system.AcquireImageAsync(server.Root + "missing");
            yield return WaitUntil(() => tooLarge.IsDone && invalid.IsDone && missing.IsDone);
            Assert.That(tooLarge.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(tooLarge.Error.ToString(), Does.Contain("encoded byte budget"));
            Assert.That(invalid.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(invalid.Error.ToString(), Does.Contain("PNG or JPEG"));
            Assert.That(missing.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(missing.Error.ToString(), Does.Contain("404"));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }
    }
}

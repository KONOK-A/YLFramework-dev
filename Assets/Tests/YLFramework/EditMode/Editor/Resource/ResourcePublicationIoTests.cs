using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourcePublicationIoTests
    {
        [Test]
        public void AtomicReplacementRetriesOnlyStableMetadataAndPreservesBlockingAndCancellationFailures()
        {
            var owner = ResourceCostEvidence.RequireRunOwner();
            var root = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("AssetBundles/YLFramework/f3-io-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            ResourceLocalSnapshot.RejectLinks(root);
            // 独立发布回归不依赖成本基线先生成标准输出父目录。
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(root)));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(root))), Is.True);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "owner.txt")), owner);
            var write = typeof(ResourcePublisher).GetMethod("AtomicWrite", BindingFlags.Static | BindingFlags.NonPublic);
            var index = Path.Combine(root, "release-index.json");
            var attempts = new List<object>();
            var unblocked = 0;
            try
            {
                for (var i = 0; i < 128; i++)
                {
                    var bytes = Encoding.UTF8.GetBytes("{\"value\":" + i + "}");
                    try
                    {
                        write.Invoke(null, new object[] { root, "release-index.json", bytes, CancellationToken.None });
                    }
                    catch (TargetInvocationException error)
                    {
                        attempts.Add(new
                        {
                            Step = "unblocked",
                            Iteration = i,
                            Error = error.InnerException.ToString(),
                            error.InnerException.HResult,
                            Attributes = File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(index)).ToString(),
                            NextExists = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(index + ".next"))
                        });
                        throw;
                    }
                    CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index)));
                    unblocked++;
                }
                var old = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index));
                using (var reader = File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(index), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var error = Assert.Throws<TargetInvocationException>(() => write.Invoke(null, new object[] { root, "release-index.json", new byte[] { 1, 2, 3 }, CancellationToken.None }));
                    Assert.That(error.InnerException, Is.InstanceOf<IOException>());
                    attempts.Add(new
                    {
                        Step = "blocked-reader",
                        Error = error.InnerException.ToString(),
                        error.InnerException.HResult,
                        Win32LowWord = error.InnerException.HResult & 0xffff,
                        Attributes = File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(index)).ToString(),
                        NextExists = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(index + ".next"))
                    });
                    CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index)));
                }
                write.Invoke(null, new object[] { root, "release-index.json", old, CancellationToken.None });
                CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index)));
                var cancel = Assert.Throws<TargetInvocationException>(() => write.Invoke(null, new object[] {
                    root, "release-index.json", new byte[] { 4, 5, 6 }, new CancellationToken(true) }));
                Assert.That(cancel.InnerException, Is.InstanceOf<OperationCanceledException>());
                CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index)));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(index + ".next")), Is.False);

                var foreign = Encoding.UTF8.GetBytes("{\"value\":999}");
                using (var reader = File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(index), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var change = Task.Run(() =>
                    {
                        if (!SpinWait.SpinUntil(() => File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(index + ".next")), TimeSpan.FromSeconds(3)))
                        {
                            throw new TimeoutException("No next metadata appeared.");
                        }

                        using var output = File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(index), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        output.Write(foreign, 0, foreign.Length);
                        output.Flush(true);
                    });
                    Exception rejected = null;
                    try
                    {
                        write.Invoke(null, new object[] { root, "release-index.json", old, CancellationToken.None });
                    }
                    catch (TargetInvocationException error)
                    {
                        rejected = error.InnerException;
                    }
                    finally
                    {
                        change.GetAwaiter().GetResult();
                    }
                    attempts.Add(new
                    {
                        Step = "changed-from-refused",
                        Error = rejected?.ToString(),
                        HResult = rejected?.HResult
                    });
                    // 竞争写者也可使复查本身报sharing violation；两种情况都必须拒绝，而不是继续提交。
                    if (rejected is IOException io)
                    {
                        Assert.That(io.HResult, Is.EqualTo(unchecked((int)0x80070020u)).Or.EqualTo(unchecked((int)0x80070497u)));
                    }
                    else
                    {
                        Assert.That(rejected, Is.InstanceOf<InvalidOperationException>());
                    }

                    CollectionAssert.AreEqual(foreign, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index)));
                }
                write.Invoke(null, new object[] { root, "release-index.json", foreign, CancellationToken.None });
                using (var reader = File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(index), FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var cancellation = new CancellationTokenSource())
                {
                    var core = typeof(ResourcePublisher).GetMethod("AtomicWriteCore",
                        BindingFlags.Static | BindingFlags.NonPublic, null, new[]
                        {
                            typeof(string), typeof(string), typeof(byte[]), typeof(CancellationToken),
                            typeof(Action<string, string>), typeof(Action<int>), typeof(Action)
                        }, null);
                    Assert.That(core, Is.Not.Null, "The post-write checkpoint must use the formal AtomicWriteCore signature.");
                    var afterNextWrittenCalls = 0;
                    Action afterNextWritten = () =>
                    {
                        afterNextWrittenCalls++;
                        CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index + ".next")));
                        cancellation.Cancel();
                    };
                    Exception rejected = null;
                    try
                    {
                        core.Invoke(null, new object[] { root, "release-index.json", old, cancellation.Token, null, null, afterNextWritten });
                    }
                    catch (TargetInvocationException error)
                    {
                        rejected = error.InnerException;
                    }
                    attempts.Add(new
                    {
                        Step = "late-cancellation-refused",
                        Source = "Synchronous post-write checkpoint; cancellation is checked by formal AtomicWriteCore",
                        AfterNextWrittenCalls = afterNextWrittenCalls,
                        CancellationRequested = cancellation.IsCancellationRequested,
                        Error = rejected?.ToString(),
                        HResult = rejected?.HResult,
                        NextExists = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(index + ".next"))
                    });
                    Assert.That(afterNextWrittenCalls, Is.EqualTo(1));
                    Assert.That(rejected, Is.InstanceOf<OperationCanceledException>());
                    Assert.That(((OperationCanceledException)rejected).CancellationToken, Is.EqualTo(cancellation.Token));
                    CollectionAssert.AreEqual(foreign, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index)));
                    CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(index + ".next")));
                }
            }
            finally
            {
                ResourceCostEvidence.Write("publication-io-probe", new
                {
                    Root = root,
                    UnblockedReplacements = unblocked,
                    Observations = attempts,
                    Boundary = "Real Unity Mono File.Replace on the original G: publication volume. The explicit reader has its own HRESULT (32 observed); it is not proof of the owner/cause of the intermittent 1175."
                });
                Assert.That(ResourceCostEvidence.RequireRunOwner(), Is.EqualTo(owner));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "owner.txt"))), Is.EqualTo(owner));
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
            }
        }
    }
}

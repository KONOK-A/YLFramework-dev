using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    /// <summary>准确HRESULT受控故障；只替换系统调用边界，不复制正式重试控制流。</summary>
    public sealed class ResourcePublicationRetryTests
    {
        private const int Retryable = unchecked((int)0x80070497u);
        private static readonly byte[] _old = Encoding.UTF8.GetBytes("{\"value\":\"old\"}");
        private static readonly byte[] _next = Encoding.UTF8.GetBytes("{\"value\":\"next\"}");
        private static readonly byte[] _competitor = Encoding.UTF8.GetBytes("{\"value\":\"competitor\"}");

        [TestCase("cancel")]
        [TestCase("from")]
        [TestCase("to")]
        public void CancellationOrMetadataChangeAfter1175IsRejectedBeforeTheNextReplacement(string action)
        {
            using var owned = new OwnedCase(action);
            using var cancellation = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim();
            using var changed = new ManualResetEventSlim();
            using var abort = new ManualResetEventSlim();
            owned.WorkerJoined = false;
            var worker = Task.Run(() =>
            {
                if (!entered.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The formal 1175 backoff was never entered.");
                }

                if (abort.IsSet)
                {
                    return;
                }

                try
                {
                    if (action == "cancel")
                    {
                        cancellation.Cancel();
                    }
                    else
                    {
                        Write(action == "from" ? owned.Target : owned.Temporary, _competitor);
                    }

                    owned.Record(action == "cancel" ? "CancellationApplied" : action == "from" ? "FromChanged" : "ToChanged");
                }
                finally
                {
                    changed.Set();
                }
            });
            Exception error;
            try
            {
                error = owned.Invoke(owned.Replacement(_ => Retryable), delay =>
                {
                    owned.BeginBackoff(delay);
                    entered.Set();
                    if (!changed.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The controlled change did not complete.");
                    }

                    owned.Record("BackoffResumed");
                }, cancellation.Token);
            }
            finally
            {
                abort.Set();
                entered.Set();
                changed.Set();
                try
                {
                    worker.GetAwaiter().GetResult();
                }
                finally
                {
                    owned.WorkerJoined = worker.IsCompleted;
                }
            }
            if (action == "cancel")
            {
                Assert.That(error, Is.InstanceOf<OperationCanceledException>());
            }
            else
            {
                Assert.That(error, Is.InstanceOf<InvalidOperationException>());
                Assert.That(error.InnerException?.HResult, Is.EqualTo(Retryable));
            }
            Assert.That(owned.Attempts, Is.EqualTo(1), "No replacement may follow a changed/cancelled backoff.");
            Assert.That(owned.RealReplacementCalls, Is.Zero);
            CollectionAssert.AreEqual(new[] { 10 }, owned.RequestedWaits);
            CollectionAssert.AreEqual(action == "from" ? _competitor : _old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Target)));
            CollectionAssert.AreEqual(action == "to" ? _competitor : _next, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Temporary)));
            CollectionAssert.AreEqual(new[] { "ReplaceAttempt", "ControlledFailure", "BackoffEntered",
                action == "cancel" ? "CancellationApplied" : action == "from" ? "FromChanged" : "ToChanged", "BackoffResumed", "TerminalError" }, owned.Kinds);
            owned.VerifyDefaultEntryAndMarkComplete();
        }

        [TestCase(1)]
        [TestCase(2)]
        public void Transient1175EventuallyExecutesExactlyOneRealReplacement(int failures)
        {
            using var owned = new OwnedCase("eventual-" + failures);
            var error = owned.Invoke(owned.Replacement(attempt => attempt <= failures ? Retryable : (int?)null), owned.VirtualBackoff);
            Assert.That(error, Is.Null, error?.ToString());
            Assert.That(owned.Attempts, Is.EqualTo(failures + 1));
            Assert.That(owned.RealReplacementCalls, Is.EqualTo(1));
            Assert.That(owned.RealCommits, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { 10, 25 }.Take(failures), owned.RequestedWaits);
            CollectionAssert.AreEqual(_next, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Target)));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Temporary)), Is.False);
            var expected = Enumerable.Range(0, failures).SelectMany(_ => new[] {
                "ReplaceAttempt", "ControlledFailure", "BackoffEntered", "BackoffResumed" }).Concat(new[] {
                "ReplaceAttempt", "RealReplaceCalled", "RealReplaceCommitted", "ReturnedSuccess" });
            CollectionAssert.AreEqual(expected, owned.Kinds);
            owned.VerifyDefaultEntryAndMarkComplete();
        }

        [Test]
        public void Persistent1175ExhaustsFiveRetriesWithoutSubmittingOrDeletingTheOldFile()
        {
            using var owned = new OwnedCase("exhausted");
            var error = owned.Invoke(owned.Replacement(_ => Retryable), owned.VirtualBackoff);
            Assert.That(error, Is.TypeOf<ControlledReplaceException>());
            Assert.That(error.HResult, Is.EqualTo(Retryable));
            Assert.That(owned.Attempts, Is.EqualTo(6), "One first attempt plus five retries.");
            Assert.That(owned.RealReplacementCalls, Is.Zero);
            CollectionAssert.AreEqual(new[] { 10, 25, 50, 100, 200 }, owned.RequestedWaits);
            CollectionAssert.AreEqual(_old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Target)));
            CollectionAssert.AreEqual(_next, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Temporary)));
            var expected = Enumerable.Range(0, 5).SelectMany(_ => new[] {
                "ReplaceAttempt", "ControlledFailure", "BackoffEntered", "BackoffResumed" }).Concat(new[] {
                "ReplaceAttempt", "ControlledFailure", "TerminalError" });
            CollectionAssert.AreEqual(expected, owned.Kinds);
            owned.VerifyDefaultEntryAndMarkComplete();
        }

        [TestCase(32)]
        [TestCase(1176)]
        [TestCase(1177)]
        public void OtherHResultsNeverEnterThe1175Backoff(int win32)
        {
            using var owned = new OwnedCase("classified-" + win32);
            var hresult = unchecked((int)(0x80070000u | (uint)win32));
            var error = owned.Invoke(owned.Replacement(_ => hresult), owned.VirtualBackoff);
            Assert.That(error, Is.TypeOf<ControlledReplaceException>());
            Assert.That(error.HResult, Is.EqualTo(hresult));
            Assert.That(owned.Attempts, Is.EqualTo(1));
            Assert.That(owned.RealReplacementCalls, Is.Zero);
            Assert.That(owned.RequestedWaits, Is.Empty);
            CollectionAssert.AreEqual(_old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Target)));
            CollectionAssert.AreEqual(_next, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(owned.Temporary)));
            CollectionAssert.AreEqual(new[] { "ReplaceAttempt", "ControlledFailure", "TerminalError" }, owned.Kinds);
            owned.VerifyDefaultEntryAndMarkComplete();
        }

        private static void Write(string path, byte[] bytes)
        {
            using var stream = File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        private sealed class ControlledReplaceException : IOException
        {
            internal ControlledReplaceException(int hresult) : base("Controlled failure at the File.Replace boundary.") => HResult = hresult;
        }

        private sealed class OrderedEvent
        {
            public int Sequence;
            public int Attempt;
            public int ThreadId;
            public string Kind;
            public int? HResult;
            public int? RequestedWaitMilliseconds;
        }

        private sealed class OwnedCase : IDisposable
        {
            private readonly string _id;
            private readonly string _root;
            private readonly string _owner;
            private readonly List<OrderedEvent> _events = new();
            private Exception _terminal;
            private bool _isComplete;
            private bool _isDefaultEntryVerified;
            internal bool WorkerJoined = true;
            internal int Attempts;
            internal int RealReplacementCalls;
            internal int RealCommits;
            internal readonly List<int> RequestedWaits = new();
            internal string Target => Path.Combine(_root, "release-index.json");
            internal string Temporary => Target + ".next";
            internal string[] Kinds
            {
                get
                {
                    lock (_events)
                    {
                        return _events.Select(x => x.Kind).ToArray();
                    }
                }
            }

            internal OwnedCase(string id)
            {
                this._id = id;
                _owner = ResourceCostEvidence.RequireRunOwner();
                var run = ResourceCostEvidence.DirectoryPath;
                _root = Path.Combine(run, "files", id);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root)), Is.False);
                ResourceLocalSnapshot.RejectLinks(_root);
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_root));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_root, "owner.txt")), _owner);
                Write(Target, _old);
            }

            internal void Record(string kind, int? hresult = null, int? delay = null)
            {
                lock (_events)
                {
                    _events.Add(new OrderedEvent
                    {
                        Sequence = _events.Count + 1,
                        Kind = kind,
                        Attempt = Attempts,
                        HResult = hresult,
                        RequestedWaitMilliseconds = delay,
                        ThreadId = Thread.CurrentThread.ManagedThreadId
                    });
                }
            }

            internal Action<string, string> Replacement(Func<int, int?> fault) => (replacement, target) =>
            {
                Assert.That(replacement, Is.EqualTo(Temporary));
                Assert.That(target, Is.EqualTo(Target));
                Attempts++;
                Record("ReplaceAttempt");
                var code = fault(Attempts);
                if (code.HasValue)
                {
                    Record("ControlledFailure", code);
                    throw new ControlledReplaceException(code.Value);
                }
                RealReplacementCalls++;
                Record("RealReplaceCalled");
                try
                {
                    File.Replace(replacement, target, null);
                }
                catch (IOException error)
                {
                    Record("ActualOsFailure", error.HResult);
                    throw;
                }
                RealCommits++;
                Record("RealReplaceCommitted");
            };

            internal void BeginBackoff(int milliseconds)
            {
                RequestedWaits.Add(milliseconds);
                Record("BackoffEntered", delay: milliseconds);
            }

            internal void VirtualBackoff(int milliseconds)
            {
                BeginBackoff(milliseconds);
                Record("BackoffResumed");
            }

            internal Exception Invoke(Action<string, string> replace, Action<int> backoff, CancellationToken cancellation = default)
            {
                var method = typeof(ResourcePublisher).GetMethod("AtomicWriteCore",
                    BindingFlags.Static | BindingFlags.NonPublic, null, new[]
                    {
                        typeof(string), typeof(string), typeof(byte[]), typeof(CancellationToken),
                        typeof(Action<string, string>), typeof(Action<int>), typeof(Action)
                    }, null);
                Assert.That(method, Is.Not.Null, "A scoped boundary seam is needed to prove the existing retry path deterministically; this is not a product-failure assertion.");
                try
                {
                    method.Invoke(null, new object[] { _root, "release-index.json", _next, cancellation, replace, backoff, null });
                    Record("ReturnedSuccess");
                    return null;
                }
                catch (TargetInvocationException error)
                {
                    _terminal = error.InnerException;
                    Record("TerminalError", _terminal.HResult);
                    return _terminal;
                }
            }

            internal void VerifyDefaultEntryAndMarkComplete()
            {
                // 新文件上的原四参数入口；不回写竞争变化后的From/To，也不沿用本case替身。
                var target = Path.Combine(_root, "default-probe.json");
                Write(target, _old);
                var method = typeof(ResourcePublisher).GetMethod("AtomicWrite", BindingFlags.Static | BindingFlags.NonPublic);
                method.Invoke(null, new object[] { _root, "default-probe.json", _next, CancellationToken.None });
                CollectionAssert.AreEqual(_next, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(target)));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(target + ".next")), Is.False);
                _isDefaultEntryVerified = true;
                _isComplete = true;
            }

            private static object State(string path)
            {
                if (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    return new
                    {
                        Exists = false,
                        Bytes = (string)null,
                        Sha256 = (string)null
                    };
                }

                var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                using var sha = SHA256.Create();
                return new
                {
                    Exists = true,
                    Bytes = Encoding.UTF8.GetString(bytes),
                    Sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                };
            }

            public void Dispose()
            {
                var from = State(Target);
                var to = State(Temporary);
                Exception cleanupError = null;
                var handlesClosed = false;
                var removed = false;
                try
                {
                    foreach (var file in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_root)))
                    {
                        using var probe = File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(file), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    handlesClosed = true;
                    if (_isComplete)
                    {
                        Assert.That(ResourceCostEvidence.RequireRunOwner(), Is.EqualTo(_owner));
                        Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_root, "owner.txt"))), Is.EqualTo(_owner));
                        var allowed = new HashSet<string>(new[] { "owner.txt", "release-index.json", "release-index.json.next", "default-probe.json", "default-probe.json.next" });
                        Assert.That(Directory.GetDirectories(global::YLFramework.Testing.PackageTestPaths.Physical(_root)), Is.Empty);
                        foreach (var path in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_root)))
                        {
                            Assert.That(allowed.Contains(Path.GetFileName(path)), Is.True, "Unknown cleanup file.");
                            Assert.That((int)(File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(path)) & FileAttributes.ReparsePoint), Is.Zero);
                        }
                        foreach (var path in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_root)))
                        {
                            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                        }

                        Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root));
                        removed = true;
                    }
                }
                catch (Exception error)
                {
                    cleanupError = error;
                }
                finally
                {
                    ResourceCostEvidence.Write("retry-" + _id, new
                    {
                        Case = _id,
                        Root = _root,
                        Source = "Formal ResourcePublisher.AtomicWriteCore with per-call replace/backoff boundaries",
                        Complete = _isComplete,
                        Attempts,
                        Retries = Math.Max(0, Attempts - 1),
                        RequestedWaitMilliseconds = RequestedWaits,
                        RealReplacementCalls,
                        RealCommits,
                        ErrorType = _terminal?.GetType().FullName,
                        ErrorHResult = _terminal?.HResult,
                        FinalFromBeforeCleanup = from,
                        FinalToBeforeCleanup = to,
                        Events = _events.ToArray(),
                        WorkerJoined,
                        DefaultEntryVerified = _isDefaultEntryVerified,
                        ExclusiveHandleProbesPassed = handlesClosed,
                        OwnedFilesDeleted = removed,
                        PreservedOnFailure = !removed,
                        CleanupError = cleanupError?.ToString(),
                        Boundary = "Controlled HRESULTs establish branch behavior, not unknown OS contention. Virtual waits record requested delays, not scheduler timing. 1176/1177 negative cases classify only; they do not simulate OS partial-commit effects."
                    });
                }
                if (cleanupError != null)
                {
                    throw new InvalidOperationException("Owned retry fixture cleanup failed.", cleanupError);
                }
            }
        }
    }
}

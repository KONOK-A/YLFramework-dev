using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using YLFramework.RedDots;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public class ReddotTests
    {
        [TearDown]
        public void Clean() => ReddotManager.Reset();

        [TestCase("Mail", true)]
        [TestCase("Mail/Gift", true)]
        [TestCase("Mai", false)]
        [TestCase("Mailbox", false)]
        [TestCase(null, false)]
        public void PathsMatchWholeSegments(string path, bool expected)
        {
            ReddotManager.AddPath(" /Mail//Gift/ ");
            Assert.That(ReddotManager.ShouldBeDisplayed(path), Is.EqualTo(expected));
        }

        [Test]
        public void ResetNotifiesAfterClearingAndDuplicateAddDoesNotNotify()
        {
            var displays = new List<bool>();
            Action changed = () => displays.Add(ReddotManager.ShouldBeDisplayed("Mail"));
            var token = ReddotManager.RegisterChanged(changed);
            try
            {
                ReddotManager.AddPath("Mail");
                ReddotManager.AddPath("/Mail/");
                ReddotManager.Reset();
                Assert.That(displays, Is.EqualTo(new[] { true, false }));
                token.Unregister();
                ReddotManager.AddPath("Mail");
                Assert.That(displays.Count, Is.EqualTo(2));
            }
            finally
            {
                token.Unregister();
            }
        }

        [Test]
        public void SamePathSourcesAreReleasedIndependently()
        {
            using var first = ReddotManager.RegisterPath("Mail/Gift");
            using var second = ReddotManager.RegisterPath("Mail/Gift/");
            first.Dispose();
            first.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            second.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
        }

        [Test]
        public void OldRegistrationCannotRemoveNewPathAfterReset()
        {
            using var old = ReddotManager.RegisterPath("Mail");
            ReddotManager.Reset();
            using var current = ReddotManager.RegisterPath("Mail");
            old.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            current.Dispose();
        }

        [Test]
        public void CollectionRemoveDoesNotStealIndependentSource()
        {
            using var source = ReddotManager.RegisterPath("Mail");
            ReddotManager.AddPath("Mail");
            ReddotManager.RemovePath("Mail");
            ReddotManager.RemovePath(null);
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            source.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
        }

        [Test]
        public void FailedRegistrationPublishesRollbackAndPreservesBothObserverErrors()
        {
            var visible = false;
            var display = ReddotManager.RegisterChanged(() => visible = ReddotManager.ShouldBeDisplayed("Mail"));
            var throwing = ReddotManager.RegisterChanged(() => throw new InvalidOperationException("observer-marker"));
            try
            {
                var error = Assert.Throws<AggregateException>(() => ReddotManager.RegisterPath("Mail"));
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
                Assert.That(visible, Is.False, "回滚后的状态也必须送给已经刷新过的观察者。");
                Assert.That(error.InnerExceptions, Has.Count.EqualTo(2));
                Assert.That(error.InnerExceptions, Has.All.Message.EqualTo("observer-marker"));
            }
            finally
            {
                display.Unregister();
                throwing.Unregister();
            }
        }
    }

    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public sealed class ReddotDiagnosticTests
    {
        [SetUp]
        public void Prepare() => ResetSession();

        [TearDown]
        public void Clean() => ResetSession();

        [Test]
        public void DiagnosticsCopySortedOwnSourcesAndNeverRegisterAnImplicitRoot()
        {
            using var second = ReddotManager.RegisterSource(" /Mail//Gift/ ", "z", 3);
            using var first = ReddotManager.RegisterSource("Mail/Gift", "a", 2);
            using var lease = ReddotManager.RegisterPath("Mail/Gift");
            ReddotManager.AddPath("Mail/Gift");
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
            using var listener = ReddotManager.Subscribe("Mail", _ => { }, false);
            var global = ReddotManager.RegisterChanged(() => { });
            try
            {
                var snapshot = Capture();
                Assert.That(Nodes(snapshot).Select(node => Read<string>(node, "Path")),
                    Is.EqualTo(new[] { "Mail/", "Mail/Gift/" }));
                var parent = Node(snapshot, "Mail/");
                var child = Node(snapshot, "Mail/Gift/");
                Assert.That(Read<long>(parent, "Value"), Is.EqualTo(1));
                Assert.That(Read<ReddotAggregation>(parent, "Aggregation"), Is.EqualTo(ReddotAggregation.Any));
                Assert.That(Read<int>(parent, "SubscriberCount"), Is.EqualTo(1));
                Assert.That(Read<long>(parent, "OwnValue"), Is.Zero);
                Assert.That(Read<long>(child, "OwnValue"), Is.EqualTo(7));
                Assert.That(Read<long>(child, "LeaseCount"), Is.EqualTo(1));
                Assert.That(Read<bool>(child, "HasPath"), Is.True);
                Assert.That(Read<int>(snapshot, "GlobalSubscriberCount"), Is.EqualTo(1));
                var sources = Items(Read<object>(child, "Sources"));
                Assert.That(sources.Select(source => Read<string>(source, "SourceId")), Is.EqualTo(new[] { "a", "z" }));
                Assert.That(sources.Select(source => Read<long>(source, "Value")), Is.EqualTo(new long[] { 2, 3 }));
                Assert.That(Items(Read<object>(parent, "Sources")), Is.Empty, "Ancestors must not pretend to own descendants' sources.");
                Assert.That(Read<int>(snapshot, "DirtyNodeCount"), Is.Zero);
                Assert.That(Read<int>(snapshot, "BatchDepth"), Is.Zero);
            }
            finally
            {
                global.Unregister();
            }
        }

        [Test]
        public void CapturedCollectionsAndRecordsCannotMutateOrFollowLiveState()
        {
            using var source = ReddotManager.RegisterSource("Mail/Gift", "gift", 2);
            var snapshot = Capture();
            var child = Node(snapshot, "Mail/Gift/");
            var sources = Read<object>(child, "Sources");
            foreach (var collection in new[] { Read<object>(snapshot, "Nodes"), sources })
            {
                Assert.That(collection, Is.InstanceOf<System.Collections.IList>());
                var list = (System.Collections.IList)collection;
                Assert.That(list.IsReadOnly, Is.True);
                Assert.Throws<NotSupportedException>(() => list.Clear());
            }
            foreach (var record in new[] { snapshot, child, Items(sources)[0] })
            {
                Assert.That(record.GetType().IsSealed, Is.True);
                Assert.That(record.GetType().GetFields(), Is.Empty);
                Assert.That(record.GetType().GetProperties().All(property => property.GetSetMethod(true) == null), Is.True);
            }
            source.SetValue(9);
            source.Dispose();
            ReddotManager.Reset();
            Assert.That(Read<long>(child, "Value"), Is.EqualTo(2));
            Assert.That(Read<long>(Items(sources)[0], "Value"), Is.EqualTo(2));
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
        }

        [Test]
        public void ExplicitCaptureDoesNotFlushBatchOrPolluteNotificationsAndOrdinaryReadsDoNotCapture()
        {
            using var source = ReddotManager.RegisterSource("Mail/Gift", "gift");
            var values = new List<long>();
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            var before = Capture();
            using (ReddotManager.BeginBatch())
            using (ReddotManager.BeginBatch())
            {
                source.SetValue(4);
                for (var index = 0; index < 100; index++)
                {
                    Assert.That(ReddotManager.GetValue("Mail/"), Is.EqualTo(4));
                    Assert.That(ReddotManager.GetSnapshot("Mail/").Value, Is.EqualTo(4));
                    source.SetValue(4);
                }
                var during = Capture();
                Assert.That(Read<long>(during, "CaptureSequence"), Is.EqualTo(Read<long>(before, "CaptureSequence") + 1));
                Assert.That(Read<int>(during, "BatchDepth"), Is.EqualTo(2));
                Assert.That(Read<int>(during, "DirtyNodeCount"), Is.EqualTo(2));
                Assert.That(Read<bool>(during, "GlobalPending"), Is.True);
                Assert.That(Read<bool>(Node(during, "Mail/"), "IsDirty"), Is.True);
                Assert.That(values, Is.Empty);
            }
            Assert.That(values, Is.EqualTo(new long[] { 4 }));
            var after = Capture();
            Assert.That(Read<int>(after, "DirtyNodeCount"), Is.Zero);
            Assert.That(Read<bool>(after, "GlobalPending"), Is.False);
            Assert.That(Read<int>(after, "BatchDepth"), Is.Zero);
        }

        [Test]
        public void CaptureInsideReentrantObserverReportsPendingWaveWithoutRunningIt()
        {
            using var source = ReddotManager.RegisterSource("Mail/Gift", "gift");
            var values = new List<long>();
            object captured = null;
            IDisposable queued = null;
            using var subscription = ReddotManager.Subscribe("Mail", snapshot =>
            {
                values.Add(snapshot.Value);
                if (snapshot.Value == 1)
                {
                    source.SetValue(2);
                    queued = ReddotManager.Subscribe("Queued", _ => { });
                    captured = Capture();
                    Assert.That(values.Count, Is.EqualTo(1));
                }
            }, false);
            try
            {
                source.SetValue(1);
                Assert.That(values, Is.EqualTo(new long[] { 1, 2 }));
                Assert.That(Read<bool>(captured, "IsFlushing"), Is.True);
                Assert.That(Read<int>(captured, "PendingInitialCount"), Is.EqualTo(1));
                Assert.That(Read<int>(captured, "DirtyNodeCount"), Is.EqualTo(2));
                Assert.That(Read<long>(Node(captured, "Mail/"), "Value"), Is.EqualTo(2));
            }
            finally
            {
                queued?.Dispose();
            }
        }

        [Test]
        public void ResetAdvancesDiagnosticGenerationWithoutChangingOldHandleSemantics()
        {
            using var old = ReddotManager.RegisterSource("Mail", "gift", 2);
            using var listener = ReddotManager.Subscribe("Mail", _ => { }, false);
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Max);
            var before = Capture();
            ReddotManager.Reset();
            using var current = ReddotManager.RegisterSource("Mail", "gift", 7);
            old.Dispose();
            Assert.Throws<ObjectDisposedException>(() => old.SetValue(1));
            var after = Capture();
            Assert.That(Read<long>(after, "Generation"), Is.GreaterThan(Read<long>(before, "Generation")));
            Assert.That(Read<ReddotAggregation>(Node(after, "Mail/"), "Aggregation"), Is.EqualTo(ReddotAggregation.Max));
            Assert.That(Read<int>(Node(after, "Mail/"), "SubscriberCount"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(7));
            ResetSession();
            var next = Capture();
            Assert.That(Read<long>(next, "Generation"), Is.GreaterThan(Read<long>(after, "Generation")));
            Assert.That(Nodes(next), Is.Empty);
        }

        private static object Capture()
        {
            var method = typeof(ReddotManager).GetMethod("CaptureDiagnostics", Type.EmptyTypes);
            Assert.That(method, Is.Not.Null, "5D requires an explicit read-only diagnostic capture, never a per-frame sampler.");
            Assert.That(method.ReturnType.FullName, Is.EqualTo("YLFramework.RedDots.ReddotDiagnosticSnapshot"));
            return method.Invoke(null, null);
        }

        private static T Read<T>(object record, string name) => (T)record.GetType().GetProperty(name).GetValue(record);
        private static object[] Items(object collection) => ((System.Collections.IEnumerable)collection).Cast<object>().ToArray();
        private static object[] Nodes(object snapshot) => Items(Read<object>(snapshot, "Nodes"));
        private static object Node(object snapshot, string path) => Nodes(snapshot).Single(node => Read<string>(node, "Path") == path);
        private static void ResetSession() => typeof(ReddotManager)
            .GetMethod("ResetSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
    }

    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public sealed class ReddotPerformanceTests
    {
        [TestCase("Mail/Gift/", "Mail/Gift/")]
        [TestCase("Mail/Gift", "Mail/Gift/")]
        [TestCase("/Mail//Gift/", "Mail/Gift/")]
        [TestCase("Mail/ \tGift /", "Mail/Gift/")]
        [TestCase("\u2003Mail\u00a0/Gift/", "Mail/Gift/")]
        [TestCase("Mail/In side/", "Mail/In side/")]
        [TestCase("///", "")]
        [TestCase(null, "")]
        public void CanonicalFastPathPreservesEverySegmentAndWhitespaceRule(string input, string expected)
        {
            Assert.That(ReddotManager.NormalizePath(input), Is.EqualTo(expected));
        }

        [Test]
        public void CanonicalPathCanBeReusedWithoutAllocatingAnotherString()
        {
            var path = new string("Mail/Gift/".ToCharArray());
            Assert.That(ReddotManager.NormalizePath(path), Is.SameAs(path));
        }

        [Test]
        public void ActualMultilevelUpdatesBatchAndWarmedReadsKeepExactValuesAndNotificationCounts()
        {
            var unbatched = Measure(false);
            var batched = Measure(true);
            Assert.That(unbatched.FinalValue, Is.EqualTo(24577));
            Assert.That(batched.FinalValue, Is.EqualTo(unbatched.FinalValue));
            Assert.That(unbatched.RootNotifications, Is.EqualTo(24577));
            Assert.That(batched.RootNotifications, Is.EqualTo(129));
            Assert.That(unbatched.LeafNotifications, Is.EqualTo(24576));
            Assert.That(batched.LeafNotifications, Is.EqualTo(8192));
            Assert.That(unbatched.GlobalNotifications, Is.EqualTo(24577));
            Assert.That(batched.GlobalNotifications, Is.EqualTo(129));
            Assert.That(unbatched.MaximumCallbackDepth, Is.EqualTo(1));
            Assert.That(batched.MaximumCallbackDepth, Is.EqualTo(1));
            TestContext.WriteLine("Real core, not Unity native: machine={0}; OS={1}; CPU={2}; processors={3}; CLR={4}; pointerBits={5}",
                Environment.MachineName, Environment.OSVersion, Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                Environment.ProcessorCount, Environment.Version, IntPtr.Size * 8);
            foreach (var result in new[] { unbatched, batched })
            {
                TestContext.WriteLine("batch={0}; groups=4; branchesPerGroup=4; leavesPerBranch=4; pathDepth=4; leaves=64; sourcesPerLeaf=3; echoSources=1; warmIterations=8; iterations=128; updates=24576; final={1}; rootNotifications={2}; leafNotifications={3}; globalNotifications={4}; maxCallbackDepth={5}; updateMs={6:F3}; updateAllocatedBytes={7}; warmedReadPairs=100000; readChecksum={8}; readMs={9:F3}; readAllocatedBytes={10}; diagnosticNodes={11}; diagnosticSources={12}; diagnosticSubscribers={13}; diagnosticMs={14:F3}; diagnosticAllocatedBytes={15}",
                    result.Batch, result.FinalValue, result.RootNotifications, result.LeafNotifications, result.GlobalNotifications,
                    result.MaximumCallbackDepth, result.UpdateMilliseconds, result.UpdateAllocatedBytes, result.ReadChecksum,
                    result.ReadMilliseconds, result.ReadAllocatedBytes, result.DiagnosticNodes, result.DiagnosticSources,
                    result.DiagnosticSubscribers, result.DiagnosticMilliseconds, result.DiagnosticAllocatedBytes);
                Assert.That(result.DiagnosticNodes, Is.EqualTo(86));
                Assert.That(result.DiagnosticSources, Is.EqualTo(193));
                Assert.That(result.DiagnosticSubscribers, Is.EqualTo(65));
            }
            Assert.That(unbatched.ReadAllocatedBytes, Is.Zero, "Warmed canonical GetValue/GetSnapshot must not allocate normalized path copies.");
            Assert.That(batched.ReadAllocatedBytes, Is.Zero);
        }

        private sealed class Measurement
        {
            internal bool Batch;
            internal long FinalValue;
            internal int RootNotifications;
            internal int LeafNotifications;
            internal int GlobalNotifications;
            internal int MaximumCallbackDepth;
            internal double UpdateMilliseconds;
            internal long UpdateAllocatedBytes;
            internal long ReadChecksum;
            internal double ReadMilliseconds;
            internal long ReadAllocatedBytes;
            internal int DiagnosticNodes;
            internal int DiagnosticSources;
            internal int DiagnosticSubscribers;
            internal double DiagnosticMilliseconds;
            internal long DiagnosticAllocatedBytes;
        }

        private static Measurement Measure(bool batch)
        {
            ReddotManager.Reset();
            var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes)
                ?? throw new InvalidOperationException("This measurement requires a real per-thread allocation counter."));
            var result = new Measurement { Batch = batch };
            var sources = new List<ReddotSource>();
            var subscriptions = new List<IDisposable>();
            using var echo = ReddotManager.RegisterSource("Perf/Echo/", "echo");
            var echoed = false;
            var depth = 0;
            using var root = ReddotManager.Subscribe("Perf/", snapshot =>
            {
                depth++;
                result.MaximumCallbackDepth = Math.Max(result.MaximumCallbackDepth, depth);
                result.RootNotifications++;
                if (snapshot.Value != ReddotManager.GetValue("Perf/"))
                {
                    throw new InvalidOperationException("The representative observer saw an uncommitted root value.");
                }
                if (!echoed && snapshot.Value > 0)
                {
                    echoed = true;
                    echo.SetValue(1);
                }
                depth--;
            }, false);
            var global = ReddotManager.RegisterChanged(() => result.GlobalNotifications++);
            try
            {
                for (var index = 0; index < 64; index++)
                {
                    var path = "Perf/G" + index / 16 + "/B" + index / 4 % 4 + "/L" + index % 4 + "/";
                    for (var source = 0; source < 3; source++)
                    {
                        sources.Add(ReddotManager.RegisterSource(path, "s" + source));
                    }
                    subscriptions.Add(ReddotManager.Subscribe(path, snapshot =>
                    {
                        result.LeafNotifications++;
                        Assert.That(snapshot.Value, Is.GreaterThan(0));
                    }, false));
                }
                void Update(int iterations)
                {
                    for (var iteration = 1; iteration <= iterations; iteration++)
                    {
                        using var scope = batch ? ReddotManager.BeginBatch() : null;
                        foreach (var source in sources)
                        {
                            source.SetValue(iteration);
                        }
                    }
                }
                Update(8);
                foreach (var subscription in subscriptions)
                {
                    subscription.Dispose();
                }
                subscriptions.Clear();
                // Reset values without measuring fixture construction or reset notifications.
                using (ReddotManager.BeginBatch())
                {
                    foreach (var source in sources)
                    {
                        source.SetValue(0);
                    }
                    echo.SetValue(0);
                }
                for (var index = 0; index < 64; index++)
                {
                    subscriptions.Add(ReddotManager.Subscribe("Perf/G" + index / 16 + "/B" + index / 4 % 4 + "/L" + index % 4 + "/",
                        _ => result.LeafNotifications++, false));
                }
                result.RootNotifications = result.LeafNotifications = result.GlobalNotifications = result.MaximumCallbackDepth = 0;
                echoed = false;
                allocated();
                var watch = Stopwatch.StartNew();
                var before = allocated();
                Update(128);
                result.UpdateAllocatedBytes = allocated() - before;
                watch.Stop();
                result.UpdateMilliseconds = watch.Elapsed.TotalMilliseconds;
                result.FinalValue = ReddotManager.GetValue("Perf/");
                const string ReadPath = "Perf/G0/B0/L0/";
                for (var index = 0; index < 1024; index++)
                {
                    ReddotManager.GetValue(ReadPath);
                    ReddotManager.GetSnapshot(ReadPath);
                }
                watch.Restart();
                before = allocated();
                for (var index = 0; index < 100000; index++)
                {
                    result.ReadChecksum += ReddotManager.GetValue(ReadPath) + ReddotManager.GetSnapshot(ReadPath).Value;
                }
                result.ReadAllocatedBytes = allocated() - before;
                watch.Stop();
                result.ReadMilliseconds = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                before = allocated();
                var diagnostic = ReddotManager.CaptureDiagnostics();
                result.DiagnosticAllocatedBytes = allocated() - before;
                watch.Stop();
                result.DiagnosticMilliseconds = watch.Elapsed.TotalMilliseconds;
                var nodes = diagnostic.Nodes.Where(node => node.Path.StartsWith("Perf/", StringComparison.Ordinal)).ToArray();
                result.DiagnosticNodes = nodes.Length;
                result.DiagnosticSources = nodes.Sum(node => node.Sources.Count);
                result.DiagnosticSubscribers = nodes.Sum(node => node.SubscriberCount);
                Assert.That(result.ReadChecksum, Is.EqualTo(76800000));
                Assert.That(sources.All(source => source.Value == 128), Is.True);
                return result;
            }
            finally
            {
                global.Unregister();
                foreach (var subscription in subscriptions)
                {
                    subscription.Dispose();
                }
                root.Dispose();
                using var scope = ReddotManager.BeginBatch();
                foreach (var source in sources)
                {
                    source.Dispose();
                }
                ReddotManager.Reset();
            }
        }
    }

    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public sealed class ReddotReadStateTests
    {
        [SetUp]
        public void Prepare() => ResetSession();

        [TearDown]
        public void Clean() => ResetSession();

        [Test]
        public void SeenVersionOnlySuppressesThatVersion()
        {
            using var state = new ReddotReadState("account/slot-0");
            state.MarkRead(key: "mail", version: "v10");
            Assert.That(state.IsRead(key: "mail", version: "v10"), Is.True);
            Assert.That(state.IsRead("mail", "v2"), Is.False);
            state.MarkRead("mail", "v2");
            Assert.That(state.IsRead("mail", "v10"), Is.True);
            Assert.That(state.IsRead("mail", "v2"), Is.True);
            Assert.That(state.IsRead("Mail", "v2"), Is.False);
            Assert.That(state.IsRead("mail", "V2"), Is.False);
            Assert.That(state.Forget(key: "mail", version: "v10"), Is.True);
            Assert.That(state.Forget("mail", "v10"), Is.False);
            Assert.That(state.IsRead("mail", "v2"), Is.True, "Forget removes only the specified confirmation.");
        }

        [Test]
        public void UnreadSourceTracksVersionWithoutCounterDrift()
        {
            using var state = new ReddotReadState("slot-0");
            using var source = state.BindUnreadSource(path: "Mail/Gift", sourceId: "gift", key: "item", version: "v1");
            var values = new List<long>();
            using var firstUi = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            using var secondUi = ReddotManager.Subscribe("Mail", _ => { }, false);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            state.MarkRead("item", "v1");
            state.MarkRead("item", "v1");
            source.SetVersion(version: "v2");
            state.MarkRead("item", "v1");
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1), "Old confirmation cannot clear new content.");
            source.SetVersion("v1");
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero, "Returning to a read version stays read.");
            source.SetVersion("v2");
            state.MarkRead("item", "v2");
            source.SetVersion("v1");
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.That(state.IsRead("item", "v1"), Is.True);
            Assert.That(state.IsRead("item", "v2"), Is.True);
            Assert.That(values, Is.EqualTo(new long[] { 0, 1, 0, 1, 0 }));
        }

        [Test]
        public void ReadVersionDoesNotAcknowledgeNewContent()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("mail", "v1");
            using var source = state.BindUnreadSource("Mail", "mail", "mail", "v1");
            Assert.Throws<ArgumentException>(() => source.SetVersion(null));
            Assert.Throws<ArgumentException>(() => source.SetVersion(""));
            Assert.Throws<ArgumentException>(() => source.SetVersion(" \t "));
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            source.SetVersion("v2");
            source.SetVersion("v2");
            Assert.That(state.IsRead("mail", "v2"), Is.False);
            Assert.That(state.IsRead("mail", "v1"), Is.True);
            state.MarkRead("mail", "v1");
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            source.SetVersion("v3");
            state.MarkRead("mail", "v2");
            Assert.That(state.IsRead("mail", "v2"), Is.True);
            Assert.That(state.IsRead("mail", "v3"), Is.False);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1), "A late first acknowledgement of v2 cannot clear v3.");
            source.SetVersion("v2");
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
        }

        [Test]
        public void NamespaceIsolationAndExplicitImport()
        {
            using var first = new ReddotReadState("account-A/slot-0");
            using var second = new ReddotReadState("account-B/slot-0");
            using var reopened = new ReddotReadState("account-A/slot-0");
            first.MarkRead("mail", "release");
            using var firstSource = first.BindUnreadSource("Mail/A", "mail", "mail", "release");
            using var secondSource = second.BindUnreadSource("Mail/B", "mail", "mail", "release");
            using var reopenedSource = reopened.BindUnreadSource("Mail/Reopened", "mail", "mail", "release");
            Assert.That(second.IsRead("mail", "release"), Is.False);
            Assert.That(reopened.IsRead("mail", "release"), Is.False, "Matching namespace does not share global memory.");
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(2));
            Assert.Throws<System.IO.InvalidDataException>(() => second.Import(first.Export()));
            reopened.Import(snapshot: first.Export());
            Assert.That(reopened.Namespace, Is.EqualTo("account-A/slot-0"));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            Assert.That(second.IsRead("mail", "release"), Is.False);
        }

        [Test]
        public void SnapshotDeepCopiesBothDirectionsAndDoesNotPersistBindings()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("z", "v2");
            state.MarkRead("a", "v2");
            state.MarkRead("a", "v10");
            using var source = state.BindUnreadSource("Mail", "not-persisted", "z", "current");
            var snapshot = state.Export();
            Assert.That(snapshot.schemaVersion, Is.EqualTo(1));
            Assert.That(snapshot.readNamespace, Is.EqualTo("slot-0"));
            Assert.That(snapshot.records, Has.Length.EqualTo(2));
            Assert.That(snapshot.records[0].key, Is.EqualTo("a"));
            Assert.That(snapshot.records[0].versions, Is.EqualTo(new[] { "v10", "v2" }));
            using var imported = new ReddotReadState("slot-0");
            imported.Import(snapshot);
            snapshot.records[0].versions[0] = "changed";
            snapshot.records[1] = new ReddotReadRecord { key = "replacement", versions = new[] { "x" } };
            Assert.That(imported.IsRead("a", "v10"), Is.True);
            Assert.That(imported.IsRead("z", "v2"), Is.True);
            Assert.That(state.IsRead("a", "v10"), Is.True);
            Assert.That(state.IsRead("z", "v2"), Is.True);
            imported.Forget("a", "v2");
            Assert.That(state.IsRead("a", "v2"), Is.True);
            Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/" }));
            Assert.Throws<System.IO.InvalidDataException>(() => new ReddotReadSnapshot().Validate());
        }

        [Test]
        public void SnapshotValidationUsesOrdinalKeysAndVersionsAndAllowsExplicitEmptyImport()
        {
            using var state = new ReddotReadState("slot-0");
            var snapshot = new ReddotReadSnapshot
            {
                schemaVersion = 1,
                readNamespace = "slot-0",
                records = new[]
                {
                    new ReddotReadRecord { key = "mail", versions = new[] { "v1", "V1" } },
                    new ReddotReadRecord { key = "Mail", versions = new[] { "v1" } }
                }
            };
            Assert.DoesNotThrow(snapshot.Validate);
            state.Import(snapshot);
            Assert.That(state.IsRead("mail", "V1"), Is.True);
            Assert.That(state.IsRead("Mail", "v1"), Is.True);
            Assert.That(state.IsRead("Mail", "V1"), Is.False);
            state.Import(new ReddotReadSnapshot
            {
                schemaVersion = 1,
                readNamespace = "slot-0",
                records = Array.Empty<ReddotReadRecord>()
            });
            Assert.That(state.IsRead("mail", "v1"), Is.False);
            Assert.That(state.Export().records, Is.Empty);
        }

        [TestCase("schema-missing")]
        [TestCase("schema-unsupported")]
        [TestCase("namespace-null")]
        [TestCase("namespace-blank")]
        [TestCase("namespace-mismatch")]
        [TestCase("records-null")]
        [TestCase("record-null")]
        [TestCase("key-null")]
        [TestCase("key-blank")]
        [TestCase("versions-null")]
        [TestCase("versions-empty")]
        [TestCase("version-null")]
        [TestCase("version-blank")]
        [TestCase("duplicate-key")]
        [TestCase("duplicate-version")]
        public void InvalidImportIsAtomic(string fault)
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("known", "v1");
            using var source = state.BindUnreadSource("Mail", "mail", "known", "v1");
            var calls = 0;
            using var observer = ReddotManager.Subscribe("Mail", _ => calls++, false);
            var snapshot = Snapshot("slot-0", "new", "v2");
            switch (fault)
            {
                case "schema-missing":
                    snapshot.schemaVersion = 0;
                    break;
                case "schema-unsupported":
                    snapshot.schemaVersion = 2;
                    break;
                case "namespace-null":
                    snapshot.readNamespace = null;
                    break;
                case "namespace-blank":
                    snapshot.readNamespace = " \t ";
                    break;
                case "namespace-mismatch":
                    snapshot.readNamespace = "Slot-0";
                    break;
                case "records-null":
                    snapshot.records = null;
                    break;
                case "record-null":
                    snapshot.records = new ReddotReadRecord[] { null };
                    break;
                case "key-null":
                    snapshot.records[0].key = null;
                    break;
                case "key-blank":
                    snapshot.records[0].key = " ";
                    break;
                case "versions-null":
                    snapshot.records[0].versions = null;
                    break;
                case "versions-empty":
                    snapshot.records[0].versions = Array.Empty<string>();
                    break;
                case "version-null":
                    snapshot.records[0].versions = new string[] { null };
                    break;
                case "version-blank":
                    snapshot.records[0].versions = new[] { " \t " };
                    break;
                case "duplicate-key":
                    snapshot.records = new[] { snapshot.records[0], snapshot.records[0] };
                    break;
                case "duplicate-version":
                    snapshot.records[0].versions = new[] { "v2", "v2" };
                    break;
            }
            Assert.Throws<System.IO.InvalidDataException>(() => state.Import(snapshot));
            Assert.That(state.IsRead("known", "v1"), Is.True);
            Assert.That(state.IsRead("new", "v2"), Is.False);
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void ImportBatchesBindingsAndCommitsRecordsBeforeObservers()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("old", "v1");
            using var first = state.BindUnreadSource("Mail/A", "a", "new", "v2");
            using var second = state.BindUnreadSource("Mail/B", "b", "new", "v2");
            var values = new List<long>();
            using var observer = ReddotManager.Subscribe("Mail", snapshot =>
            {
                Assert.That(state.IsRead("new", "v2"), Is.True);
                Assert.That(state.IsRead("old", "v1"), Is.False);
                Assert.That(ReddotManager.GetValue("Mail/A"), Is.Zero);
                Assert.That(ReddotManager.GetValue("Mail/B"), Is.Zero);
                values.Add(snapshot.Value);
            }, false);
            state.Import(Snapshot("slot-0", "new", "v2"));
            state.Import(Snapshot("slot-0", "new", "v2"));
            Assert.That(values, Is.EqualTo(new long[] { 0 }));
        }

        [Test]
        public void FailedNumericImportRollsBackAllBindingsAndKeepsOldRecords()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("b", "v1");
            state.MarkRead("c", "v1");
            using var large = ReddotManager.RegisterSource("Mail/Large", "large", long.MaxValue - 1);
            using var a = state.BindUnreadSource("Mail/A", "a", "a", "v1");
            using var b = state.BindUnreadSource("Mail/B", "b", "b", "v1");
            using var c = state.BindUnreadSource("Mail/C", "c", "c", "v1");
            var values = new List<long>();
            using var observer = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            Assert.Throws<OverflowException>(() => state.Import(Snapshot("slot-0", "a", "v1")));
            Assert.That(state.IsRead("a", "v1"), Is.False);
            Assert.That(state.IsRead("b", "v1"), Is.True);
            Assert.That(state.IsRead("c", "v1"), Is.True);
            Assert.That(ReddotManager.GetValue("Mail/A"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail/B"), Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail/C"), Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(long.MaxValue));
            Assert.That(values, Is.Empty);
            state.Import(new ReddotReadSnapshot
            {
                schemaVersion = 1,
                readNamespace = "slot-0",
                records = new[]
                {
                    new ReddotReadRecord { key = "a", versions = new[] { "v1" } },
                    new ReddotReadRecord { key = "b", versions = new[] { "v1" } }
                }
            });
            Assert.That(ReddotManager.GetValue("Mail/A"), Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail/C"), Is.EqualTo(1));
            Assert.That(values, Is.Empty, "A valid redistribution must decrease first and not emit an unchanged parent value.");
        }

        [Test]
        public void NumericImportFailureAndRollbackObserverFailureAreBothVisible()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("b", "v1");
            state.MarkRead("c", "v1");
            using var large = ReddotManager.RegisterSource("Mail/Large", "large", long.MaxValue - 1);
            using var a = state.BindUnreadSource("Mail/A", "a", "a", "v1");
            using var b = state.BindUnreadSource("Mail/B", "b", "b", "v1");
            using var c = state.BindUnreadSource("Mail/C", "c", "c", "v1");
            var legacy = ReddotManager.RegisterChanged(() => throw new InvalidOperationException("rollback-marker"));
            try
            {
                var error = Assert.Throws<AggregateException>(() => state.Import(Snapshot("slot-0", "a", "v1")));
                var failures = error.Flatten().InnerExceptions;
                Assert.That(failures, Has.Count.EqualTo(2));
                Assert.That(failures[0], Is.TypeOf<OverflowException>());
                Assert.That(failures[1].Message, Is.EqualTo("rollback-marker"));
                Assert.That(state.IsRead("a", "v1"), Is.False);
                Assert.That(state.IsRead("b", "v1"), Is.True);
                Assert.That(state.IsRead("c", "v1"), Is.True);
                Assert.That(ReddotManager.GetValue("Mail/A"), Is.EqualTo(1));
                Assert.That(ReddotManager.GetValue("Mail/B"), Is.Zero);
                Assert.That(ReddotManager.GetValue("Mail/C"), Is.Zero);
            }
            finally
            {
                legacy.Unregister();
            }
        }

        [Test]
        public void FailedForgetAndVersionChangeDoNotMutateMetadata()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("mail", "v1");
            using var large = ReddotManager.RegisterSource("Mail/Large", "large", long.MaxValue);
            using var source = state.BindUnreadSource("Mail/Item", "mail", "mail", "v1");
            Assert.Throws<OverflowException>(() => state.Forget("mail", "v1"));
            Assert.Throws<OverflowException>(() => source.SetVersion("v2"));
            Assert.That(state.IsRead("mail", "v1"), Is.True);
            Assert.That(state.IsRead("mail", "v2"), Is.False);
            large.Dispose();
            state.MarkRead("mail", "v2");
            state.Forget("mail", "v1");
            Assert.That(ReddotManager.GetValue("Mail/Item"), Is.EqualTo(1), "Rejected SetVersion must leave v1 active.");
        }

        [Test]
        public void InvalidInputsAreRejectedBeforeMutationAndUnboundStateDoesNotTouchManager()
        {
            Assert.Throws<ArgumentException>(() => new ReddotReadState(null));
            Assert.Throws<ArgumentException>(() => new ReddotReadState(" \t "));
            using var state = new ReddotReadState("slot-0");
            var calls = 0;
            var global = ReddotManager.RegisterChanged(() => calls++);
            try
            {
                Assert.Throws<ArgumentException>(() => state.IsRead(" ", "v1"));
                Assert.Throws<ArgumentException>(() => state.MarkRead("mail", null));
                Assert.Throws<ArgumentException>(() => state.Forget(null, "v1"));
                Assert.Throws<ArgumentNullException>(() => state.Import(null));
                Assert.Throws<ArgumentException>(() => state.BindUnreadSource("Mail", "id", "mail", " "));
                Assert.Throws<ArgumentException>(() => state.BindUnreadSource(" / / ", "id", "mail", "v1"));
                Assert.Throws<ArgumentException>(() => state.BindUnreadSource("Mail", " ", "mail", "v1"));
                state.MarkRead("mail", "v1");
                state.Forget("mail", "v1");
                state.Import(Snapshot("slot-0", "mail", "v2"));
                state.PruneRemovedKeys(Array.Empty<string>());
                state.Dispose();
                Assert.That(ReddotManager.ReddotPath, Is.Empty);
                Assert.That(calls, Is.Zero);
            }
            finally
            {
                global.Unregister();
            }
        }

        [Test]
        public void PruneRemovedKeysIsExplicitOrdinalAndValidatesTheWholeInput()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("keep", "v1");
            state.MarkRead("removed", "v1");
            state.MarkRead("removed", "v2");
            using var source = state.BindUnreadSource("Mail", "removed", "removed", "v2");
            source.SetVersion("v1");
            Assert.That(state.Export().records, Has.Length.EqualTo(2), "Version changes never prune records.");
            Assert.Throws<ArgumentNullException>(() => state.PruneRemovedKeys(null));
            Assert.Throws<ArgumentException>(() => state.PruneRemovedKeys(new[] { "keep", " " }));
            Assert.That(state.IsRead("removed", "v2"), Is.True);
            Assert.That(state.PruneRemovedKeys(liveKeys: new[] { "keep", "REMOVED" }), Is.EqualTo(1));
            Assert.That(state.IsRead("removed", "v1"), Is.False);
            Assert.That(state.IsRead("removed", "v2"), Is.False);
            Assert.That(state.IsRead("keep", "v1"), Is.True);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            Assert.That(state.PruneRemovedKeys(new[] { "keep" }), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OldGenerationAndLateStateDisposalCannotStealNewSource(bool newSession)
        {
            using var oldState = new ReddotReadState("old-account");
            var old = oldState.BindUnreadSource("Mail", "same-id", "item", "v1");
            if (newSession)
            {
                ResetSession();
            }
            else
            {
                ReddotManager.Reset();
            }
            using var currentState = new ReddotReadState("new-account");
            using var current = currentState.BindUnreadSource("Mail", "same-id", "item", "v1");
            Assert.Throws<ObjectDisposedException>(() => old.SetVersion("v2"));
            oldState.MarkRead("item", "v1");
            oldState.Import(Snapshot("old-account", "item", "v2"));
            old.Dispose();
            old.Dispose();
            oldState.Dispose();
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            currentState.MarkRead("item", "v1");
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
        }

        [Test]
        public void ReleasedBindingAndOldOwnerNeverReleaseAReplacementInTheSameGeneration()
        {
            using var oldState = new ReddotReadState("old-account");
            var old = oldState.BindUnreadSource("Mail", "same-id", "item", "v1");
            old.Dispose();
            using var currentState = new ReddotReadState("new-account");
            using var current = currentState.BindUnreadSource("Mail", "same-id", "item", "v1");
            oldState.MarkRead("item", "v1");
            old.Dispose();
            oldState.Dispose();
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => old.SetVersion("v1"));
            Assert.Throws<ObjectDisposedException>(() => oldState.MarkRead("item", "v1"));
        }

        [Test]
        public void DuplicateBindingAndFailedObserverRegistrationDoNotLeakOwnership()
        {
            using var state = new ReddotReadState("slot-0");
            using var source = state.BindUnreadSource("Mail", "id", "mail", "v1");
            Assert.Throws<ArgumentException>(() => state.BindUnreadSource("/Mail/", "id", "other", "v2"));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            var throwing = ReddotManager.Subscribe("Other", _ => throw new InvalidOperationException("bind-marker"), false);
            var error = Assert.Throws<AggregateException>(() => state.BindUnreadSource("Other", "id", "other", "v1"));
            Assert.That(error.Flatten().InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(ReddotManager.GetValue("Other"), Is.Zero);
            Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/" }));
            throwing.Dispose();
            using var replacement = state.BindUnreadSource("Other", "id", "other", "v1");
            state.MarkRead("other", "v1");
            Assert.That(ReddotManager.GetValue("Other"), Is.Zero);
        }

        [Test]
        public void ObserverFailureLeavesConsistentReadStateAndVersionMetadata()
        {
            using var state = new ReddotReadState("slot-0");
            using var source = state.BindUnreadSource("Mail", "id", "mail", "v1");
            var throwing = ReddotManager.Subscribe("Mail", _ => throw new InvalidOperationException("read-marker"), false);
            Assert.Throws<InvalidOperationException>(() => state.MarkRead("mail", "v1"));
            Assert.That(state.IsRead("mail", "v1"), Is.True);
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.DoesNotThrow(() => state.MarkRead("mail", "v1"));
            Assert.Throws<InvalidOperationException>(() => source.SetVersion("v2"));
            Assert.That(state.IsRead("mail", "v2"), Is.False, "SetVersion must not acknowledge content.");
            throwing.Dispose();
            state.MarkRead("mail", "v2");
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero, "Observer failure must not leave v1 in binding metadata.");
        }

        [Test]
        public void ReentrantVersionObserverSeesCommittedMetadata()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("mail", "v1");
            using var source = state.BindUnreadSource("Mail", "id", "mail", "v1");
            var values = new List<long>();
            using var observer = ReddotManager.Subscribe("Mail", snapshot =>
            {
                values.Add(snapshot.Value);
                if (snapshot.Value == 1)
                {
                    state.MarkRead("mail", "v2");
                }
            }, false);
            source.SetVersion("v2");
            Assert.That(values, Is.EqualTo(new long[] { 1, 0 }));
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
        }

        [Test]
        public void ImportObserverFailureReportsTheErrorAfterCommittingAllRecords()
        {
            using var state = new ReddotReadState("slot-0");
            using var a = state.BindUnreadSource("Mail/A", "a", "a", "v1");
            using var b = state.BindUnreadSource("Mail/B", "b", "a", "v1");
            using var throwing = ReddotManager.Subscribe("Mail", _ => throw new InvalidOperationException("import-marker"), false);
            Assert.Throws<InvalidOperationException>(() => state.Import(Snapshot("slot-0", "a", "v1")));
            Assert.That(state.IsRead("a", "v1"), Is.True);
            Assert.That(ReddotManager.GetValue("Mail/A"), Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail/B"), Is.Zero);
        }

        [Test]
        public void DisposeReleasesOnlyOwnedBindingsEvenWhenObserversThrow()
        {
            var state = new ReddotReadState("slot-0");
            var a = state.BindUnreadSource("Mail/A", "a", "a", "v1");
            var b = state.BindUnreadSource("Mail/B", "b", "b", "v1");
            using var other = ReddotManager.RegisterSource("Mail/C", "other", 3);
            var throwing = ReddotManager.Subscribe("Mail", _ => throw new InvalidOperationException("dispose-marker"), false);
            Assert.Throws<InvalidOperationException>(() => state.Dispose());
            throwing.Dispose();
            state.Dispose();
            a.Dispose();
            b.Dispose();
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(3));
            Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/C/" }));
            Assert.Throws<ObjectDisposedException>(() => a.SetVersion("v2"));
            Assert.Throws<ObjectDisposedException>(() => state.Export());
        }

        private static ReddotReadSnapshot Snapshot(string readNamespace, string key, string version)
            => new ReddotReadSnapshot
            {
                schemaVersion = 1,
                readNamespace = readNamespace,
                records = new[] { new ReddotReadRecord { key = key, versions = new[] { version } } }
            };

        private static void ResetSession() => typeof(ReddotManager)
            .GetMethod("ResetSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, null);
    }

    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public sealed class ReddotValueTests
    {
        [SetUp]
        public void Prepare() => ResetSession();

        [TearDown]
        public void Clean() => ResetSession();

        [Test]
        public void LegacyPathTruthAndLeasesUnchanged()
        {
            Assert.That(ReddotManager.NormalizePath(" /Mail/ / Gift // "), Is.EqualTo("Mail/Gift/"));
            var calls = 0;
            var changed = ReddotManager.RegisterChanged(() => calls++);
            try
            {
                Assert.That(calls, Is.Zero);
                ReddotManager.AddPath(" /Mail//Gift/ ");
                ReddotManager.AddPath("Mail/Gift");
                Assert.That(calls, Is.EqualTo(1));
                using var first = ReddotManager.RegisterPath("Mail/Gift");
                using var second = ReddotManager.RegisterPath(" /Mail/Gift/ ");
                ReddotManager.RemovePath("Mail/Gift");
                first.Dispose();
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
                Assert.That(ReddotManager.ShouldBeDisplayed("Mai"), Is.False);
                Assert.That(ReddotManager.ShouldBeDisplayed("mail"), Is.False);
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail/Gift/Child"), Is.False);
                Assert.That(ReddotManager.ShouldBeDisplayed(" / / "), Is.False);
                second.Dispose();
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
            }
            finally
            {
                changed.Unregister();
            }
        }

        [Test]
        public void CountSumAnyMaxAndBoundaries()
        {
            Assert.That(typeof(ReddotManager).GetMethod("GetValue", new[] { typeof(string) }), Is.Not.Null,
                "5B must expose cached numeric path values, not only legacy boolean truth.");
            ReddotManager.AddPath("Mail");
            using var lease = ReddotManager.RegisterPath("Mail");
            using var own = ReddotManager.RegisterSource("Mail", "own", initialValue: 3);
            using var gift = ReddotManager.RegisterSource("Mail/Gift", "gift", 7);
            using var nested = ReddotManager.RegisterSource("Mail/Gift/Nested", "nested", 2);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(14));
            ReddotManager.SetAggregation("Mail/Gift", rule: ReddotAggregation.Any);
            Assert.That(ReddotManager.GetValue("Mail/Gift"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(6));
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Max);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(5), "Max compares the entire own sum to each child.");
            own.SetValue(0);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(2));
            ReddotManager.SetAggregation("Mail/Gift", ReddotAggregation.Max);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(7));
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail/Gift"), Is.EqualTo(7), "Parent rules must not change descendants.");
            Assert.That(ReddotManager.GetValue("Mai"), Is.Zero);
            Assert.That(ReddotManager.GetValue("mail"), Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail/Gift/Unknown"), Is.Zero);
            Assert.That(ReddotManager.GetValue(null), Is.Zero);
            Assert.That(ReddotManager.GetSnapshot(" / / ").Path, Is.Empty);
        }

        [Test]
        public void BatchCoalescesAndReentrantListenersAreSafe()
        {
            Assert.That(typeof(ReddotManager).GetMethod("BeginBatch", Type.EmptyTypes), Is.Not.Null,
                "5B must expose an independently disposable nested batch scope.");
            using var first = ReddotManager.RegisterSource("Mail/A", "a");
            using var second = ReddotManager.RegisterSource("Mail/B", "b");
            var parent = new List<long>();
            var child = new List<long>();
            using var parentSubscription = ReddotManager.Subscribe("Mail", snapshot => parent.Add(snapshot.Value), false);
            using var childSubscription = ReddotManager.Subscribe("Mail/A", snapshot => child.Add(snapshot.Value), false);
            var globalCalls = 0;
            var global = ReddotManager.RegisterChanged(() => globalCalls++);
            try
            {
                using (ReddotManager.BeginBatch())
                {
                    first.SetValue(1);
                    using (ReddotManager.BeginBatch())
                    {
                        first.SetValue(3);
                        second.SetValue(4);
                    }
                    Assert.That(parent, Is.Empty);
                    Assert.That(child, Is.Empty);
                    Assert.That(globalCalls, Is.Zero);
                    Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(7), "Reads see completed valid writes in a batch.");
                }
                Assert.That(parent, Is.EqualTo(new long[] { 7 }));
                Assert.That(child, Is.EqualTo(new long[] { 3 }));
                Assert.That(globalCalls, Is.EqualTo(1));
                using (ReddotManager.BeginBatch())
                {
                    first.SetValue(8);
                    first.SetValue(3);
                }
                Assert.That(parent, Is.EqualTo(new long[] { 7 }), "Returning to the original value is not a redraw.");
                Assert.That(child, Is.EqualTo(new long[] { 3 }));
            }
            finally
            {
                global.Unregister();
            }
        }

        [Test]
        public void InvalidAndDuplicateSourcesCannotChangeVisibleState()
        {
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource(" / / ", "id"));
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource("Mail", " \t "));
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource("Mail", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReddotManager.RegisterSource("Mail", "bad", -1));
            Assert.Throws<ArgumentNullException>(() => ReddotManager.Subscribe("Mail", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReddotManager.SetAggregation("Mail", (ReddotAggregation)123));
            Assert.Throws<ArgumentException>(() => ReddotManager.SetAggregation(null, ReddotAggregation.Sum));
            Assert.That(ReddotManager.ReddotPath, Is.Empty);
            using var first = ReddotManager.RegisterSource(" /Mail//Gift/ ", "id", 2);
            Assert.Throws<ArgumentException>(() => ReddotManager.RegisterSource("Mail/Gift", "id", 9));
            using var second = ReddotManager.RegisterSource("Mail/Gift", "ID", 3);
            using var spaced = ReddotManager.RegisterSource("Mail/Gift", " id ", 4);
            Assert.Throws<ArgumentOutOfRangeException>(() => first.SetValue(-1));
            Assert.That(first.Value, Is.EqualTo(2));
            Assert.That(ReddotManager.GetValue("Mail/Gift"), Is.EqualTo(9));
            Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/Gift/" }));
            first.Dispose();
            first.Dispose();
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(7));
            Assert.Throws<ObjectDisposedException>(() => first.SetValue(1));
            using var replacement = ReddotManager.RegisterSource("Mail/Gift", "id", 5);
            first.Dispose();
            ReddotManager.RemovePath("Mail/Gift");
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(12));
        }

        [Test]
        public void OverflowIsRejectedBeforeSourcesRulesCachesOrNotificationsChange()
        {
            using var large = ReddotManager.RegisterSource("Mail/A", "large", long.MaxValue);
            using var small = ReddotManager.RegisterSource("Mail/B", "small");
            var values = new List<long>();
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            Assert.Throws<OverflowException>(() => small.SetValue(1));
            Assert.Throws<OverflowException>(() => ReddotManager.RegisterSource("Mail/C", "overflow", 1));
            Assert.Throws<OverflowException>(() => ReddotManager.RegisterPath("Mail/A"));
            Assert.Throws<OverflowException>(() => ReddotManager.AddPath("Mail/A"));
            Assert.That(small.Value, Is.Zero);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(long.MaxValue));
            Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/A/", "Mail/B/" }));
            Assert.That(values, Is.Empty);
            ReddotManager.SetAggregation("Mail/A", ReddotAggregation.Any);
            small.SetValue(1);
            Assert.Throws<OverflowException>(() => ReddotManager.SetAggregation("Mail/A", ReddotAggregation.Sum));
            Assert.That(ReddotManager.GetValue("Mail/A"), Is.EqualTo(1));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(2));
            Assert.Throws<OverflowException>(() => ReddotManager.RegisterSource("Mail/A", "own-overflow", 1));
            Assert.That(large.Value, Is.EqualTo(long.MaxValue), "Even Any must validate its full own sum.");
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Max);
            ReddotManager.SetAggregation("Mail/A", ReddotAggregation.Sum);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(long.MaxValue));
            Assert.Throws<OverflowException>(() => ReddotManager.SetAggregation("Mail", ReddotAggregation.Sum));
            small.SetValue(2);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(long.MaxValue), "The rejected Sum rule must not stick.");
        }

        [Test]
        public void PathSubscriptionsAreImmutableLocalAndValueSensitive()
        {
            using var source = ReddotManager.RegisterSource("Mail/Gift", "id", 2);
            var snapshots = new List<ReddotSnapshot>();
            var oldCalls = 0;
            var old = ReddotManager.RegisterChanged(() => oldCalls++);
            try
            {
                var subscription = ReddotManager.Subscribe(" /Mail// ", callback: snapshots.Add, emitInitial: true);
                using var empty = ReddotManager.Subscribe(null,
                    snapshot => Assert.That(snapshot.Value, Is.Zero), emitInitial: true);
                Assert.That(oldCalls, Is.Zero, "Subscribing must not replay the legacy global channel.");
                Assert.That(snapshots.Count, Is.EqualTo(1));
                Assert.That(snapshots[0].Path, Is.EqualTo("Mail/"));
                source.SetValue(3);
                source.SetValue(3);
                ReddotManager.AddPath("Mailbox");
                ReddotManager.AddPath("Other");
                Assert.That(snapshots.Count, Is.EqualTo(2));
                Assert.That(snapshots[0].Value, Is.EqualTo(2), "Previously delivered snapshots must not mutate.");
                ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
                source.SetValue(4);
                Assert.That(snapshots.Count, Is.EqualTo(3));
                Assert.That(snapshots[2].Value, Is.EqualTo(1));
                subscription.Dispose();
                subscription.Dispose();
                source.SetValue(0);
                Assert.That(snapshots.Count, Is.EqualTo(3));
                Assert.That(typeof(ReddotSnapshot).GetProperty("Value").CanWrite, Is.False);
                Assert.That(typeof(ReddotSnapshot).GetProperty("Path").CanWrite, Is.False);
                Assert.That(typeof(ReddotSource).GetProperty("Value").CanWrite, Is.False);
            }
            finally
            {
                old.Unregister();
            }
        }

        [Test]
        public void ReentrantSourcesAndSubscriptionsUseStableWaves()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var depth = 0;
            var maximumDepth = 0;
            var calls = 0;
            var removedCalls = 0;
            var addedValues = new List<long>();
            IDisposable removed = null;
            IDisposable added = null;
            using var first = ReddotManager.Subscribe("Mail", snapshot =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);
                calls++;
                if (snapshot.Value == 1)
                {
                    removed.Dispose();
                    added = ReddotManager.Subscribe("Mail", next =>
                    {
                        Assert.That(depth, Is.Zero, "Immediate replay requested in a callback must not nest callbacks.");
                        addedValues.Add(next.Value);
                    });
                }
                if (snapshot.Value < 512)
                {
                    source.SetValue(snapshot.Value + 1);
                }
                depth--;
            }, false);
            removed = ReddotManager.Subscribe("Mail", _ => removedCalls++, false);
            try
            {
                source.SetValue(1);
                Assert.That(calls, Is.EqualTo(512));
                Assert.That(maximumDepth, Is.EqualTo(1));
                Assert.That(removedCalls, Is.Zero);
                Assert.That(addedValues[0], Is.EqualTo(2));
                Assert.That(addedValues[addedValues.Count - 1], Is.EqualTo(512));
                Assert.That(addedValues.Count, Is.EqualTo(511));
            }
            finally
            {
                removed.Dispose();
                added?.Dispose();
            }
        }

        [Test]
        public void ReentrantImmediateSubscriptionsDoNotGrowTheCallStack()
        {
            var subscriptions = new List<IDisposable>();
            var depth = 0;
            var maximumDepth = 0;
            var calls = 0;
            Action<ReddotSnapshot> callback = null;
            callback = _ =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);
                if (++calls < 256)
                {
                    subscriptions.Add(ReddotManager.Subscribe("Mail", callback));
                }
                depth--;
            };
            subscriptions.Add(ReddotManager.Subscribe("Mail", callback));
            Assert.That(calls, Is.EqualTo(256));
            Assert.That(maximumDepth, Is.EqualTo(1));
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        [Test]
        public void RegistrationFailuresRollbackAndAllObserversStillRun()
        {
            var values = new List<long>();
            var first = ReddotManager.Subscribe("Mail", _ => throw new InvalidOperationException("path-marker"), false);
            using var second = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            var error = Assert.Throws<AggregateException>(() => ReddotManager.RegisterSource("Mail", "id", 1));
            Assert.That(error.Flatten().InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(values, Is.EqualTo(new long[] { 1, 0 }));
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.That(ReddotManager.ReddotPath, Is.Empty);
            first.Dispose();
            using var retry = ReddotManager.RegisterSource("Mail", "id", 2);
            Assert.That(retry.Value, Is.EqualTo(2));
        }

        [Test]
        public void ListenerErrorsAggregateWithoutRevertingDeliveredSourcesOrStallingFlush()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var values = new List<long>();
            var legacy = ReddotManager.RegisterChanged(() => throw new InvalidOperationException("legacy-marker"));
            var throwing = ReddotManager.Subscribe("Mail", _ => throw new ArgumentException("path-marker"), false);
            using var healthy = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            try
            {
                var error = Assert.Throws<AggregateException>(() => source.SetValue(2));
                Assert.That(error.Flatten().InnerExceptions, Has.Count.EqualTo(2));
                Assert.That(values, Is.EqualTo(new long[] { 2 }));
                Assert.That(source.Value, Is.EqualTo(2));
            }
            finally
            {
                legacy.Unregister();
                throwing.Dispose();
            }
            source.SetValue(3);
            Assert.That(values, Is.EqualTo(new long[] { 2, 3 }));
        }

        [Test]
        public void FailedInitialNotificationUnsubscribesWithoutLeakingAnUnobtainableToken()
        {
            var calls = 0;
            Assert.Throws<InvalidOperationException>(() => ReddotManager.Subscribe("Mail", _ =>
            {
                calls++;
                throw new InvalidOperationException("initial-marker");
            }));
            ReddotManager.AddPath("Mail");
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void BatchBodyAndObserverExceptionsStillFinishAndRecover()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var values = new List<long>();
            using var healthy = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (ReddotManager.BeginBatch())
                {
                    source.SetValue(4);
                    throw new InvalidOperationException("body-marker");
                }
            });
            Assert.That(values, Is.EqualTo(new long[] { 4 }));
            var throwing = ReddotManager.Subscribe("Mail", _ => throw new ArgumentException("batch-marker"), false);
            var batch = ReddotManager.BeginBatch();
            source.SetValue(5);
            Assert.Throws<ArgumentException>(() => batch.Dispose());
            batch.Dispose();
            throwing.Dispose();
            source.SetValue(6);
            Assert.That(values, Is.EqualTo(new long[] { 4, 5, 6 }));
        }

        [Test]
        public void ResetKeepsRulesAndSubscriptionsButInvalidatesOldSourcesAndBatches()
        {
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
            var values = new List<long>();
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            using var old = ReddotManager.RegisterSource("Mail", "id", 2);
            using var oldLease = ReddotManager.RegisterPath("Mail");
            var oldBatch = ReddotManager.BeginBatch();
            old.SetValue(8);
            ReddotManager.Reset();
            Assert.That(values, Is.EqualTo(new long[] { 1, 0 }));
            Assert.Throws<ObjectDisposedException>(() => old.SetValue(9));
            using var current = ReddotManager.RegisterSource("Mail", "id", 7);
            var currentBatch = ReddotManager.BeginBatch();
            old.Dispose();
            oldLease.Dispose();
            oldBatch.Dispose();
            current.SetValue(0);
            Assert.That(values, Is.EqualTo(new long[] { 1, 0, 1 }));
            currentBatch.Dispose();
            Assert.That(values, Is.EqualTo(new long[] { 1, 0, 1, 0 }));
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
        }

        [Test]
        public void SessionResetClearsBothSubscriptionKindsRulesAndAllOldCredentials()
        {
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Any);
            using var oldSource = ReddotManager.RegisterSource("Mail", "id", 2);
            var calls = 0;
            Action<ReddotSnapshot> callback = _ => calls++;
            var oldSubscription = ReddotManager.Subscribe("Mail", callback, false);
            var legacyCalls = 0;
            Action legacyCallback = () => legacyCalls++;
            var oldLegacy = ReddotManager.RegisterChanged(legacyCallback);
            var oldBatch = ReddotManager.BeginBatch();
            oldSource.SetValue(3);
            ResetSession();
            Assert.That(ReddotManager.ReddotPath, Is.Empty);
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.That(calls, Is.Zero);
            using var currentSubscription = ReddotManager.Subscribe("Mail", callback, false);
            var currentLegacy = ReddotManager.RegisterChanged(legacyCallback);
            try
            {
                using var current = ReddotManager.RegisterSource("Mail", "id", 7);
                oldSubscription.Dispose();
                oldLegacy.Unregister();
                oldBatch.Dispose();
                oldSource.Dispose();
                current.SetValue(8);
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(8), "A new session must return to Sum.");
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(legacyCalls, Is.EqualTo(2));
            }
            finally
            {
                currentLegacy.Unregister();
            }
        }

        [Test]
        public void ResetDuringDispatchDoesNotPublishStaleValuesToLaterListeners()
        {
            var firstValues = new List<long>();
            var laterValues = new List<long>();
            using var first = ReddotManager.Subscribe("Mail", snapshot =>
            {
                firstValues.Add(snapshot.Value);
                if (snapshot.Value != 0)
                {
                    ReddotManager.Reset();
                }
            }, false);
            using var later = ReddotManager.Subscribe("Mail", snapshot => laterValues.Add(snapshot.Value), false);
            ReddotManager.AddPath("Mail");
            Assert.That(firstValues, Is.EqualTo(new long[] { 1, 0 }));
            Assert.That(laterValues, Is.Empty, "A listener still at zero must not receive an obsolete pre-reset value.");
        }

        [Test]
        public void ResetDuringZeroNotificationStillCatchesUpLaterSubscribers()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id", 3);
            var laterValues = new List<long>();
            using var first = ReddotManager.Subscribe("Mail", snapshot =>
            {
                if (snapshot.Value == 0)
                {
                    ReddotManager.Reset();
                }
            }, false);
            using var later = ReddotManager.Subscribe("Mail", snapshot => laterValues.Add(snapshot.Value), false);
            source.SetValue(0);
            Assert.That(laterValues, Is.EqualTo(new long[] { 0 }), "Reset must not drop an already pending zero transition.");
        }

        [Test]
        public void ResetInsideQueuedInitialReplayDoesNotLoseOtherInitialReplays()
        {
            var subscriptions = new List<IDisposable>();
            var calls = 0;
            subscriptions.Add(ReddotManager.Subscribe("Seed", _ =>
            {
                subscriptions.Add(ReddotManager.Subscribe("Mail", _ => ReddotManager.Reset()));
                subscriptions.Add(ReddotManager.Subscribe("Other", _ => calls++));
            }));
            Assert.That(calls, Is.EqualTo(1), "Reset retains subscriptions, including initial replay not yet delivered.");
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        [Test]
        public void SessionResetInsideDispatchTruncatesOldListenersButDeliversTheNewSession()
        {
            var oldCalls = 0;
            var newValues = new List<long>();
            IDisposable newSubscription = null;
            ReddotSource newSource = null;
            var legacy = ReddotManager.RegisterChanged(() => oldCalls++);
            using var first = ReddotManager.Subscribe("Mail", _ =>
            {
                ResetSession();
                newSubscription = ReddotManager.Subscribe("Mail", snapshot => newValues.Add(snapshot.Value));
                newSource = ReddotManager.RegisterSource("Mail", "id", 7);
            }, false);
            using var later = ReddotManager.Subscribe("Mail", _ => oldCalls++, false);
            try
            {
                ReddotManager.AddPath("Mail");
                Assert.That(oldCalls, Is.Zero);
                Assert.That(newValues, Is.EqualTo(new long[] { 7 }));
                Assert.That(newSource.Value, Is.EqualTo(7));
            }
            finally
            {
                legacy.Unregister();
                newSubscription?.Dispose();
                newSource?.Dispose();
            }
        }

        [Test]
        public void BatchInitialReplayAndLateSubscriptionHaveIndependentBaselines()
        {
            using var source = ReddotManager.RegisterSource("Mail", "id");
            var values = new List<long>();
            var silentValues = new List<long>();
            var outer = ReddotManager.BeginBatch();
            var inner = ReddotManager.BeginBatch();
            source.SetValue(2);
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value));
            using var silent = ReddotManager.Subscribe("Mail", snapshot => silentValues.Add(snapshot.Value), false);
            Assert.That(values, Is.EqualTo(new long[] { 2 }), "An explicitly requested initial replay is immediate in a batch.");
            outer.Dispose();
            source.SetValue(3);
            Assert.That(values, Is.EqualTo(new long[] { 2 }));
            Assert.That(silentValues, Is.Empty);
            inner.Dispose();
            inner.Dispose();
            Assert.That(values, Is.EqualTo(new long[] { 2, 3 }));
            Assert.That(silentValues, Is.EqualTo(new long[] { 3 }));
        }

        [Test]
        public void InternalSourceValiditySupportsSameAssemblyCompositionWithoutASecondPublicContract()
        {
            var validity = typeof(ReddotSource).GetProperty("IsCurrent",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(validity, Is.Not.Null);
            using var source = ReddotManager.RegisterSource("Mail", "id");
            Assert.That(validity.GetValue(source), Is.True);
            ReddotManager.Reset();
            Assert.That(validity.GetValue(source), Is.False);
            using var current = ReddotManager.RegisterSource("Mail", "id");
            source.Dispose();
            Assert.That(validity.GetValue(current), Is.True);
            current.Dispose();
            Assert.That(validity.GetValue(current), Is.False);
        }

        [Test]
        public void SessionResetReleasesObserverTargetsEvenWhileAnOldLeaseStillHoldsTheTree()
        {
            using var oldLease = ReddotManager.RegisterPath("Mail");
            var observer = SubscribeCollectibleObserver();
            ResetSession();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(observer.IsAlive, Is.False, "A stale lease must not keep all previous-session observers alive.");
            GC.KeepAlive(oldLease);
        }

        [TestCase(null, false)]
        [TestCase(null, true)]
        [TestCase("", false)]
        [TestCase("", true)]
        [TestCase(" / / \t ", false)]
        [TestCase(" / / \t ", true)]
        public void SessionResetReleasesEmptyPathCallbackTargetsWhileOldTokenIsRetained(string path, bool emitInitial)
        {
            var tokens = new List<IDisposable>();
            var observer = SubscribeCollectibleEmptyObserver(path, emitInitial, tokens);
            try
            {
                Assert.That(tokens, Has.Count.EqualTo(1));
                ResetSession();
                AssertEmptyTargetCollected(observer,
                    "An empty-path token must not retain its terminal callback target across sessions.");
                GC.KeepAlive(tokens);
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void EmptyPathInitialReplayRemainsQueuedAcrossReentrantReset()
        {
            var tokens = new List<IDisposable>();
            var isInsideSeed = false;
            var calls = 0;
            try
            {
                tokens.Add(ReddotManager.Subscribe("Seed", _ =>
                {
                    isInsideSeed = true;
                    tokens.Add(ReddotManager.Subscribe("Trigger", _ => ReddotManager.Reset()));
                    tokens.Add(ReddotManager.Subscribe(" / / ", snapshot =>
                    {
                        Assert.That(isInsideSeed, Is.False, "Queued initial callbacks must not execute recursively.");
                        Assert.That(snapshot.Path, Is.Empty);
                        Assert.That(snapshot.Value, Is.Zero);
                        calls++;
                    }));
                    isInsideSeed = false;
                }));
                Assert.That(calls, Is.EqualTo(1), "Reset must reschedule the undelivered empty-path initial callback.");
                ReddotManager.AddPath("Mail");
                Assert.That(calls, Is.EqualTo(1), "An empty path must not receive later path changes.");
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void EmptyPathThrowingQueuedReplayReleasesTargetWithoutHidingObserverError()
        {
            WeakReference observer = null;
            var tokens = new List<IDisposable>();
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => ReddotManager.Subscribe("Seed", _ =>
                {
                    observer = SubscribeCollectibleEmptyObserver(null, true, tokens, throwOnInitial: true);
                }));
                Assert.That(error.Message, Is.EqualTo("empty-initial-marker"));
                Assert.That(observer, Is.Not.Null);
                Assert.That(tokens, Has.Count.EqualTo(1));
                AssertEmptyTargetCollected(observer,
                    "A throwing queued one-shot must release its callback even while its returned token is retained.");
                GC.KeepAlive(tokens);
                ReddotManager.AddPath("Mail");
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1), "Observer errors must not stall later flushes.");
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void SessionResetCancelsQueuedEmptyInitialReplayWithoutRetainingItsTarget()
        {
            WeakReference observer = null;
            var tokens = new List<IDisposable>();
            try
            {
                using var seed = ReddotManager.Subscribe("Seed", _ =>
                {
                    tokens.Add(ReddotManager.Subscribe("Trigger", _ => ResetSession()));
                    observer = SubscribeCollectibleEmptyObserver(null, true, tokens, throwOnInitial: true);
                });
                Assert.That(observer, Is.Not.Null);
                Assert.That(tokens, Has.Count.EqualTo(2));
                AssertEmptyTargetCollected(observer,
                    "Cancelling an in-flight empty-path initial replay must release its callback target.");
                GC.KeepAlive(tokens);
            }
            finally
            {
                foreach (var token in tokens)
                {
                    token.Dispose();
                    token.Dispose();
                }
            }
        }

        [Test]
        public void InvalidWriteInsideBatchDoesNotUndoOtherCompletedValidWrites()
        {
            using var large = ReddotManager.RegisterSource("Mail/A", "a", long.MaxValue - 1);
            using var small = ReddotManager.RegisterSource("Mail/B", "b");
            var values = new List<long>();
            using var subscription = ReddotManager.Subscribe("Mail", snapshot => values.Add(snapshot.Value), false);
            using (ReddotManager.BeginBatch())
            {
                small.SetValue(1);
                Assert.Throws<OverflowException>(() => large.SetValue(long.MaxValue));
                Assert.That(large.Value, Is.EqualTo(long.MaxValue - 1));
                Assert.That(small.Value, Is.EqualTo(1));
                Assert.That(values, Is.Empty);
            }
            Assert.That(values, Is.EqualTo(new[] { long.MaxValue }));
        }

        [Test]
        public void LegacyListAddsAndZeroValuedSourceKeepTheirDistinctOwnership()
        {
            var calls = 0;
            var global = ReddotManager.RegisterChanged(() => calls++);
            try
            {
                using var zero = ReddotManager.RegisterSource("Mail/Gift", "zero");
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
                ReddotManager.AddPath(new[] { "Mail/Gift", " /Mail/Gift/ ", " / / ", "Mail/Other" });
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(2));
                Assert.That(calls, Is.EqualTo(2), "One zero-source registration and one successful list Add.");
                ReddotManager.AddPath((IList<string>)null);
                ReddotManager.AddPath(new[] { "Mail/Other", " / / " });
                Assert.That(calls, Is.EqualTo(2));
                ReddotManager.RemovePath("Mail/Gift");
                Assert.That(ReddotManager.ReddotPath, Is.EqualTo(new[] { "Mail/Gift/", "Mail/Other/" }));
                zero.SetValue(5);
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(6));
            }
            finally
            {
                global.Unregister();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference SubscribeCollectibleObserver()
        {
            var target = new object();
            ReddotManager.Subscribe("Mail", _ => GC.KeepAlive(target), false);
            return new WeakReference(target);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference SubscribeCollectibleEmptyObserver(string path, bool emitInitial,
            List<IDisposable> tokens, bool throwOnInitial = false)
        {
            var target = new EmptyPathObserver(throwOnInitial);
            tokens.Add(ReddotManager.Subscribe(path, target.OnSnapshot, emitInitial));
            return new WeakReference(target);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AssertEmptyTargetCollected(WeakReference observer, string message)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(observer.IsAlive, Is.False, message);
        }

        private sealed class EmptyPathObserver
        {
            private readonly bool _throwOnInitial;

            internal EmptyPathObserver(bool throwOnInitial) => _throwOnInitial = throwOnInitial;

            internal void OnSnapshot(ReddotSnapshot snapshot)
            {
                if (_throwOnInitial)
                {
                    throw new InvalidOperationException("empty-initial-marker");
                }
            }
        }

        private static void ResetSession() => typeof(ReddotManager)
            .GetMethod("ResetSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, null);

        [Test]
        public void LegacyReentrantNotificationsUseIterativeWaves()
        {
            var depth = 0;
            var maximumDepth = 0;
            var calls = 0;
            var changed = ReddotManager.RegisterChanged(() =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);
                if (++calls < 8)
                {
                    ReddotManager.AddPath("Mail/Reentrant" + calls);
                }
                depth--;
            });
            try
            {
                ReddotManager.AddPath("Mail/Start");
                Assert.That(calls, Is.EqualTo(8));
                Assert.That(maximumDepth, Is.EqualTo(1), "Reentrant updates must run as later notification waves.");
            }
            finally
            {
                changed.Unregister();
            }
        }
    }
}

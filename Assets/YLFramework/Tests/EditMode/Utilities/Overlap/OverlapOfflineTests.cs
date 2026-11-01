using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace YLFramework.Tests.Extensions.Behavior
{
    [Category("YL.Module.Behavior")]
    public class OverlapOfflineTests
    {
        private static string Root => Environment.GetEnvironmentVariable("YL_BEHAVIOR_ROOT") ?? Directory.GetCurrentDirectory();
        private static string Runtime => Path.Combine(Root, "Assets/YLFramework/Runtime/Utilities/Overlap");

        private static object NewCycle()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Overlap.OverlapQueryCycle")).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, "The managed reentry/lifecycle guard is missing.");
            return Activator.CreateInstance(type, true);
        }

        private static object Call(object cycle, string method, params object[] arguments) =>
            cycle.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(cycle, arguments);

        [Test]
        public void SameCycleRejectsRecursiveQueriesButAnotherInstanceCanQuery()
        {
            var first = NewCycle();
            var second = NewCycle();
            Assert.That(Call(first, "TryBegin"), Is.EqualTo(true));
            Assert.That(Call(first, "TryBegin"), Is.EqualTo(false));
            Assert.That(Call(second, "TryBegin"), Is.EqualTo(true));
            Call(first, "End");
            Assert.That(Call(first, "TryBegin"), Is.EqualTo(true));
        }

        [Test]
        public void DisableInvalidatesActiveSnapshotUntilNextQuery()
        {
            var cycle = NewCycle();
            Call(cycle, "TryBegin");
            Call(cycle, "Invalidate");
            Assert.That(cycle.GetType().GetProperty("IsInvalidated").GetValue(cycle), Is.EqualTo(true));
            Assert.That(Call(cycle, "TryBegin"), Is.EqualTo(false));
            Call(cycle, "End");
            Assert.That(Call(cycle, "TryBegin"), Is.EqualTo(true));
            Assert.That(cycle.GetType().GetProperty("IsInvalidated").GetValue(cycle), Is.EqualTo(false));
        }

        [Test]
        public void PossibleTruncationOnlyReportsCapacityAndOnlyOncePerEnabledSession()
        {
            var cycle = NewCycle();
            Assert.That(Call(cycle, "ReportCapacity", 99, 100), Is.EqualTo(false));
            Assert.That(Call(cycle, "ReportCapacity", 100, 100), Is.EqualTo(true));
            Assert.That(Call(cycle, "ReportCapacity", 100, 100), Is.EqualTo(false));
            Call(cycle, "Invalidate");
            Assert.That(Call(cycle, "ReportCapacity", 100, 100), Is.EqualTo(true));
        }

        [Test]
        public void AllOriginalQueryShapesHaveYLImplementations()
        {
            foreach (var name in new[] { "Point2D", "Circle2D", "Capsule2D", "Box2D", "Area2D", "Box3D", "Sphere3D" })
            {
                Assert.That(File.Exists(Path.Combine(Runtime, "Overlap" + name + ".cs")), Is.True, name);
            }
        }

        [Test]
        public void RuntimeHasNoEditorOrOriginalFrameworkReferences()
        {
            Assert.That(Directory.Exists(Runtime), Is.True, "Overlap has not been integrated.");
            foreach (var file in Directory.GetFiles(Runtime, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(file);
                Assert.That(source, Does.Not.Contain("using UnityEditor"), file);
                Assert.That(source, Does.Contain("namespace YLFramework.Overlap"), file);
                foreach (System.Text.RegularExpressions.Match import in System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^using\s+([^;]+);"))
                {
                    CollectionAssert.Contains(new[] { "System", "System.Collections", "System.Collections.Generic",
                        "UnityEngine", "UnityEngine.Events", "YLFramework.Extensions" }, import.Groups[1].Value, file);
                }
                Assert.That(source, Does.Not.Contain("Handles."), file);
            }
        }

        [Test]
        public void BuffersAreInstanceOwnedAnd3DResetUses3DMatrix()
        {
            Assert.That(File.Exists(Path.Combine(Runtime, "OverlapBase.cs")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(Runtime, "OverlapBase.cs")), Does.Not.Contain("static T[]"));
            Assert.That(File.ReadAllText(Path.Combine(Runtime, "OverlapBase3D.cs")), Does.Contain("PhysicsSetting(gameObject.layer)"));
        }

        [Test]
        public void EveryShapeGuardsCallbackReentryAndFinalizesEvenOnException()
        {
            Assert.That(Directory.Exists(Runtime), Is.True);
            foreach (var file in Directory.GetFiles(Runtime, "*.cs").Where(p => File.ReadAllText(p).Contains("override void CheckOverlap()")))
            {
                var source = File.ReadAllText(file);
                Assert.That(source, Does.Contain("TryBeginCheck()"), file);
                Assert.That(source, Does.Contain("finally"), file);
                Assert.That(source, Does.Contain("EndCheck()"), file);
            }
        }
    }
}

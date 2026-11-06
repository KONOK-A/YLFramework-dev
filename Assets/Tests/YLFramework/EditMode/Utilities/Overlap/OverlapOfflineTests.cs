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
        private static string Runtime
        {
            get
            {
#if UNITY_EDITOR
                return global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework.overlap/Runtime/Utilities/Overlap");
#else
                return Path.Combine(Root, "PackageSources/com.kano.ylframework.overlap/Runtime/Utilities/Overlap");
#endif
            }
        }

        private static object NewCycle()
        {
#if !UNITY_EDITOR
            // A reflection-only slice has no IL type reference to load the compiled production assembly implicitly.
            Assembly.Load("YLFramework.Runtime");
#endif
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Overlap.OverlapQueryCycle")).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, "The managed reentry/lifecycle guard is missing.");
            return Activator.CreateInstance(type, true);
        }

        private static object Call(object cycle, string method, params object[] arguments) =>
            cycle.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(cycle, arguments);

#if UNITY_EDITOR
        [Test]
        public void Legacy3DAndSerializedComponentsUnchanged_HaveOneIndependentRuntimeOwner()
        {
            Assert.That(typeof(global::YLFramework.Overlap.OverlapCircle2D).Assembly.GetName().Name, Is.EqualTo("YLFramework.Overlap"));
            Assert.That(typeof(global::YLFramework.Overlap.OverlapBox3D).Assembly, Is.SameAs(typeof(global::YLFramework.Overlap.OverlapSphere3D).Assembly));
            Assert.That(typeof(global::YLFramework.Overlap.OverlapBox3D).Assembly.GetName().Name, Is.EqualTo("YLFramework.Overlap"));
        }

        [Test]
        public void OriginalComponentAndInspectorGuidsHaveIndependentRuntimeAndEditorOwners()
        {
            const string Root = "Packages/com.kano.ylframework.overlap/";
            Assert.That(typeof(global::YLFramework.Editor.Overlap.OverlapArea2DInspector).Assembly.GetName().Name,
                Is.EqualTo("YLFramework.Overlap.Editor"));
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(Root + "Runtime/Utilities/Overlap/OverlapCircle2D.cs"), Is.EqualTo("c08500f745b47234a8b6ef0ff49d0dce"));
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(Root + "Editor/Utilities/Overlap/OverlapArea2DInspector.cs"), Is.EqualTo("eb43a55cda803a04f983f8861a47ee99"));
            string definition = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(Root + "Editor/Utilities/Overlap/OverlapArea2DInspector.cs");
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(definition)), Does.Contain("YLFramework.Overlap.Editor"));
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID("Packages/com.kano.ylframework/Runtime/Utilities/Overlap/OverlapCircle2D.cs"), Is.Empty);
        }
#endif

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
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Runtime, "Overlap" + name + ".cs"))), Is.True, name);
            }
        }

        [Test]
        public void RuntimeHasNoEditorOrOriginalFrameworkReferences()
        {
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Runtime)), Is.True, "Overlap has not been integrated.");
            foreach (var file in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Runtime), "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(file));
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
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Runtime, "OverlapBase.cs"))), Is.True);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Runtime, "OverlapBase.cs"))), Does.Not.Contain("static T[]"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Runtime, "OverlapBase3D.cs"))), Does.Contain("PhysicsSetting(gameObject.layer)"));
        }

        [Test]
        public void EveryShapeGuardsCallbackReentryAndFinalizesEvenOnException()
        {
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Runtime)), Is.True);
            foreach (var file in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Runtime), "*.cs").Where(p => File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(p)).Contains("override void CheckOverlap()")))
            {
                var source = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(file));
                Assert.That(source, Does.Contain("TryBeginCheck()"), file);
                Assert.That(source, Does.Contain("finally"), file);
                Assert.That(source, Does.Contain("EndCheck()"), file);
            }
        }
    }
}

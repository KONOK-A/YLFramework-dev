using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace YLFramework.Tests.Extensions.Behavior
{
    [Category("YL.Module.Behavior")]
    public class CharacterMotorOfflineTests
    {
#if UNITY_EDITOR
        private static string Runtime => global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework.movement2d/Runtime/Utilities/CharacterController2D");
#else
        private static string Runtime => Path.Combine(Environment.GetEnvironmentVariable("YL_BEHAVIOR_ROOT") ?? Directory.GetCurrentDirectory(),
            "PackageSources/com.kano.ylframework.movement2d/Runtime/Utilities/CharacterController2D");
#endif

#if UNITY_EDITOR
        [TestCase(typeof(YLFramework.Character2D.CharacterController2D), "YLFramework.Character2D", "YLFramework.Platform2D")]
        [TestCase(typeof(YLFramework.Platform2D.Moveable), "YLFramework.Platform2D", "YLFramework.Character2D")]
        public void MovementRuntimesHaveDistinctOwnersWithoutMutualReferences(Type type, string owner, string unrelatedRuntime)
        {
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo(owner));
            Assert.That(type.Assembly.GetReferencedAssemblies().Any(reference => reference.Name == unrelatedRuntime), Is.False);
        }

        [TestCase("YLFramework.Editor.Character2D.CharacterController2DInspector", "YLFramework." + "CharacterController2D.CharacterController2DInspector")]
        [TestCase("YLFramework.Editor.Platform2D.MoveableInspector", "YLFramework." + "MoveablePlatform.MoveableInspector")]
        public void SharedMovementEditorOwnsItsPublicTypesWithoutOldNamespaceAliases(string name, string legacyName)
        {
            var editor = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "YLFramework.Movement2D.Editor");
            Assert.That(editor.GetType(name), Is.Not.Null);
            Assert.That(editor.GetType(legacyName), Is.Null);
        }
#endif

        [Test]
        public void OriginalMotorChecksAndSerializableEventsArePresentWithoutEditorDependencies()
        {
            foreach (var name in new[] { "CharacterController2D", "CheckGround", "CheckWall", "CheckTop" })
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Runtime, name + ".cs"))), Is.True, name);
            }

            foreach (var source in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Runtime), "*.cs", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != "AssemblyInfo.cs").Select(File.ReadAllText))
            {
                Assert.That(source.Contains("using UnityEditor"), Is.False);
                Assert.That(source, Does.Contain("namespace YLFramework.Character2D"));
                foreach (System.Text.RegularExpressions.Match import in System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^using\s+([^;]+);"))
                {
                    CollectionAssert.Contains(new[] { "System", "System.Collections", "System.Collections.Generic",
                        "System.Dynamic", "System.Reflection", "UnityEngine", "UnityEngine.Events", "YLFramework.Extensions" }, import.Groups[1].Value);
                }
                Assert.That(source.Contains("static RaycastHit2D[]"), Is.False);
                Assert.That(source.Contains("static ContactPoint2D[]"), Is.False);
                Assert.That(source.Contains("Handles."), Is.False);
            }
            foreach (var name in new[] { "BoolUnityEvent", "IntUnityEvent", "Vector2UnityEvent" })
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Runtime, "UnityEvent", name + ".cs"))), Is.True, name);
            }
        }

        private static object MathCall(string name, params object[] args)
        {
            var type = typeof(YLFramework.Character2D.CharacterController2D).Assembly.GetType("YLFramework.Character2D.CharacterMotorMath");
            Assert.That(type, Is.Not.Null, "Managed motor calculations are missing.");
            var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, name + " is missing.");
            return method.Invoke(null, args);
        }

        [TestCase(0f, -9.81f, 1f, 0.02f, 20f, -0.1962f)]
        [TestCase(-19.9f, -9.81f, 1f, 0.02f, 20f, -20f)]
        [TestCase(8f, -9.81f, 2f, 0f, 20f, 8f)]
        [TestCase(0f, -9.81f, 0f, 0.1f, 20f, 0f)]
        public void GravityUsesFixedDeltaAndPreservesTerminalVelocity(float speed, float gravity, float scale, float dt, float limit, float expected)
        {
            Assert.That((float)MathCall("IntegrateGravity", speed, gravity, scale, dt, limit), Is.EqualTo(expected).Within(0.00001f));
        }

        [TestCase(1f, -1f, true)]
        [TestCase(-1f, 1f, true)]
        [TestCase(1f, 1f, false)]
        [TestCase(0f, -1f, false)]
        public void WallOnlyBlocksMovementIntoItsNormal(float movement, float normal, bool expected)
        {
            Assert.That(MathCall("BlocksHorizontal", movement, normal), Is.EqualTo(expected));
        }

        [TestCase(1f, 2f, 1f, 1f, 1f, 2f)]
        [TestCase(1f, 2f, -2f, 3f, 2f, 6f)]
        [TestCase(2f, 1f, -1f, -1f, 2f, 1f)]
        public void CastDimensionsUseLocalSizeAndAbsoluteScaleWithoutAabbRotation(float x, float y, float sx, float sy, float expectedX, float expectedY)
        {
            Assert.That(MathCall("ScaledColliderSize", new Vector2(x, y), new Vector2(sx, sy)), Is.EqualTo(new Vector2(expectedX, expectedY)));
        }
    }
}

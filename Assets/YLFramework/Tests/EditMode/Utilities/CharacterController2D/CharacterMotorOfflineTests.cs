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
        private static string Root => Environment.GetEnvironmentVariable("YL_BEHAVIOR_ROOT") ?? Directory.GetCurrentDirectory();
        private static string Runtime => Path.Combine(Root, "Assets/YLFramework/Runtime/Utilities/CharacterController2D");

        [Test]
        public void OriginalMotorChecksAndSerializableEventsArePresentWithoutEditorDependencies()
        {
            foreach (var name in new[] { "CharacterController2D", "CheckGround", "CheckWall", "CheckTop" })
            {
                Assert.That(File.Exists(Path.Combine(Runtime, name + ".cs")), Is.True, name);
            }

            foreach (var source in Directory.GetFiles(Runtime, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText))
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
                Assert.That(File.Exists(Path.Combine(Runtime, "UnityEvent", name + ".cs")), Is.True, name);
            }
        }

        private static object MathCall(string name, params object[] args)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Character2D.CharacterMotorMath")).FirstOrDefault(t => t != null);
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

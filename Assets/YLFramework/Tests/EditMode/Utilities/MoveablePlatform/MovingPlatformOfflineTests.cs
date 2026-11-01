using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace YLFramework.Tests.Extensions.Behavior
{
    [Category("YL.Module.Behavior")]
    public class MovingPlatformOfflineTests
    {
        private static Type PathType
        {
            get
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Platform2D.MoveablePath")).FirstOrDefault(t => t != null);
                Assert.That(type, Is.Not.Null, "The managed path implementation is missing.");
                return type;
            }
        }

        private static object Build(Vector3[] points, bool expected = true)
        {
            var path = Activator.CreateInstance(PathType, true);
            var args = new object[] { points, null };
            var success = (bool)PathType.GetMethod("TryBuild").Invoke(path, args);
            Assert.That(success, Is.EqualTo(expected), args[1] as string);
            return path;
        }

        private static Vector3 Evaluate(object path, float progress) => (Vector3)PathType.GetMethod("Evaluate").Invoke(path, new object[] { progress });

        [Test]
        public void EmptyPathIsRejectedAndSinglePointIsStable()
        {
            Build(Array.Empty<Vector3>(), false);
            var path = Build(new[] { new Vector3(4, 5, 6) });
            Assert.That(Evaluate(path, -1), Is.EqualTo(new Vector3(4, 5, 6)));
            Assert.That(Evaluate(path, 2), Is.EqualTo(new Vector3(4, 5, 6)));
        }

        [Test]
        public void DuplicateAndAllCoincidentPointsNeverDivideByZero()
        {
            var path = Build(new[] { Vector3.zero, Vector3.zero, Vector3.right * 2 });
            Assert.That(Evaluate(path, 0), Is.EqualTo(Vector3.zero));
            Assert.That(Evaluate(path, 0.5f), Is.EqualTo(Vector3.right));
            Assert.That(Evaluate(path, 1), Is.EqualTo(Vector3.right * 2));
            path = Build(new[] { Vector3.one, Vector3.one, Vector3.one });
            Assert.That(Evaluate(path, 0.5f), Is.EqualTo(Vector3.one));
        }

        [Test]
        public void DistanceWeightedSegmentsPreserveConstantSpeed()
        {
            var path = Build(new[] { Vector3.zero, Vector3.right, Vector3.right * 4 });
            Assert.That(PathType.GetProperty("Distance").GetValue(path), Is.EqualTo(4f));
            Assert.That(Evaluate(path, 0.5f), Is.EqualTo(Vector3.right * 2));
        }

        [Test]
        public void InvalidCoordinatesReportNodeIndexAndLeaveNoUsableSnapshot()
        {
            var path = Activator.CreateInstance(PathType, true);
            var args = new object[] { new[] { Vector3.zero, new Vector3(float.NaN, 0) }, null };
            Assert.That(PathType.GetMethod("TryBuild").Invoke(path, args), Is.EqualTo(false));
            Assert.That((string)args[1], Does.Contain("1"));
            Assert.That(PathType.GetProperty("Count").GetValue(path), Is.EqualTo(0));
        }

        [TestCase(0, 1.25f, 0.75f)]
        [TestCase(1, 1.25f, 0.25f)]
        [TestCase(2, 1.25f, 1f)]
        [TestCase(0, -1f, 0f)]
        public void PingPongLoopOnceKeepOriginalTiming(int mode, float elapsed, float expected)
        {
            var method = PathType.GetMethod("Progress");
            var moveType = Enum.ToObject(method.GetParameters()[2].ParameterType, mode);
            Assert.That(method.Invoke(null, new[] { (object)elapsed, 1f, moveType }), Is.EqualTo(expected).Within(0.00001f));
        }

        [Test]
        public void AllOriginalPlatformComponentsAreIndependentOfCC2DAndEditor()
        {
            var root = Environment.GetEnvironmentVariable("YL_BEHAVIOR_ROOT") ?? Directory.GetCurrentDirectory();
            var directory = Path.Combine(root, "Assets/YLFramework/Runtime/Utilities/MoveablePlatform");
            foreach (var name in new[] { "Moveable", "MoveablePlatform2D", "PlatformCatcher2D", "BoolUnityEvent", "Vector3UnityEvent" })
            {
                Assert.That(File.Exists(Path.Combine(directory, name + ".cs")), Is.True, name);
            }

            foreach (var source in Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText))
            {
                Assert.That(source.Contains("using UnityEditor") || source.Contains("CharacterController2D"), Is.False);
                Assert.That(source, Does.Contain("namespace YLFramework.Platform2D"));
                foreach (System.Text.RegularExpressions.Match import in System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^using\s+([^;]+);"))
                {
                    CollectionAssert.Contains(new[] { "System", "System.Collections", "System.Collections.Generic",
                        "System.Reflection", "UnityEngine", "UnityEngine.Events", "static UnityEngine.GraphicsBuffer", "YLFramework.Extensions" }, import.Groups[1].Value);
                }
                Assert.That(source.Contains("static RaycastHit2D[]") || source.Contains("static ContactPoint2D[]"), Is.False);
                Assert.That(source.Contains("Handles.") || source.Contains(".IsDestroy()"), Is.False);
            }
        }
    }
}

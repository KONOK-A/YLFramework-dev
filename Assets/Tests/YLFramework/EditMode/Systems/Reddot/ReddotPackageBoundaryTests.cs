#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.RedDots;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public sealed class ReddotPackageBoundaryTests
    {
        [Test]
        public void OriginalTypesHaveOnePackageOwner()
        {
            Assert.That(typeof(ReddotManager).FullName, Is.EqualTo("YLFramework.RedDots.ReddotManager"));
            Assert.That(typeof(Reddot).FullName, Is.EqualTo("YLFramework.RedDots.Reddot"));
            Assert.That(typeof(ReddotManager).Assembly.GetName().Name, Is.EqualTo("YLFramework.RedDots"));
            Assert.That(typeof(Reddot).Assembly, Is.SameAs(typeof(ReddotManager).Assembly));
        }

        [Test]
        public void OriginalComponentAndManagerGuidsMoveWithoutRegeneratingIdentity()
        {
            const string Root = "Packages/com.kano.ylframework.reddot/Runtime/Systems/Reddot/";
            Assert.That(AssetDatabase.AssetPathToGUID(Root + "Reddot.cs"), Is.EqualTo("3ff4191f5232945449bde5c92a1b497d"));
            Assert.That(AssetDatabase.AssetPathToGUID(Root + "ReddotManager.cs"), Is.EqualTo("13f96a08d4b3e054986887b7cb3422b4"));
            Assert.That(AssetDatabase.AssetPathToGUID("Packages/com.kano.ylframework/Runtime/Systems/Reddot/Reddot.cs"), Is.Empty);
        }

        [Test]
        public void DiagnosticWindowSamplesOnlyOnRequestOrOptInAndDetachesOnDisableDestroy()
        {
            var type = Type.GetType("YLFramework.RedDots.Editor.ReddotDebugWindow, YLFramework.RedDots.Editor", true);
            var window = (EditorWindow)ScriptableObject.CreateInstance(type);
            try
            {
                Assert.That(EditorWindow.focusedWindow, Is.Not.SameAs(window));
                Assert.That(EditorApplication.update?.GetInvocationList().Count(callback => ReferenceEquals(callback.Target, window)) ?? 0, Is.EqualTo(1));
                var before = ReddotManager.CaptureDiagnostics();
                type.GetMethod("OnEditorUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                var after = ReddotManager.CaptureDiagnostics();
                Assert.That(after.CaptureSequence, Is.EqualTo(before.CaptureSequence + 1), "A hidden, opt-out window must not scan the tree.");
                type.GetMethod("Sample", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.That(ReddotManager.CaptureDiagnostics().CaptureSequence, Is.EqualTo(after.CaptureSequence + 2));
                using (var serialized = new SerializedObject(window))
                {
                    serialized.FindProperty("autoSample").boolValue = true;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                var nextSample = type.GetField("_nextSample", BindingFlags.Instance | BindingFlags.NonPublic);
                nextSample.SetValue(window, EditorApplication.timeSinceStartup + 60);
                var beforeThrottle = ReddotManager.CaptureDiagnostics();
                type.GetMethod("OnEditorUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                var throttled = ReddotManager.CaptureDiagnostics();
                Assert.That(throttled.CaptureSequence, Is.EqualTo(beforeThrottle.CaptureSequence + 1));
                nextSample.SetValue(window, 0d);
                type.GetMethod("OnEditorUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.That(ReddotManager.CaptureDiagnostics().CaptureSequence, Is.EqualTo(throttled.CaptureSequence + 2));
                type.GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.That(EditorApplication.update?.GetInvocationList().Any(callback => ReferenceEquals(callback.Target, window)) ?? false, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
            Assert.That(EditorApplication.update?.GetInvocationList().Any(callback => ReferenceEquals(callback.Target, window)) ?? false, Is.False);
        }
    }
}
#endif

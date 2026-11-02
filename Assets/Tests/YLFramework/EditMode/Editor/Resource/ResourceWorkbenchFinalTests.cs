using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Tests.ResourceBuild;

namespace YLFramework.Tests.ResourceEditor
{
    public sealed class ResourceWorkbenchFinalTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [UnityTest]
        public IEnumerator EmptySearchHintDoesNotBecomeFilterTextAndTypingStillUsesTheNativeField()
        {
            Assert.That(typeof(ResourceWorkbenchGui).GetMethod("SearchField", Flags), Is.Not.Null, "An empty-only hint must be painted separately from the actual filter value.");
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceSearchFinalProbe>();
            try
            {
                window.ShowUtility();
                window.position = new Rect(80, 60, 620, 160);
                for (var i = 0; i < 4; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Value, Is.Empty);
                ResourceWorkbenchUX2Tests.Click(window, new Vector2(100, 14));
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.X, character = 'x' });
                yield return null;
                Assert.That(window.Value, Is.EqualTo("x"));
                window.SendEvent(Event.KeyboardEvent("^a"));
                window.SendEvent(Event.KeyboardEvent("backspace"));
                yield return null;
                Assert.That(window.Value, Is.Empty, "Clearing must return an empty filter, never the placeholder.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                window.Close();
                if (focus)
                {
                    focus.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator DeploymentTechnicalSectionsUseTheSamePlanAndKeepCompleteCopyAndCancel()
        {
            var detailsField = typeof(ResourceConfirmationSummary).GetField("Details", Flags);
            Assert.That(detailsField, Is.Not.Null, "Confirmation needs structured identity, paths/files, and execution/recovery instead of parsing Message.");
            using var source = new ResourceBuiltInDeploymentTests.BuiltInSource();
            var plan = ResourceBuiltInDeployer.Preflight(source.Snapshot);
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.displayName = "Read-only confirmation";
            var focus = EditorWindow.focusedWindow;
            var clipboard = EditorGUIUtility.systemCopyBuffer;
            var window = ScriptableObject.CreateInstance<ResourceOperationConfirmation>();
            try
            {
                var before = ResourceReleaseFixture.Snapshot(source.Root);
                var summary = ResourceBuiltInScope.Summarize(project, plan);
                var details = detailsField.GetValue(summary);
                Assert.That(details, Is.Not.Null);
                string Part(string name)
                {
                    var field = details.GetType().GetField(name, Flags);
                    Assert.That(field.IsInitOnly, Is.True, "The captured technical display must not become mutable execution state.");
                    return (string)field.GetValue(details);
                }
                Assert.That(Part("Identity"), Does.Contain(source.ProjectId).And.Contain(source.ProjectGuid).And.Contain(plan.Source.Document.CatalogDigest));
                Assert.That(Part("Paths"), Does.Contain(plan.Source.DirectoryPath).And.Contain(plan.DirectoryPath).And.Contain(plan.WorkspacePath));
                foreach (var token in new[] { "payload~", "ylr-owner.json~", "ylr-deployment.json~", "deploy.lock", "journal.json.next", ".meta" })
                {
                    Assert.That(Part("Paths"), Does.Contain(token));
                }

                Assert.That(Part("Execution"), Does.Contain("恢复内置部署事务").And.Contain("AssetDatabase").And.Contain("未检查磁盘容量"));
                var message = ResourceBuiltInScope.Describe(plan);
                window.Message = message;
                window.Positive = "Not executed";
                window.Summary = summary;
                typeof(ResourceOperationConfirmation).GetField("_showTechnical", Flags).SetValue(window, true);
                window.ShowUtility();
                window.position = new Rect(80, 60, 720, 520);
                for (var i = 0; i < 5; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = new Vector2(350, 250), delta = new Vector2(0, 100) });
                yield return null;
                Assert.That(((Vector2)typeof(ResourceOperationConfirmation).GetField("_scroll", Flags).GetValue(window)).y, Is.GreaterThan(0));
                ResourceWorkbenchUX2Tests.Click(window, new Vector2(62, 500));
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(message), "Structured presentation must not replace or shorten complete scope copying.");
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                Assert.That(window.IsAccepted, Is.False);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(plan.WorkspacePath)), Is.False);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(plan.DirectoryPath)), Is.False);
                CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(source.Root));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (window)
                {
                    var parent = typeof(EditorWindow).GetField("m_Parent", Flags).GetValue(window) as UnityEngine.Object;
                    if (parent)
                    {
                        window.Close();
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(window);
                    }
                }
                UnityEngine.Object.DestroyImmediate(project);
                EditorGUIUtility.systemCopyBuffer = clipboard;
                if (focus)
                {
                    focus.Focus();
                }
            }
        }

        [Test]
        public void IndexStatusDistinguishesUnreadFailureStaleCurrentAndOtherWithoutRepeatingDigest()
        {
            var method = typeof(ResourceDeliveryView).GetMethod("DescribeIndex", Flags);
            Assert.That(method, Is.Not.Null, "Index relationship must derive from explicit read/error/staleness and exact selected identity, not a success string.");
            string Describe(bool read, bool stale, bool failed, bool selectedStale, string current, string selected) =>
                (string)method.Invoke(null, new object[] { read, stale, failed, selectedStale, current, selected });
            var digest = new string('a', 64);
            Assert.That(Describe(false, false, false, false, null, null), Does.Contain("尚未读取"));
            Assert.That(Describe(false, false, true, false, null, null), Does.Contain("读取失败"));
            Assert.That(Describe(true, true, true, false, digest, digest), Does.Contain("读取失败"));
            Assert.That(Describe(true, true, false, false, digest, digest), Does.Contain("已过期"));
            Assert.That(Describe(true, false, false, true, digest, digest), Does.Contain("所选版本需要重新验证"));
            Assert.That(Describe(true, false, false, false, null, digest), Does.Contain("没有当前发布版本"));
            Assert.That(Describe(true, false, false, false, digest, null), Does.Contain("选择一个版本"));
            var same = Describe(true, false, false, false, digest, digest);
            var other = Describe(true, false, false, false, digest, new string('b', 64));
            Assert.That(same, Does.Contain("当前指向所选版本").And.Contain("不代表应用正在使用"));
            Assert.That(other, Does.Contain("当前未指向所选版本").And.Contain("不代表应用正在使用"));
            Assert.That(same + other, Does.Not.Contain(digest));
        }

        [UnityTest]
        public IEnumerator TechnicalDetailsDoNotHideTheOriginalDefaultScopeWhenThereIsNoSummary()
        {
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceOperationConfirmation>();
            try
            {
                window.Message = string.Join("\n", Enumerable.Range(0, 100).Select(i => "owned exact target and write scope " + i));
                window.Positive = "Not executed";
                window.Details = new ResourceConfirmationDetails("owned identity", "owned paths", "owned execution");
                window.ShowUtility();
                window.position = new Rect(80, 60, 720, 520);
                for (var i = 0; i < 4; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(typeof(ResourceOperationConfirmation).GetField("_showTechnical", Flags).GetValue(window), Is.False);
                window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = new Vector2(350, 250), delta = new Vector2(0, 30) });
                yield return null;
                Assert.That(((Vector2)typeof(ResourceOperationConfirmation).GetField("_scroll", Flags).GetValue(window)).y, Is.GreaterThan(0),
                    "Adding optional technical sections must not hide the original visible target/scope before the user expands anything.");
                Assert.That(window.IsAccepted, Is.False);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                Assert.That(window.IsAccepted, Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (window)
                {
                    window.Close();
                }

                if (focus)
                {
                    focus.Focus();
                }
            }
        }
    }

    internal sealed class ResourceSearchFinalProbe : EditorWindow
    {
        internal string Value = "";
        private void OnGUI()
        {
            GUI.SetNextControlName("Final.Search");
            var method = typeof(ResourceWorkbenchGui).GetMethod("SearchField", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                Value = (string)method.Invoke(null, new object[] { Value, "Filter visible records only", new[] { GUILayout.MinWidth(80) } });
            }
        }
    }
}

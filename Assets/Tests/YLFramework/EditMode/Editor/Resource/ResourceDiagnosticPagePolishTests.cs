using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.ResourceBuild;

namespace YLFramework.Tests.ResourceEditor
{
    public sealed class ResourceDiagnosticPagePolishTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [UnityTest]
        public IEnumerator MinimumReadyCacheKeepsAReadableDataRowWithExpandedOutput()
        {
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
            try
            {
                var entries = new[] { "first", "second" }.Select(name => (ArtifactEntry)Activator.CreateInstance(typeof(ArtifactEntry), Flags, null,
                    new object[] { name, null, null, ArtifactKind.Metadata, ArtifactState.CompleteRecord, ArtifactProtection.None, 1L, true, false, Array.Empty<string>() }, null)).ToArray();
                var report = (ArtifactStoreReport)Activator.CreateInstance(typeof(ArtifactStoreReport), Flags, null,
                    new object[] { null, DateTimeOffset.UtcNow, ArtifactQueryStatus.Ready, entries, Array.Empty<ArtifactPreparedInfo>(),
                        Array.Empty<ArtifactVersionProtection>(), Array.Empty<string>(), Array.Empty<string>() }, null);
                var table = new ResourceArtifactTable();
                table.SetData(report);
                window.ShowUtility();
                window.position = new Rect(100, 80, 620, 440);
                ResourceWorkbenchPolishEvidence.SetPage(window, 4, 1);
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", Flags).GetValue(window);
                var diagnostics = view.GetType().GetField("_diagnostics", Flags).GetValue(view);
                var store = diagnostics.GetType().GetField("_store", Flags).GetValue(diagnostics);
                store.GetType().GetField("_report", Flags).SetValue(store, report);
                store.GetType().GetField("_table", Flags).SetValue(store, table);
                store.GetType().GetField("_showResults", Flags).SetValue(store, true);
                store.GetType().GetField("_note", Flags).SetValue(store, "查询已结束。");
                var layout = typeof(ResServiceEditorWindow).GetField("layout", Flags).GetValue(window);
                layout.GetType().GetField("output", Flags).SetValue(layout, true);
                layout.GetType().GetField("outputHeight", Flags).SetValue(layout, 300f);
                for (var i = 0; i < 6; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var content = (Rect)typeof(UnityEditor.IMGUI.Controls.TreeView).GetProperty("treeViewRect", Flags).GetValue(table);
                Assert.That(content.height, Is.GreaterThanOrEqualTo(ResourceWorkbenchGui.RowHeight), "Keeping only the table header is not a usable result list.\n" + window.layoutTrace);
                Assert.That(table.GetRows().Count, Is.EqualTo(2));
                Assert.That(window.rootHeight, Is.LessThanOrEqualTo(441), window.layoutTrace);
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
        public IEnumerator MinimumCacheQueryInputsHaveActualComfortableFieldsWithoutGrowingTheWindow()
        {
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
            try
            {
                window.ShowUtility();
                window.position = new Rect(100, 80, 620, 440);
                ResourceWorkbenchPolishEvidence.SetPage(window, 4, 1);
                for (var i = 0; i < 6; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var fields = window.Entries.Where(x => x.Style == "TextField" || x.Style == "textfield" || x.Style == "Resource.Field").ToArray();
                Assert.That(fields, Is.Not.Empty, window.layoutTrace);
                Assert.That(fields.All(x => x.Rect.height >= 26), Is.True,
                    "Cache root, project, platform and App inputs must be real 26-point controls, not 18-point controls inside extra spacing.\n" + window.layoutTrace);
                Assert.That(window.rootHeight, Is.LessThanOrEqualTo(441), window.layoutTrace);
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

        [Test]
        public void CacheByteSortingUsesObservedNumbersAcrossDisplayedUnitsInBothDirections()
        {
            ArtifactEntry Entry(string name, long bytes) => (ArtifactEntry)Activator.CreateInstance(typeof(ArtifactEntry), Flags, null,
                new object[] { name, null, null, ArtifactKind.Version, ArtifactState.CompleteRecord, ArtifactProtection.None, bytes, true, false, Array.Empty<string>() }, null);
            var entries = new[] { Entry("small", 512), Entry("large", 2048), Entry("zero", 0), Entry("same-as-small", 512) };
            var report = (ArtifactStoreReport)Activator.CreateInstance(typeof(ArtifactStoreReport), Flags, null,
                new object[] { null, DateTimeOffset.UtcNow, ArtifactQueryStatus.Ready, entries, Array.Empty<ArtifactPreparedInfo>(),
                    Array.Empty<ArtifactVersionProtection>(), Array.Empty<string>(), Array.Empty<string>() }, null);
            var table = new ResourceArtifactTable();
            table.SetData(report);
            table.multiColumnHeader.SetSorting(3, true);
            CollectionAssert.AreEqual(new[] { "zero", "small", "same-as-small", "large" }, table.GetRows().Select(x => x.displayName));
            table.multiColumnHeader.SetSorting(3, false);
            CollectionAssert.AreEqual(new[] { "large", "small", "same-as-small", "zero" }, table.GetRows().Select(x => x.displayName));
        }
    }
}

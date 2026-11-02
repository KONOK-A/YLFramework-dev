using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Tests.ResourceBuild;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceEditor
{
    public class ResourceWorkbenchInteractionTests
    {
        [Test]
        public void NativeTableKeepsHiddenSelectionAndResolvesModelIndicesAfterSortAndUndo()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            var a = fixture.Folder + "/Content/A-sort.txt";
            var b = fixture.Folder + "/Content/B-sort.txt";
            Assert.That(AssetDatabase.CopyAsset(fixture.TextPath, b), Is.True);
            Assert.That(AssetDatabase.CopyAsset(fixture.TextPath, a), Is.True);
            ResourceEditorOperations.AddSelections(project, 0, new[] { AssetDatabase.LoadMainAssetAtPath(b), AssetDatabase.LoadMainAssetAtPath(a) });
            var type = typeof(ResServiceView).Assembly.GetType("YLFramework.Editor.Resource.ResourceEntryTable");
            Assert.That(type, Is.Not.Null, "UX1 native resource table is not implemented yet; this is a new feature test, not a baseline product defect.");
            var table = (TreeView)Activator.CreateInstance(type, true);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var refresh = type.GetMethod("SetDefinitions", flags);
            var indices = type.GetMethod("GetSelectedIndices", flags);
            refresh.Invoke(table, new object[] { project, 0 });
            table.multiColumnHeader.SetSorting(0, true);
            CollectionAssert.AreEqual(new[] { "A-sort.txt", "B-sort.txt", "entry.txt" }, table.GetRows().Select(x => x.displayName));
            table.SetSelection(new[] { table.GetRows()[0].id, table.GetRows()[2].id });
            table.searchString = "A-sort";
            Assert.That(table.GetRows().Count, Is.EqualTo(1));
            Assert.That(table.GetSelection().Count, Is.EqualTo(2), "Filtering must not silently discard a hidden batch member.");
            CollectionAssert.AreEqual(new[] { 0, 2 }, (int[])indices.Invoke(table, new object[] { project, 0 }));
            var before = JsonUtility.ToJson(project);
            var movedGuid = AssetDatabase.AssetPathToGUID(a);
            Undo.ClearUndo(project);
            ResourceEditorOperations.MoveSelections(project, 0, (int[])indices.Invoke(table, new object[] { project, 0 }), 1);
            Assert.That(project.groups[0].selections.Single().guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(b)));
            Assert.That(project.groups[1].selections.Any(x => x.guid == movedGuid), Is.True);
            Undo.PerformUndo();
            Assert.That(JsonUtility.ToJson(project), Is.EqualTo(before));
            refresh.Invoke(table, new object[] { project, 0 });
            CollectionAssert.AreEqual(new[] { 0, 2 }, (int[])indices.Invoke(table, new object[] { project, 0 }), "Undo may replace serialized objects; selection must still resolve by identity.");
            refresh.Invoke(table, new object[] { project, -1 });
            Assert.That((int[])indices.Invoke(table, new object[] { project, -1 }), Is.Empty, "All Groups is a read-only projection, not a cross-group batch protocol.");
            Undo.ClearUndo(project);
        }

        [UnityTest]
        public IEnumerator QueuedOperationRunsOnEditorUpdateWithoutWaitingForInspectorIdle()
        {
            var view = new ResServiceView();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var calls = 0;
            try
            {
                typeof(ResServiceView).GetMethod("QueueOperation", flags).Invoke(view,
                    new object[] { new Func<string>(() =>
                    {
                    calls++;
                    return "Executed";
                    }) });
                var pending = (EditorApplication.CallbackFunction)typeof(ResServiceView).GetField("_pending", flags).GetValue(view);
                Assert.That(EditorApplication.delayCall?.GetInvocationList().Contains(pending) ?? false, Is.False,
                    "A formal queued action must not stall until background Editor inspectors become idle.");
                for (var frame = 0; frame < 20 && calls == 0; frame++)
                {
                    yield return null;
                }

                Assert.That(calls, Is.EqualTo(1));
                Assert.That(typeof(ResServiceView).GetField("_pending", flags).GetValue(view), Is.Null);
            }
            finally
            {
                view.OnDispose();
            }
        }

        [UnityTest]
        public IEnumerator OperationMessageBelongsToItsOriginalProjectAndPage()
        {
            using var fixture = new ResourceReleaseFixture();
            var view = new ResServiceView();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                view.SetProject(fixture.Project);
                var ran = false;
                typeof(ResServiceView).GetMethod("QueueOperation", flags).Invoke(view, new object[] { new Func<string>(() =>
                {
                    Assert.Throws<InvalidOperationException>(() => view.SetProject(null), "Started operations must retain their input context.");
                    view.SetProject(fixture.Project);
                    ran = true;
                    return "Owned resource operation";
                }) });
                for (var frame = 0; frame < 20 && !ran; frame++)
                {
                    yield return null;
                }

                Assert.That(ran, Is.True);
                var visible = typeof(ResServiceView).GetProperty("VisibleStatus", flags);
                Assert.That(visible.GetValue(view), Is.EqualTo("Owned resource operation"));
                view.RefreshPreview();
                var map = typeof(ResServiceView).GetField("_map", flags).GetValue(view);
                view.SetProject(fixture.Project);
                Assert.That(typeof(ResServiceView).GetField("_map", flags).GetValue(view), Is.SameAs(map), "Reselecting the same Project is not a definition change.");
                Assert.That(visible.GetValue(view), Is.EqualTo("Owned resource operation"));
                typeof(ResServiceView).GetField("_tab", flags).SetValue(view, 4);
                Assert.That(visible.GetValue(view), Is.Null, "A resource success must not become a Diagnostics banner.");
                view.SetProject(null);
                Assert.That(visible.GetValue(view), Is.Null, "Project changes must not relabel old success as the new context.");
            }
            finally
            {
                view.OnDispose();
            }
        }

        [UnityTest]
        public IEnumerator AggregateGroupRejectsDropEvenWhenGuiIsEnabled()
        {
            using var fixture = new ResourceReleaseFixture();
            var path = fixture.Folder + "/Content/aggregate-drop.txt";
            Assert.That(AssetDatabase.CopyAsset(fixture.TextPath, path), Is.True);
            var window = ScriptableObject.CreateInstance<ResourceDropTestWindow>();
            window.Project = fixture.Project;
            window.AllowEdits = true;
            window.Aggregate = true;
            try
            {
                window.ShowUtility();
                yield return null;
                var before = JsonUtility.ToJson(fixture.Project);
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new[] { AssetDatabase.LoadMainAssetAtPath(path) };
                window.PendingDrop = true;
                while (window.PendingDrop)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before));
                Assert.That(window.Changes, Is.Zero);
                Assert.That(window.LastVisualMode, Is.EqualTo(DragAndDropVisualMode.Rejected));
            }
            finally
            {
                window.Close();
                DragAndDrop.objectReferences = Array.Empty<Object>();
                DragAndDrop.paths = Array.Empty<string>();
            }
        }

        [UnityTest]
        public IEnumerator SelectingTheLatestAttemptDoesNotClaimTheDefinitionChanged()
        {
            var previous = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceAttemptTestWindow>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                window.View.GetType().GetMethod("RecordReport", flags).Invoke(window.View,
                    new object[] { new ResourceEditorBuildReport { BuildError = new InvalidOperationException("owned failed attempt") }, new Action<ResourceBuildResult>(_ => { }) });
                window.View.GetType().GetField("_areAttemptsExpanded", flags).SetValue(window.View, true);
                window.ShowUtility();
                for (var frame = 0; frame < 10 && window.Button.width == 0; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Button.width, Is.GreaterThan(0));
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = window.Button.center });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = window.Button.center });
                Assert.That(window.View.HasDefinitionChanged, Is.False, "Selecting a report is not a Project edit or import.");
                Assert.That(window.View.IsHistoricalReport, Is.False);
                window.View.GetType().GetMethod("RecordReport", flags).Invoke(window.View,
                    new object[] { new ResourceEditorBuildReport { BuildError = new InvalidOperationException("newer owned attempt") }, new Action<ResourceBuildResult>(_ => { }) });
                for (var frame = 0; frame < 3; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = window.Button.center });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = window.Button.center });
                Assert.That(window.View.IsHistoricalReport, Is.True, "The older attempt still has a separate history gate.");
                Assert.That(window.View.HasDefinitionChanged, Is.False, "History selection must not poison the global definition revision.");
            }
            finally
            {
                window.Close();
                if (previous)
                {
                    previous.Focus();
                }
            }
        }

        [Test]
        public void RefreshRebuildsSelectionRowsAfterDefinitionChangesOutsideTheView()
        {
            using var fixture = new ResourceReleaseFixture();
            var view = new ResServiceView();
            try
            {
                view.SetProject(fixture.Project);
                view.RefreshPreview();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var definition = typeof(ResServiceView).GetField("_definition", flags).GetValue(view);
                var table = (TreeView)definition.GetType().GetField("_table", flags).GetValue(definition);
                Assert.That(table.GetRows().Count, Is.EqualTo(1));
                var path = fixture.Folder + "/Content/external-edit.txt";
                Assert.That(AssetDatabase.CopyAsset(fixture.TextPath, path), Is.True);
                ResourceEditorOperations.AddSelections(fixture.Project, 0, new[] { AssetDatabase.LoadMainAssetAtPath(path) });
                view.RefreshPreview();
                CollectionAssert.AreEquivalent(new[] { AssetDatabase.AssetPathToGUID(fixture.TextPath), AssetDatabase.AssetPathToGUID(path) },
                    table.GetRows().Cast<ResourceEntryTable.Row>().Select(x => x.Guid),
                    "Explicit refresh must rebuild the real row-to-definition identity mapping after external edits.");
                Undo.ClearUndo(fixture.Project);
            }
            finally
            {
                view.OnDispose();
            }
        }

        [UnityTest]
        public IEnumerator DisabledCustomDropRejectsInputWithoutMutatingTheDefinitionOrUndo()
        {
            using var fixture = new ResourceReleaseFixture();
            var path = fixture.Folder + "/Content/disabled-drop.txt";
            Assert.That(AssetDatabase.CopyAsset(fixture.TextPath, path), Is.True);
            var window = ScriptableObject.CreateInstance<ResourceDropTestWindow>();
            window.Project = fixture.Project;
            try
            {
                window.position = new Rect(120, 80, 500, 220);
                window.ShowUtility();
                window.Focus();
                yield return null;
                var before = JsonUtility.ToJson(fixture.Project);
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new[] { AssetDatabase.LoadMainAssetAtPath(path) };
                DragAndDrop.paths = new[] { path };
                window.AllowEdits = false;
                window.PendingDrop = true;
                while (window.PendingDrop)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before), "A disabled custom input handler must respect the same editing gate as native controls.");
                Assert.That(window.Changes, Is.Zero);
                Assert.That(window.LastVisualMode, Is.EqualTo(DragAndDropVisualMode.Rejected), window.LastInput);
                window.AllowEdits = true;
                DragAndDrop.objectReferences = new[] { AssetDatabase.LoadMainAssetAtPath(path) };
                window.PendingDrop = true;
                while (window.PendingDrop)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(fixture.Project.groups[0].selections.Count, Is.EqualTo(2), "The enabled handler must remain functional.");
                Assert.That(window.Changes, Is.EqualTo(1));
                Undo.ClearUndo(fixture.Project);
            }
            finally
            {
                window.Close();
                DragAndDrop.objectReferences = Array.Empty<Object>();
                DragAndDrop.paths = Array.Empty<string>();
            }
        }
    }

    internal sealed class ResourceAttemptTestWindow : EditorWindow
    {
        internal readonly ResourceBuildView View = new();
        internal Rect Button;
        private void OnGUI()
        {
            View.DrawAttempts();
            if (Event.current.type == EventType.Repaint)
            {
                Button = GUILayoutUtility.GetLastRect();
            }
        }
    }

    internal sealed class ResourceDropTestWindow : EditorWindow
    {
        internal ResourceProject Project;
        internal bool AllowEdits;
        internal bool Aggregate;
        internal int Changes;
        internal bool PendingDrop;
        internal DragAndDropVisualMode LastVisualMode;
        internal string LastInput;
        private object _definition;
        private MethodInfo _drop;

        private void OnEnable()
        {
            var type = typeof(ResServiceView).Assembly.GetType("YLFramework.Editor.Resource.ResourceDefinitionView");
            _definition = Activator.CreateInstance(type, true);
            _drop = type.GetMethod("HandleDrop", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private void OnGUI()
        {
            if (!Project || !PendingDrop || Event.current.type != EventType.Repaint)
            {
                return;
            }

            PendingDrop = false;
            using var disabled = new EditorGUI.DisabledScope(!AllowEdits);
            var previous = Event.current;
            _definition.GetType().GetField("_group", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_definition, Aggregate ? -1 : 0);
            var input = new Event { type = EventType.DragPerform, mousePosition = new Vector2(30, 30) };
            Event.current = input;
            try
            {
                LastInput = Event.current.type + " refs=" + DragAndDrop.objectReferences.Length + " enabled=" + GUI.enabled;
                _drop.Invoke(_definition, new object[] { new Rect(10, 10, 440, 150), Project,
                    new Action(() => Changes++), new Action<Func<string>>(operation => operation()) });
                LastVisualMode = DragAndDrop.visualMode;
            }
            finally
            {
                Event.current = previous;
            }
        }
    }
}

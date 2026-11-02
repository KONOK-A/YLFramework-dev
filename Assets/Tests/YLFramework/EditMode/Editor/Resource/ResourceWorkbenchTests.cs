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
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceEditor
{
    public class ResourceWorkbenchTests
    {
        [UnityTest]
        public IEnumerator BlankClickReleasesTextFocusWithoutClearingTheResourceSelectionOrSaving()
        {
            using var fixture = new ResourceReleaseFixture();
            var previousFocus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<WorkbenchInputTestWindow>();
            try
            {
                window.position = new Rect(80, 60, 1080, 720);
                window.ShowUtility();
                window.Focus();
                window.SetProject(fixture.Project);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var layout = typeof(ResServiceEditorWindow).GetField("layout", flags).GetValue(window);
                layout.GetType().GetField("settings").SetValue(layout, true);
                window.RequestedFocus = "Resource.ContentKeyId";
                for (var frame = 0; frame < 20 && window.FocusedControl != "Resource.ContentKeyId"; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.FocusedControl, Is.EqualTo("Resource.ContentKeyId"));
                var before = JsonUtility.ToJson(fixture.Project);
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = new Vector2(1000, 600) });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = new Vector2(1000, 600) });
                window.Repaint();
                yield return null;
                Assert.That(window.FocusedControl, Is.Empty, "A blank click must dismiss the input highlight/caret instead of retaining KeyId focus.");
                Assert.That(EditorGUIUtility.editingTextField, Is.False);
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before));
                layout.GetType().GetField("settings").SetValue(layout, false);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.F, control = true });
                for (var frame = 0; frame < 10 && window.FocusedControl != "Resource.EntrySearch"; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.FocusedControl, Is.EqualTo("Resource.EntrySearch"));
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", flags).GetValue(window);
                var definition = typeof(ResServiceView).GetField("_definition", flags).GetValue(view);
                var table = (UnityEditor.IMGUI.Controls.TreeView)definition.GetType().GetField("_table", flags).GetValue(definition);
                var selected = table.GetSelection().ToArray();
                Assert.That(selected, Is.Not.Empty);
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = new Vector2(50, 600) });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = new Vector2(50, 600) });
                window.Repaint();
                yield return null;
                Assert.That(window.FocusedControl, Is.Empty);
                CollectionAssert.AreEqual(selected, table.GetSelection(), "Losing text focus must not discard the user's resource batch selection.");
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before));
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                window.Close();
                if (previousFocus)
                {
                    previousFocus.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator BlankClickCommitsThePendingGroupRenameWithoutSavingOrLosingUndo()
        {
            using var fixture = new ResourceReleaseFixture();
            var previousFocus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<WorkbenchInputTestWindow>();
            try
            {
                window.position = new Rect(80, 60, 1080, 720);
                window.ShowUtility();
                window.Focus();
                window.SetProject(fixture.Project);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", flags).GetValue(window);
                var definition = typeof(ResServiceView).GetField("_definition", flags).GetValue(view);
                definition.GetType().GetField("_showGroupSettings", flags).SetValue(definition, true);
                window.RequestedFocus = "Resource.GroupId";
                for (var frame = 0; frame < 20 && window.FocusedControl != "Resource.GroupId"; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.FocusedControl, Is.EqualTo("Resource.GroupId"));
                var oldName = fixture.Project.groups[0].id;
                Undo.ClearUndo(fixture.Project);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.A, control = true });
                foreach (var character in "blank-click-rename")
                {
                    window.SendEvent(new Event { type = EventType.KeyDown, character = character });
                }

                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = new Vector2(50, 600) });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = new Vector2(50, 600) });
                for (var frame = 0; frame < 10; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.FocusedControl, Is.Empty);
                Assert.That(fixture.Project.groups[0].id, Is.EqualTo("blank-click-rename"));
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.True, "Blank click commits editing, not an implicit save.");
                StringAssert.DoesNotContain("blank-click-rename", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(fixture.Project))));
                Undo.PerformUndo();
                Assert.That(fixture.Project.groups[0].id, Is.EqualTo(oldName));
                for (var frame = 0; frame < 5; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(fixture.Project.groups[0].id, Is.EqualTo(oldName), "Repaint must not resubmit the stale pre-Undo text.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Undo.ClearUndo(fixture.Project);
                window.Close();
                if (previousFocus)
                {
                    previousFocus.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator ActualWorkbenchKeyboardCreatesGroupSavesAndFocusesSearchWithoutTouchingImporter()
        {
            using var fixture = new ResourceReleaseFixture();
            var previousFocus = EditorWindow.focusedWindow;
            var previousSelection = Selection.objects;
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath + ".meta"));
            var other = ScriptableObject.CreateInstance<ResourceProject>();
            AssetDatabase.CreateAsset(other, fixture.Folder + "/UnrelatedProject.asset");
            other.displayName = "Unrelated dirty edit";
            EditorUtility.SetDirty(other);
            var window = ScriptableObject.CreateInstance<WorkbenchInputTestWindow>();
            try
            {
                window.position = new Rect(80, 60, 1080, 720);
                window.ShowUtility();
                window.Focus();
                window.SetProject(fixture.Project);
                yield return null;
                var count = fixture.Project.groups.Count;
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.G, alt = true });
                yield return null;
                Assert.AreEqual(count + 1, fixture.Project.groups.Count, "Alt+G must create a real undoable Group in the open workbench.");
                Assert.IsTrue(EditorUtility.IsDirty(fixture.Project));
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.S, control = true });
                for (var frame = 0; frame < 20 && EditorUtility.IsDirty(fixture.Project); frame++)
                {
                    yield return null;
                }

                Assert.IsFalse(EditorUtility.IsDirty(fixture.Project), "Ctrl+S saves only the selected Project definition.");
                Assert.IsTrue(EditorUtility.IsDirty(other), "Ctrl+S must not fall through to Unity's global Save Project command.");
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.F, control = true });
                for (var i = 0; i < 10 && window.FocusedControl != "Resource.EntrySearch"; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.AreEqual("Resource.EntrySearch", window.FocusedControl);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath + ".meta")));
                Assert.IsEmpty(AssetImporter.GetAtPath(fixture.TextPath).assetBundleName);
                Undo.ClearUndo(fixture.Project);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (window)
                {
                    window.Close();
                }

                Selection.objects = previousSelection;
                if (previousFocus)
                {
                    previousFocus.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator DisabledWorkbenchKeyboardCannotEditOrSaveTheProject()
        {
            using var fixture = new ResourceReleaseFixture();
            var previousFocus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<WorkbenchInputTestWindow>();
            try
            {
                window.position = new Rect(80, 60, 1080, 720);
                window.ShowUtility();
                window.Focus();
                window.SetProject(fixture.Project);
                window.BlockInput = true;
                yield return null;
                var count = fixture.Project.groups.Count;
                EditorUtility.SetDirty(fixture.Project);
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.True);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.G, alt = true });
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.S, control = true });
                yield return null;
                Assert.That(fixture.Project.groups.Count, Is.EqualTo(count), "A disabled workbench must reject keyboard edits as well as buttons.");
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.True, "Disabled Ctrl+S must not save an asset behind the visible editing gate. " + window.LastKeyState);
            }
            finally
            {
                Undo.ClearUndo(fixture.Project);
                if (window)
                {
                    window.Close();
                }

                if (previousFocus)
                {
                    previousFocus.Focus();
                }
            }
        }

        [Test]
        public void BatchMoveAndRemovePreserveAddressesAliasesAndUndoAsOneEdit()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            var source = project.groups[0];
            source.selections[0].address = "formal/address";
            source.selections[0].aliases = new[] { "formal/alias" };
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            var sourceGuid = source.selections[0].guid;
            var originalCount = source.selections.Count;
            var target = (int)Invoke("AddGroup", project, "batch-target");
            Undo.ClearUndo(project);
            var before = JsonUtility.ToJson(project);
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath + ".meta"));
            Invoke("MoveSelections", project, 0, new[] { 0 }, target);
            Assert.AreEqual(originalCount - 1, project.groups[0].selections.Count);
            Assert.AreEqual(sourceGuid, project.groups[target].selections.Single().guid);
            Assert.AreEqual("formal/address", project.groups[target].selections[0].address);
            CollectionAssert.AreEqual(new[] { "formal/alias" }, project.groups[target].selections[0].aliases);
            Undo.PerformUndo();
            Assert.AreEqual(before, JsonUtility.ToJson(project));
            Undo.PerformRedo();
            Invoke("RemoveSelections", project, target, new[] { 0 });
            Assert.IsEmpty(project.groups[target].selections);
            Undo.PerformUndo();
            Assert.AreEqual(sourceGuid, project.groups[target].selections.Single().guid);
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath + ".meta")));
            Undo.ClearUndo(project);
        }

        [UnityTest]
        public IEnumerator CtrlSSavesTheGroupRenameStillBeingEdited()
        {
            using var fixture = new ResourceReleaseFixture();
            var previous = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<WorkbenchInputTestWindow>();
            try
            {
                window.position = new Rect(80, 60, 1080, 720);
                window.ShowUtility();
                window.Focus();
                window.SetProject(fixture.Project);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", flags).GetValue(window);
                var definition = typeof(ResServiceView).GetField("_definition", flags).GetValue(view);
                definition.GetType().GetField("_showGroupSettings", flags).SetValue(definition, true);
                window.RequestedFocus = "Resource.GroupId";
                for (var frame = 0; frame < 20 && window.FocusedControl != "Resource.GroupId"; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.FocusedControl, Is.EqualTo("Resource.GroupId"));
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.A, control = true });
                foreach (var character in "renamed-group")
                {
                    window.SendEvent(new Event { type = EventType.KeyDown, character = character });
                }

                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.S, control = true });
                for (var frame = 0; frame < 10; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(fixture.Project.groups[0].id, Is.EqualTo("renamed-group"));
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.False);
                StringAssert.Contains("id: renamed-group", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(fixture.Project))));
            }
            finally
            {
                Undo.ClearUndo(fixture.Project);
                if (window)
                {
                    window.Close();
                }

                if (previous)
                {
                    previous.Focus();
                }
            }
        }

        [Test]
        public void InvalidBatchAndGroupRenameAreAtomicAndDoNotMutateSourceData()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            var target = (int)Invoke("AddGroup", project, "valid-target");
            var before = JsonUtility.ToJson(project);
            Assert.Throws<ArgumentException>(() => Invoke("MoveSelections", project, 0, new[] { 0, int.MaxValue }, target));
            Assert.Throws<ArgumentException>(() => Invoke("RemoveSelections", project, 0, new[] { -1 }));
            Assert.Throws<ArgumentException>(() => Invoke("RenameGroup", project, target, project.groups[0].id.ToUpperInvariant()));
            Assert.Throws<ArgumentException>(() => Invoke("RenameGroup", project, target, "../escape"));
            Assert.AreEqual(before, JsonUtility.ToJson(project), "A failed batch must not partially remove or copy selections.");
            Invoke("RenameGroup", project, target, "renamed-target");
            Assert.AreEqual("renamed-target", project.groups[target].id);
            Undo.PerformUndo();
            Assert.AreEqual("valid-target", project.groups[target].id);
            Assert.IsEmpty(AssetImporter.GetAtPath(fixture.TextPath).assetBundleName);
            Undo.ClearUndo(project);
        }

        [UnityTest]
        public IEnumerator CtrlFReturnsFromAtlasLinksToAnActuallyFocusableSearchField()
        {
            using var fixture = new ResourceReleaseFixture();
            var previous = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<WorkbenchInputTestWindow>();
            try
            {
                window.ShowUtility();
                window.Focus();
                window.SetProject(fixture.Project);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", flags).GetValue(window);
                var definition = typeof(ResServiceView).GetField("_definition", flags).GetValue(view);
                definition.GetType().GetField("_mode", flags).SetValue(definition, 2);
                yield return null;
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.F, control = true });
                for (var frame = 0; frame < 10 && window.FocusedControl != "Resource.EntrySearch"; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.FocusedControl, Is.EqualTo("Resource.EntrySearch"));
                Assert.That(definition.GetType().GetField("_mode", flags).GetValue(definition), Is.EqualTo(0));
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

        private static object Invoke(string name, params object[] arguments)
        {
            var method = typeof(ResourceEditorOperations).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(x => x.Name == name && x.GetParameters().Length == arguments.Length);
            Assert.IsNotNull(method, "Missing formal workbench operation: " + name);
            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }
    }

    internal sealed class WorkbenchInputTestWindow : ResServiceEditorWindow
    {
        internal string FocusedControl;
        internal bool BlockInput;
        internal string LastKeyState;
        internal string RequestedFocus;
        private void OnGUI()
        {
            var key = Event.current.rawType == EventType.KeyDown;
            using var disabled = new EditorGUI.DisabledScope(BlockInput);
            if (key)
            {
                LastKeyState = Event.current.keyCode + " type=" + Event.current.type + " raw=" + Event.current.rawType + " enabled=" + GUI.enabled;
            }

            typeof(ResServiceEditorWindow).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, null);
            if (Event.current.type == EventType.Repaint && RequestedFocus != null)
            {
                EditorGUI.FocusTextInControl(RequestedFocus);
                RequestedFocus = null;
            }
            if (Event.current.type == EventType.Repaint)
            {
                FocusedControl = GUI.GetNameOfFocusedControl();
            }
        }
    }
}

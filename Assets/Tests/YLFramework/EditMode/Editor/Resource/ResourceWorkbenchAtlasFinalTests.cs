using System;
using System.Collections;
using System.Collections.Generic;
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
    public sealed class ResourceWorkbenchAtlasFinalTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void DisposingAnOwnedDragLeavesNoNativePathsForTheNextTreeOwner()
        {
            Assert.That(DragAndDrop.objectReferences, Is.Empty, "Do not take over an existing object drag.");
            Assert.That(DragAndDrop.paths, Is.Null.Or.Empty, "Do not take over an existing file drag.");
            using (var fixture = new ResourceReleaseFixture())
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.paths = new[] { fixture.Folder + "/Content" };
                DragAndDrop.objectReferences = Array.Empty<Object>();
                DragAndDrop.paths = Array.Empty<string>();
            }
            Assert.That(DragAndDrop.objectReferences, Is.Empty);
            Assert.That(DragAndDrop.paths, Is.Null.Or.Empty, "Disposing this fixture must not leave its native drag for another owner.");
        }

        [Test]
        public void DisposingAnotherFixtureDoesNotTakeOverTheCurrentDragOwner()
        {
            Assert.That(DragAndDrop.objectReferences, Is.Empty);
            Assert.That(DragAndDrop.paths, Is.Null.Or.Empty);
            using (var owner = new ResourceReleaseFixture())
            {
                var expected = owner.Folder + "/Content";
                using (var other = new ResourceReleaseFixture())
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.paths = new[] { expected };
                    DragAndDrop.objectReferences = Array.Empty<Object>();
                }
                Assert.That(DragAndDrop.paths, Is.EqualTo(new[] { expected }));
            }
            Assert.That(DragAndDrop.paths, Is.Null.Or.Empty);
        }

        [UnityTest]
        public IEnumerator SourceControlsAddReplaceRemoveAndUndoWithoutSavingOrDeletingAssets()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = CreateRules(fixture);
            var folder = fixture.Folder + "/Content/ExtraInputs";
            Assert.That(AssetDatabase.CreateFolder(fixture.Folder + "/Content", "ExtraInputs"), Is.Not.Empty);
            var folderAsset = AssetDatabase.LoadMainAssetAtPath(folder);
            var sprite = configuration.rules[0].sources[0];
            var sourceBefore = ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Folder + "/Content")))
                .Where(x => !x.Replace('\\', '/').StartsWith("/GeneratedAtlases/", StringComparison.Ordinal)).ToArray();
            var otherBefore = JsonUtility.ToJson(configuration.rules[1]);
            var path = AssetDatabase.GetAssetPath(configuration);
            var saved = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var project = fixture.Project;
            project.displayName = "Unrelated unsaved atlas test edit";
            EditorUtility.SetDirty(project);
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceAtlasFinalProbe>();
            try
            {
                window.Initialize(project, 0);
                window.ShowUtility();
                window.position = new Rect(80, 60, 1080, 720);
                yield return Paint(window);
                Assert.That(window.SourceRemoves.Length, Is.EqualTo(1), "Sources needs a real object row and an explicit array-only remove, not generic Size/Element controls.\n" + window.Trace);
                Undo.ClearUndo(configuration);
                Undo.IncrementCurrentGroup();
                Click(window, window.AddSource.center);
                Assert.That(configuration.rules[0].sources.Length, Is.EqualTo(2));
                Assert.That(configuration.rules[0].sources[1], Is.Null, "Adding a source must create an empty object slot rather than duplicate the previous source.");
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(configuration.rules[0].sources, Is.EqualTo(new[] { sprite }));
                Undo.PerformRedo();
                Assert.That(configuration.rules[0].sources.Length, Is.EqualTo(2));
                yield return Paint(window);

                Undo.IncrementCurrentGroup();
                Drop(window, window.SourceField(1).center, folderAsset);
                Assert.That(configuration.rules[0].sources[1], Is.EqualTo(folderAsset), "The native serialized object field must edit the same rule configuration.");
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(configuration.rules[0].sources[1], Is.Null);
                Undo.PerformRedo();
                Assert.That(configuration.rules[0].sources[1], Is.EqualTo(folderAsset));
                yield return Paint(window);

                Undo.IncrementCurrentGroup();
                Click(window, window.SourceRemoves[0].center);
                Assert.That(configuration.rules[0].sources, Is.EqualTo(new[] { folderAsset }), "Removing a non-null object must remove its array slot, not merely clear it.");
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(configuration.rules[0].sources, Is.EqualTo(new[] { sprite, folderAsset }));
                Undo.PerformRedo();
                Assert.That(configuration.rules[0].sources, Is.EqualTo(new[] { folderAsset }));
                Assert.That(JsonUtility.ToJson(configuration.rules[1]), Is.EqualTo(otherBefore));
                Assert.That(EditorUtility.IsDirty(configuration), Is.True);
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(saved), "Browsing and editing do not implicitly save rules.");

                window.FieldsOnly = false;
                yield return Paint(window);
                Click(window, window.Button(88, ResourceWorkbenchGui.BarHeight).center);
                Assert.That(project.automaticAtlases, Is.SameAs(configuration));
                Assert.That(window.Serialized.targetObject, Is.SameAs(configuration));
                Assert.That(EditorUtility.IsDirty(configuration), Is.False);
                Assert.That(EditorUtility.IsDirty(project), Is.True, "Explicit rule save must not save the unrelated Project edit.");
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.Not.EqualTo(saved));
                Assert.That(JsonUtility.ToJson(configuration.rules[1]), Is.EqualTo(otherBefore));
                Assert.That(configuration.outputs, Is.Empty);
                Assert.That(window.Queued, Is.Zero, "Editing and saving must not request preview, generation, preparation, or building.");
                Assert.That(AssetDatabase.FindAssets("t:SpriteAtlas", new[] { Path.GetDirectoryName(path).Replace('\\', '/') }), Is.Empty);
                CollectionAssert.AreEqual(sourceBefore, ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Folder + "/Content")))
                    .Where(x => !x.Replace('\\', '/').StartsWith("/GeneratedAtlases/", StringComparison.Ordinal)).ToArray(),
                    "Only the rules asset may change; source files and their meta must survive removal unchanged.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Undo.ClearUndo(configuration);
                window.Close();
                DragAndDrop.objectReferences = Array.Empty<Object>();
                DragAndDrop.paths = Array.Empty<string>();
                if (focus)
                {
                    focus.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator DefaultInspectorDrawUsesTheSameExplicitSourceArrayControls()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = CreateRules(fixture);
            var otherBefore = JsonUtility.ToJson(configuration.rules[1]);
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceAtlasFinalProbe>();
            try
            {
                window.Initialize(fixture.Project, -1);
                window.ShowUtility();
                window.position = new Rect(80, 60, 1080, 720);
                yield return Paint(window);
                Assert.That(window.SourceRemoves.Length, Is.EqualTo(1), "ResourceAtlasFields.Draw(serialized) must not send Sources back through the generic English array drawer.\n" + window.Trace);
                Undo.ClearUndo(configuration);
                Undo.IncrementCurrentGroup();
                Click(window, window.AddSource.center);
                Assert.That(configuration.rules[0].sources.Length, Is.EqualTo(2));
                Assert.That(configuration.rules[0].sources[1], Is.Null);
                Assert.That(JsonUtility.ToJson(configuration.rules[1]), Is.EqualTo(otherBefore));
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(configuration.rules[0].sources.Length, Is.EqualTo(1));
                Assert.That(configuration.outputs, Is.Empty);
                Assert.That(window.Queued, Is.Zero);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Undo.ClearUndo(configuration);
                window.Close();
                if (focus)
                {
                    focus.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator SelectingOneOfSeveralOutputsDoesNotRetargetTheEditedRuleOrGenerate()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = CreateRules(fixture);
            var root = fixture.Folder + "/Content";
            foreach (var name in new[] { "North", "South" })
            {
                Assert.That(AssetDatabase.CreateFolder(root, name), Is.Not.Empty);
                Assert.That(AssetDatabase.CopyAsset(root + "/Sprites.png", root + "/" + name + "/Sprites.png"), Is.True);
            }
            configuration.rules[0].grouping = ResourceAtlasGrouping.PerFolder;
            configuration.rules[0].sources = new[] { AssetDatabase.LoadMainAssetAtPath(root + "/North"), AssetDatabase.LoadMainAssetAtPath(root + "/South") };
            EditorUtility.SetDirty(configuration);
            AssetDatabase.SaveAssetIfDirty(configuration);
            var before = ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Folder)));
            var packing = fixture.Project.groups.Select(x => x.packing).ToArray();
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceAtlasFinalProbe>();
            try
            {
                window.Initialize(fixture.Project, 1);
                window.FieldsOnly = false;
                window.View.SelectRule(1);
                window.ShowUtility();
                window.position = new Rect(80, 60, 1080, 720);
                yield return Paint(window);
                Click(window, window.Button(124, ResourceWorkbenchGui.BarHeight).center);
                yield return Paint(window);
                var plan = (ResourceAtlasPlan)typeof(ResourceAtlasView).GetField("_plan", Flags).GetValue(window.View);
                Assert.That(plan.Changes.Count, Is.EqualTo(3));
                CollectionAssert.AreEquivalent(new[] { "atlases/first/North", "atlases/first/South" }, plan.Changes.Where(x => x.Rule.id == "first").Select(x => x.Address));
                Assert.That(plan.Changes.All(x => x.GroupId == fixture.Project.groups[0].id), Is.True);
                Click(window, new Vector2(window.ListRect.x + 30, window.ListRect.y + 28 + 44 + 20));
                yield return Paint(window);
                Assert.That((int)typeof(ResourceAtlasView).GetField("_selected", Flags).GetValue(window.View), Is.EqualTo(1));
                Assert.That((int)typeof(ResourceAtlasView).GetField("_rule", Flags).GetValue(window.View), Is.EqualTo(1), "Selecting an output from the first rule must not silently retarget editing of the second rule.");
                Assert.That(configuration.outputs, Is.Empty);
                Assert.That(window.Queued, Is.EqualTo(1), "The one explicit read must not enqueue generation, preparation, or a build.");
                CollectionAssert.AreEqual(packing, fixture.Project.groups.Select(x => x.packing));
                CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Folder))));
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
        public IEnumerator WidePanesDefaultToOneThirdAndPreserveTheDragAcrossClampingAndNarrowMode()
        {
            using var fixture = new ResourceReleaseFixture();
            CreateRules(fixture);
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceAtlasFinalProbe>();
            try
            {
                window.Initialize(fixture.Project, 0);
                window.FieldsOnly = false;
                window.ShowUtility();
                window.position = new Rect(80, 60, 1080, 720);
                yield return Paint(window);
                Assert.That(window.ListRect.width / window.position.width, Is.InRange(.31f, .35f), "The atlas page must default to approximately 1/3 list and 2/3 editor, not the old fixed 48% list.\n" + window.Trace);
                Assert.That(window.Button(76, ResourceWorkbenchGui.BarHeight).xMax, Is.GreaterThan(window.position.width - 28), "The editor must occupy the remaining two thirds, not stop at its minimum width and leave a blank third.\n" + window.Trace);
                window.position = new Rect(80, 60, 1200, 720);
                yield return Paint(window);
                Assert.That(window.ListRect.width / window.position.width, Is.InRange(.31f, .35f), "Drawing the initial layout must not turn its automatic width into a stored user request.");
                window.position = new Rect(80, 60, 1080, 720);
                yield return Paint(window);
                var initial = window.ListRect.width;
                var divider = new Vector2(window.ListRect.xMax + 3, window.ListRect.center.y);
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = divider });
                Assert.That(GUIUtility.hotControl, Is.Not.Zero, "The visible divider must capture a real user drag.");
                window.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = divider + Vector2.right * 240, delta = Vector2.right * 240 });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = divider + Vector2.right * 240 });
                yield return Paint(window);
                var requested = window.ListRect.width;
                Assert.That(requested, Is.EqualTo(initial + 240).Within(2));
                Assert.That(window.position.width - window.ListRect.xMax, Is.GreaterThanOrEqualTo(420));
                window.position = new Rect(80, 60, 920, 720);
                yield return Paint(window);
                Assert.That(window.ListRect.width, Is.LessThan(requested - 20));
                Assert.That(window.ListRect.width, Is.GreaterThanOrEqualTo(260));
                Assert.That(window.position.width - window.ListRect.xMax, Is.GreaterThanOrEqualTo(420));

                window.position = new Rect(80, 60, 620, 440);
                yield return Paint(window);
                Assert.That(window.ListRect.width, Is.GreaterThan(590), "Narrow mode must keep the original full-width list/editor switch, not squeeze two panes together.");
                var compact = window.Entries.Where(x => x.Style == "Resource.Navigation").OrderBy(x => x.Rect.y).Last().Rect;
                Click(window, new Vector2(compact.x + compact.width * .75f, compact.center.y));
                yield return Paint(window);
                Assert.That((bool)typeof(ResourceAtlasView).GetField("_showCompactEditor", Flags).GetValue(window.View), Is.True);
                Assert.That(window.Button(76, ResourceWorkbenchGui.BarHeight).xMin, Is.LessThan(620));
                Assert.That(window.RootHeight, Is.LessThanOrEqualTo(441), window.Trace);
                window.position = new Rect(80, 60, 1080, 720);
                yield return Paint(window);
                Assert.That(window.ListRect.width, Is.EqualTo(requested).Within(2), "Clamped drawing and narrow mode must not overwrite the user's requested width.");
                Assert.That(window.Queued, Is.Zero);
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

        private static ResourceAtlasConfiguration CreateRules(ResourceReleaseFixture fixture)
        {
            var configuration = ResourceAtlasGenerator.CreateConfiguration(fixture.Project, fixture.Folder + "/Content", "GeneratedAtlases");
            foreach (var id in new[] { "first", "second" })
            {
                configuration.rules.Add(new ResourceAtlasRule
                {
                    id = id,
                    groupId = fixture.Project.groups[0].id,
                    address = "atlases/" + id,
                    sources = new[] { AssetDatabase.LoadAssetAtPath<Texture2D>(fixture.Folder + "/Content/Sprites.png") }
                });
            }

            EditorUtility.SetDirty(configuration);
            AssetDatabase.SaveAssetIfDirty(configuration);
            return configuration;
        }

        private static IEnumerator Paint(EditorWindow window)
        {
            for (var i = 0; i < 5; i++)
            {
                window.Repaint();
                yield return null;
            }
        }

        private static void Click(EditorWindow window, Vector2 point)
        {
            window.Focus();
            window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = point });
            window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = point });
        }

        private static void Drop(EditorWindow window, Vector2 point, Object value)
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new[] { value };
            DragAndDrop.paths = new[] { AssetDatabase.GetAssetPath(value) };
            window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = point });
            window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = point });
            DragAndDrop.objectReferences = Array.Empty<Object>();
            DragAndDrop.paths = Array.Empty<string>();
        }
    }

    internal sealed class ResourceAtlasFinalProbe : EditorWindow
    {
        internal sealed class Entry
        {
            internal string Style;
            internal Rect Rect;
        }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        internal readonly ResourceAtlasView View = new();
        internal readonly List<Entry> Entries = new();
        internal SerializedObject Serialized;
        internal bool FieldsOnly = true;
        internal int Queued;
        internal float RootHeight;
        internal string Trace => string.Join("\n", Entries.Select(x => x.Style + " " + x.Rect));
        internal Rect[] SourceRemoves => Entries.Where(x => x.Style == "button" && Mathf.Abs(x.Rect.width - 44) < .5f && Mathf.Abs(x.Rect.height - ResourceWorkbenchGui.FieldHeight) < .5f).Select(x => x.Rect).OrderBy(x => x.y).ToArray();
        internal Rect AddSource => Button(88, ResourceWorkbenchGui.FieldHeight);
        internal Rect ListRect => Entries.Where(x => x.Rect.x < 8 && x.Rect.width > 200 && x.Rect.height >= 100).Select(x => x.Rect).FirstOrDefault();
        private ResourceProject _project;
        private int _rule;
        private Vector2 _scroll;

        internal void Initialize(ResourceProject value, int index)
        {
            _project = value;
            _rule = index;
            Serialized = new SerializedObject(value.automaticAtlases);
            var rules = Serialized.FindProperty("rules");
            rules.isExpanded = true;
            for (var i = 0; i < rules.arraySize; i++)
            {
                rules.GetArrayElementAtIndex(i).isExpanded = i == 0;
            }

            rules.GetArrayElementAtIndex(0).FindPropertyRelative("sources").isExpanded = true;
        }

        internal Rect Button(float width, float height) => Entries.Where(x => x.Style == "button" && Mathf.Abs(x.Rect.width - width) < .5f && Mathf.Abs(x.Rect.height - height) < .5f).Select(x => x.Rect).FirstOrDefault();
        internal Rect SourceField(int index)
        {
            var remove = SourceRemoves[index];
            return Entries.Where(x => x.Rect.width > 100 && Mathf.Abs(x.Rect.y - remove.y) < .5f && Mathf.Abs(x.Rect.height - remove.height) < .5f && x.Rect.xMax <= remove.xMin).Select(x => x.Rect).Last();
        }

        private void OnGUI()
        {
            if (!_project || Serialized == null)
            {
                return;
            }

            if (FieldsOnly)
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_rule < 0)
                {
                    ResourceAtlasFields.Draw(Serialized);
                }
                else
                {
                    ResourceAtlasFields.Draw(Serialized, _rule, false);
                }

                EditorGUILayout.EndScrollView();
            }
            else
            {
                View.Draw(_project, position.width, () => { }, operation => operation(), operation =>
                {
                    Queued++;
                    operation();
                });
            }

            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var cache = typeof(GUILayoutUtility).GetProperty("current", Flags)?.GetValue(null) ?? typeof(GUILayoutUtility).GetField("current", Flags).GetValue(null);
            var top = cache.GetType().GetField("topLevel", Flags).GetValue(cache);
            RootHeight = ((Rect)top.GetType().GetField("rect", Flags).GetValue(top)).height;
            Entries.Clear();
            Walk(top, Vector2.zero);
        }

        private void Walk(object entry, Vector2 offset)
        {
            var type = entry.GetType();
            var rect = (Rect)type.GetField("rect", Flags).GetValue(entry);
            if ((bool?)type.GetField("resetCoords", Flags)?.GetValue(entry) == true)
            {
                offset += rect.position;
            }

            if (type.GetField("entries", Flags)?.GetValue(entry) is IEnumerable children)
            {
                foreach (var child in children)
                {
                    Walk(child, offset);
                }
            }
            else
            {
                Entries.Add(new Entry { Style = (type.GetProperty("style", Flags)?.GetValue(entry) as GUIStyle)?.name ?? "", Rect = new Rect(rect.position + offset, rect.size) });
            }
        }

        private void OnDisable()
        {
            Serialized?.Dispose();
            Serialized = null;
        }
    }
}

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
    public sealed class ResourceWorkbenchPolishTests
    {
        private const BindingFlags Flags = ResourceWorkbenchPolishEvidence.Flags;

        [UnityTest]
        public IEnumerator AdditionalOptionsExpandBelowTheirAnchorAndStayIndependentOfHelp()
        {
            var focus = EditorWindow.focusedWindow;
            foreach (var useRecipe in new[] { false, true })
            {
                using var fixture = new ResourceReleaseFixture();
                var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
                ResourceBuildRecipe recipe = null;
                string path = null;
                string saved = null;
                try
                {
                    window.ShowUtility();
                    window.position = new Rect(80, 60, 1080, 720);
                    window.SetProject(fixture.Project);
                    if (useRecipe)
                    {
                        recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
                        recipe.project = fixture.Project;
                        recipe.target = EditorUserBuildSettings.activeBuildTarget;
                        recipe.appContentCompatibilityId = "options-app";
                        recipe.action = ResourceBuildAction.BuildAndPublish;
                        recipe.forceRebuild = true;
                        path = fixture.Folder + "/OptionsRecipe.asset";
                        AssetDatabase.CreateAsset(recipe, path);
                        saved = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                        window.SetRecipe(recipe);
                    }
                    else
                    {
                        typeof(ResServiceEditorWindow).GetField("force", Flags).SetValue(window, true);
                    }

                    ResourceWorkbenchPolishEvidence.SetPage(window, 2);
                    for (var i = 0; i < 5; i++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                    Rect Anchor()
                    {
                        var marker = useRecipe
                            ? window.Entries.First(x => x.Style == "button" && x.Rect.width > 300 && x.Rect.y < 500).Rect
                            : window.Entries.First(x => x.Style.Equals("textfield", StringComparison.OrdinalIgnoreCase)).Rect;
                        return window.Entries.First(x => x.Style == "MiniPopup" && x.Rect.height == 26 && x.Rect.y >= marker.yMax).Rect;
                    }
                    var closed = Anchor();
                    ResourceWorkbenchUX2Tests.Click(window, closed.center);
                    for (var i = 0; i < 3; i++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                    var opened = Anchor();
                    Assert.That(opened.y, Is.EqualTo(closed.y).Within(.01f), "Expanding a section must not insert its controls above the disclosure anchor.\n" + window.layoutTrace);
                    var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", Flags).GetValue(window);
                    var build = view.GetType().GetField("_build", Flags).GetValue(view);
                    Assert.That((bool)build.GetType().GetField("_showAdvanced", Flags).GetValue(build), Is.True);
                    var help = window.Entries.Last(x => x.Style == "MiniPopup" && x.Rect.y > opened.yMax && x.Rect.y < 600).Rect;
                    Assert.That(window.Entries.Count(x => x.Style == "MiniPopup" && x.Rect.y >= opened.yMax && x.Rect.y < help.y), Is.GreaterThanOrEqualTo(2), "Editable options belong between their disclosure and the separate help section.");
                    ResourceWorkbenchUX2Tests.Click(window, help.center);
                    for (var i = 0; i < 3; i++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                    Assert.That((bool)build.GetType().GetField("_showAdvanced", Flags).GetValue(build), Is.True, "Reading help must not toggle editable options.");
                    Assert.That(Anchor().y, Is.EqualTo(closed.y).Within(.01f));
                    Assert.That(EditorUtility.IsDirty(fixture.Project), Is.False);
                    if (useRecipe)
                    {
                        Assert.That(recipe.forceRebuild, Is.True);
                        Assert.That(EditorUtility.IsDirty(recipe), Is.False);
                        Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(saved), "Disclosure changes must not save or rewrite the recipe.");
                    }
                    else
                    {
                        Assert.That((bool)typeof(ResServiceEditorWindow).GetField("force", Flags).GetValue(window), Is.True);
                    }

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
        }

        [Test]
        public void EmptyAppNamesTheVisibleFieldWithoutChangingTheFormalGate()
        {
            var type = typeof(ResServiceView).Assembly.GetType("YLFramework.Editor.Resource.ResourceBuildView");
            var describe = type.GetMethod("ReadinessSummary", Flags);
            Assert.That(describe, Is.Not.Null, "An empty App input must not be presented as a request to change the project's ProjectId.");
            using var fixture = new ResourceReleaseFixture();
            var readiness = ResourceEditorOperations.CheckReadiness(fixture.Project, "");
            var raw = readiness.Summary;
            var error = readiness.Error;
            Assert.That(readiness.State, Is.EqualTo(ResourcePreflightState.StaticBlocked));
            var text = (string)describe.Invoke(null, new object[] { readiness });
            Assert.That(text, Does.Contain("资源兼容标识"));
            Assert.That(text, Does.Contain("App"));
            Assert.That(text, Does.Not.Contain("Parameter name: projectId"));
            Assert.That(readiness.Summary, Is.EqualTo(raw));
            Assert.That(readiness.Error, Is.SameAs(error));
            var normal = ResourceEditorOperations.CheckReadiness(fixture.Project, "visible-field-app");
            Assert.That((string)describe.Invoke(null, new object[] { normal }), Is.EqualTo(normal.Summary));
        }

        [UnityTest]
        public IEnumerator TaskNavigationKeepsOneSelectedItemAndDoesNotActWhenDisabled()
        {
            Assert.That(typeof(ResourceWorkbenchGui).GetMethod("Navigation", Flags), Is.Not.Null, "Task navigation needs a separate selected-state presentation, not the action-button style.");
            var window = ScriptableObject.CreateInstance<ResourceNavigationProbe>();
            var focus = EditorWindow.focusedWindow;
            try
            {
                window.ShowUtility();
                window.position = new Rect(80, 60, 420, 150);
                for (var i = 0; i < 4; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                ResourceWorkbenchUX2Tests.Click(window, new Vector2(window.NavigationRect.x + window.NavigationRect.width / 2, window.NavigationRect.center.y));
                yield return null;
                Assert.That(window.Selected, Is.EqualTo(1));
                ResourceWorkbenchUX2Tests.Click(window, new Vector2(window.NavigationRect.x + window.NavigationRect.width / 2, window.NavigationRect.center.y));
                yield return null;
                Assert.That(window.Selected, Is.EqualTo(1), "A selected task is not an independently toggleable action.");
                window.ControlsEnabled = false;
                window.Repaint();
                yield return null;
                ResourceWorkbenchUX2Tests.Click(window, new Vector2(window.NavigationRect.x + window.NavigationRect.width * 5 / 6, window.NavigationRect.center.y));
                yield return null;
                Assert.That(window.Selected, Is.EqualTo(1));
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
        public IEnumerator SettingsShortcutSavesTheVisibleProjectNotTheBackgroundRecipeOrAtlas()
        {
            var focus = EditorWindow.focusedWindow;
            foreach (var background in new[] { 2, 0 })
            {
                using var fixture = new ResourceReleaseFixture();
                var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
                try
                {
                    UnityEngine.Object other;
                    if (background == 2)
                    {
                        var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
                        recipe.project = fixture.Project;
                        recipe.target = EditorUserBuildSettings.activeBuildTarget;
                        recipe.appContentCompatibilityId = "settings-app";
                        AssetDatabase.CreateAsset(recipe, fixture.Folder + "/OtherRecipe.asset");
                        other = recipe;
                    }
                    else
                    {
                        other = ResourceAtlasGenerator.CreateConfiguration(fixture.Project, fixture.Folder + "/Content", "PolishAtlases");
                    }

                    var path = AssetDatabase.GetAssetPath(other);
                    var before = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    window.ShowUtility();
                    window.position = new Rect(80, 60, 1080, 720);
                    window.SetProject(fixture.Project);
                    ResourceWorkbenchPolishEvidence.SetPage(window, background, background == 0 ? 2 : 0);
                    if (other is ResourceBuildRecipe selectedRecipe)
                    {
                        window.SetRecipe(selectedRecipe);
                        selectedRecipe.forceRebuild = true;
                    }
                    else
                    {
                        ((ResourceAtlasConfiguration)other).rules.Add(new ResourceAtlasRule { id = "unsaved-rule", groupId = fixture.Project.groups[0].id });
                    }

                    EditorUtility.SetDirty(other);
                    var layout = typeof(ResServiceEditorWindow).GetField("layout", Flags).GetValue(window);
                    layout.GetType().GetField("settings", Flags).SetValue(layout, true);
                    fixture.Project.displayName = "visible-project-settings";
                    EditorUtility.SetDirty(fixture.Project);
                    for (var i = 0; i < 4; i++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.S, control = true });
                    yield return null;
                    Assert.That(EditorUtility.IsDirty(fixture.Project), Is.False, "Ctrl+S in project settings must save the visible Project.");
                    Assert.That(EditorUtility.IsDirty(other), Is.True, "The hidden recipe/atlas must remain unsaved.");
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before));
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(fixture.Project))), Does.Contain("visible-project-settings"));
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
        }

        [UnityTest]
        public IEnumerator NarrowTaskDropdownAndResultSwitchUseTheirActualComfortableHeights()
        {
            using var fixture = new ResourceReleaseFixture();
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
            try
            {
                window.ShowUtility();
                window.position = new Rect(80, 60, 620, 440);
                window.SetProject(fixture.Project);
                ResourceWorkbenchPolishEvidence.SetPage(window, 2);
                for (var i = 0; i < 6; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var navigation = window.Entries.Single(x => x.Style == "MiniPopup" && x.Rect.y < 60);
                Assert.That(navigation.Rect.height, Is.GreaterThanOrEqualTo(24), "A taller style alone does not change EditorGUILayout.Popup's native 18-point layout.");
                Assert.That(window.Entries.Where(x => x.Style == "toolbarbutton" && x.Rect.y < 100).All(x => x.Rect.height >= 26), Is.True, window.layoutTrace);
                Assert.That(window.rootHeight, Is.LessThanOrEqualTo(441), window.layoutTrace);
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
        public void ResourceSelectionRestoresByExistingIdentityNotTemporaryRowNumber()
        {
            using var fixture = new ResourceReleaseFixture();
            var type = typeof(ResServiceView).Assembly.GetType("YLFramework.Editor.Resource.ResourceEntryTable");
            var capture = type.GetMethod("CaptureSelection", Flags);
            var restore = type.GetMethod("RestoreSelection", Flags);
            Assert.That(capture, Is.Not.Null, "Only existing Group/GUID/definition values may survive a reload, not TreeView integers.");
            Assert.That(restore, Is.Not.Null);
            var table = (UnityEditor.IMGUI.Controls.TreeView)Activator.CreateInstance(type, true);
            type.GetMethod("SetDefinitions", Flags).Invoke(table, new object[] { fixture.Project, 0 });
            table.SetSelection(new[] { table.GetRows()[0].id });
            var saved = capture.Invoke(table, new object[] { fixture.Project });
            fixture.Project.groups[0].selections.Insert(0, new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(fixture.Folder + "/Content"), address = "different-definition" });
            var rebuilt = (UnityEditor.IMGUI.Controls.TreeView)Activator.CreateInstance(type, true);
            type.GetMethod("SetDefinitions", Flags).Invoke(rebuilt, new object[] { fixture.Project, 0 });
            Assert.That((int)restore.Invoke(rebuilt, new[] { fixture.Project, saved }), Is.Zero);
            Assert.That((int[])type.GetMethod("GetSelectedIndices", Flags).Invoke(rebuilt, new object[] { fixture.Project, 0 }), Is.EqualTo(new[] { 1 }));
            fixture.Project.groups[0].selections[1].address = "meaning-changed";
            type.GetMethod("SetDefinitions", Flags).Invoke(rebuilt, new object[] { fixture.Project, 0 });
            Assert.That((int)restore.Invoke(rebuilt, new[] { fixture.Project, saved }), Is.EqualTo(1));
            Assert.That(rebuilt.GetSelection(), Is.Empty, "Changed definitions must not silently select a replacement row.");
        }

        [UnityTest]
        public IEnumerator CopyingTheCompleteConfirmationDoesNotAcceptOrCloseIt()
        {
            var type = typeof(ResServiceView).Assembly.GetType("YLFramework.Editor.Resource.ResourceOperationConfirmation");
            var window = (EditorWindow)ScriptableObject.CreateInstance(type);
            var focus = EditorWindow.focusedWindow;
            var clipboard = EditorGUIUtility.systemCopyBuffer;
            var text = string.Join("\n", Enumerable.Range(0, 80).Select(i => "Non-secret scope / exact-path-" + i));
            try
            {
                type.GetField("Message", Flags).SetValue(window, text);
                type.GetField("Positive", Flags).SetValue(window, "部署内置资源");
                window.ShowUtility();
                window.position = new Rect(100, 100, 720, 520);
                for (var i = 0; i < 5; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                EditorGUIUtility.systemCopyBuffer = "before";
                ResourceWorkbenchUX2Tests.Click(window, new Vector2(60, window.position.height - 20));
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(text), "The copy action must copy the original scope, never an abbreviated summary.");
                Assert.That((bool)type.GetField("IsAccepted", Flags).GetValue(window), Is.False);
                Assert.That(Resources.FindObjectsOfTypeAll(type).Contains(window), Is.True);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                yield return null;
                Assert.That(Resources.FindObjectsOfTypeAll(type).Contains(window), Is.False, "Escape is cancellation, even after copying.");
            }
            finally
            {
                if (window)
                {
                    window.Close();
                }

                EditorGUIUtility.systemCopyBuffer = clipboard;
                if (focus)
                {
                    focus.Focus();
                }
            }
        }

        [Test]
        public void ExpandedOutputClampsWithoutOverwritingTheUsersRequestedHeight()
        {
            var layout = typeof(ResServiceEditorWindow).GetField("layout", Flags).FieldType;
            Assert.That(layout.GetField("outputHeight", Flags), Is.Not.Null, "Output must retain a user drag independently of current window height.");
            var type = typeof(ResServiceView).Assembly.GetType("YLFramework.Editor.Resource.ResourceWorkbenchGui");
            var height = type.GetMethod("GetOutputHeight", Flags);
            Assert.That(height, Is.Not.Null, "Expanded output must reserve the task area rather than use a fixed 150-point pane.");
            Assert.That((float)height.Invoke(null, new object[] { 300f, 440f, true }), Is.InRange(48, 120));
            Assert.That((float)height.Invoke(null, new object[] { 300f, 900f, true }), Is.EqualTo(300));
            Assert.That((float)height.Invoke(null, new object[] { 0f, 720f, false }), Is.LessThan(90), "Empty first expansion must be compact.");
        }

        [UnityTest]
        public IEnumerator BuildInputsHaveRealComfortableHeightWithoutChangingTheRecipe()
        {
            using var fixture = new ResourceReleaseFixture();
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourcePolishGuiWindow>();
            try
            {
                window.ShowUtility();
                window.position = new Rect(100, 80, 1080, 720);
                window.SetProject(fixture.Project);
                ResourceWorkbenchPolishEvidence.SetPage(window, 2);
                for (var i = 0; i < 6; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var fields = window.Entries.Where(x => x.Style == "TextField" || x.Style == "textfield" || x.Style == "Resource.Field").ToArray();
                Assert.That(fields.Length, Is.GreaterThan(0), window.layoutTrace);
                Assert.That(fields.All(x => x.Rect.height >= 24), Is.True, "An expanded wrapper must not leave the visible text field at native 18 points.\n" + window.layoutTrace);
                Assert.That(window.rootHeight, Is.LessThanOrEqualTo(721), window.layoutTrace);
                Assert.That(fixture.Project.contentEncryption.ToString(), Is.EqualTo("None"));
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
    }

    internal sealed class ResourceNavigationProbe : EditorWindow
    {
        internal int Selected;
        internal bool ControlsEnabled = true;
        internal Rect NavigationRect;
        private void OnGUI()
        {
            using (new EditorGUI.DisabledScope(!ControlsEnabled))
            {
                Selected = (int)typeof(ResourceWorkbenchGui).GetMethod("Navigation", ResourceWorkbenchPolishEvidence.Flags).Invoke(null,
                    new object[] { Selected, new[] { "资源配置", "构建发布", "版本交付" }, true, Array.Empty<GUILayoutOption>() });
            }

            if (Event.current.type == EventType.Repaint)
            {
                NavigationRect = GUILayoutUtility.GetLastRect();
            }
        }
    }
}

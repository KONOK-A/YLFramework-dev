using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    public class ResourceWindowTests
    {
        [UnityTest]
        public IEnumerator HeaderBoundsLongProjectNamesAndKeepsActionsOnOneRightAlignedRow()
        {
            using var fixture = new ResourceReleaseFixture();
            var previousFocus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceGuiTestWindow>();
            try
            {
                fixture.Project.displayName = string.Concat(Enumerable.Repeat("长项目名称", 20));
                EditorUtility.SetDirty(fixture.Project);
                var before = JsonUtility.ToJson(fixture.Project);
                window.ShowUtility();
                window.SetProject(fixture.Project);
                foreach (var width in new[] { 620, 720, 1080, 1440 })
                {
                    window.position = new Rect(80, 60, width, 720);
                    window.Repaint();
                    yield return null;
                    window.Repaint();
                    yield return null;
                    Assert.That(window.ProjectButton.width, Is.EqualTo(320).Within(1), "A long title clamps to the readable maximum: " + width);
                    Assert.That(window.SaveProjectButton.width, Is.GreaterThan(0), "The compact save action must remain reachable: " + width);
                    Assert.That(window.MoreButton.width, Is.GreaterThan(0));
                    Assert.That(window.SaveProjectButton.center.y, Is.EqualTo(window.ProjectButton.center.y).Within(3), "Narrow windows must not add a redundant second header row.");
                    Assert.That(window.SaveProjectButton.xMin, Is.GreaterThan(window.ProjectButton.xMax));
                    Assert.That(window.MoreButton.xMax, Is.InRange(width - 12f, width + 1f));
                    Assert.That(window.ProjectPopupAnchor, Is.EqualTo(window.ProjectButton).Using(new RectComparer()), "The project picker must anchor at its actual button, not a stale layout rect.");
                    Assert.That(window.MoreMenuAnchor, Is.EqualTo(window.MoreButton).Using(new RectComparer()), "The more menu must anchor at the top-right button, not the window origin.");
                    Assert.That(window.MoreMenuAnchor.y, Is.LessThan(40), "The more menu anchor belongs to the header row.");
                    Assert.That(window.MoreMenuAnchor.xMin, Is.GreaterThan(width * .6f), "The more menu must open on the right, under its button.");
                    Assert.That(window.MorePopupAnchor.xMax, Is.EqualTo(window.MoreButton.xMax).Within(.1f), "The menu must open inward with its right edge aligned to the button.");
                    Assert.That(window.MorePopupAnchor.xMin, Is.GreaterThanOrEqualTo(6));
                    Assert.That(window.MorePopupAnchor.xMax, Is.LessThanOrEqualTo(width - 5));
                    if (width >= 960)
                    {
                        Assert.That(window.IdentityLabel.y, Is.EqualTo(window.ProjectButton.y).Within(.1f), "ProjectId must not sit below the project name.");
                        var nameContent = ResourceWorkbenchGui.Project.padding.Remove(window.ProjectButton);
                        var idContent = ResourceWorkbenchGui.Identity.padding.Remove(window.IdentityLabel);
                        Assert.That(idContent.center.y + ResourceWorkbenchGui.Identity.contentOffset.y,
                            Is.EqualTo(nameContent.center.y + ResourceWorkbenchGui.Project.contentOffset.y).Within(.1f), "Both labels must share the same text baseline geometry.");
                    }
                }
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before), "Header layout must not mutate the project.");
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.True);
                fixture.Project.displayName = "Short";
                window.Repaint();
                yield return null;
                window.Repaint();
                yield return null;
                Assert.That(window.ProjectPopupAnchor.width, Is.LessThan(240), "A short title sizes to its content so ProjectId sits next to it.");
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
        public IEnumerator MorePopupFitsTheNarrowWorkbenchAndCopiesWithoutEditingProject()
        {
            using var fixture = new ResourceReleaseFixture();
            var previousFocus = EditorWindow.focusedWindow;
            var previousClipboard = EditorGUIUtility.systemCopyBuffer;
            var originalPopups = Resources.FindObjectsOfTypeAll<PopupWindow>().Select(p => p.GetInstanceID()).ToArray();
            var window = ScriptableObject.CreateInstance<ResourceGuiTestWindow>();
            PopupWindow popup = null;
            try
            {
                window.ShowUtility();
                window.position = new Rect(100, 80, 620, 440);
                window.Focus();
                window.SetProject(fixture.Project);
                var before = JsonUtility.ToJson(fixture.Project);
                for (var frame = 0; frame < 20 && window.MoreButton.width == 0; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.MoreButton.width, Is.GreaterThan(0));
                window.Focus();
                Click(window, window.MoreButton.center);
                for (var frame = 0; frame < 10 && !popup; frame++)
                {
                    popup = Resources.FindObjectsOfTypeAll<PopupWindow>().FirstOrDefault(p => !originalPopups.Contains(p.GetInstanceID()));
                    yield return null;
                }
                Assert.That(popup, Is.Not.Null, "Clicking More must open the actual local popup.");
                Assert.That(popup.position.xMin, Is.GreaterThanOrEqualTo(window.position.x - 4));
                Assert.That(popup.position.xMax, Is.LessThanOrEqualTo(window.position.xMax + 4), "The actual popup must fit inside the narrow workbench, not extend to the desktop on its right.");
                Click(popup, new Vector2(100, 48));
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(fixture.Project.projectId));
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (popup)
                {
                    popup.Close();
                }

                window.Close();
                EditorGUIUtility.systemCopyBuffer = previousClipboard;
                if (previousFocus)
                {
                    previousFocus.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator SettingsRootFieldCopiesTheCompleteHierarchyWithoutEditingTheProject()
        {
            using var fixture = new ResourceReleaseFixture();
            var previous = EditorWindow.focusedWindow;
            var previousClipboard = EditorGUIUtility.systemCopyBuffer;
            var window = ScriptableObject.CreateInstance<ResourceGuiTestWindow>();
            try
            {
                window.position = new Rect(100, 80, 720, 480);
                window.ShowUtility();
                window.SetProject(fixture.Project);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var layout = typeof(ResServiceEditorWindow).GetField("layout", flags).GetValue(window);
                layout.GetType().GetField("settings").SetValue(layout, true);
                var before = JsonUtility.ToJson(fixture.Project);
                for (var frame = 0; frame < 20 && window.RootCopyButton.width == 0; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.RootCopyButton.width, Is.GreaterThan(0));
                Click(window, window.RootCopyButton.center);
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(fixture.Folder + "/Content"));
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                window.Close();
                EditorGUIUtility.systemCopyBuffer = previousClipboard;
                if (previous)
                {
                    previous.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator ExternalAssetDropReachesTheLiveNativeTableBeforeItsDefaultDragHandler()
        {
            using var fixture = new ResourceReleaseFixture();
            var path = fixture.Folder + "/Content/live-table-drop.txt";
            Assert.That(AssetDatabase.CopyAsset(fixture.TextPath, path), Is.True);
            var previous = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceGuiTestWindow>();
            try
            {
                window.position = new Rect(100, 80, 1080, 720);
                window.ShowUtility();
                window.SetProject(fixture.Project);
                yield return null;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", flags).GetValue(window);
                var definition = typeof(ResServiceView).GetField("_definition", flags).GetValue(view);
                var table = (TreeView)definition.GetType().GetField("_table", flags).GetValue(definition);
                var rectangle = (Rect)typeof(TreeView).GetProperty("treeViewRect", flags).GetValue(table);
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new[] { AssetDatabase.LoadMainAssetAtPath(path) };
                DragAndDrop.paths = new[] { path };
                window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = rectangle.center });
                window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = rectangle.center });
                yield return null;
                Assert.That(fixture.Project.groups[0].selections.Count, Is.EqualTo(2), "The real TreeView must not consume an external drop before the definition handler sees it.");
                Assert.That(fixture.Project.groups[0].selections.Last().guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
            }
            finally
            {
                DragAndDrop.objectReferences = Array.Empty<Object>();
                DragAndDrop.paths = Array.Empty<string>();
                Undo.ClearUndo(fixture.Project);
                window.Close();
                if (previous)
                {
                    previous.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator ActualWindowGuiSelectRefreshProjectSwitchAndCloseCancelQueuedOperations()
        {
            var previousSelection = Selection.objects;
            var previousFocus = EditorWindow.focusedWindow;
            using var fixture = new ResourceReleaseFixture();
            ResourceGuiTestWindow window = null;
            try
            {
                var path = fixture.TextPath;
                fixture.Project.groups.RemoveRange(1, fixture.Project.groups.Count - 1);
                window = ScriptableObject.CreateInstance<ResourceGuiTestWindow>();
                window.ShowUtility();
                // ShowUtility会恢复该窗口类型上次的位置，窄窗尺寸必须在显示后设置。
                window.position = new Rect(100, 50, 760, 950);
                window.Focus();
                window.SetProject(fixture.Project);
                Assert.That(window.position.width, Is.EqualTo(760).Within(1), "The narrow-window test must actually run at its declared width.");
                var view = (ResServiceView)typeof(ResServiceEditorWindow).GetField("_resServiceView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                yield return null;
                Assert.Greater(window.DefinitionNavigation.width, 0, "Resource/preview/atlas navigation must remain discoverable.");
                Click(window, new Vector2(window.DefinitionNavigation.center.x, window.DefinitionNavigation.center.y));
                yield return null;
                for (var frame = 0; frame < 20 && window.RefreshButton.width == 0; frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.Greater(window.RefreshButton.width, 0, "Actual GUI refresh button was not laid out.");
                Click(window, window.RefreshButton.center);
                yield return null;
                yield return null;
                var definition = typeof(ResServiceView).GetField("_definition", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                var table = (UnityEditor.IMGUI.Controls.TreeView)definition.GetType().GetField("_table", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(definition);
                var tableRect = (Rect)typeof(UnityEditor.IMGUI.Controls.TreeView).GetProperty("treeViewRect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(table);
                Assert.That(table.GetRows().Count, Is.GreaterThan(0), "The explicit preview must produce its original resource rows.");
                Click(window, new Vector2(tableRect.x + 45, tableRect.y + 12));
                yield return null;
                Assert.Greater(window.DetailButton.width, 0, "The narrow layout must expose its detail/list switch.");
                Click(window, window.DetailButton.center);
                yield return null;
                for (var frame = 0; frame < 20 && (window.Repaints == 0 || window.SelectButton.width == 0); frame++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.Greater(window.Layouts, 0);
                Assert.Greater(window.Repaints, 0);
                var layout = typeof(ResServiceEditorWindow).GetField("layout", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                Assert.Greater(window.SelectButton.width, 0, "Actual GUI selection button was not laid out. Detail=" + window.DetailButton +
                    " Select=" + window.SelectButton + " Repaints=" + window.Repaints + " Compact=" + layout.GetType().GetField("compactDetails").GetValue(layout) +
                    " Window=" + window.position);
                Selection.objects = Array.Empty<Object>();
                Click(window, window.SelectButton.center);
                yield return null;
                Assert.AreEqual(path, AssetDatabase.GetAssetPath(Selection.activeObject), "Button=" + window.SelectButton + "; status=" +
                    typeof(ResServiceView).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view));
                Assert.IsEmpty(AssetImporter.GetAtPath(path).assetBundleName);

                var calls = 0;
                typeof(ResServiceView).GetMethod("QueueOperation", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(view, new object[] { new Func<string>(() =>
                    {
                    calls++;
                    return "owned callback";
                    }) });
                var pending = (EditorApplication.CallbackFunction)typeof(ResServiceView).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                Assert.IsTrue(EditorApplication.update.GetInvocationList().Contains(pending));
                window.SetProject(null);
                Assert.IsFalse(EditorApplication.update?.GetInvocationList().Contains(pending) ?? false, "Project switching cancels the old queued action.");
                typeof(ResServiceView).GetMethod("QueueOperation", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(view, new object[] { new Func<string>(() =>
                    {
                    calls++;
                    return "owned callback";
                    }) });
                pending = (EditorApplication.CallbackFunction)typeof(ResServiceView).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                Assert.AreEqual(1, Selection.selectionChanged.GetInvocationList().Count(callback => callback.Target == window));
                window.Close();
                Assert.IsFalse(EditorApplication.update?.GetInvocationList().Contains(pending) ?? false);
                yield return null;
                Assert.AreEqual(0, calls);
                Assert.IsFalse(Selection.selectionChanged.GetInvocationList().Any(callback => callback.Target == window));
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

        private static void Click(EditorWindow window, Vector2 position)
        {
            window.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = position });
            window.SendEvent(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = position });
        }
    }

    internal sealed class RectComparer : IEqualityComparer<Rect>
    {
        public bool Equals(Rect a, Rect b) => Mathf.Abs(a.x - b.x) < 1 && Mathf.Abs(a.y - b.y) < 1 && Mathf.Abs(a.width - b.width) < 1 && Mathf.Abs(a.height - b.height) < 1;
        public int GetHashCode(Rect value) => value.GetHashCode();
    }

    internal class ResourceGuiTestWindow : ResServiceEditorWindow
    {
        internal int Layouts;
        internal int Repaints;
        internal Rect SelectButton;
        internal Rect RefreshButton;
        internal Rect DefinitionNavigation;
        internal Rect DetailButton;
        internal Rect RootCopyButton;
        internal Rect ProjectButton;
        internal Rect SaveProjectButton;
        internal Rect MoreButton;
        internal Rect ProjectPopupAnchor;
        internal Rect MoreMenuAnchor;
        internal Rect IdentityLabel;
        internal Rect MorePopupAnchor;

        private void OnGUI()
        {
            var type = Event.current.type;
            typeof(ResServiceEditorWindow).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, null);
            if (type == EventType.Layout)
            {
                Layouts++;
            }

            if (type != EventType.Repaint)
            {
                return;
            }

            Repaints++;
            SelectButton = default;
            RefreshButton = default;
            DetailButton = default;
            RootCopyButton = default;
            DefinitionNavigation = default;
            ProjectButton = default;
            SaveProjectButton = default;
            MoreButton = default;
            IdentityLabel = default;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var cache = typeof(GUILayoutUtility).GetProperty("current", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) ??
                        typeof(GUILayoutUtility).GetField("current", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
            Assert.IsNotNull(cache, "Unity layout cache is unavailable.");
            var group = cache.GetType().GetField("topLevel", flags).GetValue(cache);
            Read(group, Vector2.zero);
            Read(cache.GetType().GetField("windows", flags).GetValue(cache), Vector2.zero);
            var anchorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            ProjectPopupAnchor = (Rect)typeof(ResServiceEditorWindow).GetField("_projectPopupAnchor", anchorFlags).GetValue(this);
            MoreMenuAnchor = (Rect)typeof(ResServiceEditorWindow).GetField("_moreMenuAnchor", anchorFlags).GetValue(this);
            MorePopupAnchor = (Rect)typeof(ResServiceEditorWindow).GetField("_morePopupAnchor", anchorFlags).GetValue(this);
            void Read(object entry, Vector2 offset)
            {
                var entryType = entry.GetType();
                var rectangle = (Rect)entryType.GetField("rect", flags).GetValue(entry);
                if ((bool?)(entryType.GetField("resetCoords", flags)?.GetValue(entry)) == true)
                {
                    offset += rectangle.position;
                }

                var absolute = new Rect(rectangle.position + offset, rectangle.size);
                // 只接受本测试窗口内的控件；用户同时打开的工作台布局缓存不应参与命中。
                var inside = absolute.xMin >= -1 && absolute.yMin >= -1 && absolute.xMax <= position.width + 2 && absolute.yMax <= position.height + 2;
                var style = inside ? (GUIStyle)entryType.GetProperty("style", flags)?.GetValue(entry) : null;
                if (ReferenceEquals(style, ResourceWorkbenchGui.Project))
                {
                    ProjectButton = absolute;
                }

                if (ReferenceEquals(style, ResourceWorkbenchGui.Identity))
                {
                    IdentityLabel = absolute;
                }

                if (ReferenceEquals(style, ResourceWorkbenchGui.NavigationStyle) && Mathf.Abs(rectangle.width - 248) < 1)
                {
                    DefinitionNavigation = absolute;
                }

                if (absolute.y < 40 && style != null && style.name.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0 && Mathf.Abs(rectangle.width - 58) < 1)
                {
                    SaveProjectButton = absolute;
                }

                if (absolute.y < 40 && ReferenceEquals(style, ResourceWorkbenchGui.Menu) && Mathf.Abs(rectangle.width - 54) < 1)
                {
                    MoreButton = absolute;
                }

                if (style != null && style.name.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (Mathf.Abs(rectangle.width - 50) < 1)
                    {
                        SelectButton = absolute;
                    }

                    if (Mathf.Abs(rectangle.width - 74) < 1 && RefreshButton.width == 0)
                    {
                        RefreshButton = absolute;
                    }

                    if (Mathf.Abs(rectangle.width - 70) < 1)
                    {
                        DetailButton = absolute;
                    }

                    if (style.name == "button" && Mathf.Abs(rectangle.width - 44) < 1 && RootCopyButton.width == 0)
                    {
                        RootCopyButton = absolute;
                    }
                }
                var children = entryType.GetField("entries", flags)?.GetValue(entry) as IEnumerable;
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        Read(child, offset);
                    }
                }
            }
        }
    }
}

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

namespace YLFramework.Tests.Help
{
    using Editor = YLFramework.Editor.Help;

    public sealed class HelpWindowTests
    {
        private const string First = "__helpkit_test_first";
        private const string Second = "__helpkit_test_second";

        [TearDown]
        public void RemoveTestProviders()
        {
            Editor.HelpRegistry.Unregister(First);
            Editor.HelpRegistry.Unregister(Second);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyWindowStateRetainsSelectedPagesWidthsAndBooleanDefaults(bool initialized)
        {
            var window = ScriptableObject.CreateInstance<Editor.HelpWindow>();
            string folder = "Assets/YLFramework/Tests/EditMode/Editor/Help/LegacyWindow-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Help", Path.GetFileName(folder)), Is.Not.Empty);
            try
            {
                var names = new[]
                {
                    ("mModuleId", "moduleId", "legacy-module"), ("mSearch", "search", "legacy-query"),
                    ("mGuideId", "guideId", "legacy-guide"), ("mApiId", "apiId", "legacy-api"),
                    ("mAboutId", "aboutId", "legacy-about"), ("mMemberSearch", "memberSearch", "legacy-member"),
                    ("mApiCategory", "apiCategory", "legacy-category")
                };
                var serialized = new SerializedObject(window);
                foreach (var entry in names)
                {
                    serialized.FindProperty(entry.Item2).stringValue = entry.Item3;
                }

                serialized.FindProperty("tab").intValue = 2;
                serialized.FindProperty("sidebarWidth").floatValue = 271;
                serialized.FindProperty("hasInitializedGroups").boolValue = initialized;
                serialized.FindProperty("collapsedGroups").arraySize = 1;
                serialized.FindProperty("collapsedGroups").GetArrayElementAtIndex(0).stringValue = "guide:legacy";
                serialized.ApplyModifiedPropertiesWithoutUndo();
                string path = folder + "/Window.asset";
                window.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(window, path);
                EditorUtility.SetDirty(window);
                AssetDatabase.SaveAssetIfDirty(window);
                string yaml = File.ReadAllText(path);
                var aliases = names.Select(entry => (entry.Item1, entry.Item2)).Concat(new[]
                {
                    ("mTab", "tab"), ("mSidebarWidth", "sidebarWidth"),
                    ("mCollapsedGroups", "collapsedGroups"), ("mInitialGroupsSet", "hasInitializedGroups")
                });
                foreach (var alias in aliases)
                {
                    yaml = System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^  " + alias.Item2 + @":", "  " + alias.Item1 + ":");
                    Assert.That(System.Text.RegularExpressions.Regex.Matches(yaml, @"(?m)^  " + alias.Item1 + @":").Count, Is.EqualTo(1));
                }

                File.WriteAllText(path, yaml, new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                serialized = new SerializedObject(AssetDatabase.LoadAssetAtPath<Editor.HelpWindow>(path));
                foreach (var entry in names)
                {
                    Assert.That(serialized.FindProperty(entry.Item2).stringValue, Is.EqualTo(entry.Item3));
                }
                Assert.That(serialized.FindProperty("tab").intValue, Is.EqualTo(2));
                Assert.That(serialized.FindProperty("sidebarWidth").floatValue, Is.EqualTo(271));
                Assert.That(serialized.FindProperty("hasInitializedGroups").boolValue, Is.EqualTo(initialized));
                Assert.That(serialized.FindProperty("collapsedGroups").arraySize, Is.EqualTo(1));
                Assert.That(serialized.FindProperty("collapsedGroups").GetArrayElementAtIndex(0).stringValue, Is.EqualTo("guide:legacy"));
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
                if (window)
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }
            }
        }

        [TestCase(0f)]
        [TestCase(0.57f)]
        public void LegacySplitWindowRatioRetainsExplicitValuesAcrossReimport(float ratio)
        {
            var window = ScriptableObject.CreateInstance<YLFramework.Editor.Common.SplitWindow>();
            string folder = "Assets/YLFramework/Tests/EditMode/Editor/Help/LegacySplit-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Help", Path.GetFileName(folder)), Is.Not.Empty);
            try
            {
                Assert.That(window.HorizontalSplitPercent, Is.EqualTo(0.3f));
                var serialized = new SerializedObject(window);
                var property = serialized.FindProperty("horizontalSplitPercent");
                Assert.That(property, Is.Not.Null, "The persistent ratio must use the current field name.");
                property.floatValue = ratio;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                string path = folder + "/Window.asset";
                window.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(window, path);
                EditorUtility.SetDirty(window);
                AssetDatabase.SaveAssetIfDirty(window);
                string yaml = File.ReadAllText(path);
                yaml = System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^  horizontalSplitPercent:", "  horizontal_split_percent:");
                Assert.That(yaml, Does.Contain("  horizontal_split_percent:"));
                File.WriteAllText(path, yaml, new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var restored = AssetDatabase.LoadAssetAtPath<YLFramework.Editor.Common.SplitWindow>(path);
                Assert.That(restored.HorizontalSplitPercent, Is.EqualTo(ratio));
                EditorUtility.SetDirty(restored);
                AssetDatabase.SaveAssetIfDirty(restored);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.LoadAssetAtPath<YLFramework.Editor.Common.SplitWindow>(path).HorizontalSplitPercent, Is.EqualTo(ratio));
                Assert.That(File.ReadAllText(path), Does.Not.Contain("  horizontal_split_percent:"));
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
                if (window)
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }
            }
        }

        [Test]
        public void LegacyCommonTreeStateSurvivesNamespaceMigrationAndReimport()
        {
            const string Source = "Assets/YLFramework/Tests/Fixtures/NamespaceMigration/EditorCommonStateBefore.asset";
            byte[] original = File.ReadAllBytes(Source);
            string folder = "Assets/YLFramework/Tests/EditMode/Editor/Help/LegacyCommon-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Help", Path.GetFileName(folder)), Is.Not.Empty);
            try
            {
                Assert.That(File.ReadAllText(Source), Does.Contain("ns: YLFramework.Editor.Utilities"));
                string path = folder + "/State.asset";
                Assert.That(AssetDatabase.CopyAsset(Source, path), Is.True);
                for (int pass = 0; pass < 2; pass++)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    var state = AssetDatabase.LoadAssetAtPath<EditorCommonStateFixture>(path);
                    Assert.That(state, Is.Not.Null);
                    Assert.That(state.group, Is.Not.Null);
                    Assert.That(state.info, Is.Not.Null);
                    Assert.That(state.group.Get(42).selectedIDs, Is.EqualTo(new[] { 73 }));
                    Assert.That(state.group.Get(42).expandedIDs, Is.EqualTo(new[] { 99 }));
                    Assert.That(state.info.id, Is.EqualTo(17));
                    Assert.That(state.info.treeViewState, Is.Not.Null);
                    EditorUtility.SetDirty(state);
                    AssetDatabase.SaveAssetIfDirty(state);
                }
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
                Assert.That(File.ReadAllBytes(Source), Is.EqualTo(original));
            }
        }

        [Test]
        public void LegacySplitWindowAssetSurvivesNamespaceMigrationAndReimport()
        {
            const string Source = "Assets/YLFramework/Tests/Fixtures/NamespaceMigration/SplitWindowBefore.asset";
            byte[] original = File.ReadAllBytes(Source);
            string folder = "Assets/YLFramework/Tests/EditMode/Editor/Help/LegacySplitAsset-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Tests/EditMode/Editor/Help", Path.GetFileName(folder)), Is.Not.Empty);
            YLFramework.Editor.Common.SplitWindow window = null;
            try
            {
                string path = folder + "/Window.asset";
                Assert.That(AssetDatabase.CopyAsset(Source, path), Is.True);
                for (int pass = 0; pass < 2; pass++)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    window = AssetDatabase.LoadAssetAtPath<YLFramework.Editor.Common.SplitWindow>(path);
                    Assert.That(window, Is.Not.Null);
                    Assert.That(window.HorizontalSplitPercent, Is.EqualTo(0.57f));
                    EditorUtility.SetDirty(window);
                    AssetDatabase.SaveAssetIfDirty(window);
                }
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
                if (window)
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }
                Assert.That(File.ReadAllBytes(Source), Is.EqualTo(original));
            }
        }

        [Test]
        public void RegistryIsExplicitLazyAndReplacesDuplicateIds()
        {
            var count = Editor.HelpRegistry.GetModules().Length;
            var created = 0;
            Editor.HelpRegistry.Register(First, "第一个模块", () =>
            {
                created++;
                return new Editor.HelpModule();
            });
            Editor.HelpRegistry.Register(First, "新的名称", () =>
            {
                created += 10;
                return new Editor.HelpModule();
            });
            Assert.That(created, Is.Zero);
            var modules = Editor.HelpRegistry.GetModules();
            Assert.That(modules.Length, Is.EqualTo(count + 1));
            Assert.That(modules.Single(module => module.Id == First).DisplayName, Is.EqualTo("新的名称"));
            modules.Single(module => module.Id == First).Create();
            Assert.That(created, Is.EqualTo(10));
            Assert.That(Editor.HelpRegistry.Unregister(First), Is.True);
            Assert.That(Editor.HelpRegistry.Unregister(First), Is.False);
        }

        [Test]
        public void WindowSwitchDoesNotReuseSearchOrSameIdPagesFromAnotherModule()
        {
            Editor.HelpRegistry.Register(First, "模块甲", () => PageModule("甲模块的等待"));
            Editor.HelpRegistry.Register(Second, "模块乙", () => PageModule("乙模块的播放"));
            var window = ScriptableObject.CreateInstance<Editor.HelpWindow>();
            try
            {
                window.SelectModule(First);
                Set(window, "search", "等待");
                UpdateResults(window);
                Assert.That(Visible(window).Single().Title, Is.EqualTo("甲模块的等待"));
                Set(window, "tab", 1);
                window.SelectModule(Second);
                Assert.That(Get<string>(window, "search"), Is.Empty);
                Assert.That(Get<int>(window, "tab"), Is.Zero);
                UpdateResults(window);
                Assert.That(Visible(window).Single().Title, Is.EqualTo("乙模块的播放"));
                Set(window, "search", "等待");
                UpdateResults(window);
                Assert.That(Visible(window), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void RemovingAllProvidersLeavesACompleteEmptyWindow()
        {
            var saved = Editor.HelpRegistry.GetModules();
            Editor.HelpWindow window = null;
            try
            {
                foreach (var module in saved)
                {
                    Editor.HelpRegistry.Unregister(module.Id);
                }

                Editor.HelpRegistry.Register(First, "临时模块", () => PageModule("临时指南"));
                window = ScriptableObject.CreateInstance<Editor.HelpWindow>();
                window.SelectModule(First);
                UpdateResults(window);
                Assert.That(Visible(window).Length, Is.EqualTo(1));
                Editor.HelpRegistry.Unregister(First);
                Invoke(window, "LoadModule");
                UpdateResults(window);
                Assert.That(Get<Editor.HelpModule>(window, "_module"), Is.Null);
                Assert.That(Visible(window), Is.Empty);
                Assert.That(Get<Editor.HelpApiType[]>(window, "_index"), Is.Empty);
            }
            finally
            {
                if (window)
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }

                Editor.HelpRegistry.Unregister(First);
                foreach (var module in saved)
                {
                    Editor.HelpRegistry.Register(module.Id, module.DisplayName, module.Create);
                }
            }
        }

        [Test]
        public void CommonWindowHasOneMenuAndBelongsToTheEditorMainAssembly()
        {
            var type = typeof(Editor.HelpWindow);
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo("YLFramework.Editor"));
            var menu = type.GetMethod("OpenMenu", BindingFlags.NonPublic | BindingFlags.Static).GetCustomAttribute<MenuItem>();
            Assert.That(menu.menuItem, Is.EqualTo("YLFramework/帮助"));
            var references = type.Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();
            Assert.That(references.All(name => new[] { "mscorlib", "netstandard", "System", "System.Core", "System.Xml", "System.Xml.Linq",
                "Newtonsoft.Json", "YLFramework.Runtime" }.Contains(name) || name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                name.StartsWith("UnityEditor.", StringComparison.Ordinal)), Is.True, string.Join(", ", references));
        }

        [Test]
        public void HelpBoundaryAndPersistedWindowIdentityArePreserved()
        {
            const string AssemblyPath = "Assets/YLFramework/Editor/Help/YLFramework.Editor.Help.asmdef";
            Assert.That(File.Exists(AssemblyPath), Is.False);
            Assert.That(File.Exists(AssemblyPath + ".meta"), Is.False);
            Assert.That(AssetDatabase.AssetPathToGUID("Assets/YLFramework/Editor/YLFramework.Editor.asmdef"),
                Is.EqualTo("62b3fd14a5c704743835cacd6a6870a1"));
            Assert.That(AssetDatabase.AssetPathToGUID("Assets/YLFramework/Editor/Help/HelpWindow.cs"),
                Is.EqualTo("e42278efa6283704aa34a807cacdfff0"));
            var definition = JsonUtility.FromJson<HelpAssemblyDefinition>(File.ReadAllText("Assets/YLFramework/Editor/YLFramework.Editor.asmdef"));
            CollectionAssert.AreEquivalent(new[] { "YLFramework.Runtime", "UnityEngine.UI" }, definition.references);
            CollectionAssert.AreEqual(new[] { "Editor" }, definition.includePlatforms);
            Assert.That(definition.overrideReferences, Is.True);
            CollectionAssert.AreEqual(new[] { "Newtonsoft.Json.dll" }, definition.precompiledReferences);
            Assert.That(typeof(HelpWindowTests).Assembly.GetName().Name, Is.EqualTo("YLFramework.Tests.EditMode"));
            var moved = typeof(Editor.HelpWindow).GetCustomAttributesData()
                .Single(attribute => attribute.AttributeType == typeof(UnityEngine.Scripting.APIUpdating.MovedFromAttribute));
            CollectionAssert.AreEqual(new object[] { true, "Project.HelpKit.Editor", "Project.HelpKit.Editor", "HelpWindow" },
                moved.ConstructorArguments.Select(argument => argument.Value).ToArray());
        }

        [Serializable]
        private sealed class HelpAssemblyDefinition
        {
            public string[] references;
            public string[] includePlatforms;
            public bool overrideReferences;
            public string[] precompiledReferences;
        }

        [TestCase(132, 440)]
        [TestCase(500, 440)]
        [TestCase(500, 1200)]
        public void SidebarKeepsReaderSpace(float requested, float width)
        {
            var sidebar = Editor.HelpWindow.ClampSidebar(requested, width);
            Assert.That(sidebar, Is.GreaterThanOrEqualTo(132));
            Assert.That(width - sidebar, Is.GreaterThanOrEqualTo(240));
        }

        [TestCase(440)]
        [TestCase(800)]
        [TestCase(1200)]
        public void HeaderHasTallNonOverlappingControlsAndEqualTabs(float width)
        {
            var layout = new Editor.HelpHeaderLayout(width);
            Assert.That(layout.Module.height, Is.EqualTo(32));
            Assert.That(layout.Tabs.height, Is.EqualTo(34));
            Assert.That(layout.Search.height, Is.EqualTo(30));
            Assert.That(layout.Title.xMax, Is.LessThan(layout.Module.xMin));
            Assert.That(layout.Module.xMax, Is.LessThan(layout.Back.xMin));
            Assert.That(layout.Search.xMax, Is.LessThan(layout.Clear.xMin));
            Assert.That(layout.Module.yMax, Is.LessThan(layout.Tabs.yMin));
            Assert.That(layout.Tabs.yMax, Is.LessThan(layout.Search.yMin));
            Assert.That(layout.Search.yMax, Is.LessThan(layout.Scope.yMin));
            Assert.That(layout.BodyY, Is.EqualTo(layout.Scope.yMax + 10));
            Assert.That(layout.BodyY, Is.InRange(166, 170));
            for (var tab = 0; tab < 3; tab++)
            {
                Assert.That(layout.Tab(tab).width, Is.EqualTo(layout.Tabs.width / 3).Within(0.01f));
                Assert.That(layout.Tab(tab).height, Is.EqualTo(34));
                Assert.That(layout.Tab(tab).xMax, Is.LessThanOrEqualTo(layout.Tabs.xMax + 0.01f));
                if (tab != 0)
                {
                    Assert.That(layout.Tab(tab).xMin, Is.EqualTo(layout.Tab(tab - 1).xMax).Within(0.01f));
                }
            }
        }

        [Test]
        public void HighlightInvalidationIncludesSameQueryRefreshAndModuleSwitch()
        {
            Editor.HelpRegistry.Register(First, "模块甲", () => PageModule("等待甲"));
            Editor.HelpRegistry.Register(Second, "模块乙", () => PageModule("等待乙"));
            var window = ScriptableObject.CreateInstance<Editor.HelpWindow>();
            try
            {
                window.SelectModule(First);
                Set(window, "search", "等待");
                UpdateResults(window);
                var highlight = Get<Editor.HelpSearchHighlight>(window, "_highlights");
                var ranges = highlight.GetRanges("等待原文");
                Assert.That(ranges, Is.Not.Empty);
                Set(window, "_filterKey", null);
                UpdateResults(window);
                Assert.That(highlight.GetRanges("等待原文"), Is.Not.SameAs(ranges));
                Set(window, "search", " \t ");
                UpdateResults(window);
                Assert.That(highlight.GetRanges("等待原文"), Is.Empty);
                Set(window, "search", "等待");
                UpdateResults(window);
                window.SelectModule(Second);
                Assert.That(highlight.GetRanges("等待原文"), Is.Empty);
                Assert.That(window.minSize, Is.EqualTo(new Vector2(440, 360)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [UnityTest]
        public IEnumerator ReadingStylesHaveSeparateHierarchyAndRebuildWithoutChangingSharedStyles()
        {
            var window = ScriptableObject.CreateInstance<Editor.HelpWindow>();
            var probe = ScriptableObject.CreateInstance<RenderingProbe>();
            var previous = EditorWindow.focusedWindow;
            try
            {
                probe.Draw = () =>
                {
                    var shared = new[] { EditorStyles.label, EditorStyles.foldout, EditorStyles.popup,
                        EditorStyles.toolbarButton, EditorStyles.toolbarSearchField, EditorStyles.helpBox };
                    var heights = shared.Select(style => style.fixedHeight).ToArray();
                    var fonts = shared.Select(style => style.fontSize).ToArray();
                    Set(window, "_body", new GUIStyle(EditorStyles.label));
                    Set(window, "_isDarkTheme", EditorGUIUtility.isProSkin);
                    Invoke(window, "EnsureStyles");
                    var fields = new[] { "_title", "_headerTitle", "_heading", "_subheading", "_body", "_small", "_code", "_signature",
                        "_navGroup", "_apiGroup", "_detailFoldout", "_popup", "_tabButton", "_searchField", "_button", "_notice", "_link" };
                    var styles = fields.Select(field => Get<GUIStyle>(window, field)).ToArray();
                    Assert.That(styles.Take(8).Select(style => style.fontSize), Is.EqualTo(new[] { 24, 20, 18, 15, 14, 12, 13, 13 }));
                    Assert.That(Get<GUIStyle>(window, "_navGroup").fontSize, Is.EqualTo(13));
                    Assert.That(Get<GUIStyle>(window, "_apiGroup").fontSize, Is.EqualTo(18));
                    Assert.That(Get<GUIStyle>(window, "_detailFoldout").fontSize, Is.EqualTo(15));
                    Assert.That(styles.All(style => !style.richText), Is.True);
                    // Toolbar resolves skin variants by style name; keep every tab on our copy.
                    var tabStyle = Get<GUIStyle>(window, "_tabButton");
                    foreach (var suffix in new[] { "left", "mid", "right" })
                    {
                        Assert.That(GUI.skin.FindStyle(tabStyle.name + suffix), Is.Null);
                    }

                    foreach (var field in new[] { "_popup", "_tabButton", "_searchField", "_button" })
                    {
                        Assert.That(Get<GUIStyle>(window, field).fixedHeight, Is.Zero);
                    }

                    Set(window, "_isDarkTheme", !EditorGUIUtility.isProSkin);
                    Invoke(window, "EnsureStyles");
                    for (var i = 0; i < fields.Length; i++)
                    {
                        Assert.That(Get<GUIStyle>(window, fields[i]), Is.Not.SameAs(styles[i]));
                    }

                    Assert.That(shared.Select(style => style.fixedHeight), Is.EqualTo(heights));
                    Assert.That(shared.Select(style => style.fontSize), Is.EqualTo(fonts));
                };
                probe.ShowUtility();
                probe.Repaint();
                for (var frame = 0; frame < 30 && !probe.Completed; frame++)
                {
                    yield return null;
                }

                Assert.That(probe.Completed, Is.True);
                if (probe.Failure != null)
                {
                    throw probe.Failure;
                }
            }
            finally
            {
                probe.Close();
                UnityEngine.Object.DestroyImmediate(window);
                if (previous)
                {
                    previous.Focus();
                }
            }
        }

        [TestCase("屏幕与屏幕", "屏幕", 0, 2, 3, 5)]
        [TestCase("屏幕屏幕", "屏幕", 0, 4)]
        [TestCase("屏幕", "屏幕 Delay", 0, 2)]
        [TestCase("屏幕 Delay", "屏幕\t\r\nDelay", 0, 2, 3, 8)]
        [TestCase("Delay delay DELAY", "delay DELAY", 0, 5, 6, 11, 12, 17)]
        [TestCase("banana", "ana nan", 1, 6)]
        [TestCase("Task<T> <color> a+b", "Task<T> <color> a+b", 0, 7, 8, 15, 16, 19)]
        [TestCase("屏幕 Delay", "不存在")]
        [TestCase("屏幕 Delay", " \t\r\n")]
        public void HighlightRangesUseLiteralCaseInsensitiveOverlappingTerms(string text, string query, params int[] expected)
        {
            var highlight = new Editor.HelpSearchHighlight();
            var original = text;
            highlight.SetQuery(query);
            Assert.That(highlight.GetRanges(text).SelectMany(range => new[] { range.Start, range.End }), Is.EqualTo(expected));
            Assert.That(text, Is.EqualTo(original));
            highlight.SetQuery("");
            Assert.That(highlight.GetRanges(text), Is.Empty);
        }

        [UnityTest]
        public IEnumerator HighlightGeometryUsesActualFontAcrossWrappedAndExplicitLines()
        {
            var probe = ScriptableObject.CreateInstance<RenderingProbe>();
            var previous = EditorWindow.focusedWindow;
            try
            {
                probe.Draw = () =>
                {
                    var style = new GUIStyle(EditorStyles.label)
                    {
                        fontSize = 14,
                        wordWrap = true,
                        richText = false,
                        alignment = TextAnchor.UpperLeft,
                        padding = new RectOffset(0, 0, 3, 4)
                    };
                    foreach (var text in new[] { "屏幕屏幕屏幕屏幕屏幕", "Delay\r\n屏幕 Delay", "Delay\n屏幕 Delay", "A\r\nB\r\nC", "A\U0001F600B", "Task<T> a+b <color>" })
                    {
#if UNITY_6000_2_OR_NEWER
                        // 明确列出预期元素索引，不复用被测产品的转换算法。
                        var cursorIndices = text switch
                        {
                            "Delay\r\n屏幕 Delay" => new[] { 0, 1, 2, 3, 4, 5, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 },
                            "A\r\nB\r\nC" => new[] { 0, 1, 1, 2, 3, 3, 4, 5 },
                            "A\U0001F600B" => new[] { 0, 1, 1, 2, 3 },
                            _ => null
                        };
                        int CursorIndex(int sourceIndex) => cursorIndices == null ? sourceIndex : cursorIndices[sourceIndex];
#else
                        int CursorIndex(int sourceIndex) => sourceIndex;
#endif
                        foreach (var width in new[] { 32f, 71f, 240f })
                        {
                            var rect = new Rect(12, 12, width, style.CalcHeight(new GUIContent(text), width));
                            var fragments = Editor.HelpSearchHighlight.BuildFragments(text, style, rect,
                                new[] { new Editor.HelpSearchHighlight.Range(0, text.Length) });
                            Assert.That(fragments, Is.Not.Empty);
                            foreach (var fragment in fragments)
                            {
                                Assert.That(fragment.xMin, Is.GreaterThanOrEqualTo(rect.xMin));
                                Assert.That(fragment.xMax, Is.LessThanOrEqualTo(rect.xMax));
                                Assert.That(fragment.yMin, Is.GreaterThanOrEqualTo(rect.yMin));
                                Assert.That(fragment.yMax, Is.LessThanOrEqualTo(rect.yMax));
                                Assert.That(fragment.height, Is.LessThanOrEqualTo(style.lineHeight + 0.01f));
                            }
                            var content = new GUIContent(text);
                            for (var i = 0; i < text.Length; i++)
                            {
                                if (char.IsWhiteSpace(text[i]) || (i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])))
                                {
                                    continue;
                                }

                                var cursor = style.GetCursorPixelPosition(rect, content, CursorIndex(i));
                                var covered = fragments.Any(fragment => Mathf.Abs(fragment.y - cursor.y) < 0.5f &&
                                    cursor.x >= fragment.xMin - 0.5f && cursor.x < fragment.xMax);
                                if (!covered)
                                {
                                    var unwrapped = new GUIStyle(style) { wordWrap = false };
                                    var first = Math.Max(0, i - 1);
                                    var carets = Enumerable.Range(first, Math.Min(text.Length, i + 1) - first + 1)
                                        .Select(index => $"{index}->{CursorIndex(index)}:wrapped={style.GetCursorPixelPosition(rect, content, CursorIndex(index)):F3},unwrapped={unwrapped.GetCursorPixelPosition(rect, content, CursorIndex(index)):F3}");
                                    Assert.Fail($"Missing glyph {i} at {width}; text={text.Replace("\r", "\\r").Replace("\n", "\\n")}; rect={rect:F3}; lineHeight={style.lineHeight}; font={style.font}; clipping={style.clipping}; fixed={style.fixedWidth}/{style.fixedHeight}; dpi={EditorGUIUtility.pixelsPerPoint}\n" +
                                        string.Join("; ", carets) + "\nfragments=" + string.Join("; ", fragments.Select(fragment => fragment.ToString("F3"))));
                                }
                            }

                            var singleRanges = text switch
                            {
                                "Delay\r\n屏幕 Delay" => new[] { new Editor.HelpSearchHighlight.Range(7, 8), new Editor.HelpSearchHighlight.Range(14, 15) },
                                "A\r\nB\r\nC" => new[] { new Editor.HelpSearchHighlight.Range(3, 4), new Editor.HelpSearchHighlight.Range(6, 7) },
                                "A\U0001F600B" => new[] { new Editor.HelpSearchHighlight.Range(3, 4) },
                                _ => Array.Empty<Editor.HelpSearchHighlight.Range>()
                            };
                            foreach (var range in singleRanges)
                            {
                                var single = Editor.HelpSearchHighlight.BuildFragments(text, style, rect, new[] { range });
                                Assert.That(single, Has.Length.EqualTo(1), $"Single match [{range.Start},{range.End}) at {width}");
                                var start = style.GetCursorPixelPosition(rect, content, CursorIndex(range.Start));
                                var end = style.GetCursorPixelPosition(rect, content, CursorIndex(range.End));
                                Assert.That(single[0].width, Is.GreaterThan(0));
                                Assert.That(single[0].xMin, Is.EqualTo(start.x).Within(0.5f));
                                Assert.That(single[0].yMin, Is.EqualTo(start.y).Within(0.5f));
                                Assert.That(single[0].xMax, Is.EqualTo(end.x).Within(0.5f));
                            }

                            var lineBreak = text.IndexOf("\r\n", StringComparison.Ordinal);
                            if (lineBreak >= 0)
                            {
                                Assert.That(Editor.HelpSearchHighlight.BuildFragments(text, style, rect,
                                    new[] { new Editor.HelpSearchHighlight.Range(lineBreak, lineBreak + 2) }), Is.Empty);
                            }
                        }
                    }

                    var highlight = new Editor.HelpSearchHighlight();
                    highlight.SetQuery("屏幕");
                    var size = new Vector2(90, 200);
                    var cached = highlight.GetFragments("屏幕屏幕屏幕", style, size);
                    Assert.That(highlight.GetFragments("屏幕屏幕屏幕", style, size), Is.SameAs(cached));
                    var translated = Editor.HelpSearchHighlight.BuildFragments("屏幕屏幕屏幕", style,
                        new Rect(new Vector2(47, 83), size), highlight.GetRanges("屏幕屏幕屏幕"));
                    Assert.That(translated.Length, Is.EqualTo(cached.Length));
                    for (var i = 0; i < cached.Length; i++)
                    {
                        Assert.That(translated[i].x - cached[i].x, Is.EqualTo(47).Within(0.01f));
                        Assert.That(translated[i].y - cached[i].y, Is.EqualTo(83).Within(0.01f));
                        Assert.That(translated[i].width, Is.EqualTo(cached[i].width).Within(0.01f));
                        Assert.That(translated[i].height, Is.EqualTo(cached[i].height).Within(0.01f));
                    }
                    var boundaryText = "屏幕屏幕屏幕";
                    var boundaryRect = new Rect(0, 0, 32, 200);
                    for (var i = 0; i < boundaryText.Length - 1; i++)
                    {
                        var a = style.GetCursorPixelPosition(boundaryRect, new GUIContent(boundaryText), i);
                        var b = style.GetCursorPixelPosition(boundaryRect, new GUIContent(boundaryText), i + 1);
                        if (Mathf.Abs(a.y - b.y) < 0.5f)
                        {
                            continue;
                        }

                        var fragment = Editor.HelpSearchHighlight.BuildFragments(boundaryText, style, boundaryRect,
                            new[] { new Editor.HelpSearchHighlight.Range(i, i + 1) }).Single();
                        var glyphWidth = style.CalcSize(new GUIContent(boundaryText.Substring(i, 1))).x - style.padding.horizontal;
                        Assert.That(fragment.xMax, Is.EqualTo(a.x + glyphWidth).Within(0.5f));
                    }
                    Assert.That(highlight.GetFragments("屏幕屏幕屏幕", style, new Vector2(32, 200)), Is.Not.SameAs(cached));
                    highlight.ClearGeometry();
                    Assert.That(highlight.GetFragments("屏幕屏幕屏幕", style, size), Is.Not.SameAs(cached));
                    highlight.Clear();
                    Assert.That(highlight.GetFragments("屏幕屏幕屏幕", style, size), Is.Empty);
                };
                probe.ShowUtility();
                probe.Repaint();
                for (var frame = 0; frame < 30 && !probe.Completed; frame++)
                {
                    yield return null;
                }

                Assert.That(probe.Completed, Is.True, "No IMGUI Repaint received");
                if (probe.Failure != null)
                {
                    throw probe.Failure;
                }
            }
            finally
            {
                probe.Close();
                if (previous)
                {
                    previous.Focus();
                }
            }
        }

        private sealed class RenderingProbe : EditorWindow
        {
            internal Action Draw;
            internal bool Once = true;
            internal bool Completed;
            internal int Repaints;
            internal Exception Failure;
            private void OnGUI()
            {
                if (Once && (Completed || Event.current.type != EventType.Repaint))
                {
                    return;
                }

                try
                {
                    Draw();
                }
                catch (ExitGUIException)
                {
                    Completed = true;
                    throw;
                }
                catch (Exception exception)
                {
                    Failure = exception;
                }
                if (Event.current.type == EventType.Repaint)
                {
                    Repaints++;
                }

                Completed = true;
            }
        }

        [UnityTest]
        public IEnumerator HighlightKeepsNativeSelectionAndScrolledCodeCopyUnmodified()
        {
            var probe = ScriptableObject.CreateInstance<RenderingProbe>();
            var previous = EditorWindow.focusedWindow;
            var clipboard = EditorGUIUtility.systemCopyBuffer;
            var text = "Task<T> Delay 屏幕 <color> a+b";
            var code = new string(' ', 18) + text + "\n" + text;
            var first = Vector2.zero;
            var last = Vector2.zero;
            try
            {
                probe.Once = false;
                probe.Draw = () =>
                {
                    var style = new GUIStyle(EditorStyles.label)
                    {
                        fontSize = 14,
                        richText = false,
                        wordWrap = true,
                        alignment = TextAnchor.UpperLeft,
                        padding = new RectOffset(0, 0, 0, 0)
                    };
                    var highlight = new Editor.HelpSearchHighlight();
                    highlight.SetQuery("Task<T> 屏幕 <color> a+b");
                    var rect = new Rect(10, 10, 290, 90);
                    first = style.GetCursorPixelPosition(rect, new GUIContent(text), 0) + new Vector2(1, style.lineHeight * 0.5f);
                    last = style.GetCursorPixelPosition(rect, new GUIContent(text), text.Length) + new Vector2(2, style.lineHeight * 0.5f);
                    highlight.Draw(rect, text, style, EditorGUIUtility.isProSkin);
                    EditorGUI.SelectableLabel(rect, text, style);
                    GUI.BeginScrollView(new Rect(10, 110, 290, 80), new Vector2(65, 5), new Rect(0, 0, 800, 160));
                    style.wordWrap = false;
                    var codeRect = new Rect(8, 5, 780, 140);
                    highlight.Draw(codeRect, code, style, EditorGUIUtility.isProSkin);
                    EditorGUI.SelectableLabel(codeRect, code, style);
                    GUI.EndScrollView();
                };
                probe.position = new Rect(100, 100, 330, 220);
                probe.ShowUtility();
                probe.Focus();
                probe.Repaint();
                for (var frame = 0; frame < 30 && probe.Repaints == 0; frame++)
                {
                    yield return null;
                }

                Assert.That(probe.Repaints, Is.GreaterThan(0));
                if (probe.Failure != null)
                {
                    throw probe.Failure;
                }

                EditorGUIUtility.systemCopyBuffer = "__helpkit_copy_sentinel";
                probe.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = first });
                probe.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = last });
                probe.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = last });
                probe.SendEvent(EditorGUIUtility.CommandEvent("Copy"));
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(text));
                probe.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = new Vector2(80, 132) });
                probe.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = new Vector2(80, 132) });
                probe.SendEvent(EditorGUIUtility.CommandEvent("SelectAll"));
                probe.SendEvent(EditorGUIUtility.CommandEvent("Copy"));
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(code));
                if (probe.Failure != null)
                {
                    throw probe.Failure;
                }
            }
            finally
            {
                probe.Close();
                EditorGUIUtility.systemCopyBuffer = clipboard;
                if (previous)
                {
                    previous.Focus();
                }
            }
        }

        [UnityTest]
        public IEnumerator ActualReaderKeepsRawBlocksHistoryAndBothCodeScrollAxes()
        {
            var type = typeof(Fixtures.ApiFixture<>);
            var notice = string.Concat(Enumerable.Repeat("等待提示 Delay 屏幕。", 12));
            var code = string.Join("\r\n", Enumerable.Repeat("Task<T> Delay 屏幕 <color> a+b " + new string('x', 180), 35));
            var documentation = new Editor.HelpDocumentation("等待 Delay Run 摘要", "Run 返回原文", "Run 备注原文",
                new[] { new Editor.HelpParameter("value", "Run 参数原文") }, new[] { new Editor.HelpParameter("T", "Run 类型参数原文") });
            Editor.HelpRegistry.Register(First, "窗口集成验证", () => new Editor.HelpModule
            {
                Guides = new[]
                {
                    new Editor.HelpPage("shared", "等待甲", "测试", "等待摘要 Delay", new[]
                    {
                        Editor.HelpBlock.Notice("等待提示", notice), Editor.HelpBlock.Code("Delay 代码", "原文只读", code),
                        Editor.HelpBlock.Link("等待链接 Delay", "next")
                    }) { RelatedTypes = new[] { type } },
                    new Editor.HelpPage("next", "等待乙", "测试", "等待摘要 Delay", new[] { Editor.HelpBlock.Paragraph("", "等待正文 Delay") })
                },
                ApiTypes = new[] { new Editor.HelpApiTypeDefinition(type, "测试 API") },
                Documentation = _ => documentation
            });
            var window = ScriptableObject.CreateInstance<Editor.HelpWindow>();
            var previous = EditorWindow.focusedWindow;
            try
            {
                window.SelectModule(First);
                Set(window, "search", "等待 Delay");
                Set(window, "_filterKey", null);
                window.position = new Rect(100, 100, 800, 700);
                window.ShowUtility();
                window.Repaint();
                var highlight = Get<Editor.HelpSearchHighlight>(window, "_highlights");
                var ranges = Get<Dictionary<string, Editor.HelpSearchHighlight.Range[]>>(highlight, "_ranges");
                for (var frame = 0; frame < 30 && !ranges.ContainsKey(code); frame++)
                {
                    yield return null;
                }

                Assert.That(ranges.ContainsKey(notice), Is.True, "Notice did not use the original text highlighter");
                Assert.That(ranges.ContainsKey(code), Is.True, "Code did not use the original text highlighter");
                Assert.That(ranges.ContainsKey("等待链接 Delay"), Is.True);
                var originalRanges = ranges[notice];
                var collapsed = Get<List<string>>(window, "collapsedGroups");
                var searching = Get<HashSet<string>>(window, "_searchCollapsed");
                var details = Get<HashSet<string>>(window, "_detailExpanded");
                collapsed.Add("saved-normal");
                searching.Add("saved-search");
                details.Add("saved-detail");
                yield return RunGuiAction(() => typeof(Editor.HelpWindow).GetMethod("Navigate", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(window, new object[] { 0, "next", false }));
                Assert.That(Get<string>(window, "guideId"), Is.EqualTo("next"));
                Assert.That(highlight.GetRanges(notice), Is.Not.SameAs(originalRanges));
                collapsed.Remove("saved-normal");
                searching.Remove("saved-search");
                Click(window, new Editor.HelpHeaderLayout(window.position.width).Back.center);
                Assert.That(Get<string>(window, "guideId"), Is.EqualTo("shared"));
                Assert.That(Get<List<string>>(window, "collapsedGroups").Contains("saved-normal"), Is.True);
                Assert.That(searching.Contains("saved-search"), Is.True);
                Assert.That(details.Contains("saved-detail"), Is.True);
                var scrolling = Get<Dictionary<string, Vector2>>(window, "_codeScroll");
                foreach (var width in new[] { 440f, 800f, 1200f })
                {
                    var geometry = Get<IDictionary>(highlight, "_geometry");
                    highlight.ClearGeometry();
                    window.position = new Rect(100, 100, width, 700);
                    scrolling["shared:1"] = new Vector2(10000, 10000);
                    window.Repaint();
                    for (var frame = 0; frame < 30 && geometry.Count == 0; frame++)
                    {
                        yield return null;
                    }

                    Assert.That(geometry.Count, Is.GreaterThan(0), "Reader received no highlighted Repaint");
                    Assert.That(scrolling["shared:1"].x, Is.GreaterThan(0), "Code must still scroll horizontally");
                    Assert.That(scrolling["shared:1"].y, Is.GreaterThan(0), "Code must still scroll vertically");
                    Assert.That(Get<float>(window, "_readingWidth"), Is.LessThan(width - 100));
                }
                Click(window, new Editor.HelpHeaderLayout(window.position.width).Tab(1).center);
                Assert.That(Get<int>(window, "tab"), Is.EqualTo(1));
                Set(window, "search", "Run");
                Set(window, "_filterKey", null);
                var api = Get<Editor.HelpApiType[]>(window, "_index")[0];
                var entry = api.Entries.First(member => member.Name == "Run");
                details.Add(api.Id + "\n" + entry.Signature);
                window.Repaint();
                for (var frame = 0; frame < 30 && !ranges.ContainsKey(documentation.Returns); frame++)
                {
                    yield return null;
                }

                Assert.That(ranges.ContainsKey(api.Declaration), Is.True);
                Assert.That(ranges.ContainsKey(entry.Signature), Is.True);
                Assert.That(ranges.ContainsKey("Run 参数原文"), Is.True);
                Assert.That(ranges.ContainsKey("Run 类型参数原文"), Is.True);
                Assert.That(ranges.ContainsKey(documentation.Returns), Is.True);
                Assert.That(ranges.ContainsKey(documentation.Remarks), Is.True);
                Assert.That(code, Does.Contain("<color>").And.Contain("\r\n"));
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

        private static void Click(EditorWindow window, Vector2 position)
        {
            window.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = position });
            window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = position });
        }

        private static IEnumerator RunGuiAction(Action action)
        {
            var probe = ScriptableObject.CreateInstance<RenderingProbe>();
            try
            {
                probe.Draw = () =>
                {
                    try
                    {
                        action();
                    }
                    catch (TargetInvocationException exception) when (exception.InnerException is ExitGUIException)
                    {
                        throw exception.InnerException;
                    }
                };
                probe.ShowUtility();
                probe.Repaint();
                for (var frame = 0; frame < 30 && !probe.Completed; frame++)
                {
                    yield return null;
                }

                Assert.That(probe.Completed, Is.True);
                if (probe.Failure != null)
                {
                    throw probe.Failure;
                }
            }
            finally
            {
                probe.Close();
            }
        }

        private static Editor.HelpModule PageModule(string title) => new Editor.HelpModule
        {
            Guides = new[] { new Editor.HelpPage("shared", title, "入门", "中文使用说明",
                Editor.HelpBlock.Paragraph("使用", "同一页面标识只属于当前模块。")) }
        };

        private static FieldInfo Field(string name) => typeof(Editor.HelpWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        private static T Get<T>(object instance, string name) => (T)instance.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        private static void Set(Editor.HelpWindow window, string name, object value) => Field(name).SetValue(window, value);
        private static void Invoke(Editor.HelpWindow window, string name) => typeof(Editor.HelpWindow)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
        private static void UpdateResults(Editor.HelpWindow window) => Invoke(window, "UpdateResults");
        private static Editor.HelpPage[] Visible(Editor.HelpWindow window) => Get<Editor.HelpPage[]>(window, "_visibleTopics");
    }
}

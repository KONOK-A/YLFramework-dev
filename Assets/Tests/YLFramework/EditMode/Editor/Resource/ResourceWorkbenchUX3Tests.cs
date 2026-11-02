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
using YLFramework.Resource;
using YLFramework.Tests.ResourceBuild;

namespace YLFramework.Tests.ResourceEditor
{
    public sealed class ResourceWorkbenchUX3Tests
    {
        private static readonly BindingFlags _internal = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        public void OlderAttemptAlwaysMarksExactAnalysisConservativelyStale()
        {
            var view = new ResourceBuildView();
            var older = new ResourceEditorBuildReport { ProjectGuid = "owned-guid", BuildError = new InvalidOperationException("old") };
            var latest = new ResourceEditorBuildReport { ProjectGuid = "owned-guid", BuildError = new InvalidOperationException("latest") };
            foreach (var report in new[] { older, latest })
            {
                typeof(ResourceBuildView).GetMethod("RecordReport", _internal).Invoke(view, new object[] { report, new Action<ResourceBuildResult>(_ => { }) });
            }

            typeof(ResourceBuildView).GetField("_report", _internal).SetValue(view, older);
            view.HasDefinitionChanged = false;
            var stale = typeof(ResourceBuildView).GetProperty("IsExactReportStale", _internal);
            Assert.That(stale, Is.Not.Null);
            Assert.That(stale.GetValue(view), Is.True);
        }

        [Test]
        public void SharedAppChangesInvalidateOnlyReadinessAndFormalPreflightNotTheMap()
        {
            using var fixture = new ResourceReleaseFixture();
            var view = new ResourceBuildView();
            var map = ResourceBuildMap.Create(fixture.Project);
            view.SetPreview(map, fixture.Project);
            var observe = typeof(ResourceBuildView).GetMethod("TrackWindowApp", _internal);
            Assert.That(observe, Is.Not.Null);
            observe.Invoke(view, new object[] { "bad/app" });
            typeof(ResourceBuildView).GetField("_readiness", _internal).SetValue(view, ResourceEditorOperations.CheckReadiness(fixture.Project, "bad/app"));
            observe.Invoke(view, new object[] { "valid-app" });
            Assert.That(typeof(ResourceBuildView).GetField("_readiness", _internal).GetValue(view), Is.Null);
            Assert.That(typeof(ResourceBuildView).GetField("_preview", _internal).GetValue(view), Is.SameAs(map));
        }

        [UnityTest]
        public IEnumerator PendingOperationAlsoGuardsTheBottomHistorySelection()
        {
            using var fixture = new ResourceReleaseFixture();
            var window = ScriptableObject.CreateInstance<ResourceUX2GuiWindow>();
            var focus = EditorWindow.focusedWindow;
            try
            {
                window.ShowUtility();
                window.position = new Rect(100, 80, 1080, 720);
                window.SetProject(fixture.Project);
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", _internal).GetValue(window);
                view.GetType().GetField("_tab", _internal).SetValue(view, 2);
                var build = (ResourceBuildView)view.GetType().GetField("_build", _internal).GetValue(view);
                var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fixture.Project));
                ResourceEditorBuildReport Small(string value) => new ResourceEditorBuildReport { ProjectGuid = guid, BuildError = new InvalidOperationException(value) };
                var older = Small("older bounded-history fixture");
                var current = Small("current bounded-history fixture");
                var record = typeof(ResourceBuildView).GetMethod("RecordReport", _internal);
                foreach (var report in new[] { older, current })
                {
                    record.Invoke(build, new object[] { report, new Action<ResourceBuildResult>(_ => { }) });
                }

                typeof(ResourceBuildView).GetField("_areAttemptsExpanded", _internal).SetValue(build, true);
                var layout = typeof(ResServiceEditorWindow).GetField("layout", _internal).GetValue(window);
                layout.GetType().GetField("output").SetValue(layout, true);
                view.GetType().GetField("_pending", _internal).SetValue(view, new EditorApplication.CallbackFunction(() => { }));
                for (var i = 0; i < 8; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.HistoryButtons.Count, Is.EqualTo(2), window.LayoutTrace);
                ResourceWorkbenchUX2Tests.Click(window, window.HistoryButtons[1].center);
                Assert.That(build.CurrentReport, Is.SameAs(current), "Output history is another input path; it must not change a queued operation's displayed target.");
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
        public void FailedAttemptBriefNamesTheFailureInsteadOfOnlyTheRecipePath()
        {
            using var fixture = new ResourceReleaseFixture();
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = fixture.Project;
            recipe.target = EditorUserBuildSettings.activeBuildTarget;
            recipe.appContentCompatibilityId = "ux3-brief-app";
            AssetDatabase.CreateAsset(recipe, fixture.Folder + "/BriefRecipe.asset");
            var view = new ResServiceView();
            try
            {
                view.SetProject(fixture.Project);
                var build = (ResourceBuildView)typeof(ResServiceView).GetField("_build", _internal).GetValue(view);
                var failure = new InvalidOperationException("owned-brief-failure");
                var report = new ResourceEditorBuildReport
                {
                    Invocation = ResourceEditorOperations.ResolveRecipe(recipe),
                    BuildError = failure,
                    ProjectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fixture.Project))
                };
                Func<string> action = () => (string)typeof(ResourceBuildView).GetMethod("RecordReport", _internal).Invoke(build,
                    new object[] { report, new Action<ResourceBuildResult>(_ => { }) });
                typeof(ResServiceView).GetMethod("PerformOperation", _internal).Invoke(view,
                    new object[] { action, report.ProjectGuid, fixture.Project.displayName, 0 });
                Assert.That((string)typeof(ResServiceView).GetField("_statusBrief", _internal).GetValue(view), Does.Contain("构建未完成").And.Not.Contain("BriefRecipe.asset"));
                Assert.That((string)typeof(ResServiceView).GetField("_status", _internal).GetValue(view), Does.Contain("Recipe=").And.Contain("owned-brief-failure"), "Full output must retain frozen recipe metadata as well as the failure.");
                Assert.That(report.BuildError, Is.SameAs(failure));
            }
            finally
            {
                view.OnDispose();
            }
        }

        [UnityTest]
        public IEnumerator LongOperationConfirmationIsScrollableAndEscapeDoesNotAccept()
        {
            var type = typeof(ResourceEditorOperations).Assembly.GetType("YLFramework.Editor.Resource.ResourceOperationConfirmation");
            Assert.That(type, Is.Not.Null, "Native DisplayDialog truncates full ownership/range text; a complete read-only confirmation must remain inspectable.");
            var window = (EditorWindow)ScriptableObject.CreateInstance(type);
            var focus = EditorWindow.focusedWindow;
            try
            {
                var message = "OWNED Project/GUID/App\n" + string.Join("\n", Enumerable.Range(0, 300).Select(i => "owned-write-scope-" + i + " / owner / lock / journal / stage / payload~ / meta / import"));
                type.GetField("Message", _internal).SetValue(window, message);
                type.GetField("Positive", _internal).SetValue(window, "确认自有范围");
                window.ShowUtility();
                window.position = new Rect(100, 80, 720, 520);
                window.Focus();
                for (var i = 0; i < 6; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(type.GetField("Message", _internal).GetValue(window), Is.EqualTo(message));
                window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = new Vector2(350, 250), delta = new Vector2(0, 15) });
                yield return null;
                Assert.That(((Vector2)type.GetField("_scroll", _internal).GetValue(window)).y, Is.GreaterThan(0), "Long scope text must be reachable by actual scrolling, not truncated at 1024 characters.");
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                Assert.That((bool)type.GetField("IsAccepted", _internal).GetValue(window), Is.False);
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

        [Test]
        public void ARejectedRecipeStillReportsItsKnownOriginalProjectAndRequestedOptions()
        {
            using var fixture = new ResourceReleaseFixture();
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = fixture.Project;
            recipe.target = EditorUserBuildSettings.activeBuildTarget;
            recipe.appContentCompatibilityId = "ux3-rejected-app";
            AssetDatabase.CreateAsset(recipe, fixture.Folder + "/RejectedRecipe.asset");
            recipe.forceRebuild = true;
            EditorUtility.SetDirty(recipe);
            var result = ResourceEditorOperations.ExecuteRecipe(recipe);
            Assert.That(result.BuildError, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.ProjectGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fixture.Project))));
            Assert.That(result.ProjectId, Is.EqualTo(fixture.Project.projectId));
            Assert.That(result.AppContentCompatibilityId, Is.EqualTo("ux3-rejected-app"));
            Assert.That(result.ForceRebuild, Is.True);
            Assert.That(result.Build, Is.Null);
        }

        [Test]
        public void RecalculatingAnEstimateCannotRevalidateAnEarlierExactBuildReport()
        {
            using var fixture = new ResourceReleaseFixture();
            var view = new ResourceBuildView();
            view.HasDefinitionChanged = true;
            view.SetPreview(ResourceBuildMap.Create(fixture.Project), fixture.Project);
            Assert.That(view.HasDefinitionChanged, Is.True, "A new estimate has not revalidated a previous Workspace or native report.");
        }

        [UnityTest]
        public IEnumerator InlineRecipeSaveAndShortcutKeepTheActualAssetAndDoNotSaveTheProject()
        {
            using var fixture = new ResourceReleaseFixture();
            var method = typeof(ResServiceEditorWindow).GetMethod("SetRecipe");
            Assert.That(method, Is.Not.Null, "The same real Recipe reference must be retained by the window across reload, not copied into Project or a temporary UI object.");
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = fixture.Project;
            recipe.appContentCompatibilityId = "ux3-gui-app";
            recipe.target = EditorUserBuildSettings.activeBuildTarget;
            AssetDatabase.CreateAsset(recipe, fixture.Folder + "/GuiRecipe.asset");
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceUX2GuiWindow>();
            try
            {
                window.ShowUtility();
                window.position = new Rect(100, 80, 1080, 720);
                window.SetProject(fixture.Project);
                method.Invoke(window, new object[] { recipe });
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", _internal).GetValue(window);
                view.GetType().GetField("_tab", _internal).SetValue(view, 2);
                fixture.Project.displayName = "Project must stay unsaved";
                EditorUtility.SetDirty(fixture.Project);
                recipe.forceRebuild = true;
                EditorUtility.SetDirty(recipe);
                for (var i = 0; i < 8; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.SaveRecipe.width, Is.EqualTo(96));
                window.Focus();
                ResourceWorkbenchUX2Tests.Click(window, window.SaveRecipe.center);
                yield return null;
                Assert.That(EditorUtility.IsDirty(recipe), Is.False);
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.True);
                Assert.That(recipe.action, Is.EqualTo(ResourceBuildAction.BuildOnly), "Displaying/saving the primary action must not temporarily change Recipe.Action.");
                recipe.appContentCompatibilityId = "saved-by-shortcut";
                EditorUtility.SetDirty(recipe);
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.S, modifiers = EventModifiers.Control });
                Assert.That(EditorUtility.IsDirty(recipe), Is.False);
                Assert.That(EditorUtility.IsDirty(fixture.Project), Is.True);
                window.OnDisable();
                typeof(ResServiceEditorWindow).GetMethod("OnEnable", _internal).Invoke(window, null);
                var saved = typeof(ResServiceEditorWindow).GetField("selectedRecipe", _internal).GetValue(window);
                Assert.That(saved, Is.SameAs(recipe));
                Assert.That(recipe.appContentCompatibilityId, Is.EqualTo("saved-by-shortcut"));
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
        public void StaticReadinessNeverAcquiresKeysAndFullPreflightPreservesTheOriginalError()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "ux3-missing";
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            var inspect = typeof(ResourceEditorOperations).GetMethod("InspectPreflight", new[] { typeof(ResourceProject), typeof(string), typeof(IResourceContentKeyProvider) });
            var ready = typeof(ResourceEditorOperations).GetMethod("CheckReadiness", new[] { typeof(ResourceProject), typeof(string) });
            Assert.That(inspect, Is.Not.Null, "The one formal preflight flow needs a structured result, not parsing its successful text.");
            Assert.That(ready, Is.Not.Null);
            var keys = new MissingKeys();
            var state = ready.Invoke(null, new object[] { project, "ux3-app" });
            Assert.That(ResourceWorkbenchUX2Tests.Value(state, "State").ToString(), Is.EqualTo("StaticReady"));
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.target = EditorUserBuildSettings.activeBuildTarget;
            recipe.appContentCompatibilityId = "ux3-app";
            AssetDatabase.CreateAsset(recipe, fixture.Folder + "/ReadinessRecipe.asset");
            var recipeState = ResourceEditorOperations.InspectRecipePreflight(recipe, false, keys);
            Assert.That(recipeState.State, Is.EqualTo(ResourcePreflightState.StaticReady));
            Assert.That(keys.Copies, Is.Zero);
            var full = inspect.Invoke(null, new object[] { project, "ux3-app", keys });
            Assert.That(ResourceWorkbenchUX2Tests.Value(full, "State").ToString(), Is.EqualTo("Failed"));
            Assert.That(ResourceWorkbenchUX2Tests.Value(full, "Error"), Is.TypeOf<ResourceException>());
            Assert.That(keys.Copies, Is.EqualTo(1));
            Assert.Throws<ResourceException>(() => ResourceEditorOperations.Preflight(project, "ux3-app", keys));
            Assert.That(keys.Copies, Is.EqualTo(2));
            project.displayName = "unsaved";
            EditorUtility.SetDirty(project);
            Assert.That(ResourceWorkbenchUX2Tests.Value(ready.Invoke(null, new object[] { project, "ux3-app" }), "State").ToString(), Is.EqualTo("StaticBlocked"));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, EditorUserBuildSettings.activeBuildTarget)))), Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [Test]
        public void StructuredPreflightDistinguishesAtlasPreparationFromACompleteReadOnlyResult()
        {
            using var fixture = new ResourceReleaseFixture();
            var inspect = typeof(ResourceEditorOperations).GetMethod("InspectPreflight", new[] { typeof(ResourceProject), typeof(string), typeof(IResourceContentKeyProvider) });
            Assert.That(inspect, Is.Not.Null);
            var complete = inspect.Invoke(null, new object[] { fixture.Project, "ux3-app", null });
            Assert.That(ResourceWorkbenchUX2Tests.Value(complete, "State").ToString(), Is.EqualTo("Complete"));
            Assert.That(ResourceWorkbenchUX2Tests.Value(complete, "Summary"), Is.EqualTo(ResourceEditorOperations.Preflight(fixture.Project, "ux3-app")));
            var config = ResourceAtlasGenerator.CreateConfiguration(fixture.Project, fixture.Folder + "/Content", "GeneratedAtlases");
            config.rules.Add(new ResourceAtlasRule
            {
                id = "pending",
                groupId = fixture.Project.groups[0].id,
                address = "atlases/pending",
                sources = new[] { AssetDatabase.LoadAssetAtPath<Texture2D>(fixture.Folder + "/Content/Sprites.png") }
            });
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
            var pending = inspect.Invoke(null, new object[] { fixture.Project, "ux3-app", null });
            Assert.That(ResourceWorkbenchUX2Tests.Value(pending, "State").ToString(), Is.EqualTo("AtlasPreparationRequired"));
            Assert.That(ResourceWorkbenchUX2Tests.Value(pending, "Error"), Is.Null);
            Assert.That(ResourceWorkbenchUX2Tests.Value(pending, "Summary"), Is.EqualTo(ResourceEditorOperations.Preflight(fixture.Project, "ux3-app")));
            Assert.That(AssetDatabase.LoadMainAssetAtPath(ResourceAtlasGenerator.Preview(fixture.Project).Changes[0].Path), Is.Null, "Read-only preflight must not generate Atlas output.");
        }

        private static ResourceProject _observationProject;
        private static ResourceEditorBuildReport _reentrant;
        private static bool _hasThrown;
        private static readonly List<string> _reached = new();
        private static void ThrowingObserver(object notice)
        {
            if (_hasThrown)
            {
                return;
            }

            _hasThrown = true;
            _reentrant = ResourceEditorOperations.BuildOnly(_observationProject, "ux3-observer");
            throw new InvalidOperationException("Deliberate observer failure, not an execution decision.");
        }
        private static void CollectObserver(object notice) => _reached.Add(ResourceWorkbenchUX2Tests.Value(notice, "Phase").ToString());

        [Test]
        public void StageObserverExceptionsAndReentryDoNotPreventNativeReadbackOrWorkspaceCompletion()
        {
            var type = typeof(ResourceEditorOperations).Assembly.GetType("YLFramework.Editor.Resource.ResourceBuildProgress");
            Assert.That(type, Is.Not.Null, "Production observation must be distinct from internal fault-injection hooks.");
            var signal = type.GetEvent("StageReached");
            Assert.That(signal, Is.Not.Null);
            using var fixture = new ResourceReleaseFixture();
            _observationProject = fixture.Project;
            _reached.Clear();
            _hasThrown = false;
            _reentrant = null;
            var first = Delegate.CreateDelegate(signal.EventHandlerType, typeof(ResourceWorkbenchUX3Tests).GetMethod(nameof(ThrowingObserver), _internal));
            var second = Delegate.CreateDelegate(signal.EventHandlerType, typeof(ResourceWorkbenchUX3Tests).GetMethod(nameof(CollectObserver), _internal));
            signal.AddEventHandler(null, first);
            signal.AddEventHandler(null, second);
            try
            {
                LogAssert.Expect(LogType.Warning, "资源构建进度回调发生异常；构建流程将继续，并照常验证输入和输出。");
                var result = fixture.Build();
                Assert.That(result.WasUnityBuildInvoked, Is.True);
                Assert.That(_reached, Does.Contain("NativeBuildStarted").And.Contain("ReadbackStarted").And.Contain("WorkspaceVerified"));
                Assert.That(_reached, Does.Not.Contain("PublicationStarted").And.Not.Contain("ReferenceGenerationStarted"));
                Assert.That(_reentrant?.BuildError, Is.TypeOf<InvalidOperationException>());
                Assert.That(_reentrant.BuildError.Message, Does.Contain("observer"));
                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
                Assert.That(typeof(ResourceBundleBuilder).GetField("_isBuilding", _internal).GetValue(null), Is.False,
                    "Observer rejection/failure must not bypass the Builder finally responsibility.");
            }
            finally
            {
                signal.RemoveEventHandler(null, first);
                signal.RemoveEventHandler(null, second);
                _observationProject = null;
            }
        }

        [Test]
        public void AttemptsRemainBoundedAndFilterByOriginalProjectAfterContextReset()
        {
            using var first = new ResourceReleaseFixture();
            using var second = new ResourceReleaseFixture();
            var view = new ResourceBuildView();
            var guid = typeof(ResourceEditorBuildReport).GetProperty("ProjectGuid");
            Assert.That(guid, Is.Not.Null, "A failed attempt still needs its original Project identity.");
            var record = typeof(ResourceBuildView).GetMethod("RecordReport", _internal);
            for (var i = 0; i < 34; i++)
            {
                var report = new ResourceEditorBuildReport { BuildError = new InvalidOperationException("Small bounded-history fixture " + i) };
                var project = i % 2 == 0 ? first.Project : second.Project;
                guid.SetValue(report, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)));
                record.Invoke(view, new object[] { report, new Action<ResourceBuildResult>(_ => { }) });
            }
            view.Reset();
            var filter = typeof(ResourceBuildView).GetMethod("GetAttempts", _internal);
            Assert.That(filter, Is.Not.Null);
            var one = (IReadOnlyList<ResourceEditorBuildReport>)filter.Invoke(view, new object[] { AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(first.Project)) });
            var two = (IReadOnlyList<ResourceEditorBuildReport>)filter.Invoke(view, new object[] { AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(second.Project)) });
            Assert.That(one.Count, Is.EqualTo(16));
            Assert.That(two.Count, Is.EqualTo(16));
            Assert.That(one.Concat(two).Distinct().Count(), Is.EqualTo(32));
        }

        private sealed class MissingKeys : IResourceContentKeyProvider
        {
            internal int Copies;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                Copies++;
                return false;
            }
        }
    }
}

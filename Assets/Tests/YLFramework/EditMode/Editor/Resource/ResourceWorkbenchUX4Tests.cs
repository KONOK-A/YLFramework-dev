using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.ResourceBuild;

namespace YLFramework.Tests.ResourceEditor
{
    public sealed class ResourceWorkbenchUX4Tests
    {
        private static readonly BindingFlags _internal = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        public void NewPublicationCannotBeReplacedByAnOldTableSelectionOrFreshenOldIndex()
        {
            using var fixture = new ResourceReleaseFixture();
            var first = Release(fixture.Project, "ux4-app", 'a');
            var next = Release(fixture.Project, "ux4-app", 'b');
            var view = new ResourceDeliveryView();
            var table = new ResourceReleaseTable();
            table.SetData(new[] { first }, first.Catalog.CatalogDigest);
            table.SetSelection(new[] { table.GetRows()[0].id });
            typeof(ResourceDeliveryView).GetField("_table", _internal).SetValue(view, table);
            typeof(ResourceDeliveryView).GetField("_hasReadHistory", _internal).SetValue(view, true);
            typeof(ResourceDeliveryView).GetField("_hasReadIndex", _internal).SetValue(view, true);
            view.OfferRelease(next);
            Assert.That(table.Selected == null || table.Selected.Release.Catalog.CatalogDigest == next.Catalog.CatalogDigest, Is.True,
                "An old selected row must not overwrite the new publication on the next repaint.");
            Assert.That((bool)typeof(ResourceDeliveryView).GetField("_isHistoryStale", _internal).GetValue(view), Is.True);
            Assert.That((bool)typeof(ResourceDeliveryView).GetField("_isIndexStale", _internal).GetValue(view), Is.True);
        }

        [Test]
        public void RefreshingOnlyIndexDoesNotRevalidateHistoryOrAnotherSelectedVersion()
        {
            using var fixture = new ResourceReleaseFixture();
            var first = Release(fixture.Project, "ux4-app", 'a');
            var next = Release(fixture.Project, "ux4-app", 'b');
            var view = new ResourceDeliveryView();
            view.OfferRelease(first);
            typeof(ResourceDeliveryView).GetField("_hasReadHistory", _internal).SetValue(view, true);
            view.Invalidate();
            var apply = typeof(ResourceDeliveryView).GetMethod("ApplyCurrent", _internal);
            Assert.That(apply, Is.Not.Null);
            apply.Invoke(view, new object[] { next });
            Assert.That((bool)typeof(ResourceDeliveryView).GetField("_isHistoryStale", _internal).GetValue(view), Is.True);
            Assert.That((bool)typeof(ResourceDeliveryView).GetField("_isSelectionStale", _internal).GetValue(view), Is.True);
            Assert.That((bool)typeof(ResourceDeliveryView).GetField("_isIndexStale", _internal).GetValue(view), Is.False);
        }

        [Test]
        public void ArchiveExportKeepsTheOriginalHistoricalValidationInsteadOfCurrentUnityReadVersion()
        {
            using var fixture = new ResourceReleaseFixture();
            var release = Release(fixture.Project, "ux4-archive", 'a');
            var identity = new ResourceReleaseIdentity(release);
            var method = typeof(ResourceReleaseIdentity).GetMethod("ResolveForArchive", _internal);
            Assert.That(method, Is.Not.Null, "Byte archive export has its original VerifyExportInput contract, not signing's current-Unity ReadVersion gate.");
            var failure = Assert.Throws<TargetInvocationException>(() => method.Invoke(identity, new object[] { release }));
            Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>(), "Original VerifyExportInput uses InvalidOperationException for an unregistered owner.");
            Assert.That(failure.InnerException.Message, Does.Contain("no longer registered"));
            Assert.Throws<TargetInvocationException>(() => method.Invoke(identity, new object[] { Release(fixture.Project, "other-app", 'b') }));
        }

        [Test]
        public void ExportTargetsKeepTheCatalogDigestDirectoryRequiredBySnapshotOpen()
        {
            using var fixture = new ResourceReleaseFixture();
            var release = Release(fixture.Project, "ux4-export", 'a');
            var identity = new ResourceReleaseIdentity(release);
            var method = typeof(ResourceDeliveryView).GetMethod("ExportDirectory", _internal);
            Assert.That(method, Is.Not.Null, "An arbitrary new output directory can pass Publisher copying but cannot be opened as a complete version; UI must keep the Digest directory.");
            var parent = Path.Combine(Path.GetTempPath(), "kilo");
            var result = (string)method.Invoke(null, new object[] { identity, parent });
            Assert.That(Path.GetFileName(result), Is.EqualTo(release.Catalog.CatalogDigest));
            Assert.That(Path.GetDirectoryName(result), Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(parent))));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(result)), Is.False);
        }

        [UnityTest]
        public IEnumerator DeliveryViewingAndResizingDoNotReadHistoryIndexOrCreateDeploymentState()
        {
            using var fixture = new ResourceReleaseFixture();
            var focus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<ResourceUX2GuiWindow>();
            try
            {
                window.ShowUtility();
                window.SetProject(fixture.Project);
                window.position = new Rect(100, 80, 1080, 720);
                var view = typeof(ResServiceEditorWindow).GetField("_resServiceView", _internal).GetValue(window);
                view.GetType().GetField("_tab", _internal).SetValue(view, 3);
                var build = view.GetType().GetField("_build", _internal).GetValue(view);
                var delivery = build.GetType().GetField("_delivery", _internal).GetValue(build);
                var before = JsonUtility.ToJson(fixture.Project);
                foreach (var width in new[] { 1080, 720, 620 })
                {
                    window.position = new Rect(100, 80, width, width == 620 ? 440 : 720);
                    for (var i = 0; i < 3; i++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                }
                Assert.That(delivery.GetType().GetField("_hasReadHistory", _internal).GetValue(delivery), Is.False);
                Assert.That(delivery.GetType().GetField("_hasReadIndex", _internal).GetValue(delivery), Is.False);
                Assert.That(delivery.GetType().GetField("_plan", _internal).GetValue(delivery), Is.Null);
                Assert.That(JsonUtility.ToJson(fixture.Project), Is.EqualTo(before));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(ResourceBuildMap.Absolute("AssetBundles/YLFramework"), fixture.Project.projectId))), Is.False);
                Assert.That(ResourceSystem.Current, Is.Null);
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
        public void SelectedSigningKeepsTheExactIdentityAndRechecksVersionRatherThanCurrentIndex()
        {
            using var fixture = new ResourceReleaseFixture();
            var source = Release(fixture.Project, "ux4-app", 'a');
            var type = typeof(ResourcePublisher).Assembly.GetType("YLFramework.Editor.Resource.ResourceReleaseSigningWindow");
            var bind = type.GetMethod("SelectRelease", _internal);
            Assert.That(bind, Is.Not.Null, "Selected-version signing must not silently read ReadCurrent at execution time.");
            var window = ScriptableObject.CreateInstance(type);
            try
            {
                bind.Invoke(window, new object[] { fixture.Project, source });
                var identity = type.GetField("selected", _internal).GetValue(window);
                Assert.That(ResourceWorkbenchUX2Tests.Value(identity, "CatalogDigest"), Is.EqualTo(source.Catalog.CatalogDigest));
                Assert.That(ResourceWorkbenchUX2Tests.Value(identity, "ProjectGuid"), Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fixture.Project))));
                var failure = Assert.Throws<TargetInvocationException>(() => type.GetMethod("ResolveRelease", _internal).Invoke(window, null));
                Assert.That(failure.InnerException, Is.TypeOf<InvalidDataException>(), "This unit fixture is not an owned Publisher release: exact ReadVersion must reject it, not fall back to a null/current Index.");
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(source.RootPath)), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void ReleaseTablePreservesExactVersionOnSortAndFilterAndDoesNotInventTime()
        {
            using var fixture = new ResourceReleaseFixture();
            var first = Release(fixture.Project, "ux4-app-a", 'a');
            var second = Release(fixture.Project, "ux4-app-b", 'b');
            var type = typeof(ResourcePublisher).Assembly.GetType("YLFramework.Editor.Resource.ResourceReleaseTable");
            Assert.That(type, Is.Not.Null);
            var table = (TreeView)Activator.CreateInstance(type, true);
            type.GetMethod("SetData", _internal).Invoke(table, new object[] { new[] { first, second }, second.Catalog.CatalogDigest });
            Assert.That(table.GetRows().Count, Is.EqualTo(2));
            var row = table.GetRows()[0];
            table.SetSelection(new[] { row.id });
            var selected = type.GetProperty("Selected", _internal);
            var original = selected.GetValue(table);
            table.multiColumnHeader.SetSorting(1, false);
            Assert.That(selected.GetValue(table), Is.SameAs(original));
            var release = (ResourceRelease)ResourceWorkbenchUX2Tests.Value(original, "Release");
            table.searchString = release.Catalog.CatalogDigest;
            Assert.That(table.GetRows().Count, Is.EqualTo(1));
            Assert.That(selected.GetValue(table), Is.SameAs(original));
            Assert.That((bool)ResourceWorkbenchUX2Tests.Value(original, "IsCurrent"), Is.EqualTo(release.Catalog.CatalogDigest == second.Catalog.CatalogDigest));
            table.searchString = "not-an-owned-version";
            Assert.That(table.GetRows(), Is.Empty);
            Assert.That(table.GetSelection(), Is.EqualTo(new[] { row.id }));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(first.RootPath)), Is.False, "Projection/sorting/filtering must not read or create Publisher directories.");
        }

        [Test]
        public void DeploymentConfirmationIncludesWorkspaceControlShellMetaAndImportNotJustPayload()
        {
            using var fixture = new ResourceBuiltInDeploymentTests.BuiltInSource();
            var plan = ResourceBuiltInDeployer.Preflight(fixture.Snapshot);
            var type = typeof(ResourcePublisher).Assembly.GetType("YLFramework.Editor.Resource.ResourceBuiltInScope");
            Assert.That(type, Is.Not.Null);
            var text = (string)type.GetMethod("Describe", _internal).Invoke(null, new object[] { plan });
            foreach (var token in new[] { plan.WorkspacePath, plan.DirectoryPath, fixture.ProjectId, "ylr-owner.json~", "deploy.lock", "journal.json",
                "journal.json.next", "stage", "ylr-deployment.json~", "payload~", ".meta", "AssetDatabase", "未检查磁盘容量" })
            {
                Assert.That(text, Does.Contain(token));
            }

            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(plan.WorkspacePath)), Is.False);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(plan.DirectoryPath)), Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [Test]
        public void RecoveryLocationIsReadOnlyAndDoesNotRequireASuccessfulVersionPlan()
        {
            using var fixture = new ResourceBuiltInDeploymentTests.BuiltInSource();
            var locate = typeof(ResourceBuiltInDeployer).GetMethod("GetWorkspacePath", new[] { typeof(string), typeof(string) });
            Assert.That(locate, Is.Not.Null, "Independent recovery must not depend on a new successful release Preflight or parse an error/journal.");
            var path = (string)locate.Invoke(null, new object[] { fixture.ProjectGuid, "StandaloneWindows64" });
            Assert.That(path, Is.EqualTo(fixture.WorkspacePath));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.False);
        }

        private static ResourceRelease Release(ResourceProject project, string app, char build)
        {
            var data = ResourceCatalogCodecTests.Payload();
            data["ProjectId"] = project.projectId;
            data["AppContentCompatibilityId"] = app;
            data["UnityCompatibility"] = Application.unityVersion;
            data["BuildSettingsFingerprint"] = new string(build, 64);
            var oldFile = (string)data["Bundles"][0]["RelativeFile"];
            var native = "ylr_" + ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(project.projectId)).Substring(0, 32) + "_a_fixture";
            var file = "b_" + ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(native)).Substring(0, 32);
            data["Bundles"][0]["LogicalId"] = native;
            data["Bundles"][0]["NativeName"] = native;
            data["Bundles"][0]["RelativeFile"] = file;
            data["Assets"][0]["BundleId"] = native;
            foreach (var support in (JArray)data["SupportFiles"])
            {
                if ((string)support["RelativeFile"] == oldFile + ".manifest")
                {
                    support["RelativeFile"] = file + ".manifest";
                }
            }

            var catalog = ResourceCatalogCodec.Read(ResourceCatalogCodec.Seal(Encoding.UTF8.GetBytes(data.ToString())), project.projectId,
                "StandaloneWindows64", Application.unityVersion, app);
            var root = Path.Combine(ResourceBuildMap.Absolute("AssetBundles/YLFramework"), project.projectId, "StandaloneWindows64");
            return new ResourceRelease(root, catalog, projectGuid: AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)));
        }
    }
}

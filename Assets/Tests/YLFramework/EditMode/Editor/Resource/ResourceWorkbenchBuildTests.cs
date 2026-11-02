using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using YLFramework.Editor.Resource;
using YLFramework.Tests.ResourceBuild;

namespace YLFramework.Tests.ResourceEditor
{
    public class ResourceWorkbenchBuildTests
    {
        [Test]
        public void RealBuildMeasuresWorkAndOwnedHistoryAndExportNeverAdoptUnknownDirectories()
        {
            var history = typeof(ResourcePublisher).GetMethod("ReadHistory", new[] { typeof(ResourceProject), typeof(BuildTarget) });
            Assert.IsNotNull(history, "Owned release history must come from the Publisher, not a directory scan in the window.");
            Assert.IsNotNull(typeof(ResourceBuildResult).GetProperty("ElapsedMilliseconds"), "Actual build measurement is missing.");
            var export = typeof(ResourceEditorOperations).GetMethod("ExportRelease", new[] { typeof(ResourceRelease), typeof(string) });
            Assert.IsNotNull(export, "Explicit safe local release export is missing.");
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            project.groups.RemoveRange(1, project.groups.Count - 1);
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            var first = fixture.Build();
            var release = ResourcePublisher.Publish(first, "fixture-v1");
            fixture.ReleaseRoot = release.RootPath;
            var before = ResourceReleaseFixture.Snapshot(release.RootPath);
            var releases = (IReadOnlyList<ResourceRelease>)history.Invoke(null, new object[] { fixture.Project, EditorUserBuildSettings.activeBuildTarget });
            Assert.AreEqual(1, releases.Count);
            Assert.AreEqual(release.Catalog.CatalogDigest, releases[0].Catalog.CatalogDigest);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(release.RootPath), "History reads must not recover or rewrite publication state.");
            Assert.GreaterOrEqual(Number(first, "ElapsedMilliseconds"), first.BuildMilliseconds);
            Assert.GreaterOrEqual(Number(first, "BuildPipelineMilliseconds"), 0);
            Assert.Greater(first.SourceHashBytes, 0);
            Assert.Greater(first.ArtifactHashBytes, 0);
            Assert.AreEqual(first.Files.Single(x => x.Name == first.Bundles[0].Name).Length, Number(first, "BundleFileBytes"));
            var second = fixture.Build();
            Assert.IsTrue(second.WasUnityBuildInvoked && second.ForceRebuild && !second.IsCacheHit, "Untracked callbacks must still force the safe native build.");
            var changes = (System.Collections.IEnumerable)second.GetType().GetProperty("BundleChanges").GetValue(second);
            var unchanged = changes.Cast<object>().ToArray();
            Assert.AreEqual(1, unchanged.Length);
            Assert.AreEqual(false, unchanged[0].GetType().GetProperty("ContentChanged").GetValue(unchanged[0]), "A forced native rebuild is not evidence that Bundle bytes changed.");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.TextPath), "8.9 measured single Text content change");
            AssetDatabase.ImportAsset(fixture.TextPath, ImportAssetOptions.ForceSynchronousImport);
            var changed = fixture.Build();
            Assert.IsTrue(changed.WasUnityBuildInvoked && changed.ForceRebuild && !changed.IsCacheHit);
            var changedBundles = ((System.Collections.IEnumerable)changed.GetType().GetProperty("BundleChanges").GetValue(changed)).Cast<object>().ToArray();
            Assert.AreEqual(1, changedBundles.Length);
            Assert.AreEqual(true, changedBundles[0].GetType().GetProperty("ContentChanged").GetValue(changedBundles[0]));
            Assert.AreNotEqual(first.Files.Single(x => x.Name == first.Bundles[0].Name).Sha256, changed.Files.Single(x => x.Name == changed.Bundles[0].Name).Sha256);
            UnityEngine.Debug.Log("RESOURCE_89_BUILD clean=" + Number(first, "ElapsedMilliseconds").ToString("F2") +
                "ms / no-change=" + Number(second, "ElapsedMilliseconds").ToString("F2") + "ms / single-change=" + Number(changed, "ElapsedMilliseconds").ToString("F2") +
                "ms; hash bytes=" + first.ArtifactHashBytes + "/" + second.ArtifactHashBytes + "/" + changed.ArtifactHashBytes +
                "; callbacks=" + changed.CacheReuseBlockedBy.Count + "; no-change SHA stable=true; single-change SHA changed=true");
            var output = Path.Combine(Path.GetTempPath(), "kilo", "ResourceExport89-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(output));
            try
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(output, "sentinel")), "unknown directory");
                var unknown = ResourceReleaseFixture.Snapshot(output);
                var failure = Assert.Throws<TargetInvocationException>(() => export.Invoke(null, new object[] { release, output }));
                Assert.IsInstanceOf<InvalidOperationException>(failure.InnerException);
                CollectionAssert.AreEqual(unknown, ResourceReleaseFixture.Snapshot(output));
                var child = Path.Combine(output, release.Catalog.CatalogDigest);
                export.Invoke(null, new object[] { release, child });
                CollectionAssert.AreEqual(ResourceReleaseFixture.Snapshot(release.DirectoryPath), ResourceReleaseFixture.Snapshot(child));
                Assert.Throws<TargetInvocationException>(() => export.Invoke(null, new object[] { release, child }));
                CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(release.RootPath));
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(output), true);
            }
            fixture.AssertProtected();
        }

        private static double Number(object value, string property)
        {
            var member = value.GetType().GetProperty(property);
            Assert.IsNotNull(member, "Missing measurement: " + property);
            return Convert.ToDouble(member.GetValue(value));
        }
    }
}

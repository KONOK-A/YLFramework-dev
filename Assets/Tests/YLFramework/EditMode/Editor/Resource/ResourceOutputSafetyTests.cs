using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;

namespace YLFramework.Tests.ResourceEditor
{
    [Category("YL.UPM.Generators")]
    public class ResourceOutputSafetyTests
    {
        private const string StateKey = "YL.ResourceOutputSafetyTests";
        private static string _root => SessionState.GetString(StateKey, "");
        private static Type Output => typeof(ResourceCodeGenerator).Assembly.GetType("YLFramework.Editor.Resource.ResourceReferenceOutput");
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Start resource output safety regression");
            Assert.That(_root, Is.Empty, "Do not take over an interrupted safety fixture.");
            string root = "Assets/Safety82c-" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(root) || File.Exists(root) || File.Exists(root + ".meta"), Is.False);
            SessionState.SetString(StateKey, root);
            SessionState.SetString(StateKey + ".Owner", TestContext.CurrentContext.Test.FullName);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (string.IsNullOrEmpty(_root) || SessionState.GetString(StateKey + ".Owner", "") != TestContext.CurrentContext.Test.FullName)
            {
                yield break;
            }
            var foreign = SessionState.GetString(StateKey + ".Foreign", "");
            if (!string.IsNullOrEmpty(foreign))
            {
                Assert.That(foreign, Does.StartWith("Assets/ForeignAssembly82c-"));
                AssetDatabase.DeleteAsset(foreign);
            }
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root), true);
            }

            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root + ".meta")))
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root + ".meta"));
            }
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Remove resource output safety fixture");
            SessionState.EraseString(StateKey);
            SessionState.EraseString(StateKey + ".Owner");
            SessionState.EraseString(StateKey + ".Foreign");
            SessionState.EraseString(StateKey + ".Definition");
            SessionState.EraseString(StateKey + ".Source");
        }
        private static bool Publish(string path) => (bool)ResourceEditorTests.Invoke(Output, "Publish", path, "namespace Safe82c { public static class References {} }\n");

        [Test]
        public void GenerationRefusesAncestorAssemblyFileOccupancyAndOutOfAssetsPaths()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_root));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/User.asmdef"), "{\"name\":\"UserAssembly\"}");
            Assert.Throws<InvalidOperationException>(() => Publish(_root + "/Generated"));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/Generated")), Is.False);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/User.asmdef")), Is.EqualTo("{\"name\":\"UserAssembly\"}"));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/User.asmdef"));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/Generated"), "occupied by user file");
            Assert.Throws<InvalidOperationException>(() => Publish(_root + "/Generated"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/Generated")), Is.EqualTo("occupied by user file"));
            Assert.Throws<InvalidOperationException>(() => Publish("Packages/Generated82c"));
        }

        [Test]
        public void JunctionAncestorsAndNestedLinksAreRejectedWithoutFollowingTheirTargets()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_root));
            var outside = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Temp/Safety82c-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(outside));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "foreign")), "keep");
            var link = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/Linked"));
            try
            {
                CreateJunction(link, outside);
                Assert.Throws<InvalidOperationException>(() => Publish(_root + "/Linked/Generated"));
                Assert.Throws<InvalidOperationException>(() => Publish(_root));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "foreign"))), Is.EqualTo("keep"));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "Generated"))), Is.False);
            }
            finally
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(link)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(link));
                }
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(outside), true);
            }
        }

        [UnityTest]
        public IEnumerator LiveDuplicateAssemblyIsRejectedEvenForByteIdenticalGeneration()
        {
            Assert.That(Publish(_root), Is.True);
            SessionState.SetString(StateKey + ".Source", Convert.ToBase64String(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/YLResourceReferences.cs"))));
            string definition = _root + "/YLFramework.Generated.ResourceNames.asmdef";
            SessionState.SetString(StateKey + ".Definition", Convert.ToBase64String(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(definition))));
            // Import only the foreign identity. Exposing both definitions to Unity would itself cause a compile error before the writer is exercised.
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(definition));
            var foreign = "Assets/ForeignAssembly82c-" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(foreign) || File.Exists(foreign) || File.Exists(foreign + ".meta"), Is.False);
            SessionState.SetString(StateKey + ".Foreign", foreign);
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(foreign));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(foreign + "/Foreign.asmdef"), "{\"name\":\"YLFramework.Generated.ResourceNames\",\"references\":[],\"noEngineReferences\":true}");
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Import duplicate resource assembly");
            AssertDuplicateIdentityUnchanged();
        }

        private static void AssertDuplicateIdentityUnchanged()
        {
            string definition = _root + "/YLFramework.Generated.ResourceNames.asmdef";
            var definitionBytes = Convert.FromBase64String(SessionState.GetString(StateKey + ".Definition", ""));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(definition), definitionBytes);
            var error = Assert.Throws<InvalidOperationException>(() => Publish(_root));
            Assert.That(error.Message, Does.Contain(SessionState.GetString(StateKey + ".Foreign", "") + "/Foreign.asmdef"));
            CollectionAssert.AreEqual(Convert.FromBase64String(SessionState.GetString(StateKey + ".Source", "")),
                File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_root + "/YLResourceReferences.cs")));
            CollectionAssert.AreEqual(definitionBytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(definition)));
        }

        internal static void CreateJunction(string link, string target)
        {
            Assert.IsTrue(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(link))) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(target)));
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(start);
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.EqualTo(0), output);
            Assert.That((File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(link)) & FileAttributes.ReparsePoint) != 0, Is.True);
        }
    }
}

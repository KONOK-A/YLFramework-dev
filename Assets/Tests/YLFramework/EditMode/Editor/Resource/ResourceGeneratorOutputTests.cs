using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Core;
using YLFramework.Editor.Resource;

namespace YLFramework.Tests.ResourceEditor
{
    [Category("YL.UPM.Generators")]
    public class ResourceGeneratorOutputTests
    {
        private const string LegacyRoot = "Assets/YLFrameworkData";
        private const string DefaultDirectory = "Assets/YLFrameworkData/Generated/ResourceReferences";
        private const string AssemblyName = "YLFramework.Generated.ResourceNames";
        private const string StateKey = "YL.ResourceGeneratorOutputTests";

        [UnitySetUp]
        public IEnumerator ReserveCleanProjectOutput()
        {
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Reserve default generator outputs");
            Assert.That(SessionState.GetString(StateKey, ""), Is.Empty, "Do not take over an interrupted fixture reservation.");
            Assert.That(Directory.Exists(LegacyRoot) || File.Exists(LegacyRoot) || File.Exists(LegacyRoot + ".meta"), Is.False,
                "Run in a clean tool-owned host; never remove or relocate pre-existing project output for this fixture.");
            SessionState.SetString(StateKey, TestContext.CurrentContext.Test.FullName);
            SessionState.SetString(StateKey + ".Module", "YLGeneratorTest_" + Guid.NewGuid().ToString("N"));
            SessionState.SetIntArray(StateKey + ".Selection", Selection.objects.Select(value => value.GetInstanceID()).ToArray());
        }

        [UnityTearDown]
        public IEnumerator RemoveOnlyReservedOutput()
        {
            if (SessionState.GetString(StateKey, "") != TestContext.CurrentContext.Test.FullName)
            {
                yield break;
            }
            Selection.objects = SessionState.GetIntArray(StateKey + ".Selection", Array.Empty<int>())
                .Select(EditorUtility.InstanceIDToObject).Where(value => value).ToArray();

            AssetDatabase.DeleteAsset(LegacyRoot);
            if (Directory.Exists(LegacyRoot))
            {
                Directory.Delete(LegacyRoot, true);
            }
            if (File.Exists(LegacyRoot + ".meta"))
            {
                File.Delete(LegacyRoot + ".meta");
            }
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Remove reserved default generator outputs");
            Assert.That(Directory.Exists(LegacyRoot) || File.Exists(LegacyRoot) || File.Exists(LegacyRoot + ".meta"), Is.False);
            SessionState.EraseString(StateKey);
            SessionState.EraseString(StateKey + ".Module");
            SessionState.EraseString(StateKey + ".Asset");
            SessionState.EraseIntArray(StateKey + ".Selection");
        }

        [UnityTest]
        public IEnumerator DefaultOutputsCoexistResourceModuleScriptableObject() => DefaultOutputsCoexist("RMS");

        [UnityTest]
        public IEnumerator DefaultOutputsCoexistResourceScriptableObjectModule() => DefaultOutputsCoexist("RSM");

        [UnityTest]
        public IEnumerator DefaultOutputsCoexistModuleResourceScriptableObject() => DefaultOutputsCoexist("MRS");

        [UnityTest]
        public IEnumerator DefaultOutputsCoexistModuleScriptableObjectResource() => DefaultOutputsCoexist("MSR");

        [UnityTest]
        public IEnumerator DefaultOutputsCoexistScriptableObjectResourceModule() => DefaultOutputsCoexist("SRM");

        [UnityTest]
        public IEnumerator DefaultOutputsCoexistScriptableObjectModuleResource() => DefaultOutputsCoexist("SMR");

        private static IEnumerator DefaultOutputsCoexist(string order)
        {
            Assert.That(AssetDatabase.CreateFolder("Assets", "YLFrameworkData"), Is.Not.Empty);
            Assert.That(AssetDatabase.CreateFolder(LegacyRoot, "Generated"), Is.Not.Empty);
            yield return ResourceGeneratorTestWait.Clear("Create shared output parents");
            // Keep reload boundaries in the top-level iterator; UTF restores its program counter, not its local objects or loop counters.
            RunDefaultGenerator(order[0]);
            if (order[0] != 'S')
            {
                yield return new RecompileScripts();
            }
            yield return ResourceGeneratorTestWait.Clear("Complete first default generator");
            RunDefaultGenerator(order[1]);
            if (order[1] != 'S')
            {
                yield return new RecompileScripts();
            }
            yield return ResourceGeneratorTestWait.Clear("Complete second default generator");
            RunDefaultGenerator(order[2]);
            if (order[2] != 'S')
            {
                yield return new RecompileScripts();
            }
            yield return ResourceGeneratorTestWait.Clear("Complete third default generator");
            AssertDefaultOutputsUnchanged();
        }

        private static void RunDefaultGenerator(char step)
        {
            switch (step)
            {
                case 'R':
                    Assert.That(ResourceCodeGenerator.GenerateReferences(new[] { ResourceCodeGenerationTests.Catalog("coexist.key") }), Is.True);
                    break;
                case 'M':
                    CreateModuleCommand.CreateModule(SessionState.GetString(StateKey + ".Module", ""), "Project", false, "");
                    break;
                case 'S':
                    var asset = ScriptableObject.CreateInstance<ResourceProject>();
                    try
                    {
                        asset.CreateAsset();
                        SessionState.SetString(StateKey + ".Asset", AssetDatabase.GetAssetPath(asset));
                    }
                    finally
                    {
                        if (asset && !AssetDatabase.Contains(asset))
                        {
                            UnityEngine.Object.DestroyImmediate(asset);
                        }
                    }
                    break;
            }
        }

        private static void AssertDefaultOutputsUnchanged()
        {
            string source = DefaultDirectory + "/YLResourceReferences.cs";
            string assembly = DefaultDirectory + "/" + AssemblyName + ".asmdef";
            string module = SessionState.GetString(StateKey + ".Module", "");
            string moduleSource = CreateModuleCommand.OutputRoot + "/" + module + "/" + module + ".cs";
            string assetPath = SessionState.GetString(StateKey + ".Asset", "");
            Assert.That(assetPath, Does.StartWith("Assets/YLFrameworkData/ScriptableObjects/"));
            Assert.That(File.Exists(source) && File.Exists(assembly) && File.Exists(moduleSource), Is.True);
            Assert.That(Directory.GetFiles(DefaultDirectory).Where(path => !path.EndsWith(".meta")).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "YLResourceReferences.cs", AssemblyName + ".asmdef" }));
            var paths = new[] { source, assembly, moduleSource, assetPath, assetPath + ".meta" };
            var bytes = paths.Select(File.ReadAllBytes).ToArray();
            var times = paths.Select(File.GetLastWriteTimeUtc).ToArray();
            Assert.That(ResourceCodeGenerator.GenerateReferences(new[] { ResourceCodeGenerationTests.Catalog("coexist.key") }), Is.False);
            for (int index = 0; index < paths.Length; index++)
            {
                CollectionAssert.AreEqual(bytes[index], File.ReadAllBytes(paths[index]), paths[index]);
                Assert.That(File.GetLastWriteTimeUtc(paths[index]), Is.EqualTo(times[index]), paths[index]);
            }
        }

        [UnityTest]
        public IEnumerator DefaultCreatesMissingParentsAndUsesTheSameLeafAsTheWriterContract()
        {
            yield return ResourceGeneratorTestWait.Clear("Generate default resource references");
            Assert.That(ResourceCodeGenerator.GenerateReferences(new[] { ResourceCodeGenerationTests.Catalog("key") }), Is.True);
            Assert.That(File.Exists(DefaultDirectory + "/YLResourceReferences.cs"), Is.True);
            var writer = typeof(ResourceCodeGenerator).Assembly.GetType("YLFramework.Editor.Resource.ResourceReferenceOutput");
            Assert.That(writer.GetField("DirectoryPath", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue(), Is.EqualTo(DefaultDirectory));
            Assert.That(File.Exists(LegacyRoot + "/YLResourceReferences.cs"), Is.False);
        }

        [UnityTest]
        public IEnumerator LegacyRootIdentityIsPreservedAndDefaultRefusesADuplicateWithoutCreatingParents()
        {
            Assert.That(ResourceCodeGenerator.GenerateReferences(new[] { ResourceCodeGenerationTests.Catalog("legacy.key") }, LegacyRoot), Is.True);
            yield return new RecompileScripts();
            yield return ResourceGeneratorTestWait.Clear("Import existing legacy resource identity");
            AssertLegacyIdentityUnchanged();
        }

        private static void AssertLegacyIdentityUnchanged()
        {
            string source = LegacyRoot + "/YLResourceReferences.cs";
            string assembly = LegacyRoot + "/" + AssemblyName + ".asmdef";
            var paths = new[] { source, assembly, source + ".meta", assembly + ".meta", LegacyRoot + ".meta" };
            var bytes = paths.Select(File.ReadAllBytes).ToArray();
            var times = paths.Select(File.GetLastWriteTimeUtc).ToArray();

            var error = Assert.Throws<InvalidOperationException>(() => ResourceCodeGenerator.GenerateReferences(new[] { ResourceCodeGenerationTests.Catalog("legacy.key") }));

            Assert.That(error.Message.Replace('\\', '/'), Does.Contain(assembly));
            Assert.That(error.Message, Does.Contain(AssemblyName).And.Contain("Migration-UPM"));
            Assert.That(Directory.Exists(LegacyRoot + "/Generated"), Is.False);
            for (int index = 0; index < paths.Length; index++)
            {
                CollectionAssert.AreEqual(bytes[index], File.ReadAllBytes(paths[index]), paths[index]);
                Assert.That(File.GetLastWriteTimeUtc(paths[index]), Is.EqualTo(times[index]), paths[index]);
            }
            Assert.That(ResourceCodeGenerator.GenerateReferences(new[] { ResourceCodeGenerationTests.Catalog("updated.key") }, LegacyRoot), Is.True,
                "An explicit valid legacy location must retain its identity, not be redirected or auto-moved.");
            for (int index = 1; index < paths.Length; index++)
            {
                CollectionAssert.AreEqual(bytes[index], File.ReadAllBytes(paths[index]), paths[index]);
            }
        }

        [UnityTest]
        public IEnumerator RelocatedAssemblyIsReportedAndDefaultDoesNotWriteASecondDefinition()
        {
            Assert.That(AssetDatabase.CreateFolder("Assets", "YLFrameworkData"), Is.Not.Empty);
            Assert.That(AssetDatabase.CreateFolder(LegacyRoot, "Relocated"), Is.Not.Empty);
            string assembly = LegacyRoot + "/Relocated/Existing.asmdef";
            File.WriteAllText(assembly, "{\"name\":\"" + AssemblyName + "\",\"references\":[],\"noEngineReferences\":true}");
            AssetDatabase.ImportAsset(assembly, ImportAssetOptions.ForceSynchronousImport);
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Import relocated resource assembly");
            AssertRelocatedIdentityUnchanged();
        }

        private static void AssertRelocatedIdentityUnchanged()
        {
            string assembly = LegacyRoot + "/Relocated/Existing.asmdef";
            var before = File.ReadAllBytes(assembly);
            var meta = File.ReadAllBytes(assembly + ".meta");

            var error = Assert.Throws<InvalidOperationException>(() => ResourceCodeGenerator.GenerateReferences(new[] { ResourceCodeGenerationTests.Catalog("key") }));

            Assert.That(error.Message, Does.Contain(assembly).And.Contain("Migration-UPM"));
            Assert.That(Directory.Exists(LegacyRoot + "/Generated"), Is.False);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(assembly));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(assembly + ".meta"));
        }
    }
}

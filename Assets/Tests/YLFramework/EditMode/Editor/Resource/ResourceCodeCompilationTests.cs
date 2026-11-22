using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;

namespace YLFramework.Tests.ResourceEditor
{
    [Category("YL.UPM.Generators")]
    public class ResourceCodeCompilationTests
    {
        private const string Key = "YL.Resource82c.CompilationProbe";
        private static string Output => SessionState.GetString(Key + ".Output", "");
        private const string GeneratedAssembly = "YLFramework.Generated.ResourceNames";
        private static string ConsumerAssembly => "YLFramework.Tests.ResourceNamesConsumer63." +
              Path.GetFileName(SessionState.GetString(Key, "")).Replace("YLGeneratorInputs-", "");

        [SetUp]
        public void ReserveIsolatedOutput()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(Key, "")))
            {
                return;
            }

            if (!string.IsNullOrEmpty(Output))
            {
                Assert.AreEqual(TestContext.CurrentContext.Test.FullName, SessionState.GetString(Key + ".OutputOwner", ""),
                    "Do not take over another probe's output reservation.");
                Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")),
                    "A reservation resumed before generation must still be empty.");
                return;
            }
            var output = "Assets/Tests/YLFramework/Fixtures/ResourceNamesCompile-" + Guid.NewGuid().ToString("N");
            Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output + ".meta")));
            SessionState.SetString(Key + ".Output", output);
            SessionState.SetString(Key + ".OutputOwner", TestContext.CurrentContext.Test.FullName);
        }

        [Test]
        public void InterruptedCleanupRefusesRecreatedOrModifiedOutput()
        {
            Assert.IsEmpty(SessionState.GetString(Key, ""));
            Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")));
            var output = Output;
            var root = ResourceAddressablesGeneratorFixture.InputPrefix + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Output));
            try
            {
                SessionState.SetString(Key, root);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output + "/User.cs"), "// independently recreated user source");
                Assert.Throws<AssertionException>(() => Cleanup().MoveNext());
                Assert.AreEqual("// independently recreated user source", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output + "/User.cs")));

                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
                // Unimported byte sentinels test cleanup ownership, not AssetDatabase GUID facts.
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + ".meta"), "probe identity");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta"), "output identity");
                RecordOwned();
                File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output + "/User.cs"), " changed after interruption");
                Assert.Throws<AssertionException>(() => Cleanup().MoveNext());
                Assert.IsTrue(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)));
                StringAssert.EndsWith(" changed after interruption", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output + "/User.cs")));
            }
            finally
            {
                ClearState();
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
                }

                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root + ".meta")))
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root + ".meta"));
                }

                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(output), true);
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output + ".meta")))
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(output + ".meta"));
                }
            }
        }

        [UnityTest]
        public IEnumerator RealGeneratedSourceCompilesForAsmdefAndPredefinedConsumers()
        {
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Start generated consumer compilation probe");
            var previous = SessionState.GetString(Key, "");
            // 清理后等待编译时若被中断，旧 Key 不能授权清理后来出现的用户目录。
            if (!string.IsNullOrEmpty(previous) && !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(previous)))
            {
                ClearState();
                ReserveIsolatedOutput();
            }
            if (string.IsNullOrEmpty(SessionState.GetString(Key, "")))
            {
                yield return Prepare();
            }

            RequireOwned();
            yield return new RecompileScripts();
            yield return ResourceGeneratorTestWait.Clear("Compile generated references and both consumers");
            VerifyCompiledAndUpdate();
            yield return new RecompileScripts();
            yield return ResourceGeneratorTestWait.Clear("Compile updated official labels");
            Assert.IsFalse(EditorUtility.scriptCompilationFailed);
            var generated = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == GeneratedAssembly);
            Assert.AreEqual("new-label", generated.GetType("YLFramework.Resources.ResourceNames+Labels", true).GetField("NewLabel").GetRawConstantValue());
        }

        // Test Framework 恢复 iterator 时不保留跨 Domain Reload 的闭包对象，验证局部值在普通方法中重建。
        private static void VerifyCompiledAndUpdate()
        {
            var root = SessionState.GetString(Key, "");
            Assert.IsFalse(EditorUtility.scriptCompilationFailed);
            var generated = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == GeneratedAssembly);
            var settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(root + ResourceAddressablesGeneratorFixture.SettingsRelativePath);
            Assert.That(settings, Is.Not.Null);
            foreach (var assemblyName in new[] { ConsumerAssembly, "Assembly-CSharp" })
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == assemblyName);
                var consumer = assembly.GetType("YLFramework.Tests.GeneratedConsumer82c." +
                     (assemblyName == ConsumerAssembly ? "Explicit" : "Predefined"), true);
                var address = (string)consumer.GetMethod("Address").Invoke(null, null);
                Assert.AreEqual("entry.txt", address);
                var entry = settings.DefaultGroup.entries.Single(value => value.address == address);
                Assert.That(entry.AssetPath, Is.EqualTo(ResourceAddressablesGeneratorFixture.InputPath(root, 0)));
                Assert.That(entry.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(entry.AssetPath)));
                Assert.AreEqual(ResourceAddressablesGeneratorFixture.FirstContent, AssetDatabase.LoadAssetAtPath<TextAsset>(entry.AssetPath).text);
                var secondary = (string)consumer.GetMethod("SecondaryAddress").Invoke(null, null);
                Assert.That(secondary, Is.EqualTo("secondary/item"));
                var secondaryEntry = settings.DefaultGroup.entries.Single(value => value.address == secondary);
                Assert.That(secondaryEntry.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(ResourceAddressablesGeneratorFixture.InputPath(root, 1))));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(secondaryEntry.AssetPath).text, Is.EqualTo("generator input 1"));
                Assert.AreEqual("stable-label", consumer.GetMethod("Label").Invoke(null, null));
                Assert.That(entry.labels, Does.Contain("stable-label"));
            }
            var graph = CompilationPipeline.GetAssemblies().Single(x => x.name == GeneratedAssembly);
            CollectionAssert.AreEqual(new[] { Output + "/YLResourceReferences.cs" }, graph.sourceFiles.Select(x => x.Replace('\\', '/')).ToArray());
            Assert.IsEmpty(graph.assemblyReferences, "Generated constants must not depend on Framework/Editor/consumers.");
            Assert.IsFalse(graph.compiledAssemblyReferences.Any(x => Path.GetFileName(x).StartsWith("UnityEngine") ||
                                                                   Path.GetFileName(x).StartsWith("UnityEditor")));
            Assert.AreEqual(ConsumerAssembly, Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(root + "/Consumer/Explicit.cs")));
            Assert.AreEqual("Assembly-CSharp", Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(root + "/Predefined.cs")));
            var consumerGraph = CompilationPipeline.GetAssemblies().Single(x => x.name == ConsumerAssembly);
            CollectionAssert.AreEqual(new[] { GeneratedAssembly }, consumerGraph.assemblyReferences.Select(assembly => assembly.name).ToArray());
            CollectionAssert.AreEqual(new[] { root + "/Consumer/Explicit.cs" }, consumerGraph.sourceFiles.Select(x => x.Replace('\\', '/')).ToArray());
            CollectionAssert.Contains(CompilationPipeline.GetAssemblies().Single(x => x.name == "Assembly-CSharp").sourceFiles
                .Select(x => x.Replace('\\', '/')).ToArray(), root + "/Predefined.cs");

            var path = Output + "/YLResourceReferences.cs";
            var asmdef = Output + "/" + GeneratedAssembly + ".asmdef";
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
            var asmMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef + ".meta"));
            var folderMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta"));
            var time = File.GetLastWriteTimeUtc(path);
            RequireOwned();
            Assert.IsFalse(ResourceCodeGenerator.GenerateReferences(settings, Output));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.AreEqual(time, File.GetLastWriteTimeUtc(path));
            var mutableInputs = new[] { AssetDatabase.GetAssetPath(settings), AssetDatabase.GetAssetPath(settings.DefaultGroup) }
                .Select(PhysicalInputPath).ToHashSet(StringComparer.Ordinal);
            Assert.That(mutableInputs, Has.Count.EqualTo(2));
            CollectionAssert.AreEquivalent(mutableInputs,
                Directory.GetFiles(PhysicalInputPath(root), "*", SearchOption.AllDirectories).Select(PhysicalInputPath).Where(mutableInputs.Contains),
                "The mutable whitelist must identify exactly the persisted Settings and Group, not their metas or relative path aliases.");
            var protectedInputs = ProtectedInputPaths().ToDictionary(input => input, File.ReadAllBytes, StringComparer.Ordinal);

            // AssetDatabase uses '/', while Directory enumeration uses native separators. Compare one file identity on both sides.
            string PhysicalInputPath(string input) => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(input));

            string[] ProtectedInputPaths() => Directory.GetFiles(PhysicalInputPath(root), "*", SearchOption.AllDirectories).Select(PhysicalInputPath)
                .Where(input => !mutableInputs.Contains(input)).Concat(new[] { PhysicalInputPath(root + ".meta") })
                .OrderBy(input => input, StringComparer.Ordinal).ToArray();

            void AssertInputsUnchanged()
            {
                CollectionAssert.AreEquivalent(protectedInputs.Keys, ProtectedInputPaths(), "Only the owned Settings and Group may change when authoring a new label.");
                foreach (var input in protectedInputs)
                    CollectionAssert.AreEqual(input.Value, File.ReadAllBytes(input.Key), input.Key);
            }

            // 加入真实的规范化碰撞，公共入口必须在写入之前拒绝，且旧有效产物仍可用。
            var secondaryInput = settings.DefaultGroup.entries.Single(value => value.address == "secondary/item");
            secondaryInput.SetAddress("entry_txt", false);
            Assert.Throws<InvalidOperationException>(() => ResourceCodeGenerator.GenerateReferences(settings, Output));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            secondaryInput.SetAddress("secondary/item", false);
            settings.AddLabel("new-label", false);
            foreach (var entry in settings.DefaultGroup.entries)
                entry.SetLabel("new-label", true, false, false);
            ResourceAddressablesGeneratorFixture.Save(settings);
            AssertInputsUnchanged();
            RecordOwned();
            RequireOwned();
            using (var locked = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => ResourceCodeGenerator.GenerateReferences(settings, Output));
            }

            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.IsTrue(ResourceCodeGenerator.GenerateReferences(settings, Output));
            AssertInputsUnchanged();
            RecordOwned();
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            CollectionAssert.AreEqual(asmMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef + ".meta")));
            CollectionAssert.AreEqual(folderMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")));
            Assert.IsFalse(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Output)).Any(x => Path.GetFileName(x).StartsWith(".yl-names-")));
        }

        private static IEnumerator Prepare()
        {
            Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")), "Do not take over existing generated/user assets.");
            var root = ResourceAddressablesGeneratorFixture.InputPrefix + Guid.NewGuid().ToString("N");
            ResourceAddressablesGeneratorFixture.CreateInputs(root);
            var inputFingerprint = ResourceAddressablesGeneratorFixture.Fingerprint(root);
            bool prepared = false;
            try
            {
                yield return ResourceGeneratorTestWait.Clear("Import real Addressables generator inputs");
                Assert.That(ResourceAddressablesGeneratorFixture.Fingerprint(root), Is.EqualTo(inputFingerprint));
                using var fixture = new ResourceAddressablesGeneratorFixture(root, new[] { "entry.txt", "secondary/item" }, new[] { "stable-label" }, persisted: true);
                inputFingerprint = ResourceAddressablesGeneratorFixture.Fingerprint(root);
                AssetDatabase.CreateFolder(root, "Consumer");
                inputFingerprint = ResourceAddressablesGeneratorFixture.Fingerprint(root);
                yield return ResourceGeneratorTestWait.Clear("Prepare consumer folder before resource generation");
                Assert.That(ResourceAddressablesGeneratorFixture.Fingerprint(root), Is.EqualTo(inputFingerprint));
                const string Full = "YLFramework.Resources.ResourceNames";
                var body = "public static string Address() => " + Full + ".Addresses.EntryTxt;" +
                    "public static string SecondaryAddress() => " + Full + ".Addresses.SecondaryItem;" +
                    "public static string Label() => " + Full + ".Labels.StableLabel;";
                Assert.IsTrue(ResourceCodeGenerator.GenerateReferences(fixture.Settings, Output));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Consumer/Explicit.cs"), "namespace YLFramework.Tests.GeneratedConsumer82c { public static class Explicit { " + body + " } }");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Predefined.cs"), "namespace YLFramework.Tests.GeneratedConsumer82c { public static class Predefined { " + body + " } }");
                var consumerAssembly = "YLFramework.Tests.ResourceNamesConsumer63." + Path.GetFileName(root).Replace("YLGeneratorInputs-", "");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Consumer/Consumer.asmdef"), "{\"name\":\"" + consumerAssembly + "\",\"references\":[\"" +
                    GeneratedAssembly + "\"],\"autoReferenced\":false,\"overrideReferences\":true,\"precompiledReferences\":[],\"noEngineReferences\":true}");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                // Only a complete output authorizes recovery after a Domain Reload; preparation failures remain owned by the live fixture.
                SessionState.SetString(Key, root);
                RecordOwned();
                prepared = true;
            }
            finally
            {
                if (!prepared)
                {
                    ResourceAddressablesGeneratorFixture.DeleteInputs(root, inputFingerprint);
                }
            }
        }

        private static string Fingerprint()
        {
            var root = SessionState.GetString(Key, "");
            var files = new[] { root, Output }.SelectMany(directory => Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(directory), "*", SearchOption.AllDirectories)
                .Concat(new[] { directory + ".meta" })).OrderBy(path => path, StringComparer.Ordinal);
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", files.Select(path =>
                path + ":" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)))))))));
        }

        private static void RecordOwned() => SessionState.SetString(Key + ".Fingerprint", Fingerprint());

        private static void RequireOwned()
        {
            var root = SessionState.GetString(Key, "");
            Assert.AreEqual(TestContext.CurrentContext.Test.FullName, SessionState.GetString(Key + ".OutputOwner", ""),
                "Only the original test may validate or clean up this probe.");
            Assert.IsTrue(root.StartsWith(ResourceAddressablesGeneratorFixture.InputPrefix, StringComparison.Ordinal) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)));
            Assert.IsTrue(Output.StartsWith("Assets/Tests/YLFramework/Fixtures/ResourceNamesCompile-", StringComparison.Ordinal));
            Assert.IsTrue(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)), "Generated output disappeared; refuse to take over replacement assets.");
            Assert.AreEqual(SessionState.GetString(Key + ".Fingerprint", ""), Fingerprint(),
                "Probe assets or generated identities changed outside this run; refuse cleanup.");
        }

        private static void ClearState()
        {
            SessionState.EraseString(Key);
            SessionState.EraseString(Key + ".Fingerprint");
            SessionState.EraseString(Key + ".Output");
            SessionState.EraseString(Key + ".OutputOwner");
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            var root = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(root))
            {
                if (!string.IsNullOrEmpty(Output))
                {
                    Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")));
                }
                ClearState();
                yield break;
            }
            // 仅清理本探针在开始时确认不存在的位置，不触及 Bundle 输出、游戏或用户源码。
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
            {
                RequireOwned();
                Assert.IsTrue(AssetDatabase.DeleteAsset(root));
                Assert.IsTrue(AssetDatabase.DeleteAsset(Output));
            }
            else
            {
                Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)), "Never delete a recreated output after interrupted cleanup.");
            }

            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Remove compiled resource probe");
            Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key, ""))) ||
                Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)));
            Assert.IsFalse(EditorUtility.scriptCompilationFailed);
            ClearState();
        }
    }
}

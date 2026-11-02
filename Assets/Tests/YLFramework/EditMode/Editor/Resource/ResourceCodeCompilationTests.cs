using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.ResourceBuild;

namespace YLFramework.Tests.ResourceEditor
{
    [Category("YL.UPM.Generators")]
    public class ResourceCodeCompilationTests
    {
        private const string Key = "YL.Resource82c.CompilationProbe";
        private static string Output => SessionState.GetString(Key + ".Output", "");
        private const string GeneratedAssembly = "YLFramework.Generated.ResourceNames";
        private static string ConsumerAssembly => "YLFramework.Tests.ResourceNamesConsumer63." +
              Path.GetFileName(SessionState.GetString(Key, "")).Replace("Release82b-", "");

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
            var root = "Assets/Tests/YLFramework/Fixtures/Release82b-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Output));
            try
            {
                SessionState.SetString(Key, root);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output + "/User.cs"), "// independently recreated user source");
                Assert.Throws<AssertionException>(() => Cleanup().MoveNext());
                Assert.AreEqual("// independently recreated user source", File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output + "/User.cs")));

                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
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
            yield return ResourceGeneratorTestWait.Clear("Compile updated generated aliases");
            Assert.IsFalse(EditorUtility.scriptCompilationFailed);
            var generated = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == GeneratedAssembly);
            var name = "YLFramework.Resources." + SessionState.GetString(Key + ".Class", "") + "+Aliases";
            Assert.AreEqual("new-alias", generated.GetType(name, true).GetField("NewAlias").GetRawConstantValue());
        }

        // Test Framework 恢复 iterator 时不保留跨 Domain Reload 的闭包对象，验证局部值在普通方法中重建。
        private static void VerifyCompiledAndUpdate()
        {
            var root = SessionState.GetString(Key, "");
            Assert.IsFalse(EditorUtility.scriptCompilationFailed);
            var generated = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == GeneratedAssembly);
            var catalog = Catalog(root);
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(root + "/Project.asset");
            var simulation = new ResourceEditorSimulation(new[] { project }, new[] { catalog });
            foreach (var assemblyName in new[] { ConsumerAssembly, "Assembly-CSharp" })
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == assemblyName);
                var consumer = assembly.GetType("YLFramework.Tests.GeneratedConsumer82c." +
                     (assemblyName == ConsumerAssembly ? "Explicit" : "Predefined"), true);
                var address = (ResourceAddress)consumer.GetMethod("Address").Invoke(null, null);
                Assert.AreEqual("entry.txt", address.Path);
                Assert.AreEqual("first release content", simulation.Load<TextAsset>(address).text);
                var sub = (ResourceAddress)consumer.GetMethod("Sprite").Invoke(null, null);
                Assert.AreEqual("Left", simulation.Load<Sprite>(sub).name);
                Assert.AreEqual("entry", consumer.GetMethod("Group").Invoke(null, null));
                Assert.AreEqual("stable-alias", consumer.GetMethod("Alias").Invoke(null, null));
                Assert.AreEqual(simulation.Resolve(address).AssetId, simulation.GetGroup(project.projectId, "entry").Single().AssetId);
            }
            var graph = CompilationPipeline.GetAssemblies().Single(x => x.name == GeneratedAssembly);
            CollectionAssert.AreEqual(new[] { Output + "/YLResourceReferences.cs" }, graph.sourceFiles.Select(x => x.Replace('\\', '/')).ToArray());
            Assert.IsEmpty(graph.assemblyReferences, "Generated constants must not depend on Framework/Editor/consumers.");
            Assert.IsFalse(graph.compiledAssemblyReferences.Any(x => Path.GetFileName(x).StartsWith("UnityEngine") ||
                                                                   Path.GetFileName(x).StartsWith("UnityEditor")));
            Assert.AreEqual(ConsumerAssembly, Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(root + "/Consumer/Explicit.cs")));
            Assert.AreEqual("Assembly-CSharp", Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(root + "/Predefined.cs")));
            var consumerGraph = CompilationPipeline.GetAssemblies().Single(x => x.name == ConsumerAssembly);
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
            Assert.IsFalse(ResourceCodeGenerator.GenerateReferences(new[] { catalog }, Output));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.AreEqual(time, File.GetLastWriteTimeUtc(path));

            // 加入真实的规范化碰撞，公共入口必须在写入之前拒绝，且旧有效产物仍可用。
            var data = JObject.Parse(Encoding.UTF8.GetString(ResourceCatalogCodec.Write(catalog)));
            data.Remove("BuildId");
            data["Assets"].First(x => (string)x["Address"] == "A.prefab")["Address"] = "entry_txt";
            var collision = ResourceCodeGenerationTests.Read(data);
            Assert.Throws<InvalidOperationException>(() => ResourceCodeGenerator.GenerateReferences(new[] { collision }, Output));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));

            data = JObject.Parse(Encoding.UTF8.GetString(ResourceCatalogCodec.Write(catalog)));
            data.Remove("BuildId");
            data["Assets"].First(x => (string)x["Address"] == "entry.txt")["Aliases"] = new JArray("new-alias", "stable-alias");
            var updated = ResourceCodeGenerationTests.Read(data);
            RequireOwned();
            using (var locked = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => ResourceCodeGenerator.GenerateReferences(new[] { updated }, Output));
            }

            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.IsTrue(ResourceCodeGenerator.GenerateReferences(new[] { updated }, Output));
            RecordOwned();
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            CollectionAssert.AreEqual(asmMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef + ".meta")));
            CollectionAssert.AreEqual(folderMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")));
            Assert.IsFalse(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Output)).Any(x => Path.GetFileName(x).StartsWith(".yl-names-")));
        }

        private static ResourceCatalogDocument Catalog(string root) => ResourceCatalogCodec.Read(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/catalog.json")),
            SessionState.GetString(Key + ".Project", ""), EditorUserBuildSettings.activeBuildTarget.ToString(), Application.unityVersion, "fixture-v1");

        private static IEnumerator Prepare()
        {
            Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")), "Do not take over existing generated/user assets.");
            var fixture = new ResourceReleaseFixture();
            bool prepared = false;
            try
            {
                var root = fixture.Folder;
                yield return ResourceGeneratorTestWait.Clear("Import release fixture before its build");
                var project = fixture.Project;
                ResourceEditorSimulationTests.Aliases(project.groups[0].selections[0], "stable-alias");
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
                yield return ResourceGeneratorTestWait.Clear("Save release authoring data before its build");
                var result = fixture.Build();
                var catalog = ResourceCatalogBuilder.Create(result, "fixture-v1");
                fixture.AssertProtected();
                // 编译消费只需 immutable Catalog 和源夹具，不保留这个测试已完整验证的私有 Workspace。
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(result.WorkspacePath)), true);
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/catalog.json"), ResourceCatalogCodec.Write(catalog));
                var generatedSource = (string)ResourceEditorTests.Invoke(typeof(ResourceCodeGenerator), "GenerateCatalogs", new[] { catalog }, "YLFramework.Resources");
                var declaration = System.Text.RegularExpressions.Regex.Match(generatedSource, @"(?m)^    public static class (?<name>[A-Za-z_][A-Za-z0-9_]*)$");
                Assert.That(declaration.Success, Is.True);
                var type = declaration.Groups["name"].Value;
                AssetDatabase.CreateFolder(root, "Consumer");
                yield return ResourceGeneratorTestWait.Clear("Prepare consumer folder before resource generation");
                var full = "YLFramework.Resources." + type;
                var body = "public static YLFramework.Resource.ResourceAddress Address() => new YLFramework.Resource.ResourceAddress(" + full + ".ProjectId," + full + ".Addresses.EntryTxt);" +
                    "public static YLFramework.Resource.ResourceAddress Sprite() => new YLFramework.Resource.ResourceAddress(" + full + ".ProjectId," + full + ".SubAssets.SpritesPngLeft.Address," + full + ".SubAssets.SpritesPngLeft.Key);" +
                    "public static string Group() => " + full + ".Groups.Entry; public static string Alias() => " + full + ".Aliases.StableAlias;";
                Assert.IsTrue(ResourceCodeGenerator.GenerateReferences(new[] { catalog }, Output));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Consumer/Explicit.cs"), "namespace YLFramework.Tests.GeneratedConsumer82c { public static class Explicit { " + body + " } }");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Predefined.cs"), "namespace YLFramework.Tests.GeneratedConsumer82c { public static class Predefined { " + body + " } }");
                var consumerAssembly = "YLFramework.Tests.ResourceNamesConsumer63." + Path.GetFileName(root).Replace("Release82b-", "");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Consumer/Consumer.asmdef"), "{\"name\":\"" + consumerAssembly + "\",\"references\":[\"" +
                    GeneratedAssembly + "\",\"YLFramework.Runtime\"],\"autoReferenced\":false,\"overrideReferences\":true,\"precompiledReferences\":[],\"noEngineReferences\":true}");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                // Only a complete output authorizes recovery after a Domain Reload; preparation failures remain owned by the live fixture.
                SessionState.SetString(Key, root);
                SessionState.SetString(Key + ".Project", catalog.ProjectId);
                SessionState.SetString(Key + ".Class", type);
                RecordOwned();
                prepared = true;
            }
            finally
            {
                if (!prepared)
                {
                    fixture.Dispose();
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
            Assert.IsTrue(root.StartsWith("Assets/Tests/YLFramework/Fixtures/Release82b-", StringComparison.Ordinal) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)));
            Assert.IsTrue(Output.StartsWith("Assets/Tests/YLFramework/Fixtures/ResourceNamesCompile-", StringComparison.Ordinal));
            Assert.IsTrue(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)), "Generated output disappeared; refuse to take over replacement assets.");
            Assert.AreEqual(SessionState.GetString(Key + ".Fingerprint", ""), Fingerprint(),
                "Probe assets or generated identities changed outside this run; refuse cleanup.");
        }

        private static void ClearState()
        {
            SessionState.EraseString(Key);
            SessionState.EraseString(Key + ".Fingerprint");
            SessionState.EraseString(Key + ".Project");
            SessionState.EraseString(Key + ".Class");
            SessionState.EraseString(Key + ".Output");
            SessionState.EraseString(Key + ".OutputOwner");
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            var root = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(root))
            {
                Assert.IsFalse(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Output + ".meta")));
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

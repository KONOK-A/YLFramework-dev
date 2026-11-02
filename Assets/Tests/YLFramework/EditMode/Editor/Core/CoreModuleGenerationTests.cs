using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using YLFramework.Editor.Core;

namespace YLFramework.Core.Editor.Tests
{
    [Category("YL.UPM.Generators")]
    public class CoreModuleGenerationTests
    {
        private string[] _absentParents;

        [SetUp]
        public void CaptureUnownedOutputParents()
        {
            _absentParents = new[] { CreateModuleCommand.OutputRoot, "Assets/YLFrameworkData/Generated", "Assets/YLFrameworkData" }
                .Where(path => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();
        }

        [TearDown]
        public void RemoveOnlyNewEmptyOutputParents()
        {
            foreach (string path in _absentParents)
            {
                if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Length != 0)
                {
                    continue;
                }

                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")))
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
                }
            }
        }

        [TestCase("SingletonModule")]
        [TestCase("UnityEngine")]
        public void TemplateQualifiesFrameworkAndUnityTypesForLegalShadowingNames(string name)
        {
            var field = typeof(CreateModuleCommand).GetField("ModuleTemplate", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            // Inspect in memory; actual file generation must retain a unique random directory name.
            string source = ((string)field.GetRawConstantValue()).Replace("{ModuleName}", name);
            Assert.That(source, Does.Contain("class " + name + " : global::YLFramework.Core.SingletonModule<" + name + ">"));
            Assert.That(source, Does.Contain("global::UnityEngine.Debug.Log"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ModuleUsesIndependentNamespaceAssemblyAndEscapedLiteralData(bool baseModule)
        {
            string name = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string directory = CreateModuleCommand.OutputRoot + "/" + name;
            const string Project = "quote\" slash\\\r\n\t\0{BaseModule}{ModuleName}{ProjectName}{DefaultStartUpScene}";
            const string Scene = "scene\"; injected(); //\\\n{ModuleName}{BaseModule}{DefaultStartUpScene}{ProjectName}";
            try
            {
                Assert.That(CreateModuleCommand.OutputRoot, Is.EqualTo("Assets/YLFrameworkData/Generated/Modules"));
                CreateModuleCommand.CreateModule(name, Project, baseModule, Scene);
                Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(directory)).Select(Path.GetFileName), Is.EquivalentTo(new[]
                {
                    name + ".cs", name + ".asmdef"
                }));
                string source = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(directory + "/" + name + ".cs"));
                Assert.That(source, Does.Contain("using YLFramework.Core;"));
                Assert.That(source, Does.Contain("namespace YLFramework.Core.Examples"));
                Assert.That(source, Does.Contain("class " + name + " : global::YLFramework.Core.SingletonModule<" + name + ">"));
                Assert.That(source, Does.Contain("global::UnityEngine.Debug.Log"));
                Assert.That(source, Does.Contain("ProjectName => \"quote\\\" slash\\\\\\r\\n\\t\\u0000{BaseModule}{ModuleName}{ProjectName}{DefaultStartUpScene}\";"));
                Assert.That(source, Does.Contain("DefaultStartUpScene => \"scene\\\"; injected(); //\\\\\\n{ModuleName}{BaseModule}{DefaultStartUpScene}{ProjectName}\";"));
                Assert.That(source, Does.Contain("IsBaseModule => " + (baseModule ? "true" : "false") + ";"));
                Assert.That(System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^using\s+([^;]+);")
                    .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value),
                    Is.EqualTo(new[] { "YLFramework.Core" }));
                Assert.That(source, Does.Not.Contain("namespace Project.Game"));
                Assert.That(source, Does.Not.Contain("OnExit"));
                Assert.That(source, Does.Not.Contain("ClassObjectPoolManager"));
                Assert.That(source, Does.Not.Contain("AllModules"));
                Assert.That(source, Does.Not.Contain("AllBaseModules"));
                var definition = CoreEditorIdentityTests.ReadDefinition(directory + "/" + name + ".asmdef");
                Assert.That(definition.name, Is.EqualTo("YLFramework.Core.Examples." + name));
                Assert.That(definition.rootNamespace, Is.EqualTo(CreateModuleCommand.ExampleNamespace));
                Assert.That(definition.references, Is.EqualTo(new[] { "YLFramework.Runtime" }));
                Assert.That(definition.autoReferenced, Is.False);
                Assert.That(definition.includePlatforms ?? new string[0], Is.Empty);
            }
            finally
            {
                DeleteOwnedDirectory(directory);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        public void EmptyDefaultSceneGeneratesStringEmpty(string scene)
        {
            string name = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string directory = CreateModuleCommand.OutputRoot + "/" + name;
            try
            {
                CreateModuleCommand.CreateModule(name, "YLCoreEditorTestProject", false, scene);
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(directory + "/" + name + ".cs")),
                    Does.Contain("DefaultStartUpScene => string.Empty;"));
            }
            finally
            {
                DeleteOwnedDirectory(directory);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("class")]
        [TestCase("namespace")]
        [TestCase("__arglist")]
        [TestCase("__makeref")]
        [TestCase("__reftype")]
        [TestCase("__refvalue")]
        [TestCase("9Module")]
        [TestCase("Module Name")]
        [TestCase("../Escape")]
        [TestCase("..\\Escape")]
        [TestCase("Assets/Games/Injected")]
        [TestCase("C:\\Injected")]
        [TestCase("Module\n")]
        [TestCase("OnStart")]
        [TestCase("ProjectName")]
        [TestCase("IsBaseModule")]
        [TestCase("DefaultStartUpScene")]
        public void IllegalNamesPathsAndGeneratedMemberNamesAreRejectedWithoutWriting(string name)
        {
            var before = SnapshotOutputFiles();
            Assert.Throws<ArgumentException>(() => CreateModuleCommand.CreateModule(name, "YLCoreEditorTestProject", false, ""));
            Assert.That(SnapshotOutputFiles(), Is.EqualTo(before));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t\r\n")]
        public void EmptyProjectNameIsRejectedBeforeCreatingOutput(string project)
        {
            string name = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string directory = CreateModuleCommand.OutputRoot + "/" + name;
            try
            {
                Assert.Throws<ArgumentException>(() => CreateModuleCommand.CreateModule(name, project, false, ""));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory)), Is.False);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory)), Is.False);
            }
            finally
            {
                DeleteOwnedDirectory(directory);
            }
        }

        [Test]
        public void RepeatedGenerationRefusesToOverwriteSourceAssemblyOrMetadata()
        {
            string name = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string directory = CreateModuleCommand.OutputRoot + "/" + name;
            try
            {
                CreateModuleCommand.CreateModule(name, "YLCoreEditorTestProject", false, "FirstScene");
                string source = directory + "/" + name + ".cs";
                string assembly = directory + "/" + name + ".asmdef";
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta"), "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(assembly + ".meta"), "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                byte[] sourceBefore = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source));
                byte[] assemblyBefore = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly));
                byte[] sourceMetaBefore = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta"));
                byte[] assemblyMetaBefore = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly + ".meta"));
                DateTime sourceTime = File.GetLastWriteTimeUtc(source);
                DateTime assemblyTime = File.GetLastWriteTimeUtc(assembly);
                Assert.Throws<IOException>(() => CreateModuleCommand.CreateModule(name, "ChangedProject", true, "ChangedScene"));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source)), Is.EqualTo(sourceBefore));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly)), Is.EqualTo(assemblyBefore));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta")), Is.EqualTo(sourceMetaBefore));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly + ".meta")), Is.EqualTo(assemblyMetaBefore));
                Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(sourceTime));
                Assert.That(File.GetLastWriteTimeUtc(assembly), Is.EqualTo(assemblyTime));
            }
            finally
            {
                DeleteOwnedDirectory(directory);
            }
        }

        [TestCase("Assets/YLFramework/Examples/Core/Modules")]
        [TestCase("Assets/RelocatedYLModules")]
        public void ExistingAssemblyAtLegacyOrRelocatedPathRejectsBeforeCreatingNewOutput(string originalRoot)
        {
            string name = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string original = originalRoot + "/" + name;
            string target = CreateModuleCommand.OutputRoot + "/" + name;
            var absentParents = new System.Collections.Generic.List<string>();
            for (var parent = originalRoot; parent != "Assets"; parent = Path.GetDirectoryName(parent).Replace('\\', '/'))
            {
                if (!Directory.Exists(parent) && !File.Exists(parent) && !File.Exists(parent + ".meta"))
                {
                    absentParents.Add(parent);
                }
            }
            bool hadOutputRoot = Directory.Exists(CreateModuleCommand.OutputRoot);
            var outputBefore = SnapshotOutputFiles();
            Assert.That(Directory.Exists(original) || File.Exists(original) || File.Exists(original + ".meta"), Is.False);
            Assert.That(Directory.Exists(target) || File.Exists(target) || File.Exists(target + ".meta"), Is.False);
            try
            {
                Directory.CreateDirectory(original);
                string source = original + "/" + name + ".cs";
                string assembly = original + "/" + name + ".asmdef";
                File.WriteAllText(source, "// Existing project-owned module source.\n");
                File.WriteAllText(assembly, "{\"name\":\"YLFramework.Core.Examples." + name + "\",\"rootNamespace\":\"YLFramework.Core.Examples\",\"references\":[\"YLFramework.Runtime\"],\"autoReferenced\":false}");
                AssetDatabase.ImportAsset(original, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
                Assert.That(AssetDatabase.LoadAssetAtPath<UnityEditorInternal.AssemblyDefinitionAsset>(assembly), Is.Not.Null);
                var paths = new[] { source, assembly, source + ".meta", assembly + ".meta", original + ".meta" };
                var bytes = paths.Select(File.ReadAllBytes).ToArray();
                var times = paths.Select(File.GetLastWriteTimeUtc).ToArray();

                var error = Assert.Throws<IOException>(() => CreateModuleCommand.CreateModule(name, "ChangedProject", true, "ChangedScene"));

                Assert.That(error.Message, Does.Contain(assembly));
                Assert.That(Directory.Exists(target) || File.Exists(target) || File.Exists(target + ".meta"), Is.False);
                Assert.That(Directory.Exists(CreateModuleCommand.OutputRoot), Is.EqualTo(hadOutputRoot));
                Assert.That(SnapshotOutputFiles(), Is.EqualTo(outputBefore));
                for (int index = 0; index < paths.Length; index++)
                {
                    Assert.That(File.ReadAllBytes(paths[index]), Is.EqualTo(bytes[index]), paths[index]);
                    Assert.That(File.GetLastWriteTimeUtc(paths[index]), Is.EqualTo(times[index]), paths[index]);
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(original);
                DeleteOwnedDirectory(original);
                DeleteOwnedDirectory(target);
                foreach (var parent in absentParents)
                {
                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        AssetDatabase.DeleteAsset(parent);
                        DeleteOwnedDirectory(parent);
                    }
                }
            }
        }

        [Test]
        public void LoadedTypeIdentityRejectsBeforeCreatingAnyOutputAndIdentifiesItsAssembly()
        {
            const string Name = "ModuleGenerationLoadedIdentityProbe";
            string target = CreateModuleCommand.OutputRoot + "/" + Name;
            bool hadOutputRoot = Directory.Exists(CreateModuleCommand.OutputRoot);
            var before = SnapshotOutputFiles();
            Assert.That(Directory.Exists(target) || File.Exists(target) || File.Exists(target + ".meta"), Is.False);
            try
            {
                var error = Assert.Throws<IOException>(() => CreateModuleCommand.CreateModule(Name, "Project", false, ""));
                Assert.That(error.Message, Does.Contain(typeof(global::YLFramework.Core.Examples.ModuleGenerationLoadedIdentityProbe).Assembly.Location));
                Assert.That(Directory.Exists(target) || File.Exists(target) || File.Exists(target + ".meta"), Is.False);
                Assert.That(Directory.Exists(CreateModuleCommand.OutputRoot), Is.EqualTo(hadOutputRoot));
                Assert.That(SnapshotOutputFiles(), Is.EqualTo(before));
            }
            finally
            {
                DeleteOwnedDirectory(target);
            }
        }

        [TestCase("directory")]
        [TestCase("file")]
        [TestCase("orphanMeta")]
        public void ExistingOutputOrOrphanMetadataIsNeverOverwritten(string existingKind)
        {
            string name = "YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            string directory = CreateModuleCommand.OutputRoot + "/" + name;
            string sentinel = existingKind == "orphanMeta" ? directory + ".meta" : directory;
            try
            {
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(CreateModuleCommand.OutputRoot));
                if (existingKind == "directory")
                {
                    Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
                    sentinel += "/owned-test-sentinel.txt";
                }
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(sentinel), "YL test owns this sentinel.");
                byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sentinel));
                DateTime timestamp = File.GetLastWriteTimeUtc(sentinel);
                Assert.Throws<IOException>(() => CreateModuleCommand.CreateModule(name, "YLCoreEditorTestProject", false, ""));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sentinel)), Is.EqualTo(before));
                Assert.That(File.GetLastWriteTimeUtc(sentinel), Is.EqualTo(timestamp));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory + "/" + name + ".cs")), Is.False);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory + "/" + name + ".asmdef")), Is.False);
            }
            finally
            {
                DeleteOwnedDirectory(directory);
            }
        }

        private static string[] SnapshotOutputFiles()
        {
            return Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(CreateModuleCommand.OutputRoot))
                ? Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(CreateModuleCommand.OutputRoot), "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray()
                : new string[0];
        }

        private static void DeleteOwnedDirectory(string directory)
        {
            // These synchronous tests never refresh/import, so generated scripts cannot compile before cleanup.
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(directory), true);
            }

            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory)))
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
            }

            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory + ".meta")))
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(directory + ".meta"));
            }
        }
    }
}

namespace YLFramework.Core.Examples
{
    internal sealed class ModuleGenerationLoadedIdentityProbe
    {
    }
}

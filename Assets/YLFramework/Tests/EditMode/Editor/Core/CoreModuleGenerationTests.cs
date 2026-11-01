using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Editor.Core;

namespace YLFramework.Core.Editor.Tests
{
    public class CoreModuleGenerationTests
    {
        private string[] _absentParents;

        [SetUp]
        public void CaptureUnownedOutputParents()
        {
            _absentParents = new[] { CreateModuleCommand.OutputRoot, "Assets/YLFramework/Examples/Core", "Assets/YLFramework/Examples" }
                .Where(path => !Directory.Exists(path) && !File.Exists(path) && !File.Exists(path + ".meta")).ToArray();
        }

        [TearDown]
        public void RemoveOnlyNewEmptyOutputParents()
        {
            foreach (string path in _absentParents)
            {
                if (!Directory.Exists(path) || Directory.GetFileSystemEntries(path).Length != 0)
                {
                    continue;
                }

                Directory.Delete(path);
                if (File.Exists(path + ".meta"))
                {
                    File.Delete(path + ".meta");
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
                Assert.That(CreateModuleCommand.OutputRoot, Is.EqualTo("Assets/YLFramework/Examples/Core/Modules"));
                CreateModuleCommand.CreateModule(name, Project, baseModule, Scene);
                Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName), Is.EquivalentTo(new[]
                {
                    name + ".cs", name + ".asmdef"
                }));
                string source = File.ReadAllText(directory + "/" + name + ".cs");
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
                Assert.That(File.ReadAllText(directory + "/" + name + ".cs"),
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
                Assert.That(Directory.Exists(directory), Is.False);
                Assert.That(File.Exists(directory), Is.False);
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
                File.WriteAllText(source + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                File.WriteAllText(assembly + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                byte[] sourceBefore = File.ReadAllBytes(source);
                byte[] assemblyBefore = File.ReadAllBytes(assembly);
                byte[] sourceMetaBefore = File.ReadAllBytes(source + ".meta");
                byte[] assemblyMetaBefore = File.ReadAllBytes(assembly + ".meta");
                DateTime sourceTime = File.GetLastWriteTimeUtc(source);
                DateTime assemblyTime = File.GetLastWriteTimeUtc(assembly);
                Assert.Throws<IOException>(() => CreateModuleCommand.CreateModule(name, "ChangedProject", true, "ChangedScene"));
                Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBefore));
                Assert.That(File.ReadAllBytes(assembly), Is.EqualTo(assemblyBefore));
                Assert.That(File.ReadAllBytes(source + ".meta"), Is.EqualTo(sourceMetaBefore));
                Assert.That(File.ReadAllBytes(assembly + ".meta"), Is.EqualTo(assemblyMetaBefore));
                Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(sourceTime));
                Assert.That(File.GetLastWriteTimeUtc(assembly), Is.EqualTo(assemblyTime));
            }
            finally
            {
                DeleteOwnedDirectory(directory);
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
                Directory.CreateDirectory(CreateModuleCommand.OutputRoot);
                if (existingKind == "directory")
                {
                    Directory.CreateDirectory(directory);
                    sentinel += "/owned-test-sentinel.txt";
                }
                File.WriteAllText(sentinel, "YL test owns this sentinel.");
                byte[] before = File.ReadAllBytes(sentinel);
                DateTime timestamp = File.GetLastWriteTimeUtc(sentinel);
                Assert.Throws<IOException>(() => CreateModuleCommand.CreateModule(name, "YLCoreEditorTestProject", false, ""));
                Assert.That(File.ReadAllBytes(sentinel), Is.EqualTo(before));
                Assert.That(File.GetLastWriteTimeUtc(sentinel), Is.EqualTo(timestamp));
                Assert.That(File.Exists(directory + "/" + name + ".cs"), Is.False);
                Assert.That(File.Exists(directory + "/" + name + ".asmdef"), Is.False);
            }
            finally
            {
                DeleteOwnedDirectory(directory);
            }
        }

        private static string[] SnapshotOutputFiles()
        {
            return Directory.Exists(CreateModuleCommand.OutputRoot)
                ? Directory.GetFileSystemEntries(CreateModuleCommand.OutputRoot, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray()
                : new string[0];
        }

        private static void DeleteOwnedDirectory(string directory)
        {
            // These synchronous tests never refresh/import, so generated scripts cannot compile before cleanup.
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            if (File.Exists(directory))
            {
                File.Delete(directory);
            }

            if (File.Exists(directory + ".meta"))
            {
                File.Delete(directory + ".meta");
            }
        }
    }
}

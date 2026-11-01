using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Common;
using YLFramework.Editor.Core;
using YLFramework.Editor.Foundation;

namespace YLFramework.Core.Editor.Tests
{
    [Serializable]
    internal sealed class CoreEditorAssemblyDefinition
    {
        public string name;
        public string rootNamespace;
        public string[] references;
        public string[] includePlatforms;
        public string[] excludePlatforms;
        public bool overrideReferences;
        public string[] precompiledReferences;
        public bool autoReferenced;
        public string[] defineConstraints;
    }

    public class CoreEditorIdentityTests
    {
        private const string EditorRoot = "Assets/YLFramework/Editor/Core";
        private const string EditorAssemblyName = "YLFramework.Editor";

        [TestCase("YLFramework.Editor.Core.UIPanelAssetMigration", "YLFramework.Core." + "Editor.UIPanelAssetMigration")]
        [TestCase("YLFramework.Editor.Common.EditorAssetTool", "YLFramework.Core." + "Editor.EditorAssetTool")]
        [TestCase("YLFramework.Editor.Common.AssetNameTools", "YLFramework.Core." + "Editor.AssetNameTools")]
        [TestCase("YLFramework.Editor.Foundation.DisableEditDrawer", "YLFramework.Core." + "Editor.DisableEditDrawer")]
        [TestCase("YLFramework.Editor.Foundation.SerializedDictionaryDrawer", "YLFramework.Editor." + "SerializedDictionaryDrawer")]
        [TestCase("YLFramework.Editor.Skill.SkillsConfigController", "YLFramework.Skill." + "SkillsConfigController")]
        [TestCase("YLFramework.Editor.StateMachine.FsmEditorWindow", "YLFramework.StateMachine." + "FsmEditorWindow")]
        public void EditorTypesUseOwnedScopesWithoutRuntimeNamespaceAliases(string fullName, string legacyName)
        {
            var assembly = System.Reflection.Assembly.Load(EditorAssemblyName);
            Assert.That(assembly.GetType(fullName), Is.Not.Null, fullName);
            Assert.That(assembly.GetType(legacyName), Is.Null, legacyName);
        }

        [Test]
        public void NeutralEditorHelpersKeepTheirGuidsAndSingleEditorOwnerAtTheDeclaredPaths()
        {
            const string Root = "Assets/YLFramework/Editor/";
            var moves = new[]
            {
                (fullName: "YLFramework.Editor.Foundation.DisableEditDrawer", oldPath: "Core/Inspectors/DisableEditDrawer.cs", newPath: "Foundation/Attributes/DisableEditDrawer.cs", guid: "fac117eb3216be84e9e4f3089011b84b"),
                (fullName: "YLFramework.Editor.Common.SerializedObjectExtensions", oldPath: "Utilities/Serialization/SerializedObjectExtensions.cs", newPath: "Common/Inspector/SerializedObjectExtensions.cs", guid: "f658c2a8eb0ce3d41a46c6612f44b294"),
                (fullName: "YLFramework.Editor.Common.SerializedPropertyExtensions", oldPath: "Utilities/Serialization/SerializedPropertyExtensions.cs", newPath: "Common/Inspector/SerializedPropertyExtensions.cs", guid: "75f4e5425829b58458510ac9994f3744"),
                (fullName: "YLFramework.Editor.Common.HandlesExtensions", oldPath: "Utilities/Scene/HandlesExtensions.cs", newPath: "Common/Scene/HandlesExtensions.cs", guid: "d7fc63baa0640114a99a6bbc20b1c847"),
                (fullName: "YLFramework.Editor.Common.AssetNameTools", oldPath: "Utilities/Assets/AssetNameTools.cs", newPath: "Common/Assets/AssetNameTools.cs", guid: "38fb61f5e49fe5d40b2013e5c8266e34"),
                (fullName: "YLFramework.Editor.Common.EditorAssetTool", oldPath: "Utilities/Assets/EditorAssetTool.cs", newPath: "Common/Assets/EditorAssetTool.cs", guid: "8b8ad24c1e834bb4e93a9237f2888f66"),
                (fullName: "YLFramework.Editor.Common.TreeViewStateGroup", oldPath: "Utilities/TreeViews/TreeViewStateGroup.cs", newPath: "Common/TreeViews/TreeViewStateGroup.cs", guid: "61cd0951782754842a7806170fc61254"),
                (fullName: "YLFramework.Editor.Common.ReorderableTreeView`1", oldPath: "Utilities/TreeViews/ReorderableTreeView.cs", newPath: "Common/TreeViews/ReorderableTreeView.cs", guid: "e787e4c31c9ddc5489ad3a6cd7e8b545"),
                (fullName: "YLFramework.Editor.Common.SplitWindow", oldPath: "Utilities/Windows/SplitWindow.cs", newPath: "Common/Windows/SplitWindow.cs", guid: "c9b6cf7bd9e787f48a839406ebcde184"),
                (fullName: "YLFramework.Editor.Common.ProjectSettingTools", oldPath: "Utilities/Settings/ProjectSettingTools.cs", newPath: "Common/Settings/ProjectSettingTools.cs", guid: "b265c367d455d4b4c81180460116cb3d"),
                (fullName: "YLFramework.Editor.Common.ProjectSettingsScriptableObject`1", oldPath: "Utilities/Settings/ProjectSettingsScriptableObject.cs", newPath: "Common/Settings/ProjectSettingsScriptableObject.cs", guid: "5c5731fb2ee10ba46b4c302aeb0c3717"),
                (fullName: "YLFramework.Editor.Tools.ClearLog", oldPath: "Utilities/Menus/ClearLog.cs", newPath: "Tools/ClearLog.cs", guid: "ffda0101de12441d9fb84fa9c264fc81"),
                (fullName: "YLFramework.Editor.Tools.OpenFolder", oldPath: "Utilities/Menus/OpenFolder.cs", newPath: "Tools/OpenFolder.cs", guid: "93238e2549504f9cac66e1ad2b041515"),
                (fullName: "YLFramework.Editor.Core.ScriptableObjectExtensions", oldPath: "Utilities/Assets/ScriptableObjectExtensions.cs", newPath: "Core/ScriptableObjectExtensions.cs", guid: "8f36b44478729c34486b79b7b1ce95f0"),
                (fullName: "YLFramework.Editor.Domain.DomainAssetOwnership", oldPath: "Utilities/Assets/DomainAssetOwnership.cs", newPath: "Systems/Shared/DomainAssetOwnership.cs", guid: "1ddd9cd019957054cbe3961a7ca6d7b5")
            };
            var assembly = typeof(CreateModuleCommand).Assembly;
            var player = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            var playerSources = player.SelectMany(a => a.sourceFiles).Select(Path.GetFullPath).ToArray();
            Assert.That(player.Any(a => a.name == EditorAssemblyName), Is.False);
            Assert.That(player.Single(a => a.name == "YLFramework.Runtime").assemblyReferences.Any(a => a.name == EditorAssemblyName), Is.False);
            foreach (var move in moves)
            {
                string path = Root + move.newPath;
                var type = assembly.GetType(move.fullName);
                Assert.That(File.Exists(path), Is.True, path);
                Assert.That(File.Exists(path + ".meta"), Is.True, path);
                Assert.That(File.Exists(Root + move.oldPath), Is.False, move.oldPath);
                Assert.That(File.Exists(Root + move.oldPath + ".meta"), Is.False, move.oldPath);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(move.guid), path);
                Assert.That(AssetDatabase.GUIDToAssetPath(move.guid), Is.EqualTo(path));
                Assert.That(type, Is.Not.Null, move.fullName);
                if (!type.IsGenericTypeDefinition)
                {
                    Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(path).GetClass(), Is.SameAs(type));
                }
                Assert.That(assembly.GetType("YLFramework.Core.Editor." + type.Name), Is.Null);
                Assert.That(assembly.GetType("YLFramework.Editor.Utilities." + type.Name), Is.Null);
                Assert.That(type.Assembly, Is.SameAs(assembly));
                Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType(move.fullName) != null), Is.EqualTo(1));
                Assert.That(CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(path), Is.EqualTo(Root + "YLFramework.Editor.asmdef"));
                Assert.That(playerSources.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase), Is.False);
            }
            Assert.That(assembly.GetType("YLFramework.Editor.Common.TreeViewStateInfo")?.Assembly, Is.SameAs(assembly));
            Assert.That(assembly.GetType("YLFramework.Editor.Common.PreviousProjectSettingsNameAttribute")?.Assembly, Is.SameAs(assembly));
            var folders = new[]
            {
                (path: "Common/Assets", guid: "134f6e674e0fea948af9f69a7c607018"),
                (path: "Common/Inspector", guid: "760e18b16495f14418b3e016e1fba13d"),
                (path: "Common/Scene", guid: "e8e1663f329484c43acc5ee1e88fd41d"),
                (path: "Common/Settings", guid: "a05fe4b74c0d3b141862e00280b14c50"),
                (path: "Common/TreeViews", guid: "93e05ab0300c5ef4fae056c40616e303"),
                (path: "Common/Windows", guid: "079e5b9ba993dac4382ea556745ab2eb"),
                (path: "Tools", guid: "89e050b838b27cc4fa18a131226b2cc5")
            };
            foreach (var folder in folders)
            {
                Assert.That(AssetDatabase.IsValidFolder(Root + folder.path), Is.True, folder.path);
                Assert.That(AssetDatabase.AssetPathToGUID(Root + folder.path), Is.EqualTo(folder.guid));
            }
            Assert.That(Directory.GetDirectories(Root + "Utilities").Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "Overlap", "CharacterController2D", "MoveablePlatform" }));
        }

        [UnityTest]
        public IEnumerator FormalRuntimeHasActualUnityPositiveAndNegativeAnalyzerWitnesses()
            => CompileAnalyzerWitnesses(AssemblyBuilderFlags.EditorAssembly);

        [UnityTest]
        public IEnumerator FormalRuntimeHasActualUnityPlayerPositiveAndNegativeAnalyzerWitnesses()
            => CompileAnalyzerWitnesses(AssemblyBuilderFlags.DevelopmentBuild);

        [UnityTest]
        public IEnumerator NewUsageRulesHaveActualUnityPositiveAndNegativeWitnesses()
            => CompileNewUsageWitnesses(AssemblyBuilderFlags.EditorAssembly);

        [UnityTest]
        public IEnumerator NewUsageRulesHaveActualUnityPlayerPositiveAndNegativeWitnesses()
            => CompileNewUsageWitnesses(AssemblyBuilderFlags.DevelopmentBuild);

        internal static IEnumerator CompileNewUsageWitnesses(AssemblyBuilderFlags flags)
        {
            var editor = (flags & AssemblyBuilderFlags.EditorAssembly) != 0;
            var runtime = CompilationPipeline.GetAssemblies(editor ? AssembliesType.Editor : AssembliesType.Player)
                .Single(assembly => assembly.name == "YLFramework.Runtime");
            var folder = Path.GetFullPath(Path.Combine("Temp", "YLFramework.Analyzers", "NewUnityRules-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var negative in new[] { true, false })
                {
                    var source = "using System.Collections;using System.Collections.Generic;using UnityEngine;using YLFramework.Core;using YLFramework.Audio;using YLFramework.DependencyInjection;class Probe{" +
                        (negative
                            ? "[Inject]public static object Bad;[field:Inject]static event System.Action Changed;void Load(Module owner){owner.LoadView<Panel>(\"name\",loadViewArgs:null);}IEnumerator Run(AudioPlayRequest request){yield return request;}"
                            : "[Inject]public object Dependency{get;private set;}[field:Inject]event System.Action Changed;void Load(Module owner){owner.LoadView<View>(\"name\",loadViewArgs:null);owner.LoadPanel<Panel>(\"name\");owner.GetView<Panel>(\"name\");owner.AttachView<Panel>(null);}IEnumerator Run(AudioPlayRequest request){yield return new WaitUntil(()=>request.HasStarted||request.IsDone);yield return new WaitUntil(()=>request.IsDone);}") +
                        "IEnumerable<AudioPlayRequest> Data(AudioPlayRequest request){yield return request;}}" +
                        (negative ? "record Consumer([field:Inject]object Service);" : "record Consumer([property:Inject]object Service);") +
                        "namespace System.Runtime.CompilerServices{public sealed class IsExternalInit{}}";
                    var path = Path.Combine(folder, negative ? "Negative.cs" : "Positive.cs");
                    var output = Path.ChangeExtension(path, ".dll");
                    File.WriteAllText(path, source);
                    var builder = new AssemblyBuilder(output, new[] { path })
                    {
                        flags = flags,
                        buildTarget = BuildTarget.StandaloneWindows64,
                        buildTargetGroup = BuildTargetGroup.Standalone,
                        referencesOptions = ReferencesOptions.UseEngineModules,
                        additionalReferences = new[] { typeof(Module).Assembly.Location },
                        compilerOptions = new ScriptCompilerOptions { RoslynAnalyzerDllPaths = runtime.compilerOptions.RoslynAnalyzerDllPaths }
                    };
                    builder.excludeReferences = builder.defaultReferences.Where(reference =>
                        Path.GetFileName(reference).StartsWith("YLFramework", StringComparison.Ordinal) ||
                        Path.GetFileName(reference).EndsWith("TestRunner.dll", StringComparison.Ordinal) ||
                        (!editor && Path.GetFileName(reference).StartsWith("UnityEditor", StringComparison.Ordinal))).ToArray();
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }
                    Assert.That(messages, Is.Not.Null);
                    Assert.That(messages.Where(message => message.type == CompilerMessageType.Error), Is.Empty,
                        string.Join("\n", messages.Select(message => message.message)));
                    Assert.That(messages.Any(message => message.message.Contains("AD0001") || message.message.Contains("CS8032")), Is.False);
                    var own = messages.Where(message => Regex.IsMatch(message.message, @"\bYL\d{4}\b")).ToArray();
                    Assert.That(own.Select(message => Regex.Match(message.message, @"\bYL\d{4}\b").Value),
                        Is.EquivalentTo(negative ? new[] { "YL0008", "YL0008", "YL0008", "YL0009", "YL0010" } : Array.Empty<string>()));
                    Assert.That(own.All(message => message.type == CompilerMessageType.Warning), Is.True);
                    Assert.That(File.Exists(output), Is.True);
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static IEnumerator CompileAnalyzerWitnesses(AssemblyBuilderFlags flags)
        {
            var editor = (flags & AssemblyBuilderFlags.EditorAssembly) != 0;
            var runtime = CompilationPipeline.GetAssemblies(editor ? AssembliesType.Editor : AssembliesType.Player)
                .Single(a => a.name == "YLFramework.Runtime");
            Assert.That(runtime.compilerOptions.RoslynAnalyzerDllPaths.Count(path => path.EndsWith("YLFramework.Analyzers.dll")), Is.EqualTo(1));
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.Analyzer.Unity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (bool negative in new[] { true, false })
                {
                    string source = "using YLFramework.Core;class Probe:View{" + (negative
                        ? "LoadViewArgs held;protected override void Start(){}void OnEnable(){}void Wrong(Model model){RegisterEvent(1,Changed);EventManager.AddEvent(1,Changed);var args=new LoadViewArgs();model.Id=1;}"
                        : "protected override void Start(){base.Start();}void Awake(){RegisterEvent(1,Changed);}void Borrow(){var args=LoadViewArgs.New();}long Read(Model model)=>model.Id;") + "void Changed(){}}";
                    string path = Path.Combine(folder, negative ? "Negative.cs" : "Positive.cs");
                    string output = Path.ChangeExtension(path, ".dll");
                    File.WriteAllText(path, source);
                    var builder = new AssemblyBuilder(output, new[] { path })
                    {
                        flags = flags,
                        buildTarget = BuildTarget.StandaloneWindows64,
                        buildTargetGroup = BuildTargetGroup.Standalone,
                        referencesOptions = ReferencesOptions.UseEngineModules,
                        additionalReferences = new[] { typeof(Module).Assembly.Location },
                        compilerOptions = new ScriptCompilerOptions { RoslynAnalyzerDllPaths = runtime.compilerOptions.RoslynAnalyzerDllPaths }
                    };
                    builder.excludeReferences = builder.defaultReferences.Where(reference =>
                        Path.GetFileName(reference).StartsWith("YLFramework", StringComparison.Ordinal) ||
                        Path.GetFileName(reference).EndsWith("TestRunner.dll", StringComparison.Ordinal) ||
                        (!editor && Path.GetFileName(reference).StartsWith("UnityEditor", StringComparison.Ordinal))).ToArray();
                    Assert.That(builder.defaultDefines.Contains("UNITY_EDITOR"), Is.EqualTo(editor));
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }

                    Assert.That(messages, Is.Not.Null);
                    var errors = messages.Where(message => message.type == CompilerMessageType.Error).ToArray();
                    Assert.That(errors, Is.Empty,
                        string.Join("\n", errors.Select(message => message.message)));
                    Assert.That(messages.Any(message => message.message.Contains("AD0001")), Is.False);
                    var diagnostics = messages.Where(message => Regex.IsMatch(message.message, @"\bYL000[1-7]\b")).ToArray();
                    Assert.That(diagnostics.All(message => message.type == CompilerMessageType.Warning), Is.True);
                    Assert.That(diagnostics.Select(message => Regex.Match(message.message, @"\bYL000[1-7]\b").Value),
                        Is.EquivalentTo(negative ? Enumerable.Range(1, 7).Select(id => "YL000" + id) : Array.Empty<string>()));
                    Assert.That(File.Exists(output), Is.True);
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void FormalCoreEditorRootPreservesGuidAndExactEditorAssemblyOwnership()
        {
            Assert.That(AssetDatabase.AssetPathToGUID(EditorRoot), Is.EqualTo("d9b09bbe8b78666468f046d49ff546f3"));
            var assembly = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor)
                .Single(a => a.name == EditorAssemblyName);
            var files = Directory.GetFiles("Assets/YLFramework/Editor", "*.cs", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/')).ToArray();
            Assert.That(assembly.sourceFiles.Select(p => p.Replace('\\', '/')), Is.EquivalentTo(files));
            Assert.That(File.Exists(EditorRoot + "/YLFramework.Core.Editor.asmdef"), Is.False);
            Assert.That(File.Exists(EditorRoot + "/YLFramework.Core.Editor.asmdef.meta"), Is.False);
            Assert.That(AssetDatabase.AssetPathToGUID("Assets/YLFramework/Editor/YLFramework.Editor.asmdef"),
                Is.EqualTo("62b3fd14a5c704743835cacd6a6870a1"));
            var help = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Editor.Help.HelpWindow"))
                .Single(type => type != null);
            Assert.That(typeof(CreateModuleCommand).Assembly, Is.SameAs(help.Assembly));
            Assert.That(Directory.GetDirectories("Assets/YLFramework/Editor").Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "Foundation", "Core", "Systems", "Utilities", "Common", "Tools", "Help", "Distribution" }));
        }

        [Test]
        public void EditorAssemblyIsEditorOnlyWithExplicitAllowedDependencies()
        {
            var definition = ReadDefinition("Assets/YLFramework/Editor/YLFramework.Editor.asmdef");
            Assert.That(definition.name, Is.EqualTo(EditorAssemblyName));
            Assert.That(definition.rootNamespace, Is.EqualTo("YLFramework.Editor"));
            Assert.That(definition.includePlatforms, Is.EqualTo(new[] { "Editor" }));
            Assert.That(definition.excludePlatforms, Is.Empty);
            Assert.That(definition.autoReferenced, Is.True);
            var references = definition.references.Select(ResolveReference).ToArray();
            Assert.That(references, Is.EquivalentTo(new[]
            {
                "YLFramework.Runtime", "UnityEngine.UI"
            }), "The Editor subject must not require the original backend or optional adapters.");
            Assert.That(references.Distinct().Count(), Is.EqualTo(references.Length));
            Assert.That(definition.overrideReferences, Is.True);
            Assert.That(definition.precompiledReferences, Is.EqualTo(new[] { "Newtonsoft.Json.dll" }));

            var assembly = typeof(CreateModuleCommand).Assembly;
            Assert.That(assembly.GetName().Name, Is.EqualTo(EditorAssemblyName));
            var compiledReferences = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Assert.That(compiledReferences, Does.Contain("YLFramework.Runtime"));
            Assert.That(compiledReferences, Does.Not.Contain("YLFramework.Editor.Help"));
            Assert.That(compiledReferences, Does.Not.Contain("YLFramework.Core.Editor"));
            Assert.That(compiledReferences.Any(n => n == "UnityEditor" || n.StartsWith("UnityEditor.", StringComparison.Ordinal)), Is.True);
            Assert.That(compiledReferences.Any(IsForbiddenReference), Is.False,
                string.Join(", ", compiledReferences));
        }

        [Test]
        public void TestAssemblyHasNoOriginalGameOrExistingYLEditorDependency()
        {
            var definition = ReadDefinition("Assets/YLFramework/Tests/EditMode/Editor/Core/YLFramework.Core.Editor.Tests.asmdef");
            Assert.That(definition.name, Is.EqualTo("YLFramework.Core.Editor.Tests"));
            Assert.That(definition.includePlatforms, Is.EqualTo(new[] { "Editor" }));
            Assert.That(definition.excludePlatforms, Is.Empty);
            Assert.That(definition.references.Select(ResolveReference), Is.EquivalentTo(new[]
            {
                EditorAssemblyName, "YLFramework.Runtime",
                "UnityEngine.UI", "UnityEngine.TestRunner", "UnityEditor.TestRunner"
            }));
            Assert.That(definition.overrideReferences, Is.True);
            Assert.That(definition.precompiledReferences, Is.EqualTo(new[] { "nunit.framework.dll" }));
            Assert.That(definition.defineConstraints, Does.Contain("UNITY_INCLUDE_TESTS"));
            Assert.That(definition.autoReferenced, Is.False);
            Assert.That(typeof(CoreEditorIdentityTests).Assembly.GetReferencedAssemblies()
                .Any(a => IsForbiddenReference(a.Name)), Is.False);
        }

        [Test]
        public void EditorTypeNamesAreOwnedAndUniqueAcrossLoadedAssemblies()
        {
            var assembly = typeof(CreateModuleCommand).Assembly;
            Assert.That(assembly.GetName().Name, Is.EqualTo(EditorAssemblyName));
            var types = assembly.GetTypes();
            Assert.That(types, Does.Contain(typeof(CreateModuleCommand)));
            Assert.That(types, Does.Contain(typeof(SerializedPropertyExtensions)));
            Assert.That(types, Does.Contain(typeof(DisableEditDrawer)));
            foreach (var type in types)
            {
                var root = type;
                while (root.DeclaringType != null)
                {
                    root = root.DeclaringType;
                }

                if (root.IsDefined(typeof(CompilerGeneratedAttribute), false))
                {
                    continue;
                }

                Assert.That(type.Namespace == "YLFramework.Editor" ||
                    (type.Namespace != null && type.Namespace.StartsWith("YLFramework.Editor.", StringComparison.Ordinal)), Is.True, type.FullName);
                foreach (var other in AppDomain.CurrentDomain.GetAssemblies().Where(a => a != assembly))
                {
                    Assert.That(other.GetType(type.FullName), Is.Null,
                        type.FullName + " also exists in " + other.GetName().Name);
                }
            }
        }

        [Test]
        public void OnlyNewYLMenuEntriesMustBeUniqueAcrossAllRegisteredMenus()
        {
            var entries = TypeCache.GetMethodsWithAttribute<MenuItem>()
                .SelectMany(method => method.GetCustomAttributes(typeof(MenuItem), false)
                    .Cast<MenuItem>().Select(attribute => new { method, attribute })).ToArray();
            var owned = entries.Where(e => e.method.DeclaringType.Assembly == typeof(CreateModuleCommand).Assembly &&
                e.method.DeclaringType.Namespace != null && e.method.DeclaringType.Namespace.StartsWith("YLFramework.Editor.Core", StringComparison.Ordinal)).ToArray();
            Assert.That(owned.Any(e => e.attribute.menuItem == CreateModuleCommand.MenuPath), Is.True);
            Assert.That(owned.Any(e => e.attribute.menuItem == "YLFramework/Core/Create Missing Default Resources"), Is.True);
            foreach (var entry in owned)
            {
                string path = NormalizeMenuPath(entry.attribute.menuItem);
                Assert.That(path.StartsWith("YLFramework/Core/", StringComparison.Ordinal) ||
                    path.StartsWith("Assets/Create/YLFramework/Core/", StringComparison.Ordinal), Is.True, path);
                // 同路径的验证项合法；其他菜单的重复项不属于本测试的所有权范围。
                var conflicts = entries.Where(e => NormalizeMenuPath(e.attribute.menuItem) == path &&
                    e.attribute.validate == entry.attribute.validate).ToArray();
                Assert.That(conflicts.Length, Is.EqualTo(1), path + ": " + string.Join(", ",
                    conflicts.Select(e => e.method.DeclaringType.FullName + "." + e.method.Name)));
            }
        }

        [TestCase(typeof(DisableEditDrawer), typeof(CustomPropertyDrawer), typeof(YLFramework.Attributes.DisableEdit))]
        public void InspectorAndDrawerBindNeutralFoundationMetadata(Type editorType, Type attributeType, Type runtimeType)
        {
            var binding = editorType.GetCustomAttributesData().Single(a => a.AttributeType == attributeType);
            Assert.That(binding.ConstructorArguments[0].Value, Is.EqualTo(runtimeType));
            Assert.That(runtimeType.Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(editorType.Assembly.GetName().Name, Is.EqualTo(EditorAssemblyName));
        }

        [Test]
        public void CoreAudioHasRetiredWhileFormalRuntimeOwnsAudioTypes()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var core = typeof(Module).Assembly;
            var formal = assemblies.Single(a => a.GetName().Name == "YLFramework.Runtime");
            foreach (var name in new[] { "AudioData", "AudioController", "AudioPlayer", "AudioPlayerExtension", "AudioPlayerAutoPause",
                "AudioAssetType", "AudioPlayType", "StopType", "ClipAssetNameInfo", "ClipAssetInfo" })
            {
                Assert.IsNull(core.GetType("YLFramework.Core." + name), name);
            }

            Assert.IsNull(typeof(CreateModuleCommand).Assembly.GetType("YLFramework.Core.Editor.AudioPlayerInspector"));
            foreach (var name in new[] { "AudioKit", "AudioHandle", "AudioEmitter", "AudioMusicPlayer", "AudioClipLibrary", "AudioGroupPlayer" })
            {
                Assert.IsNotNull(formal.GetType("YLFramework.Audio." + name), name);
            }

            CollectionAssert.DoesNotContain(core.GetReferencedAssemblies().Select(a => a.Name).ToArray(), formal.GetName().Name);
            CollectionAssert.DoesNotContain(formal.GetReferencedAssemblies().Select(a => a.Name).ToArray(), core.GetName().Name);
        }

        [Test]
        public void EditorFilesHaveIndependentImportedGuidsAndExcludePackageManagementAndDlls()
        {
            var files = Directory.GetFiles(EditorRoot, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.That(files, Is.Not.Empty);
            foreach (string path in files)
            {
                Assert.That(File.Exists(path + ".meta"), Is.True, "Not imported: " + path);
            }

            Assert.That(File.Exists(EditorRoot + ".meta"), Is.True, "Editor root must also have an independent GUID.");
            var guids = Directory.GetFiles(EditorRoot, "*.meta", SearchOption.AllDirectories)
                .Concat(new[] { EditorRoot + ".meta" }).Select(ReadGuid).ToArray();
            Assert.That(guids.Distinct().Count(), Is.EqualTo(guids.Length), "Duplicate new asset GUIDs.");
            foreach (string file in files)
            {
                string relative = file.Replace('\\', '/').Substring(EditorRoot.Length + 1);
                Assert.That(Path.GetExtension(file), Is.Not.EqualTo(".dll").IgnoreCase, relative);
                Assert.That(relative.Split('/').Any(part => part.Equals("About", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("Expansion", StringComparison.OrdinalIgnoreCase)), Is.False, relative);
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string source = File.ReadAllText(file);
                Assert.That(source, Does.Not.Contain("UnityEditor.PackageManager"), relative);
                Assert.That(source, Does.Not.Contain("ExpansionWindow"), relative);
                Assert.That(source, Does.Not.Contain("ExpansionPackageListTree"), relative);
                Assert.That(source, Does.Not.Contain("ExpansionPackages"), relative);
                Assert.That(source, Does.Not.Contain("Packages/"), relative);
            }
        }

        [Test]
        public void FrameworkRootCarriesTheConsolidatedLicenseSet()
        {
            const string LicensePath = "Assets/YLFramework/LICENSE.md";
            Assert.That(File.Exists(LicensePath), Is.True, "The consolidated license must be distributed at the framework root.");
            string license = File.ReadAllText(LicensePath);
            Assert.That(license, Is.Not.Empty);
            Assert.That(File.Exists("Assets/YLFramework/Integrations/Excel/Editor/Licenses/MiniExcel-LICENSE.txt"), Is.True);
            var remaining = Directory.GetFiles("Assets/YLFramework", "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .Where(file =>
                {
                    string name = Path.GetFileName(file).ToLowerInvariant();
                    return name.Contains("license", StringComparison.Ordinal) ||
                           name.Contains("licence", StringComparison.Ordinal) ||
                           name.Contains("thirdpartynotice", StringComparison.Ordinal) ||
                           name.Contains("third-party-notice", StringComparison.Ordinal);
                })
                .Select(file => file.Replace('\\', '/'))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEquivalent(new[]
            {
                "Assets/YLFramework/LICENSE.md",
                "Assets/YLFramework/Integrations/Excel/Editor/Licenses/MiniExcel-LICENSE.txt"
            }, remaining);
        }

        internal static CoreEditorAssemblyDefinition ReadDefinition(string path)
        {
            return JsonUtility.FromJson<CoreEditorAssemblyDefinition>(File.ReadAllText(path));
        }

        private static string ResolveReference(string reference)
        {
            if (!reference.StartsWith("GUID:", StringComparison.Ordinal))
            {
                return reference;
            }

            return ReadDefinition(AssetDatabase.GUIDToAssetPath(reference.Substring(5))).name;
        }

        private static bool IsForbiddenReference(string name)
        {
            return !new[] { "mscorlib", "netstandard", "System", "System.Core", "System.Xml", "System.Xml.Linq",
                "Newtonsoft.Json", "YLFramework.Runtime", "YLFramework.Editor", "nunit.framework" }.Contains(name) &&
                !name.StartsWith("UnityEngine.", StringComparison.Ordinal) &&
                !name.StartsWith("UnityEditor.", StringComparison.Ordinal);
        }

        private static string NormalizeMenuPath(string path)
        {
            return Regex.Replace(path.Trim(), @"\s+[_%&#][^/\s]+$", "");
        }

        private static string ReadGuid(string path)
        {
            var match = Regex.Match(File.ReadAllText(path), @"(?m)^guid:\s*([0-9a-f]{32})\s*$");
            Assert.That(match.Success, Is.True, "Missing GUID: " + path);
            return match.Groups[1].Value;
        }
    }
}

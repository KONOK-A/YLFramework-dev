using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Actions;
using YLFramework.Utilities.ZipTool.GZip;
using YLFramework.Utilities.ZipTool.Tar;
using Object = UnityEngine.Object;

namespace YLFramework.Tests
{
    public class ExportUnityPackageTests
    {
        private const string FrameworkRoot = "Assets/YLFramework";
        private const string PrefabPath = FrameworkRoot + "/Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab";
        private const string CanvasPath = FrameworkRoot + "/Runtime/Systems/Action/ScreenTransition/ScreenTransitionCanvas.cs";
        private const string PrefabGuid = "2a0bf8e4b9b44ec46a47f771105de1e4";
        private const string CanvasGuid = "256f0a9016a7098469c8afed875a5d50";
        private const string AnalyzerPath = FrameworkRoot + "/Runtime/Core/Analyzers/YLFramework.Analyzers.dll";

        [Serializable]
        private sealed class AnalyzerBuildInput
        {
            public string Path;
            public string Sha256;
        }

        [Serializable]
        private sealed class AnalyzerBuildReceipt
        {
            public string DllSha256;
            public AnalyzerBuildInput[] Inputs;
        }

        [Test]
        public void DeployedAnalyzerMatchesTheLastVerifiedSourceBuild()
        {
            var root = Directory.GetParent(Application.dataPath).FullName;
            var receiptPath = Path.Combine(root, "Temp/YLFramework.Analyzers/build-info.json");
            Assert.That(File.Exists(receiptPath), Is.True, "先执行 Tools/YLFramework.Analyzers/build.ps1 -Deploy 再验收发行产物。");
            var receipt = JsonUtility.FromJson<AnalyzerBuildReceipt>(File.ReadAllText(receiptPath));
            var tools = Path.Combine(root, "Tools/YLFramework.Analyzers");
            var expectedInputs = new[]
            {
                "global.json", "Directory.Build.props", "build.ps1", "Analyzer/YLFramework.Analyzers.csproj",
                "Analyzer/packages.lock.json", "../../Assets/YLFramework/Editor/Core/Analyzers/AnalyzerRuleCatalog.cs"
            }.Concat(Directory.GetFiles(Path.Combine(tools, "Analyzer"), "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(tools, path).Replace('\\', '/'))).ToArray();
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.Inputs, Is.Not.Null);
            Assert.That(receipt.Inputs.Select(input => input.Path), Is.EquivalentTo(expectedInputs),
                "当前源码输入集合与构建收据不一致，可能存在新增、缺失或重复输入，请重建并部署。");
            using var sha = SHA256.Create();
            Assert.That(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(root, AnalyzerPath)))).Replace("-", ""),
                Is.EqualTo(receipt.DllSha256), "部署 DLL 与已验证源码构建不一致。");
            Assert.That(receipt.Inputs.Any(input => input.Path.EndsWith("AnalyzerRuleCatalog.cs", StringComparison.Ordinal)), Is.True);
            foreach (var input in receipt.Inputs)
            {
                var path = Path.GetFullPath(Path.Combine(root, "Tools/YLFramework.Analyzers", input.Path));
                Assert.That(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""),
                    Is.EqualTo(input.Sha256), input.Path + " 在已验证构建后发生变化。");
            }
        }

        [Test]
        public void SourceBuildGateRejectsAFileAddedAfterTheVerifiedBuild()
        {
            var root = Directory.GetParent(Application.dataPath).FullName;
            var probe = Path.Combine(root, "Tools/YLFramework.Analyzers/Analyzer/InputSetProbe-" + Guid.NewGuid().ToString("N") + ".cs");
            Assert.That(File.Exists(probe), Is.False);
            try
            {
                File.WriteAllText(probe, "// Owned gate fixture: this source was added after the recorded build.\n");
                var error = Assert.Throws<AssertionException>(() => DeployedAnalyzerMatchesTheLastVerifiedSourceBuild());
                Assert.That(error.Message, Does.Contain("输入集合"));
            }
            finally
            {
                File.Delete(probe);
            }
        }

        [Test]
        public void FormalExporterAcceptsAnExplicitDestinationWithoutChangingItsRoots()
        {
            Assert.That(typeof(YLFramework.Editor.Distribution.ExportUnityPackage).GetMethod("Export", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(bool) }, null), Is.Not.Null,
                "Automated delivery must use the same formal exporter without saving unrelated assets or requiring the project-root output.");
        }

        [TestCase("\\\\127.0.0.1\\YLFrameworkNoShare\\framework.unitypackage")]
        [TestCase("//127.0.0.1/YLFrameworkNoShare/framework.unitypackage")]
        public void FormalExporterRejectsBothNetworkPathSpellingsBeforeDestinationAccess(string path)
        {
            Assert.Throws<ArgumentException>(() => YLFramework.Editor.Distribution.ExportUnityPackage.Export(path));
        }

        [Test]
        public void DefaultExportDoesNotRedistributeEditorEnhancers()
        {
            var roots = (string[])typeof(YLFramework.Editor.Distribution.ExportUnityPackage)
                .GetField("_exportRoots", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var commercial = "Assets/Plugins/Editor Enhancers Bundle";
            Assert.That(roots.Any(root => IsAtOrBelow(commercial, root)), Is.False,
                "The independent delivery must not recursively redistribute commercial Editor tools without a redistribution grant.");
            Assert.That(roots, Does.Contain(FrameworkRoot + "/Plugins/BouncyCastle"));
        }

        [Test]
        public void DefaultExportDoesNotContainLocalAiInstructions()
        {
            var roots = (string[])typeof(YLFramework.Editor.Distribution.ExportUnityPackage)
                .GetField("_exportRoots", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (var path in new[] { "AGENTS.md", FrameworkRoot + "/AGENTS.md", FrameworkRoot + "/AGENTS.md.meta", "kilocodeignore" })
            {
                Assert.That(roots.Any(root => IsAtOrBelow(path, root)), Is.False, path);
            }
        }

        [Test]
        public void DefaultExportDoesNotContainTheRepositoryOnlyUiAcceptanceBuilder()
        {
            var path = AssetDatabase.GUIDToAssetPath("358ebd50e0ddc2d4b8c4f0266f11ff1e");
            Assert.That(path, Does.StartWith(FrameworkRoot + "/Tests/"),
                "The UI acceptance builder requires a non-delivered test scene and must remain in the repository test boundary.");
        }

        [Test]
        public void DefaultExportContainsTheIntegratedSourcesAndPreservesTheScreenPrefab()
        {
            var rootsField = typeof(YLFramework.Editor.Distribution.ExportUnityPackage).GetField("_exportRoots",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(rootsField, Is.Not.Null);
            var configuredRoots = rootsField.GetValue(null) as string[];
            Assert.That(configuredRoots, Is.Not.Null);
            var roots = (string[])configuredRoots.Clone();
            Assert.That(roots, Is.EqualTo(new[]
            {
                FrameworkRoot + "/Editor",
                FrameworkRoot + "/Plugins/BouncyCastle",
                FrameworkRoot + "/Prefabs",
                FrameworkRoot + "/Runtime",
                FrameworkRoot + "/Samples",
                FrameworkRoot + "/Documentation",
                FrameworkRoot + "/LICENSE.md"
            }));
            foreach (var root in roots)
            {
                Assert.That(AssetDatabase.LoadMainAssetAtPath(root), Is.Not.Null, root);
            }

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var sentinels = new[]
            {
                FrameworkRoot + "/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll",
                FrameworkRoot + "/Plugins/BouncyCastle/SOURCE.txt",
                FrameworkRoot + "/Runtime/Systems/Action/Core/ActionKit.cs",
                FrameworkRoot + "/Runtime/Systems/Audio/AudioKit.cs",
                FrameworkRoot + "/Runtime/Systems/Audio/Actions/PlaySoundAction.cs",
                FrameworkRoot + "/Runtime/Systems/Save/SaveManager.cs",
                FrameworkRoot + "/Runtime/Systems/Save/SaveStore.cs",
                FrameworkRoot + "/Runtime/Systems/Localization/MultiLanguageConfigs.cs",
                FrameworkRoot + "/Runtime/Systems/DataAssets/ScriptableObjectConfigs.cs",
                FrameworkRoot + "/Editor/Systems/DataAssets/DataAssetsWorkbookMemory.cs",
                FrameworkRoot + "/Editor/Help/HelpRegistry.cs",
                FrameworkRoot + "/Editor/Help/HelpWindow.cs",
                FrameworkRoot + "/Editor/Systems/Action/Help/ActionKitHelpProvider.cs",
                FrameworkRoot + "/Editor/Systems/Audio/Help/AudioKitHelpProvider.cs",
                FrameworkRoot + "/Editor/Core/CoreHelpProvider.cs",
                FrameworkRoot + "/Editor/Core/Analyzers/AnalyzerRuleCatalog.cs",
                FrameworkRoot + "/Editor/Core/Analyzers/AnalyzerRuleSetFile.cs",
                FrameworkRoot + "/Editor/Core/Analyzers/AnalyzerRuleSettingsProvider.cs",
                FrameworkRoot + "/Editor/Core/Analyzers/AssemblyInfo.cs",
                FrameworkRoot + "/Editor/Help/FoundationToolsHelpProvider.cs",
                FrameworkRoot + "/Editor/Foundation/Serialization/SerializedDictionaryDrawer.cs",
                FrameworkRoot + "/Editor/Foundation/Events/EventListenerEditor.cs",
                FrameworkRoot + "/Editor/Foundation/Attributes/DisableEditDrawer.cs",
                FrameworkRoot + "/Editor/Common/Inspector/SerializedObjectExtensions.cs",
                FrameworkRoot + "/Editor/Common/Inspector/SerializedPropertyExtensions.cs",
                FrameworkRoot + "/Editor/Common/Scene/HandlesExtensions.cs",
                FrameworkRoot + "/Editor/Common/Assets/AssetNameTools.cs",
                FrameworkRoot + "/Editor/Common/Assets/EditorAssetTool.cs",
                FrameworkRoot + "/Editor/Common/Settings/ProjectSettingTools.cs",
                FrameworkRoot + "/Editor/Common/Settings/ProjectSettingsScriptableObject.cs",
                FrameworkRoot + "/Editor/Common/TreeViews/TreeViewStateGroup.cs",
                FrameworkRoot + "/Editor/Common/TreeViews/ReorderableTreeView.cs",
                FrameworkRoot + "/Editor/Common/Windows/SplitWindow.cs",
                FrameworkRoot + "/Editor/Core/ScriptableObjectExtensions.cs",
                FrameworkRoot + "/Editor/Systems/Shared/DomainAssetOwnership.cs",
                FrameworkRoot + "/Editor/Tools/ClearLog.cs",
                FrameworkRoot + "/Editor/Tools/OpenFolder.cs",
                FrameworkRoot + "/Editor/Distribution/ExportUnityPackage.cs",
                FrameworkRoot + "/Editor/Systems/Resource/Build/ResourceBuildOperation.cs",
                FrameworkRoot + "/Editor/Systems/Resource/Build/ResourceBuildMap.cs",
                FrameworkRoot + "/Editor/Systems/Resource/Build/ResourceBuildWorkspace.cs",
                FrameworkRoot + "/Editor/Systems/Resource/Build/ResourceBundleBuilder.cs",
                FrameworkRoot + "/Editor/Systems/Resource/Build/ResourceCatalogBuilder.cs",
                FrameworkRoot + "/Editor/Systems/Resource/Publishing/ResourcePublisher.cs",
                FrameworkRoot + "/Editor/Systems/Resource/Data/ResourceProject.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/Catalog/ResourceCatalogDocument.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/Catalog/ResourceCatalogCodec.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/Catalog/ResourceReleaseIndex.cs",
                FrameworkRoot + "/Editor/Systems/Resource/CodeGeneration/ResourceReferenceOutput.cs",
                FrameworkRoot + "/Editor/Systems/Resource/CodeGeneration/ResourceCodeGenerator.cs",
                FrameworkRoot + "/Editor/Systems/Resource/ResourceEditorOperations.cs",
                FrameworkRoot + "/Editor/Systems/Resource/ResourceEditorSimulation.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/ResourceSystem.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/ResourceConfiguration.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/Integration/CoreResourceProvider.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/Integration/Audio/ResourceSystem.Audio.cs",
                FrameworkRoot + "/Runtime/Systems/Resource/Integration/Audio/ResourceAudioClipProvider.cs",
                FrameworkRoot + "/Editor/Systems/Audio/AudioClipLibraryMigration.cs",
                FrameworkRoot + "/Documentation/Modules/Attributes.md",
                FrameworkRoot + "/Documentation/CodingStyle.md",
                AnalyzerPath,
                FrameworkRoot + "/Runtime/csc.rsp",
                FrameworkRoot + "/Documentation/Modules/Action.md",
                FrameworkRoot + "/Documentation/Modules/Audio.md",
                FrameworkRoot + "/Documentation/Modules/Help.md",
                FrameworkRoot + "/Documentation/Modules/UI.md",
                FrameworkRoot + "/Prefabs/YLFramework.prefab",
                FrameworkRoot + "/Prefabs/UITips.prefab",
                FrameworkRoot + "/Prefabs/UITipsOverlay.prefab",
                FrameworkRoot + "/Prefabs/ColorSelector.prefab",
                FrameworkRoot + "/Runtime/Utilities/UI/ColorSelector/ColorWheelGraphic.cs",
                FrameworkRoot + "/Runtime/Utilities/UI/ColorSelector/ColorSelectorWindow.cs",
                FrameworkRoot + "/LICENSE.md",
                FrameworkRoot + "/Runtime/Core/Modules/Module.cs",
                FrameworkRoot + "/Runtime/Core/MVC/Views/View.cs",
                FrameworkRoot + "/Runtime/Core/UI/Windows/Panel.cs",
                FrameworkRoot + "/Runtime/Core/UI/Windows/PanelConfig.cs",
                FrameworkRoot + "/Runtime/Core/UI/Windows/PanelOverrides.cs",
                FrameworkRoot + "/Runtime/Core/UI/Windows/PanelRule.cs",
                FrameworkRoot + "/Runtime/Core/UI/Windows/PanelManager.Requests.cs",
                FrameworkRoot + "/Runtime/Core/UI/Input/PanelRaycastGate.cs",
                FrameworkRoot + "/Runtime/Foundation/Timing/Components/TimerObject.cs",
                FrameworkRoot + "/Runtime/Core/Attributes/MustCallBaseAttribute.cs",
                FrameworkRoot + "/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab",
                FrameworkRoot + "/Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab",
                FrameworkRoot + "/Editor/Core/DefaultResources/ListenerInitOnLoad.cs",
                FrameworkRoot + "/Runtime/Systems/Http/HttpManager.cs",
                FrameworkRoot + "/Runtime/Systems/Http/SendWebRequest.cs",
                FrameworkRoot + "/Runtime/Foundation/Pool/Object/ObjectPool.cs",
                FrameworkRoot + "/Runtime/Foundation/Events/Subscriptions/EventChannel.cs",
                FrameworkRoot + "/Runtime/Foundation/Update/UpdateManager.cs",
                FrameworkRoot + "/Runtime/Foundation/Timing/Timer.cs",
                FrameworkRoot + "/Runtime/Foundation/Serialization/SerializedDictionary.cs",
                FrameworkRoot + "/Runtime/Foundation/Attributes/CustomPropertyGui.cs",
                FrameworkRoot + "/Runtime/Foundation/Utilities/AssemblyTool.cs",
                FrameworkRoot + "/Runtime/Foundation/Internal/AssemblyInfo.cs",
                FrameworkRoot + "/Runtime/YLFramework.Runtime.asmdef",
                FrameworkRoot + "/Runtime/Utilities/StringTools.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/GameObjectExtension.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/ObjectExtension.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/AnimatorExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/ColorExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/EventTriggerExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/LayerMaskExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/RigidbodyExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/SceneExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Unity/TimeExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Math/BoundsExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Math/QuaternionExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Math/RayExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Math/Vector2Extensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Math/Vector3Extensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/System/TypeExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/Extensions/Utilities/RandomExtensions.cs",
                FrameworkRoot + "/Runtime/Utilities/FSM/Core/Fsm.cs",
                FrameworkRoot + "/Runtime/Utilities/Grid/NormalGrid.cs",
                FrameworkRoot + "/Runtime/Utilities/Grid/DynamicGrid.cs",
                FrameworkRoot + "/Runtime/Utilities/Json/JSONObject.cs",
                FrameworkRoot + "/Runtime/Utilities/Table/Table.cs",
                FrameworkRoot + "/Runtime/Utilities/Table/TableIndex.cs",
                FrameworkRoot + "/Runtime/Utilities/ZipTool/Utilities/ZipUtility.cs",
                FrameworkRoot + "/Documentation/Modules/FSM.md",
                FrameworkRoot + "/Documentation/Modules/Grid.md",
                FrameworkRoot + "/Documentation/Modules/Json.md",
                FrameworkRoot + "/Documentation/Modules/Table.md",
                FrameworkRoot + "/Documentation/Modules/ZipTool.md",
                PrefabPath,
                CanvasPath
            };
            var payloadGuids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in sentinels)
            {
                Assert.That(File.Exists(Path.Combine(projectRoot, path)), Is.True, "Missing sentinel: " + path);
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(guid, Does.Match("^[0-9a-f]{32}$"), path);
                Assert.That(payloadGuids.Add(guid), Is.True, "Duplicate sentinel GUID: " + path);
            }

            var requiredPaths = AssetDatabase.GetAllAssetPaths()
                .Where(path => roots.Any(root => IsAtOrBelow(path, root))).ToArray();
            var excludedRoots = new[]
            {
                FrameworkRoot + "/Tests", FrameworkRoot + "/Integrations",
                FrameworkRoot + "/Examples", "Assets/YLFrameworkData", "Assets/Games", "Assets/Scenes",
                "Assets/StreamingAssets", "Assets/Plugins/NuGet", "Tools", "Temp", "Library", "Packages",
                FrameworkRoot + "/Runtime/UI",
                FrameworkRoot + "/Runtime/Shared",
                FrameworkRoot + "/Runtime/Tools",
                FrameworkRoot + "/Editor/Serialization",
                FrameworkRoot + "/Runtime/Action",
                FrameworkRoot + "/Runtime/Audio",
                FrameworkRoot + "/Runtime/Save",
                FrameworkRoot + "/Runtime/Services",
                FrameworkRoot + "/Editor/Action",
                FrameworkRoot + "/Editor/Audio",
                FrameworkRoot + "/Runtime/Resource",
                FrameworkRoot + "/Editor/Resource",
                FrameworkRoot + "/Plugins/Sirenix",
                "Assets/Plugins/Editor Enhancers Bundle",
                FrameworkRoot + "/Runtime/Tools/AssemblyTool",
                FrameworkRoot + "/Runtime/Tools/Attributes",
                FrameworkRoot + "/Runtime/Core/Extensions",
                FrameworkRoot + "/Runtime/Core/Tools",
                FrameworkRoot + "/Runtime/Core/UI/Windows/Tools",
                FrameworkRoot + "/Runtime/Utilities/UI/Input",
                FrameworkRoot + "/Samples/ColorSelector",
                "Assets/Plugins/Demigiant/DOTween",
                FrameworkRoot + "/Runtime/Systems/Resource/Framework",
                FrameworkRoot + "/Runtime/Systems/Resource/Implementations",
                FrameworkRoot + "/Runtime/Systems/Resource/HotUpdate",
                FrameworkRoot + "/Runtime/Systems/Resource/Utilities"
            };
            var retiredFiles = new[] { "ActionKitSingleFileCreator.cs", "ActionKitSingleFileExporter.cs", "ActionKitSingleFileCreator.asset", "LogUtil.cs",
                "YLFramework.Foundation.Runtime.asmdef", "YLFramework.Core.Runtime.asmdef", "YLFramework.Utilities.ZipTool.asmdef",
                "YLFramework.Core.Editor.asmdef", "YLFramework.Editor.Help.asmdef",
                "ResManager.cs", "ResLoader.cs", "ServiceManager.cs", "GameObjectLoader.cs", "SubProject.cs", "SubProjectData.cs" };
            Assert.That(Directory.GetDirectories(FrameworkRoot + "/Runtime").Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "Foundation", "Core", "Systems", "Utilities" }));
            Assert.That(Directory.GetDirectories(FrameworkRoot + "/Editor").Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "Foundation", "Core", "Systems", "Utilities", "Common", "Tools", "Help", "Distribution" }));
            var tempDirectory = Path.Combine(@"C:\Users\Kano\AppData\Local\Temp\kilo", "YLFramework.ExportTests-" + Guid.NewGuid().ToString("N"));
            var packagePath = Path.Combine(tempDirectory, "framework.unitypackage");
            try
            {
                Directory.CreateDirectory(tempDirectory);
                var export = typeof(YLFramework.Editor.Distribution.ExportUnityPackage).GetMethod("Export", BindingFlags.Public | BindingFlags.Static);
                Assert.That(export, Is.Not.Null);
                export.Invoke(null, new object[] { packagePath, false });
                Assert.That(File.Exists(packagePath), Is.True, packagePath);
                Assert.That(new FileInfo(packagePath).Length, Is.GreaterThan(0L), packagePath);

                var recordsByGuid = new Dictionary<string, PackageRecord>(StringComparer.Ordinal);
                using (var file = File.OpenRead(packagePath))
                using (var gzip = new GZipInputStream(file) { IsStreamOwner = false })
                using (var tar = new TarInputStream(gzip) { IsStreamOwner = false })
                {
                    TarEntry entry;
                    while ((entry = tar.GetNextEntry()) != null)
                    {
                        if (entry.IsDirectory)
                        {
                            continue;
                        }

                        var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name.Substring(2) : entry.Name;
                        var parts = name.Split('/');
                        Assert.That(parts.Length, Is.EqualTo(2), "Unexpected package member: " + entry.Name);
                        var guid = parts[0];
                        var member = parts[1];
                        Assert.That(guid, Does.Match("^[0-9a-f]{32}$"), entry.Name);
                        Assert.That(member, Is.Not.Empty, entry.Name);
                        Assert.That(entry.Size, Is.GreaterThanOrEqualTo(0L), entry.Name);
                        if (!recordsByGuid.TryGetValue(guid, out var record))
                        {
                            record = new PackageRecord();
                            recordsByGuid.Add(guid, record);
                        }
                        Assert.That(record.MemberSizes.ContainsKey(member), Is.False, "Duplicate GUID member: " + entry.Name);
                        record.MemberSizes.Add(member, entry.Size);
                        // Select by GUID before pathname arrives; archive order is not a contract.
                        if (member == "pathname")
                        {
                            record.Pathname = Encoding.UTF8.GetString(ReadEntry(tar, entry)).TrimEnd('\r', '\n', '\0');
                        }
                        else if (member == "asset.meta")
                        {
                            record.Meta = ReadEntry(tar, entry);
                        }
                        else if (member == "asset")
                        {
                            var payload = ReadEntry(tar, entry);
                            using (var sha = new SHA256Managed())
                            {
                                record.PayloadHash = sha.ComputeHash(payload);
                            }

                            if (payloadGuids.Contains(guid))
                            {
                                record.Payload = payload;
                            }
                        }
                    }
                }

                Assert.That(recordsByGuid, Is.Not.Empty);
                var recordsByPath = new Dictionary<string, PackageRecord>(StringComparer.Ordinal);
                foreach (var pair in recordsByGuid)
                {
                    var guid = pair.Key;
                    var record = pair.Value;
                    Assert.That(record.Pathname, Is.Not.Null.And.Not.Empty, "Missing pathname: " + guid);
                    Assert.That(record.Meta, Is.Not.Null, "Missing asset.meta: " + record.Pathname);
                    Assert.That(record.Pathname.Contains("\\"), Is.False, record.Pathname);
                    Assert.That(record.Pathname.Split('/').Any(part => part == "." || part == ".."), Is.False, record.Pathname);
                    Assert.That(recordsByPath.ContainsKey(record.Pathname), Is.False, "Duplicate pathname: " + record.Pathname);
                    recordsByPath.Add(record.Pathname, record);
                    var meta = Encoding.UTF8.GetString(record.Meta);
                    var metaGuids = Regex.Matches(meta, @"^guid:[ \t]*([0-9a-f]{32})[ \t]*\r?$", RegexOptions.Multiline);
                    Assert.That(metaGuids.Count, Is.EqualTo(1), record.Pathname);
                    Assert.That(metaGuids[0].Groups[1].Value, Is.EqualTo(guid), record.Pathname);
                    Assert.That(AssetDatabase.AssetPathToGUID(record.Pathname), Is.EqualTo(guid), record.Pathname);
                    Assert.That(record.Meta, Is.EqualTo(File.ReadAllBytes(Path.Combine(projectRoot, record.Pathname + ".meta"))), record.Pathname + ".meta");
                    var isFolder = AssetDatabase.IsValidFolder(record.Pathname);
                    Assert.That(Directory.Exists(Path.Combine(projectRoot, record.Pathname)), Is.EqualTo(isFolder), record.Pathname);
                    // Existing folder metadata can predate the folderAsset marker; do not rewrite it during export.
                    if (Regex.IsMatch(meta, @"^folderAsset:[ \t]*yes[ \t]*\r?$", RegexOptions.Multiline))
                    {
                        Assert.That(isFolder, Is.True, record.Pathname);
                    }

                    if (isFolder)
                    {
                        if (record.MemberSizes.TryGetValue("asset", out var size))
                        {
                            Assert.That(size, Is.Zero, "Unexpected folder payload: " + record.Pathname);
                        }
                    }
                    else
                    {
                        Assert.That(record.MemberSizes.ContainsKey("asset"), Is.True, "Missing asset: " + record.Pathname);
                        var source = File.ReadAllBytes(Path.Combine(projectRoot, record.Pathname));
                        Assert.That(record.MemberSizes["asset"], Is.EqualTo(source.LongLength), record.Pathname);
                        using (var sha = new SHA256Managed())
                        {
                            Assert.That(record.PayloadHash, Is.EqualTo(sha.ComputeHash(source)), record.Pathname);
                        }
                    }
                    Assert.That(excludedRoots.Any(root => IsAtOrBelow(record.Pathname, root)), Is.False, "Excluded export path: " + record.Pathname);
                    Assert.That(Path.GetFileName(record.Pathname), Is.Not.EqualTo("DOTween.dll").IgnoreCase, "External DOTween must not be exported: " + record.Pathname);
                    Assert.That(retiredFiles.Contains(Path.GetFileName(record.Pathname), StringComparer.Ordinal), Is.False, "Retired export asset: " + record.Pathname);
                    Assert.That(roots.Any(root => IsAtOrBelow(record.Pathname, root)) || (isFolder && roots.Any(root => IsAtOrBelow(root, record.Pathname))),
                        Is.True, "Unexpected path outside export roots: " + record.Pathname);
                }

                foreach (var path in requiredPaths)
                {
                    Assert.That(recordsByPath.ContainsKey(path), Is.True, "Missing recursive asset: " + path);
                }
                // 批准的插件根全递归，精确核验二进制与来源。
                Assert.That(recordsByPath.Keys.Where(p => IsAtOrBelow(p, FrameworkRoot + "/Plugins/BouncyCastle")),
                    Is.EquivalentTo(requiredPaths.Where(p => IsAtOrBelow(p, FrameworkRoot + "/Plugins/BouncyCastle"))));
                Assert.That(recordsByPath.Keys.Where(p => Path.GetFileName(p) == "CodingStyle.md"),
                    Is.EqualTo(new[] { FrameworkRoot + "/Documentation/CodingStyle.md" }));
                Assert.That(recordsByGuid["af0ff0e561630f34f8e69387e0233953"].Pathname,
                    Is.EqualTo(FrameworkRoot + "/Documentation/CodingStyle.md"));
                Assert.That(recordsByPath.Keys.Where(p => Path.GetFileName(p) == "YLFramework.Analyzers.dll"), Is.EqualTo(new[] { AnalyzerPath }));
                Assert.That(recordsByPath.Keys.Any(p => Path.GetFileName(p).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)), Is.False);
                Assert.That(recordsByPath.Keys.Any(p => new[] { "bcrypt.dll", "ncrypt.dll", "crypt32.dll" }.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)), Is.False);
                Assert.That(recordsByPath.Keys.Any(p => new[] { ".pfx", ".p12", ".key", ".pem" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)), Is.False);
                Assert.That(TestDependencyGuard.IsApprovedBouncyCastleReference(Path.Combine(projectRoot,
                    FrameworkRoot + "/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll")), Is.True);
                var analyzerMeta = Encoding.UTF8.GetString(recordsByPath[AnalyzerPath].Meta);
                Assert.That(analyzerMeta, Does.Contain("guid: 471f81c4273ee66429677df44cbd6960"));
                Assert.That(Regex.Matches(analyzerMeta, @"(?m)^- RoslynAnalyzer\s*$").Count, Is.EqualTo(1));
                Assert.That(analyzerMeta, Does.Contain("isExplicitlyReferenced: 1"));
                Assert.That(Regex.Matches(analyzerMeta, @"(?m)^\s+enabled:\s*1\s*$"), Is.Empty);
                var importer = (PluginImporter)AssetImporter.GetAtPath(AnalyzerPath);
                Assert.That(importer.GetCompatibleWithAnyPlatform(), Is.False);
                Assert.That(importer.GetCompatibleWithEditor(), Is.False);
                Assert.That(importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64), Is.False);
                foreach (var path in sentinels)
                {
                    Assert.That(recordsByPath.TryGetValue(path, out var record), Is.True, "Missing fixed sentinel: " + path);
                    Assert.That(record.Payload, Is.Not.Null, path);
                    Assert.That(record.MemberSizes["asset"], Is.EqualTo((long)record.Payload.Length), path);
                    Assert.That(record.Payload, Is.EqualTo(File.ReadAllBytes(Path.Combine(projectRoot, path))), path);
                    Assert.That(record.Meta, Is.EqualTo(File.ReadAllBytes(Path.Combine(projectRoot, path + ".meta"))), path + ".meta");
                }
                Assert.That(Encoding.UTF8.GetString(recordsByPath[FrameworkRoot + "/Runtime/csc.rsp"].Payload), Does.Contain("-doc:Library/YLFramework.Runtime.xml"));
                Assert.That(AssetDatabase.AssetPathToGUID(FrameworkRoot + "/LICENSE.md"), Is.EqualTo("52c8029f1cf2acf41bba065d713f352b"));
                Assert.That(AssetDatabase.AssetPathToGUID(FrameworkRoot + "/Runtime/Utilities/UI/ColorSelector/ColorSelectorWindow.cs"), Is.EqualTo("4878c19ecf0794f45baa1869cc9513cb"));
                Assert.That(recordsByGuid.ContainsKey(PrefabGuid), Is.True);
                Assert.That(recordsByGuid[PrefabGuid].Pathname, Is.EqualTo(PrefabPath));
                Assert.That(recordsByGuid.ContainsKey(CanvasGuid), Is.True);
                Assert.That(recordsByGuid[CanvasGuid].Pathname, Is.EqualTo(CanvasPath));
                foreach (var retiredGuid in new[]
                {
                    "29e4e189755c172469818bfbd3d8a3a4", "18a39294c5bc61646be12faf6a17fe94",
                    "ef050e654d014a7cb539e809f36326a2", "eaa2ff45b0824bb0a88fbda69f891959",
                    "5117b7f5560b4af8a25c8d427cbae882",
                    "d33818ff6e2077b489a7329d47d3e566", "f9840bdb773148b4d9eddbd2b90046f1",
                    "a6a803ed92acefd45859058b9d3f9948", "ff117a6c8f0101e408f313e4f6c48634",
                    "151b6b54d6304454880ee84ff559511b", "e4d725362ffe3074e9be3b81105a75bc"
                })
                {
                    Assert.That(recordsByGuid.ContainsKey(retiredGuid), Is.False, "Retired UI identity must not be distributed: " + retiredGuid);
                }

                foreach (var retiredGuid in new[] { "6b5d25785704e1d488a8656928450940", "0b536a55a19ee624189b83d3c308893e", "3b3ac1c2986230d43a7e7cee1e54d130" })
                {
                    Assert.That(recordsByGuid.ContainsKey(retiredGuid), Is.False, "Retired utility identity must not be distributed: " + retiredGuid);
                    // Unity 在本 Editor 会话中保留最近删除的 GUID 路径；检查真实存续资产而非缓存墓碑。
                    var retiredPath = AssetDatabase.GUIDToAssetPath(retiredGuid);
                    if (!string.IsNullOrEmpty(retiredPath))
                    {
                        Assert.That(AssetDatabase.AssetPathToGUID(retiredPath, AssetPathToGUIDOptions.OnlyExistingAssets), Is.Empty, retiredGuid);
                        Assert.That(File.Exists(Path.Combine(projectRoot, retiredPath)), Is.False, retiredPath);
                        Assert.That(File.Exists(Path.Combine(projectRoot, retiredPath + ".meta")), Is.False, retiredPath + ".meta");
                    }
                }
                foreach (var name in new[] { "HttpManager.cs", "SendWebRequest.cs" })
                {
                    Assert.That(recordsByPath.Keys.Where(path => Path.GetFileName(path) == name),
                        Is.EqualTo(new[] { FrameworkRoot + "/Runtime/Systems/Http/" + name }), name);
                }

                var prefabYaml = Encoding.UTF8.GetString(recordsByPath[PrefabPath].Payload);
                Assert.That(prefabYaml, Does.Match(@"m_Script:[ \t]*\{[^}\r\n]*\bguid:[ \t]*" + CanvasGuid + @"\b"));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                Assert.That(prefab, Is.Not.Null, PrefabPath);
                foreach (var child in prefab.GetComponentsInChildren<Transform>(true))
                {
                    Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject), Is.Zero, child.name);
                    foreach (Object component in child.GetComponents<Component>())
                    {
                        Assert.That(component, Is.Not.Null, child.name);
                    }
                }
                var canvas = prefab.GetComponent<ScreenTransitionCanvas>();
                Assert.That(canvas, Is.Not.Null);
                var canvasScript = MonoScript.FromMonoBehaviour(canvas);
                Assert.That(canvasScript, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(canvasScript), Is.EqualTo(CanvasPath));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(canvasScript)), Is.EqualTo(CanvasGuid));
                var uiGuids = new[] { "0cd44c1031e13a943bb63640046fad76", "fe87c0e1cc204ed48ad3b37840f39efc" };
                var uiTypes = new[] { typeof(CanvasScaler), typeof(Image) };
                for (var i = 0; i < uiGuids.Length; i++)
                {
                    Assert.That(prefabYaml, Does.Match(@"m_Script:[ \t]*\{[^}\r\n]*\bguid:[ \t]*" + uiGuids[i] + @"\b"));
                    var uiPath = AssetDatabase.GUIDToAssetPath(uiGuids[i]);
                    Assert.That(uiPath, Is.Not.Empty, uiGuids[i]);
                    var uiScript = AssetDatabase.LoadAssetAtPath<MonoScript>(uiPath);
                    Assert.That(uiScript, Is.Not.Null, uiPath);
                    Assert.That(uiScript.GetClass(), Is.EqualTo(uiTypes[i]), uiPath);
                    Assert.That(AssetDatabase.AssetPathToGUID(uiPath), Is.EqualTo(uiGuids[i]), uiPath);
                }
                Assert.That(() => YLFramework.Editor.Distribution.ExportUnityPackage.Export(packagePath), Throws.TypeOf<IOException>(), "Existing packages are not overwritten by default.");
                Assert.That(() => YLFramework.Editor.Distribution.ExportUnityPackage.Export(Path.Combine(tempDirectory, "missing", "framework.unitypackage")), Throws.TypeOf<DirectoryNotFoundException>());
                TestContext.Progress.WriteLine("G4 verified formal framework export " + packagePath + "; records=" + recordsByPath.Count + "; BC exact SHA/meta included.");
            }
            finally
            {
                if (SessionState.GetBool("YL.E04.G4.KeepExport", false))
                {
                    SessionState.SetString("YL.E04.G4.LastExport", packagePath);
                }
                else if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        private static bool IsAtOrBelow(string path, string root)
        {
            return path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
        }

        private static byte[] ReadEntry(TarInputStream tar, TarEntry entry)
        {
            using (var memory = new MemoryStream())
            {
                tar.CopyEntryContents(memory);
                Assert.That(memory.Length, Is.EqualTo(entry.Size), "Truncated package member: " + entry.Name);
                return memory.ToArray();
            }
        }

        private sealed class PackageRecord
        {
            public readonly Dictionary<string, long> MemberSizes = new Dictionary<string, long>(StringComparer.Ordinal);
            public string Pathname;
            public byte[] Meta;
            public byte[] Payload;
            public byte[] PayloadHash;
        }
    }
}

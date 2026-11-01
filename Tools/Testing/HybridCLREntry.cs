using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HybridCLR.Installer;
using HybridCLR.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Integrations.HybridCLR.Editor;
using YLFramework.Samples.HybridCLR.Editor;

namespace YLFramework.Testing
{
    /// <summary>仅安装在显式自有宿主的原生验收入口，调用正式流水线而非复制构建实现。</summary>
    public static class HybridCLREntry
    {
        private static readonly string[] _hotAssemblies =
        {
            "YLFramework.HybridCLR.Sample.Entry", "YLFramework.HybridCLR.Sample.Support"
        };

        public static object Execute(JObject request)
        {
            if ((string)request["backend"] != "IL2CPP" || (bool?)request["hybridCLR"] != true ||
                EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new InvalidOperationException("Explicit owned Windows64 IL2CPP HybridCLR acceptance is required.");
            }
            if ((string)request["phase"] == "HybridConfigure")
            {
                var installer = new InstallerController();
                if (Directory.Exists(global::HybridCLR.SettingsUtil.LocalUnityDataDir))
                {
                    throw new InvalidOperationException("Do not replace an existing local native runtime.");
                }
                if (installer.PackageVersion != "9.0.3")
                {
                    throw new InvalidOperationException("The exact existing HybridCLR SDK is required.");
                }
                HybridCLRSettings.instance.enable = true;
                HybridCLRSettings.instance.useGlobalIl2cpp = false;
                HybridCLRSettings.instance.hotUpdateAssemblyDefinitions = Array.Empty<UnityEditorInternal.AssemblyDefinitionAsset>();
                HybridCLRSettings.instance.hotUpdateAssemblies = _hotAssemblies;
                HybridCLRSettings.instance.preserveHotUpdateAssemblies = Array.Empty<string>();
                HybridCLRSettings.Save();
                installer.InstallFromLocal((string)request["nativeSource"]);
                if (!installer.HasInstalledHybridCLR() || installer.InstalledLibil2cppVersion != "9.0.3")
                {
                    throw new InvalidOperationException("The owned local-source preparation did not complete.");
                }
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Standalone, Il2CppCompilerConfiguration.Release);
                PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Low);
                EditorUserBuildSettings.development = false;
                PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_4_6);
                return new
                {
                    status = "Completed",
                    nativeVariant = (string)request["nativeVariant"],
                    installedVersion = installer.InstalledLibil2cppVersion,
                    localRuntime = global::HybridCLR.SettingsUtil.LocalUnityDataDir
                };
            }
            if ((string)request["phase"] != "HybridBuild")
            {
                throw new ArgumentException("Unknown explicit HybridCLR phase.");
            }

            if (Directory.Exists("Assets/S") || File.Exists("Assets/YLTestingDistribution/DistributionScene.unity"))
            {
                throw new InvalidOperationException("Only fresh owned sample assets and a new build scene are allowed.");
            }
            if (!AssetDatabase.IsValidFolder("Assets/StreamingAssets"))
            {
                AssetDatabase.CreateFolder("Assets", "StreamingAssets");
            }
            var profile = HybridCLRExampleAssets.Create("Assets/S", (string)request["baseline"]);
            // 发布快照保留完整项目身份；短身份绑定本轮，使 Player 内旁车路径保持在 Mono 预算内。
            profile.recipe.project.projectId = "hs-" + ((string)request["resultId"]).Substring(0, 8);
            EditorUtility.SetDirty(profile.recipe.project);
            AssetDatabase.SaveAssetIfDirty(profile.recipe.project);
            profile.hotUpdateAssemblies = new[] { _hotAssemblies[0] };
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var type = Type.GetType("YLFramework.Testing.HybridCLRSmoke, YLTesting.PlayerSmoke", true);
            var probe = new GameObject("YL.HybridCLRSmoke").AddComponent(type);
            type.GetField("resultId").SetValue(probe, (string)request["resultId"]);
            type.GetField("hostId").SetValue(probe, (string)request["hostId"]);
            const string scenePath = "Assets/YLTestingDistribution/DistributionScene.unity";
            if (!EditorSceneManager.SaveScene(scene, scenePath))
            {
                throw new IOException("The owned HybridCLR build scene was not saved.");
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            // Single 场景切换会卸载未被原生对象引用的资产，正式预检读取保存后的新句柄。
            profile = AssetDatabase.LoadAssetAtPath<HybridCLRBuildProfile>("Assets/S/Profile.asset");
            if (!profile || !profile.recipe || !profile.recipe.project)
            {
                throw new InvalidOperationException("The owned saved sample inputs did not reload.");
            }
            var output = (string)request["output"];
            var playerRoot = Path.GetFullPath((string)request["playerRoot"]);
            var playerParent = Path.GetDirectoryName(playerRoot);
            var playerOwner = JObject.Parse(File.ReadAllText(Path.Combine(playerParent, ".yl-owner.json")));
            if ((string)playerOwner["purpose"] != "YLTesting.Directory" || (string)playerOwner["kind"] != "Cache" ||
                (string)playerOwner["id"] != (string)request["playerOwnerId"] ||
                !string.Equals((string)playerOwner["path"], playerParent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The short Player output must have its own leased cache owner.");
            }
            var report = HybridCLRBuildPipeline.BuildBasePlayer(profile, new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
                locationPathName = Path.Combine(playerRoot, "YLDistribution.exe")
            });
            WriteNew(Path.Combine(output, "stages.json"), new
            {
                succeeded = report.HasSucceeded,
                stages = report.Stages.Select(stage => new
                {
                    name = stage.Name,
                    succeeded = stage.HasSucceeded,
                    error = stage.Error?.ToString(),
                    diagnostics = stage.Diagnostics
                }).ToArray()
            });
            if (!report.HasSucceeded || report.PlayerBuild == null || report.PlayerBuild.summary.totalErrors != 0 ||
                report.BaselineDirectory == null || report.Manifest == null || report.Manifest.AotMetadata.Length == 0)
            {
                throw new InvalidOperationException("The real HybridCLR base pipeline did not close: " + report.Summary);
            }
            var managed = Path.Combine(report.BaselineDirectory, "Aot");
            var files = Directory.GetFiles(managed, "*.dll");
            if (files.Any(path => Regex.IsMatch(Path.GetFileName(path), "TestRunner|nunit|YLFramework.*(Tests|Acceptance|Editor|Analyzer)", RegexOptions.IgnoreCase)))
            {
                throw new InvalidOperationException("The final HybridCLR AOT baseline contains forbidden test/tool assemblies.");
            }
            var runtime = Assembly.ReflectionOnlyLoadFrom(Path.Combine(managed, "YLFramework.Runtime.dll"));
            var references = runtime.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
            var rejectedReferences = references.Where(name => !Regex.IsMatch(name,
                @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$") ||
                Regex.IsMatch(name, "TestRunner|Tests|Editor|Analyzer", RegexOptions.IgnoreCase)).ToArray();
            if (rejectedReferences.Length != 0)
            {
                throw new InvalidOperationException("The final AOT Runtime gained forbidden references: " + string.Join(", ", rejectedReferences));
            }
            var evidence = new
            {
                resultId = (string)request["resultId"],
                hostId = (string)request["hostId"],
                unityVersion = Application.unityVersion,
                projectId = report.Manifest.ProjectId,
                compatibilityId = report.Manifest.AppContentCompatibilityId,
                catalogDigest = report.Release.Catalog.CatalogDigest,
                codePackageId = report.Manifest.CodePackageId,
                nativeVariant = (string)request["nativeVariant"],
                cacheRoot = (string)request["runtimeCache"],
                cacheOwnerId = (string)request["cacheOwnerId"],
                metadataAssemblies = report.Manifest.AotMetadata.Select(payload => payload.Name).ToArray(),
                assemblies = report.Manifest.HotUpdateAssemblies.Select(payload => payload.Name).ToArray()
            };
            var receipt = Path.Combine(output, "hybrid-runtime.json");
            WriteNew(receipt, evidence);
            return new
            {
                status = "Passed",
                backend = "IL2CPP",
                smoke = true,
                buildOptions = "None",
                hybridClr = evidence,
                executable = report.PlayerBuild.summary.outputPath,
                receipt,
                baseline = report.BaselineDirectory,
                errors = report.PlayerBuild.summary.totalErrors,
                warnings = report.PlayerBuild.summary.totalWarnings,
                runtimeReferences = references
            };
        }

        private static void WriteNew(string path, object value)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
    }
}

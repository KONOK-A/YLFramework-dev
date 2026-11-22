using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HybridCLR.Installer;
using HybridCLR.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.AddressableAssets;
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
            bool patch = (string)request["phase"] == "HybridPatch";
            if ((string)request["phase"] != "HybridBuild" && !patch)
            {
                throw new ArgumentException("Unknown explicit HybridCLR phase.");
            }

            if (!patch && (Directory.Exists("Assets/S") || File.Exists("Assets/YLTestingDistribution/DistributionScene.unity")))
            {
                throw new InvalidOperationException("Only fresh owned sample assets and a new build scene are allowed.");
            }
            if (!AssetDatabase.IsValidFolder("Assets/StreamingAssets"))
            {
                AssetDatabase.CreateFolder("Assets", "StreamingAssets");
            }
            HybridCLRBuildProfile profile;
            if (patch) profile = AssetDatabase.LoadAssetAtPath<HybridCLRBuildProfile>("Assets/S/Profile.asset");
            else
            {
                if (Directory.Exists("Assets/A")) throw new InvalidOperationException("Unknown official AA settings output exists.");
                AssetDatabase.CreateFolder("Assets", "A");
                var settings = AddressableAssetSettings.Create("Assets/A", "Settings", true, true);
                var code = settings.CreateGroup("NativeCode", false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var sample = settings.CreateGroup("NativeSample", false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                profile = HybridCLRExampleAssets.Create("Assets/S", (string)request["baseline"], settings, code, sample);
            }
            if (!profile || profile.schemaVersion != 2 || !profile.addressableSettings || !profile.codeGroup)
                throw new InvalidOperationException("The saved schema2 official Addressables profile is required.");
            if (!patch) ConfigureOfficialContent(profile, request);
            profile.hotUpdateAssemblies = new[] { _hotAssemblies[0] };
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            const string scenePath = "Assets/YLTestingDistribution/DistributionScene.unity";
            if (!patch)
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var type = Type.GetType("YLFramework.Testing.HybridCLRSmoke, YLTesting.PlayerSmoke", true);
                var probe = new GameObject("YL.HybridCLRSmoke").AddComponent(type);
                type.GetField("resultId").SetValue(probe, (string)request["resultId"]);
                type.GetField("hostId").SetValue(probe, (string)request["hostId"]);
                if (!EditorSceneManager.SaveScene(scene, scenePath))
                {
                    throw new IOException("The owned HybridCLR build scene was not saved.");
                }
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            }
            // Single 场景切换会卸载未被原生对象引用的资产，正式预检读取保存后的新句柄。
            profile = AssetDatabase.LoadAssetAtPath<HybridCLRBuildProfile>("Assets/S/Profile.asset");
            if (!profile || profile.schemaVersion != 2 || !profile.addressableSettings || !profile.codeGroup)
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
            var report = patch ? HybridCLRBuildPipeline.PublishPatch(profile) : HybridCLRBuildPipeline.BuildBasePlayer(profile, new BuildPlayerOptions
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
            if (!report.HasSucceeded || (!patch && (report.PlayerBuild == null || report.PlayerBuild.summary.totalErrors != 0)) ||
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
                @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Unity\.Addressables|Unity\.ResourceManager|Unity\.Profiling\.Core|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json)$") ||
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
                inputHash = (string)request[patch ? "patchInputHash" : "inputHash"],
                contentHash = ContentHash((string)request["runtimeCache"]),
                addressablesVersion = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Single(package => package.name == "com.unity.addressables").version,
                sampleVersion = patch ? "v2" : "v1",
                manifestSchemaVersion = report.Manifest.SchemaVersion,
                manifestAddress = profile.projectId + "/" + YLFramework.Integrations.HybridCLR.HybridCLRManifest.ManifestPath,
                manifestSha256 = Hash(File.ReadAllBytes(profile.generatedAssetDirectory.TrimEnd('/') + "/" + YLFramework.Integrations.HybridCLR.HybridCLRManifest.ManifestPath)),
                codePackageId = report.Manifest.CodePackageId,
                nativeVariant = (string)request["nativeVariant"],
                cacheRoot = (string)request["runtimeCache"],
                cacheOwnerId = (string)request["cacheOwnerId"],
                metadataAssemblies = report.Manifest.AotMetadata.Select(payload => payload.Name).ToArray(),
                assemblies = report.Manifest.HotUpdateAssemblies.Select(payload => payload.Name).ToArray()
                ,
                payloads = report.Manifest.AotMetadata.Select(payload => Payload("AotMetadata", payload)).Concat(
                    report.Manifest.HotUpdateAssemblies.Select(payload => Payload("HotUpdate", payload))).ToArray()
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
                executable = Path.Combine(playerRoot, "YLDistribution.exe"),
                receipt,
                baseline = report.BaselineDirectory,
                errors = report.PlayerBuild?.summary.totalErrors ?? 0,
                warnings = report.PlayerBuild?.summary.totalWarnings ?? 0,
                runtimeReferences = references,
                contentHash = evidence.contentHash,
                addressablesVersion = evidence.addressablesVersion,
                publication = patch ? "PublishPatch" : "BuildBasePlayer"
            };
        }

        private static object Payload(string role, YLFramework.Integrations.HybridCLR.HybridCLRPayload payload) => new
        {
            role,
            name = payload.Name,
            address = payload.Address,
            sha256 = payload.Sha256,
            length = payload.Length,
            mvid = payload.Mvid,
            fullName = payload.Identity
        };

        private static void ConfigureOfficialContent(HybridCLRBuildProfile profile, JObject request)
        {
            var root = Path.GetFullPath((string)request["runtimeCache"]);
            if (Directory.Exists(root)) throw new InvalidOperationException("Do not replace an existing official AA publication root.");
            Directory.CreateDirectory(root);
            var settings = profile.addressableSettings;
            if (!AssetDatabase.IsValidFolder("Assets/AddressableAssetsData")) AssetDatabase.CreateFolder("Assets", "AddressableAssetsData");
            AddressableAssetSettingsDefaultObject.Settings = settings;
            settings.BuildRemoteCatalog = true;
            settings.DisableCatalogUpdateOnStartup = true;
            settings.OverridePlayerVersion = "native-" + ((string)request["resultId"]).Substring(0, 8);
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            settings.profileSettings.CreateValue("Smoke.Build", root.Replace('\\', '/'));
            settings.profileSettings.CreateValue("Smoke.Load", new Uri(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/'));
            settings.RemoteCatalogBuildPath.SetVariableByName(settings, "Smoke.Build");
            settings.RemoteCatalogLoadPath.SetVariableByName(settings, "Smoke.Load");
            foreach (var group in settings.groups.Where(group => group && !group.ReadOnly))
            {
                var bundle = group.GetSchema<BundledAssetGroupSchema>();
                if (!bundle) continue;
                bundle.UseDefaultSchemaSettings = false;
                bundle.BuildPath.SetVariableByName(settings, "Smoke.Build");
                bundle.LoadPath.SetVariableByName(settings, "Smoke.Load");
                var update = group.GetSchema<ContentUpdateGroupSchema>();
                if (update) update.StaticContent = false;
                EditorUtility.SetDirty(bundle);
                if (update) EditorUtility.SetDirty(update);
            }
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        private static string ContentHash(string root)
        {
            var rows = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path => new
            {
                path = path.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'),
                hash = Hash(File.ReadAllBytes(path))
            }).OrderBy(row => row.path, StringComparer.Ordinal);
            return Hash(Encoding.UTF8.GetBytes(string.Concat(rows.Select(row => row.path + "\0" + row.hash + "\n"))));
        }
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteNew(string path, object value)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
    }
}

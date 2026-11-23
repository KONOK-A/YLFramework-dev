using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using static YLFramework.Testing.P0NativePackageProbe;

namespace YLFramework.Testing
{
    public static class P0NativePackageEntry
    {
        private const string Extension = "com.kano.ylframework.resource-encryption";
        private const string Main = "com.kano.ylframework";

        public static void Build()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-ylP0CRequest");
            Require(index >= 0 && index + 1 < args.Length && index == Array.LastIndexOf(args, "-ylP0CRequest"), "Explicit owned package fixture request required.");
            string requestPath = Path.GetFullPath(args[index + 1]);
            var request = JObject.Parse(File.ReadAllText(requestPath));
            string host = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var job = request["job"].ToObject<Job>();
            bool installed = job.stage == "Installed";
            Require((installed || job.stage == "Removed") && job.playerRoot == Path.Combine(host, "P0C", installed ? "I" : "R") &&
                Path.GetDirectoryName(requestPath) == Path.Combine(host, "P0C") && Application.isBatchMode &&
                (string)request["unityVersion"] == Application.unityVersion &&
                !EditorUtility.scriptCompilationFailed && !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                "Invalid package fixture host/stage or unsettled compile.");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var report = new JObject { ["status"] = "Failed", ["stage"] = job.stage, ["token"] = job.token,
                ["unityVersion"] = Application.unityVersion, ["pid"] = process.Id, ["requestSha256"] = FileHash(requestPath), ["inputHash"] = job.inputHash };
            int code = 1;
            try
            {
                FrozenPlayerBuildEvidence.Configure(job.backend);
                var registered = PackageInfo.GetAllRegisteredPackages();
                var main = registered.Single(package => package.name == Main);
                var aa = registered.Single(package => package.name == "com.unity.addressables");
                var ext = registered.SingleOrDefault(package => package.name == Extension);
                Require(main.source == UnityEditor.PackageManager.PackageSource.LocalTarball && main.version == (string)request["packageVersion"] &&
                    aa.source == UnityEditor.PackageManager.PackageSource.Registry && aa.version == (string)request["addressablesVersion"] &&
                    (ext != null) == installed, "Installed package source/version does not match the frozen fixture.");
                report["mainSource"] = main.source.ToString(); report["addressablesVersion"] = aa.version; report["addressablesSource"] = aa.source.ToString();
                FrozenPlayerBuildEvidence.CapturePackageInputs(report, host, FileHash);
                string nativeName = job.finalArchive ? ReleasePlugin : Plugin;
                string managedName = job.finalArchive ? ReleaseAssembly : ExtensionAssembly;
                var plugins = PluginImporter.GetAllImporters().Where(importer => Path.GetFileName(importer.assetPath) == nativeName).ToArray();
                var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                Require(plugins.Length == (installed ? 1 : 0) && assemblies.Any(assembly => assembly.name == managedName) == installed,
                    "Plugin and managed extension assembly are not both present/absent according to the stage.");
                if (installed)
                {
                    string pluginAsset = "Runtime/Plugins/Win64/" + nativeName;
                    var plugin = plugins.Single();
                    string origin = Path.Combine(ext.resolvedPath, pluginAsset.Replace('/', Path.DirectorySeparatorChar));
                    Require(ext.source == UnityEditor.PackageManager.PackageSource.LocalTarball && ext.version == main.version &&
                        plugin.assetPath == "Packages/" + Extension + "/" + pluginAsset && plugin.isNativePlugin &&
                        plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64) &&
                        !plugin.GetCompatibleWithAnyPlatform() && plugin.GetCompatibleWithEditor() == job.finalArchive &&
                        !plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows) &&
                        FileHash(origin) == job.pluginSha256 && FileHash(origin + ".meta") == (string)request["metaSha256"],
                        "Unity did not import the approved Win64-only native plugin from the frozen extension tarball.");
                    report["extensionSource"] = ext.source.ToString(); report["importedPluginSha256"] = FileHash(origin);
                    report["importerSha256"] = FileHash(origin + ".meta");
                    report["pluginImporter"] = "Native Win64 only";
                }
                Require(!Directory.Exists(job.playerRoot), "P0-C refuses reused Player output.");
                Directory.CreateDirectory(job.playerRoot);
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                const string sentinel = "Assets/P0CSentinel.txt";
                if (installed)
                {
                    Require(settings == null && !File.Exists(sentinel), "P0-C install must start in a fresh host.");
                    AssetDatabase.CreateFolder("Assets", "P0CAA");
                    AssetDatabase.CreateFolder("Assets", "AddressableAssetsData");
                    settings = AddressableAssetSettings.Create("Assets/P0CAA", "Addressables", true, true);
                    AddressableAssetSettingsDefaultObject.Settings = settings;
                    File.WriteAllText(sentinel, "main:" + job.token);
                    AssetDatabase.ImportAsset(sentinel, ImportAssetOptions.ForceSynchronousImport);
                    settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(sentinel), settings.DefaultGroup).address = Key;
                }
                Require(settings != null && File.ReadAllText(sentinel) == "main:" + job.token, "Removal must rebuild the installed host's ordinary content.");
                settings.BuildRemoteCatalog = false;
                settings.DisableCatalogUpdateOnStartup = true;
                settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                AssetDatabase.SaveAssets();
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult content);
                Require(content != null && string.IsNullOrEmpty(content.Error), "The ordinary control catalog failed to build: " + content?.Error);
                job.catalogSha256 = FileHash(Path.Combine(Addressables.BuildPath, job.catalogName));
                report["catalogSha256"] = job.catalogSha256;

                Require(!UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, "P0-C refuses to replace a dirty scene.");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Packaged native probe").AddComponent<P0NativePackageProbe>().Configure(job);
                string scenePath = "Assets/P0C" + job.stage + ".unity";
                Require(!File.Exists(scenePath) && EditorSceneManager.SaveScene(scene, scenePath), "Could not save the owned scene fixture.");
                string player = Path.Combine(job.playerRoot, "S.exe");
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = player,
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.StrictMode,
                    extraScriptingDefines = installed && !job.finalArchive ? new[] { "YL_P0_PACKAGED_NATIVE" } : Array.Empty<string>() });
                Require(build.summary.result == BuildResult.Succeeded && build.summary.totalErrors == 0, "Native package acceptance BuildPlayer failed.");
                string data = Path.Combine(job.playerRoot, "S_Data");
                Require(FileHash(Path.Combine(data, "StreamingAssets", "aa", job.catalogName)) == job.catalogSha256, "Build processor deployed a different catalog.");
                var emitted = Directory.GetFiles(job.playerRoot, nativeName, SearchOption.AllDirectories);
                Require(emitted.Length == (installed ? 1 : 0) && (!installed || FileHash(emitted[0]) == job.pluginSha256), "Build output contains the wrong plugin bytes or presence.");
                report["player"] = player;
                FrozenPlayerBuildEvidence.Capture(report, job.backend, player, FileHash, installed ? managedName : null, typeof(P0NativePackageProbe));
                report["binaryPlugins"] = JArray.FromObject(emitted);
                report["status"] = "Built";
                code = 0;
            }
            catch (Exception error) { report["error"] = error.ToString(); Debug.LogException(error); }
            finally
            {
                using (var file = new FileStream((string)request["output"], FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file)) writer.Write(report.ToString(Formatting.Indented));
                EditorApplication.Exit(code);
            }
        }
    }
}

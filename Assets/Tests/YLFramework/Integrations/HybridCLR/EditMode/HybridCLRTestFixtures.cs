using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Integrations.HybridCLR;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    /// <summary>当前测试输入与只读场景准入；宿主归属、发现和退出由统一 YLTestEntry/进程合同负责。</summary>
    public static class HybridCLRTestFixtures
    {
        public const string Root = "Assets/Tests/YLFramework/Integrations/HybridCLR/PlayMode/Resources/YLHybridCLRTests";

        internal static void ValidateRunnerScenes(string savedScene)
        {
            if (SceneManager.sceneCount > 1)
            {
                throw new InvalidOperationException("Only one known runner bootstrap can be replaced.");
            }
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty || (!string.IsNullOrEmpty(scene.path) && scene.path != savedScene))
                {
                    throw new InvalidOperationException("Never replace dirty or unknown saved scenes in test setup.");
                }
                if (!string.IsNullOrEmpty(savedScene) && scene.path == savedScene && scene.rootCount == 0)
                {
                    continue;
                }
                ValidateRunnerScene(scene);
            }
        }

        internal static void ValidateRunnerScene(Scene scene)
        {
            if (scene.isDirty || !string.IsNullOrEmpty(scene.path))
            {
                throw new InvalidOperationException("Never replace dirty or unknown saved scenes in test setup.");
            }
            var roots = scene.GetRootGameObjects();
            if (roots.Length == 0)
            {
                return;
            }
            var camera = roots.Where(root => root.GetComponent<Camera>()).ToArray();
            var light = roots.Where(root => root.GetComponent<Light>()).ToArray();
            if (roots.Length != 2 || roots.Any(root => root.transform.childCount != 0) ||
                camera.Length != 1 || light.Length != 1 || camera[0] == light[0] || !camera[0].GetComponent<AudioListener>() ||
                camera[0].GetComponents<Component>().Any(component => !component ||
                    (component is not Transform && component is not Camera && component is not AudioListener)) ||
                light[0].GetComponents<Component>().Any(component => !component || (component is not Transform && component is not Light)))
            {
                throw new InvalidOperationException("Only the known clean official runner bootstrap may be replaced.");
            }
        }

        public static void Prepare()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Prepare owned test inputs before PlayMode.");
            }
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "YLFramework.HybridCLR.Tests.PlayMode");
            var entryBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly.Location));
            var aot = typeof(HybridCLRManifest).Assembly;
            var metadata = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(aot.Location));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Root));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/Entry.bytes"), entryBytes);
            var conflictBytes = entryBytes.Concat(new byte[] { 1 }).ToArray();
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/ConflictEntry.bytes"), conflictBytes);
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/Metadata.bytes")))
            {
                AssetDatabase.DeleteAsset(Root + "/Metadata.bytes");
            }
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/MetadataPayload.bytes"), metadata);
            foreach (var variant in new[] { "Good", "BadHash", "BadMvid", "Metadata", "Missing", "Conflict", "External" })
            {
                HybridCLRPayload Payload(System.Reflection.Assembly source, byte[] bytes, bool isAot) => new HybridCLRPayload
                {
                    Name = source.GetName().Name,
                    Identity = source.GetName().FullName,
                    Mvid = source.ManifestModule.ModuleVersionId.ToString("D"),
                    Address = HybridCLRManifest.PayloadAddress(source.GetName().Name, isAot),
                    Length = bytes.Length,
                    Sha256 = HybridCLRManifest.Hash(bytes)
                };
                var entry = Payload(assembly, variant == "Conflict" ? conflictBytes : entryBytes, false);
                if (variant == "BadHash")
                    entry.Sha256 = new string('a', 64);
                if (variant == "BadMvid")
                    entry.Mvid = "74a011fb-8799-4f16-9d21-8fe27d01771d";
                if (variant == "External")
                {
                    entry.Name = "Missing.External";
                    entry.Identity = "Missing.External, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
                    entry.Address = HybridCLRManifest.PayloadAddress(entry.Name, false);
                }
                var manifest = new HybridCLRManifest
                {
                    ProjectId = "hybridclr-test",
                    AppContentCompatibilityId = "base-test",
                    UnityVersion = Application.unityVersion,
                    Development = Debug.isDebugBuild,
                    HotUpdateAssemblies = new[] { entry },
                    AotMetadata = variant == "Metadata" ? new[] { Payload(aot, metadata, true) } : Array.Empty<HybridCLRPayload>()
                };
                manifest.Seal();
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/" + variant + ".json"), manifest.ToJson());
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
    }
}

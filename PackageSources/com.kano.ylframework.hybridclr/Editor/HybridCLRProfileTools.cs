using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Compilation;
using UnityEngine;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    internal sealed class HybridCLRProfileInspection
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public string[] Assemblies = Array.Empty<string>();
        public string[] Dependencies = Array.Empty<string>();
        public bool HasSucceeded => Errors.Count == 0;
    }

    /// <summary>只创建新版编排 Profile；官方配置由用户显式选择，不创建旧 Project/Recipe。</summary>
    internal static class HybridCLRProfileTools
    {
        internal static HybridCLRBuildProfile Create(string assetPath)
        {
            RequireEditMode();
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                assetPath.Contains('\\') || assetPath.Split('/').Any(part => part.Length == 0 || part == "." || part == "..") ||
                Path.GetExtension(assetPath) != ".asset" || !AssetDatabase.IsValidFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/')))
            {
                throw new ArgumentException("Select a new profile path in an existing Assets folder.", nameof(assetPath));
            }
            HybridCLRBuildBaseline.RejectLinks(assetPath);
            if (File.Exists(assetPath) || Directory.Exists(assetPath) || File.Exists(assetPath + ".meta"))
            {
                throw new IOException("Profile destination is already occupied: " + assetPath);
            }
            var profile = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            try
            {
                profile.schemaVersion = 2;
                profile.addressableSettings = AddressableAssetSettingsDefaultObject.GetSettings(false);
                profile.projectId = "hot-" + Guid.NewGuid().ToString("N");
                profile.development = EditorUserBuildSettings.development;
                profile.generatedAssetDirectory = assetPath.Substring(0, assetPath.Length - ".asset".Length) + "-Generated";
                profile.baselineDirectory = "Build/HybridCLR/" + profile.projectId + "/aa-base-v2";
                AssetDatabase.CreateAsset(profile, assetPath);
                return profile;
            }
            catch
            {
                if (!EditorUtility.IsPersistent(profile))
                {
                    UnityEngine.Object.DestroyImmediate(profile);
                }
                throw;
            }
        }

        internal static void MapGeneratedResources(HybridCLRBuildProfile profile)
        {
            RequireEditMode();
            HybridCLRBuildPipeline.ValidateResourceInputs(profile);
            HybridCLRBuildPipeline.ValidateInputPaths(profile);
            var path = profile.generatedAssetDirectory.TrimEnd('/') + "/" + HybridCLRManifest.ManifestPath;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Build payloads first; this action registers only real generated source GUIDs.", path);
            }
            var manifest = HybridCLRManifest.Parse(File.ReadAllText(path));
            HybridCLRBuildPipeline.RegisterResourceEntries(profile, manifest);
        }

        internal static HybridCLRProfileInspection Inspect(HybridCLRBuildProfile profile)
        {
            var result = new HybridCLRProfileInspection();
            if (!profile)
            {
                result.Errors.Add("Select a schema 2 HybridCLR profile.");
                return result;
            }
            void Check(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    result.Errors.Add(error.Message);
                }
            }
            Check(() => HybridCLRBuildPipeline.ValidateResourceInputs(profile));
            Check(() => HybridCLRBuildPipeline.ValidateInputPaths(profile));
            Check(() => HybridCLRBuildPipeline.ResolveBaseProfile(profile));
            Check(() =>
            {
                var names = HybridCLRBuildPipeline.ConfiguredNames();
                var graph = CompilationPipeline.GetAssemblies(AssembliesType.Player).ToDictionary(assembly => assembly.name, StringComparer.Ordinal);
                var external = HybridCLRBuildPipeline.ExternalCandidates(names).ToDictionary(assembly => assembly.Name, StringComparer.Ordinal);
                var records = new List<HybridCLRAssemblyRecord>();
                foreach (var name in names)
                {
                    var scripted = graph.TryGetValue(name, out var assembly);
                    if (scripted && external.ContainsKey(name))
                    {
                        throw new InvalidDataException("An assembly is supplied by scripts and an external DLL: " + name);
                    }
                    if (scripted)
                    {
                        records.Add(new HybridCLRAssemblyRecord
                        {
                            Name = name,
                            References = assembly.assemblyReferences.Select(reference => reference.name)
                                .Concat(assembly.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).Distinct(StringComparer.Ordinal).ToArray()
                        });
                    }
                    else if (external.TryGetValue(name, out var dll))
                    {
                        records.Add(dll);
                    }
                    else
                    {
                        throw new InvalidDataException("No Player script or external DLL supplies: " + name);
                    }
                }
                var selected = HybridCLRBuildPipeline.SelectAssemblies(profile,
                    new HybridCLRAssemblySet { HotUpdate = records, Aot = Array.Empty<HybridCLRAssemblyRecord>() });
                result.Assemblies = selected.HotUpdate.Select(assembly => assembly.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                result.Dependencies = profile.hotUpdateAssemblies == null || profile.hotUpdateAssemblies.Length == 0 ? Array.Empty<string>() :
                    result.Assemblies.Except(profile.hotUpdateAssemblies, StringComparer.Ordinal).ToArray();
            });
            if (EditorUtility.IsDirty(profile))
            {
                result.Warnings.Add("Save the selected profile explicitly before a formal build.");
            }
            result.Warnings.Add("Inspection is not compilation, a content build, or native Player verification. Old native bases must be rebuilt.");
            return result;
        }

        private static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            {
                throw new InvalidOperationException("Edit profiles in a settled Editor.");
            }
        }
    }
}

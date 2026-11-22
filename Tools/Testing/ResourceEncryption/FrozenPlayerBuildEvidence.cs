using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Testing
{
    internal static class FrozenPlayerBuildEvidence
    {
        internal static void Configure(string backend)
        {
            if (backend != "Mono" && backend != "IL2CPP") throw new ArgumentException("Expected Mono or IL2CPP.", nameof(backend));
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingBackend(backend));
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, Stripping(backend));
#if UNITY_6000_3_OR_NEWER
            UnityEditor.WindowsStandalone.UserBuildSettings.architecture = OSArchitecture.x64;
#endif
        }

        internal static void Capture(JObject report, string backend, string player, Func<string, string> hash, string extensionAssembly, Type probe)
        {
            var active = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            var stripping = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Standalone);
            if (active != ScriptingBackend(backend) || stripping != Stripping(backend))
                throw new InvalidOperationException("The effective Win64 scripting backend or stripping setting differs from the request.");
            report["backend"] = backend;
            report["stripping"] = stripping.ToString();
            string exe = Path.GetFileName(player);
            string data = Path.GetFileNameWithoutExtension(player) + "_Data";
            string root = Path.GetDirectoryName(player);
            var files = new JObject { [exe] = hash(player) };
            void Add(string name) => files.Add(name, hash(Path.Combine(root, name)));
            if (active == ScriptingImplementation.IL2CPP)
            {
                Add("GameAssembly.dll");
                Add(data + "/il2cpp_data/Metadata/global-metadata.dat");
            }
            else
            {
                Add("MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll");
                Add(data + "/Managed/YLFramework.Runtime.dll");
                Add(data + "/Managed/" + probe.Assembly.GetName().Name + ".dll");
                if (extensionAssembly != null) Add(data + "/Managed/" + extensionAssembly + ".dll");
            }
            report["binaries"] = files;
        }

        internal static void CapturePackageInputs(JObject report, string root, Func<string, string> hash)
        {
            report["manifestSha256"] = hash(Path.Combine(root, "Packages", "manifest.json"));
            report["packageLockSha256"] = hash(Path.Combine(root, "Packages", "packages-lock.json"));
            report["registeredPackages"] = new JArray(PackageInfo.GetAllRegisteredPackages().OrderBy(package => package.name, StringComparer.Ordinal)
                .Select(package => JObject.FromObject(new
                {
                    package.name, package.version, source = package.source.ToString(), package.assetPath, package.packageId,
                    package.resolvedPath, package.isDirectDependency, registryUrl = package.registry?.url
                })));
            report["cacheEnvironment"] = JObject.FromObject(new
            {
                UPM_CACHE_ROOT = Environment.GetEnvironmentVariable("UPM_CACHE_ROOT"),
                UPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_CACHE_PATH"),
                UPM_NPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_NPM_CACHE_PATH"),
                UPM_GIT_LFS_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_GIT_LFS_CACHE_PATH")
            });
        }

        private static ScriptingImplementation ScriptingBackend(string backend) => backend == "Mono" ? ScriptingImplementation.Mono2x : ScriptingImplementation.IL2CPP;
        private static ManagedStrippingLevel Stripping(string backend) => backend == "Mono" ? ManagedStrippingLevel.Disabled : ManagedStrippingLevel.Medium;
    }
}

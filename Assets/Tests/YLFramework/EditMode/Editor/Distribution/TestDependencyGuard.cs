using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;
using CompilationAssembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Tests
{
    internal static class TestDependencyGuard
    {
        internal static bool IsAllowedReference(string path, string unityContents, string ui, bool editor, string json = null)
        {
            var absolute = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            if ((json != null && IsApprovedJsonReference(absolute, json)) ||
                string.Equals(absolute, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(ui)), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var name = Path.GetFileName(absolute);
            if (OfficialPackage(Path.GetFileNameWithoutExtension(name), editor) != null && IsApprovedOfficialOutput(absolute, editor))
            {
                return true;
            }
            if ((name == "UnityEngine.dll" || name.StartsWith("UnityEngine.", StringComparison.Ordinal)) && name != "UnityEngine.TestRunner.dll")
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "Managed/UnityEngine")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "PlaybackEngines")) ||
                    string.Equals(absolute, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(unityContents, "Managed/UnityEngine.dll"))), StringComparison.OrdinalIgnoreCase);
            }
            if (editor && (name == "UnityEditor.dll" || name.StartsWith("UnityEditor.", StringComparison.Ordinal)) &&
                name.EndsWith(".dll", StringComparison.Ordinal) && name != "UnityEditor.TestRunner.dll")
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "Managed/UnityEngine")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "Managed/UnityEditor")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "PlaybackEngines")) ||
                    (string.Equals(Path.GetDirectoryName(absolute), Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(unityContents, "Managed"))),
                        StringComparison.OrdinalIgnoreCase) && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(absolute)));
            }
            if (editor && name.StartsWith("Unity.", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal))
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "PlaybackEngines")) && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(absolute));
            }
            if (name == "mscorlib.dll" || name == "netstandard.dll" || name == "Microsoft.CSharp.dll" ||
                name == "Microsoft.Win32.Primitives.dll" || name == "System.dll" || name.StartsWith("System.", StringComparison.Ordinal))
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "NetStandard")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "UnityReferenceAssemblies")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "MonoBleedingEdge/lib/mono"));
            }
            return false;
        }

        private static bool IsApprovedJsonReference(string path, string json)
        {
            if (Path.GetFileName(path) != "Newtonsoft.Json.dll" || Path.GetFileName(json) != "Newtonsoft.Json.dll")
            {
                return false;
            }
            var package = PackageInfo.GetAllRegisteredPackages().SingleOrDefault(value => value.name == "com.unity.nuget.newtonsoft-json");
            if (!IsOfficialRegistryIdentity(package, ReadProfile("manifest.json"), ReadProfile("packages-lock.json")))
            {
                return false;
            }
            // 同一已解析官方包分别为 Editor 和 Player 提供 CLR/AOT 变体，不能靠文件名或缓存目录标签推断身份。
            var editor = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(package.resolvedPath, "Runtime/Newtonsoft.Json.dll")));
            var aot = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(package.resolvedPath, "Runtime/AOT/Newtonsoft.Json.dll")));
            var requested = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(json));
            return File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(requested)) &&
                (string.Equals(path, editor, StringComparison.OrdinalIgnoreCase) || string.Equals(path, aot, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(requested, editor, StringComparison.OrdinalIgnoreCase) || string.Equals(requested, aot, StringComparison.OrdinalIgnoreCase));
        }

        internal static string FrameworkPackageRoot()
        {
            var package = PackageInfo.GetAllRegisteredPackages().Single(value => value.name == "com.kano.ylframework");
            var path = Path.Combine(package.resolvedPath, "package.json");
            RejectLinks(path);
            var manifest = JObject.Parse(File.ReadAllText(path));
            if (package.assetPath != "Packages/com.kano.ylframework" || (string)manifest["name"] != package.name ||
                (string)manifest["version"] != package.version || package.errors == null || package.errors.Length != 0)
            {
                throw new InvalidOperationException("The main subject has no verified installed package identity.");
            }
            return Path.GetFullPath(package.resolvedPath);
        }

        private static string OfficialPackage(string name, bool editor) => name switch
        {
            "Unity.Addressables" or "Unity.ResourceManager" => "com.unity.addressables",
            "Unity.Profiling.Core" => "com.unity.profiling.core",
            "Unity.Addressables.Editor" when editor => "com.unity.addressables",
            "Unity.ScriptableBuildPipeline" or "Unity.ScriptableBuildPipeline.Editor" when editor => "com.unity.scriptablebuildpipeline",
            _ => null
        };

        private static string EditorSdkPackage(string name) => name switch
        {
            "Unity.Addressables.DocExampleCode.Editor.Tests" => "com.unity.addressables",
            "UnityEngine.TestRunner" or "UnityEditor.TestRunner" => "com.unity.test-framework",
            _ => null
        };

        private static JObject ReadProfile(string file)
        {
            var path = Path.Combine(Application.dataPath, "../Packages", file);
            RejectLinks(path);
            return JObject.Parse(File.ReadAllText(path));
        }

        // PackageInfo owns package sources; CompilationPipeline owns copied ScriptAssemblies outputs.
        // FindForAssembly is deliberately not used to infer ownership of those copied DLLs.
        internal static bool IsApprovedOfficialOutput(string path, bool editor)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (OfficialPackage(name, editor) == null || !File.Exists(path))
                return false;
            var graph = CompilationPipeline.GetAssemblies(editor ? AssembliesType.Editor : AssembliesType.PlayerWithoutTestAssemblies)
                .SingleOrDefault(value => value.name == name);
            var editorGraph = CompilationPipeline.GetAssemblies(AssembliesType.Editor).SingleOrDefault(value => value.name == name);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(value => value.GetName().Name == name);
            return graph != null && editorGraph != null && (SamePath(path, graph.outputPath) || SamePath(path, editorGraph.outputPath)) &&
                (loaded == null || !SamePath(path, editorGraph.outputPath) || SamePath(path, loaded.Location)) &&
                System.Reflection.AssemblyName.GetAssemblyName(path).Name == name && IsApprovedPackageGraph(graph, editor);
        }

        internal static bool IsOfficialRegistryIdentity(PackageInfo package, JObject manifest, JObject locked)
        {
            if (package == null || package.source != PackageSource.Registry || package.registry?.url != "https://packages.unity.com")
                return false;
            return HasPackageIdentity(package, manifest, locked, "registry") &&
                (string)locked["dependencies"]?[package.name]?["url"] == "https://packages.unity.com";
        }

        private static bool HasPackageIdentity(PackageInfo package, JObject manifest, JObject locked, string source)
        {
            var entry = locked["dependencies"]?[package.name] as JObject;
            var requested = manifest["dependencies"]?[package.name];
            if (entry == null || package.assetPath != "Packages/" + package.name || package.packageId != package.name + "@" + package.version ||
                string.IsNullOrEmpty(package.resolvedPath) || package.errors == null || package.errors.Length != 0 ||
                (string)entry["source"] != source || (string)entry["version"] != package.version ||
                entry["depth"]?.Type != JTokenType.Integer || package.isDirectDependency != (requested != null) ||
                (requested != null ? (string)requested != package.version || (int)entry["depth"] != 0 : (int)entry["depth"] <= 0))
                return false;
            var path = Path.Combine(package.resolvedPath, "package.json");
            RejectLinks(path);
            var installed = JObject.Parse(File.ReadAllText(path));
            if ((string)installed["name"] != package.name || (string)installed["version"] != package.version)
                return false;
            var dependencies = (entry["dependencies"] as JObject)?.Properties().ToArray();
            return dependencies != null && package.dependencies != null && dependencies.Length == package.dependencies.Length &&
                package.dependencies.All(dependency => (string)entry["dependencies"]?[dependency.name] == dependency.version);
        }

        internal static bool IsApprovedPackageGraph(CompilationAssembly graph, bool editor, bool editorSdk = false)
        {
            var packageName = editorSdk && editor ? EditorSdkPackage(graph.name) : OfficialPackage(graph.name, editor);
            if (packageName == null)
                return false;
            var packages = PackageInfo.GetAllRegisteredPackages();
            var package = packages.SingleOrDefault(value => value.name == packageName);
            var manifest = ReadProfile("manifest.json");
            var locked = ReadProfile("packages-lock.json");
            var builtin = editorSdk && editor && packageName == "com.unity.test-framework" && package?.source == PackageSource.BuiltIn;
            if (builtin ? !HasPackageIdentity(package, manifest, locked, "builtin") : !IsOfficialRegistryIdentity(package, manifest, locked))
                return false;
            var aa = packages.SingleOrDefault(value => value.name == "com.unity.addressables");
            if (!IsOfficialRegistryIdentity(aa, manifest, locked))
                return false;
            if (packageName != aa.name && !aa.dependencies.Any(value => value.name == packageName))
                return false;
            if (packageName == aa.name && !packages.Single(value => value.name == "com.kano.ylframework").dependencies.Any(value => value.name == aa.name))
                return false;
            var root = Path.GetFullPath(package.resolvedPath);
            var definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(graph.name);
            if (string.IsNullOrEmpty(definition))
                return false;
            var asmdef = PhysicalSource(definition, package);
            if (!IsInsideFolder(asmdef, root) || !File.Exists(asmdef) || (string)JObject.Parse(File.ReadAllText(asmdef))["name"] != graph.name ||
                graph.sourceFiles == null || graph.sourceFiles.Length == 0)
                return false;
            RejectLinks(asmdef);
            string installedRoot = builtin ? VerifyBuiltinRoot(package) : null;
            foreach (var source in graph.sourceFiles)
            {
                var path = PhysicalSource(source, package);
                if (!IsInsideFolder(path, root) || !File.Exists(path))
                    return false;
                RejectLinks(path);
                if (builtin)
                {
                    var installed = Path.Combine(installedRoot, path.Substring(root.Length + 1));
                    RejectLinks(installed);
                    if (!IsInsideFolder(installed, installedRoot) || !File.Exists(installed) || Hash(path) != Hash(installed))
                        return false;
                }
            }
            return true;
        }

        // These are compiler-injected Editor SDK inputs only, never direct product or Player references.
        internal static bool IsApprovedEditorSdkReference(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (EditorSdkPackage(name) != null)
            {
                var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor).SingleOrDefault(value => value.name == name);
                return graph != null && SamePath(path, graph.outputPath) && IsApprovedPackageGraph(graph, true, true);
            }
            if (name != "nunit.framework")
                return false;
            var packages = PackageInfo.GetAllRegisteredPackages();
            var package = packages.SingleOrDefault(value => value.name == "com.unity.ext.nunit");
            var manifest = ReadProfile("manifest.json");
            var locked = ReadProfile("packages-lock.json");
            if (package == null || !SamePath(path, Path.Combine(package.resolvedPath, "net40/unity-custom/nunit.framework.dll")) || !File.Exists(path))
                return false;
            var builtin = package.source == PackageSource.BuiltIn;
            if (builtin ? !HasPackageIdentity(package, manifest, locked, "builtin") : !IsOfficialRegistryIdentity(package, manifest, locked))
                return false;
            var utf = packages.SingleOrDefault(value => value.name == "com.unity.test-framework");
            if (utf == null || !utf.dependencies.Any(value => value.name == package.name))
                return false;
            RejectLinks(path);
            return !builtin || Hash(path) == Hash(Path.Combine(VerifyBuiltinRoot(package), "net40/unity-custom/nunit.framework.dll"));
        }

        private static string VerifyBuiltinRoot(PackageInfo package)
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var resolutionPath = Path.Combine(project, "Library/PackageManager/projectResolution.json");
            RejectLinks(resolutionPath);
            var resolution = JObject.Parse(File.ReadAllText(resolutionPath));
            var rows = (resolution["outputs"] as JObject)?.Properties().Where(value => (string)value.Value["name"] == package.name).ToArray();
            var materialized = JObject.Parse(File.ReadAllText(Path.Combine(package.resolvedPath, "package.json")));
            if (rows == null || rows.Length != 1 || rows[0].Name != package.name + "@" + package.version)
                throw new InvalidOperationException("Ambiguous BuiltIn resolution.");
            var row = rows[0].Value;
            var fingerprint = (string)row["fingerprint"];
            if (!SamePath((string)resolution["context"]?["projectPath"], Path.Combine(project, "Packages")) ||
                (string)resolution["context"]?["unityVersion"] != Application.unityVersion || (string)row["source"] != "builtin" ||
                (string)row["version"] != package.version || row["testable"]?.Type != JTokenType.Boolean || (bool)row["testable"] ||
                fingerprint == null || !Regex.IsMatch(fingerprint, "^[0-9a-f]{40}$") || (string)materialized["_fingerprint"] != fingerprint ||
                !SamePath(package.resolvedPath, (string)row["resolvedPath"]) ||
                !SamePath(package.resolvedPath, Path.Combine(project, "Library/PackageCache", package.name + "@" + fingerprint.Substring(0, 12))))
                throw new InvalidOperationException("BuiltIn source is not this host's exact native UPM materialization.");
            var installedRoot = Path.Combine(EditorApplication.applicationContentsPath, "Resources/PackageManager/BuiltInPackages", package.name);
            RejectLinks(installedRoot);
            var installedManifest = Path.Combine(installedRoot, "package.json");
            RejectLinks(installedManifest);
            var installed = JObject.Parse(File.ReadAllText(installedManifest));
            if ((string)installed["name"] != package.name || (string)installed["version"] != package.version)
                throw new InvalidOperationException("BuiltIn installation identity differs.");
            return installedRoot;
        }

        private static string PhysicalSource(string path, PackageInfo package)
        {
            var normalized = path.Replace('\\', '/');
            return Path.GetFullPath(normalized.StartsWith(package.assetPath + "/", StringComparison.Ordinal)
                ? Path.Combine(package.resolvedPath, normalized.Substring(package.assetPath.Length + 1)) : path);
        }

        private static bool SamePath(string first, string second) => !string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second) &&
            string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void RejectLinks(string path)
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Dependency evidence must not traverse a link: " + path);
            }
        }

        internal static bool IsAllowedSdkBinaryDependency(string parent, string reference, string unityContents)
        {
            var name = Path.GetFileName(reference);
            if (Array.IndexOf(new[] { "Newtonsoft.Json.dll", "BouncyCastle.Cryptography.dll", "UnityEngine.TestRunner.dll",
                "UnityEditor.TestRunner.dll", "nunit.framework.dll", "ICSharpCode.SharpZipLib.dll", "EPPlus.dll", "MiniExcel.dll" }, name) >= 0)
            {
                return false;
            }
            var folders = new[] { "Managed", "NetStandard", "UnityReferenceAssemblies", "MonoBleedingEdge/lib/mono", "PlaybackEngines" };
            bool IsSdkBinary(string path)
            {
                if (Path.GetExtension(path) != ".dll" || !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    return false;
                }
                foreach (var folder in folders)
                {
                    if (IsInsideFolder(path, Path.Combine(unityContents, folder)))
                    {
                        return true;
                    }
                }
                return false;
            }
            // SDK 内部边可以跨其私有支持库；这不授权产品直接引用它们或加载项目中的同名副本。
            return IsSdkBinary(parent) && IsSdkBinary(reference);
        }

        internal static bool IsInsideFolder(string path, string folder)
        {
            var prefix = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(folder)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path)).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}

// Only the NoUnity boundary double. This file is never copied to a Unity host.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnityEngine
{
    public class Object
    {
        public string AssetPath;
        public static implicit operator bool(Object value) => value != null;
        public static bool operator !(Object value) => value == null;
    }
    public class ScriptableObject : Object { }
    public class Component : Object { public GameObject gameObject; }
    public class Transform : Component { }
    public class GameObject : Object
    {
        public Component GetComponent(Type type) => (Component)Activator.CreateInstance(type);
        public T[] GetComponentsInChildren<T>(bool inactive) where T : Component, new() => new[] { new T { gameObject = this } };
    }
    public class TextAsset : Object { }
    public class AudioClip : Object { }
    public static class Application
    {
        public static string dataPath;
        public static string unityVersion = "2022.3.62f3";
        public static bool isBatchMode = true;
    }
}
namespace YLFramework.Editor.Resource
{
    public class ResourceProject : UnityEngine.ScriptableObject { }
    public class ResourceBuildRecipe : UnityEngine.ScriptableObject { }
}
namespace YLFramework.Integrations.HybridCLR.Editor
{
    public class HybridCLRBuildProfile : UnityEngine.ScriptableObject { public string baselineDirectory; public string generatedAssetDirectory; }
}
namespace UnityEditor
{
    public class PackageInfo { }
    [AttributeUsage(AttributeTargets.Method)] public class InitializeOnLoadMethodAttribute : Attribute { }
    public class DefaultAsset : UnityEngine.Object { }
    public class SceneAsset : UnityEngine.Object { }
    public class MonoScript : UnityEngine.Object { }
    public static class EditorUtility { public static bool scriptCompilationFailed; }
    public static class GameObjectUtility { public static int GetMonoBehavioursWithMissingScriptCount(UnityEngine.GameObject value) => 0; }
    public static class EditorApplication
    {
        public static string applicationPath;
        public static bool isPlayingOrWillChangePlaymode, isCompiling, isUpdating;
        public static event Action update;
        public static event Action delayCall;
        public static List<int> ExitCodes = new List<int>();
        public static void Exit(int code) => ExitCodes.Add(code);
        public static void Reset() { update = null; delayCall = null; ExitCodes.Clear(); isCompiling = isUpdating = isPlayingOrWillChangePlaymode = false; }
        public static void Pump() { var delayed = delayCall; delayCall = null; delayed?.Invoke(); update?.Invoke(); }
    }
    public static class AssetDatabase
    {
        public static string Host;
        private static readonly Dictionary<string, UnityEngine.Object> values = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        public static string Full(string path) => Path.Combine(Host, path.Replace('/', Path.DirectorySeparatorChar));
        public static string AssetPathToGUID(string path)
        {
            var meta = Full(path) + ".meta";
            return File.Exists(meta) ? Regex.Match(File.ReadAllText(meta), "(?m)^guid: ([0-9a-f]{32})\\r?$").Groups[1].Value : "";
        }
        public static Type GetMainAssetTypeAtPath(string path)
        {
            if (Directory.Exists(Full(path))) return typeof(DefaultAsset);
            if (values.TryGetValue(Full(path), out var value)) return value.GetType();
            switch (Path.GetExtension(path))
            {
                case ".cs": return typeof(MonoScript);
                case ".asmdef": return typeof(UnityEditorInternal.AssemblyDefinitionAsset);
                case ".unity": return typeof(SceneAsset);
                case ".prefab": return typeof(UnityEngine.GameObject);
                case ".wav": return typeof(UnityEngine.AudioClip);
                case ".asset": return null;
                default: return typeof(UnityEngine.TextAsset);
            }
        }
        public static UnityEngine.Object LoadMainAssetAtPath(string path)
        {
            if (values.TryGetValue(Full(path), out var value)) return value;
            var type = GetMainAssetTypeAtPath(path);
            return type != null ? (UnityEngine.Object)Activator.CreateInstance(type) : null;
        }
        public static string GetAssetPath(UnityEngine.Object value) => value.AssetPath;
        public static void Put(string path, string typeName)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false)).First(t => t != null);
            var value = (UnityEngine.Object)Activator.CreateInstance(type); value.AssetPath = path; values[Full(path)] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(Full(path))); File.WriteAllText(Full(path), "Contract factory asset: " + typeName + "\n"); Meta(Full(path));
        }
        public static string CreateFolder(string parent, string name)
        {
            var path = parent + "/" + name; Directory.CreateDirectory(Full(path)); Meta(Full(path)); return AssetPathToGUID(path);
        }
        public static void Meta(string full)
        {
            if (!File.Exists(full + ".meta")) File.WriteAllText(full + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
        }
        public static string MoveAsset(string source, string target)
        {
            Directory.Move(Full(source), Full(target)); File.Move(Full(source) + ".meta", Full(target) + ".meta");
            foreach (var key in values.Keys.Where(p => p.StartsWith(Full(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var value = values[key]; values.Remove(key); var destination = Full(target) + key.Substring(Full(source).Length);
                value.AssetPath = target + value.AssetPath.Substring(source.Length); values[destination] = value;
            }
            return "";
        }
    }
    public static class ContractAssets
    {
        public static int FactoryCalls;
        public static string Fault;
        public static string CreateResource()
        {
            FactoryCalls++;
            var root = "Assets/YLResourceStartupExample"; AssetDatabase.CreateFolder("Assets", "YLResourceStartupExample");
            if (Fault == "factory-empty") return root;
            AssetDatabase.CreateFolder(root, "Content");
            AssetDatabase.Put(root + "/Project.asset", Fault == "wrong-type" ? "UnityEngine.TextAsset" : "YLFramework.Editor.Resource.ResourceProject");
            AssetDatabase.Put(root + "/Content/Data.asset", "YLFramework.Samples.Resource.ResourceStartupExampleData");
            AssetDatabase.Put(root + "/Content/Message.txt", "UnityEngine.TextAsset"); AssetDatabase.Put(root + "/Content/Tone.wav", "UnityEngine.AudioClip");
            AssetDatabase.Put(root + "/Example.unity", "UnityEditor.SceneAsset");
            if (Fault == "factory-cache-write") File.AppendAllText(Path.Combine(AssetDatabase.Host, "Library/PackageCache/com.kano.ylframework/package.json"), "\n");
            return root;
        }
        public static UnityEngine.Object CreateHybrid(string root, string baseline)
        {
            FactoryCalls++;
            AssetDatabase.CreateFolder("Assets/YLFrameworkData", Path.GetFileName(root));
            AssetDatabase.CreateFolder(root, "Generated"); AssetDatabase.CreateFolder(root + "/Generated", "hybridclr"); AssetDatabase.CreateFolder(root, "Data");
            AssetDatabase.Put(root + "/Project.asset", "YLFramework.Editor.Resource.ResourceProject"); AssetDatabase.Put(root + "/Recipe.asset", "YLFramework.Editor.Resource.ResourceBuildRecipe");
            AssetDatabase.Put(root + "/Profile.asset", "YLFramework.Integrations.HybridCLR.Editor.HybridCLRBuildProfile");
            AssetDatabase.Put(root + "/Data/message.json", "UnityEngine.TextAsset"); AssetDatabase.Put(root + "/Data/HotView.prefab", "UnityEngine.GameObject");
            var profile = (YLFramework.Integrations.HybridCLR.Editor.HybridCLRBuildProfile)AssetDatabase.LoadMainAssetAtPath(root + "/Profile.asset");
            profile.baselineDirectory = baseline; profile.generatedAssetDirectory = root + "/Generated";
            return profile;
        }
    }
}
namespace UnityEditorInternal { public class AssemblyDefinitionAsset : UnityEngine.Object { } }
namespace UnityEditor.PackageManager
{
    public enum PackageSource { Registry, LocalTarball, Local, Git, Embedded, BuiltIn }
    public class RegistryInfo { public string url; }
    public class DependencyInfo { public string name, version; }
    public class Error { public string message; }
    public class PackageInfo
    {
        public string name, version, packageId, resolvedPath;
        public PackageSource source;
        public bool isDirectDependency;
        public RegistryInfo registry;
        public DependencyInfo[] dependencies;
        public Error[] errors = new Error[0];
        public static PackageInfo[] Registered = new PackageInfo[0];
        public static PackageInfo[] GetAllRegisteredPackages() => Registered;
    }
}
namespace UnityEditor.PackageManager.UI
{
    public struct Sample
    {
        [Flags] public enum ImportOptions { None = 0, OverridePreviousImports = 1 }
        public string displayName { get; set; }
        public string importPath { get; set; }
        public string resolvedPath { get; set; }
        public bool isImported => Directory.Exists(importPath);
        public bool interactiveImport => false;
        public static Sample Current;
        public static bool ImportResult = true;
        public static int ImportCalls;
        public static IEnumerable<Sample> FindByPackage(string name, string version) { yield return Current; }
        public static void Configure(string host, string entry, string version)
        {
            var hybrid = entry.EndsWith(".hybridclr", StringComparison.Ordinal);
            Current = new Sample { displayName = hybrid ? "HybridCLR Module" : "Resource Startup", importPath = Path.Combine(host, "Assets/Samples", hybrid ? "YLFramework HybridCLR Integration" : "YLFramework", version, hybrid ? "HybridCLR Module" : "Resource Startup"),
                resolvedPath = Path.Combine(host, "Library/PackageCache", entry, hybrid ? "Samples~/HybridCLR" : "Samples~/ResourceStartup") };
            ImportResult = true;
        }
        public bool Import(ImportOptions options)
        {
            ImportCalls++;
            if (options != ImportOptions.None || !ImportResult || Directory.Exists(importPath)) return false;
            Directory.CreateDirectory(importPath);
            foreach (var source in Directory.GetFiles(resolvedPath, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(importPath, Path.GetRelativePath(resolvedPath, source)); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target);
            }
            for (var directory = importPath; directory != Path.Combine(AssetDatabase.Host, "Assets"); directory = Path.GetDirectoryName(directory)) AssetDatabase.Meta(directory);
            return true;
        }
    }
}
namespace UnityEditor.Compilation
{
    public enum AssembliesType { Editor, Player }
    public class Assembly { public string name, outputPath; public string[] sourceFiles; }
    public static class CompilationPipeline
    {
        private static string host, fixture; private static bool before, missing, foreign;
        public static void Configure(string hostRoot, string fixtureRoot, bool compiledBefore, bool missingAssembly, bool foreignSource)
        { host = hostRoot; fixture = fixtureRoot; before = compiledBefore; missing = missingAssembly; foreign = foreignSource; }
        public static Assembly[] GetAssemblies(AssembliesType mode)
        {
            var samples = Path.Combine(host, "Assets/Samples");
            if (before) return new[] { new Assembly { name = "YLFramework.ResourceStartup.Sample", sourceFiles = new[] { "Packages/com.kano.ylframework/Samples~/ResourceStartup/Runtime/Example.cs" } } };
            if (!Directory.Exists(samples)) return new Assembly[0];
            var result = new List<Assembly>();
            foreach (var path in Directory.GetFiles(samples, "*.asmdef", SearchOption.AllDirectories))
            {
                var name = (string)JObject.Parse(File.ReadAllText(path))["name"];
                if (missing && name.EndsWith(".Editor", StringComparison.Ordinal)) continue;
                if (mode == AssembliesType.Player && name.EndsWith(".Editor", StringComparison.Ordinal)) continue;
                var output = Path.Combine(host, "Library/ScriptAssemblies", name + ".dll"); Directory.CreateDirectory(Path.GetDirectoryName(output)); File.Copy(Path.Combine(fixture, name + ".dll"), output, true);
                result.Add(new Assembly { name = name, outputPath = output, sourceFiles = foreign ? new[] { Path.Combine(fixture, "foreign.cs") } : Directory.GetFiles(Path.GetDirectoryName(path), "*.cs", SearchOption.AllDirectories) });
            }
            return result.ToArray();
        }
    }
}

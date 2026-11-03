// NoUnity contract support only. Real Unity verification never includes this file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
namespace UnityEngine
{
    public static class Application { public static string dataPath; public static string unityVersion = "2022.3.62f3"; public static bool isBatchMode = true; }
    public static class Debug
    {
        public static bool AllowOwnedLog; public static int LogCalls; public static Exception LoggedException;
        public static void LogException(Exception error)
        {
            LogCalls++; LoggedException = error;
            if (!AllowOwnedLog) { throw new InvalidOperationException("Unexpected owned execution failure", error); }
        }
    }
    public class GameObject
    {
        public int MissingScripts;
        public GameObject(string name) { }
        public object AddComponent(Type type) => Activator.CreateInstance(type);
        public T[] GetComponentsInChildren<T>(bool includeInactive) => typeof(T) == typeof(Transform) ? new[] { (T)(object)new Transform { gameObject = this } } : Array.Empty<T>();
    }
    public class Transform { public GameObject gameObject; }
}
namespace UnityEditor
{
    public static class EditorUtility { public static bool scriptCompilationFailed; }
    public static class EditorApplication
    {
        public static bool isCompiling; public static bool isUpdating;
        public static bool AllowOwnedExit; public static int ExitCalls; public static int LastExitCode = -1;
        public static void Exit(int code)
        {
            ExitCalls++; LastExitCode = code;
            if (!AllowOwnedExit) { throw new InvalidOperationException("NoUnity contracts must return without EditorApplication.Exit: " + code); }
        }
    }
    public enum BuildTargetGroup { Standalone }
    public enum BuildTarget { StandaloneWindows64 }
    public enum ScriptingImplementation { IL2CPP, Mono2x }
    public enum Il2CppCompilerConfiguration { Release }
    public enum ManagedStrippingLevel { Low }
    public enum BuildOptions { None }
    public struct BuildPlayerOptions { public string[] scenes; public BuildTarget target; public BuildOptions options; public string locationPathName; }
    public static class BuildPipeline
    {
        public static bool IsBuildTargetSupported(BuildTargetGroup group, BuildTarget target) => false;
        public static Build.Reporting.BuildReport BuildPlayer(BuildPlayerOptions options) => throw new NotSupportedException("NoUnity");
    }
    public static class EditorUserBuildSettings { public static BuildTarget activeBuildTarget; }
    public static class PlayerSettings
    {
        public static void SetScriptingBackend(Build.NamedBuildTarget target, ScriptingImplementation backend) { }
        public static void SetIl2CppCompilerConfiguration(Build.NamedBuildTarget target, Il2CppCompilerConfiguration config) { }
        public static void SetManagedStrippingLevel(Build.NamedBuildTarget target, ManagedStrippingLevel level) { }
    }
    public static class AssetDatabase
    {
        public static readonly Dictionary<string, string> Guids = new Dictionary<string, string>(StringComparer.Ordinal);
        public static readonly Dictionary<string, UnityEngine.GameObject> Prefabs = new Dictionary<string, UnityEngine.GameObject>(StringComparer.Ordinal);
        public static string AssetPathToGUID(string path) => Guids.TryGetValue(path, out var guid) ? guid : "";
        public static string[] FindAssets(string filter, string[] roots) => Prefabs.Keys.Where(path => roots.Any(root => path.StartsWith(root + "/", StringComparison.Ordinal))).Select(path => Guids[path]).ToArray();
        public static string GUIDToAssetPath(string guid) => Guids.Single(item => item.Value == guid).Key;
        public static T LoadAssetAtPath<T>(string path) => Prefabs.TryGetValue(path, out var prefab) ? (T)(object)prefab : default;
    }
    public static class GameObjectUtility { public static int GetMonoBehavioursWithMissingScriptCount(UnityEngine.GameObject value) => value.MissingScripts; }
}
namespace UnityEditor.Build
{
    public struct NamedBuildTarget { public static NamedBuildTarget Standalone; }
}
namespace UnityEditor.Build.Reporting
{
    public enum BuildResult { Succeeded }
    public class BuildReport { public BuildSummary summary = new BuildSummary(); }
    public class BuildSummary { public BuildResult result; public int totalErrors; public int totalWarnings; }
}
namespace UnityEditor.SceneManagement
{
    public enum NewSceneSetup { EmptyScene }
    public enum NewSceneMode { Single }
    public static class EditorSceneManager
    {
        public static object NewScene(NewSceneSetup setup, NewSceneMode mode) => new object();
        public static bool SaveScene(object scene, string path) => true;
    }
}
namespace UnityEditor.Compilation
{
    public enum AssembliesType { Editor, PlayerWithoutTestAssemblies }
    public class Assembly { public string name; public string outputPath; public string[] sourceFiles = Array.Empty<string>(); public Assembly[] assemblyReferences = Array.Empty<Assembly>(); public string[] compiledAssemblyReferences = Array.Empty<string>(); }
    public static class CompilationPipeline
    {
        public static Assembly[] Editor = Array.Empty<Assembly>(); public static Assembly[] Player = Array.Empty<Assembly>();
        public static Assembly[] GetAssemblies(AssembliesType type) => type == AssembliesType.Editor ? Editor : Player;
    }
}
namespace YLFramework.Testing.NoUnity
{
    // Real .NET fixture DLLs, not Unity-compiled product assemblies. Collectible to release the temporary host.
    public static class FixtureAssemblies
    {
        private static AssemblyLoadContext context;
        public static void Load(string[] paths)
        {
            if (context != null) { throw new InvalidOperationException("Unload the previous NoUnity fixture first."); }
            context = new AssemblyLoadContext("Distribution NoUnity " + Guid.NewGuid(), isCollectible: true);
            foreach (var path in paths) { context.LoadFromAssemblyPath(Path.GetFullPath(path)); }
        }
        public static void Unload() { var previous = context; context = null; previous?.Unload(); }
    }
}
namespace UnityEditor.PackageManager
{
    public enum PackageSource { Local, LocalTarball, Git, Registry, BuiltIn, Embedded, Unknown }
    public class GitInfo { public string hash; public string revision; }
    public class RegistryInfo { public string url; }
    public class Error { public string message; }
    public class DependencyInfo { public string name; public string version; }
    public class PackageInfo
    {
        public string name; public string version; public string packageId; public string assetPath; public string resolvedPath; public PackageSource source; public GitInfo git;
        public RegistryInfo registry; public bool isDirectDependency;
        public Error[] errors = Array.Empty<Error>(); public DependencyInfo[] dependencies = Array.Empty<DependencyInfo>();
        public static PackageInfo[] Registered = Array.Empty<PackageInfo>();
        public static int ReadCount; public static bool FailObservation;
        public static PackageInfo[] GetAllRegisteredPackages()
        {
            ReadCount++;
            if (FailObservation) { throw new InvalidOperationException("NoUnity PackageInfo observation unavailable"); }
            return Registered;
        }
        public static PackageInfo FindForAssetPath(string path) => null;
    }
}

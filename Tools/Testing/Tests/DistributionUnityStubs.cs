// NoUnity contract support only. Real Unity verification never includes this file.
using System;
namespace UnityEngine
{
    public static class Application { public static string dataPath; public static string unityVersion = "2022.3.62f3"; public static bool isBatchMode = true; }
    public static class Debug { public static void LogException(Exception error) { throw new InvalidOperationException("Unexpected owned execution failure", error); } }
    public class GameObject
    {
        public GameObject(string name) { }
        public object AddComponent(Type type) => Activator.CreateInstance(type);
        public T[] GetComponentsInChildren<T>(bool includeInactive) => Array.Empty<T>();
    }
    public class Transform { public GameObject gameObject; }
}
namespace UnityEditor
{
    public static class EditorUtility { public static bool scriptCompilationFailed; }
    public static class EditorApplication
    {
        public static bool isCompiling; public static bool isUpdating;
        public static void Exit(int code) { throw new InvalidOperationException("NoUnity contracts must return without EditorApplication.Exit: " + code); }
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
        public static string AssetPathToGUID(string path) => "";
        public static string[] FindAssets(string filter, string[] roots) => Array.Empty<string>();
        public static string GUIDToAssetPath(string guid) => "";
        public static T LoadAssetAtPath<T>(string path) => default;
    }
    public static class GameObjectUtility { public static int GetMonoBehavioursWithMissingScriptCount(UnityEngine.GameObject value) => 0; }
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
    public class Assembly { public string name; public string[] sourceFiles = Array.Empty<string>(); public Assembly[] assemblyReferences = Array.Empty<Assembly>(); public string[] compiledAssemblyReferences = Array.Empty<string>(); }
    public static class CompilationPipeline { public static Assembly[] GetAssemblies(AssembliesType type) => Array.Empty<Assembly>(); }
}
namespace UnityEditor.PackageManager
{
    public enum PackageSource { Local, Git, Registry, BuiltIn, Embedded, Unknown }
    public class GitInfo { public string hash; public string revision; }
    public class Error { public string message; }
    public class DependencyInfo { public string name; public string version; }
    public class PackageInfo
    {
        public string name; public string version; public string packageId; public string assetPath; public string resolvedPath; public PackageSource source; public GitInfo git;
        public Error[] errors = Array.Empty<Error>(); public DependencyInfo[] dependencies = Array.Empty<DependencyInfo>();
        public static PackageInfo[] Registered = Array.Empty<PackageInfo>();
        public static PackageInfo[] GetAllRegisteredPackages() => Registered;
        public static PackageInfo FindForAssetPath(string path) => null;
    }
}

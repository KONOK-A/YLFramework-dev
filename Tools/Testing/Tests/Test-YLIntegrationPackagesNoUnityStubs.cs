// NoUnity contract support only; never staged into a Unity consumer.
using System;
namespace UnityEngine
{
    public static class Application { public static string dataPath; public static string unityVersion = "2022.3.62f3"; public static bool isBatchMode = true; }
    public static class Debug { public static void LogException(Exception error) { throw new InvalidOperationException("NoUnity unexpected execution", error); } }
}
namespace UnityEditor
{
    public enum BuildTarget { StandaloneWindows64 }
    public enum BuildTargetGroup { Standalone }
    public static class EditorUtility { public static bool scriptCompilationFailed; }
    public static class EditorApplication
    {
        public static string applicationContentsPath;
        public static bool isCompiling; public static bool isUpdating;
        public static void Exit(int code) => throw new InvalidOperationException("NoUnity cannot exit an Editor: " + code);
    }
    public static class PlayerSettings { public static string GetScriptingDefineSymbols(Build.NamedBuildTarget target) => ""; }
}
namespace UnityEditor.Build { public struct NamedBuildTarget { public static NamedBuildTarget Standalone; } }
namespace UnityEditor.Build.Player
{
    public enum ScriptCompilationOptions { None }
    public struct ScriptCompilationSettings { public UnityEditor.BuildTargetGroup group; public UnityEditor.BuildTarget target; public ScriptCompilationOptions options; }
    public struct ScriptCompilationResult { public System.Collections.Generic.List<string> assemblies; }
    public static class PlayerBuildInterface
    {
        public static ScriptCompilationResult CompilePlayerScripts(ScriptCompilationSettings settings, string output) => throw new InvalidOperationException("NoUnity cannot compile Player scripts.");
    }
}
namespace UnityEditor.Compilation
{
    public enum AssembliesType { Editor, PlayerWithoutTestAssemblies }
    public class Assembly
    {
        public string name; public string outputPath; public string[] defines = Array.Empty<string>(); public string[] sourceFiles = Array.Empty<string>();
        public Assembly[] assemblyReferences = Array.Empty<Assembly>(); public string[] compiledAssemblyReferences = Array.Empty<string>();
    }
    public static class CompilationPipeline
    {
        public static readonly System.Collections.Generic.Dictionary<string, string> DefinitionPaths = new System.Collections.Generic.Dictionary<string, string>();
        public static Assembly[] GetAssemblies(AssembliesType type) => Array.Empty<Assembly>();
        public static string GetAssemblyDefinitionFilePathFromAssemblyReference(string reference) => DefinitionPaths.TryGetValue(reference, out var path) ? path : null;
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
        public string name; public string version; public string assetPath; public string resolvedPath; public string packageId; public PackageSource source; public GitInfo git;
        public RegistryInfo registry; public bool isDirectDependency;
        public Error[] errors = Array.Empty<Error>(); public DependencyInfo[] dependencies = Array.Empty<DependencyInfo>();
        public static PackageInfo[] Registered = Array.Empty<PackageInfo>();
        public static PackageInfo[] GetAllRegisteredPackages() => Registered;
    }
}

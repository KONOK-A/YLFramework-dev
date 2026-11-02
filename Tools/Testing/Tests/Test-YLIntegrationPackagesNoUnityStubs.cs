// NoUnity contract support only; never staged into a Unity consumer.
using System;
namespace UnityEngine
{
    public static class Application { public static string dataPath; public static string unityVersion = "2022.3.62f3"; public static bool isBatchMode = true; }
    public static class Debug { public static void LogException(Exception error) { throw new InvalidOperationException("NoUnity unexpected execution", error); } }
}
namespace UnityEditor
{
    public static class EditorUtility { public static bool scriptCompilationFailed; }
    public static class EditorApplication
    {
        public static bool isCompiling; public static bool isUpdating;
        public static void Exit(int code) => throw new InvalidOperationException("NoUnity cannot exit an Editor: " + code);
    }
    public static class PlayerSettings { public static string GetScriptingDefineSymbols(Build.NamedBuildTarget target) => ""; }
}
namespace UnityEditor.Build { public struct NamedBuildTarget { public static NamedBuildTarget Standalone; } }
namespace UnityEditor.Compilation
{
    public enum AssembliesType { Editor, PlayerWithoutTestAssemblies }
    public class Assembly
    {
        public string name; public string outputPath; public string[] defines = Array.Empty<string>(); public string[] sourceFiles = Array.Empty<string>();
        public Assembly[] assemblyReferences = Array.Empty<Assembly>(); public string[] compiledAssemblyReferences = Array.Empty<string>();
    }
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
        public string name; public string version; public string assetPath; public string resolvedPath; public string packageId; public PackageSource source; public GitInfo git;
        public Error[] errors = Array.Empty<Error>(); public DependencyInfo[] dependencies = Array.Empty<DependencyInfo>();
        public static PackageInfo[] Registered = Array.Empty<PackageInfo>();
        public static PackageInfo[] GetAllRegisteredPackages() => Registered;
    }
}

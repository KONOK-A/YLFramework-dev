// NoUnity boundary only. Never copied into a Unity host or a release package.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public static class Application
    {
        public static string dataPath;
        public static string unityVersion = "2022.3.62f3";
        public static bool isBatchMode = true;
    }
    public static class JsonUtility
    {
        public static string ToJson(object value, bool prettyPrint = false) => JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions { IncludeFields = true, WriteIndented = prettyPrint });
    }
}
namespace UnityEditor
{
    public sealed class PackageInfo { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class InitializeOnLoadMethodAttribute : Attribute { }
    public static class EditorUtility { public static bool scriptCompilationFailed; }
    public static class EditorApplication
    {
        public static string applicationPath;
        public static bool isCompiling, isUpdating;
        public static event Action update;
        public static event Action delayCall;
        public static readonly List<int> ExitCodes = new List<int>();
        public static void Exit(int code) { ExitCodes.Add(code); }
        public static void Reset() { update = null; delayCall = null; ExitCodes.Clear(); }
        public static void Pump() { var delayed = delayCall; delayCall = null; delayed?.Invoke(); update?.Invoke(); }
    }
}
namespace UnityEditor.PackageManager
{
    public enum StatusCode { InProgress, Success, Failure }
    public enum PackageSource { Unknown, Registry, BuiltIn, Local, Git, Embedded, LocalTarball }
    public sealed class Error { public string message; }
    public sealed class RegistryInfo { public string url; }
    public sealed class DependencyInfo { public string name, version; }
    public sealed class PackageInfo
    {
        public string name, version, packageId, resolvedPath;
        public PackageSource source;
        public bool isDirectDependency;
        public RegistryInfo registry;
        public DependencyInfo[] dependencies = Array.Empty<DependencyInfo>();
        public Error[] errors = Array.Empty<Error>();
        public static PackageInfo[] Registered = Array.Empty<PackageInfo>();
        public static PackageInfo[] GetAllRegisteredPackages() => Registered;
    }
    public static class Client
    {
        private static string manifest, lockPath, target;
        private static bool fail, retain;
        private static Requests.RemoveRequest pending;
        public static int RemoveCalls;
        public static void Configure(string manifestPath, string packagesLock, bool failure, bool retainIndirect)
        {
            manifest = manifestPath; lockPath = packagesLock; fail = failure; retain = retainIndirect; RemoveCalls = 0; pending = null;
        }
        public static Requests.ListRequest List(bool offlineMode, bool includeIndirectDependencies) => new Requests.ListRequest { Result = PackageInfo.Registered };
        public static Requests.RemoveRequest Remove(string packageName)
        {
            RemoveCalls++; target = packageName;
            pending = new Requests.RemoveRequest();
            EditorApplication.delayCall += () => EditorApplication.delayCall += CompleteRemoval;
            return pending;
        }
        public static void CompleteRemoval()
        {
            if (pending == null || pending.IsCompleted) return;
            pending.IsCompleted = true;
            pending.Status = fail ? StatusCode.Failure : StatusCode.Success;
            if (fail) { pending.Error = new Error { message = "controlled UPM failure" }; return; }
            var input = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest));
            input["dependencies"].AsObject().Remove(target);
            File.WriteAllText(manifest, input.ToJsonString());
            PackageInfo.Registered = retain ? PackageInfo.Registered.Where(p => p.name != target).ToArray() : Array.Empty<PackageInfo>();
            var locked = new Dictionary<string, object>();
            foreach (var p in PackageInfo.Registered)
                locked[p.name] = new { version = p.version, source = "registry", url = p.registry.url, depth = 1, dependencies = p.dependencies.ToDictionary(d => d.name, d => d.version) };
            File.WriteAllText(lockPath, JsonSerializer.Serialize(new { dependencies = locked }));
        }
    }
}
namespace UnityEditor.PackageManager.Requests
{
    public sealed class ListRequest
    {
        public bool IsCompleted = true;
        public UnityEditor.PackageManager.StatusCode Status = UnityEditor.PackageManager.StatusCode.Success;
        public UnityEditor.PackageManager.Error Error;
        public UnityEditor.PackageManager.PackageInfo[] Result;
    }
    public sealed class RemoveRequest
    {
        public bool IsCompleted;
        public UnityEditor.PackageManager.StatusCode Status = UnityEditor.PackageManager.StatusCode.InProgress;
        public UnityEditor.PackageManager.Error Error;
        public string Result => "removed";
    }
}

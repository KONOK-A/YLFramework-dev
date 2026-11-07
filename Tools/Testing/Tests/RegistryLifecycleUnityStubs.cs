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
        private static string manifest, lockPath;
        private static string[] additions, removals;
        private static Dictionary<string, PackageInfo> available;
        private static bool fail, retain;
        private static Requests.RemoveRequest pending;
        private static Requests.AddAndRemoveRequest changing;
        public static int RemoveCalls, AddAndRemoveCalls;
        public static bool FakeRestore;
        public static string PreserveRemoval = "";
        public static void Configure(string manifestPath, string packagesLock, bool failure, bool retainIndirect)
        {
            if (manifest != manifestPath) available = PackageInfo.Registered.ToDictionary(p => p.name, StringComparer.Ordinal);
            manifest = manifestPath; lockPath = packagesLock; fail = failure; retain = retainIndirect; RemoveCalls = 0; AddAndRemoveCalls = 0; pending = null; changing = null; FakeRestore = false;
        }
        public static Requests.ListRequest List(bool offlineMode, bool includeIndirectDependencies) => new Requests.ListRequest { Result = PackageInfo.Registered };
        public static Requests.RemoveRequest Remove(string packageName)
        {
            RemoveCalls++; additions = Array.Empty<string>(); removals = new[] { packageName };
            pending = new Requests.RemoveRequest();
            EditorApplication.delayCall += () => EditorApplication.delayCall += CompleteRemoval;
            return pending;
        }
        public static Requests.AddAndRemoveRequest AddAndRemove(string[] packagesToAdd, string[] packagesToRemove)
        {
            AddAndRemoveCalls++; additions = packagesToAdd; removals = packagesToRemove;
            changing = new Requests.AddAndRemoveRequest();
            EditorApplication.delayCall += () => EditorApplication.delayCall += CompleteRemoval;
            return changing;
        }
        public static void CompleteRemoval()
        {
            if ((pending == null || pending.IsCompleted) && (changing == null || changing.IsCompleted)) return;
            if (pending != null) { pending.IsCompleted = true; pending.Status = fail ? StatusCode.Failure : StatusCode.Success; pending.Error = fail ? new Error { message = "controlled UPM failure" } : null; }
            if (changing != null) { changing.IsCompleted = true; changing.Status = fail ? StatusCode.Failure : StatusCode.Success; changing.Error = fail ? new Error { message = "controlled UPM failure" } : null; }
            if (fail || (FakeRestore && additions.Length != 0)) return;
            var input = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest));
            foreach (var id in removals.Where(id => id != PreserveRemoval)) input["dependencies"].AsObject().Remove(id);
            foreach (var locator in additions)
            {
                var id = locator.StartsWith("file:", StringComparison.Ordinal) ? available.Keys.Single(key => Path.GetFileName(locator.Substring(5)).StartsWith(key + "-", StringComparison.Ordinal)) : locator.Substring(0, locator.IndexOf('@'));
                input["dependencies"][id] = locator.StartsWith("file:", StringComparison.Ordinal) ? locator : locator.Substring(locator.IndexOf('@') + 1);
            }
            File.WriteAllText(manifest, input.ToJsonString());
            var direct = input["dependencies"].AsObject().ToDictionary(p => p.Key, p => p.Value.GetValue<string>(), StringComparer.Ordinal);
            var depths = direct.Keys.ToDictionary(id => id, id => 0, StringComparer.Ordinal);
            var queue = new Queue<string>(direct.Keys);
            while (queue.Count != 0)
            {
                var id = queue.Dequeue();
                foreach (var dep in available[id].dependencies)
                    if (!depths.ContainsKey(dep.name)) { depths[dep.name] = depths[id] + 1; queue.Enqueue(dep.name); }
            }
            var kept = new HashSet<string>(depths.Keys, StringComparer.Ordinal);
            if (retain) foreach (var p in PackageInfo.Registered.Where(p => !removals.Contains(p.name) || p.name == "com.kano.ylframework.data-assets")) kept.Add(p.name);
            PackageInfo.Registered = kept.OrderBy(id => id, StringComparer.Ordinal).Select(id => available[id]).ToArray();
            var locked = new Dictionary<string, object>();
            foreach (var p in PackageInfo.Registered)
            {
                p.isDirectDependency = direct.ContainsKey(p.name);
                locked[p.name] = new { version = p.source == PackageSource.LocalTarball ? (direct.TryGetValue(p.name, out var locator) ? locator : p.packageId.Substring(p.name.Length + 1)) : p.version, source = p.source == PackageSource.LocalTarball ? "local-tarball" : p.source.ToString().ToLowerInvariant(), url = p.registry?.url,
                    depth = depths.TryGetValue(p.name, out var depth) ? depth : 1, dependencies = p.dependencies.ToDictionary(d => d.name, d => d.version) };
            }
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
    public sealed class AddAndRemoveRequest
    {
        public bool IsCompleted;
        public UnityEditor.PackageManager.StatusCode Status = UnityEditor.PackageManager.StatusCode.InProgress;
        public UnityEditor.PackageManager.Error Error;
    }
}

namespace UnityEditor.Compilation
{
    public enum AssembliesType { Editor, Player }
    public sealed class Assembly
    {
        public string name;
        public string[] sourceFiles = Array.Empty<string>(), compiledAssemblyReferences = Array.Empty<string>();
        public Assembly[] assemblyReferences = Array.Empty<Assembly>();
    }
    public static class CompilationPipeline
    {
        public static Assembly[] GetAssemblies(AssembliesType mode)
        {
            var ids = new HashSet<string>(UnityEditor.PackageManager.PackageInfo.Registered.Select(p => p.name), StringComparer.Ordinal);
            var result = new List<Assembly>();
            if (ids.Contains("com.kano.ylframework"))
            {
                result.Add(new Assembly { name = "YLFramework.Runtime", sourceFiles = new[] { "Packages/com.kano.ylframework/Runtime/Product.cs" } });
                if (mode == AssembliesType.Editor) result.Add(new Assembly { name = "YLFramework.Editor", sourceFiles = new[] { "Packages/com.kano.ylframework/Editor/Product.cs" } });
            }
            if (ids.Contains("com.kano.ylframework.reddot") || ids.Contains("com.kano.ylframework.save"))
            {
                if (ids.Contains("com.kano.ylframework.save")) result.Add(new Assembly { name = "YLFramework.Save", sourceFiles = new[] { "Packages/com.kano.ylframework.save/Runtime/Product.cs" } });
                if (ids.Contains("com.kano.ylframework.reddot"))
                {
                    result.Add(new Assembly { name = "YLFramework.RedDots", sourceFiles = new[] { "Packages/com.kano.ylframework.reddot/Runtime/Product.cs" }, assemblyReferences = new[] { new Assembly { name = "YLFramework.Runtime" } } });
                    if (mode == AssembliesType.Editor) result.Add(new Assembly { name = "YLFramework.RedDots.Editor", sourceFiles = new[] { "Packages/com.kano.ylframework.reddot/Editor/Product.cs" } });
                    if (ids.Contains("com.kano.ylframework.save")) result.Add(new Assembly { name = "YLFramework.RedDots.Save", sourceFiles = new[] { "Packages/com.kano.ylframework.reddot/Runtime/Integration/Save/Product.cs" }, assemblyReferences = new[] { new Assembly { name = "YLFramework.RedDots" }, new Assembly { name = "YLFramework.Save" } } });
                }
            }
            if (ids.Contains("com.kano.ylframework.gameplay"))
            {
                foreach (var name in new[] { "Skill", "Buff", "Mission", "Equipment" }) result.Add(new Assembly { name = "YLFramework." + name, sourceFiles = new[] { "Packages/com.kano.ylframework.gameplay/Runtime/" + name + "/Product.cs" } });
                if (mode == AssembliesType.Editor) result.Add(new Assembly { name = "YLFramework.Gameplay.Editor", sourceFiles = new[] { "Packages/com.kano.ylframework.gameplay/Editor/Product.cs" } });
            }
            foreach (var entry in new[] {
                (id: "data-assets", runtime: "DataAssets", editor: "DataAssets.Editor"), (id: "localization", runtime: "Localization", editor: "Localization.Editor"),
                (id: "state-machine", runtime: "StateMachine", editor: "StateMachine.Editor"), (id: "overlap", runtime: "Overlap", editor: "Overlap.Editor"),
                (id: "movement2d", runtime: "Character2D", editor: "Movement2D.Editor"), (id: "hybridclr", runtime: "HybridCLR", editor: "HybridCLR.Editor") })
            {
                if (!ids.Contains("com.kano.ylframework." + entry.id)) continue;
                result.Add(new Assembly { name = "YLFramework." + entry.runtime, sourceFiles = new[] { "Packages/com.kano.ylframework." + entry.id + "/Runtime/Product.cs" } });
                if (entry.id == "movement2d") result.Add(new Assembly { name = "YLFramework.Platform2D", sourceFiles = new[] { "Packages/com.kano.ylframework.movement2d/Runtime/Platform/Product.cs" } });
                if (mode == AssembliesType.Editor) result.Add(new Assembly { name = "YLFramework." + entry.editor, sourceFiles = new[] { "Packages/com.kano.ylframework." + entry.id + "/Editor/Product.cs" } });
            }
            if (mode == AssembliesType.Editor && ids.Contains("com.kano.ylframework.excel"))
            {
                var excel = new Assembly { name = "YLFramework.Excel.Editor", sourceFiles = new[] { "Packages/com.kano.ylframework.excel/Editor/Excel.cs" }, compiledAssemblyReferences = new[] { "Library/PackageCache/com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll" } };
                result.Add(excel);
                if (ids.Contains("com.kano.ylframework.gameplay")) result.Add(new Assembly { name = "YLFramework.Domain.Excel.Editor", sourceFiles = new[] { "Packages/com.kano.ylframework.excel/Editor/Gameplay/Adapter.cs" }, assemblyReferences = result.ToArray() });
            }
            return result.ToArray();
        }
    }
}

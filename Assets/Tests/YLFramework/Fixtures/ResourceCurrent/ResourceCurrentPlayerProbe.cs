using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceCurrentPlayerProbe : MonoBehaviour
    {
        public string firstAddress;
        public string secondAddress;
        public string contentHash;

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 2;
            public int pid;
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public bool resourceLoaded;
            public string contentHash;
            public bool ledgerMeasured;
            public string resourceState;
            public int leaseCount;
            public int scopeCount;
            public int requestCount;
            public int pendingWorkCount;
            public int drainingHandleCount;
            public bool independentHeldAfterScopeClose;
            public string probeAssembly;
            public string[] probeReferences;
            public string[] assemblies;
        }

        private IEnumerator Start()
        {
            var report = new Report
            {
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                isEditor = Application.isEditor,
                runtimeAssembly = typeof(ResourceSystem).Assembly.GetName().Name,
                probeAssembly = GetType().Assembly.GetName().Name,
                probeReferences = GetType().Assembly.GetReferencedAssemblies().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray()
            };
            string error = null;
            var operation = Execute(report);
            try
            {
                while (true)
                {
                    bool next;
                    try
                    {
                        next = operation.MoveNext();
                    }
                    catch (Exception exception) { error = exception.ToString(); break; }
                    if (!next)
                        break;
                    yield return operation.Current;
                }
            }
            finally { (operation as IDisposable)?.Dispose(); }
            report.assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(value => value.GetName().Name).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (error == null && (report.probeReferences.Any(IsTestAssembly) || report.assemblies.Any(IsTestAssembly)))
                error = "Editor or test assemblies entered the ordinary Player.";
            report.status = error == null ? "Passed" : "Failed";
            report.error = error;
            using (var output = new FileStream(Argument("-ylFeatureResult"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(output))
                writer.Write(JsonUtility.ToJson(report, true));
            Application.Quit(error == null ? 0 : 1);
        }

        private IEnumerator Execute(Report report)
        {
            if (ResourceSystem.Current != null)
                throw new InvalidOperationException("The Player booted an implicit Resource owner.");
            report.contentHash = HashContent(Path.Combine(Application.streamingAssetsPath, "aa"));
            if (string.IsNullOrEmpty(contentHash) || report.contentHash != contentHash)
                throw new InvalidDataException("Installed official Addressables bytes differ from the frozen build.");
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = startup.StartAsync();
            yield return start;
            start.TakeResult();
            var resources = startup.Resources;
            var first = resources.CreateScope("current-first");
            var second = resources.CreateScope("current-second");
            using var firstLoad = first.LoadAsync<TextAsset>(firstAddress);
            using var secondLoad = second.LoadAsync<TextAsset>(secondAddress);
            yield return firstLoad;
            yield return secondLoad;
            var firstAsset = firstLoad.TakeResult();
            var secondAsset = secondLoad.TakeResult();
            if (firstAsset.text != "current resource first" || secondAsset.text != "current resource second")
                throw new InvalidDataException("A formal address loaded different content.");
            using var acquired = resources.AcquireAsync<TextAsset>(firstAddress);
            yield return acquired;
            using var independent = acquired.TakeResult();
            using var firstClose = first.CloseAsync();
            yield return firstClose;
            firstClose.TakeResult();
            report.independentHeldAfterScopeClose = independent.Asset == firstAsset && independent.Asset.text == "current resource first";
            if (!report.independentHeldAfterScopeClose)
                throw new InvalidOperationException("Closing one Scope revoked an independent lease.");
            independent.Dispose();
            using var secondClose = second.CloseAsync();
            yield return secondClose;
            secondClose.TakeResult();
            using var stopped = startup.StopAsync();
            yield return stopped;
            stopped.TakeResult();
            var snapshot = resources.GetDiagnostics();
            report.ledgerMeasured = true;
            report.resourceState = snapshot.State.ToString();
            report.leaseCount = snapshot.LeaseCount;
            report.scopeCount = snapshot.ScopeCount;
            report.requestCount = snapshot.RequestCount;
            report.pendingWorkCount = snapshot.PendingWorkCount;
            report.drainingHandleCount = snapshot.DrainingHandleCount;
            if (ResourceSystem.Current != null || report.resourceState != "Closed" || report.leaseCount != 0 ||
                report.scopeCount != 0 || report.requestCount != 0 || report.pendingWorkCount != 0 || report.drainingHandleCount != 0 || snapshot.Errors.Count != 0)
                throw new InvalidOperationException("The resource owner did not close with an empty responsibility ledger.");
            report.resourceLoaded = true;
        }

        private static string HashContent(string directory)
        {
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);
            var rows = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(path =>
            {
                for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked Player content is not an installation proof.");
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return path.Substring(directory.Length + 1).Replace('\\', '/') + "=" + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (rows.Length == 0)
                throw new InvalidDataException("The installed official content is empty.");
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", rows)))).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsTestAssembly(string name) => name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
            name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("YLFramework.Core.Tests") || name.Contains("YLFramework.Tests.Fixtures");

        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, key);
            if (index < 0 || index + 1 >= args.Length)
                throw new ArgumentException("Missing Player argument: " + key);
            return args[index + 1];
        }
    }
}

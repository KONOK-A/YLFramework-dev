using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceCurrentPlayerProbe : MonoBehaviour
    {
        public string projectId;
        public string fromDigest;
        public string targetDigest;
        public string compatibility;

        [Serializable]
        private sealed class Report
        {
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public string probeAssembly;
            public string[] assemblies;
            public string[] probeReferences;
            public string catalogDigest;
            public int pid;
            public int leaseCount;
            public int recordCount;
            public bool currentTextLoaded;
            public bool updatedTextLoaded;
            public bool rolledBack;
            public bool versionTwoWithEmptyFloors;
        }

        private readonly Report _report = new();

        private IEnumerator Start()
        {
            var operation = Run();
            while (true)
            {
                bool next;
                object current;
                try
                {
                    next = operation.MoveNext();
                    current = operation.Current;
                }
                catch (Exception error)
                {
                    Finish("Failed", error.ToString());
                    yield break;
                }
                if (!next)
                {
                    break;
                }

                yield return current;
            }
            Finish("Passed", null);
        }

        private IEnumerator Run()
        {
            Require(!Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer, "An actual Windows Player is required.");
            _report.runtimeAssembly = typeof(ResourceSystem).Assembly.GetName().Name;
            Require(_report.runtimeAssembly == "YLFramework.Runtime", "Resource must use its real runtime assembly.");
            var probe = typeof(ResourceCurrentPlayerProbe).Assembly;
            _report.probeAssembly = probe.GetName().Name;
            _report.probeReferences = probe.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
            _report.assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToArray();
            Require(_report.probeAssembly == "YLFramework.Tests.ResourceCurrent.Player", "The explicit acceptance assembly is missing.");
            foreach (var name in _report.assemblies.Concat(_report.probeReferences))
            {
                Require(!name.StartsWith("UnityEditor", StringComparison.Ordinal) &&
                    name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !string.Equals(name, "nunit.framework", StringComparison.OrdinalIgnoreCase), "Unexpected Player dependency: " + name);
            }
            var baseline = ResourceLocalSnapshot.OpenBuiltIn(projectId, fromDigest, "StandaloneWindows64", compatibility);
            var target = ResourceLocalSnapshot.OpenBuiltIn(projectId, targetDigest, "StandaloneWindows64", compatibility);
            _report.catalogDigest = target.Document.CatalogDigest;
            var root = Path.Combine(Path.GetDirectoryName(Application.dataPath), "resource-store-" + Guid.NewGuid().ToString("N"));
            Require(!Directory.Exists(root) && !File.Exists(root), "Never adopt another Store.");
            var store = new ArtifactStore(root, new[] { new ResourceProjectMount(projectId, "StandaloneWindows64", compatibility, baseline) });
            var startup = new ResourceStartup(new ResourceConfiguration(store));
            using (var started = startup.StartAsync())
            {
                yield return started;
                Require(started.Status == ResourceRequestStatus.Succeeded, started.Error?.ToString());
                Require(ReferenceEquals(started.TakeResult(), startup) && startup.OwnsResourceSystem, "Startup must own its actual session.");
            }
            var resource = startup.Resources;
            var address = new ResourceAddress(projectId, "entry.txt");
            using (var lease = resource.Acquire<TextAsset>(address))
            {
                Require(lease.Asset.text == "current resource first", "The current publication did not load.");
                _report.currentTextLoaded = true;
            }
            UpdatePlan plan;
            using (var check = resource.CheckUpdatesAsync(new[] { target }))
            {
                yield return check;
                Require(check.Status == ResourceRequestStatus.Succeeded, check.Error?.ToString());
                plan = check.TakeResult();
            }
            PreparedUpdate prepared;
            using (var prepare = resource.PrepareUpdateAsync(plan))
            {
                yield return prepare;
                Require(prepare.Status == ResourceRequestStatus.Succeeded, prepare.Error?.ToString());
                prepared = prepare.TakeResult();
            }
            using (var activate = resource.ActivatePreparedUpdateAsync(prepared))
            {
                yield return activate;
                Require(activate.Status == ResourceRequestStatus.Succeeded && activate.TakeResult(), activate.Error?.ToString());
            }
            using (var lease = resource.Acquire<TextAsset>(address))
            {
                Require(lease.Asset.text == "current resource second", "The prepared publication was not activated.");
                _report.updatedTextLoaded = true;
            }
            foreach (var file in new[] { "active.json", "last-known-good.json" })
            {
                var text = File.ReadAllText(Path.Combine(root, projectId, "StandaloneWindows64", file));
                Require(text.Contains("\"Version\":2") && text.Contains("\"ReleaseFloors\":[]"), "The local Player Store did not write explicit v2 controls.");
            }
            _report.versionTwoWithEmptyFloors = true;
            using (var rollback = resource.RollbackAsync())
            {
                yield return rollback;
                Require(rollback.Status == ResourceRequestStatus.Succeeded && rollback.TakeResult(), rollback.Error?.ToString());
            }
            using (var lease = resource.Acquire<TextAsset>(address))
            {
                Require(lease.Asset.text == "current resource first", "Rollback did not restore the previous complete publication.");
                _report.rolledBack = true;
            }
            using (var stopped = startup.StopAsync())
            {
                yield return stopped;
                Require(stopped.Status == ResourceRequestStatus.Succeeded && stopped.TakeResult(), stopped.Error?.ToString());
            }
            _report.leaseCount = resource.GetDiagnosticsSnapshot().LeaseCount;
            _report.recordCount = resource.GetDiagnosticsSnapshot().RecordCount;
            Require(_report.leaseCount == 0 && _report.recordCount == 0 && ResourceSystem.Current == null, "The Player session left Resource owners behind.");
        }

        private void Finish(string status, string error)
        {
            try
            {
                var arguments = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(arguments, "-ylFeatureResult");
                Require(index > 0 && index < arguments.Length - 1, "The owned result locator is missing.");
                string path = Path.GetFullPath(arguments[index + 1]);
                string root = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                Require(string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetFileName(path) == "result-1.json" || Path.GetFileName(path) == "result-2.json") && !File.Exists(path), "Do not escape or overwrite the owned Player output.");
                _report.status = status;
                _report.error = error;
                _report.isEditor = Application.isEditor;
                _report.platform = Application.platform.ToString();
                _report.unityVersion = Application.unityVersion;
                _report.pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                File.WriteAllText(path, JsonUtility.ToJson(_report, true));
            }
            finally
            {
                Application.Quit(status == "Passed" ? 0 : 1);
            }
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition)
            {
                throw new InvalidOperationException(reason);
            }
        }
    }
}

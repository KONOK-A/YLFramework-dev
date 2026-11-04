using System;
using System.Collections;
using System.IO;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Resource;

namespace YLFramework.Tests.DataAssets
{
    public sealed class DataAssetPlayerProbe : MonoBehaviour
    {
        public string projectId;
        public string catalogDigest;
        public string compatibility;
        public int rowId;
        public string rowName;
        public string rowDescription;

        [Serializable]
        private sealed class Result
        {
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public string catalogDigest;
            public int pid;
            public int leaseCount;
            public int recordCount;
            public bool childSurvivedRootRelease;
        }

        private readonly Result _result = new Result();

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
            Require(!Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer, "A real Windows Player is required.");
            _result.runtimeAssembly = typeof(ScriptableObjectConfigs).Assembly.GetName().Name;
            Require(_result.runtimeAssembly == "YLFramework.DataAssets", "DataAssets must not come from the main assembly.");
            var snapshot = ResourceLocalSnapshot.OpenBuiltIn(projectId, catalogDigest, "StandaloneWindows64", compatibility);
            _result.catalogDigest = snapshot.Document.CatalogDigest;
            ResourceSystem resource;
            using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog(new[] { typeof(ScriptableObjectConfigs), typeof(ScriptableObjectData) }) })))
            {
                yield return initialized;
                Require(initialized.Status == ResourceRequestStatus.Succeeded, initialized.Error?.ToString());
                resource = initialized.TakeResult();
            }
            using (var root = ScriptableObjectManager.Load(resource, new ResourceAddress(projectId, "root")))
            using (var child = resource.Acquire<ScriptableObjectData>(new ResourceAddress(projectId, "row")))
            {
                Require(root.Config.GetName(rowId) == rowName, "The old asset row name was not preserved.");
                Require(child.Asset.parent == root.Config && child.Asset.description == rowDescription, "The old root/row relation was lost.");
                root.Dispose();
                Require(child.IsValid && child.Asset && resource.GetDiagnosticsSnapshot().LeaseCount == 1, "The root stole its child's real resource lifetime.");
                _result.childSurvivedRootRelease = true;
            }
            Require(ScriptableObjectManager.RegistrationCount == 0 && resource.GetDiagnosticsSnapshot().LeaseCount == 0, "A DataAssets owner leaked.");
            using (var shutdown = resource.ShutdownAsync())
            {
                yield return shutdown;
                Require(shutdown.Status == ResourceRequestStatus.Succeeded, shutdown.Error?.ToString());
            }
            _result.leaseCount = resource.GetDiagnosticsSnapshot().LeaseCount;
            _result.recordCount = resource.GetDiagnosticsSnapshot().RecordCount;
            Require(_result.leaseCount == 0 && _result.recordCount == 0, "Resource shutdown left records behind.");
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
                    (Path.GetFileName(path) == "result-1.json" || Path.GetFileName(path) == "result-2.json") && !File.Exists(path), "Do not overwrite or escape the owned Player output.");
                _result.status = status;
                _result.error = error;
                _result.isEditor = Application.isEditor;
                _result.platform = Application.platform.ToString();
                _result.unityVersion = Application.unityVersion;
                _result.pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                File.WriteAllText(path, JsonUtility.ToJson(_result, true));
            }
            finally { Application.Quit(status == "Passed" ? 0 : 1); }
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

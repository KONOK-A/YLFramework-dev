using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Resource;

namespace YLFramework.Tests.DataAssets
{
    public sealed class DataAssetPlayerProbe : MonoBehaviour
    {
        public string projectId;
        public string contentHash;
        public int rowId;
        public string rowName;
        public string rowDescription;

        [Serializable]
        private sealed class Result
        {
            public int schemaVersion = 2;
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public string contentHash;
            public bool ledgerMeasured;
            public string resourceState;
            public int pid;
            public int leaseCount = -1;
            public int scopeCount = -1;
            public int requestCount = -1;
            public int pendingWorkCount = -1;
            public int drainingHandleCount = -1;
            public bool childSurvivedRootRelease;
        }

        private readonly Result _result = new Result();
        private ResourceStartup _startup;
        private ResourceSystem _resources;

        private IEnumerator Start()
        {
            var operation = Run();
            while (true)
            {
                bool next = false;
                try
                {
                    next = operation.MoveNext();
                }
                catch (Exception error) { RecordFailure(error); }
                if (!next || _result.error != null)
                    break;
                yield return operation.Current;
            }
            try
            {
                (operation as IDisposable)?.Dispose();
            }
            catch (Exception error) { RecordFailure(error); }
            ResourceRequest<bool> stop = null;
            try
            {
                if (_startup != null)
                    stop = _startup.StopAsync();
            }
            catch (Exception error) { RecordFailure(error); }
            if (stop != null)
            {
                using (stop)
                {
                    double deadline = Time.realtimeSinceStartupAsDouble + 15;
                    while (!stop.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                        yield return null;
                    if (stop.Status != ResourceRequestStatus.Succeeded)
                        RecordFailure(new InvalidOperationException("Owned DataAssets resource stop failed or timed out: " + stop.Error));
                }
            }
            try
            {
                Require(_resources != null, "The resource service never started.");
                var report = _resources.GetDiagnostics();
                _result.leaseCount = report.LeaseCount;
                _result.scopeCount = report.ScopeCount;
                _result.requestCount = report.RequestCount;
                _result.pendingWorkCount = report.PendingWorkCount;
                _result.drainingHandleCount = report.DrainingHandleCount;
                _result.resourceState = report.State.ToString();
                _result.ledgerMeasured = true;
                Require(report.State == ResourceSystemState.Closed && ResourceSystem.Current == null &&
                    report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 &&
                    report.PendingWorkCount == 0 && report.DrainingHandleCount == 0 && report.Errors.Count == 0,
                    "DataAssets shutdown left owned resource responsibilities behind.");
                Require(ScriptableObjectManager.RegistrationCount == 0, "A DataAssets registration survived shutdown.");
            }
            catch (Exception error) { RecordFailure(error); }
            Finish();
        }

        private IEnumerator Run()
        {
            Require(!Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer, "A real Windows Player is required.");
            Require(ResourceSystem.Current == null && ScriptableObjectManager.RegistrationCount == 0, "Do not take over another owner's resources or registrations.");
            _result.runtimeAssembly = typeof(ScriptableObjectConfigs).Assembly.GetName().Name;
            Require(_result.runtimeAssembly == "YLFramework.DataAssets", "DataAssets must not come from the main assembly.");
            _result.contentHash = ReadContentHash();
            Require(_result.contentHash == contentHash, "The deployed official Addressables bytes differ from the frozen build.");
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using (var initialized = _startup.StartAsync())
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!initialized.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                Require(initialized.Status == ResourceRequestStatus.Succeeded, initialized.Error?.ToString() ?? "Resource startup timed out.");
                _resources = initialized.TakeResult().Resources;
            }
            using (var root = ScriptableObjectManager.LoadAsync(_resources, new ResourceAddress(projectId, "root")))
            using (var childRequest = _resources.AcquireAsync<ScriptableObjectData>(projectId + "/row"))
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while ((!root.IsDone || !childRequest.IsDone) && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                Require(root.Status == ResourceRequestStatus.Succeeded && root.IsValid, root.Error?.ToString() ?? "The root load timed out or failed.");
                Require(childRequest.Status == ResourceRequestStatus.Succeeded, childRequest.Error?.ToString() ?? "The row load timed out or failed.");
                using var child = childRequest.TakeResult();
                Require(root.Asset.GetName(rowId) == rowName, "The old asset row name was not preserved.");
                Require(ReferenceEquals(root.Asset.GetScriptableObjectData(rowId), child.Asset), "The root index did not borrow the published row.");
                Require(child.Asset.parent == root.Asset && child.Asset.description == rowDescription, "The old root/row relation was lost.");
                Require(_resources.GetDiagnostics().LeaseCount == 2, "The root and explicitly acquired row need independent leases.");
                root.Dispose();
                Require(child.IsValid && child.Asset && _resources.GetDiagnostics().LeaseCount == 1, "The root stole its child's real resource lifetime.");
                Require(ScriptableObjectManager.RegistrationCount == 0, "Disposing the root must end only its registration.");
                _result.childSurvivedRootRelease = true;
            }
            Require(ScriptableObjectManager.RegistrationCount == 0 && _resources.GetDiagnostics().LeaseCount == 0, "A DataAssets owner leaked.");
        }

        private static string ReadContentHash()
        {
            string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "aa"));
            Require(Directory.Exists(root), "The official local Addressables content is missing.");
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Require(files.Length != 0 && files.Any(path => Path.GetFileName(path) == "settings.json"), "The official runtime settings are missing.");
            var entries = files.Select(path =>
            {
                for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
                    Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "Do not read linked content as frozen Player evidence.");
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                return (Path: path.Substring(root.Length + 1).Replace('\\', '/'), Digest: digest);
            }).OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Path + "=" + entry.Digest);
            using var algorithm = SHA256.Create();
            return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).Replace("-", "").ToLowerInvariant();
        }

        private void RecordFailure(Exception error) => _result.error = _result.error == null ? error.ToString() : _result.error + "\n" + error;

        private void Finish()
        {
            bool persisted = false;
            try
            {
                var arguments = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(arguments, "-ylFeatureResult");
                Require(index > 0 && index < arguments.Length - 1 && arguments.Count(value => value == "-ylFeatureResult") == 1, "The owned result locator is missing or duplicated.");
                Require(Path.IsPathFullyQualified(arguments[index + 1]), "The result path must be absolute.");
                string path = Path.GetFullPath(arguments[index + 1]);
                string root = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                Require(string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetFileName(path) == "result-1.json" || Path.GetFileName(path) == "result-2.json") && !File.Exists(path) && !Directory.Exists(path), "Do not overwrite or escape the owned Player output.");
                for (var directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
                    Require((directory.Attributes & FileAttributes.ReparsePoint) == 0, "Do not write through linked Player output.");
                _result.status = _result.error == null ? "Passed" : "Failed";
                _result.isEditor = Application.isEditor;
                _result.platform = Application.platform.ToString();
                _result.unityVersion = Application.unityVersion;
                _result.pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(JsonUtility.ToJson(_result, true));
                writer.Flush();
                persisted = true;
            }
            finally { Application.Quit(_result.error == null && persisted ? 0 : 1); }
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition)
                throw new InvalidOperationException(reason);
        }
    }
}

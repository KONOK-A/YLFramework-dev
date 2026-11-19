using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using YLFramework.Equipment;
using YLFramework.Resource;

namespace YLFramework.Equipment.Acceptance
{
    public sealed class EquipmentPlayerProbe : MonoBehaviour
    {
        public string projectId = "equipment-acceptance";
        public string contentHash;
        public string contentToken;
        [Serializable]
        private sealed class Result
        {
            public int schemaVersion = 2;
            public string status;
            public string run;
            public string backend;
            public string unity;
            public string error;
            public string window;
            public bool passed;
            public bool quitObserved;
            public bool development;
            public bool graphical;
            public int width;
            public int height;
            public string[] checks;
            public string platform;
            public string runtimeAssembly;
            public string equipmentAssembly;
            public string probeAssembly;
            public int pid;
            public string contentHash;
            public string contentToken;
            public bool ledgerMeasured;
            public string resourceState;
            public int leaseCount = -1;
            public int scopeCount = -1;
            public int requestCount = -1;
            public int pendingWorkCount = -1;
            public int drainingHandleCount = -1;
            public bool independentRootSurvived;
            public bool managersClosed;
            public bool resourceReturned;
        }
        private sealed class Host : IEquippable
        {
            public List<IEquipment> Equipments { get; } = new();
            public bool CanAcceptEquipment = true;
            public bool CanEquip(IEquipment equipment) => CanAcceptEquipment;
        }
        private sealed class Borrowed : IEquipment
        {
            public IEquippable Equipper
            {
                get; set;
            }
            public EquipmentConfigData EquipmentData
            {
                get; set;
            }
            public int Unloads;
            public void OnLoaded() => throw new InvalidOperationException("Borrowed object must not be acquired.");
            public void OnUnloaded() => Unloads++;
            public void OnEquip()
            {
            }
            public void OnUnequip()
            {
            }
        }
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        private readonly List<string> _checks = new();
        private Result _result;
        private string _output;
        private ResourceStartup _startup;
        private ResourceSystem _resources;
        private ResourceScope _prefabScope;
        private AssetLease<EquipmentsConfig> _independent;
        private Task _configLoad;
        private Task<IEquipment> _creation;
        private bool _ownsEquipment;
        private Host _host;
        private Host _other;
        private string _rootName;
        private string _runtimeError;
        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        private void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Equipment Player contract: " + name);
            }
            _checks.Add(name);
        }
        private IEnumerator Start()
        {
            _output = Argument("--eq-result");
            _result = new Result
            {
                run = Argument("--eq-run"),
                unity = Application.unityVersion,
                platform = Application.platform.ToString(),
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                runtimeAssembly = typeof(ResourceSystem).Assembly.GetName().Name,
                equipmentAssembly = typeof(EquipmentManager).Assembly.GetName().Name,
                probeAssembly = GetType().Assembly.GetName().Name,
                development = Debug.isDebugBuild,
#if ENABLE_IL2CPP
                backend = "IL2CPP"
#else
                backend = "Mono"
#endif
            };
            Application.quitting += Quitting;
            Application.logMessageReceived += ObserveError;
            foreach (var phase in new[] { Run(), Cleanup() })
            {
                var work = Drive(phase);
                try { while (work.MoveNext()) yield return work.Current; }
                finally { (work as IDisposable)?.Dispose(); }
            }
            try
            {
                if (_resources != null) MeasureLedger();
                if (_runtimeError != null) RecordFailure(new InvalidOperationException(_runtimeError));
                Check(_runtimeError == null, "no-unexpected-runtime-error");
                _result.resourceReturned = _resources != null && _result.ledgerMeasured && _result.resourceState == ResourceSystemState.Closed.ToString() &&
                    ResourceSystem.Current == null && _result.leaseCount == 0 && _result.scopeCount == 0 && _result.requestCount == 0 &&
                    _result.pendingWorkCount == 0 && _result.drainingHandleCount == 0;
                Check(_result.resourceReturned, "owner-release-zero-yl-ledger");
                Check(_result.contentHash == ReadContentHash(), "deployed-content-unchanged");
            }
            catch (Exception error) { RecordFailure(error); }
            Application.logMessageReceived -= ObserveError;
            _result.passed = _result.error == null;
            _result.status = _result.passed ? "Passed" : "Failed";
            _result.checks = _checks.ToArray();
            if (_result.passed) Debug.Log("YL_EQUIPMENT_PLAYER_PASS run=" + _result.run + "; backend=" + _result.backend + "; checks=" + _checks.Count);
            else Debug.LogError("YL_EQUIPMENT_PLAYER_FAIL " + _result.run + " " + _result.error);
            Application.Quit(_result.passed ? 0 : 1);
        }

        private IEnumerator Drive(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            try
            {
                while (stack.Count != 0)
                {
                    var current = stack.Peek();
                    bool next;
                    try { next = current.MoveNext(); }
                    catch (Exception error) { RecordFailure(error); break; }
                    if (!next)
                    {
                        stack.Pop();
                        try { (current as IDisposable)?.Dispose(); }
                        catch (Exception error) { RecordFailure(error); }
                    }
                    else if (current.Current is IEnumerator child) stack.Push(child);
                    else yield return current.Current;
                }
            }
            finally
            {
                while (stack.Count != 0)
                {
                    try { (stack.Pop() as IDisposable)?.Dispose(); }
                    catch (Exception error) { RecordFailure(error); }
                }
            }
        }
        private IEnumerator Run()
        {
            Check(!Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer && !Debug.isDebugBuild &&
                !string.IsNullOrEmpty(_output) && !string.IsNullOrEmpty(_result.run), "real-non-development-player");
            Check(Path.IsPathFullyQualified(_output), "absolute-owned-output");
            _output = Path.GetFullPath(_output);
            Check(Directory.Exists(Path.GetDirectoryName(_output)) && !File.Exists(_output) && !Directory.Exists(_output), "new-owned-output");
            Check(_result.runtimeAssembly == "YLFramework.Runtime" && _result.equipmentAssembly == "YLFramework.Equipment" &&
                _result.probeAssembly == "YLFramework.Equipment.Acceptance.Player", "actual-runtime-assembly-boundaries");
            Check(Guid.TryParseExact(contentToken, "N", out _) && contentHash != null && contentHash.Length == 64 &&
                contentHash.All(Uri.IsHexDigit), "frozen-publication-identity");
            _result.contentToken = contentToken;
            _result.contentHash = ReadContentHash();
            Check(_result.contentHash == contentHash, "actual-official-content-hash");
            for (var i = 0; i < 12; i++)
            {
                yield return null;
            }
            _result.window = GetActiveWindow().ToInt64().ToString("X");
            _result.width = Screen.width;
            _result.height = Screen.height;
            _result.graphical = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && !Application.isBatchMode;
            Check(_result.graphical && _result.window != "0" && Screen.width > 0 && Screen.height > 0, "real-window-and-graphics");
            Check(ResourceSystem.Current == null, "no-foreign-resource-authority");
            Check(EquipmentManager.GetEquipmentInfo(1001) == null && EquipmentManager.GetEquipmentInfo(1002) == null, "no-foreign-equipment-ids");
            var configAddress = new ResourceAddress(projectId, "config");
            string prefabKey = projectId + "/prefab";
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using (var init = _startup.StartAsync())
            {
                yield return Wait(() => init.IsDone, "Resource startup");
                Check(init.Status == ResourceRequestStatus.Succeeded, "official-resource-startup");
                _resources = init.TakeResult().Resources;
            }
            using (var loading = _resources.AcquireAsync<EquipmentsConfig>(configAddress))
            {
                yield return Wait(() => loading.IsDone, "independent root");
                Check(loading.Status == ResourceRequestStatus.Succeeded, "independent-root-loaded");
                _independent = loading.TakeResult();
            }
            var root = _independent.Asset;
            _rootName = root.name;
            Check(root.Equipments.Count == 2 && root.Equipments.All(row => row && row.parent == root && row is ProbeEquipmentData &&
                ((ProbeEquipmentData)row).bonus == 42 && row.description == contentToken) &&
                root.GetEquipmentConfigData(1001).equipmentClassFullName == "YLFramework.Equipment.Acceptance.StringOnlyEquipment" &&
                root.GetEquipmentConfigData(1002).equipmentType == EquipmentType.Asset &&
                root.GetEquipmentConfigData(1002).projectName == projectId && root.GetEquipmentConfigData(1002).assetName == "prefab", "actual-published-definitions");
            _ownsEquipment = true;
            _configLoad = EquipmentManager.LoadConfigAsync(_resources, configAddress);
            yield return Wait(() => _configLoad.IsCompleted, "Equipment root registration");
            Check(_configLoad.Status == TaskStatus.RanToCompletion, "async-config-registration");
            _configLoad = EquipmentManager.LoadConfigAsync(_resources, configAddress);
            yield return Wait(() => _configLoad.IsCompleted, "idempotent root registration");
            Check(_configLoad.Status == TaskStatus.RanToCompletion && _resources.GetDiagnostics().Leases.Count(lease => lease.Key == projectId + "/config") == 2,
                "idempotent-config-keeps-independent-owners");
            _host = new Host();
            _other = new Host();
            var first = EquipmentManager.CreateEquipment(1001);
            Check(first.GetType().FullName == "YLFramework.Equipment.Acceptance.StringOnlyEquipment" && ((ProbeEquipmentData)first.EquipmentData).bonus == 42, "string-only-type-and-derived-definition");
            ((IAcceptanceState)first).State = 99;
            EquipmentManager.DestroyEquipment(first);
            var reused = EquipmentManager.CreateEquipment(1001);
            Check(ReferenceEquals(first, reused) && ((IAcceptanceState)reused).State == 0 && ((IAcceptanceState)reused).Loads == 2, "clr-cache-reset");
            Check(EquipmentManager.Equip(_host, reused) == EquipState.Success && EquipmentManager.Equip(_other, reused) == EquipState.AlreadyEquipped, "exclusive-instance-owner");
            Check(EquipmentManager.CreateAndEquip(_host, 1001) != null && _host.Equipments.Count == 2, "same-id-distinct-instances");
            _other.CanAcceptEquipment = false;
            Check(EquipmentManager.CreateAndEquip(_other, 1001) == null && _other.Equipments.Count == 0, "rejected-create-is-returned");
            _creation = EquipmentManager.CreateAndEquipAsync(_host, 1002);
            yield return Wait(() => _creation.IsCompleted, "cold Prefab equipment");
            Check(_creation.Status == TaskStatus.RanToCompletion && _creation.Result != null, "cold-prefab-async-checkout");
            var prefab = _creation.Result;
            _prefabScope = _resources.CreateScope("Equipment.Player.Prefab");
            AssetLease<GameObject> prefabLease;
            using (var loading = _resources.AcquireAsync<GameObject>(prefabKey))
            {
                yield return Wait(() => loading.IsDone, "retained Prefab");
                Check(loading.Status == ResourceRequestStatus.Succeeded, "scoped-prefab-loaded");
                prefabLease = _prefabScope.Own(loading.TakeResult());
            }
            Check(prefabLease.Asset.GetComponents<MonoBehaviour>().OfType<IEquipment>().Count() == 1 && prefab is PrefabEquipmentProbe,
                "real-prefab-root-component");
            var cache = _resources.GetOrCreateInstanceCache(prefabLease);
            ((IAcceptanceState)prefab).State = 77;
            EquipmentManager.DestroyEquipment(prefab);
            var prefabReused = EquipmentManager.CreateEquipment(1002);
            Check(ReferenceEquals(prefab, prefabReused) && ((IAcceptanceState)prefabReused).State == 0, "prefab-cache-reset");
            EquipmentManager.DestroyEquipment(prefabReused);
            EquipmentProbeControl.FailLoad = true;
            var failed = false;
            try
            {
                EquipmentManager.CreateEquipment(1002);
            }
            catch (InvalidOperationException error)
            {
                failed = error.Message == "EXPECTED_EQUIPMENT_LOAD_FAILURE";
            }
            var failedObject = (MonoBehaviour)prefabReused;
            yield return Wait(() => !failedObject, "failed Prefab actual destruction");
            Check(failed && !failedObject && cache.CachedCount == 0, "failed-prefab-discarded");
            var borrowed = new Borrowed { EquipmentData = _independent.Asset.Equipments[0] };
            Check(EquipmentManager.Equip(_host, borrowed) == EquipState.Success, "borrowed-attachment");
            var detached = EquipmentManager.CreateEquipment(1001);
            _creation = EquipmentManager.CreateAndEquipAsync(_host, 1002);
            yield return Wait(() => _creation.IsCompleted, "new Prefab after discard");
            Check(_creation.Status == TaskStatus.RanToCompletion && _creation.Result != null && !ReferenceEquals(prefabReused, _creation.Result), "discarded-prefab-not-reused");
            EquipmentManager.CloseHost(_host);
            EquipmentManager.Shutdown();
            Check(_host.Equipments.Count == 0 && _other.Equipments.Count == 0 && borrowed.Equipper == null && borrowed.Unloads == 0, "host-close-does-not-own-borrower");
            Check(detached.EquipmentData == null && ((IAcceptanceState)detached).Unloads > 0 && EquipmentManager.GetConfig(_independent.Asset.name) == null, "shutdown-returns-detached-and-config");
            _result.independentRootSurvived = _independent.IsValid && _independent.Asset && ReferenceEquals(ResourceSystem.Current, _resources);
            Check(_result.independentRootSurvived, "independent-root-lease-preserved");
        }

        private IEnumerator Cleanup()
        {
            if (_ownsEquipment)
            {
                foreach (Action close in new Action[] { () => { if (_host != null) EquipmentManager.CloseHost(_host); },
                    () => { if (_other != null) EquipmentManager.CloseHost(_other); }, EquipmentManager.Shutdown })
                {
                    try { close(); }
                    catch (Exception error) { RecordFailure(error); }
                }
                _result.managersClosed = (_host == null || _host.Equipments.Count == 0) && (_other == null || _other.Equipments.Count == 0) && EquipmentManager.GetConfig(_rootName) == null;
                CleanupCheck(_result.managersClosed, "owned-manager-returned");
            }
            foreach (var task in new Task[] { _configLoad, _creation })
            {
                if (task == null) continue;
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                if (!task.IsCompleted) RecordFailure(new TimeoutException("Owned Equipment task cleanup"));
                else _ = task.Exception;
            }
            _independent?.Dispose();
            _independent = null;
            _resources?.RetireInstanceCaches();
            if (_prefabScope != null)
            {
                using var close = _prefabScope.CloseAsync();
                yield return Wait(() => close.IsDone, "owned Prefab Scope close");
                CleanupCheck(close.Status == ResourceRequestStatus.Succeeded, "prefab-scope-returned");
            }
            if (_startup != null)
            {
                using var stop = _startup.StopAsync();
                yield return Wait(() => stop.IsDone, "owned Resource stop");
                CleanupCheck(stop.Status == ResourceRequestStatus.Succeeded, "owned-startup-stopped");
                if (stop.Status == ResourceRequestStatus.Succeeded) CleanupCheck(stop.TakeResult(), "owned-stop-result");
            }
        }

        private void MeasureLedger()
        {
            var report = _resources.GetDiagnostics();
            _result.leaseCount = report.LeaseCount;
            _result.scopeCount = report.ScopeCount;
            _result.requestCount = report.RequestCount;
            _result.pendingWorkCount = report.PendingWorkCount;
            _result.drainingHandleCount = report.DrainingHandleCount;
            _result.resourceState = report.State.ToString();
            _result.ledgerMeasured = true;
            Check(report.Errors.Count == 0, "no-resource-cleanup-errors");
        }

        private void CleanupCheck(bool condition, string name)
        {
            try { Check(condition, name); }
            catch (Exception error) { RecordFailure(error); }
        }
        private void Write()
        {
            Check(Path.IsPathFullyQualified(_output), "absolute-result-path");
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(_output)); directory != null; directory = directory.Parent)
                Check((directory.Attributes & FileAttributes.ReparsePoint) == 0, "non-linked-output-parent");
            using var stream = new FileStream(_output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            _result.checks = _checks.ToArray();
            writer.Write(JsonUtility.ToJson(_result, true));
            writer.Flush();
        }
        private void Quitting()
        {
            _result.quitObserved = true;
            try { Write(); }
            catch (Exception error) { Debug.LogError("YL_EQUIPMENT_RESULT_WRITE_FAIL " + error); }
            Application.quitting -= Quitting;
        }

        private void RecordFailure(Exception error) => _result.error = _result.error == null ? error.ToString() : _result.error + "\n" + error;

        private void ObserveError(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
                _runtimeError = (_runtimeError ?? "") + type + ": " + message + "\n" + stack + "\n";
        }

        private static IEnumerator Wait(Func<bool> complete, string operation)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!complete() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            if (!complete()) throw new TimeoutException(operation);
        }

        private static string ReadContentHash()
        {
            string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "aa"));
            if (!Directory.Exists(root)) throw new InvalidOperationException("Official local Addressables content is missing.");
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            if (!files.Any(path => Path.GetFileName(path) == "settings.json")) throw new InvalidOperationException("Official runtime settings are missing.");
            var entries = files.Select(path =>
            {
                for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked content is not frozen Player evidence.");
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return (Path: path.Substring(root.Length + 1).Replace('\\', '/'), Digest: BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
            }).OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Path + "=" + entry.Digest);
            using var algorithm = SHA256.Create();
            return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).Replace("-", "").ToLowerInvariant();
        }
    }
}

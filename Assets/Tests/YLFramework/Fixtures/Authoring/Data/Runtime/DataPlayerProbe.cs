using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Scripting;
using YLFramework.DataAssets;
using YLFramework.Localization;
using YLFramework.Resource;
using YLFramework.Save;

namespace YLFramework.Tests.DataBatch
{
    public sealed class DataPlayerProbe : MonoBehaviour
    {
        [SerializeField] private string resourcePrefix;
        [SerializeField] private string backend;
        [SerializeField] private DataPlayerTextSink sink;
        private string _output;
        private ResourceSystem _resources;
        private LocalizationConfigHandle _localization;
        private IDisposable _data;
        private readonly Report _report = new();

        [Serializable, Preserve]
        public sealed class Model : ISaveModel
        {
            public int Id
            {
                get; set;
            }
            public int Score;
            public string[] Names;
        }
        [Serializable, Preserve]
        public sealed class Report
        {
            public bool passed;
            public string backend;
            public string actualBackend;
            public bool development;
            public string graphics;
            public int pid;
            public List<string> errorLogs = new List<string>();
            public string unityVersion;
            public string persistentDataPath;
            public string error;
            public bool saveRoundTrip;
            public bool backupRecovery;
            public bool canceledWritePreserved;
            public bool concreteSoReflection;
            public bool localizedReflectionText;
            public bool playerAssembliesExcluded;
            public int exitLeaseCount;
            public string[] assemblies;
        }

        private IEnumerator Start()
        {
            Application.logMessageReceived += OnLog;
            _output = Argument("-dataProbeOutput");
            if (string.IsNullOrEmpty(_output) || !Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(_output)).Replace('\\', '/').StartsWith("G:/Unity/MB26/", StringComparison.Ordinal) || !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("G:/Unity/MB26/owner.json")))
            {
                Application.Quit(21);
                yield break;
            }
            var checks = Checks();
            while (true)
            {
                object instruction = null;
                bool next = false;
                Exception failure = null;
                try
                {
                    next = checks.MoveNext();
                    if (next)
                    {
                        instruction = checks.Current;
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
                if (failure != null)
                {
                    ((IDisposable)checks).Dispose();
                    Finish(failure);
                    yield break;
                }
                if (!next)
                {
                    ((IDisposable)checks).Dispose();
                    Finish(null);
                    yield break;
                }
                yield return instruction;
            }
        }

        private IEnumerator Checks()
        {
            _report.backend = backend;
#if ENABLE_IL2CPP
            _report.actualBackend = "IL2CPP";
#else
            _report.actualBackend = "Mono2x";
#endif
            _report.development = Debug.isDebugBuild;
            _report.graphics = SystemInfo.graphicsDeviceType + "/" + SystemInfo.graphicsDeviceName;
            _report.pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Require(!_report.development && _report.actualBackend == backend, "A new non-Development Player with the requested backend is required.");
            _report.unityVersion = Application.unityVersion;
            _report.persistentDataPath = Application.persistentDataPath;
            Require(_report.persistentDataPath.Contains("YL-MainBData-Player"), "Player persistent data is not isolated.");
            _report.assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToArray();
            Require(!_report.assemblies.Any(n => n.StartsWith("UnityEditor", StringComparison.Ordinal) || n.Contains("MiniExcel")), "Editor/backend assembly entered Player.");
            Require(_report.assemblies.Contains("YLFramework.DataAssets") && _report.assemblies.Contains("YLFramework.Localization"), "The real Data Player lost a moved runtime assembly.");
            _report.playerAssembliesExcluded = true;
            var loc = new ResourceAddress("bplayer", "localization");
            var config = new ResourceAddress("bplayer", "data");
            using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog("bplayer", new[]
                {
                    new ResourceCatalogEntry("localization", loc, new ResourceLocation(ResourceSourceId.Resources, resourcePrefix + "/Localization", typeof(MultiLanguageConfigs))),
                    new ResourceCatalogEntry("data", config, new ResourceLocation(ResourceSourceId.Resources, resourcePrefix + "/Data", typeof(ScriptableObjectConfigs)))
                })
            })))
            {
                yield return initialized;
                Require(initialized.Status == ResourceRequestStatus.Succeeded, initialized.Error?.ToString());
                _resources = initialized.TakeResult();
            }
            _localization = LocalizationManager.LoadConfigAsync(_resources, loc);
            while (_localization.IsLoading)
            {
                yield return null;
            }

            Require(_localization.Error == null && _localization.Config, _localization.Error?.ToString());
            _data = ScriptableObjectManager.Load(_resources, config);
            var root = ScriptableObjectManager.GetConfig("Data");
            Require(root.GetScriptableObjectData<DataPlayerFixture>(17).amount == 41, "Concrete data asset did not deserialize.");
            var type = Type.GetType(typeof(DataPlayerFixture).FullName + ", " + typeof(DataPlayerFixture).Assembly.GetName().Name, true);
            var reflected = ScriptableObject.CreateInstance(type);
            Require(reflected is DataPlayerFixture, "SO reflection/AOT resolution failed.");
            Destroy(reflected);
            _report.concreteSoReflection = true;
            LocalizationManager.Language = SystemLanguage.Japanese;
            sink.gameObject.SetActive(true);
            yield return null;
            Require(sink.text == "Native English" && sink.Enables == 1, "Localized Other text or non-Chinese fallback failed.");
            sink.GetComponent<LocalizeString>().Refresh();
            Require(sink.Enables == 1, "Refreshing restarted the business object.");
            _report.localizedReflectionText = true;
            RunFileChecks();
            sink.gameObject.SetActive(false);
            _localization.Dispose();
            _localization = null;
            _data.Dispose();
            _data = null;
            _report.exitLeaseCount = _resources.GetDiagnosticsSnapshot().LeaseCount;
            Require(_report.exitLeaseCount == 0, "Player leases did not return to zero.");
            using (var stopped = _resources.ShutdownAsync())
            {
                yield return stopped;
                Require(stopped.Status == ResourceRequestStatus.Succeeded, stopped.Error?.ToString());
            }
            _resources = null;
        }

        private void RunFileChecks()
        {
            string root = Path.Combine(_output, "save");
            using (var store = new SaveStore(root))
            {
                var model = store.CreateModel<Model>(3);
                model.Score = 7;
                model.Names = new[] { "a", "b" };
                store.SaveModelAsync(model).GetAwaiter().GetResult();
                model.Score = 8;
                store.SaveModel(model);
                store.ClearCache();
                Require(store.LoadModel<Model>(3).Score == 8, "Model round trip failed.");
                _report.saveRoundTrip = true;
                string path = Path.Combine(root, "SaveData/3/" + SaveStore.ModelFileName + ".json");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "damaged-primary");
                store.ClearCache();
                var recovered = store.ReadObject<Model>(SaveStore.ModelFileName, 3);
                Require(recovered.Status == SaveReadStatus.RecoveredFromBackup && recovered.Value.Score == 7 && File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)) == "damaged-primary", "Backup read modified or lost the primary.");
                store.RestoreObjectBackup(SaveStore.ModelFileName, 3);
                Require(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path)), "*.corrupt-*").Length == 1, "Recovery did not keep the damaged primary.");
                _report.backupRecovery = true;
                byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    bool rejected = false;
                    try
                    {
                        store.SaveModelAsync(new Model { Id = 3, Score = 99 }, canceled.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        rejected = true;
                    }
                    Require(rejected && File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)).SequenceEqual(before), "Canceled write changed the primary.");
                }
                _report.canceledWritePreserved = true;
            }
        }

        private void Finish(Exception error)
        {
            _localization?.Dispose();
            _data?.Dispose();
            if (error == null && _report.errorLogs.Count != 0)
            {
                error = new InvalidOperationException("Player emitted Error/Assert/Exception logs.");
            }

            _report.passed = error == null;
            _report.error = error?.ToString();
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_output, "player-result.json")), JsonUtility.ToJson(_report, true));
            if (error == null)
            {
                Debug.Log("B_DATA_PLAYER_OK " + backend);
            }
            else
            {
                Debug.LogException(error);
            }

            Application.Quit(error == null ? 0 : 20);
        }
        private void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _report.errorLogs.Add(type + ": " + condition + Environment.NewLine + stack);
            }
        }
        private void OnDestroy() => Application.logMessageReceived -= OnLog;
        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message ?? "B data probe failed.");
            }
        }
        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}

using System;
using System.Collections;
using System.IO;
using UnityEngine;
using YLFramework.Localization;
using YLFramework.Resource;

namespace YLFramework.Tests.Localization
{
    public sealed class LocalizationPlayerProbe : MonoBehaviour
    {
        public string projectId;
        public string catalogDigest;
        public string compatibility;
        public LocalizeString binding;
        public string Value
        {
            get; set;
        }

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
            public bool fallbackAndReflection;
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
                catch (Exception error) { Finish("Failed", error.ToString()); yield break; }
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
            Require(!Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer, "A real standalone Player is required.");
            _result.runtimeAssembly = typeof(LocalizationManager).Assembly.GetName().Name;
            Require(_result.runtimeAssembly == "YLFramework.Localization", "Localization must not remain in the main Runtime.");
            bool hadPreference = PlayerPrefs.HasKey(LocalizationManager.LanguagePreferenceKey);
            int preference = PlayerPrefs.GetInt(LocalizationManager.LanguagePreferenceKey);
            try
            {
                var snapshot = ResourceLocalSnapshot.OpenBuiltIn(projectId, catalogDigest, "StandaloneWindows64", compatibility);
                _result.catalogDigest = snapshot.Document.CatalogDigest;
                ResourceSystem resource;
                using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
                {
                    snapshot.CreateCatalog(new[] { typeof(MultiLanguageConfigs), typeof(LanguageConfig) })
                })))
                {
                    yield return initialized;
                    Require(initialized.Status == ResourceRequestStatus.Succeeded, initialized.Error?.ToString());
                    resource = initialized.TakeResult();
                }
                using (var root = LocalizationManager.LoadConfigAsync(resource, new ResourceAddress(projectId, "root")))
                {
                    while (root.IsLoading)
                    {
                        yield return null;
                    }
                    Require(root.Error == null && root.Config, root.Error?.ToString());
                    LocalizationManager.Language = SystemLanguage.Japanese;
                    binding.SetArgs(new[] { new AppendData { type = AppendType.String, str = "actor" } });
                    binding.enabled = true;
                    Require(Value == "hello actor", "The real fallback/property binding failed.");
                    binding.Append("!");
                    Require(Value == "hello actor!", "Append lost the reflected target.");
                    binding.ClearAppend();
                    Require(Value == "hello actor", "ClearAppend changed the primary value.");
                    binding.enabled = false;
                    Value = "disabled";
                    LocalizationManager.Refresh();
                    Require(Value == "disabled", "Disabled binding still receives callbacks.");
                    using (var language = resource.Acquire<LanguageConfig>(new ResourceAddress(projectId, "english")))
                    {
                        root.Dispose();
                        Require(language.IsValid && language.Asset.languagesInfo[0].str == "hello {0}", "A child language lost its independent lease.");
                    }
                    _result.fallbackAndReflection = true;
                }
                Require(LocalizationManager.Configs.Count == 0 && resource.GetDiagnosticsSnapshot().LeaseCount == 0, "Localization ownership leaked.");
                using (var shutdown = resource.ShutdownAsync())
                {
                    yield return shutdown;
                    Require(shutdown.Status == ResourceRequestStatus.Succeeded, shutdown.Error?.ToString());
                }
                _result.leaseCount = resource.GetDiagnosticsSnapshot().LeaseCount;
                _result.recordCount = resource.GetDiagnosticsSnapshot().RecordCount;
                Require(_result.leaseCount == 0 && _result.recordCount == 0, "Resource shutdown left records.");
            }
            finally
            {
                LocalizationManager.Shutdown();
                if (hadPreference)
                {
                    PlayerPrefs.SetInt(LocalizationManager.LanguagePreferenceKey, preference);
                }
                else
                {
                    PlayerPrefs.DeleteKey(LocalizationManager.LanguagePreferenceKey);
                }
                PlayerPrefs.Save();
            }
        }

        private void Finish(string status, string error)
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-ylFeatureResult");
                Require(index > 0 && index < args.Length - 1, "The owned result argument is missing.");
                string path = Path.GetFullPath(args[index + 1]);
                string parent = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                Require(string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetFileName(path) == "result-1.json" || Path.GetFileName(path) == "result-2.json") && !File.Exists(path), "Do not overwrite or escape Player output.");
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

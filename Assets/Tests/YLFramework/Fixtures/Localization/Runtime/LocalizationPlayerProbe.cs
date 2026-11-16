using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement.AsyncOperations;
using YLFramework.Binding;
using YLFramework.Localization;

namespace YLFramework.Tests.Localization
{
    public sealed class LocalizationPlayerProbe : MonoBehaviour
    {
        public LocalizedString text;
        public LocalizedSprite picture;
        public LocalizedFont font;
        public Locale english;
        public Locale french;
        private LocalizationSession _session;

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
            public bool officialContentLoaded;
            public bool bindingsDisposed;
            public string backend;
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
                catch (Exception error) { _session?.Dispose(); Finish("Failed", error.ToString()); yield break; }
                if (!next)
                    break;
                yield return current;
            }
            Finish("Passed", null);
        }

        private IEnumerator Run()
        {
            Require(!Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer, "A real Windows Player is required.");
            _result.runtimeAssembly = typeof(LocalizationSession).Assembly.GetName().Name;
#if ENABLE_IL2CPP
            _result.backend = "IL2CPP";
#else
            _result.backend = "Mono";
#endif
            var initialization = LocalizationSettings.InitializationOperation;
            yield return initialization;
            Require(initialization.Status == AsyncOperationStatus.Succeeded, initialization.OperationException?.ToString());
            LocalizationSettings.SelectedLocale = english;
            var count = new BindableProperty<int>(2);
            string value = null;
            Sprite loadedSprite = null;
            Font loadedFont = null;
            _session = new LocalizationSession();
            _session.BindString(text, result => value = result, binding => binding.BindVariable("count", count));
            _session.BindAsset(picture, result => loadedSprite = result);
            _session.BindAsset(font, result => loadedFont = result);
            var limit = Time.realtimeSinceStartup + 40;
            while ((!_session.IsReady || value == null || !loadedSprite || !loadedFont) && _session.Error == null && Time.realtimeSinceStartup < limit)
                yield return null;
            Require(_session.IsReady, _session.Error?.ToString() ?? "Initial official content timed out.");
            Require(value == "hello 2" && loadedSprite && loadedFont, "String/Sprite/Font did not load from the built official content. text=" + value + ", sprite=" + (bool)loadedSprite + ", font=" + (bool)loadedFont);
            count.Value = 5;
            for (var i = 0; i < 3; i++)
                yield return null;
            Require(value == "hello 5", "The observable variable did not refresh the official Smart String.");
            LocalizationSettings.SelectedLocale = french;
            limit = Time.realtimeSinceStartup + 40;
            while ((value != "salut 5" || !_session.IsReady) && _session.Error == null && Time.realtimeSinceStartup < limit)
                yield return null;
            Require(value == "salut 5" && _session.IsReady, _session.Error?.ToString() ?? "Locale switch failed.");
            _result.officialContentLoaded = true;
            _session.Dispose();
            value = "closed";
            count.Value = 9;
            LocalizationSettings.SelectedLocale = english;
            for (var i = 0; i < 3; i++)
                yield return null;
            Require(value == "closed", "A closed consumer received a late update.");
            _session = new LocalizationSession();
            _session.BindString(text, result => value = result, binding => binding.BindVariable("count", count));
            limit = Time.realtimeSinceStartup + 40;
            while ((value != "hello 9" || !_session.IsReady) && _session.Error == null && Time.realtimeSinceStartup < limit)
                yield return null;
            Require(value == "hello 9" && _session.IsReady, "Reopening did not create an independent current binding.");
            _session.Dispose();
            _result.bindingsDisposed = _session.IsDisposed;
            // 本探针没有启动 YL Resource；官方缓存不以这两个旧资源诊断字段冒充归零证据。
            _result.leaseCount = 0;
            _result.recordCount = 0;
        }

        private void Finish(string status, string error)
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(args, "-ylFeatureResult");
                Require(index > 0 && index < args.Length - 1, "The owned result argument is missing.");
                var path = Path.GetFullPath(args[index + 1]);
                var parent = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
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

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}

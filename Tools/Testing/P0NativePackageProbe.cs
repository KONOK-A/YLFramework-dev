using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.AddressableAssets;
using YLFramework.Resource;

namespace YLFramework.Testing
{
    public sealed class P0NativePackageProbe : MonoBehaviour
    {
        public const string Key = "acceptance/p0c/main";
        public const string ExtensionAssembly = "YLFramework.ResourceEncryption.P0";
        public const string Plugin = "ylresource_p0.dll";
        public const string ReleasePlugin = "ylresource_tink.dll";
        public const string ReleaseAssembly = "YLFramework.Resource.Encryption";

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string name);

        [Serializable]
        public sealed class Job
        {
            public string token, stage, playerRoot, ready, exit, catalogName, catalogSha256, pluginSha256, backend, inputHash;
            public long plainLength;
            public Range[] ranges;
            public bool finalArchive;
        }

        [Serializable]
        public sealed class Range
        {
            public long at;
            public int length;
            public string sha256;
        }

        [Serializable]
        private sealed class RunReport
        {
            public string token, stage, status, error, unityVersion, backend, architecture, graphics, assembly, pluginSha256, catalogSha256, inputHash;
            public int pid, bits, samples, leases, openBundles, requestedExitCode;
            public bool extensionLoaded, mainLoaded, exclusiveOpenAfterDispose, endOfFile, quit, disabledNativeInactive;
        }

        [SerializeField] private Job _job;
        private RunReport _report;
        private bool _finished;
        public void Configure(Job job) => _job = job;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            _report = new RunReport { token = _job.token, stage = _job.stage, unityVersion = Application.unityVersion, inputHash = _job.inputHash,
                graphics = SystemInfo.graphicsDeviceType.ToString(), bits = IntPtr.Size * 8, architecture = RuntimeInformation.ProcessArchitecture.ToString() };
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) _report.pid = process.Id;
#if ENABLE_IL2CPP
            _report.backend = "IL2CPP";
#elif ENABLE_MONO
            _report.backend = "Mono";
#else
            _report.backend = "Unknown";
#endif
            var stack = new Stack<IEnumerator>();
            stack.Push(Run());
            while (stack.Count != 0)
            {
                object current;
                try
                {
                    if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                    current = stack.Peek().Current;
                }
                catch (Exception error)
                {
                    foreach (var enumerator in stack) (enumerator as IDisposable)?.Dispose();
                    Finish(error);
                    yield break;
                }
                if (current is IEnumerator nested) stack.Push(nested);
                else yield return current;
            }
            Finish(null);
        }

        private IEnumerator Run()
        {
            Require(!Application.isEditor && Application.isBatchMode && !Debug.isDebugBuild && Application.platform == RuntimePlatform.WindowsPlayer &&
                _report.backend == _job.backend && _report.architecture == "X64" && _report.bits == 64 && _report.graphics != "Null",
                "Package evidence requires the requested Win64 Release graphics Player.");
            Require(Path.GetFullPath(Path.Combine(Application.dataPath, "..")) == _job.playerRoot, "The Player moved from the owned experiment.");
            _report.catalogSha256 = FileHash(Path.Combine(Addressables.RuntimePath, _job.catalogName));
            Require(_report.catalogSha256 == _job.catalogSha256, "The ordinary control must read its real shipped catalog.");
            string[] plugins = Directory.GetFiles(_job.playerRoot, _job.finalArchive ? ReleasePlugin : Plugin, SearchOption.AllDirectories);
            bool installed = _job.stage == "Installed";
#if YL_P0_PACKAGED_NATIVE
            Require(_job.stage == "Installed" && plugins.Length == 1, "The native plugin was not deployed by Unity from the frozen tarball.");
            _report.pluginSha256 = FileHash(plugins[0]);
            Require(_report.pluginSha256 == _job.pluginSha256, "The deployed native plugin differs from the approved hash.");
            _report.assembly = typeof(NativeP0.P0NativeReadStream).Assembly.GetName().Name;
            Require(_report.assembly == ExtensionAssembly, "The bridge must come from the installed optional package.");
            string fixture = Path.Combine(_job.playerRoot, "../cipher.bin");
            byte[] key = new byte[32];
            try
            {
                using (var keyFile = File.OpenRead(Path.Combine(_job.playerRoot, "../fixture.key")))
                    Require(keyFile.Length == key.Length && keyFile.Read(key, 0, key.Length) == key.Length, "Missing ephemeral test input.");
                string Digest(byte[] data, int start, int size)
                {
                    using var hash = SHA256.Create();
                    return Hex(hash.ComputeHash(data, start, size));
                }
                using (var stream = new NativeP0.P0NativeReadStream(fixture, key))
                {
                    Require(stream.Length == _job.plainLength && stream.Length > 2 * 1024 * 1024, "Unexpected multi-segment clear length.");
                    foreach (var range in _job.ranges)
                    {
                        Require(stream.Seek(range.at, SeekOrigin.Begin) == range.at, "Native seek position disagrees.");
                        byte[] bytes = new byte[range.length + 10];
                        Require(stream.Read(bytes, 5, range.length) == range.length && Digest(bytes, 5, range.length) == range.sha256 &&
                            stream.Position == range.at + range.length, "Cross-segment random read differs from the authoring fixture at " + range.at);
                        _report.samples++;
                    }
                    stream.Seek(-7, SeekOrigin.End);
                    var tail = new byte[512];
                    _report.endOfFile = stream.Read(tail, 0, tail.Length) == 7 && stream.Read(tail, 0, tail.Length) == 0;
                    Require(_report.endOfFile, "Valid final segment and EOF were not distinguished.");
                }
                using (var released = new FileStream(fixture, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    _report.exclusiveOpenAfterDispose = released.Length > _job.plainLength;
                Require(_report.exclusiveOpenAfterDispose, "The native reader still owns the ciphertext file.");
            }
            finally { Array.Clear(key, 0, key.Length); }
#else
            Require(plugins.Length == (_job.finalArchive && installed ? 1 : 0), "Unexpected native artifact presence.");
            if (_job.finalArchive && installed) _report.pluginSha256 = FileHash(plugins.Single());
#endif
            _report.extensionLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == (_job.finalArchive ? ReleaseAssembly : ExtensionAssembly));
            if (!_job.finalArchive || !installed) Require(_report.extensionLoaded == installed, "Optional managed package assembly presence differs from the stage.");
            if (_job.finalArchive) Require(GetModuleHandleW(ReleasePlugin) == IntPtr.Zero, "Installed but disabled unexpectedly initialized the native library.");

            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = startup.StartAsync();
            yield return Until(() => start.IsDone);
            var system = start.TakeResult().Resources;
            using (var scope = system.CreateScope("packaged main control"))
            {
                using var request = scope.LoadAsync<TextAsset>(Key);
                yield return Until(() => request.IsDone);
                var asset = request.TakeResult();
                _report.mainLoaded = asset && asset.text == "main:" + _job.token;
                Require(_report.mainLoaded, "The unchanged published main package could not consume the ordinary local Bundle.");
                using var close = scope.CloseAsync();
                yield return Until(() => close.IsDone);
                close.TakeResult();
            }
            using var stop = startup.StopAsync();
            yield return Until(() => stop.IsDone);
            Require(stop.TakeResult(), "Packaged main owner did not stop.");
            yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
            _report.leases = system.GetDiagnostics().LeaseCount;
            _report.openBundles = AssetBundle.GetAllLoadedAssetBundles().Count();
            Require(_report.leases == 0, "The packaged main control retained leases.");
            if (_job.finalArchive)
            {
                _report.disabledNativeInactive = GetModuleHandleW(ReleasePlugin) == IntPtr.Zero;
                Require(_report.disabledNativeInactive && Addressables.InternalIdTransformFunc == null &&
                    !File.Exists(Path.Combine(Addressables.RuntimePath, "yl-encrypted-bundles.json")),
                    "The disabled or removed extension affected stock resource startup.");
            }
        }

        private void Finish(Exception error)
        {
            if (error != null) Debug.LogException(error);
            _report.status = error == null ? "Passed" : "Failed";
            _report.error = error?.ToString();
            _report.requestedExitCode = error == null ? 0 : 1;
            Write(_job.ready, _report);
            _finished = true;
            Application.Quit(_report.requestedExitCode);
        }

        private void OnApplicationQuit()
        {
            if (!_finished) return;
            _report.quit = true;
            Write(_job.exit, _report);
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            float deadline = Time.realtimeSinceStartup + 25;
            while (!condition())
            {
                Require(Time.realtimeSinceStartup < deadline, "The P0-C operation did not settle.");
                yield return null;
            }
        }

        public static void Require(bool pass, string reason) { if (!pass) throw new InvalidOperationException(reason); }
        public static string FileHash(string path)
        {
            using var file = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(file));
        }
        private static string Hex(byte[] data) => BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        private static void Write(string path, RunReport report)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(JsonUtility.ToJson(report, true));
        }
    }
}

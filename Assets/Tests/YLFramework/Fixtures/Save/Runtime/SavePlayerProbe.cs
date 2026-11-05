using System;
using System.IO;
using System.Linq;
using UnityEngine;
using YLFramework.Save;

namespace YLFramework.Tests.SavePackage
{
    public sealed class SavePlayerProbe : MonoBehaviour
    {
        public sealed class Payload
        {
            public int Value;
        }

        [Serializable]
        private sealed class Report
        {
            public string status;
            public string error;
            public bool isEditor;
            public string platform;
            public string unityVersion;
            public int pid;
            public int run;
            public string runtimeAssembly;
            public bool mainAbsent;
            public bool coldReopened;
            public bool backupRecovered;
        }

        private async void Start()
        {
            var report = new Report
            {
                isEditor = Application.isEditor,
                platform = Application.platform.ToString(),
                unityVersion = Application.unityVersion,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                runtimeAssembly = typeof(SaveStore).Assembly.GetName().Name,
                mainAbsent = !AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "YLFramework.Runtime")
            };
            string result = null;
            try
            {
                Require(!report.isEditor && report.platform == "WindowsPlayer" && report.runtimeAssembly == "YLFramework.Save" && report.mainAbsent, "Standalone Save must not install or load the main package.");
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-ylSaveResult");
                Require(index > 0 && index < args.Length - 1, "Missing owned result argument.");
                string candidate = Path.GetFullPath(args[index + 1]);
                string directory = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                Require(string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetFileName(candidate) == "result-1.json" || Path.GetFileName(candidate) == "result-2.json") && !File.Exists(candidate), "Result must not escape or overwrite the owned Player directory.");
                result = candidate;
                report.run = Path.GetFileName(result) == "result-1.json" ? 1 : 2;
                string root = Path.Combine(directory, "Store");
                if (report.run == 1)
                {
                    Require(!Directory.Exists(root), "First run must not adopt another store.");
                    using (var store = new SaveStore(root))
                    {
                        store.CreateSaveItem(7);
                        store.RenameSaveItem(7, "player-slot");
                        await store.SaveObjectAsync(new Payload { Value = 17 }, "state", 7);
                        await store.SaveObjectAsync(new Payload { Value = 23 }, "state", 7);
                    }
                    using (var cold = new SaveStore(root))
                    {
                        Require(cold.LoadObject<Payload>("state", 7).Value == 23 && cold.GetSaveItem(7).DisplayName == "player-slot", "Cold reopen lost the actual DTO or slot metadata.");
                    }
                    report.coldReopened = true;
                }
                else
                {
                    using (var cold = new SaveStore(root))
                    {
                        Require(cold.LoadObject<Payload>("state", 7).Value == 23, "The second process did not reopen the first process files.");
                    }
                    report.coldReopened = true;
                    string primary = Path.Combine(root, "SaveData/7/state.json");
                    File.WriteAllText(primary, "{broken");
                    using (var recovering = new SaveStore(root))
                    {
                        var read = recovering.ReadObject<Payload>("state", 7);
                        Require(read.Status == SaveReadStatus.RecoveredFromBackup && read.Value.Value == 17, "Damaged primary did not use the actual previous backup.");
                        Require(File.ReadAllText(primary) == "{broken", "Read must not silently repair the primary.");
                        string preserved = recovering.RestoreObjectBackup("state", 7);
                        Require(File.Exists(preserved) && File.ReadAllText(preserved) == "{broken", "Explicit recovery did not preserve corrupt evidence.");
                    }
                    using (var restored = new SaveStore(root))
                    {
                        Require(restored.LoadObject<Payload>("state", 7).Value == 17, "Explicit backup recovery did not survive cold reopen.");
                    }
                    report.backupRecovered = true;
                }
                report.status = "Passed";
            }
            catch (Exception error) { report.status = "Failed"; report.error = error.ToString(); }
            finally
            {
                try
                {
                    if (result != null && !File.Exists(result))
                    {
                        File.WriteAllText(result, JsonUtility.ToJson(report, true));
                    }
                }
                finally { Application.Quit(report.status == "Passed" ? 0 : 1); }
            }
        }

        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

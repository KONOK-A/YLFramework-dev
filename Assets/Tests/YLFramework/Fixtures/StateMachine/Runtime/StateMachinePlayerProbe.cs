using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace YLFramework.StateMachine.Acceptance
{
    public sealed class StateMachinePlayerProbe : MonoBehaviour
    {
        [Serializable]
        private sealed class PlayerOwner
        {
            public int schemaVersion;
            public string token;
        }

        [Serializable]
        public sealed class StateCounts
        {
            public string node;
            public int creates;
            public int enters;
            public int updates;
            public int exits;
        }

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string status;
            public string error;
            public bool isEditor;
            public string platform;
            public string unityVersion;
            public string backend;
            public int pid;
            public int run;
            public string token;
            public string runtimeAssembly;
            public string configurationAssembly;
            public string stateAssembly;
            public string[] frameworkAssemblies;
            public List<StateCounts> states = new List<StateCounts>();
            public List<string> events = new List<string>();
            public int disableExits;
            public int destroyExits;
            public bool disabledUpdatesStopped;
            public bool cloneReused;
            public bool cloneReleased;
            public bool sourceUnchanged;
        }

        public RuntimeFsmController configuration;
        public string runToken;

        private Report _report;
        private FsmController _controller;
        private GameObject _host;
        private string _runtimeError;

        private IEnumerator Start()
        {
            _report = new Report
            {
                isEditor = Application.isEditor,
                platform = Application.platform.ToString(),
                unityVersion = Application.unityVersion,
#if ENABLE_MONO
                backend = "Mono",
#else
                backend = "NotMono",
#endif
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                token = runToken,
                runtimeAssembly = typeof(FsmController).Assembly.GetName().Name,
                configurationAssembly = typeof(RuntimeFsmController).Assembly.GetName().Name,
                stateAssembly = typeof(StateMachinePlayerState).Assembly.GetName().Name
            };
            string result = null;
            Application.logMessageReceived += CaptureError;
            var exercise = Exercise();
            try
            {
                try
                {
                    var args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "-ylFsmResult");
                    Require(index > 0 && index + 1 < args.Length, "Missing owned FSM result argument.");
                    string candidate = Path.GetFullPath(args[index + 1]);
                    string directory = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                    Require(string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase) &&
                        (Path.GetFileName(candidate) == "result-1.json" || Path.GetFileName(candidate) == "result-2.json") &&
                        !File.Exists(candidate), "Result must be new and inside this Player's owned directory.");
                    for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
                    {
                        Require((parent.Attributes & FileAttributes.ReparsePoint) == 0, "Linked Player directories are not owned output.");
                    }
                    result = candidate;
                    _report.run = Path.GetFileName(result) == "result-1.json" ? 1 : 2;
                    Require(Guid.TryParseExact(runToken, "N", out _), "Missing serialized run identity.");
                    string ownerPath = Path.Combine(directory, ".fsm-owner.json");
                    Require((File.GetAttributes(ownerPath) & FileAttributes.ReparsePoint) == 0, "Linked Player owner markers are not accepted.");
                    var owner = JsonUtility.FromJson<PlayerOwner>(File.ReadAllText(ownerPath));
                    Require(owner != null && owner.schemaVersion == 1 && owner.token == runToken, "The Player owner marker does not match this build's serialized token.");
                    Require(!_report.isEditor && _report.platform == "WindowsPlayer" && _report.backend == "Mono",
                        "Only an actual Windows Mono Player can satisfy this acceptance.");
                }
                catch (Exception error)
                {
                    _report.error = error.ToString();
                }

                while (_report.error == null)
                {
                    bool more;
                    try
                    {
                        Require(_runtimeError == null, _runtimeError);
                        more = exercise.MoveNext();
                    }
                    catch (Exception error)
                    {
                        _report.error = error.ToString();
                        break;
                    }
                    if (!more)
                    {
                        _report.status = "Passed";
                        break;
                    }
                    yield return exercise.Current;
                }
            }
            finally
            {
                Application.logMessageReceived -= CaptureError;
                (exercise as IDisposable)?.Dispose();
                if (_host)
                {
                    Destroy(_host);
                }
                _report.status = _report.status == "Passed" ? "Passed" : "Failed";
                _report.frameworkAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name)
                    .Where(name => name.StartsWith("YLFramework", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                bool persisted = false;
                try
                {
                    if (result != null)
                    {
                        using var stream = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                        writer.Write(JsonUtility.ToJson(_report, true));
                        writer.Flush();
                        persisted = true;
                    }
                }
                finally
                {
                    Application.Quit(_report.status == "Passed" && persisted ? 0 : 1);
                }
            }
        }

        private IEnumerator Exercise()
        {
            Require(_report.runtimeAssembly == "YLFramework.StateMachine" && _report.configurationAssembly == "YLFramework.StateMachine",
                "Configured FSM types must come from the new Runtime assembly.");
            Require(configuration && configuration.name == "StateMachinePackageAcceptance", "The built scene lost its serialized FSM asset.");
            Require(configuration.ValidateConfiguration(out string error), error);
            string sourceBefore = JsonUtility.ToJson(configuration);
            _host = new GameObject("OwnedFsmLifecycleHost");
            _host.SetActive(false);
            _controller = _host.AddComponent<FsmController>();
            _controller.UserData = this;
            _controller.AddRuntimeFsmController(configuration, "Acceptance");
            _host.SetActive(true);
            float deadline = Time.realtimeSinceStartup + 20;
            while (!_controller.IsInitialized || Counts("Idle").updates == 0)
            {
                Require(Time.realtimeSinceStartup < deadline, "The real FsmController Start/Update did not enter Idle.");
                yield return null;
            }
            Require(_controller.GetCurrentStateInfo("Acceptance")?.Data.name == "Idle", "The configured default state was not entered.");
            var clone = _controller.RuntimeFsmController.Single();
            Require(clone && clone != configuration, "The host must own a runtime clone, not mutate the shared source asset.");
            _controller.SetBool("advance", true);
            while (Counts("Nested").updates == 0 || Counts("Child").updates == 0)
            {
                Require(Time.realtimeSinceStartup < deadline, "The actual parameter transition did not update the child state.");
                yield return null;
            }
            Require(_controller.GetCurrentStateInfo("Acceptance")?.Data.name == "Nested" &&
                _controller.GetCurrentStateInfo("Acceptance", "Nested")?.Data.name == "Child", "The serialized child connection was not used.");
            Require(Counts("Idle").exits == 1, "The parameter transition must exit Idle exactly once.");
            int exits = _report.states.Sum(state => state.exits);
            _controller.enabled = false;
            _report.disableExits = _report.states.Sum(state => state.exits) - exits;
            Require(_report.disableExits == 2 && !_controller.GetBool("advance") &&
                _controller.GetCurrentStateInfo("Acceptance") == null && _controller.GetCurrentStateInfo("Acceptance", "Nested") == null,
                "OnDisable must exit the container and child and reset the instance.");
            int updates = _report.states.Sum(state => state.updates);
            yield return null;
            yield return null;
            _report.disabledUpdatesStopped = _report.states.Sum(state => state.updates) == updates;
            Require(_report.disabledUpdatesStopped, "Disabled FSM scripts still received updates.");
            int idleUpdates = Counts("Idle").updates;
            _controller.enabled = true;
            while (Counts("Idle").enters != 2 || Counts("Idle").updates <= idleUpdates)
            {
                Require(Time.realtimeSinceStartup < deadline, "Re-enabling the host did not restart its default state.");
                yield return null;
            }
            _report.cloneReused = _controller.RuntimeFsmController.Single() == clone;
            Require(_report.cloneReused && Counts("Idle").creates == 1, "Disable/re-enable must reuse the clone and the once-created user state.");
            exits = _report.states.Sum(state => state.exits);
            Destroy(_host);
            yield return null;
            yield return null;
            _report.destroyExits = _report.states.Sum(state => state.exits) - exits;
            _report.cloneReleased = !clone && !_host && !_controller;
            _report.sourceUnchanged = configuration && JsonUtility.ToJson(configuration) == sourceBefore;
            Require(_report.destroyExits == 1 && _report.cloneReleased, "Destroy must exit the resumed state once and release the owned clone.");
            Require(_report.sourceUnchanged, "Runtime lifecycle mutated or destroyed the serialized source asset.");
            Require(_report.states.Count == 3 && Counts("Idle").creates == 1 && Counts("Idle").enters == 2 && Counts("Idle").exits == 2 &&
                Counts("Nested").creates == 1 && Counts("Nested").enters == 1 && Counts("Nested").exits == 1 &&
                Counts("Child").creates == 1 && Counts("Child").enters == 1 && Counts("Child").exits == 1,
                "Unexpected user-state lifecycle counts.");
            Require(_runtimeError == null, _runtimeError);
        }

        public void Record(FsmState state, string phase)
        {
            Require(state.Controller == _controller && ReferenceEquals(state.UserData, this) && state.CurrentStateInfo != null,
                "The runtime did not bind the actual user state to its controller, node and UserData.");
            var counts = Counts(state.CurrentStateInfo.Data.name);
            if (phase == "create")
            {
                counts.creates++;
            }
            else if (phase == "enter")
            {
                counts.enters++;
            }
            else if (phase == "update")
            {
                counts.updates++;
            }
            else if (phase == "exit")
            {
                counts.exits++;
            }
            if (phase != "update")
            {
                _report.events.Add(phase + ":" + counts.node);
            }
        }

        private StateCounts Counts(string node)
        {
            var counts = _report.states.Find(value => value.node == node);
            if (counts == null)
            {
                counts = new StateCounts { node = node };
                _report.states.Add(counts);
            }
            return counts;
        }

        private void CaptureError(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _runtimeError = message + "\n" + stack;
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

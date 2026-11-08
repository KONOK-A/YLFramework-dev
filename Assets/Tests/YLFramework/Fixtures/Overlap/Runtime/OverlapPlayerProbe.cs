using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Resource;

namespace YLFramework.Overlap.Acceptance
{
    public sealed class OverlapPlayerProbe : MonoBehaviour
    {
        [Serializable]
        public sealed class Measurement
        {
            public bool threeD;
            public int density;
            public int unionChildren;
            public bool grouping;
            public bool sorting;
            public int ignoredBodies;
            public int layerMask = 1 << 11;
            public bool includeTriggers = true;
            public int initialCapacity = 8;
            public int maxCapacity;
            public int actualCapacity;
            public int warmIterations = 8;
            public int iterations = 65;
            public int finalContacts;
            public int finalRawCount;
            public int entered;
            public int stayed;
            public int exited;
            public int contactsAfterStop;
            public double milliseconds;
            public bool allocationSupported;
            public long calibrationBytes;
            public long allocatedBytes = -1;
        }

        [Serializable]
        public sealed class CurvedMeasurement
        {
            public string kind;
            public int segments;
            public int parts;
            public int cooks;
            public int steadyAdditionalCooks;
            public int copiedTargets;
            public int createdObjects;
            public float requestedError;
            public float generatedError;
            public double constructMilliseconds;
            public double buildMilliseconds;
            public double steady100Milliseconds;
            public int sceneCountBeforeConstruct;
            public int sceneCountAfterConstruct;
            public bool allocationSupported;
            public long calibrationBytes;
            public long allocatedBytes = -1;
        }

        [Serializable]
        public sealed class ConvexGateObservation
        {
            public bool defaultScene;
            public bool inactiveBeforeSync;
            public bool activeBeforeSync;
            public bool[] inactiveHits;
            public bool[] activeHits;
            public bool[] reversedHits;
            public bool separated;
            public int rawOverlap;
            public bool helperInOverlap;
            public bool targetInOverlap;
            public int helperContacts;
            public int controlContacts = -1;
            public bool posesUnchanged;
            public bool globalsUnchanged;
            public bool firstInactiveDisabledReadable;
            public int privateOverlap;
        }

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public string backend;
            public bool isEditor;
            public int pid;
            public int round;
            public string token;
            public string runtimeAssembly;
            public string probeAssembly;
            public string[] frameworkAssemblies;
            public string catalogDigest = string.Empty;
            public bool resourceWasNull;
            public int leaseCount = -1;
            public int recordCount = -1;
            public string machine;
            public string operatingSystem;
            public string processor;
            public int processorCount;
            public int pointerBits;
            public string clrVersion;
            public bool serializedPrefab;
            public bool queriesAndCasts2D;
            public bool queriesAndCasts3D;
            public bool partialNoFalseExit;
            public bool boundedUnion;
            public bool stopPool;
            public bool callbackInvalidation;
            public bool fakeNull;
            public bool currentBoxSphere;
            public bool persistentContactEvents;
            public bool serializedLifecycleBatches;
            public int serializedLifecycleCases;
            public bool closestPoint3DZ;
            public bool destroyedLastPoint;
            public bool areaInvalidation;
            public bool unscaledUpdate;
            public bool triggerPrerequisite;
            public bool inactiveConvexGate;
            public int convexGatePositives;
            public int convexGateContacts;
            public ConvexGateObservation[] convexGateObservations;
            public string selectedRoute;
            public bool isolatedConvexGate;
            public bool isolatedScenesReleased;
            public ConvexGateObservation[] isolatedGateObservations;
            public bool targetProxyGate;
            public bool targetProxyScenesReleased;
            public int targetProxyComparisons;
            public int targetProxyPositives;
            public int targetProxyNegatives;
            public float targetProxyMaxClosestDelta;
            public bool targetProxyWorldIsolation;
            public bool targetProxyNoContactSimulation;
            public bool serializedCurvedGeometry;
            public bool sectorRing2D;
            public bool sectorRingCone3D;
            public bool curvedUnsupportedAndPartial;
            public bool curvedWorkspaceRelease;
            public bool fix1ConeBroad;
            public int fix1ConeComparisons;
            public int fix1OldMidpointMisses;
            public List<CurvedMeasurement> curvedMeasurements = new List<CurvedMeasurement>();
            public int insideRay2D;
            public int insideRay3D;
            public bool queriesStartInColliders;
            public float castDistance2D;
            public float castDistance3D;
            public Vector2 castPoint2D;
            public Vector3 castPoint3D;
            public Measurement[] measurements;
        }

        [Serializable]
        public sealed class Owner
        {
            public int schemaVersion = 1;
            public string token;
            public string player;
        }

        public GameObject scenarioPrefab;
        public string runToken;
        private readonly Report _report = new Report();
        private string _runtimeError;

        private IEnumerator Start()
        {
            string result = null;
            var exercise = Exercise();
            Application.logMessageReceived += CaptureError;
            try
            {
                try
                {
                    _report.unityVersion = Application.unityVersion;
                    _report.platform = Application.platform.ToString();
                    _report.isEditor = Application.isEditor;
#if ENABLE_MONO
                    _report.backend = "Mono";
#else
                    _report.backend = "NotMono";
#endif
                    _report.pid = Process.GetCurrentProcess().Id;
                    _report.token = runToken;
                    _report.runtimeAssembly = typeof(OverlapDetector2D).Assembly.GetName().Name;
                    _report.probeAssembly = typeof(OverlapPlayerProbe).Assembly.GetName().Name;
                    _report.machine = Environment.MachineName;
                    _report.operatingSystem = Environment.OSVersion.ToString();
                    _report.processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                    _report.processorCount = Environment.ProcessorCount;
                    _report.pointerBits = IntPtr.Size * 8;
                    _report.clrVersion = Environment.Version.ToString();
                    var args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "-ylFeatureResult");
                    Require(index > 0 && index + 1 < args.Length, "Missing owned shared-fixture result argument.");
                    string directory = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                    string candidate = Path.GetFullPath(args[index + 1]);
                    Require(string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase) &&
                        (Path.GetFileName(candidate) == "result-1.json" || Path.GetFileName(candidate) == "result-2.json") &&
                        !File.Exists(candidate) && !Directory.Exists(candidate), "Result must be new and inside this Player.");
                    NoLinks(directory);
                    string ownerPath = Path.Combine(directory, ".overlap-owner.json");
                    NoLinks(ownerPath);
                    var owner = JsonUtility.FromJson<Owner>(File.ReadAllText(ownerPath));
                    Require(Guid.TryParseExact(runToken, "N", out _) && owner.schemaVersion == 1 && owner.token == runToken &&
                        string.Equals(Path.GetFullPath(owner.player), directory, StringComparison.OrdinalIgnoreCase), "Serialized token does not match Player ownership.");
                    result = candidate;
                    _report.round = Path.GetFileName(candidate) == "result-1.json" ? 1 : 2;
                    Require(!_report.isEditor && _report.platform == "WindowsPlayer" && _report.backend == "Mono", "Actual Windows x64 Mono Player required.");
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
                        Require(_runtimeError == null, _runtimeError);
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
                _report.frameworkAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name)
                    .Where(name => name.StartsWith("YLFramework", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                _report.status = _report.status == "Passed" ? "Passed" : "Failed";
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
            Require(ResourceSystem.Current == null, "Minimal Overlap must not create a Resource authority.");
            var gate = InactiveConvexGate();
            while (gate.MoveNext())
            {
                yield return gate.Current;
            }
            gate = InactiveConvexGate(true);
            while (gate.MoveNext())
            {
                yield return gate.Current;
            }
            gate = TargetProxyGate();
            while (gate.MoveNext())
            {
                yield return gate.Current;
            }
            gate = Fix1ConeBroadControls();
            while (gate.MoveNext())
            {
                yield return gate.Current;
            }
            var instance = Instantiate(scenarioPrefab);
            var two = new Rig(instance, false);
            var three = new Rig(instance, true);
            Require(two.Two.Composite.Count == 2 && three.Three.Composite.Count == 2 &&
                two.Two.Composite.GetShape(0).Center == new Vector2(2, 0) && three.Three.Composite.GetShape(0).Center == new Vector3(2, 0, 0) &&
                two.Two.Frequency == 17 && three.Three.Frequency == 17 && two.Two.SamplingMode == OverlapSamplingMode.Manual &&
                three.Three.SamplingMode == OverlapSamplingMode.Manual && (int)two.Two.Filter.ContactFilter.layerMask == 1 << 11 &&
                (int)three.Three.Filter.LayerMask == 1 << 11 && three.Three.Filter.TriggerInteraction == QueryTriggerInteraction.Ignore &&
                two.Two.Filter.IgnoredRoot && three.Three.Filter.IgnoredRoot, "Cold Player lost authored geometry, policy or local object references.");
            _report.serializedPrefab = true;
            var curved = CurvedGeometry(instance);
            while (curved.MoveNext())
                yield return curved.Current;
            var current = CurrentContactEvents(instance);
            while (current.MoveNext())
                yield return current.Current;
            var targets = new List<GameObject>();
            for (int i = 0; i < 3; i++)
                targets.Add(Target(instance.transform, new Vector3(1.6f + i * 0.4f, 0, 0)));
            Sync();
            foreach (var rig in new[] { two, three })
            {
                Require(rig.Sample() && rig.Count == 3 && rig.Capacity == 8 && rig.RawCount == 8 && rig.Entered == 3,
                    "Bounded grow/rerun or repeated-child Union failed.");
                Require(rig.Sample() && rig.Stayed == 3, "A complete repeated observation must Stay once per unique contact.");
                rig.Capacities(2, 2, false);
                Require(!rig.Sample() && rig.Status == OverlapQueryStatus.MayBeTruncated && rig.Count == 3 && rig.Exited == 0,
                    "Partial observation emitted false exits or replaced last complete contacts.");
                rig.Capacities(2, 16, true);
                Require(rig.Sample() && rig.Count == 3, "Complete sampling did not recover after bounded truncation.");
                rig.Stop();
                Require(rig.Count == 0 && rig.Exited == 3 && rig.LastReason == OverlapExitReason.Stopped, "Stop did not release owned contacts.");
                rig.Start();
                Require(rig.Sample() && rig.Entered == 6, "Explicit restart failed.");
            }
            _report.boundedUnion = _report.partialNoFalseExit = true;
            instance.SetActive(false);
            Require(two.Count == 0 && three.Count == 0 && two.LastReason == OverlapExitReason.Disabled && three.LastReason == OverlapExitReason.Disabled,
                "Pool return did not release both dimensions.");
            instance.SetActive(true);
            Require(two.Sample() && three.Sample() && two.Entered == 9 && three.Entered == 9, "Pool reuse did not establish fresh contacts.");
            _report.stopPool = true;
            QueriesAndCasts(instance.transform, two, three);
            bool globalTriggers = Physics2D.queriesHitTriggers;
            try
            {
                Physics2D.queriesHitTriggers = false;
                var policy = two.Two.Filter.ContactFilter;
                policy.useTriggers = true;
                var old = two.Two.Filter;
                two.Two.Filter = new OverlapFilter2D(policy, old.IgnoredCollider, old.IgnoredRoot);
                int exits = two.Exited;
                Require(!two.Sample() && two.Status == OverlapQueryStatus.Invalidated &&
                    two.Two.LastQuery.InvalidationReason == OverlapQueryInvalidationReason.TriggerQueriesDisabled &&
                    two.Count == 3 && two.Exited == exits && !Physics2D.queriesHitTriggers,
                    "Unsatisfiable 2D trigger request must diagnose, preserve baseline and never mutate the global switch.");
                policy.useTriggers = false;
                two.Two.Filter = new OverlapFilter2D(policy, old.IgnoredCollider, old.IgnoredRoot);
            }
            finally
            {
                Physics2D.queriesHitTriggers = globalTriggers;
            }
            _report.triggerPrerequisite = true;
            foreach (var rig in new[] { two, three })
            {
                rig.Stop();
                rig.Entered = rig.Exited = 0;
                rig.StopOnEnter = true;
                rig.Start();
                Require(!rig.Sample() && rig.Entered == 1 && rig.Exited == 3 && rig.Count == 0 && rig.LaterObservers == 0,
                    "Reentrant Stop did not invalidate remaining entered observers.");
                rig.StopOnEnter = false;
                rig.Start();
                Require(rig.Sample(), "Reentrant release corrupted the next sample.");
            }
            _report.callbackInvalidation = true;
            var unionHost = new GameObject("BoxSphereUnion") { transform = { position = new Vector3(2, 0, 0) } };
            unionHost.transform.SetParent(instance.transform, true);
            var union = unionHost.AddComponent<OverlapDetector3D>();
            union.Filter = new OverlapFilter3D(1 << 11);
            union.Composite = new OverlapComposite3D(OverlapShape3D.Box(Vector3.zero, Vector3.one * 3, Quaternion.identity),
                OverlapShape3D.Sphere(Vector3.zero, 2));
            Require(union.Sample() && union.LastComplete.Count == 4 && union.LastQuery.Status == OverlapQueryStatus.Complete,
                "Current Box/Sphere union failed to deduplicate real collider identities.");
            union.Stop();
            _report.currentBoxSphere = true;
            Destroy(targets[0]);
            yield return null;
            Sync();
            Require(two.Sample() && three.Sample() && two.Count == 2 && three.Count == 2 &&
                two.LastReason == OverlapExitReason.TargetDestroyed && three.LastReason == OverlapExitReason.TargetDestroyed,
                "Fake-null cleanup must use captured identity without dereferencing destroyed targets.");
            _report.fakeNull = true;
            two.Stop();
            three.Stop();
            instance.SetActive(false);
            Destroy(instance);
            yield return null;
            var measurements = new List<Measurement>();
            foreach (bool dimension in new[] { false, true })
            {
                foreach (int density in new[] { 64, 256 })
                {
                    measurements.Add(Measure(dimension, density, false));
                    measurements.Add(Measure(dimension, density, true));
                }
            }
            _report.measurements = measurements.ToArray();
            var resource = ResourceSystem.Current;
            var diagnostic = resource?.GetDiagnosticsSnapshot();
            _report.resourceWasNull = resource == null;
            _report.leaseCount = diagnostic?.LeaseCount ?? 0;
            _report.recordCount = diagnostic?.RecordCount ?? 0;
            Require(_report.resourceWasNull && _report.leaseCount == 0 && _report.recordCount == 0, "Overlap leaked Resource state.");
        }

        private IEnumerator CurrentContactEvents(GameObject instance)
        {
            var root = instance.transform.Find("CurrentEvents");
            var receiver = root.GetComponent<OverlapPlayerEventReceiver>();
            var two = root.Find("Area2D").GetComponent<OverlapDetector2D>();
            var three = root.Find("Detector3D").GetComponent<OverlapDetector3D>();
            Require(!root.gameObject.activeSelf && receiver.entered2D + receiver.entered3D == 0 &&
                two.EnteredEvent.GetPersistentEventCount() == 2 && two.StayedEvent.GetPersistentEventCount() == 2 &&
                two.ExitedEvent.GetPersistentEventCount() == 1 && three.EnteredEvent.GetPersistentEventCount() == 2 &&
                three.StayedEvent.GetPersistentEventCount() == 2 && three.ExitedEvent.GetPersistentEventCount() == 1,
                "Cold current prefab lost its ten persistent Contact callbacks across six channels.");
            root.gameObject.SetActive(true);
            Sync();
            Require(two.Sample() && three.Sample() && receiver.entered2D == 1 && receiver.entered3D == 1,
                "Cold persistent Enter methods were not called by the actual Detector.");
            Require(two.Sample() && three.Sample() && receiver.stayed2D == 1 && receiver.stayed3D == 1,
                "Cold persistent Stay methods were not called by the actual Detector.");
            Require(Vector3.Distance(receiver.point2D, new Vector3(0.25f, 120, 0)) < 0.00001f &&
                Vector3.Distance(receiver.point3D, new Vector3(0.25f, 140, 7.5f)) < 0.00001f,
                "Closest points must be relative to Detector position, with 2D Z zero and real 3D Z. Actual=" +
                receiver.point2D.ToString("F8") + "/" + receiver.point3D.ToString("F8") + "; targets=" +
                two.LastComplete[0].Collider.name + "/" + three.LastComplete[0].Collider.name);
            _report.closestPoint3DZ = true;
            Vector3 point2D = receiver.point2D, point3D = receiver.point3D;
            Destroy(root.Find("Target2D").gameObject);
            Destroy(root.Find("Target3D").gameObject);
            yield return null;
            Sync();
            Require(two.Sample() && three.Sample() && receiver.exited2D == 1 && receiver.exited3D == 1 &&
                receiver.reason2D == OverlapExitReason.TargetDestroyed && receiver.reason3D == OverlapExitReason.TargetDestroyed &&
                receiver.exitPoint2D == point2D && receiver.exitPoint3D == point3D,
                "Destroyed persistent Exit must carry captured identity/reason and the last submitted point.");
            _report.persistentContactEvents = _report.destroyedLastPoint = true;
            var target = new GameObject("Unscaled2D") { layer = 11 };
            target.transform.SetParent(root, false);
            target.transform.position = new Vector3(0.75f, 120);
            target.AddComponent<BoxCollider2D>();
            var target3D = new GameObject("Unscaled3D") { layer = 11 };
            target3D.transform.SetParent(root, false);
            target3D.transform.position = new Vector3(0.75f, 140, 8);
            target3D.AddComponent<BoxCollider>();
            Sync();
            Require(two.Sample() && two.LastComplete.Count == 1, "Live Area Transform input lost its adjacent-pair hit.");
            var to = root.Find("To");
            to.localPosition = new Vector3(-2, 2);
            Require(two.Sample() && two.LastQuery.Status == OverlapQueryStatus.Complete && two.LastComplete.Count == 0 &&
                receiver.exited2D == 2, "A finite all-degenerate Area must commit empty and release the previous contact once.");
            Require(two.Sample() && receiver.exited2D == 2, "Empty Area must not repeat Exit.");
            two.SetAreaPositions(new[] { new Vector3(-2, -2), new Vector3(2, 2) });
            Require(two.Sample() && two.LastComplete.Count == 1, "Local Area positions did not recover before invalid-input checking.");
            two.SetAreaPositions(new[] { new Vector3(float.NaN, 1) });
            Require(!two.Sample() && two.LastQuery.Status == OverlapQueryStatus.Invalidated && two.LastComplete.Count == 1 &&
                receiver.exited2D == 2, "An unpaired nonfinite node must not commit false Exit.");
            two.SetAreaPositions(new[] { new Vector3(-2, -2), new Vector3(2, 2) });
            Require(two.Sample() && two.LastComplete.Count == 1, "Local Area positions did not recover.");
            _report.areaInvalidation = true;
            float scale = Time.timeScale;
            try
            {
                two.SamplingMode = OverlapSamplingMode.UnscaledUpdate;
                three.SamplingMode = OverlapSamplingMode.FixedUpdate;
                int stays = receiver.stayed2D;
                Time.timeScale = 0;
                yield return new WaitForSecondsRealtime(0.08f);
                Require(receiver.stayed2D > stays && receiver.entered3D == 1,
                    "Unscaled Update must run while FixedUpdate stays paused at zero timeScale.");
                three.SamplingMode = OverlapSamplingMode.UnscaledUpdate;
                yield return new WaitForSecondsRealtime(0.08f);
                Require(receiver.entered3D == 2 && receiver.stayed3D > 1, "3D unscaled Update did not resume real sampling.");
                _report.unscaledUpdate = true;
            }
            finally
            {
                Time.timeScale = scale;
                two.SamplingMode = OverlapSamplingMode.Manual;
                three.SamplingMode = OverlapSamplingMode.Manual;
            }
            two.SetAreaTransforms(new[] { root.Find("From"), null, to });
            Require(two.Sample() && two.LastComplete.Count == 0, "Missing Transform nodes must skip adjacent pairs, not bridge across them.");
            two.SetAreaPositions(Array.Empty<Vector3>());
            Require(two.Sample() && two.LastQuery.Status == OverlapQueryStatus.Complete && two.LastComplete.Count == 0,
                "An empty Area chain must submit complete empty.");
            two.SetAreaPositions(new[] { new Vector3(-2, -2), new Vector3(2, 2) });
            foreach (var command in new[] { "stop", "disable", "restart" })
            {
                foreach (bool stay in new[] { false, true })
                {
                    CheckTwo(stay, command);
                    CheckThree(stay, command);
                }
            }
            _report.serializedLifecycleBatches = _report.serializedLifecycleCases == 12;
            two.Stop();
            three.Stop();
            root.gameObject.SetActive(false);

            void CheckTwo(bool stay, string command)
            {
                two.enabled = true;
                two.Stop();
                two.StartDetection();
                if (stay)
                    Require(two.Sample() && two.LastComplete.Count == 1, "Persistent Stay must start with a real committed contact.");
                var phase = stay ? "stay" : "enter";
                receiver.BeginLifecycle(two, phase, command);
                var callback = stay ? two.StayedEvent : two.EnteredEvent;
                UnityEngine.Events.UnityAction<OverlapContact<Collider2D>> runtime = _ =>
                {
                    Require(two.LastComplete.Count == 1, "Persistent 2D lifecycle cleared the atomic batch snapshot too early.");
                    receiver.lifecycleTrace.Add(phase + ".runtime");
                };
                callback.AddListener(runtime);
                try
                {
                    Require(!two.Sample() && two.LastComplete.Count == 0 && two.IsRunning == (command == "restart"), "2D serialized lifecycle did not terminate its old generation.");
                    CheckTrace(phase, command);
                    receiver.EndLifecycle();
                    if (command == "restart")
                        Require(two.Sample() && two.LastComplete.Count == 1, "2D restart did not observe a fresh generation.");
                }
                finally
                {
                    callback.RemoveListener(runtime);
                    receiver.EndLifecycle();
                    two.enabled = true;
                    two.Stop();
                }
            }

            void CheckThree(bool stay, string command)
            {
                three.enabled = true;
                three.Stop();
                three.StartDetection();
                if (stay)
                    Require(three.Sample() && three.LastComplete.Count == 1, "Persistent 3D Stay must start with a real contact.");
                var phase = stay ? "stay" : "enter";
                receiver.BeginLifecycle(three, phase, command);
                var callback = stay ? three.StayedEvent : three.EnteredEvent;
                UnityEngine.Events.UnityAction<OverlapContact<Collider>> runtime = _ =>
                {
                    Require(three.LastComplete.Count == 1, "Persistent 3D lifecycle cleared the atomic batch snapshot too early.");
                    receiver.lifecycleTrace.Add(phase + ".runtime");
                };
                callback.AddListener(runtime);
                try
                {
                    Require(!three.Sample() && three.LastComplete.Count == 0 && three.IsRunning == (command == "restart"), "3D serialized lifecycle did not terminate its old generation.");
                    CheckTrace(phase, command);
                    receiver.EndLifecycle();
                    if (command == "restart")
                        Require(three.Sample() && three.LastComplete.Count == 1, "3D restart did not observe a fresh generation.");
                }
                finally
                {
                    callback.RemoveListener(runtime);
                    receiver.EndLifecycle();
                    three.enabled = true;
                    three.Stop();
                }
            }

            void CheckTrace(string phase, string command)
            {
                var reason = command == "disable" ? OverlapExitReason.Disabled : OverlapExitReason.Stopped;
                Require(receiver.lifecycleTrace.SequenceEqual(new[] { phase + ".A", phase + ".B", phase + ".runtime", "exit:" + reason }),
                    "Persistent lifecycle batch interleaved an old callback after Exit: " + string.Join(",", receiver.lifecycleTrace));
                _report.serializedLifecycleCases++;
            }
        }

        private void QueriesAndCasts(Transform parent, Rig two, Rig three)
        {
            var buffer2D = new Collider2D[1];
            var buffer3D = new Collider[1];
            Require(OverlapQuery2D.Query(OverlapShape2D.Circle(new Vector2(2, 0), 2), two.Two.Filter, buffer2D).Status == OverlapQueryStatus.MayBeTruncated &&
                OverlapQuery3D.Query(OverlapShape3D.Sphere(new Vector3(2, 0, 0), 2), three.Three.Filter, buffer3D).Status == OverlapQueryStatus.MayBeTruncated,
                "A full raw caller buffer must not claim complete after ignore filtering.");
            var lane = Target(parent, new Vector3(3, 10, 0));
            Sync();
            var hits2D = new RaycastHit2D[8];
            var hits3D = new RaycastHit[8];
            var filter2D = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 }, sortByDistance: true);
            var filter3D = new OverlapFilter3D(1 << 11, sortByDistance: true);
            Require(OverlapQuery2D.Cast(OverlapShape2D.Circle(new Vector2(0, 10), 0.5f), Vector2.right, 6, filter2D, hits2D).Count == 1 &&
                OverlapQuery3D.Cast(OverlapShape3D.Sphere(new Vector3(0, 10, 0), 0.5f), Vector3.right, 6, filter3D, hits3D).Count == 1,
                "Real shape casts must hit the owned lane.");
            _report.castDistance2D = hits2D[0].distance;
            _report.castDistance3D = hits3D[0].distance;
            _report.castPoint2D = hits2D[0].point;
            _report.castPoint3D = hits3D[0].point;
            Require(Mathf.Abs(hits2D[0].distance - 2.4f) < 0.01f && Mathf.Abs(hits3D[0].distance - 2.4f) < 0.01f &&
                Mathf.Abs(hits2D[0].point.x - 2.9f) < 0.01f && Mathf.Abs(hits3D[0].point.x - 2.9f) < 0.01f &&
                hits2D[0].fraction > 0 && hits2D[0].normal.x < -0.99f && hits3D[0].normal.x < -0.99f,
                "Native distances/points/normals/fraction were lost or fabricated.");
            _report.queriesStartInColliders = Physics2D.queriesStartInColliders;
            _report.insideRay2D = OverlapQuery2D.Cast(OverlapShape2D.Point(lane.transform.position), Vector2.right, 1, filter2D, hits2D).Count;
            _report.insideRay3D = OverlapQuery3D.Raycast(lane.transform.position, Vector3.right, 1, filter3D, hits3D).Count;
            Require(_report.insideRay2D == (_report.queriesStartInColliders ? 1 : 0) && _report.insideRay3D == 0,
                "2D/3D native starting-inside ray behavior must remain distinct.");
            _report.queriesAndCasts2D = _report.queriesAndCasts3D = true;
        }

        private IEnumerator InactiveConvexGate(bool isolated = false)
        {
            var observations = new List<ConvexGateObservation>();
            bool passed = true;
            bool controlsPassed = true;
            int sceneCount = SceneManager.sceneCount;
            var activeScene = SceneManager.GetActiveScene();
            var privateScene = isolated ? SceneManager.CreateScene("OwnedConvexWorkspaceGate",
                new CreateSceneParameters(LocalPhysicsMode.Physics3D)) : default;
            var secondScene = isolated ? SceneManager.CreateScene("IndependentConvexWorkspaceGate",
                new CreateSceneParameters(LocalPhysicsMode.Physics3D)) : default;
            foreach (bool defaultScene in new[] { false, true })
            {
                var scene = defaultScene ? gameObject.scene : SceneManager.CreateScene("OwnedConvexGate",
                    new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                var physics = scene.GetPhysicsScene();
                var origin = new Vector3(500, 700, 900);
                var owner = new GameObject("ConvexGate.Inactive");
                owner.SetActive(false);
                SceneManager.MoveGameObjectToScene(owner, isolated ? privateScene : scene);
                owner.transform.position = origin;
                var mesh = new Mesh
                {
                    vertices = new[]
                    {
                        new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                        new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                        new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                        new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f)
                    },
                    triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                        3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 }
                };
                var helper = owner.AddComponent<MeshCollider>();
                helper.enabled = false;
                helper.convex = true;
                Physics.BakeMesh(mesh.GetInstanceID(), true, helper.cookingOptions);
                helper.sharedMesh = mesh;
                bool firstInactiveDisabledReadable = mesh.isReadable && !helper.enabled && !owner.activeSelf;
                if (isolated)
                {
                    helper.enabled = true;
                    owner.SetActive(true);
                }
                var target = new GameObject("ConvexGate.Target");
                SceneManager.MoveGameObjectToScene(target, scene);
                target.transform.position = origin;
                var controlObject = new GameObject("ConvexGate.ActiveControl");
                SceneManager.MoveGameObjectToScene(controlObject, scene);
                controlObject.transform.position = origin + Vector3.right * 20;
                var controlMesh = Instantiate(mesh);
                var control = controlObject.AddComponent<MeshCollider>();
                control.convex = true;
                control.sharedMesh = controlMesh;
                GameObject contactTarget = null;
                bool triggers = Physics.queriesHitTriggers;
                bool backfaces = Physics.queriesHitBackfaces;
                var simulation = Physics.simulationMode;
                var gravity = Physics.gravity;
                bool layers = Physics.GetIgnoreLayerCollision(0, 2);
                var observation = new ConvexGateObservation
                {
                    defaultScene = defaultScene,
                    inactiveHits = new bool[6],
                    activeHits = new bool[6],
                    reversedHits = new bool[6],
                    firstInactiveDisabledReadable = firstInactiveDisabledReadable
                };
                observations.Add(observation);
                try
                {
                    void Check(int index, Collider collider, Vector3 helperPose, Vector3 targetPose, Quaternion rotation)
                    {
                        observation.inactiveHits[index] = Physics.ComputePenetration(helper, helperPose, Quaternion.identity,
                            collider, targetPose, rotation, out _, out _);
                        observation.activeHits[index] = Physics.ComputePenetration(control, helperPose, Quaternion.identity,
                            collider, targetPose, rotation, out _, out _);
                        observation.reversedHits[index] = Physics.ComputePenetration(collider, targetPose, rotation,
                            helper, helperPose, Quaternion.identity, out _, out _);
                    }
                    var box = target.AddComponent<BoxCollider>();
                    box.size = Vector3.one * 4;
                    observation.inactiveBeforeSync = Physics.ComputePenetration(helper, origin, Quaternion.identity,
                        box, origin, Quaternion.identity, out _, out _);
                    observation.activeBeforeSync = Physics.ComputePenetration(control, origin, Quaternion.identity,
                        box, origin, Quaternion.identity, out _, out _);
                    Physics.SyncTransforms();
                    if (isolated)
                    {
                        var privateBuffer = new Collider[16];
                        observation.privateOverlap = privateScene.GetPhysicsScene().OverlapSphere(origin, 3, privateBuffer,
                            ~0, QueryTriggerInteraction.Collide);
                        Require(observation.privateOverlap == 1 && privateBuffer[0] == helper &&
                            secondScene.GetPhysicsScene().OverlapSphere(origin, 3, privateBuffer, ~0, QueryTriggerInteraction.Collide) == 0 &&
                            privateScene.GetPhysicsScene() != secondScene.GetPhysicsScene(), "Private workspace scenes are not independent.");
                    }
                    Check(0, box, origin, origin, Quaternion.identity);
                    box.size = new Vector3(4, 0.2f, 0.2f);
                    Check(1, box, origin, origin, Quaternion.Euler(0, 30, 0));
                    box.enabled = false;
                    var sphere = target.AddComponent<SphereCollider>();
                    sphere.radius = 3;
                    Check(2, sphere, origin, origin, Quaternion.identity);
                    sphere.radius = 0.5f;
                    Check(3, sphere, origin, origin + Vector3.right * 0.8f, Quaternion.identity);
                    sphere.enabled = false;
                    var convex = target.AddComponent<MeshCollider>();
                    convex.convex = true;
                    convex.sharedMesh = controlMesh;
                    Check(4, convex, origin, origin, Quaternion.identity);
                    Check(5, convex, origin + Vector3.right * 0.75f, origin, Quaternion.identity);
                    observation.separated = !Physics.ComputePenetration(helper, origin + Vector3.right * 10,
                        Quaternion.identity, convex, origin, Quaternion.identity, out _, out _);
                    var buffer = new Collider[16];
                    Physics.SyncTransforms();
                    observation.rawOverlap = physics.OverlapSphere(origin, 3, buffer, ~0, QueryTriggerInteraction.Collide);
                    observation.helperInOverlap = Array.IndexOf(buffer, helper) >= 0;
                    observation.targetInOverlap = Array.IndexOf(buffer, convex) >= 0;
                    var observer = target.AddComponent<ConvexPlayerGateObserver>();
                    target.AddComponent<Rigidbody>().useGravity = false;
                    if (!defaultScene)
                    {
                        contactTarget = new GameObject("ConvexGate.ContactControl");
                        SceneManager.MoveGameObjectToScene(contactTarget, scene);
                        contactTarget.transform.position = controlObject.transform.position;
                        contactTarget.AddComponent<BoxCollider>().size = Vector3.one * 4;
                        var contactObserver = controlObject.AddComponent<ConvexPlayerGateObserver>();
                        controlObject.AddComponent<Rigidbody>().useGravity = false;
                        Physics.SyncTransforms();
                        physics.Simulate(0.02f);
                        convex.isTrigger = true;
                        physics.Simulate(0.02f);
                        observation.controlContacts = contactObserver.Contacts;
                    }
                    observation.helperContacts = observer.Contacts;
                    observation.posesUnchanged = owner.transform.position == origin && target.transform.position == origin &&
                        owner.transform.rotation == Quaternion.identity && target.transform.rotation == Quaternion.identity;
                    observation.globalsUnchanged = Physics.queriesHitTriggers == triggers && Physics.queriesHitBackfaces == backfaces &&
                        Physics.simulationMode == simulation && Physics.gravity == gravity && Physics.GetIgnoreLayerCollision(0, 2) == layers;
                    bool controls = observation.firstInactiveDisabledReadable &&
                        observation.activeHits.All(hit => hit) && observation.separated && observation.rawOverlap == 1 &&
                        !observation.helperInOverlap && observation.targetInOverlap && observation.helperContacts == 0 &&
                        (defaultScene || observation.controlContacts > 0) && observation.posesUnchanged && observation.globalsUnchanged &&
                        SceneManager.GetActiveScene() == activeScene;
                    controlsPassed &= controls && (isolated || observation.inactiveHits.All(hit => !hit));
                    passed &= controls && observation.inactiveHits.All(hit => hit);
                    if (!isolated)
                    {
                        _report.convexGatePositives += observation.inactiveHits.Count(hit => hit);
                        _report.convexGateContacts += observer.Contacts;
                    }
                }
                finally
                {
                    Destroy(owner);
                    Destroy(target);
                    Destroy(controlObject);
                    if (contactTarget)
                    {
                        Destroy(contactTarget);
                    }
                    Destroy(mesh);
                    Destroy(controlMesh);
                    if (isolated)
                        _report.isolatedGateObservations = observations.ToArray();
                    else
                        _report.convexGateObservations = observations.ToArray();
                }
                if (!defaultScene)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
                else
                {
                    yield return null;
                }
            }
            if (isolated)
            {
                Require(SceneManager.sceneCount == sceneCount + 2, "Workspace scene count drift.");
                yield return SceneManager.UnloadSceneAsync(privateScene);
                Require(!privateScene.IsValid() && secondScene.IsValid(), "Unloading one owner affected the other.");
                yield return SceneManager.UnloadSceneAsync(secondScene);
                _report.isolatedScenesReleased = !secondScene.IsValid() && SceneManager.sceneCount == sceneCount &&
                    SceneManager.GetActiveScene() == activeScene;
                _report.selectedRoute = "IsolatedPhysicsScene";
                _report.isolatedConvexGate = passed && _report.isolatedScenesReleased;
                bool recordedSupport = Application.unityVersion.StartsWith("2022.3.", StringComparison.Ordinal);
                Require(controlsPassed && _report.isolatedConvexGate == recordedSupport,
                    "Recorded cross-scene limitation/controls changed; this is not the selected target-proxy backend.");
            }
            else
            {
                _report.inactiveConvexGate = passed;
                Require(!passed && controlsPassed, "Known unsupported inactive route or independent controls changed.");
            }
        }

        private IEnumerator TargetProxyGate()
        {
            int before = SceneManager.sceneCount;
            var active = SceneManager.GetActiveScene();
            var workspace = new OverlapQueryWorkspace3D();
            var other = new OverlapQueryWorkspace3D();
            var ownedScene = workspace.OwnedScene;
            var otherScene = other.OwnedScene;
            Mesh Cube() => new Mesh
            {
                vertices = new[] { new Vector3(-0.04f, -0.04f, -0.04f), new Vector3(0.04f, -0.04f, -0.04f),
                    new Vector3(0.04f, 0.04f, -0.04f), new Vector3(-0.04f, 0.04f, -0.04f),
                    new Vector3(-0.04f, -0.04f, 0.04f), new Vector3(0.04f, -0.04f, 0.04f),
                    new Vector3(0.04f, 0.04f, 0.04f), new Vector3(-0.04f, 0.04f, 0.04f) },
                triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                    3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 }
            };
            var privateMesh = Cube();
            var controlMesh = Cube();
            var privateObject = new GameObject("ProxyGate.PrivateQuery");
            privateObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(privateObject, ownedScene);
            var privateQuery = privateObject.AddComponent<MeshCollider>();
            privateQuery.convex = true;
            privateObject.SetActive(true);
            Physics.BakeMesh(privateMesh.GetInstanceID(), true, privateQuery.cookingOptions);
            privateQuery.sharedMesh = privateMesh;
            var copy = typeof(OverlapQueryWorkspace3D).GetMethod("CopyTarget",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            try
            {
                Require(SceneManager.sceneCount == before + 2 && ownedScene.GetPhysicsScene() != otherScene.GetPhysicsScene(), "Proxy owner scenes not independent.");
                foreach (bool defaultScene in new[] { false, true })
                {
                    var targetScene = defaultScene ? gameObject.scene : SceneManager.CreateScene("OwnedProxyGateTarget",
                        new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                    var root = new GameObject("ProxyGate.Root");
                    SceneManager.MoveGameObjectToScene(root, targetScene);
                    root.transform.SetPositionAndRotation(new Vector3(30, 50, 70), Quaternion.Euler(17, 23, 11));
                    root.transform.localScale = new Vector3(-1.25f, 0.8f, 1.5f);
                    var middle = new GameObject("ProxyGate.Middle");
                    middle.transform.SetParent(root.transform, false);
                    middle.transform.localRotation = Quaternion.Euler(7, 11, 3);
                    var target = new GameObject("ProxyGate.Target");
                    target.transform.SetParent(root.transform, false);
                    target.transform.localPosition = new Vector3(0.3f, -0.4f, 0.2f);
                    target.transform.localRotation = Quaternion.Euler(12, 31, 19);
                    var controlObject = new GameObject("ProxyGate.SourceQueryControl");
                    SceneManager.MoveGameObjectToScene(controlObject, targetScene);
                    var control = controlObject.AddComponent<MeshCollider>();
                    control.convex = true;
                    control.sharedMesh = controlMesh;
                    for (int kind = 0; kind < 5; kind++)
                    {
                        Collider source;
                        if (kind == 0)
                        {
                            var box = target.AddComponent<BoxCollider>();
                            box.center = new Vector3(0.1f, 0.2f, -0.1f);
                            box.size = new Vector3(1.5f, 2, 1);
                            source = box;
                        }
                        else if (kind == 1)
                        {
                            var sphere = target.AddComponent<SphereCollider>();
                            sphere.center = new Vector3(-0.2f, 0.1f, 0.3f);
                            sphere.radius = 0.75f;
                            source = sphere;
                        }
                        else
                        {
                            var capsule = target.AddComponent<CapsuleCollider>();
                            capsule.center = new Vector3(0.1f, -0.2f, 0.2f);
                            capsule.radius = 0.4f;
                            capsule.height = 2.5f;
                            capsule.direction = kind - 2;
                            source = capsule;
                        }
                        for (int body = 0; body < 3; body++)
                        {
                            var rigidbody = body > 0 ? root.AddComponent<Rigidbody>() : null;
                            if (rigidbody)
                            {
                                rigidbody.isKinematic = body == 1;
                                rigidbody.useGravity = false;
                            }
                            for (int revision = 0; revision < 2; revision++)
                            {
                                target.transform.SetParent(revision == 0 ? root.transform : middle.transform, false);
                                root.transform.position += new Vector3(0.2f, -0.1f, 0.3f);
                                target.transform.localPosition += new Vector3(0.05f, 0.01f, -0.02f);
                                if (source is BoxCollider box)
                                {
                                    box.size += Vector3.one * 0.1f;
                                    box.center += Vector3.up * 0.1f;
                                }
                                if (source is SphereCollider sphere)
                                {
                                    sphere.radius += 0.05f;
                                    sphere.center += Vector3.up * 0.1f;
                                }
                                if (source is CapsuleCollider capsule)
                                {
                                    capsule.height += 0.1f;
                                    capsule.center += Vector3.up * 0.1f;
                                }
                                Physics.SyncTransforms();
                                object[] args = { source, null };
                                Require((OverlapQueryInvalidationReason)copy.Invoke(workspace, args) == OverlapQueryInvalidationReason.None, "Primitive proxy copy rejected valid TRS.");
                                var proxy = (Collider)args[1];
                                Require(proxy.gameObject.scene == ownedScene && proxy.attachedRigidbody == null &&
                                    proxy.transform.localToWorldMatrix == source.transform.localToWorldMatrix, "Proxy hierarchy differs from source.");
                                Vector3 savedPosition = source.transform.position;
                                Vector3 outside = source.bounds.center + Vector3.right * 10;
                                Vector3 surface = Physics.ClosestPoint(outside, source, source.transform.position, source.transform.rotation);
                                Vector3 normal = (outside - surface).normalized;
                                float support = 0.04f * (Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z));
                                for (int point = -4; point <= 7; point++)
                                {
                                    Vector3 pose = point <= 4 ? source.bounds.center + new Vector3(point * 0.7f, point * 0.11f, point * -0.17f) :
                                        point == 5 ? surface : point == 6 ? surface + normal * support : surface - normal * 0.08f;
                                    bool expected = Physics.ComputePenetration(control, pose, Quaternion.identity, source,
                                        source.transform.position, source.transform.rotation, out _, out _);
                                    bool actual = Physics.ComputePenetration(privateQuery, pose, Quaternion.identity, proxy,
                                        proxy.transform.position, proxy.transform.rotation, out _, out _);
                                    Require(actual == expected, "Proxy penetration differs: default/kind/body/revision/point=" +
                                        defaultScene + "/" + kind + "/" + body + "/" + revision + "/" + point);
                                    var sourceClosest = Physics.ClosestPoint(pose, source, source.transform.position, source.transform.rotation);
                                    var proxyClosest = Physics.ClosestPoint(pose, proxy, proxy.transform.position, proxy.transform.rotation);
                                    float delta = Vector3.Distance(sourceClosest, proxyClosest);
                                    _report.targetProxyMaxClosestDelta = Mathf.Max(_report.targetProxyMaxClosestDelta, delta);
                                    Require(delta <= 0.00001f, "Primitive explicit-pose closest point differs.");
                                    _report.targetProxyComparisons++;
                                    if (expected)
                                        _report.targetProxyPositives++;
                                    else
                                        _report.targetProxyNegatives++;
                                }
                                Require(source.transform.position == savedPosition && source.gameObject.scene == targetScene,
                                    "Proxy query mutated borrowed source.");
                                Physics.SyncTransforms();
                                var observed = new Collider[16];
                                Require(targetScene.GetPhysicsScene().OverlapSphere(source.bounds.center, 5, observed, ~0, QueryTriggerInteraction.Collide) == 1 &&
                                    observed[0] == source, "Proxy registered in a target world.");
                                Array.Clear(observed, 0, observed.Length);
                                Require(ownedScene.GetPhysicsScene().OverlapSphere(Vector3.zero, 200, observed, ~0, QueryTriggerInteraction.Collide) == 2 &&
                                    observed.Contains(proxy) && observed.Contains(privateQuery) &&
                                    otherScene.GetPhysicsScene().OverlapSphere(Vector3.zero, 200, observed, ~0, QueryTriggerInteraction.Collide) == 0,
                                    "Workspace helper/proxy registration or two-owner independence failed.");
                                if (!defaultScene && kind == 0 && body == 2 && revision == 0)
                                {
                                    var observer = root.AddComponent<ConvexPlayerGateObserver>();
                                    var contact = new GameObject("ProxyGate.StaticContactControl");
                                    SceneManager.MoveGameObjectToScene(contact, targetScene);
                                    contact.transform.position = Vector3.one * 500;
                                    contact.AddComponent<BoxCollider>();
                                    var mover = new GameObject("ProxyGate.DynamicContactControl");
                                    SceneManager.MoveGameObjectToScene(mover, targetScene);
                                    mover.transform.position = contact.transform.position;
                                    mover.AddComponent<BoxCollider>();
                                    mover.AddComponent<Rigidbody>().useGravity = false;
                                    var positive = mover.AddComponent<ConvexPlayerGateObserver>();
                                    Physics.SyncTransforms();
                                    targetScene.GetPhysicsScene().Simulate(0.02f);
                                    _report.targetProxyNoContactSimulation = observer.Contacts == 0 && positive.Contacts == 1 &&
                                        source.transform.position == savedPosition;
                                    Require(_report.targetProxyNoContactSimulation, "Active private proxy leaked target-world contacts or pose changes.");
                                    contact.SetActive(false);
                                    mover.SetActive(false);
                                    Destroy(contact);
                                    Destroy(mover);
                                }
                            }
                            if (rigidbody)
                            {
                                Destroy(rigidbody);
                                yield return null;
                            }
                        }
                        source.enabled = false;
                        Destroy(source);
                        yield return null;
                    }
                    Destroy(controlObject);
                    Destroy(root);
                    if (!defaultScene)
                    {
                        yield return SceneManager.UnloadSceneAsync(targetScene);
                    }
                    else
                    {
                        yield return null;
                    }
                }
                _report.targetProxyWorldIsolation = true;
                _report.targetProxyGate = _report.targetProxyComparisons == 720 && _report.targetProxyPositives > 0 &&
                    _report.targetProxyNegatives > 0 && SceneManager.GetActiveScene() == active;
                Require(_report.targetProxyGate, "Target proxy gate incomplete.");
            }
            finally
            {
                workspace.Dispose();
                other.Dispose();
                Destroy(privateObject);
                Destroy(privateMesh);
                Destroy(controlMesh);
            }
            yield return workspace.ReleaseOperation;
            yield return other.ReleaseOperation;
            _report.targetProxyScenesReleased = !ownedScene.IsValid() && !otherScene.IsValid() && SceneManager.sceneCount == before;
            Require(_report.targetProxyScenesReleased, "Target proxy scenes did not finish releasing.");
            _report.selectedRoute = "IsolatedSceneTargetProxy";
        }

        private IEnumerator Fix1ConeBroadControls()
        {
            int scenes = SceneManager.sceneCount;
            var intersects = typeof(OverlapQueryWorkspace3D).GetMethod("Intersects",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            foreach (bool useDefault in new[] { false, true })
            {
                var targetScene = useDefault ? gameObject.scene : SceneManager.CreateScene("OwnedFix1ConeTarget",
                    new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                var workspace = new OverlapQueryWorkspace3D();
                var target = new GameObject("Fix1ConeSphere") { layer = 11 };
                SceneManager.MoveGameObjectToScene(target, targetScene);
                var sphere = target.AddComponent<SphereCollider>();
                var buffer = new Collider[16];
                try
                {
                    for (int variant = 0; variant < 8; variant++)
                    {
                        Vector3 apex = variant == 1 ? new Vector3(0, 0, -16777216) : variant == 3 ? Vector3.one * 16777216 :
                            variant >= 4 ? new Vector3(1, 2, 3) : new Vector3(0, 0, 16777216);
                        Quaternion rotation = variant == 3 || variant >= 4 ? Quaternion.Euler(15, 45, 12) : Quaternion.identity;
                        float height = variant == 2 ? 1.5f : 3;
                        float distance = variant == 2 || variant == 5 ? height : variant == 6 ? height + 0.5f : variant == 7 ? -0.5f : 0;
                        target.transform.position = apex + rotation * Vector3.forward * distance;
                        sphere.radius = variant == 2 ? 0.6f : 0.1f;
                        Physics.SyncTransforms();
                        var shape = OverlapShape3D.Cone(apex, rotation, height, 2);
                        var query = OverlapQuery3D.Query(targetScene.GetPhysicsScene(), shape, new OverlapFilter3D(1 << 11), buffer, workspace);
                        object[] args = { sphere, shape, false };
                        Require((OverlapQueryInvalidationReason)intersects.Invoke(workspace, args) == OverlapQueryInvalidationReason.None,
                            "Fix1 native control could not copy its supported sphere.");
                        bool expected = variant < 6;
                        Require((bool)args[2] == expected, "Fix1 actual same-world generated-cone penetration precondition differs.");
                        Require(query.Status == OverlapQueryStatus.Complete && query.Count == (expected ? 1 : 0) &&
                            (!expected || buffer[0] == sphere), "Fix1 certified broad hid a native generated-cone hit.");
                        if (variant == 0)
                        {
                            var old = OverlapShape3D.Box(apex + Vector3.forward * 1.5f, new Vector3(5, 5, 3), Quaternion.identity);
                            Require(OverlapQuery3D.Query(targetScene.GetPhysicsScene(), old, new OverlapFilter3D(1 << 11), buffer).Count == 0 &&
                                OverlapQuery3D.Query(targetScene.GetPhysicsScene(), OverlapShape3D.Sphere(apex, 5), new OverlapFilter3D(1 << 11), buffer).Count == 1,
                                "Fix1 Player did not reproduce the old midpoint native miss/control.");
                            _report.fix1OldMidpointMisses++;
                        }
                        _report.fix1ConeComparisons++;
                    }
                }
                finally
                {
                    workspace.Dispose();
                    target.SetActive(false);
                    Destroy(target);
                }
                yield return workspace.ReleaseOperation;
                if (!useDefault)
                    yield return SceneManager.UnloadSceneAsync(targetScene);
                else
                    yield return null;
            }
            _report.fix1ConeBroad = _report.fix1ConeComparisons == 16 && _report.fix1OldMidpointMisses == 2 && SceneManager.sceneCount == scenes;
            Require(_report.fix1ConeBroad, "Fix1 Player control or async release incomplete.");
        }

        private IEnumerator CurvedGeometry(GameObject instance)
        {
            int originalScenes = SceneManager.sceneCount;
            var two = instance.transform.Find("Curved2D").GetComponent<OverlapDetector2D>();
            var three = instance.transform.Find("Curved3D").GetComponent<OverlapDetector3D>();
            Require(two.Composite.Count == 2 && two.Composite.GetShape(0).InnerRadius == 0.5f &&
                two.Composite.GetShape(1).AngleDegrees == 360 && three.Composite.Count == 3 &&
                three.Composite.GetShape(0).Height == 2 && three.Composite.GetShape(1).InnerRadius == 1 &&
                three.Composite.GetShape(2).Kind == OverlapShapeKind3D.Cone && three.Composite.GetShape(2).Height == 4 &&
                three.RequestedBoundaryError == 0.02f && three.MaxSegments == 192 && three.MaxParts == 192 &&
                three.MaxHierarchyDepth == 16 && !three.HasGeometryWorkspace, "Cold serialized curved geometry/budgets were lost.");
            _report.serializedCurvedGeometry = true;
            var created = new List<GameObject>();
            GameObject TargetAt(Transform parent, Vector3 local, bool is2D)
            {
                var target = new GameObject("OwnedCurvedTarget") { layer = 11 };
                target.transform.SetParent(parent, false);
                target.transform.localPosition = local;
                if (is2D)
                {
                    target.AddComponent<CircleCollider2D>().radius = 0.05f;
                }
                else
                {
                    target.AddComponent<SphereCollider>().radius = 0.05f;
                }
                created.Add(target);
                return target;
            }
            TargetAt(two.transform, new Vector3(-2.5f, 0, 0), true);
            TargetAt(two.transform, new Vector3(6, 0, 0), true);
            TargetAt(two.transform, new Vector3(-4, 0, 0), true);
            TargetAt(two.transform, new Vector3(4, 0, 0), true);
            var sectorTarget = TargetAt(three.transform, new Vector3(-5, 0, 1.5f), false);
            var ringTarget = TargetAt(three.transform, new Vector3(3, 0, 2), false);
            var coneTarget = TargetAt(three.transform, new Vector3(10, 0, 2), false);
            TargetAt(three.transform, new Vector3(-5, 0, 0), false);
            TargetAt(three.transform, new Vector3(3, 0, 0), false);
            var buffer2D = new Collider2D[16];
            var buffer3D = new Collider[16];
            var workspace2D = new OverlapQueryWorkspace2D();
            var workspace = new OverlapQueryWorkspace3D(0.02f, 192, 192, 16);
            var scene = three.gameObject.scene.GetPhysicsScene();
            OverlapShape3D World(OverlapShape3D local)
            {
                Vector3 center = three.transform.TransformPoint(local.Center);
                return local.Kind == OverlapShapeKind3D.Cone ? OverlapShape3D.Cone(center, three.transform.rotation * local.Orientation, local.Height, local.Radius) :
                    OverlapShape3D.Sector(center, three.transform.rotation * local.Orientation, local.Radius, local.AngleDegrees, local.Height, local.InnerRadius);
            }
            try
            {
                Sync();
                for (int i = 0; i < two.Composite.Count; i++)
                {
                    var local = two.Composite.GetShape(i);
                    var shape = OverlapShape2D.Sector((Vector2)two.transform.TransformPoint(local.Center), local.OuterRadius,
                        local.AngleDegrees, local.InnerRadius, local.RotationDegrees);
                    var result = OverlapQuery2D.Query(two.gameObject.scene.GetPhysicsScene2D(), shape, two.Filter, buffer2D, workspace2D);
                    Require(result.Status == OverlapQueryStatus.Complete && result.Count == 1, "New 2D sector/ring actual Player query or hole exclusion failed.");
                }
                Require(two.Sample() && two.LastComplete.Count == 2, "New 2D sector/ring Detector union failed.");
                _report.sectorRing2D = true;
                var identities = new[] { sectorTarget.GetComponent<Collider>(), ringTarget.GetComponent<Collider>(), coneTarget.GetComponent<Collider>() };
                for (int i = 0; i < three.Composite.Count; i++)
                {
                    var shape = World(three.Composite.GetShape(i));
                    var result = OverlapQuery3D.Query(scene, shape, three.Filter, buffer3D, workspace);
                    Require(result.Status == OverlapQueryStatus.Complete && result.Count == 1 && buffer3D[0] == identities[i], "New 3D sector/ring/cone query lost original candidate identity or hole policy.");
                    var cast = OverlapQuery3D.Cast(scene, shape, Vector3.forward, 2, three.Filter, new RaycastHit[4]);
                    Require(cast.InvalidationReason == OverlapQueryInvalidationReason.UnsupportedCast, "Curved 3D Cast was not explicitly unsupported.");
                }
                Require(three.Sample() && three.LastComplete.Count == 3, "New 3D curved Detector union failed.");
                _report.sectorRingCone3D = true;
                int exits = 0;
                three.Exited += (_, _) => exits++;
                var unknown = new GameObject("UnsupportedCurvedTarget") { layer = 11 };
                unknown.transform.SetParent(three.transform, false);
                unknown.transform.localPosition = new Vector3(3, 0, 0);
                var unknownMesh = new Mesh
                {
                    vertices = new[] { new Vector3(-0.1f, -0.1f, -0.1f), new Vector3(0.1f, -0.1f, -0.1f),
                        new Vector3(0.1f, 0.1f, -0.1f), new Vector3(-0.1f, 0.1f, -0.1f),
                        new Vector3(-0.1f, -0.1f, 0.1f), new Vector3(0.1f, -0.1f, 0.1f),
                        new Vector3(0.1f, 0.1f, 0.1f), new Vector3(-0.1f, 0.1f, 0.1f) },
                    triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                        3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 }
                };
                var unsupported = unknown.AddComponent<MeshCollider>();
                unsupported.convex = true;
                unsupported.sharedMesh = unknownMesh;
                Sync();
                var ring = World(three.Composite.GetShape(1));
                var invalid = OverlapQuery3D.Query(scene, ring, three.Filter, buffer3D, workspace);
                Require(invalid.Count == 0 && invalid.RawCount == 3 && invalid.InvalidationReason == OverlapQueryInvalidationReason.UnsupportedGeometry &&
                    buffer3D.All(collider => collider == null), "Unsupported mesh inside hole was silently skipped.");
                Require(!three.Sample() && three.LastQuery.RawCount == 5 && three.LastQuery.Count == 0 &&
                    three.LastQuery.InvalidationReason == OverlapQueryInvalidationReason.UnsupportedGeometry &&
                    three.LastComplete.Count == 3 && exits == 0, "Invalid curved child lost accumulated raw/baseline or emitted false Exit.");
                var partial = OverlapQuery3D.Query(scene, ring, new OverlapFilter3D(1 << 11, ignoredRoot: three.transform), new Collider[1], workspace);
                Require(partial.Status == OverlapQueryStatus.MayBeTruncated && partial.Count == 0 && partial.RawCount == 1,
                    "Full native curved buffer filtered to zero was falsely Complete.");
                unknown.SetActive(false);
                Destroy(unknown);
                Destroy(unknownMesh);
                _report.curvedUnsupportedAndPartial = true;
                foreach (int index in new[] { 1, 2 })
                {
                    int scenes = SceneManager.sceneCount;
                    var measurement = new CurvedMeasurement { kind = index == 1 ? "Ring3D" : "Cone3D", requestedError = 0.02f, sceneCountBeforeConstruct = scenes };
                    var watch = Stopwatch.StartNew();
                    var measured = new OverlapQueryWorkspace3D(0.02f, 192, 192, 16);
                    measurement.constructMilliseconds = watch.Elapsed.TotalMilliseconds;
                    measurement.sceneCountAfterConstruct = SceneManager.sceneCount;
                    try
                    {
                        var shape = World(three.Composite.GetShape(index));
                        watch.Restart();
                        Require(OverlapQuery3D.Query(scene, shape, three.Filter, buffer3D, measured).Count == 1, "Curved measured build failed.");
                        measurement.buildMilliseconds = watch.Elapsed.TotalMilliseconds;
                        measurement.segments = measured.SegmentCount;
                        measurement.parts = measured.PartCount;
                        measurement.cooks = measured.CookCount;
                        measurement.generatedError = measured.GeneratedBoundaryError;
                        var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
                        Func<long> allocated = method == null ? () => -1 : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
                        allocated();
                        long before = allocated();
                        var positive = new byte[65536];
                        positive[0] = 1;
                        measurement.calibrationBytes = method == null ? -1 : allocated() - before;
                        GC.KeepAlive(positive);
                        measurement.allocationSupported = measurement.calibrationBytes >= 65536;
                        before = allocated();
                        watch.Restart();
                        for (int i = 0; i < 100; i++)
                        {
                            var local = three.Composite.GetShape(index);
                            Vector3 center = shape.Center + Vector3.up * (i % 2 * 0.001f);
                            var moved = index == 1 ? OverlapShape3D.Ring(center, shape.Orientation, local.InnerRadius, local.Radius, local.Height) :
                                OverlapShape3D.Cone(center, shape.Orientation, local.Height, local.Radius);
                            var result = OverlapQuery3D.Query(scene, moved, three.Filter, buffer3D, measured);
                            Require(result.Status == OverlapQueryStatus.Complete && result.Count == 1, "Curved steady pose query failed.");
                        }
                        measurement.steady100Milliseconds = watch.Elapsed.TotalMilliseconds;
                        measurement.allocatedBytes = measurement.allocationSupported ? allocated() - before : -1;
                        measurement.steadyAdditionalCooks = measured.CookCount - measurement.cooks;
                        measurement.copiedTargets = measured.TargetCopyCount;
                        measurement.createdObjects = measured.CreatedObjectCount;
                        Require(measurement.steadyAdditionalCooks == 0, "Pose-only queries recooked geometry.");
                        _report.curvedMeasurements.Add(measurement);
                    }
                    finally { measured.Dispose(); }
                    yield return measured.ReleaseOperation;
                    Require(SceneManager.sceneCount == scenes, "Measured workspace did not finish releasing its private scene.");
                }
                two.Stop();
                three.Stop();
                int later = 0;
                Action<OverlapContact<Collider>> cancel = _ => three.Stop();
                Action<OverlapContact<Collider>> late = _ => later++;
                three.Entered += cancel;
                three.Entered += late;
                three.StartDetection();
                Require(!three.Sample() && three.LastComplete.Count == 0 && !three.HasGeometryWorkspace && later == 0,
                    "Curved callback Stop leaked ownership or continued stale notifications.");
                three.Entered -= cancel;
                three.Entered -= late;
            }
            finally
            {
                two.Stop();
                three.Stop();
                workspace2D.Dispose();
                workspace.Dispose();
                foreach (var target in created)
                {
                    target.SetActive(false);
                    Destroy(target);
                }
            }
            yield return workspace.ReleaseOperation;
            yield return null;
            _report.curvedWorkspaceRelease = SceneManager.sceneCount == originalScenes && !three.HasGeometryWorkspace;
            Require(_report.curvedWorkspaceRelease, "Curved Stop/Dispose did not finish async scene release.");
        }

        private static Measurement Measure(bool threeD, int density, bool repeated)
        {
            var result = new Measurement
            {
                threeD = threeD,
                density = density,
                unionChildren = repeated ? 4 : 1,
                grouping = repeated,
                sorting = repeated,
                ignoredBodies = repeated ? 1 : 0,
                maxCapacity = density * 2
            };
            var root = new GameObject("MeasuredNativeWorkload");
            root.transform.position = new Vector3(0, 100 + density, 0);
            var rig = new Rig(root, threeD, true);
            Transform ignored = null;
            for (int i = 0; i < density / 2; i++)
            {
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform, false);
                if (i == 0)
                    ignored = body.transform;
                if (threeD)
                    body.AddComponent<Rigidbody>().isKinematic = true;
                else
                    body.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
                for (int j = 0; j < 2; j++)
                {
                    var target = Target(body.transform, root.transform.position + new Vector3((i % 8) * 0.2f, (i / 8) * 0.1f, 0));
                    target.GetComponentInChildren<Collider>().isTrigger = i % 4 == 0;
                    target.GetComponentInChildren<Collider2D>().isTrigger = i % 4 == 0;
                }
            }
            rig.Shapes(result.unionChildren);
            rig.Capacities(8, density * 2, true);
            rig.Policy(1 << 11, repeated, repeated, repeated ? ignored : null, true);
            Sync();
            int contacts = repeated ? density / 2 - 1 : density;
            for (int i = 0; i < 8; i++)
                Require(rig.Sample(), "Native warm-up was not complete.");
            rig.Stop();
            rig.Entered = rig.Stayed = rig.Exited = 0;
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            Func<long> allocated = method == null ? () => -1 : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
            allocated();
            long before = allocated();
            var positiveControl = new byte[65536];
            positiveControl[0] = 1;
            result.calibrationBytes = method == null ? -1 : allocated() - before;
            GC.KeepAlive(positiveControl);
            result.allocationSupported = result.calibrationBytes >= 65536;
            rig.Start();
            var watch = Stopwatch.StartNew();
            before = allocated();
            for (int i = 0; i < 64; i++)
            {
                rig.Policy(i % 16 < 8 ? 1 << 11 : 0, repeated, repeated, repeated ? ignored : null, true);
                Require(rig.Sample(), "Measured native sample was partial/invalid.");
            }
            rig.Policy(1 << 11, repeated, repeated, repeated ? ignored : null, true);
            Require(rig.Sample(), "Final native sample was not complete.");
            result.finalContacts = rig.Count;
            result.finalRawCount = rig.RawCount;
            result.actualCapacity = rig.Capacity;
            rig.Stop();
            result.allocatedBytes = result.allocationSupported ? allocated() - before : -1;
            watch.Stop();
            result.milliseconds = watch.Elapsed.TotalMilliseconds;
            result.entered = rig.Entered;
            result.stayed = rig.Stayed;
            result.exited = rig.Exited;
            result.contactsAfterStop = rig.Count;
            Require(result.finalContacts == contacts && result.finalRawCount == density * result.unionChildren &&
                result.actualCapacity == density * 2 && result.entered == contacts * 5 && result.stayed == contacts * 28 &&
                result.exited == contacts * 5 && result.contactsAfterStop == 0, "Measured physical contacts/notifications/capacity drifted.");
            root.SetActive(false);
            Destroy(root);
            return result;
        }

        private sealed class Rig
        {
            internal readonly OverlapDetector2D Two;
            internal readonly OverlapDetector3D Three;
            internal int Entered;
            internal int Stayed;
            internal int Exited;
            internal int LaterObservers;
            internal bool StopOnEnter;
            internal OverlapExitReason LastReason;
            internal int Count => Two ? Two.LastComplete.Count : Three.LastComplete.Count;
            internal int RawCount => Two ? Two.LastQuery.RawCount : Three.LastQuery.RawCount;
            internal int Capacity => Two ? Two.BufferCapacity : Three.BufferCapacity;
            internal OverlapQueryStatus Status => Two ? Two.LastQuery.Status : Three.LastQuery.Status;

            internal Rig(GameObject root, bool threeD, bool create = false)
            {
                if (threeD)
                {
                    Three = create ? root.AddComponent<OverlapDetector3D>() : root.GetComponent<OverlapDetector3D>();
                    Three.Entered += _ => Enter();
                    Three.Entered += _ => LaterObservers++;
                    Three.Stayed += _ => Stayed++;
                    Three.Exited += (_, reason) => { Exited++; LastReason = reason; };
                }
                else
                {
                    Two = create ? root.AddComponent<OverlapDetector2D>() : root.GetComponent<OverlapDetector2D>();
                    Two.Entered += _ => Enter();
                    Two.Entered += _ => LaterObservers++;
                    Two.Stayed += _ => Stayed++;
                    Two.Exited += (_, reason) => { Exited++; LastReason = reason; };
                }
            }

            private void Enter()
            {
                Entered++;
                if (StopOnEnter)
                    Stop();
            }

            internal bool Sample() => Two ? Two.Sample() : Three.Sample();
            internal void Start()
            {
                LaterObservers = 0;
                if (Two)
                    Two.StartDetection();
                else
                    Three.StartDetection();
            }
            internal void Stop()
            {
                if (Two)
                    Two.Stop();
                else
                    Three.Stop();
            }
            internal void Capacities(int initial, int max, bool grow)
            {
                if (Two)
                    Two.ConfigureCapacity(initial, max, grow);
                else
                    Three.ConfigureCapacity(initial, max, grow);
            }
            internal void Shapes(int count)
            {
                if (Two)
                    Two.Composite = new OverlapComposite2D(Enumerable.Repeat(OverlapShape2D.Circle(Vector2.zero, 10), count).ToArray());
                else
                    Three.Composite = new OverlapComposite3D(Enumerable.Repeat(OverlapShape3D.Sphere(Vector3.zero, 10), count).ToArray());
            }
            internal void Policy(int mask, bool group, bool sort, Transform ignored, bool triggers)
            {
                if (Two)
                    Two.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = mask, useTriggers = triggers },
                    ignoredRoot: ignored, groupByRigidbody: group, sortByDistance: sort);
                else
                    Three.Filter = new OverlapFilter3D(mask, triggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore,
                    ignoredRoot: ignored, groupByRigidbody: group, sortByDistance: sort);
            }
        }

        private static GameObject Target(Transform parent, Vector3 world)
        {
            var target = new GameObject("OwnedNativeTarget") { layer = 11 };
            target.transform.SetParent(parent, false);
            target.transform.position = world;
            var two = new GameObject("2D") { layer = 11 };
            two.transform.SetParent(target.transform, false);
            two.AddComponent<BoxCollider2D>().size = Vector2.one * 0.2f;
            var three = new GameObject("3D") { layer = 11 };
            three.transform.SetParent(target.transform, false);
            three.AddComponent<BoxCollider>().size = Vector3.one * 0.2f;
            return target;
        }

        private static void Sync()
        {
            Physics2D.SyncTransforms();
            Physics.SyncTransforms();
        }

        private void CaptureError(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _runtimeError ??= message + "\n" + trace;
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition)
                throw new InvalidOperationException(reason);
        }

        private static void NoLinks(string path)
        {
            if (File.Exists(path))
                Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Linked Player file refused.");
            for (var parent = new DirectoryInfo(File.Exists(path) ? Path.GetDirectoryName(path) : path); parent != null; parent = parent.Parent)
            {
                if (parent.Exists)
                    Require((parent.Attributes & FileAttributes.ReparsePoint) == 0, "Linked Player directory refused.");
            }
        }
    }

    public sealed class ConvexPlayerGateObserver : MonoBehaviour
    {
        public int Contacts;
        private void OnCollisionEnter(Collision collision) => Contacts++;
        private void OnTriggerEnter(Collider other) => Contacts++;
    }
}

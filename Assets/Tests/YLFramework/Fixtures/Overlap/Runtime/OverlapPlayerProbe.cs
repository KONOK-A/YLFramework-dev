using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
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
            public bool legacyBoxSphere;
            public bool triggerPrerequisite;
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
            var legacyHost = new GameObject("Legacy3D") { transform = { position = new Vector3(2, 0, 0) } };
            legacyHost.transform.SetParent(instance.transform, true);
            var box = legacyHost.AddComponent<OverlapBox3D>();
            box.IsRunning = false;
            box.LayerMask = 1 << 11;
            box.Size = Vector3.one * 3;
            box.CheckOverlap();
            var sphere = legacyHost.AddComponent<OverlapSphere3D>();
            sphere.IsRunning = false;
            sphere.LayerMask = 1 << 11;
            sphere.Radius = 2;
            sphere.CheckOverlap();
            Vector2 oldPoint = box.results[0].point;
            Require(box.results.Count == 4 && sphere.results.Count == 4 && box.LastQueryStatus == OverlapQueryStatus.Complete &&
                !float.IsNaN(oldPoint.x), "Legacy Box/Sphere or the original XY point protocol regressed.");
            _report.legacyBoxSphere = true;
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
}

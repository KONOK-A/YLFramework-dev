using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Scripting;
using YLFramework.Save;
using YLFramework.Serialization;

namespace YLFramework.Tests.DataBatch
{
    public sealed class UnityJsonPlayerProbe : MonoBehaviour
    {
        private static readonly UnityValueJsonConverter _converter = new UnityValueJsonConverter();
        private string _errorLogs;

        [Preserve]
        public sealed class Payload
        {
            public int WriterPid;
            public int Revision;
            public Values Nested;
            public Values[] Rows;
            public NullableValues Present;
            public NullableValues Absent;
        }

        [Preserve]
        public sealed class Values
        {
            public Vector2 V2;
            public Vector3 V3;
            public Vector4 V4;
            public Quaternion Rotation;
            public Color Tint;
            public Rect Bounds;
            public Vector2Int V2Int;
            public Vector3Int V3Int;
            public RectInt GridRect;
            public Color32 Tint32;
            public Bounds Volume;
            public BoundsInt GridBounds;
        }

        [Preserve]
        public sealed class NullableValues
        {
            public Vector2? V2;
            public Vector3? V3;
            public Vector4? V4;
            public Quaternion? Rotation;
            public Color? Tint;
            public Rect? Bounds;
            public Vector2Int? V2Int;
            public Vector3Int? V3Int;
            public RectInt? GridRect;
            public Color32? Tint32;
            public Bounds? Volume;
            public BoundsInt? GridBounds;
        }

        [Serializable]
        private sealed class Report
        {
            public string status;
            public string error;
            public string errorLogs;
            public string platform;
            public string unityVersion;
            public string runtimeAssembly;
            public string saveAssembly;
            public string probeAssembly;
            public int pid;
            public int writerPid;
            public int run;
            public bool isEditor;
            public bool isIL2CPP;
            public bool developmentBuild;
            public bool mainPresent;
            public bool savePresent;
            public bool jsonRoundTrip;
            public bool nullableRoots;
            public int componentRejections;
            public bool saveRoundTrip;
            public bool coldProcessRead;
            public bool backupRecovered;
        }

        private async void Start()
        {
            string result = null;
            var report = new Report
            {
                platform = Application.platform.ToString(),
                unityVersion = Application.unityVersion,
                runtimeAssembly = typeof(UnityValueJsonConverter).Assembly.GetName().Name,
                saveAssembly = typeof(SaveStore).Assembly.GetName().Name,
                probeAssembly = typeof(UnityJsonPlayerProbe).Assembly.GetName().Name,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                isEditor = Application.isEditor,
                developmentBuild = Debug.isDebugBuild,
#if ENABLE_IL2CPP
                isIL2CPP = true
#else
                isIL2CPP = false
#endif
            };
            Application.logMessageReceived += OnLog;
            try
            {
                var arguments = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(arguments, "-ylUnityJsonResult");
                Require(index > 0 && index < arguments.Length - 1, "Missing owned result argument.");
                string candidate = Path.GetFullPath(arguments[index + 1]);
                string directory = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                Require(string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetFileName(candidate) == "result-1.json" || Path.GetFileName(candidate) == "result-2.json") &&
                    !File.Exists(candidate), "Result must not escape or overwrite the owned Player directory.");
                result = candidate;
                report.run = Path.GetFileName(result) == "result-1.json" ? 1 : 2;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToArray();
                report.mainPresent = assemblies.Contains("YLFramework.Runtime");
                report.savePresent = assemblies.Contains("YLFramework.Save");
                Require(!report.isEditor && report.platform == "WindowsPlayer" && report.isIL2CPP && report.developmentBuild,
                    "This probe requires an actual Windows Development IL2CPP Player.");
                Require(report.runtimeAssembly == "YLFramework.Runtime" && report.saveAssembly == "YLFramework.Save" &&
                    report.probeAssembly == "YLFramework.Tests.Data.Probes" && report.mainPresent && report.savePresent,
                    "The consumer requires the real main + Save packages and the existing probe assembly.");
                Require(!assemblies.Any(name => name.StartsWith("UnityEditor", StringComparison.Ordinal)), "Editor assembly entered Player.");

                var settings = Settings();
                var defaults = JsonConvert.DefaultSettings;
                var source = CreatePayload(report.pid, 23);
                var json = JsonConvert.SerializeObject(source, settings);
                var roundTrip = JsonConvert.DeserializeObject<Payload>(json, settings);
                CheckPayload(roundTrip, report.pid, 23);
                Require(!ReferenceEquals(roundTrip, source) && !ReferenceEquals(roundTrip.Nested, source.Nested), "JsonConvert returned a source reference.");
                report.jsonRoundTrip = true;
                Require(JsonConvert.DeserializeObject<Vector2?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Vector3?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Vector4?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Quaternion?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Color?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Rect?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Vector2Int?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Vector3Int?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<RectInt?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Color32?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<Bounds?>("null", settings) == null &&
                    JsonConvert.DeserializeObject<BoundsInt?>("null", settings) == null, "Nullable root null was not preserved.");
                Require(JsonConvert.DeserializeObject<Vector2Int?>("{\"x\":16777217,\"y\":-16777217}", settings).Equals(new Vector2Int(16777217, -16777217)) &&
                    JsonConvert.DeserializeObject<Vector3Int?>("{\"x\":-2147483648,\"y\":0,\"z\":2147483647}", settings).Equals(new Vector3Int(int.MinValue, 0, int.MaxValue)) &&
                    JsonConvert.DeserializeObject<RectInt?>("{\"x\":-16777217,\"y\":0,\"width\":-2147483648,\"height\":2147483647}", settings).Equals(new RectInt(-16777217, 0, int.MinValue, int.MaxValue)),
                    "Non-null nullable integer roots lost exact components or raw sizes.");
                var byteRoot = JsonConvert.DeserializeObject<Color32?>("{\"r\":0,\"g\":127,\"b\":255,\"a\":0}", settings);
                Require(byteRoot.HasValue && byteRoot.Value.r == 0 && byteRoot.Value.g == 127 && byteRoot.Value.b == 255 && byteRoot.Value.a == 0,
                    "Non-null nullable Color32 root was converted or clamped.");
                var volumeRoot = JsonConvert.DeserializeObject<Bounds?>("{\"center\":{\"x\":-3.75,\"y\":0,\"z\":5.25},\"extents\":{\"x\":2e38,\"y\":-2.5,\"z\":0}}", settings);
                Require(volumeRoot.HasValue && volumeRoot.Value.center.Equals(new Vector3(-3.75f, 0, 5.25f)) &&
                    volumeRoot.Value.extents.Equals(new Vector3(2e38f, -2.5f, 0)), "Non-null nullable Bounds root changed finite extents through size overflow.");
                var gridRoot = JsonConvert.DeserializeObject<BoundsInt?>("{\"position\":{\"x\":2147483647,\"y\":-2147483648,\"z\":0},\"size\":{\"x\":-16777217,\"y\":0,\"z\":16777217}}", settings);
                Require(gridRoot.HasValue && gridRoot.Value.position.Equals(new Vector3Int(int.MaxValue, int.MinValue, 0)) &&
                    gridRoot.Value.size.Equals(new Vector3Int(-16777217, 0, 16777217)), "Non-null nullable BoundsInt root recalculated raw position or size.");
                report.nullableRoots = true;
                report.componentRejections = CheckInvalidComponents(settings);

                string root = Path.Combine(directory, "Store");
                string primary = Path.Combine(root, "SaveData/7/unity-values.json");
                if (report.run == 1)
                {
                    Require(!Directory.Exists(root) && !File.Exists(root), "First process must not adopt another store.");
                    report.writerPid = report.pid;
                    using (var store = new SaveStore(root, settings))
                    {
                        Require(ReferenceEquals(store.SerializerSettings, settings), "SaveStore copied the explicit settings.");
                        store.CreateSaveItem(7);
                        store.CurrentSaveId = 7;
                        store.SaveObject(CreatePayload(report.pid, 17), "unity-values", 7);
                        var saving = store.SaveObjectAsync(source, "unity-values", 7);
                        source.Revision = 999;
                        source.Nested.V3 = Vector3.zero;
                        source.Present.Rotation = null;
                        source.Nested.V2Int = Vector2Int.zero;
                        source.Nested.V3Int = Vector3Int.zero;
                        source.Rows[0].GridRect = default;
                        source.Rows[0].Tint32 = default;
                        source.Nested.Volume = default;
                        source.Nested.GridBounds = default;
                        source.Present.V2Int = null;
                        source.Present.V3Int = null;
                        source.Present.GridRect = null;
                        source.Present.Tint32 = null;
                        source.Present.Volume = null;
                        source.Present.GridBounds = null;
                        await saving;
                        store.ClearCache();
                        var read = await store.ReadObjectAsync<Payload>("unity-values", 7);
                        Require(read.Status == SaveReadStatus.Success && !read.IsCached && !ReferenceEquals(read.Value, source),
                            "Worker read did not use the committed disk snapshot.");
                        CheckPayload(read.Value, report.pid, 23);
                    }
                    using (var cold = new SaveStore(root, Settings()))
                    {
                        var read = cold.ReadObject<Payload>("unity-values", 7);
                        Require(read.Status == SaveReadStatus.Success && !read.IsCached && cold.CurrentSaveId == 7,
                            "Cold owner lost the primary or slot selection.");
                        CheckPayload(read.Value, report.pid, 23);
                    }
                    Require(File.Exists(primary + ".bak"), "Two real commits did not produce a backup.");
                    report.saveRoundTrip = true;
                }
                else
                {
                    string firstResult = Path.Combine(directory, "result-1.json");
                    Require(File.Exists(firstResult) && File.Exists(primary) && File.Exists(primary + ".bak"), "First process artifacts are missing.");
                    var first = JsonUtility.FromJson<Report>(File.ReadAllText(firstResult));
                    Require(first.status == "Passed" && first.run == 1 && first.pid > 0 && first.pid != report.pid,
                        "Cold-read evidence requires a successful independent first process.");
                    report.writerPid = first.pid;
                    using (var cold = new SaveStore(root, settings))
                    {
                        var read = await cold.ReadObjectAsync<Payload>("unity-values", 7);
                        Require(read.Status == SaveReadStatus.Success && !read.IsCached && cold.CurrentSaveId == 7,
                            "The second process did not read the first process primary from disk.");
                        CheckPayload(read.Value, first.pid, 23);
                        report.coldProcessRead = true;
                        report.saveRoundTrip = true;
                    }

                    var damagedPayloads = new[]
                    {
                        "{\"Nested\":{\"V3\":{\"x\":null,\"y\":2,\"z\":3}}}",
                        "{\"Nested\":{\"V2Int\":{\"x\":1.5,\"y\":2}}}",
                        "{\"Nested\":{\"V2Int\":{\"x\":1.0,\"y\":2}}}",
                        "{\"Nested\":{\"V3Int\":{\"x\":2147483648,\"y\":2,\"z\":3}}}",
                        "{\"Nested\":{\"GridRect\":{\"x\":1,\"y\":2,\"width\":1e0,\"height\":4}}}",
                        "{\"Nested\":{\"Tint32\":{\"r\":-1,\"g\":0,\"b\":255,\"a\":0}}}",
                        "{\"Nested\":{\"Tint32\":{\"r\":0,\"g\":0,\"b\":255,\"a\":256}}}",
                        "{\"Nested\":{\"Volume\":{\"center\":{\"x\":1,\"y\":2},\"extents\":{\"x\":1,\"y\":2,\"z\":3}}}}",
                        "{\"Nested\":{\"Volume\":{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":null}}}",
                        "{\"Nested\":{\"GridBounds\":{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":-2147483649}}}}"
                    };
                    var backup = File.ReadAllBytes(primary + ".bak");
                    foreach (string damaged in damagedPayloads)
                    {
                        Reject<Payload>(damaged, settings);
                        File.WriteAllText(primary, damaged);
                        using (var recovering = new SaveStore(root, settings))
                        {
                            var read = await recovering.ReadObjectAsync<Payload>("unity-values", 7);
                            Require(read.Status == SaveReadStatus.RecoveredFromBackup && !read.IsCached,
                                "Invalid Unity component did not fall back to the actual prior backup.");
                            CheckPayload(read.Value, first.pid, 17);
                            var cached = recovering.ReadObject<Payload>("unity-values", 7);
                            Require(cached.Status == SaveReadStatus.RecoveredFromBackup && cached.IsCached && ReferenceEquals(cached.Value, read.Value),
                                "Cached recovery hid backup provenance.");
                            recovering.ClearCache();
                            var reread = recovering.ReadObject<Payload>("unity-values", 7);
                            Require(reread.Status == SaveReadStatus.RecoveredFromBackup && !reread.IsCached && !ReferenceEquals(reread.Value, read.Value),
                                "Cleared recovery cache did not read the actual prior backup.");
                            CheckPayload(reread.Value, first.pid, 17);
                            Require(File.ReadAllText(primary) == damaged && File.ReadAllBytes(primary + ".bak").SequenceEqual(backup),
                                "Recovery read modified the corrupt primary or consumed the real backup.");

                            File.WriteAllText(primary + ".bak", damaged);
                            recovering.ClearCache();
                            bool syncRejected = false;
                            try
                            {
                                recovering.ReadObject<Payload>("unity-values", 7);
                            }
                            catch (InvalidDataException)
                            {
                                syncRejected = true;
                            }
                            Require(syncRejected, "Two invalid saved copies returned synchronous cached success or a default payload.");
                            bool asyncRejected = false;
                            try
                            {
                                await recovering.ReadObjectAsync<Payload>("unity-values", 7);
                            }
                            catch (InvalidDataException)
                            {
                                asyncRejected = true;
                            }
                            Require(asyncRejected, "Two invalid saved copies returned asynchronous cached success or a default payload.");
                        }
                        Require(File.ReadAllText(primary) == damaged && File.ReadAllText(primary + ".bak") == damaged,
                            "Failed read modified an invalid saved copy.");
                        File.WriteAllBytes(primary + ".bak", backup);
                    }
                    Require(File.ReadAllBytes(primary + ".bak").SequenceEqual(backup), "The actual first-process backup was not retained.");
                    report.backupRecovered = true;
                }
                Require(ReferenceEquals(JsonConvert.DefaultSettings, defaults), "The explicit converter mutated global JSON settings.");
                Require(string.IsNullOrEmpty(_errorLogs), "Player emitted Error, Assert or Exception logs.");
                report.status = "Passed";
            }
            catch (Exception error)
            {
                report.status = "Failed";
                report.error = error.ToString();
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                report.errorLogs = _errorLogs;
                try
                {
                    if (result != null)
                    {
                        using (var stream = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        using (var writer = new StreamWriter(stream))
                        {
                            writer.Write(JsonUtility.ToJson(report, true));
                        }
                    }
                }
                finally
                {
                    Application.Quit(report.status == "Passed" ? 0 : 1);
                }
            }
        }

        private static JsonSerializerSettings Settings()
        {
            var settings = new JsonSerializerSettings();
            settings.Converters.Add(_converter);
            return settings;
        }

        private static int CheckInvalidComponents(JsonSerializerSettings settings)
        {
            var invalidVectors = new[]
            {
                "{\"x\":1,\"y\":2}",
                "{\"x\":1,\"x\":2,\"y\":2,\"z\":3}",
                "{\"x\":1,\"y\":2,\"z\":3,\"extra\":4}",
                "{\"X\":1,\"y\":2,\"z\":3}",
                "{\"x\":null,\"y\":2,\"z\":3}",
                "{\"x\":\"1\",\"y\":2,\"z\":3}",
                "{\"x\":true,\"y\":2,\"z\":3}",
                "{\"x\":NaN,\"y\":2,\"z\":3}",
                "{\"x\":Infinity,\"y\":2,\"z\":3}",
                "{\"x\":1e39,\"y\":2,\"z\":3}",
                "{\"x\":1e-50,\"y\":2,\"z\":3}"
            };
            foreach (string invalid in invalidVectors)
            {
                Reject<Vector3>(invalid, settings);
            }
            Reject<Vector2>("{\"x\":null,\"y\":2}", settings);
            Reject<Vector4>("{\"x\":1,\"y\":2,\"z\":3}", settings);
            Reject<Quaternion>("{\"x\":1,\"y\":2,\"z\":3,\"w\":1e39}", settings);
            Reject<Color>("{\"r\":1,\"g\":2,\"b\":3,\"a\":\"4\"}", settings);
            Reject<Rect>("{\"x\":1,\"y\":2,\"width\":3,\"height\":4,\"xMin\":1}", settings);
            var invalidVectors2Int = new[]
            {
                "{\"x\":1.5,\"y\":2}",
                "{\"x\":1.0,\"y\":2}",
                "{\"x\":1e0,\"y\":2}",
                "{\"x\":2147483648,\"y\":2}",
                "{\"x\":-2147483649,\"y\":2}",
                "{\"x\":1,\"x\":2,\"y\":3}",
                "{\"X\":1,\"y\":2}"
            };
            foreach (string invalid in invalidVectors2Int)
            {
                Reject<Vector2Int>(invalid, settings);
            }
            var invalidVectors3Int = new[]
            {
                "{\"x\":1,\"y\":2,\"z\":-2.5}",
                "{\"x\":1,\"y\":1.0,\"z\":3}",
                "{\"x\":1e0,\"y\":2,\"z\":3}",
                "{\"x\":1,\"y\":2,\"z\":2147483648}",
                "{\"x\":1,\"y\":2,\"z\":-2147483649}",
                "{\"x\":1,\"y\":2,\"z\":null}",
                "{\"x\":1,\"y\":2}"
            };
            foreach (string invalid in invalidVectors3Int)
            {
                Reject<Vector3Int>(invalid, settings);
            }
            var invalidRectsInt = new[]
            {
                "{\"x\":1,\"y\":2,\"width\":3.5,\"height\":4}",
                "{\"x\":1,\"y\":2,\"width\":3,\"height\":1.0}",
                "{\"x\":1e0,\"y\":2,\"width\":3,\"height\":4}",
                "{\"x\":1,\"y\":2,\"width\":3,\"height\":2147483648}",
                "{\"x\":1,\"y\":2,\"width\":-2147483649,\"height\":4}",
                "{\"x\":1,\"y\":2,\"width\":3,\"height\":4,\"xMin\":1}"
            };
            foreach (string invalid in invalidRectsInt)
            {
                Reject<RectInt>(invalid, settings);
            }
            var invalidColors32 = new[]
            {
                "{\"r\":-1,\"g\":0,\"b\":255,\"a\":0}",
                "{\"r\":0,\"g\":0,\"b\":255,\"a\":256}",
                "{\"r\":0,\"g\":0.5,\"b\":255,\"a\":0}",
                "{\"r\":0,\"g\":0,\"b\":1.0,\"a\":0}",
                "{\"r\":0,\"g\":0,\"b\":255,\"a\":1e0}"
            };
            foreach (string invalid in invalidColors32)
            {
                Reject<Color32>(invalid, settings);
            }
            var invalidBounds = new[]
            {
                "{\"center\":null,\"extents\":{\"x\":1,\"y\":2,\"z\":3}}",
                "{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":null}",
                "{\"center\":{\"x\":1,\"y\":2},\"extents\":{\"x\":1,\"y\":2,\"z\":3}}",
                "{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":{\"x\":1,\"x\":2,\"y\":2,\"z\":3}}",
                "{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":2,\"y\":4,\"z\":6}}",
                "{\"Center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":{\"x\":1,\"y\":2,\"z\":3}}",
                "{\"center\":{\"x\":1,\"y\":2,\"z\":3}}"
            };
            foreach (string invalid in invalidBounds)
            {
                Reject<Bounds>(invalid, settings);
            }
            var invalidBoundsInt = new[]
            {
                "{\"position\":null,\"size\":{\"x\":1,\"y\":2,\"z\":3}}",
                "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":null}",
                "{\"position\":{\"x\":1.5,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3}}",
                "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":1.0,\"z\":3}}",
                "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":1e0}}",
                "{\"position\":{\"x\":2147483648,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3}}",
                "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":-2147483649}}",
                "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3,\"magnitude\":4}}",
                "{\"position\":{\"x\":1,\"y\":2,\"z\":3}}",
                "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":4,\"y\":5,\"z\":6}}"
            };
            foreach (string invalid in invalidBoundsInt)
            {
                Reject<BoundsInt>(invalid, settings);
            }
            bool rejected = false;
            try
            {
                JsonConvert.SerializeObject(new Vector3(float.NaN, 2, 3), settings);
            }
            catch (JsonSerializationException)
            {
                rejected = true;
            }
            Require(rejected, "Nonfinite write did not throw JsonSerializationException.");
            return invalidVectors.Length + invalidVectors2Int.Length + invalidVectors3Int.Length + invalidRectsInt.Length +
                invalidColors32.Length + invalidBounds.Length + invalidBoundsInt.Length + 6;
        }

        private static void Reject<T>(string json, JsonSerializerSettings settings)
        {
            bool rejected = false;
            try
            {
                JsonConvert.DeserializeObject<T>(json, settings);
            }
            catch (JsonSerializationException)
            {
                rejected = true;
            }
            Require(rejected, "Invalid " + typeof(T).Name + " component did not throw JsonSerializationException: " + json);
        }

        private static Payload CreatePayload(int pid, int revision) => new Payload
        {
            WriterPid = pid,
            Revision = revision,
            Nested = CreateValues(),
            Rows = new[] { CreateValues() },
            Present = new NullableValues
            {
                V2 = new Vector2(1.25f, -2.5f),
                V3 = new Vector3(-3.75f, 4.5f, -5.25f),
                V4 = new Vector4(6, -7, 8, -9),
                Rotation = new Quaternion(-1, 2, -3, 4),
                Tint = new Color(-0.25f, 1.5f, 2.75f, -3),
                Bounds = new Rect(10.25f, -11.5f, -12.75f, -13),
                V2Int = new Vector2Int(int.MinValue, int.MaxValue),
                V3Int = new Vector3Int(16777217, -16777217, 0),
                GridRect = new RectInt(0, int.MinValue, 0, -16777217),
                Tint32 = new Color32(255, 0, 128, 255),
                Volume = new Bounds(Vector3.zero, Vector3.zero) { extents = new Vector3(0, -float.MaxValue, 1.25f) },
                GridBounds = new BoundsInt(new Vector3Int(-16777217, 16777217, 0), new Vector3Int(int.MinValue, int.MaxValue, 0))
            },
            Absent = new NullableValues { V2Int = null, V3Int = null, GridRect = null, Tint32 = null, Volume = null, GridBounds = null }
        };

        private static Values CreateValues() => new Values
        {
            V2 = new Vector2(1.25f, -2.5f),
            V3 = new Vector3(-3.75f, 4.5f, -5.25f),
            V4 = new Vector4(6, -7, 8, -9),
            Rotation = new Quaternion(-1, 2, -3, 4),
            Tint = new Color(-0.25f, 1.5f, 2.75f, -3),
            Bounds = new Rect(10.25f, -11.5f, -12.75f, -13),
            V2Int = new Vector2Int(16777217, -16777217),
            V3Int = new Vector3Int(int.MinValue, 0, int.MaxValue),
            GridRect = new RectInt(-16777217, 0, int.MinValue, int.MaxValue),
            Tint32 = new Color32(0, 127, 255, 0),
            Volume = new Bounds(new Vector3(-3.75f, 0, 5.25f), Vector3.zero) { extents = new Vector3(float.MaxValue, -2.5f, 0) },
            GridBounds = new BoundsInt(new Vector3Int(int.MaxValue, int.MinValue, 0), new Vector3Int(-16777217, 0, 16777217))
        };

        private static void CheckPayload(Payload payload, int pid, int revision)
        {
            Require(payload != null && payload.WriterPid == pid && payload.Revision == revision, "Payload identity or revision changed.");
            CheckValues(payload.Nested);
            Require(payload.Rows != null && payload.Rows.Length == 1, "Nested array was not restored.");
            CheckValues(payload.Rows[0]);
            Require(payload.Present != null && payload.Absent != null, "Nullable DTOs were not restored.");
            Require(payload.Present.V2.HasValue && payload.Present.V2.Value.Equals(new Vector2(1.25f, -2.5f)) &&
                payload.Present.V3.HasValue && payload.Present.V3.Value.Equals(new Vector3(-3.75f, 4.5f, -5.25f)) &&
                payload.Present.V4.HasValue && payload.Present.V4.Value.Equals(new Vector4(6, -7, 8, -9)) &&
                payload.Present.Rotation.HasValue && payload.Present.Rotation.Value.Equals(new Quaternion(-1, 2, -3, 4)) &&
                payload.Present.Tint.HasValue && payload.Present.Tint.Value.Equals(new Color(-0.25f, 1.5f, 2.75f, -3)) &&
                payload.Present.Bounds.HasValue && payload.Present.Bounds.Value.Equals(new Rect(10.25f, -11.5f, -12.75f, -13)),
                "Nullable component values changed, normalized or clamped.");
            Require(payload.Present.V2Int.HasValue && payload.Present.V2Int.Value.Equals(new Vector2Int(int.MinValue, int.MaxValue)) &&
                payload.Present.V3Int.HasValue && payload.Present.V3Int.Value.Equals(new Vector3Int(16777217, -16777217, 0)) &&
                payload.Present.GridRect.HasValue && payload.Present.GridRect.Value.x == 0 && payload.Present.GridRect.Value.y == int.MinValue &&
                payload.Present.GridRect.Value.width == 0 && payload.Present.GridRect.Value.height == -16777217 &&
                payload.Present.Tint32.HasValue && payload.Present.Tint32.Value.r == 255 && payload.Present.Tint32.Value.g == 0 &&
                payload.Present.Tint32.Value.b == 128 && payload.Present.Tint32.Value.a == 255,
                "Nullable integer or byte values lost exact components, normalized or clamped.");
            Require(payload.Present.Volume.HasValue && payload.Present.Volume.Value.center.Equals(Vector3.zero) &&
                payload.Present.Volume.Value.extents.Equals(new Vector3(0, -float.MaxValue, 1.25f)) &&
                payload.Present.GridBounds.HasValue && payload.Present.GridBounds.Value.position.Equals(new Vector3Int(-16777217, 16777217, 0)) &&
                payload.Present.GridBounds.Value.size.Equals(new Vector3Int(int.MinValue, int.MaxValue, 0)),
                "Nullable bounds recalculated position, extents or raw negative sizes.");
            Require(!payload.Absent.V2.HasValue && !payload.Absent.V3.HasValue && !payload.Absent.V4.HasValue &&
                !payload.Absent.Rotation.HasValue && !payload.Absent.Tint.HasValue && !payload.Absent.Bounds.HasValue &&
                !payload.Absent.V2Int.HasValue && !payload.Absent.V3Int.HasValue && !payload.Absent.GridRect.HasValue &&
                !payload.Absent.Tint32.HasValue && !payload.Absent.Volume.HasValue && !payload.Absent.GridBounds.HasValue,
                "Null nullable components became default values.");
        }

        private static void CheckValues(Values values)
        {
            Require(values != null && values.V2.Equals(new Vector2(1.25f, -2.5f)) &&
                values.V3.Equals(new Vector3(-3.75f, 4.5f, -5.25f)) && values.V4.Equals(new Vector4(6, -7, 8, -9)) &&
                values.Rotation.x == -1 && values.Rotation.y == 2 && values.Rotation.z == -3 && values.Rotation.w == 4 &&
                values.Tint.Equals(new Color(-0.25f, 1.5f, 2.75f, -3)) && values.Bounds.x == 10.25f &&
                values.Bounds.y == -11.5f && values.Bounds.width == -12.75f && values.Bounds.height == -13,
                "Unity component values changed, normalized or clamped.");
            Require(values.V2Int.Equals(new Vector2Int(16777217, -16777217)) && values.V3Int.Equals(new Vector3Int(int.MinValue, 0, int.MaxValue)) &&
                values.GridRect.x == -16777217 && values.GridRect.y == 0 && values.GridRect.width == int.MinValue && values.GridRect.height == int.MaxValue &&
                values.Tint32.r == 0 && values.Tint32.g == 127 && values.Tint32.b == 255 && values.Tint32.a == 0,
                "Unity integer or byte values lost exact components, normalized or clamped.");
            Require(values.Volume.center.Equals(new Vector3(-3.75f, 0, 5.25f)) && values.Volume.extents.Equals(new Vector3(float.MaxValue, -2.5f, 0)) &&
                values.GridBounds.position.Equals(new Vector3Int(int.MaxValue, int.MinValue, 0)) && values.GridBounds.size.Equals(new Vector3Int(-16777217, 0, 16777217)),
                "Unity bounds recalculated position, extents or raw negative sizes.");
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
            {
                _errorLogs += type + ": " + condition + Environment.NewLine + stackTrace + Environment.NewLine;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

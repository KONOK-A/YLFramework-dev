using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Save;
using YLFramework.Serialization;

namespace YLFramework.Tests
{
    [Category("YL.Module.UnityJson")]
    public class UnityJsonSaveTests
    {
        public sealed class Record
        {
            public int Revision;
            public Values Nested;
            public Values[] Rows;
            public NullableValues Present;
            public NullableValues Absent;
        }

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

        public sealed class PositionRecord
        {
            public int Revision;
            public Vector3 Position;
        }

        [Test]
        public void ExplicitSettingsReferenceRoundTripsAllTwelveNestedValuesThroughColdDiskWithoutChangingIndexProtocol()
        {
            Run(root =>
            {
                var settings = new JsonSerializerSettings();
                var source = CreateRecord(17);
                Record firstRead;
                byte[] indexBefore;
                string indexPath = Path.Combine(root, "SaveData/SaveSystemData.json");
                using (var store = new SaveStore(root, settings))
                {
                    Assert.That(store.SerializerSettings, Is.SameAs(settings));
                    settings.Converters.Add(new UnityValueJsonConverter());
                    store.CreateSaveItem(7);
                    store.CurrentSaveId = 7;
                    indexBefore = File.ReadAllBytes(indexPath);
                    store.SaveObject(source, "unity-values", 7);
                    var cached = store.ReadObject<Record>("unity-values", 7);
                    Assert.That(cached.IsCached, Is.True);
                    Assert.That(cached.Value, Is.SameAs(source));
                    AssertComponentKeys(JObject.Parse(File.ReadAllText(Path.Combine(root, "SaveData/7/unity-values.json"))));

                    source.Revision = 999;
                    source.Nested.V3 = Vector3.zero;
                    source.Rows[0].Rotation = Quaternion.identity;
                    source.Present.V2 = null;
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
                    store.ClearCache();
                    var read = store.ReadObject<Record>("unity-values", 7);
                    Assert.That(read.Status, Is.EqualTo(SaveReadStatus.Success));
                    Assert.That(read.IsCached, Is.False);
                    Assert.That(read.Value, Is.Not.SameAs(source));
                    Assert.That(read.Value.Nested, Is.Not.SameAs(source.Nested));
                    Assert.That(read.Value.Rows[0], Is.Not.SameAs(source.Rows[0]));
                    AssertRecord(read.Value, 17);
                    firstRead = read.Value;
                }

                using (var cold = new SaveStore(root, Settings()))
                {
                    var read = cold.ReadObject<Record>("unity-values", 7);
                    Assert.That(read.Status, Is.EqualTo(SaveReadStatus.Success));
                    Assert.That(read.IsCached, Is.False);
                    Assert.That(read.Value, Is.Not.SameAs(firstRead));
                    AssertRecord(read.Value, 17);
                    Assert.That(cold.GetSaveItem(7).SaveId, Is.EqualTo(7));
                    Assert.That(cold.CurrentSaveId, Is.EqualTo(7));
                }

                Assert.That(File.ReadAllBytes(indexPath), Is.EqualTo(indexBefore));
                var index = JObject.Parse(File.ReadAllText(indexPath));
                Assert.That(index.Properties().Select(property => property.Name),
                    Is.EquivalentTo(new[] { "DataVersion", "SaveItemDict", "CurrentSaveID" }));
                Assert.That(SaveStore.FormatVersion, Is.EqualTo(2));
                Assert.That((int)index["DataVersion"], Is.EqualTo(2));
                Assert.That((int)index["CurrentSaveID"], Is.EqualTo(7));
                var item = (JObject)index["SaveItemDict"]["7"];
                Assert.That(item.Properties().Select(property => property.Name),
                    Is.EquivalentTo(new[] { "SaveID", "CreateTime", "LastSaveTime" }));
                Assert.That((int)item["SaveID"], Is.EqualTo(7));
                Assert.That(typeof(SaveStore).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name),
                    Does.Not.Contain(typeof(UnityValueJsonConverter).Assembly.GetName().Name),
                    "The adapter is business-side configuration, not a new Save-to-main dependency.");
            });
        }

        [Test]
        public void AsyncSaveCapturesValuesBeforeIoAndAsyncColdReadDoesNotReturnTheMutatedCachedInstance()
        {
            Run(root =>
            {
                var source = CreateRecord(23);
                using (var store = new SaveStore(root, Settings()))
                {
                    store.CreateSaveItem(7);
                    var saving = store.SaveObjectAsync(source, "async-values", 7);
                    source.Revision = 999;
                    source.Nested.Tint = Color.clear;
                    source.Rows[0].Bounds = default;
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
                    saving.GetAwaiter().GetResult();
                    store.ClearCache();
                    var read = store.ReadObjectAsync<Record>("async-values", 7).GetAwaiter().GetResult();
                    Assert.That(read.Status, Is.EqualTo(SaveReadStatus.Success));
                    Assert.That(read.IsCached, Is.False);
                    Assert.That(read.Value, Is.Not.SameAs(source));
                    AssertRecord(read.Value, 23);
                }
                using (var cold = new SaveStore(root, Settings()))
                {
                    var read = cold.ReadObjectAsync<Record>("async-values", 7).GetAwaiter().GetResult();
                    Assert.That(read.Status, Is.EqualTo(SaveReadStatus.Success));
                    Assert.That(read.IsCached, Is.False);
                    AssertRecord(read.Value, 23);
                }
            });
        }

        [Test]
        public void OneExplicitConverterSupportsConcurrentSaveStoreWorkerReadsInDifferentSlots()
        {
            Run(root =>
            {
                using (var store = new SaveStore(root, Settings()))
                {
                    for (int id = 7; id < 11; id++)
                    {
                        store.CreateSaveItem(id);
                        store.SaveObject(CreateRecord(id), "parallel-values", id);
                    }
                    store.ClearCache();
                    var reads = Enumerable.Range(7, 4).Select(id => store.ReadObjectAsync<Record>("parallel-values", id)).ToArray();
                    var results = Task.WhenAll(reads).GetAwaiter().GetResult();
                    for (int index = 0; index < results.Length; index++)
                    {
                        Assert.That(results[index].Status, Is.EqualTo(SaveReadStatus.Success));
                        Assert.That(results[index].IsCached, Is.False);
                        AssertRecord(results[index].Value, index + 7);
                    }
                }
            });
        }

        [TestCase("{\"x\":1,\"y\":2}")]
        [TestCase("{\"x\":1,\"x\":2,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":1,\"y\":2,\"z\":3,\"extra\":4}")]
        [TestCase("{\"X\":1,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":null,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":\"1\",\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":true,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":NaN,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":Infinity,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":1e39,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":1e-50,\"y\":2,\"z\":3}")]
        [TestCase("{\"x\":-1e-50,\"y\":2,\"z\":3}")]
        public void InvalidUnityComponentUsesTheRealPreviousBackupWithoutRepairingPrimary(string component)
        {
            Run(root =>
            {
                var settings = Settings();
                string damaged = "{\"Revision\":99,\"Position\":" + component + "}";
                Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<PositionRecord>(damaged, settings));
                using (var writer = new SaveStore(root, settings))
                {
                    writer.CreateSaveItem(7);
                    writer.SaveObject(new PositionRecord { Revision = 17, Position = new Vector3(1.25f, -2.5f, 3.75f) }, "position", 7);
                    writer.SaveObject(new PositionRecord { Revision = 23, Position = new Vector3(4, 5, 6) }, "position", 7);
                }
                string path = Path.Combine(root, "SaveData/7/position.json");
                Assert.That(File.Exists(path + ".bak"), Is.True);
                var backup = File.ReadAllBytes(path + ".bak");
                Assert.That((int)JObject.Parse(File.ReadAllText(path + ".bak"))["Revision"], Is.EqualTo(17));
                File.WriteAllText(path, damaged);
                using (var cold = new SaveStore(root, settings))
                {
                    var read = cold.ReadObject<PositionRecord>("position", 7);
                    Assert.That(read.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(read.IsCached, Is.False);
                    Assert.That(read.Value, Is.Not.Null);
                    Assert.That(read.Value.Revision, Is.EqualTo(17));
                    Assert.That(read.Value.Position, Is.EqualTo(new Vector3(1.25f, -2.5f, 3.75f)));
                    var cached = cold.ReadObjectAsync<PositionRecord>("position", 7).GetAwaiter().GetResult();
                    Assert.That(cached.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(cached.IsCached, Is.True);
                    Assert.That(cached.Value, Is.SameAs(read.Value));
                    cold.ClearCache();
                    var worker = cold.ReadObjectAsync<PositionRecord>("position", 7).GetAwaiter().GetResult();
                    Assert.That(worker.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(worker.IsCached, Is.False);
                    Assert.That(worker.Value, Is.Not.SameAs(read.Value));
                    Assert.That(worker.Value.Revision, Is.EqualTo(17));
                    Assert.That(worker.Value.Position, Is.EqualTo(new Vector3(1.25f, -2.5f, 3.75f)));
                }
                Assert.That(File.ReadAllText(path), Is.EqualTo(damaged));
                Assert.That(File.ReadAllBytes(path + ".bak"), Is.EqualTo(backup));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidComponentWithoutAValidBackupFailsInsteadOfReturningADefaultRecord(bool asynchronous)
        {
            Run(root =>
            {
                using (var store = new SaveStore(root, Settings()))
                {
                    store.CreateSaveItem(7);
                    store.SaveObject(new PositionRecord { Revision = 17, Position = new Vector3(1, 2, 3) }, "position", 7);
                    string path = Path.Combine(root, "SaveData/7/position.json");
                    const string Damaged = "{\"Revision\":99,\"Position\":{\"x\":null,\"y\":2,\"z\":3}}";
                    Assert.That(File.Exists(path + ".bak"), Is.False);
                    File.WriteAllText(path, Damaged);
                    store.ClearCache();
                    if (asynchronous)
                    {
                        Assert.Throws<InvalidDataException>(() => store.ReadObjectAsync<PositionRecord>("position", 7).GetAwaiter().GetResult());
                    }
                    else
                    {
                        Assert.Throws<InvalidDataException>(() => store.ReadObject<PositionRecord>("position", 7));
                    }
                    Assert.That(File.ReadAllText(path), Is.EqualTo(Damaged));
                    Assert.That(File.Exists(path + ".bak"), Is.False);
                }
            });
        }

        [TestCase("V2Int", "{\"x\":1.5,\"y\":2}")]
        [TestCase("V2Int", "{\"x\":1.0,\"y\":2}")]
        [TestCase("V2Int", "{\"x\":1e0,\"y\":2}")]
        [TestCase("V2Int", "{\"x\":2147483648,\"y\":2}")]
        [TestCase("V2Int", "{\"x\":-2147483649,\"y\":2}")]
        [TestCase("V2Int", "{\"x\":1,\"x\":2,\"y\":3}")]
        [TestCase("V2Int", "{\"X\":1,\"y\":2}")]
        [TestCase("V3Int", "{\"x\":1,\"y\":2,\"z\":-2.5}")]
        [TestCase("V3Int", "{\"x\":1,\"y\":1.0,\"z\":3}")]
        [TestCase("V3Int", "{\"x\":1e0,\"y\":2,\"z\":3}")]
        [TestCase("V3Int", "{\"x\":1,\"y\":2,\"z\":2147483648}")]
        [TestCase("V3Int", "{\"x\":1,\"y\":2,\"z\":-2147483649}")]
        [TestCase("V3Int", "{\"x\":1,\"y\":2,\"z\":null}")]
        [TestCase("V3Int", "{\"x\":1,\"y\":2}")]
        [TestCase("GridRect", "{\"x\":1,\"y\":2,\"width\":3.5,\"height\":4}")]
        [TestCase("GridRect", "{\"x\":1,\"y\":2,\"width\":3,\"height\":1.0}")]
        [TestCase("GridRect", "{\"x\":1e0,\"y\":2,\"width\":3,\"height\":4}")]
        [TestCase("GridRect", "{\"x\":1,\"y\":2,\"width\":3,\"height\":2147483648}")]
        [TestCase("GridRect", "{\"x\":1,\"y\":2,\"width\":-2147483649,\"height\":4}")]
        [TestCase("GridRect", "{\"x\":1,\"y\":2,\"width\":3,\"height\":4,\"xMin\":1}")]
        [TestCase("Tint32", "{\"r\":-1,\"g\":0,\"b\":255,\"a\":0}")]
        [TestCase("Tint32", "{\"r\":0,\"g\":0,\"b\":255,\"a\":256}")]
        [TestCase("Tint32", "{\"r\":0,\"g\":0.5,\"b\":255,\"a\":0}")]
        [TestCase("Tint32", "{\"r\":0,\"g\":0,\"b\":1.0,\"a\":0}")]
        [TestCase("Tint32", "{\"r\":0,\"g\":0,\"b\":255,\"a\":1e0}")]
        [TestCase("Volume", "{\"center\":null,\"extents\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("Volume", "{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":null}")]
        [TestCase("Volume", "{\"center\":{\"x\":1,\"y\":2},\"extents\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("Volume", "{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":{\"x\":1,\"x\":2,\"y\":2,\"z\":3}}")]
        [TestCase("Volume", "{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":2,\"y\":4,\"z\":6}}")]
        [TestCase("Volume", "{\"Center\":{\"x\":1,\"y\":2,\"z\":3},\"extents\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("Volume", "{\"center\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("GridBounds", "{\"position\":null,\"size\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":null}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1.5,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":1.0,\"z\":3}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":1e0}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":2147483648,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":-2147483649}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3,\"magnitude\":4}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1,\"y\":2,\"z\":3}}")]
        [TestCase("GridBounds", "{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":1,\"y\":2,\"z\":3},\"size\":{\"x\":4,\"y\":5,\"z\":6}}")]
        public void InvalidExtendedComponentRecoversTheRealBackupButTwoDamagedCopiesNeverReturnCachedSuccess(string member, string component)
        {
            Run(root =>
            {
                var settings = Settings();
                string damaged = "{\"Revision\":99,\"Nested\":{\"" + member + "\":" + component + "}}";
                Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<Record>(damaged, settings));
                using (var writer = new SaveStore(root, settings))
                {
                    writer.CreateSaveItem(7);
                    writer.SaveObject(CreateRecord(17), "extended-values", 7);
                    writer.SaveObject(CreateRecord(23), "extended-values", 7);
                }
                string path = Path.Combine(root, "SaveData/7/extended-values.json");
                Assert.That(File.Exists(path + ".bak"), Is.True);
                var backup = File.ReadAllBytes(path + ".bak");
                Assert.That((int)JObject.Parse(File.ReadAllText(path))["Revision"], Is.EqualTo(23));
                Assert.That((int)JObject.Parse(File.ReadAllText(path + ".bak"))["Revision"], Is.EqualTo(17));
                File.WriteAllText(path, damaged);
                using (var cold = new SaveStore(root, settings))
                {
                    var read = cold.ReadObject<Record>("extended-values", 7);
                    Assert.That(read.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(read.IsCached, Is.False);
                    AssertRecord(read.Value, 17);
                    var cached = cold.ReadObjectAsync<Record>("extended-values", 7).GetAwaiter().GetResult();
                    Assert.That(cached.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(cached.IsCached, Is.True);
                    Assert.That(cached.Value, Is.SameAs(read.Value));
                    cold.ClearCache();
                    var worker = cold.ReadObjectAsync<Record>("extended-values", 7).GetAwaiter().GetResult();
                    Assert.That(worker.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(worker.IsCached, Is.False);
                    Assert.That(worker.Value, Is.Not.SameAs(read.Value));
                    AssertRecord(worker.Value, 17);
                    Assert.That(File.ReadAllText(path), Is.EqualTo(damaged));
                    Assert.That(File.ReadAllBytes(path + ".bak"), Is.EqualTo(backup));

                    File.WriteAllText(path + ".bak", damaged);
                    cold.ClearCache();
                    Assert.Throws<InvalidDataException>(() => cold.ReadObject<Record>("extended-values", 7));
                    Assert.Throws<InvalidDataException>(() => cold.ReadObjectAsync<Record>("extended-values", 7).GetAwaiter().GetResult());
                }
                using (var cold = new SaveStore(root, settings))
                {
                    Assert.Throws<InvalidDataException>(() => cold.ReadObject<Record>("extended-values", 7));
                    Assert.Throws<InvalidDataException>(() => cold.ReadObjectAsync<Record>("extended-values", 7).GetAwaiter().GetResult());
                }
                Assert.That(File.ReadAllText(path), Is.EqualTo(damaged));
                Assert.That(File.ReadAllText(path + ".bak"), Is.EqualTo(damaged));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonfiniteWriteRejectionDoesNotAdvancePrimaryBackupOrCachedRecord(bool asynchronous)
        {
            Run(root =>
            {
                using (var store = new SaveStore(root, Settings()))
                {
                    store.CreateSaveItem(7);
                    store.SaveObject(new PositionRecord { Revision = 17, Position = new Vector3(1, 2, 3) }, "position", 7);
                    var committed = new PositionRecord { Revision = 23, Position = new Vector3(4, 5, 6) };
                    store.SaveObject(committed, "position", 7);
                    string path = Path.Combine(root, "SaveData/7/position.json");
                    var primary = File.ReadAllBytes(path);
                    var backup = File.ReadAllBytes(path + ".bak");
                    var invalid = new PositionRecord { Revision = 99, Position = new Vector3(float.NaN, 2, 3) };
                    if (asynchronous)
                    {
                        Task pending = null;
                        Assert.Throws<JsonSerializationException>(() => pending = store.SaveObjectAsync(invalid, "position", 7));
                        Assert.That(pending, Is.Null, "SaveStore must reject during caller-side serialization, before queuing IO.");
                    }
                    else
                    {
                        Assert.Throws<JsonSerializationException>(() => store.SaveObject(invalid, "position", 7));
                    }
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(primary));
                    Assert.That(File.ReadAllBytes(path + ".bak"), Is.EqualTo(backup));
                    var cached = store.ReadObject<PositionRecord>("position", 7);
                    Assert.That(cached.IsCached, Is.True);
                    Assert.That(cached.Value, Is.SameAs(committed));
                }
            });
        }

        private static JsonSerializerSettings Settings()
        {
            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new UnityValueJsonConverter());
            return settings;
        }

        private static Record CreateRecord(int revision)
        {
            return new Record
            {
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
        }

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

        private static void AssertRecord(Record record, int revision)
        {
            Assert.That(record, Is.Not.Null);
            Assert.That(record.Revision, Is.EqualTo(revision));
            AssertValues(record.Nested);
            Assert.That(record.Rows, Has.Length.EqualTo(1));
            AssertValues(record.Rows[0]);
            Assert.That(record.Present, Is.Not.Null);
            Assert.That(record.Present.V2, Is.EqualTo(new Vector2(1.25f, -2.5f)));
            Assert.That(record.Present.V3, Is.EqualTo(new Vector3(-3.75f, 4.5f, -5.25f)));
            Assert.That(record.Present.V4, Is.EqualTo(new Vector4(6, -7, 8, -9)));
            Assert.That(record.Present.Rotation, Is.EqualTo(new Quaternion(-1, 2, -3, 4)));
            Assert.That(record.Present.Tint, Is.EqualTo(new Color(-0.25f, 1.5f, 2.75f, -3)));
            Assert.That(record.Present.Bounds, Is.EqualTo(new Rect(10.25f, -11.5f, -12.75f, -13)));
            Assert.That(record.Present.V2Int, Is.EqualTo(new Vector2Int(int.MinValue, int.MaxValue)));
            Assert.That(record.Present.V3Int, Is.EqualTo(new Vector3Int(16777217, -16777217, 0)));
            Assert.That(record.Present.GridRect.HasValue, Is.True);
            Assert.That(new[] { record.Present.GridRect.Value.x, record.Present.GridRect.Value.y,
                record.Present.GridRect.Value.width, record.Present.GridRect.Value.height }, Is.EqualTo(new[] { 0, int.MinValue, 0, -16777217 }));
            Assert.That(record.Present.Tint32.HasValue, Is.True);
            Assert.That(new[] { record.Present.Tint32.Value.r, record.Present.Tint32.Value.g,
                record.Present.Tint32.Value.b, record.Present.Tint32.Value.a }, Is.EqualTo(new byte[] { 255, 0, 128, 255 }));
            Assert.That(record.Present.Volume.HasValue, Is.True);
            Assert.That(record.Present.Volume.Value.center, Is.EqualTo(Vector3.zero));
            Assert.That(record.Present.Volume.Value.extents, Is.EqualTo(new Vector3(0, -float.MaxValue, 1.25f)));
            Assert.That(record.Present.GridBounds.HasValue, Is.True);
            Assert.That(record.Present.GridBounds.Value.position, Is.EqualTo(new Vector3Int(-16777217, 16777217, 0)));
            Assert.That(record.Present.GridBounds.Value.size, Is.EqualTo(new Vector3Int(int.MinValue, int.MaxValue, 0)));
            Assert.That(record.Absent, Is.Not.Null);
            Assert.That(record.Absent.V2, Is.Null);
            Assert.That(record.Absent.V3, Is.Null);
            Assert.That(record.Absent.V4, Is.Null);
            Assert.That(record.Absent.Rotation, Is.Null);
            Assert.That(record.Absent.Tint, Is.Null);
            Assert.That(record.Absent.Bounds, Is.Null);
            Assert.That(record.Absent.V2Int, Is.Null);
            Assert.That(record.Absent.V3Int, Is.Null);
            Assert.That(record.Absent.GridRect, Is.Null);
            Assert.That(record.Absent.Tint32, Is.Null);
            Assert.That(record.Absent.Volume, Is.Null);
            Assert.That(record.Absent.GridBounds, Is.Null);
        }

        private static void AssertValues(Values values)
        {
            Assert.That(values, Is.Not.Null);
            Assert.That(values.V2, Is.EqualTo(new Vector2(1.25f, -2.5f)));
            Assert.That(values.V3, Is.EqualTo(new Vector3(-3.75f, 4.5f, -5.25f)));
            Assert.That(values.V4, Is.EqualTo(new Vector4(6, -7, 8, -9)));
            Assert.That(new[] { values.Rotation.x, values.Rotation.y, values.Rotation.z, values.Rotation.w },
                Is.EqualTo(new[] { -1f, 2f, -3f, 4f }));
            Assert.That(values.Tint, Is.EqualTo(new Color(-0.25f, 1.5f, 2.75f, -3)));
            Assert.That(new[] { values.Bounds.x, values.Bounds.y, values.Bounds.width, values.Bounds.height },
                Is.EqualTo(new[] { 10.25f, -11.5f, -12.75f, -13f }));
            Assert.That(values.V2Int, Is.EqualTo(new Vector2Int(16777217, -16777217)));
            Assert.That(values.V3Int, Is.EqualTo(new Vector3Int(int.MinValue, 0, int.MaxValue)));
            Assert.That(new[] { values.GridRect.x, values.GridRect.y, values.GridRect.width, values.GridRect.height },
                Is.EqualTo(new[] { -16777217, 0, int.MinValue, int.MaxValue }));
            Assert.That(new[] { values.Tint32.r, values.Tint32.g, values.Tint32.b, values.Tint32.a }, Is.EqualTo(new byte[] { 0, 127, 255, 0 }));
            Assert.That(values.Volume.center, Is.EqualTo(new Vector3(-3.75f, 0, 5.25f)));
            Assert.That(values.Volume.extents, Is.EqualTo(new Vector3(float.MaxValue, -2.5f, 0)));
            Assert.That(values.GridBounds.position, Is.EqualTo(new Vector3Int(int.MaxValue, int.MinValue, 0)));
            Assert.That(values.GridBounds.size, Is.EqualTo(new Vector3Int(-16777217, 0, 16777217)));
        }

        private static void AssertComponentKeys(JObject record)
        {
            var names = new[] { "V2", "V3", "V4", "Rotation", "Tint", "Bounds", "V2Int", "V3Int", "GridRect", "Tint32", "Volume", "GridBounds" };
            var components = new[] { new[] { "x", "y" }, new[] { "x", "y", "z" }, new[] { "x", "y", "z", "w" },
                new[] { "x", "y", "z", "w" }, new[] { "r", "g", "b", "a" }, new[] { "x", "y", "width", "height" },
                new[] { "x", "y" }, new[] { "x", "y", "z" }, new[] { "x", "y", "width", "height" }, new[] { "r", "g", "b", "a" },
                new[] { "center", "extents" }, new[] { "position", "size" } };
            foreach (var values in new[] { record["Nested"], record["Rows"][0], record["Present"] })
            {
                for (int index = 0; index < names.Length; index++)
                {
                    Assert.That(((JObject)values[names[index]]).Properties().Select(property => property.Name),
                        Is.EquivalentTo(components[index]));
                }
                foreach (string name in new[] { "V2Int", "V3Int", "GridRect", "Tint32" })
                {
                    Assert.That(((JObject)values[name]).Properties().Select(property => property.Value.Type), Is.All.EqualTo(JTokenType.Integer), name);
                }
                foreach (var vector in new[] { values["Volume"]["center"], values["Volume"]["extents"],
                    values["GridBounds"]["position"], values["GridBounds"]["size"] })
                {
                    Assert.That(((JObject)vector).Properties().Select(property => property.Name), Is.EquivalentTo(new[] { "x", "y", "z" }));
                }
                foreach (string name in new[] { "position", "size" })
                {
                    Assert.That(((JObject)values["GridBounds"][name]).Properties().Select(property => property.Value.Type), Is.All.EqualTo(JTokenType.Integer), name);
                }
            }
            foreach (string name in names)
            {
                Assert.That(record["Absent"][name].Type, Is.EqualTo(JTokenType.Null), name);
            }
        }

        private static void Run(Action<string> check)
        {
            var ambient = JsonConvert.DefaultSettings;
            var defaults = SaveManager.DefaultSerializerSettings;
            var defaultConverters = defaults.Converters.ToArray();
            var format = SaveManager.DefaultSerializationFormat;
            string root = Path.Combine(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput(),
                "UnityJsonSave-" + Guid.NewGuid().ToString("N"));
            Assert.That(Directory.Exists(root) || File.Exists(root), Is.False);
            Directory.CreateDirectory(root);
            var passed = false;
            try
            {
                check(root);
                Assert.That(JsonConvert.DefaultSettings, Is.SameAs(ambient));
                Assert.That(SaveManager.DefaultSerializerSettings, Is.SameAs(defaults));
                Assert.That(defaults.Converters, Is.EqualTo(defaultConverters));
                Assert.That(SaveManager.DefaultSerializationFormat, Is.EqualTo(format));
                passed = true;
            }
            finally
            {
                if (passed)
                {
                    Directory.Delete(root, true);
                }
                else
                {
                    Console.WriteLine("Preserved failed Unity JSON Save fixture: " + root);
                }
            }
        }
    }
}

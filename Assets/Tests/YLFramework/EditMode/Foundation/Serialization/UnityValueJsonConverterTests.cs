using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Serialization;

namespace YLFramework.Tests
{
    [Category("YL.Module.UnityJson")]
    public class UnityValueJsonConverterTests
    {
        private static readonly (Type Type, Type NullableType, object Value, string Json)[] _schemas =
        {
            (typeof(Vector2), typeof(Vector2?), new Vector2(0, -1.25f), "{\"x\":0.0,\"y\":-1.25}"),
            (typeof(Vector3), typeof(Vector3?), new Vector3(-2, 0, 1.5f), "{\"x\":-2.0,\"y\":0.0,\"z\":1.5}"),
            (typeof(Vector4), typeof(Vector4?), new Vector4(0, 2.5f, -3, 0), "{\"x\":0.0,\"y\":2.5,\"z\":-3.0,\"w\":0.0}"),
            (typeof(Quaternion), typeof(Quaternion?), new Quaternion(1, -2, 3, 4), "{\"x\":1.0,\"y\":-2.0,\"z\":3.0,\"w\":4.0}"),
            (typeof(Color), typeof(Color?), new Color(1.5f, -0.25f, 0, 0), "{\"r\":1.5,\"g\":-0.25,\"b\":0.0,\"a\":0.0}"),
            (typeof(Rect), typeof(Rect?), new Rect(-1.5f, 2.25f, -3, 0), "{\"x\":-1.5,\"y\":2.25,\"width\":-3.0,\"height\":0.0}"),
            (typeof(Vector2Int), typeof(Vector2Int?), new Vector2Int(16777217, int.MinValue), "{\"x\":16777217,\"y\":-2147483648}"),
            (typeof(Vector3Int), typeof(Vector3Int?), new Vector3Int(int.MaxValue, -16777217, 0), "{\"x\":2147483647,\"y\":-16777217,\"z\":0}"),
            (typeof(RectInt), typeof(RectInt?), new RectInt(16777217, int.MinValue, -3, int.MaxValue), "{\"x\":16777217,\"y\":-2147483648,\"width\":-3,\"height\":2147483647}"),
            (typeof(Color32), typeof(Color32?), new Color32(0, 127, 255, 0), "{\"r\":0,\"g\":127,\"b\":255,\"a\":0}"),
            (typeof(Bounds), typeof(Bounds?), new Bounds(new Vector3(-2, 0, 1.5f), Vector3.zero) { extents = new Vector3(-1, 0.5f, 0) }, "{\"center\":{\"x\":-2.0,\"y\":0.0,\"z\":1.5},\"extents\":{\"x\":-1.0,\"y\":0.5,\"z\":0.0}}"),
            (typeof(BoundsInt), typeof(BoundsInt?), new BoundsInt(new Vector3Int(16777217, int.MinValue, 0), new Vector3Int(-1, 0, int.MaxValue)), "{\"position\":{\"x\":16777217,\"y\":-2147483648,\"z\":0},\"size\":{\"x\":-1,\"y\":0,\"z\":2147483647}}")
        };

        public enum SampleMode
        {
            First
        }

        public sealed class Payload
        {
            public Vector2 Offset;
            public Vector3 SpawnPoint;
            public Vector4 Plane;
            public Quaternion Rotation;
            public Color Tint;
            public Rect Window;
            public Vector3[] Points;
            public Quaternion? Optional;
            public ChildPayload Child;
            public SampleMode Mode;
            public Vector2Int Cell;
            public Vector3Int Chunk;
            public RectInt Occupancy;
            public Color32 ByteTint;
            public Bounds Region;
            public BoundsInt GridRegion;
            public Vector3Int[] Cells;
            public BoundsInt? OptionalGrid;
            public Color32? OptionalTint;
        }

        public sealed class ChildPayload
        {
            public Color Tint;
        }

        private static IEnumerable<TestCaseData> Shapes()
        {
            foreach (var schema in _schemas)
            {
                yield return new TestCaseData(schema.Type, schema.NullableType, schema.Value, schema.Json)
                    .SetName("CompleteComponentsRoundTrip(" + schema.Type.Name + ")");
            }
        }

        [TestCaseSource(nameof(Shapes))]
        public void CompleteComponentsRoundTrip(Type type, Type nullableType, object value, string expected)
        {
            var converter = Converter();
            Assert.That(converter.CanConvert(type), Is.True);
            Assert.That(converter.CanConvert(nullableType), Is.True);
            var settings = Settings(converter);
            settings.DefaultValueHandling = DefaultValueHandling.Ignore;
            settings.NullValueHandling = NullValueHandling.Ignore;
            var json = JsonConvert.SerializeObject(value, settings);
            Assert.That(JToken.DeepEquals(JToken.Parse(json), JToken.Parse(expected)), Is.True, json);
            Assert.That(JsonConvert.DeserializeObject(expected, type, settings), Is.EqualTo(value));
            Assert.That(JsonConvert.DeserializeObject(expected, nullableType, settings), Is.EqualTo(value));
            Assert.That(JsonConvert.DeserializeObject("null", nullableType, settings), Is.Null);
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject("null", type, settings));
        }

        [TestCase(0, 0, 0, 0)]
        [TestCase(0, 0, 0, 1)]
        [TestCase(1, 2, 3, 4)]
        public void QuaternionKeepsZeroIdentityAndNonUnitValues(float x, float y, float z, float w)
        {
            var value = new Quaternion(x, y, z, w);
            var settings = Settings(Converter());
            var json = JsonConvert.SerializeObject(value, settings);
            Assert.That(JObject.Parse(json).Properties().Select(property => property.Name), Is.EqualTo(new[] { "x", "y", "z", "w" }));
            Assert.That(JsonConvert.DeserializeObject<Quaternion>(json, settings), Is.EqualTo(value));
        }

        private static IEnumerable<TestCaseData> InvalidInputs()
        {
            foreach (var schema in _schemas)
            {
                var complete = JObject.Parse(schema.Json);
                var first = complete.Properties().First().Name;
                var rest = "," + string.Join(",", complete.Properties().Skip(1).Select(property =>
                    JsonConvert.ToString(property.Name) + ":" + property.Value.ToString(Formatting.None))) + "}";
                var missing = JObject.Parse(schema.Json);
                missing.Remove(first);
                var cases = new Dictionary<string, string>
                {
                    ["missing"] = missing.ToString(Formatting.None),
                    ["duplicate"] = schema.Json.Insert(1, JsonConvert.ToString(first) + ":" + complete[first].ToString(Formatting.None) + ","),
                    ["unknown"] = schema.Json.Insert(1, "\"unexpected\":0,"),
                    ["caseSensitive"] = schema.Json.Replace("\"" + first + "\"", "\"" + first.ToUpperInvariant() + "\""),
                    ["empty"] = "{}",
                    ["nullRoot"] = "null",
                    ["arrayRoot"] = "[]",
                    ["stringRoot"] = "\"value\"",
                    ["numberRoot"] = "1",
                    ["booleanRoot"] = "false",
                    ["truncated"] = schema.Json.Substring(0, schema.Json.Length - 1)
                };
                var components = new Dictionary<string, string>
                {
                    ["stringComponent"] = "\"1.25\"",
                    ["nullComponent"] = "null",
                    ["booleanComponent"] = "true",
                    ["objectComponent"] = "{}",
                    ["arrayComponent"] = "[]",
                    ["nan"] = "NaN",
                    ["positiveInfinity"] = "Infinity",
                    ["negativeInfinity"] = "-Infinity",
                    ["doubleOverflow"] = "1e400",
                    ["positiveOverflow"] = "3.5e38",
                    ["negativeOverflow"] = "-3.5e38",
                    ["integerOverflow"] = "340282346638528859811704183484516925441",
                    ["positiveUnderflow"] = "1e-100",
                    ["negativeUnderflow"] = "-1e-100"
                };
                foreach (var component in components)
                {
                    cases.Add(component.Key, "{\"" + first + "\":" + component.Value + rest);
                }
                foreach (var invalid in cases)
                {
                    yield return new TestCaseData(schema.Type, invalid.Value)
                        .SetName("RejectsInvalidComponents(" + schema.Type.Name + "," + invalid.Key + ")");
                }
            }
        }

        [TestCaseSource(nameof(InvalidInputs))]
        public void RejectsInvalidComponents(Type type, string json)
        {
            var settings = Settings(Converter());
            settings.MissingMemberHandling = MissingMemberHandling.Ignore;
            Assert.That(() => JsonConvert.DeserializeObject(json, type, settings), Throws.InstanceOf<JsonException>(), json);
        }

        private static IEnumerable<TestCaseData> NonFiniteValues()
        {
            foreach (var number in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                yield return new TestCaseData(new Vector2(number, 1));
                yield return new TestCaseData(new Vector3(0, number, 1));
                yield return new TestCaseData(new Vector4(0, 0, 0, number));
                yield return new TestCaseData(new Quaternion(0, number, 0, 1));
                yield return new TestCaseData(new Color(0, 0, 0, number));
                yield return new TestCaseData(new Rect(0, 0, number, 1));
                yield return new TestCaseData(new Bounds(new Vector3(number, 0, 0), Vector3.zero));
                yield return new TestCaseData(new Bounds(Vector3.zero, Vector3.zero) { extents = new Vector3(0, number, 0) });
            }
        }

        [TestCaseSource(nameof(NonFiniteValues))]
        public void RefusesToWriteNonFiniteComponents(object value)
        {
            var settings = Settings(Converter());
            settings.FloatFormatHandling = FloatFormatHandling.DefaultValue;
            Assert.Throws<JsonSerializationException>(() => JsonConvert.SerializeObject(value, settings));
        }

        [Test]
        public void SingleExtremesAndSubnormalsRoundTripWithoutOverflowOrSilentZero()
        {
            var source = new Vector4(float.MaxValue, float.MinValue, float.Epsilon, -float.Epsilon);
            var settings = Settings(Converter());
            var json = JsonConvert.SerializeObject(source, settings);
            Assert.That(JsonConvert.DeserializeObject<Vector4>(json, settings), Is.EqualTo(source), json);
            var maxInteger = "{\"x\":340282346638528859811704183484516925440,\"y\":-340282346638528859811704183484516925440}";
            Assert.That(JsonConvert.DeserializeObject<Vector2>(maxInteger, settings), Is.EqualTo(new Vector2(float.MaxValue, float.MinValue)));
            Assert.That(JsonConvert.DeserializeObject<Vector2>("{\"y\":-1.25e-2,\"x\":2E1}", settings), Is.EqualTo(new Vector2(20, -0.0125f)));
            Assert.That(JsonConvert.DeserializeObject<Vector2>("{\"x\":100000000000000000000,\"y\":0}", settings), Is.EqualTo(new Vector2(1e20f, 0)));
        }

        [Test]
        public void NestedDtosKeepCallerNamingConvertersCultureAndNullableSemantics()
        {
            var existing = new StringEnumConverter();
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
                Culture = CultureInfo.GetCultureInfo("fr-FR"),
                FloatParseHandling = FloatParseHandling.Decimal
            };
            settings.Converters.Add(existing);
            var converter = Converter();
            settings.Converters.Add(converter);
            var source = new Payload
            {
                Offset = new Vector2(0, -1.25f),
                SpawnPoint = new Vector3(1, 2, 3),
                Plane = new Vector4(0, 2, 0, 4),
                Rotation = new Quaternion(1, 2, 3, 4),
                Tint = new Color(1.5f, -0.25f, 0, 0),
                Window = new Rect(1, 2, -3, 0),
                Points = new[] { Vector3.zero, new Vector3(-1, 0.5f, 0) },
                Optional = null,
                Child = new ChildPayload { Tint = Color.clear },
                Mode = SampleMode.First,
                Cell = new Vector2Int(16777217, int.MinValue),
                Chunk = new Vector3Int(int.MaxValue, -16777217, 0),
                Occupancy = new RectInt(16777217, int.MinValue, -3, 0),
                ByteTint = new Color32(0, 128, 255, 0),
                Region = new Bounds(new Vector3(1, -2, 0), Vector3.zero) { extents = new Vector3(-1, 0, 0.5f) },
                GridRegion = new BoundsInt(new Vector3Int(16777217, 0, -16777217), new Vector3Int(-1, 0, int.MaxValue)),
                Cells = new[] { Vector3Int.zero, new Vector3Int(int.MinValue, 16777217, int.MaxValue) },
                OptionalGrid = null,
                OptionalTint = new Color32(255, 0, 0, 0)
            };
            var json = JsonConvert.SerializeObject(source, settings);
            var tree = JObject.Parse(json);
            Assert.That(tree["spawn_point"].Value<float>("x"), Is.EqualTo(1));
            Assert.That((string)tree["mode"], Is.EqualTo("First"));
            Assert.That(tree["optional"].Type, Is.EqualTo(JTokenType.Null));
            var copy = JsonConvert.DeserializeObject<Payload>(json, settings);
            Assert.That(copy.Offset, Is.EqualTo(source.Offset));
            Assert.That(copy.SpawnPoint, Is.EqualTo(source.SpawnPoint));
            Assert.That(copy.Plane, Is.EqualTo(source.Plane));
            Assert.That(copy.Rotation, Is.EqualTo(source.Rotation));
            Assert.That(copy.Tint, Is.EqualTo(source.Tint));
            Assert.That(copy.Window, Is.EqualTo(source.Window));
            Assert.That(copy.Points, Is.EqualTo(source.Points));
            Assert.That(copy.Optional, Is.Null);
            Assert.That(copy.Child.Tint, Is.EqualTo(Color.clear));
            Assert.That(copy.Cell, Is.EqualTo(source.Cell));
            Assert.That(copy.Chunk, Is.EqualTo(source.Chunk));
            Assert.That(copy.Occupancy, Is.EqualTo(source.Occupancy));
            Assert.That(copy.ByteTint, Is.EqualTo(source.ByteTint));
            Assert.That(copy.Region.center, Is.EqualTo(source.Region.center));
            Assert.That(copy.Region.extents, Is.EqualTo(source.Region.extents));
            Assert.That(copy.GridRegion.position, Is.EqualTo(source.GridRegion.position));
            Assert.That(copy.GridRegion.size, Is.EqualTo(source.GridRegion.size));
            Assert.That(copy.Cells, Is.EqualTo(source.Cells));
            Assert.That(copy.OptionalGrid, Is.Null);
            Assert.That(copy.OptionalTint, Is.EqualTo(source.OptionalTint));
            Assert.That(settings.Converters, Is.EqualTo(new JsonConverter[] { existing, converter }));
            Assert.That(settings.FloatParseHandling, Is.EqualTo(FloatParseHandling.Decimal));
        }

        [Test]
        public void ExplicitRegistrationDoesNotChangeDefaultOrUnconfiguredSettings()
        {
            var defaults = JsonConvert.DefaultSettings;
            const string partial = "{\"x\":2}";
            var baseline = JsonConvert.DeserializeObject<Vector2>(partial, new JsonSerializerSettings());
            var configured = Settings(Converter());
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<Vector2>(partial, configured));
            Assert.That(JsonConvert.DeserializeObject<Vector2>(partial, new JsonSerializerSettings()), Is.EqualTo(baseline));
            Assert.That(JsonConvert.DefaultSettings, Is.SameAs(defaults));
            Assert.That(JsonConvert.SerializeObject(new
            {
                Number = 0
            }), Is.EqualTo("{\"Number\":0}"));
            foreach (var unsupported in new[] { typeof(LayerMask), typeof(Ray), typeof(Ray2D), typeof(RectOffset), typeof(Matrix4x4), typeof(int), typeof(Vector3[]) })
            {
                Assert.That(configured.Converters[0].CanConvert(unsupported), Is.False, unsupported.FullName);
            }
        }

        [Test]
        public void ExistingValuesCannotFillMissingComponentsAndErrorsKeepTheirPath()
        {
            var source = new Payload { SpawnPoint = new Vector3(10, 20, 30) };
            var settings = Settings(Converter());
            Assert.Throws<JsonSerializationException>(() => JsonConvert.PopulateObject("{\"SpawnPoint\":{\"x\":1}}", source, settings));
            var exception = Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<Payload>("{\"SpawnPoint\":{\"x\":\"1\",\"y\":2,\"z\":3}}", settings));
            Assert.That(exception.Message, Does.Contain("SpawnPoint.x"));
        }

        private static IEnumerable<TestCaseData> InvalidIntegerInputs()
        {
            foreach (var schema in _schemas.Where(schema => schema.Type == typeof(Vector2Int) || schema.Type == typeof(Vector3Int) || schema.Type == typeof(RectInt) || schema.Type == typeof(Color32)))
            {
                var first = JObject.Parse(schema.Json).Properties().First().Name;
                var rest = schema.Json.Substring(schema.Json.IndexOf(','));
                var invalid = new[] { "1.0", "1e0", "1.25", "-0.5", "2147483648", "-2147483649", "18446744073709551615", "9999999999999999999999999999999999999999" };
                if (schema.Type == typeof(Color32))
                {
                    invalid = invalid.Concat(new[] { "-1", "256" }).ToArray();
                }
                foreach (var number in invalid)
                {
                    yield return new TestCaseData(schema.Type, "{\"" + first + "\":" + number + rest)
                        .SetName("RejectsNonIntegralOrOutOfRange(" + schema.Type.Name + "," + number + ")");
                }
            }
        }

        [TestCaseSource(nameof(InvalidIntegerInputs))]
        public void RejectsNonIntegralOrOutOfRange(Type type, string json)
        {
            var settings = Settings(Converter());
            Assert.That(() => JsonConvert.DeserializeObject(json, type, settings), Throws.InstanceOf<JsonException>(), json);
        }

        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        [TestCase(16777217)]
        [TestCase(-16777217)]
        [TestCase(0)]
        public void IntegerValuesNeverPassThroughSinglePrecision(int number)
        {
            var settings = Settings(Converter());
            var source = new Vector3Int(number, number, number);
            var json = JsonConvert.SerializeObject(source, settings);
            var tree = JObject.Parse(json);
            foreach (var component in tree.Properties())
            {
                Assert.That(component.Value.Type, Is.EqualTo(JTokenType.Integer));
                Assert.That(component.Value.Value<long>(), Is.EqualTo((long)number));
            }
            Assert.That(JsonConvert.DeserializeObject<Vector3Int>(json, settings), Is.EqualTo(source));
        }

        [Test]
        public void UnsignedJTokenIntegersFailWithJsonErrorsRatherThanOverflowOrWrap()
        {
            var token = new JObject { ["x"] = new JValue(ulong.MaxValue), ["y"] = 0 };
            var serializer = JsonSerializer.Create(Settings(Converter()));
            using var reader = token.CreateReader();
            Assert.That(() => serializer.Deserialize<Vector2Int>(reader), Throws.InstanceOf<JsonException>());
        }

        [Test]
        public void BoundsPreserveTheirStoredRepresentationWithoutDerivedOverflow()
        {
            var settings = Settings(Converter());
            var source = new Bounds(new Vector3(-1, 0, 2), Vector3.zero) { extents = new Vector3(float.MaxValue, float.MinValue, float.Epsilon) };
            var json = JsonConvert.SerializeObject(source, settings);
            Assert.That(JObject.Parse(json).Properties().Select(property => property.Name), Is.EqualTo(new[] { "center", "extents" }));
            var copy = JsonConvert.DeserializeObject<Bounds>(json, settings);
            Assert.That(copy.center, Is.EqualTo(source.center));
            Assert.That(copy.extents, Is.EqualTo(source.extents));
            var cells = new BoundsInt(new Vector3Int(int.MinValue, 16777217, int.MaxValue), new Vector3Int(int.MinValue, -1, int.MaxValue));
            var gridJson = JsonConvert.SerializeObject(cells, settings);
            Assert.That(JObject.Parse(gridJson).Properties().Select(property => property.Name), Is.EqualTo(new[] { "position", "size" }));
            var grid = JsonConvert.DeserializeObject<BoundsInt>(gridJson, settings);
            Assert.That(grid.position, Is.EqualTo(cells.position));
            Assert.That(grid.size, Is.EqualTo(cells.size));
        }

        [TestCase(typeof(Bounds), "{\"center\":{\"x\":0,\"y\":0},\"extents\":{\"x\":0,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(Bounds), "{\"center\":{\"x\":0,\"y\":0,\"z\":0},\"extents\":{\"x\":0,\"y\":0,\"z\":0,\"size\":1}}")]
        [TestCase(typeof(Bounds), "{\"center\":{\"x\":0,\"x\":1,\"y\":0,\"z\":0},\"extents\":{\"x\":0,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(Bounds), "{\"center\":null,\"extents\":{\"x\":0,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(Bounds), "{\"center\":{\"x\":0,\"y\":0,\"z\":0},\"size\":{\"x\":0,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(BoundsInt), "{\"position\":{\"x\":0,\"y\":0},\"size\":{\"x\":0,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(BoundsInt), "{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"size\":{\"x\":0,\"y\":0,\"z\":0,\"max\":1}}")]
        [TestCase(typeof(BoundsInt), "{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"size\":{\"x\":0,\"x\":1,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(BoundsInt), "{\"position\":{\"x\":1.25,\"y\":0,\"z\":0},\"size\":{\"x\":0,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(BoundsInt), "{\"position\":{\"x\":2147483648,\"y\":0,\"z\":0},\"size\":{\"x\":0,\"y\":0,\"z\":0}}")]
        [TestCase(typeof(BoundsInt), "{\"position\":{\"x\":0,\"y\":0,\"z\":0},\"size\":null}")]
        [TestCase(typeof(BoundsInt), "{\"min\":{\"x\":0,\"y\":0,\"z\":0},\"max\":{\"x\":0,\"y\":0,\"z\":0}}")]
        public void BoundsRejectIncompleteDuplicateUnknownAndInvalidNestedComponents(Type type, string json)
        {
            var settings = Settings(Converter());
            Assert.That(() => JsonConvert.DeserializeObject(json, type, settings), Throws.InstanceOf<JsonException>());
        }

        private static JsonSerializerSettings Settings(JsonConverter converter)
        {
            return new JsonSerializerSettings { Converters = { converter } };
        }

        private static JsonConverter Converter()
        {
            return new UnityValueJsonConverter();
        }
    }
}

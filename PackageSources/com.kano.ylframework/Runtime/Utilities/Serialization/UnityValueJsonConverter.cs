using System;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;
using BigInteger = System.Numerics.BigInteger;

namespace YLFramework.Serialization
{
    /// <summary>
    /// Explicit Json.NET support for twelve Unity value types and their nullable forms.
    /// Components are complete and case-sensitive, with separate Single, Int32 and byte numeric contracts.
    /// </summary>
    public sealed class UnityValueJsonConverter : JsonConverter
    {
        private static readonly string[] _vector2Names = { "x", "y" };
        private static readonly string[] _vector3Names = { "x", "y", "z" };
        private static readonly string[] _vector4Names = { "x", "y", "z", "w" };
        private static readonly string[] _colorNames = { "r", "g", "b", "a" };
        private static readonly string[] _rectNames = { "x", "y", "width", "height" };
        private static readonly string[] _boundsNames = { "center", "extents" };
        private static readonly string[] _boundsIntNames = { "position", "size" };
        private static readonly BigInteger _maximumSingleInteger = new BigInteger(float.MaxValue);

        /// <inheritdoc />
        public override bool CanConvert(Type objectType)
        {
            var type = Nullable.GetUnderlyingType(objectType) ?? objectType;
            return type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
                type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Rect) ||
                type == typeof(Vector2Int) || type == typeof(Vector3Int) || type == typeof(RectInt) ||
                type == typeof(Color32) || type == typeof(Bounds) || type == typeof(BoundsInt);
        }

        /// <summary>Writes every component without normalization, clamping or global settings changes.</summary>
        /// <exception cref="JsonSerializationException">The value is unsupported or contains a non-finite component.</exception>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            var names = ComponentNames(value.GetType());
            var children = value switch
            {
                Bounds bounds => new object[] { bounds.center, bounds.extents },
                BoundsInt bounds => new object[] { bounds.position, bounds.size },
                _ => null
            };
            var integers = value switch
            {
                Vector2Int vector => new[] { vector.x, vector.y },
                Vector3Int vector => new[] { vector.x, vector.y, vector.z },
                RectInt rect => new[] { rect.x, rect.y, rect.width, rect.height },
                Color32 color => new[] { (int)color.r, color.g, color.b, color.a },
                _ => null
            };
            var values = children != null || integers != null ? null : value switch
            {
                Vector2 vector => new[] { vector.x, vector.y },
                Vector3 vector => new[] { vector.x, vector.y, vector.z },
                Vector4 vector => new[] { vector.x, vector.y, vector.z, vector.w },
                Quaternion quaternion => new[] { quaternion.x, quaternion.y, quaternion.z, quaternion.w },
                Color color => new[] { color.r, color.g, color.b, color.a },
                Rect rect => new[] { rect.x, rect.y, rect.width, rect.height },
                _ => throw new JsonSerializationException("Unsupported Unity value type: " + value.GetType().FullName)
            };
            for (var i = 0; values != null && i < values.Length; i++)
            {
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                {
                    throw new JsonSerializationException("Non-finite component '" + names[i] + "' at path '" + writer.Path + "'.");
                }
            }

            writer.WriteStartObject();
            for (var i = 0; i < names.Length; i++)
            {
                writer.WritePropertyName(names[i]);
                if (children != null)
                {
                    WriteJson(writer, children[i], serializer);
                }
                else if (integers != null)
                {
                    writer.WriteValue(integers[i]);
                }
                else
                {
                    // Mono's Single formatter can round Single.MaxValue above the accepted numeric range.
                    writer.WriteValue((double)values[i]);
                }
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads a complete component object. Only a nullable target accepts JSON null; existing values never fill gaps.
        /// </summary>
        /// <exception cref="JsonSerializationException">The object, component set or numeric value is invalid.</exception>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var nullable = Nullable.GetUnderlyingType(objectType);
            var type = nullable ?? objectType;
            var names = ComponentNames(type);
            if (reader.TokenType == JsonToken.Null && nullable != null)
            {
                return null;
            }
            if (reader.TokenType != JsonToken.StartObject)
            {
                throw Invalid(reader, "Expected a complete component object for " + type.Name + ".");
            }

            var isInteger = type == typeof(Vector2Int) || type == typeof(Vector3Int) || type == typeof(RectInt) || type == typeof(Color32);
            var isBounds = type == typeof(Bounds) || type == typeof(BoundsInt);
            var values = isInteger || isBounds ? null : new float[names.Length];
            var integers = isInteger ? new int[names.Length] : null;
            var children = isBounds ? new object[names.Length] : null;
            var seen = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                {
                    if (seen != (1 << names.Length) - 1)
                    {
                        throw Invalid(reader, "All components are required: " + string.Join(", ", names) + ".");
                    }
                    if (isInteger)
                    {
                        return CreateIntegerValue(type, integers);
                    }
                    if (type == typeof(Bounds))
                    {
                        return new Bounds((Vector3)children[0], Vector3.zero) { extents = (Vector3)children[1] };
                    }
                    if (type == typeof(BoundsInt))
                    {
                        return new BoundsInt((Vector3Int)children[0], (Vector3Int)children[1]);
                    }
                    return CreateValue(type, values);
                }
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    throw Invalid(reader, "Expected a component name.");
                }

                var name = (string)reader.Value;
                var index = Array.IndexOf(names, name);
                if (index < 0)
                {
                    throw Invalid(reader, "Unknown component '" + name + "' for " + type.Name + ".");
                }
                if ((seen & (1 << index)) != 0)
                {
                    throw Invalid(reader, "Duplicate component '" + name + "'.");
                }
                if (!reader.Read())
                {
                    throw Invalid(reader, "Unexpected end of component value.");
                }
                if (isBounds)
                {
                    children[index] = ReadJson(reader, type == typeof(Bounds) ? typeof(Vector3) : typeof(Vector3Int), null, serializer);
                }
                else if (isInteger)
                {
                    integers[index] = ReadIntegerComponent(reader, type == typeof(Color32));
                }
                else
                {
                    values[index] = ReadComponent(reader);
                }
                seen |= 1 << index;
            }
            throw Invalid(reader, "Unexpected end of component object.");
        }

        private static float ReadComponent(JsonReader reader)
        {
            if (reader.TokenType != JsonToken.Integer && reader.TokenType != JsonToken.Float)
            {
                throw Invalid(reader, "A component must be a JSON number, not " + reader.TokenType + ".");
            }
            if (reader.Value is BigInteger integer && (integer > _maximumSingleInteger || integer < -_maximumSingleInteger))
            {
                throw Invalid(reader, "The integer component is outside the Single range.");
            }
            var number = reader.Value is BigInteger large ? (double)large : Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number) || number > float.MaxValue || number < float.MinValue)
            {
                throw Invalid(reader, "A component must be finite and within the Single range.");
            }
            var value = (float)number;
            if (number != 0 && value == 0)
            {
                throw Invalid(reader, "The nonzero component underflows to Single zero.");
            }
            return value;
        }

        private static int ReadIntegerComponent(JsonReader reader, bool byteChannel)
        {
            if (reader.TokenType != JsonToken.Integer)
            {
                throw Invalid(reader, "An integer component requires an Integer token, not " + reader.TokenType + ".");
            }
            var integer = reader.Value switch
            {
                BigInteger large => large,
                ulong unsigned => new BigInteger(unsigned),
                _ => new BigInteger(Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture))
            };
            var minimum = byteChannel ? byte.MinValue : int.MinValue;
            var maximum = byteChannel ? byte.MaxValue : int.MaxValue;
            if (integer < minimum || integer > maximum)
            {
                throw Invalid(reader, byteChannel ? "A Color32 component must be an integer from 0 to 255." : "The integer component is outside the Int32 range.");
            }
            return (int)integer;
        }

        private static string[] ComponentNames(Type type)
        {
            if (type == typeof(Vector2) || type == typeof(Vector2Int))
            {
                return _vector2Names;
            }
            if (type == typeof(Vector3) || type == typeof(Vector3Int))
            {
                return _vector3Names;
            }
            if (type == typeof(Vector4) || type == typeof(Quaternion))
            {
                return _vector4Names;
            }
            if (type == typeof(Color) || type == typeof(Color32))
            {
                return _colorNames;
            }
            if (type == typeof(Rect) || type == typeof(RectInt))
            {
                return _rectNames;
            }
            if (type == typeof(Bounds))
            {
                return _boundsNames;
            }
            if (type == typeof(BoundsInt))
            {
                return _boundsIntNames;
            }
            throw new JsonSerializationException("Unsupported Unity value type: " + type.FullName);
        }

        private static object CreateIntegerValue(Type type, int[] values)
        {
            if (type == typeof(Vector2Int))
            {
                return new Vector2Int(values[0], values[1]);
            }
            if (type == typeof(Vector3Int))
            {
                return new Vector3Int(values[0], values[1], values[2]);
            }
            if (type == typeof(RectInt))
            {
                return new RectInt(values[0], values[1], values[2], values[3]);
            }
            return new Color32((byte)values[0], (byte)values[1], (byte)values[2], (byte)values[3]);
        }

        private static object CreateValue(Type type, float[] values)
        {
            if (type == typeof(Vector2))
            {
                return new Vector2(values[0], values[1]);
            }
            if (type == typeof(Vector3))
            {
                return new Vector3(values[0], values[1], values[2]);
            }
            if (type == typeof(Vector4))
            {
                return new Vector4(values[0], values[1], values[2], values[3]);
            }
            if (type == typeof(Quaternion))
            {
                return new Quaternion(values[0], values[1], values[2], values[3]);
            }
            if (type == typeof(Color))
            {
                return new Color(values[0], values[1], values[2], values[3]);
            }
            return new Rect(values[0], values[1], values[2], values[3]);
        }

        private static JsonSerializationException Invalid(JsonReader reader, string message)
        {
            return new JsonSerializationException(message + " Path '" + reader.Path + "'.");
        }
    }
}

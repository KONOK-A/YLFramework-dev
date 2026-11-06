using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    public enum OverlapShapeKind3D
    {
        Sphere,
        Box,
        Capsule
    }

    /// <summary>World-space geometry with positive volume. Capsule endpoints may coincide (a sphere).</summary>
    public readonly struct OverlapShape3D
    {
        public OverlapShapeKind3D Kind
        {
            get;
        }
        public Vector3 Center
        {
            get;
        }
        public Vector3 Size
        {
            get;
        }
        public Quaternion Orientation
        {
            get;
        }
        public Vector3 Point1
        {
            get;
        }
        public Vector3 Point2
        {
            get;
        }
        public float Radius
        {
            get;
        }

        private OverlapShape3D(OverlapShapeKind3D kind, Vector3 center, Vector3 size, Quaternion orientation,
            Vector3 point1, Vector3 point2, float radius)
        {
            Kind = kind;
            Center = center;
            Size = size;
            Orientation = orientation;
            Point1 = point1;
            Point2 = point2;
            Radius = radius;
            Validate();
        }

        public static OverlapShape3D Sphere(Vector3 center, float radius) =>
            new OverlapShape3D(OverlapShapeKind3D.Sphere, center, default, Quaternion.identity, default, default, radius);

        /// <param name="size">Full positive dimensions before rotation.</param>
        /// <param name="orientation">Finite nonzero quaternion, normalized by this factory.</param>
        public static OverlapShape3D Box(Vector3 center, Vector3 size, Quaternion orientation) =>
            new OverlapShape3D(OverlapShapeKind3D.Box, center, size, Normalize(orientation), default, default, 0);

        /// <summary>Endpoints are the centers of the two spherical caps, not the outer tips.</summary>
        public static OverlapShape3D Capsule(Vector3 point1, Vector3 point2, float radius) =>
            new OverlapShape3D(OverlapShapeKind3D.Capsule, point1 * 0.5f + point2 * 0.5f,
                default, Quaternion.identity, point1, point2, radius);

        internal void Validate()
        {
            ValidateVector(Center, "center");
            switch (Kind)
            {
                case OverlapShapeKind3D.Sphere:
                    ValidatePositive(Radius, "radius");
                    break;
                case OverlapShapeKind3D.Box:
                    ValidatePositive(Size.x, "size");
                    ValidatePositive(Size.y, "size");
                    ValidatePositive(Size.z, "size");
                    Normalize(Orientation);
                    break;
                case OverlapShapeKind3D.Capsule:
                    ValidateVector(Point1, "point1");
                    ValidateVector(Point2, "point2");
                    ValidatePositive(Radius, "radius");
                    break;
                default:
                    throw new ArgumentOutOfRangeException("shape");
            }
        }

        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static void ValidateVector(Vector3 value, string parameter)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
            {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }

        private static void ValidatePositive(float value, string parameter)
        {
            if (!IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(parameter, "A finite positive dimension is required.");
            }
        }

        private static Quaternion Normalize(Quaternion value)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z) || !IsFinite(value.w))
            {
                throw new ArgumentOutOfRangeException("orientation");
            }
            float max = Mathf.Max(Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y)),
                Mathf.Max(Mathf.Abs(value.z), Mathf.Abs(value.w)));
            if (max == 0)
            {
                throw new ArgumentOutOfRangeException("orientation");
            }
            value = new Quaternion(value.x / max, value.y / max, value.z / max, value.w / max);
            float length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            return new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length);
        }
    }
}

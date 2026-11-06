using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    public enum OverlapShapeKind3D
    {
        Sphere,
        Box,
        Capsule,
        Sector,
        Cone
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
        public float InnerRadius
        {
            get;
        }
        public float AngleDegrees
        {
            get;
        }
        public float Height
        {
            get;
        }
        internal bool NeedsWorkspace => Kind == OverlapShapeKind3D.Sector || Kind == OverlapShapeKind3D.Cone;

        private OverlapShape3D(OverlapShapeKind3D kind, Vector3 center, Vector3 size, Quaternion orientation,
            Vector3 point1, Vector3 point2, float radius, float innerRadius = 0, float angleDegrees = 0, float height = 0)
        {
            Kind = kind;
            Center = center;
            Size = size;
            Orientation = orientation;
            Point1 = point1;
            Point2 = point2;
            Radius = radius;
            InnerRadius = innerRadius;
            AngleDegrees = angleDegrees;
            Height = height;
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

        /// <summary>Local XZ sector, +Z middle direction, +Y centered thickness. Radius is the outer radius.</summary>
        public static OverlapShape3D Sector(Vector3 center, Quaternion rotation, float outerRadius,
            float angleDegrees, float height, float innerRadius = 0) =>
            new OverlapShape3D(OverlapShapeKind3D.Sector, center, default, Normalize(rotation), default, default,
                outerRadius, innerRadius, angleDegrees, height);

        public static OverlapShape3D Sector(Vector3 center, float outerRadius, float angleDegrees, float height,
            float innerRadius = 0) => Sector(center, Quaternion.identity, outerRadius, angleDegrees, height, innerRadius);

        public static OverlapShape3D AnnularSector(Vector3 center, Quaternion rotation, float innerRadius,
            float outerRadius, float angleDegrees, float height) => Sector(center, rotation, outerRadius, angleDegrees, height, innerRadius);

        public static OverlapShape3D Ring(Vector3 center, Quaternion rotation, float innerRadius, float outerRadius,
            float height) => Sector(center, rotation, outerRadius, 360, height, innerRadius);

        public static OverlapShape3D Ring(Vector3 center, float innerRadius, float outerRadius, float height) =>
            Ring(center, Quaternion.identity, innerRadius, outerRadius, height);

        /// <summary>Finite +Z cone, Center is the apex, Radius is the closed base radius.</summary>
        public static OverlapShape3D Cone(Vector3 apex, Quaternion rotation, float height, float baseRadius) =>
            new OverlapShape3D(OverlapShapeKind3D.Cone, apex, default, Normalize(rotation), default, default,
                baseRadius, 0, 0, height);

        public static OverlapShape3D Cone(Vector3 apex, float height, float baseRadius) =>
            Cone(apex, Quaternion.identity, height, baseRadius);

        public static OverlapShape3D ConeFromAngle(Vector3 apex, Quaternion rotation, float height, float angleDegrees)
        {
            if (!IsFinite(angleDegrees) || angleDegrees <= 0 || angleDegrees >= 180)
                throw new ArgumentOutOfRangeException(nameof(angleDegrees));
            ValidatePositive(height, nameof(height));
            return Cone(apex, rotation, height, (float)(height * Math.Tan(angleDegrees * (Math.PI / 360))));
        }

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
                case OverlapShapeKind3D.Sector:
                    ValidatePositive(Radius, "outerRadius");
                    ValidatePositive(Height, "height");
                    Normalize(Orientation);
                    if (!IsFinite(InnerRadius) || InnerRadius < 0 || InnerRadius >= Radius)
                        throw new ArgumentOutOfRangeException("innerRadius");
                    if (!IsFinite(AngleDegrees) || AngleDegrees <= 0 || AngleDegrees > 360)
                        throw new ArgumentOutOfRangeException("angleDegrees");
                    break;
                case OverlapShapeKind3D.Cone:
                    ValidatePositive(Radius, "baseRadius");
                    ValidatePositive(Height, "height");
                    Normalize(Orientation);
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

using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    public enum OverlapShapeKind2D
    {
        Point,
        Circle,
        Box,
        Capsule
    }

    /// <summary>World-space geometry. Volumes require positive dimensions; Point has no extent.</summary>
    public readonly struct OverlapShape2D
    {
        public OverlapShapeKind2D Kind
        {
            get;
        }
        public Vector2 Center
        {
            get;
        }
        public Vector2 Size
        {
            get;
        }
        public float Radius
        {
            get;
        }
        public float Angle
        {
            get;
        }
        public CapsuleDirection2D CapsuleDirection
        {
            get;
        }

        private OverlapShape2D(OverlapShapeKind2D kind, Vector2 center, Vector2 size, float radius,
            float angle, CapsuleDirection2D capsuleDirection)
        {
            Kind = kind;
            Center = center;
            Size = size;
            Radius = radius;
            Angle = angle;
            CapsuleDirection = capsuleDirection;
            Validate();
        }

        public static OverlapShape2D Point(Vector2 center) =>
            new OverlapShape2D(OverlapShapeKind2D.Point, center, default, 0, 0, default);

        public static OverlapShape2D Circle(Vector2 center, float radius) =>
            new OverlapShape2D(OverlapShapeKind2D.Circle, center, default, radius, 0, default);

        /// <param name="size">Full positive dimensions before rotation.</param>
        /// <param name="angle">World Z rotation in degrees.</param>
        public static OverlapShape2D Box(Vector2 center, Vector2 size, float angle = 0) =>
            new OverlapShape2D(OverlapShapeKind2D.Box, center, size, 0, angle, default);

        /// <summary>The axial dimension must be at least the transverse dimension; equality is a circle.</summary>
        public static OverlapShape2D Capsule(Vector2 center, Vector2 size, CapsuleDirection2D capsuleDirection,
            float angle = 0) =>
            new OverlapShape2D(OverlapShapeKind2D.Capsule, center, size, 0, angle, capsuleDirection);

        internal void Validate()
        {
            ValidateVector(Center, "center");
            if (Kind == OverlapShapeKind2D.Circle)
            {
                ValidatePositive(Radius, "radius");
            }
            else if (Kind == OverlapShapeKind2D.Box || Kind == OverlapShapeKind2D.Capsule)
            {
                ValidatePositive(Size.x, "size");
                ValidatePositive(Size.y, "size");
                if (!IsFinite(Angle))
                {
                    throw new ArgumentOutOfRangeException("angle");
                }
                if (Kind == OverlapShapeKind2D.Capsule)
                {
                    if (CapsuleDirection != CapsuleDirection2D.Horizontal && CapsuleDirection != CapsuleDirection2D.Vertical)
                    {
                        throw new ArgumentOutOfRangeException("capsuleDirection");
                    }
                    if ((CapsuleDirection == CapsuleDirection2D.Horizontal && Size.x < Size.y) ||
                        (CapsuleDirection == CapsuleDirection2D.Vertical && Size.y < Size.x))
                    {
                        throw new ArgumentOutOfRangeException("size", "The capsule axis cannot be shorter than its diameter.");
                    }
                }
            }
            else if (Kind != OverlapShapeKind2D.Point)
            {
                throw new ArgumentOutOfRangeException("shape");
            }
        }

        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static void ValidateVector(Vector2 value, string parameter)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y))
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
    }
}

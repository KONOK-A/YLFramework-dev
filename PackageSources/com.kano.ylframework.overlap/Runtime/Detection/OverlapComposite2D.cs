using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>Serializable local geometry only. Arrays are copied; no scene objects or runtime state are retained.</summary>
    [Serializable]
    public sealed class OverlapComposite2D
    {
        [Serializable]
        private struct LocalShape
        {
            public OverlapShapeKind2D kind;
            public Vector2 center;
            public Vector2 size;
            public float radius;
            public float angle;
            public CapsuleDirection2D direction;
        }

        [SerializeField] private LocalShape[] shapes;
        public int Count => shapes == null ? throw new InvalidOperationException("Local shapes cannot be null.") : shapes.Length;

        public OverlapComposite2D() : this(OverlapShape2D.Circle(Vector2.zero, 0.5f)) { }

        public OverlapComposite2D(params OverlapShape2D[] localShapes)
        {
            if (localShapes == null)
            {
                throw new ArgumentNullException(nameof(localShapes));
            }
            shapes = new LocalShape[localShapes.Length];
            for (int i = 0; i < shapes.Length; i++)
            {
                var shape = localShapes[i];
                shape.Validate();
                shapes[i] = new LocalShape
                {
                    kind = shape.Kind,
                    center = shape.Center,
                    size = shape.Size,
                    radius = shape.Radius,
                    angle = shape.Angle,
                    direction = shape.CapsuleDirection
                };
            }
        }

        public OverlapShape2D GetShape(int index)
        {
            var shape = shapes[index];
            switch (shape.kind)
            {
                case OverlapShapeKind2D.Point:
                    return OverlapShape2D.Point(shape.center);
                case OverlapShapeKind2D.Circle:
                    return OverlapShape2D.Circle(shape.center, shape.radius);
                case OverlapShapeKind2D.Box:
                    return OverlapShape2D.Box(shape.center, shape.size, shape.angle);
                case OverlapShapeKind2D.Capsule:
                    return OverlapShape2D.Capsule(shape.center, shape.size, shape.direction, shape.angle);
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }

        internal OverlapShape2D ToWorld(int index, Matrix4x4 matrix)
        {
            var shape = GetShape(index);
            Vector3 center = matrix.MultiplyPoint3x4(shape.Center);
            OverlapShape3D.ValidateVector(center, "center");
            if (shape.Kind == OverlapShapeKind2D.Point)
            {
                return OverlapShape2D.Point(center);
            }
            OverlapDetectionGeometry.Basis(matrix, Quaternion.Euler(0, 0, shape.Angle), out var x, out var y,
                out _, out var scale);
            if (Mathf.Abs(x.z) > 0.00001f || Mathf.Abs(y.z) > 0.00001f)
            {
                throw new InvalidOperationException("2D volume axes must remain in the XY plane.");
            }
            float angle = Mathf.Atan2(x.y, x.x) * Mathf.Rad2Deg;
            if (shape.Kind == OverlapShapeKind2D.Box)
            {
                return OverlapShape2D.Box(center, Vector2.Scale(shape.Size, scale), angle);
            }
            if (!OverlapDetectionGeometry.EqualScale(scale.x, scale.y))
            {
                throw new InvalidOperationException("Circles and capsules require uniform XY scale, not an elliptical bound.");
            }
            return shape.Kind == OverlapShapeKind2D.Circle ? OverlapShape2D.Circle(center, shape.Radius * scale.x) :
                OverlapShape2D.Capsule(center, shape.Size * scale.x, shape.CapsuleDirection, angle);
        }
    }
}

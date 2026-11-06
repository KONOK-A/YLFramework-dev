using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>Serializable local Sphere/Box/Capsule Union geometry; never stores scene objects or query state.</summary>
    [Serializable]
    public sealed class OverlapComposite3D
    {
        [Serializable]
        private struct LocalShape
        {
            public OverlapShapeKind3D kind;
            public Vector3 center;
            public Vector3 size;
            public Quaternion orientation;
            public Vector3 point1;
            public Vector3 point2;
            public float radius;
        }

        [SerializeField] private LocalShape[] shapes;
        public int Count => shapes == null ? throw new InvalidOperationException("Local shapes cannot be null.") : shapes.Length;

        public OverlapComposite3D() : this(OverlapShape3D.Sphere(Vector3.zero, 0.5f)) { }

        public OverlapComposite3D(params OverlapShape3D[] localShapes)
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
                    orientation = shape.Orientation,
                    point1 = shape.Point1,
                    point2 = shape.Point2,
                    radius = shape.Radius
                };
            }
        }

        public OverlapShape3D GetShape(int index)
        {
            var shape = shapes[index];
            switch (shape.kind)
            {
                case OverlapShapeKind3D.Sphere:
                    return OverlapShape3D.Sphere(shape.center, shape.radius);
                case OverlapShapeKind3D.Box:
                    return OverlapShape3D.Box(shape.center, shape.size, shape.orientation);
                case OverlapShapeKind3D.Capsule:
                    return OverlapShape3D.Capsule(shape.point1, shape.point2, shape.radius);
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }

        internal OverlapShape3D ToWorld(int index, Matrix4x4 matrix)
        {
            var shape = GetShape(index);
            OverlapDetectionGeometry.Basis(matrix, shape.Kind == OverlapShapeKind3D.Box ? shape.Orientation : Quaternion.identity,
                out _, out var y, out var z, out var scale);
            if (shape.Kind == OverlapShapeKind3D.Box)
            {
                return OverlapShape3D.Box(matrix.MultiplyPoint3x4(shape.Center), Vector3.Scale(shape.Size, scale),
                    Quaternion.LookRotation(z, y));
            }
            if (!OverlapDetectionGeometry.EqualScale(scale.x, scale.y) || !OverlapDetectionGeometry.EqualScale(scale.x, scale.z))
            {
                throw new InvalidOperationException("Spheres and capsules require uniform XYZ scale, not an ellipsoidal bound.");
            }
            return shape.Kind == OverlapShapeKind3D.Sphere ?
                OverlapShape3D.Sphere(matrix.MultiplyPoint3x4(shape.Center), shape.Radius * scale.x) :
                OverlapShape3D.Capsule(matrix.MultiplyPoint3x4(shape.Point1), matrix.MultiplyPoint3x4(shape.Point2), shape.Radius * scale.x);
        }
    }
}

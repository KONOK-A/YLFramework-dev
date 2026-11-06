using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Overlap
{
    /// <summary>
    /// Main-thread, caller-owned private physics world. Dispose queues destruction and asynchronous scene unload.
    /// Targets are borrowed only while copying primitive geometry; no target object or mesh is moved or retained.
    /// </summary>
    public sealed class OverlapQueryWorkspace3D : IDisposable
    {
        public float RequestedBoundaryError
        {
            get;
        }
        public int MaxSegments
        {
            get;
        }
        public int MaxParts
        {
            get;
        }
        public int MaxHierarchyDepth
        {
            get;
        }
        public Scene OwnedScene
        {
            get;
        }
        public AsyncOperation ReleaseOperation
        {
            get; private set;
        }
        public int CreatedObjectCount
        {
            get; private set;
        }
        public bool IsDisposed => _disposed;
        public int SegmentCount
        {
            get; private set;
        }
        public int RequiredPartCount
        {
            get; private set;
        }
        public int PartCount => _parts.Count;
        public int CookCount
        {
            get; private set;
        }
        public int GeometryBuildCount
        {
            get; private set;
        }
        public int TargetCopyCount
        {
            get; private set;
        }
        public float GeneratedBoundaryError
        {
            get; private set;
        }
        public OverlapQueryInvalidationReason GeometryReason
        {
            get; private set;
        }
        public string CookingError { get; private set; } = string.Empty;
        public OverlapShape3D BroadShape
        {
            get; private set;
        }

        private readonly Transform[] _ancestors;
        private readonly Transform[] _chain;
        private BoxCollider _box;
        private SphereCollider _sphere;
        private CapsuleCollider _capsule;
        private bool _disposed;
        private bool _inUse;
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<MeshCollider> _parts = new List<MeshCollider>();
        private BoxCollider _cookProbe;
        private OverlapShapeKind3D _cachedKind;
        private float _cachedRadius, _cachedInner, _cachedAngle, _cachedHeight, _expandedRadius;
        private bool _hasGeometry;

        public OverlapQueryWorkspace3D(float requestedBoundaryError = 0.01f, int maxSegments = 128,
            int maxParts = 128, int maxHierarchyDepth = 64)
        {
            ValidateConfiguration(requestedBoundaryError, maxSegments, maxParts, maxHierarchyDepth);
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException("RequiresPlayMode: the runtime workspace owns a local physics scene.");
            }
            RequestedBoundaryError = requestedBoundaryError;
            MaxSegments = maxSegments;
            MaxParts = maxParts;
            MaxHierarchyDepth = maxHierarchyDepth;
            _ancestors = new Transform[maxHierarchyDepth];
            _chain = new Transform[maxHierarchyDepth];
            OwnedScene = SceneManager.CreateScene("YL.Overlap3D." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            try
            {
                _box = CreateObject("BoxProxy").AddComponent<BoxCollider>();
                _sphere = CreateObject("SphereProxy").AddComponent<SphereCollider>();
                _capsule = CreateObject("CapsuleProxy").AddComponent<CapsuleCollider>();
                DisableProxies();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static void ValidateConfiguration(float requestedBoundaryError, int maxSegments, int maxParts,
            int maxHierarchyDepth)
        {
            if (!OverlapShape3D.IsFinite(requestedBoundaryError) || requestedBoundaryError <= 0)
                throw new ArgumentOutOfRangeException(nameof(requestedBoundaryError));
            if (maxSegments <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSegments));
            if (maxParts <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxParts));
            if (maxHierarchyDepth <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxHierarchyDepth));
        }

        private GameObject CreateObject(string name)
        {
            var result = new GameObject("YL.Overlap3D." + name);
            result.SetActive(false);
            SceneManager.MoveGameObjectToScene(result, OwnedScene);
            CreatedObjectCount++;
            return result;
        }

        internal void Begin()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OverlapQueryWorkspace3D));
            if (_inUse)
                throw new InvalidOperationException("A workspace cannot be shared by reentrant queries.");
            _inUse = true;
        }

        internal void End()
        {
            Array.Clear(_ancestors, 0, _ancestors.Length);
            DisableProxies();
            _inUse = false;
        }

        private void DisableProxies()
        {
            if (_box)
                _box.enabled = false;
            if (_sphere)
                _sphere.enabled = false;
            if (_capsule)
                _capsule.enabled = false;
        }

        internal OverlapQueryInvalidationReason CopyTarget(Collider source, out Collider proxy)
        {
            proxy = null;
            if (_disposed)
                throw new ObjectDisposedException(nameof(OverlapQueryWorkspace3D));
            if (!source || !OwnedScene.IsValid() || !OwnedScene.isLoaded)
                return OverlapQueryInvalidationReason.InvalidGeometry;
            if (!(source is BoxCollider) && !(source is SphereCollider) && !(source is CapsuleCollider))
                return OverlapQueryInvalidationReason.UnsupportedGeometry;
            if (source.attachedRigidbody && source.attachedRigidbody.interpolation != RigidbodyInterpolation.None)
                return OverlapQueryInvalidationReason.UnsupportedGeometry;
            DisableProxies();
            int depth = 0;
            try
            {
                for (var node = source.transform; node; node = node.parent)
                {
                    if (depth == _ancestors.Length)
                        return OverlapQueryInvalidationReason.GeometryBudgetExceeded;
                    _ancestors[depth++] = node;
                }
                for (int i = 0; i < depth; i++)
                {
                    var original = _ancestors[depth - i - 1];
                    Vector3 position = original.localPosition, scale = original.localScale;
                    Quaternion rotation = original.localRotation;
                    if (!Finite(position) || !Finite(scale) || scale.x == 0 || scale.y == 0 || scale.z == 0 ||
                        !OverlapShape3D.IsFinite(rotation.x) || !OverlapShape3D.IsFinite(rotation.y) ||
                        !OverlapShape3D.IsFinite(rotation.z) || !OverlapShape3D.IsFinite(rotation.w))
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                    if (!_chain[i])
                    {
                        _chain[i] = CreateObject("TargetTransform").transform;
                        _chain[i].SetParent(i == 0 ? null : _chain[i - 1], false);
                        _chain[i].gameObject.SetActive(true);
                    }
                    _chain[i].localPosition = position;
                    _chain[i].localRotation = rotation;
                    _chain[i].localScale = scale;
                }
                var matrix = source.transform.localToWorldMatrix;
                for (int i = 0; i < 16; i++)
                    if (!OverlapShape3D.IsFinite(matrix[i]))
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                if (!OverlapShape3D.IsFinite(matrix.determinant) || matrix.determinant == 0)
                    return OverlapQueryInvalidationReason.InvalidGeometry;

                if (source is BoxCollider box)
                {
                    if (!Finite(box.center) || !Positive(box.size.x) || !Positive(box.size.y) || !Positive(box.size.z))
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                    _box.center = box.center;
                    _box.size = box.size;
                    proxy = _box;
                }
                else if (source is SphereCollider sphere)
                {
                    if (!Finite(sphere.center) || !Positive(sphere.radius))
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                    _sphere.center = sphere.center;
                    _sphere.radius = sphere.radius;
                    proxy = _sphere;
                }
                else if (source is CapsuleCollider capsule)
                {
                    if (!Finite(capsule.center) || !Positive(capsule.radius) || !Positive(capsule.height) ||
                        capsule.direction < 0 || capsule.direction > 2)
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                    _capsule.center = capsule.center;
                    _capsule.radius = capsule.radius;
                    _capsule.height = capsule.height;
                    _capsule.direction = capsule.direction;
                    proxy = _capsule;
                }
                proxy.transform.SetParent(_chain[depth - 1], false);
                proxy.transform.localPosition = Vector3.zero;
                proxy.transform.localRotation = Quaternion.identity;
                proxy.transform.localScale = Vector3.one;
                if (!proxy.transform.localToWorldMatrix.Equals(matrix))
                {
                    proxy = null;
                    return OverlapQueryInvalidationReason.InvalidGeometry;
                }
                proxy.enabled = true;
                proxy.gameObject.SetActive(true);
                TargetCopyCount++;
                return OverlapQueryInvalidationReason.None;
            }
            finally
            {
                Array.Clear(_ancestors, 0, _ancestors.Length);
            }
        }

        private static bool Positive(float value) => OverlapShape3D.IsFinite(value) && value > 0;
        private static bool Finite(Vector3 value) => OverlapShape3D.IsFinite(value.x) &&
            OverlapShape3D.IsFinite(value.y) && OverlapShape3D.IsFinite(value.z);

        internal OverlapQueryInvalidationReason Prepare(in OverlapShape3D shape)
        {
            GeometryReason = OverlapQueryInvalidationReason.None;
            if (!OwnedScene.IsValid() || !OwnedScene.isLoaded)
                return GeometryReason = OverlapQueryInvalidationReason.InvalidScene;
            if (!_hasGeometry || _cachedKind != shape.Kind || _cachedRadius != shape.Radius ||
                _cachedInner != shape.InnerRadius || _cachedAngle != shape.AngleDegrees || _cachedHeight != shape.Height)
            {
                // A failed replacement must not pair old parts with a newly selected broad radius.
                ClearGeometry();
                SegmentCount = RequiredPartCount = 0;
                GeneratedBoundaryError = 0;
                CookingError = string.Empty;
                double total = shape.Kind == OverlapShapeKind3D.Cone ? Math.PI * 2 : shape.AngleDegrees * (Math.PI / 180);
                double maxHalf = Math.Min(Math.PI / 4, Math.Acos(shape.Radius / ((double)shape.Radius + RequestedBoundaryError)));
                if (shape.InnerRadius > RequestedBoundaryError)
                    maxHalf = Math.Min(maxHalf, Math.Acos(1 - (double)RequestedBoundaryError / shape.InnerRadius));
                double required = maxHalf > 0 ? Math.Ceiling(total / (2 * maxHalf)) : double.PositiveInfinity;
                if (required > MaxSegments)
                    return GeometryReason = OverlapQueryInvalidationReason.GeometryBudgetExceeded;
                int minimum = shape.Kind == OverlapShapeKind3D.Cone ? 4 : 1;
                bool selected = false;
                for (int segments = Math.Max(minimum, (int)required); segments <= MaxSegments; segments++)
                {
                    double half = total / (2 * segments);
                    float radius = RoundUp(shape.Radius / Math.Cos(half));
                    double error = Math.Max((double)radius - shape.Radius, shape.InnerRadius * (1 - Math.Cos(half)));
                    if (Positive(radius) && error <= RequestedBoundaryError)
                    {
                        SegmentCount = segments;
                        GeneratedBoundaryError = RoundUp(error);
                        _expandedRadius = radius;
                        selected = true;
                        break;
                    }
                    if (segments == MaxSegments)
                        break;
                }
                if (!selected)
                    return GeometryReason = OverlapQueryInvalidationReason.GeometryBudgetExceeded;
                RequiredPartCount = shape.Kind == OverlapShapeKind3D.Cone && SegmentCount <= 128 ? 1 : SegmentCount;
                if (RequiredPartCount > MaxParts)
                    return GeometryReason = OverlapQueryInvalidationReason.GeometryBudgetExceeded;
                if (!Positive(_expandedRadius * 2))
                    return GeometryReason = OverlapQueryInvalidationReason.InvalidGeometry;
                if (shape.Kind == OverlapShapeKind3D.Cone && !TryConeBroad(shape))
                    return GeometryReason = OverlapQueryInvalidationReason.InvalidGeometry;
                GeometryBuildCount++;
                if (!_cookProbe)
                {
                    _cookProbe = CreateObject("CookControl").AddComponent<BoxCollider>();
                    _cookProbe.gameObject.SetActive(true);
                }
                if (shape.Kind == OverlapShapeKind3D.Sector)
                {
                    double start = -total * 0.5;
                    for (int i = 0; i < SegmentCount; i++)
                    {
                        double a = start + total * i / SegmentCount;
                        double b = shape.AngleDegrees == 360 && i == SegmentCount - 1 ? start : start + total * (i + 1) / SegmentCount;
                        var corners = shape.InnerRadius == 0 ? new[] { Vector3.zero, Radial(a, _expandedRadius), Radial(b, _expandedRadius) } :
                            new[] { Radial(a, shape.InnerRadius), Radial(a, _expandedRadius), Radial(b, _expandedRadius), Radial(b, shape.InnerRadius) };
                        int n = corners.Length;
                        var vertices = new Vector3[n * 2];
                        var triangles = new List<int>();
                        for (int j = 0; j < n; j++)
                        {
                            vertices[j] = corners[j] - Vector3.up * (shape.Height * 0.5f);
                            vertices[j + n] = corners[j] + Vector3.up * (shape.Height * 0.5f);
                            int next = (j + 1) % n;
                            triangles.AddRange(new[] { j, next, next + n, j, next + n, j + n });
                        }
                        for (int j = 1; j < n - 1; j++)
                            triangles.AddRange(new[] { 0, j + 1, j, n, n + j, n + j + 1 });
                        if (!Cook(vertices, triangles.ToArray()))
                            return FailedCook();
                    }
                }
                else if (SegmentCount <= 128)
                {
                    var vertices = new Vector3[SegmentCount + 1];
                    var triangles = new List<int>();
                    for (int i = 0; i < SegmentCount; i++)
                    {
                        vertices[i + 1] = ConeBase(i, SegmentCount, _expandedRadius, shape.Height);
                        triangles.AddRange(new[] { 0, (i + 1) % SegmentCount + 1, i + 1 });
                    }
                    for (int i = 1; i < SegmentCount - 1; i++)
                        triangles.AddRange(new[] { 1, i + 1, i + 2 });
                    if (!Cook(vertices, triangles.ToArray()))
                        return FailedCook();
                }
                else
                {
                    for (int i = 0; i < SegmentCount; i++)
                    {
                        var vertices = new[] { Vector3.zero, Vector3.forward * shape.Height,
                            ConeBase(i, SegmentCount, _expandedRadius, shape.Height), ConeBase((i + 1) % SegmentCount, SegmentCount, _expandedRadius, shape.Height) };
                        if (!Cook(vertices, new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 }))
                            return FailedCook();
                    }
                }
                _cookProbe.enabled = false;
                _cachedKind = shape.Kind;
                _cachedRadius = shape.Radius;
                _cachedInner = shape.InnerRadius;
                _cachedAngle = shape.AngleDegrees;
                _cachedHeight = shape.Height;
                _hasGeometry = true;
            }
            if (shape.Kind == OverlapShapeKind3D.Cone)
            {
                if (!TryConeBroad(shape))
                    return GeometryReason = OverlapQueryInvalidationReason.InvalidGeometry;
                return OverlapQueryInvalidationReason.None;
            }
            Vector3 center = shape.Center;
            Vector3 size = new Vector3(_expandedRadius * 2, shape.Height, _expandedRadius * 2);
            if (!Finite(center) || !Finite(size))
                return GeometryReason = OverlapQueryInvalidationReason.InvalidGeometry;
            BroadShape = OverlapShape3D.Box(center, size, shape.Orientation);
            return OverlapQueryInvalidationReason.None;
        }

        private bool TryConeBroad(in OverlapShape3D shape)
        {
            // Keep the exact apex, avoiding a rounded world midpoint. The symmetric local
            // box [-R,R] x [-R,R] x [-H,H] contains every generated cone part.
            double x = shape.Orientation.x, y = shape.Orientation.y, z = shape.Orientation.z, w = shape.Orientation.w;
            double xx = 2 * x * x, yy = 2 * y * y, zz = 2 * z * z;
            double xy = 2 * x * y, xz = 2 * x * z, yz = 2 * y * z;
            double wx = 2 * w * x, wy = 2 * w * y, wz = 2 * w * z;
            double radius = _expandedRadius, height = shape.Height;
            double boundX = (Math.Abs(1 - yy - zz) + Math.Abs(xy - wz)) * radius + Math.Abs(xz + wy) * height;
            double boundY = (Math.Abs(xy + wz) + Math.Abs(1 - xx - zz)) * radius + Math.Abs(yz - wx) * height;
            double boundZ = (Math.Abs(xz - wy) + Math.Abs(yz + wx)) * radius + Math.Abs(1 - xx - yy) * height;
            var size = new Vector3(RoundUp(2 * boundX), RoundUp(2 * boundY), RoundUp(2 * boundZ));
            // Certify the actual float halves QueryRaw will pass, not an unrounded size.
            if (!Finite(size) || size.x * 0.5f < boundX || size.y * 0.5f < boundY || size.z * 0.5f < boundZ ||
                !Positive(size.x) || !Positive(size.y) || !Positive(size.z))
                return false;
            BroadShape = OverlapShape3D.Box(shape.Center, size, Quaternion.identity);
            return true;
        }

        private static float RoundUp(double value)
        {
            float result = (float)value;
            if (result < value && OverlapShape3D.IsFinite(result))
                result = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(result) + 1);
            return result;
        }

        private static Vector3 Radial(double angle, float radius) =>
            new Vector3((float)(Math.Sin(angle) * radius), 0, (float)(Math.Cos(angle) * radius));

        private static Vector3 ConeBase(int index, int count, float radius, float height)
        {
            double angle = Math.PI * 2 * index / count;
            return new Vector3((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius), height);
        }

        private bool Cook(Vector3[] vertices, int[] triangles)
        {
            string part = "Part " + _parts.Count + ": ";
            if (triangles.Length / 3 > 255)
            {
                CookingError = part + "TriangleBudgetExceeded";
                return false;
            }
            Vector3 witness = Vector3.zero;
            Vector3 minimum = vertices[0], maximum = vertices[0];
            foreach (var vertex in vertices)
            {
                if (!Finite(vertex))
                {
                    CookingError = part + "NonfiniteVertex";
                    return false;
                }
                witness += vertex / vertices.Length;
                minimum = Vector3.Min(minimum, vertex);
                maximum = Vector3.Max(maximum, vertex);
            }
            if (minimum.x == maximum.x || minimum.y == maximum.y || minimum.z == maximum.z)
            {
                CookingError = part + "FloatDegenerateBounds";
                return false;
            }
            var mesh = new Mesh { name = "YL.Overlap3D.ConvexPart", vertices = vertices, triangles = triangles };
            _meshes.Add(mesh);
            mesh.RecalculateBounds();
            var collider = CreateObject("ConvexPart").AddComponent<MeshCollider>();
            _parts.Add(collider);
            collider.convex = true;
            collider.gameObject.SetActive(true);
            // BakeMesh is void: verify the cooked part against an interior, same-world positive control.
            try
            {
                CookCount++;
                Physics.BakeMesh(mesh.GetInstanceID(), true, collider.cookingOptions);
                collider.sharedMesh = mesh;
                _cookProbe.center = witness;
                _cookProbe.size = Vector3.one * 0.0001f;
                _cookProbe.enabled = true;
                bool valid = mesh.isReadable && Physics.ComputePenetration(collider, Vector3.zero, Quaternion.identity,
                    _cookProbe, Vector3.zero, Quaternion.identity, out _, out float distance) && distance > 0;
                if (!valid)
                    CookingError = part + "NativeInteriorControlFailed (readable=" + mesh.isReadable + ")";
                return valid;
            }
            catch (UnityException error)
            {
                CookingError = part + "NativeCookException: " + error.Message;
                return false;
            }
            catch (ArgumentException error)
            {
                CookingError = part + "NativeCookArgument: " + error.Message;
                return false;
            }
        }

        private OverlapQueryInvalidationReason FailedCook()
        {
            if (_cookProbe)
                _cookProbe.enabled = false;
            ClearGeometry();
            return GeometryReason = OverlapQueryInvalidationReason.CookingFailed;
        }

        internal OverlapQueryInvalidationReason Intersects(Collider source, in OverlapShape3D shape, out bool hit)
        {
            hit = false;
            var reason = CopyTarget(source, out var proxy);
            if (reason != OverlapQueryInvalidationReason.None)
                return reason;
            for (int i = 0; i < _parts.Count; i++)
            {
                if (Physics.ComputePenetration(_parts[i], shape.Center, shape.Orientation, proxy,
                    proxy.transform.position, proxy.transform.rotation, out _, out _))
                {
                    hit = true;
                    break;
                }
            }
            return OverlapQueryInvalidationReason.None;
        }

        private void ClearGeometry()
        {
            _hasGeometry = false;
            foreach (var part in _parts)
            {
                if (!part)
                    continue;
                part.enabled = false;
                UnityEngine.Object.Destroy(part.gameObject);
            }
            foreach (var mesh in _meshes)
                if (mesh)
                    UnityEngine.Object.Destroy(mesh);
            _parts.Clear();
            _meshes.Clear();
        }

        public void Dispose()
        {
            if (_inUse)
                throw new InvalidOperationException("Cannot dispose a workspace during its query.");
            if (_disposed)
                return;
            _disposed = true;
            DisableProxies();
            ClearGeometry();
            if (_cookProbe)
            {
                _cookProbe.enabled = false;
                UnityEngine.Object.Destroy(_cookProbe.gameObject);
                _cookProbe = null;
            }
            Array.Clear(_ancestors, 0, _ancestors.Length);
            if (_box)
                UnityEngine.Object.Destroy(_box.gameObject);
            if (_sphere)
                UnityEngine.Object.Destroy(_sphere.gameObject);
            if (_capsule)
                UnityEngine.Object.Destroy(_capsule.gameObject);
            if (_chain[0])
                UnityEngine.Object.Destroy(_chain[0].gameObject);
            Array.Clear(_chain, 0, _chain.Length);
            _box = null;
            _sphere = null;
            _capsule = null;
            if (OwnedScene.IsValid() && OwnedScene.isLoaded)
                ReleaseOperation = SceneManager.UnloadSceneAsync(OwnedScene);
        }
    }
}

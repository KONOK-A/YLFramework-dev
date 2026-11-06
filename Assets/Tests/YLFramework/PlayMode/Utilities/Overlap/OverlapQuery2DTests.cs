using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Overlap;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class OverlapQuery2DTests
    {
        private Scene _scene;
        private PhysicsScene2D _physics;

        [SetUp]
        public void CreateScene()
        {
            _scene = SceneManager.CreateScene("YL.Query2D." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            _physics = _scene.GetPhysicsScene2D();
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
        }

        private GameObject NewObject(Vector3 position, Transform parent = null)
        {
            var obj = new GameObject("YL.Query2D.Target");
            SceneManager.MoveGameObjectToScene(obj, _scene);
            obj.transform.position = position;
            obj.transform.SetParent(parent, true);
            return obj;
        }

        private BoxCollider2D Target(Vector3 position, Transform parent = null)
        {
            var collider = NewObject(position, parent).AddComponent<BoxCollider2D>();
            collider.size = Vector2.one * 0.5f;
            return collider;
        }

        private static OverlapFilter2D Filter(bool triggers = false, Collider2D ignoredCollider = null,
            Transform ignoredRoot = null, bool groupByRigidbody = false, bool sortByDistance = false)
        {
            var contact = new ContactFilter2D { useTriggers = triggers };
            contact.SetLayerMask(1);
            return new OverlapFilter2D(contact, ignoredCollider, ignoredRoot, groupByRigidbody, sortByDistance);
        }

        private static OverlapShape2D Shape(int kind, Vector2 center)
        {
            switch (kind)
            {
                case 0:
                    return OverlapShape2D.Point(center);
                case 1:
                    return OverlapShape2D.Circle(center, 0.5f);
                case 2:
                    return OverlapShape2D.Box(center, new Vector2(1, 2), 37);
                default:
                    return OverlapShape2D.Capsule(center, new Vector2(1, 2), CapsuleDirection2D.Vertical, 37);
            }
        }

        private int NativeCast(in OverlapShape2D shape, Vector2 direction, float distance,
            ContactFilter2D contact, RaycastHit2D[] results)
        {
            switch (shape.Kind)
            {
                case OverlapShapeKind2D.Point:
                    return _physics.Raycast(shape.Center, direction, distance, contact, results);
                case OverlapShapeKind2D.Circle:
                    return _physics.CircleCast(shape.Center, shape.Radius, direction, distance, contact, results);
                case OverlapShapeKind2D.Box:
                    return _physics.BoxCast(shape.Center, shape.Size, shape.Angle, direction, distance, contact, results);
                default:
                    return _physics.CapsuleCast(shape.Center, shape.Size, shape.CapsuleDirection,
                        shape.Angle, direction, distance, contact, results);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeSectorProbeExportsGeneratedGeometryWithoutApplyingTransformTwice(bool attachedBody)
        {
            var root = NewObject(new Vector3(7, -3));
            root.transform.rotation = Quaternion.Euler(0, 0, 31);
            if (attachedBody)
            {
                root.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            }
            var child = NewObject(Vector3.zero, root.transform);
            child.transform.localPosition = new Vector3(2, 1);
            child.transform.localRotation = Quaternion.Euler(0, 0, 17);
            child.transform.localScale = new Vector3(2, 3, 1);
            var circle = child.AddComponent<CircleCollider2D>();
            circle.offset = new Vector2(0.3f, -0.2f);
            circle.radius = 0.4f;
            Physics2D.SyncTransforms();
            var group = new PhysicsShapeGroup2D(1, 32);
            Assert.That(circle.GetShapes(group, 0, 1), Is.EqualTo(1));
            var shape = group.GetShape(0);
            Assert.That(shape.shapeType, Is.EqualTo(PhysicsShapeType2D.Circle));
            Assert.That(shape.vertexCount, Is.EqualTo(1));
            Vector2 world = group.localToWorldMatrix.MultiplyPoint3x4(group.GetShapeVertex(0, 0));
            Assert.That(Vector2.Distance(world, child.transform.TransformPoint(circle.offset)), Is.LessThan(0.0001f));
            Assert.That(shape.radius, Is.EqualTo(1.2f).Within(0.0001f));
            if (!attachedBody)
            {
                Assert.That(group.localToWorldMatrix, Is.EqualTo(Matrix4x4.identity));
            }
            TestContext.WriteLine("Sector native circle body=" + attachedBody + " vertex=" + group.GetShapeVertex(0, 0) +
                " world=" + world + " radius=" + shape.radius + " matrix=" + group.localToWorldMatrix);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void NativeSectorProbeStreamsEveryShapeWithinTypedVertexUpperBounds(int kind)
        {
            var go = NewObject(new Vector3(2, 0));
            go.transform.localScale = new Vector3(2, 3, 1);
            Collider2D collider;
            int bound;
            if (kind == 0)
            {
                var capsule = go.AddComponent<CapsuleCollider2D>();
                capsule.size = new Vector2(1, 3);
                collider = capsule;
                bound = 2;
            }
            else if (kind == 1)
            {
                var edge = go.AddComponent<EdgeCollider2D>();
                edge.points = new[] { Vector2.left, Vector2.zero, Vector2.up, Vector2.right };
                edge.edgeRadius = 0.1f;
                edge.useAdjacentStartPoint = edge.useAdjacentEndPoint = true;
                edge.adjacentStartPoint = new Vector2(-2, 0);
                edge.adjacentEndPoint = new Vector2(2, 0);
                collider = edge;
                bound = edge.pointCount + 2;
            }
            else if (kind == 2)
            {
                var polygon = go.AddComponent<PolygonCollider2D>();
                polygon.pathCount = 2;
                polygon.SetPath(0, new[] { new Vector2(-2, -1), new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(-2, 1) });
                polygon.SetPath(1, new[] { new Vector2(1, -1), new Vector2(2, -1), new Vector2(2, 1), new Vector2(1, 1) });
                collider = polygon;
                bound = Physics2D.MaxPolygonShapeVertices;
            }
            else
            {
                go.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Static;
                var composite = go.AddComponent<CompositeCollider2D>();
                composite.generationType = CompositeCollider2D.GenerationType.Manual;
                composite.geometryType = kind == 3 ? CompositeCollider2D.GeometryType.Outlines : CompositeCollider2D.GeometryType.Polygons;
                composite.edgeRadius = 0.1f;
                for (int i = 0; i < 2; i++)
                {
                    var box = go.AddComponent<BoxCollider2D>();
                    box.offset = new Vector2(i * 3, 0);
                    box.usedByComposite = true;
                }
                composite.GenerateGeometry();
                collider = composite;
                bound = kind == 3 ? composite.pointCount + 2 : Physics2D.MaxPolygonShapeVertices;
                TestContext.WriteLine("Sector native composite pointCount=" + composite.pointCount + " paths=" + composite.pathCount);
            }
            Physics2D.SyncTransforms();
            Assert.That(collider.shapeCount, Is.GreaterThan(0));
            if (kind >= 2)
            {
                Assert.That(collider.shapeCount, Is.GreaterThan(1));
            }
            var group = new PhysicsShapeGroup2D(1, bound);
            for (int i = 0; i < collider.shapeCount; i++)
            {
                Assert.That(collider.GetShapes(group, i, 1), Is.EqualTo(1));
                Assert.That(group.shapeCount, Is.EqualTo(1), "Streaming must replace, not append previous geometry.");
                var shape = group.GetShape(0);
                Assert.That(shape.vertexCount, Is.InRange(1, bound));
                Assert.That(shape.radius, Is.GreaterThanOrEqualTo(0));
                TestContext.WriteLine("Sector native " + collider.GetType().Name + " shape=" + i + "/" + collider.shapeCount +
                    " type=" + shape.shapeType + " vertices=" + shape.vertexCount + " bound=" + bound + " radius=" + shape.radius);
            }
        }

        [Test]
        public void SectorAnnularHoleRequiresTheNewExplicitWorkspaceQuery()
        {
            var shape = OverlapShape2D.Sector(Vector2.zero, 3, 90, 1);
            var hole = Target(Vector3.zero);
            var hit = Target(new Vector3(2, 0));
            Physics2D.SyncTransforms();
            using var workspace = new OverlapQueryWorkspace2D();
            var results = new Collider2D[8];
            var result = OverlapQuery2D.Query(_physics, shape, Filter(), results, workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(results[0], Is.SameAs(hit));
            Assert.That(results, Has.None.EqualTo(hole));
        }

        private static OverlapShape2D Sector(float outer = 3, float angle = 90, float inner = 1, float rotation = 0) =>
            OverlapShape2D.Sector(Vector2.zero, outer, angle, inner, rotation);

        private static OverlapQueryWorkspace2D SectorWorkspace(int shapes = 256, int vertices = 4096, float tolerance = 0.00001f) =>
            new OverlapQueryWorkspace2D(shapes, vertices, tolerance);

        private OverlapQueryResult2D QuerySector(OverlapShape2D shape, Collider2D[] results, OverlapQueryWorkspace2D workspace,
            OverlapFilter2D? filter = null) => OverlapQuery2D.Query(_physics, shape, filter ?? Filter(), results, workspace);

        [TestCase(90, 1)]
        [TestCase(180, 3)]
        [TestCase(270, 3)]
        [TestCase(360, 4)]
        public void SectorAnglesAndFullRingRespectTheHoleRatherThanSmallSectorHalfPlanes(float angle, int expected)
        {
            var hole = Target(Vector3.zero);
            var right = Target(new Vector3(2, 0));
            Target(new Vector3(0, 2));
            Target(new Vector3(0, -2));
            var left = Target(new Vector3(-2, 0));
            var gap = Target(new Vector3(-1.6f, 1.6f));
            gap.enabled = false;
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace();
            var results = new Collider2D[16];
            var result = QuerySector(Sector(angle: angle), results, workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(expected));
            Assert.That(results, Has.None.EqualTo(hole));
            Assert.That(results, Has.Some.EqualTo(right));
            if (angle < 360)
            {
                Assert.That(results, Has.None.EqualTo(left));
            }
            gap.enabled = true;
            gap.transform.position = new Vector3(-1.6f, 1.4f);
            Physics2D.SyncTransforms();
            result = QuerySector(Sector(angle: 270), results, workspace);
            Assert.That(results, Has.Some.EqualTo(gap), "A >180 degree region includes the upper-left lobe.");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void SectorConsumesActualCircleCapsuleBoxPolygonEdgeAndCompositeGeometry(int kind)
        {
            var go = NewObject(Vector3.zero);
            Collider2D collider;
            if (kind == 0)
            {
                var circle = go.AddComponent<CircleCollider2D>();
                circle.radius = 0.1f;
                circle.offset = new Vector2(2, 0);
                collider = circle;
            }
            else if (kind == 1)
            {
                var capsule = go.AddComponent<CapsuleCollider2D>();
                capsule.size = new Vector2(0.2f, 8);
                capsule.offset = new Vector2(2, 0);
                collider = capsule;
            }
            else if (kind == 2)
            {
                var box = go.AddComponent<BoxCollider2D>();
                box.size = new Vector2(0.1f, 8);
                box.offset = new Vector2(2, 0);
                box.edgeRadius = 0.15f;
                collider = box;
            }
            else if (kind == 3)
            {
                var polygon = go.AddComponent<PolygonCollider2D>();
                polygon.pathCount = 2;
                polygon.SetPath(0, new[] { new Vector2(-2, -0.1f), new Vector2(-1.8f, -0.1f), new Vector2(-1.8f, 0.1f), new Vector2(-2, 0.1f) });
                polygon.SetPath(1, new[] { new Vector2(2, -4), new Vector2(2.1f, -4), new Vector2(2.1f, 4), new Vector2(2, 4) });
                collider = polygon;
            }
            else if (kind == 4)
            {
                var edge = go.AddComponent<EdgeCollider2D>();
                edge.points = new[] { new Vector2(-2, -2), new Vector2(-2, 4), new Vector2(2, 4), new Vector2(2, -4) };
                edge.edgeRadius = 0.1f;
                collider = edge;
            }
            else
            {
                go.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Static;
                var composite = go.AddComponent<CompositeCollider2D>();
                composite.geometryType = CompositeCollider2D.GeometryType.Polygons;
                var first = go.AddComponent<BoxCollider2D>();
                first.offset = new Vector2(-2, 0);
                first.size = new Vector2(0.2f, 0.2f);
                first.usedByComposite = true;
                var second = go.AddComponent<BoxCollider2D>();
                second.offset = new Vector2(2, 0);
                second.size = new Vector2(0.2f, 8);
                second.usedByComposite = true;
                collider = composite;
            }
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace();
            var results = new Collider2D[8];
            var result = QuerySector(Sector(), results, workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(results[0], Is.SameAs(collider));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectorFilledPolygonContainsRegionButOutlineDoesNotFillItsInterior(bool outline)
        {
            var go = NewObject(Vector3.zero);
            go.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Static;
            var composite = go.AddComponent<CompositeCollider2D>();
            composite.geometryType = outline ? CompositeCollider2D.GeometryType.Outlines : CompositeCollider2D.GeometryType.Polygons;
            var box = go.AddComponent<BoxCollider2D>();
            box.size = Vector2.one * 20;
            box.usedByComposite = true;
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace();
            var result = QuerySector(Sector(), new Collider2D[8], workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(outline ? 0 : 1));
        }

        [TestCase(0, 1)]
        [TestCase(0.0002f, 0)]
        [TestCase(-0.0002f, 1)]
        public void SectorAnalyticArcsIncludeTangencyWithoutPolygonizingTheCircle(float gap, int expected)
        {
            var circle = NewObject(new Vector3(3.25f + gap, 0)).AddComponent<CircleCollider2D>();
            circle.radius = 0.25f;
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace();
            var result = QuerySector(Sector(), new Collider2D[8], workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.EqualTo(expected));
            circle.transform.position = new Vector3(0.75f - gap, 0);
            Physics2D.SyncTransforms();
            result = QuerySector(Sector(), new Collider2D[8], workspace);
            Assert.That(result.Count, Is.EqualTo(expected), "Inner arc uses the same world-unit tolerance.");
        }

        [Test]
        public void SectorCrossesHoleAndArcWithNoEndpointInsideAndHandlesTheApexAndRadialBoundary()
        {
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            edge.points = new[] { new Vector2(-4, 0), new Vector2(4, 0) };
            var centerOutside = Target(new Vector3(3.2f, 0));
            var apex = NewObject(Vector3.zero).AddComponent<CircleCollider2D>();
            apex.radius = 0.05f;
            var radial = NewObject(new Vector3(1.45f, 1.45f)).AddComponent<CircleCollider2D>();
            radial.radius = 0.05f;
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace();
            var results = new Collider2D[8];
            var result = QuerySector(Sector(), results, workspace);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(results, Has.Some.EqualTo(edge));
            Assert.That(results, Has.Some.EqualTo(centerOutside));
            Assert.That(results, Has.Some.EqualTo(radial));
            Assert.That(results, Has.None.EqualTo(apex));
            result = QuerySector(Sector(inner: 0), results, workspace);
            Assert.That(result.Count, Is.EqualTo(4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectorReReadsNonuniformTargetScaleOffsetBodyPoseAndShapeOnEveryQuery(bool attachedBody)
        {
            var go = NewObject(new Vector3(2, 0));
            if (attachedBody)
            {
                go.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            }
            go.transform.localScale = new Vector3(2, 3, 1);
            var circle = go.AddComponent<CircleCollider2D>();
            circle.radius = 0.05f;
            circle.offset = new Vector2(0.1f, 0);
            using var workspace = SectorWorkspace();
            var results = new Collider2D[8];
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.EqualTo(1));
            go.transform.position = new Vector3(-2, 0);
            go.transform.rotation = Quaternion.Euler(0, 0, 90);
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.Zero);
            circle.offset = new Vector2(0, -2);
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.Zero, "The fresh shape is now beyond the outer arc.");
            circle.offset = new Vector2(0, -1.3f);
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.EqualTo(1));
        }

        [Test]
        public void SectorRawSaturationRemainsPartialWhenHoleRootOrBodyFilteringReducesCount()
        {
            var root = NewObject(Vector3.zero);
            root.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            Target(Vector3.zero, root.transform);
            Target(new Vector3(0.1f, 0), root.transform);
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace();
            var results = new Collider2D[2];
            foreach (var filter in new[] { Filter(), Filter(ignoredRoot: root.transform), Filter(groupByRigidbody: true) })
            {
                var result = QuerySector(Sector(), results, workspace, filter);
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.EqualTo(2));
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
                Assert.That(results, Is.EqualTo(new Collider2D[2]));
            }
            root.transform.position = new Vector3(2, 0);
            Physics2D.SyncTransforms();
            var grouped = QuerySector(Sector(), results, workspace, Filter(groupByRigidbody: true, sortByDistance: true));
            Assert.That(grouped.Count, Is.EqualTo(1));
            Assert.That(grouped.RawCount, Is.EqualTo(2));
            Assert.That(grouped.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
        }

        [Test]
        public void SectorBudgetsInvalidateBeforeUnboundedEdgeExtractionAndIgnoredGeometryCanBeSkipped()
        {
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            var points = new Vector2[40];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new Vector2(2, i * 0.1f - 2);
            }
            edge.points = points;
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace(vertices: 8);
            var results = new Collider2D[8];
            var result = QuerySector(Sector(), results, workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(result.InvalidationReason.ToString(), Is.EqualTo("GeometryBudgetExceeded"));
            Assert.That(results, Is.EqualTo(new Collider2D[8]));
            result = QuerySector(Sector(), results, workspace, Filter(ignoredCollider: edge));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(result.Count, Is.Zero);
        }

        [Test]
        public void SectorUnionCommitsOnlyCompleteObservationsAndReentrantStopReleasesOwnedGeometry()
        {
            var target = Target(new Vector3(2, 0));
            var owner = NewObject(Vector3.zero).AddComponent<OverlapDetector2D>();
            owner.Composite = new OverlapComposite2D(Sector(), Sector());
            owner.Filter = Filter();
            owner.ConfigureCapacity(2, 4, true);
            int enters = 0;
            int exits = 0;
            owner.Entered += _ => enters++;
            owner.Exited += (_, __) => exits++;
            Physics2D.SyncTransforms();
            Assert.That(owner.Sample(), Is.True);
            Assert.That(owner.LastComplete.Count, Is.EqualTo(1));
            Assert.That(enters, Is.EqualTo(1));
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            var points = new Vector2[4100];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new Vector2(2, i * 0.001f - 2);
            }
            edge.points = points;
            target.enabled = false;
            Physics2D.SyncTransforms();
            Assert.That(owner.Sample(), Is.False);
            Assert.That(owner.LastQuery.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(owner.LastComplete.Count, Is.EqualTo(1));
            Assert.That(exits, Is.Zero, "Invalid geometry cannot emit a false separation.");
            edge.enabled = false;
            target.enabled = true;
            owner.Stayed += _ => owner.Stop();
            Physics2D.SyncTransforms();
            Assert.That(owner.Sample(), Is.False);
            Assert.That(exits, Is.EqualTo(1));
            Assert.That(owner.LastComplete, Is.Empty);
            owner.StartDetection();
            Assert.That(owner.Sample(), Is.True);
            Assert.That(enters, Is.EqualTo(2));
        }

        [Test]
        public void SectorRejectsUnsupportedCustomGeometryAndShapeCountBudgetWithoutACompleteMiss()
        {
            var custom = NewObject(Vector3.zero).AddComponent<CustomCollider2D>();
            var native = new PhysicsShapeGroup2D();
            native.AddCircle(new Vector2(2, 0), 0.1f);
            custom.SetCustomShapes(native);
            Physics2D.SyncTransforms();
            using var workspace = SectorWorkspace(shapes: 1);
            var results = new Collider2D[8];
            var result = QuerySector(Sector(), results, workspace);
            Assert.That(result.RawCount, Is.EqualTo(1), "The unknown collider must be a real native candidate.");
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.UnsupportedGeometry));
            Assert.That(results, Is.EqualTo(new Collider2D[8]));
            result = QuerySector(Sector(), results, workspace, Filter(ignoredCollider: custom));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            custom.enabled = false;
            var polygon = NewObject(Vector3.zero).AddComponent<PolygonCollider2D>();
            polygon.pathCount = 2;
            polygon.SetPath(0, new[] { new Vector2(-2, -1), new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(-2, 1) });
            polygon.SetPath(1, new[] { new Vector2(1, -1), new Vector2(2, -1), new Vector2(2, 1), new Vector2(1, 1) });
            Physics2D.SyncTransforms();
            Assert.That(polygon.shapeCount, Is.GreaterThan(1));
            result = QuerySector(Sector(), results, workspace);
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.GeometryBudgetExceeded));
            Assert.That(results, Is.EqualTo(new Collider2D[8]));
        }

        [Test]
        public void SectorWorkspaceRejectsDisposedAndReentrantUseBeforeTouchingCallerOutput()
        {
            var target = Target(new Vector3(2, 0));
            var results = new[] { (Collider2D)target, target };
            var shape = Sector();
            var filter = Filter();
            var first = SectorWorkspace();
            using var second = SectorWorkspace();
            first.Dispose();
            first.Dispose();
            Assert.Throws<ObjectDisposedException>(() => OverlapQuery2D.Query(_physics, shape, filter, results, first));
            Assert.That(results, Is.EqualTo(new[] { target, target }));
            var begin = typeof(OverlapQueryWorkspace2D).GetMethod("Begin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var end = typeof(OverlapQueryWorkspace2D).GetMethod("End", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            begin.Invoke(second, null);
            try
            {
                Assert.Throws<InvalidOperationException>(() => OverlapQuery2D.Query(_physics, shape, filter, results, second));
                Assert.Throws<InvalidOperationException>(() => second.Dispose());
                Assert.That(results, Is.EqualTo(new[] { target, target }));
            }
            finally
            {
                end.Invoke(second, null);
            }
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(shape, results, second).Count, Is.EqualTo(1));
        }

        [Test]
        public void SectorInvalidArgumentsMissingWorkspaceAndUnsupportedCastHaveExplicitNonCorruptingContracts()
        {
            var target = Target(new Vector3(2, 0));
            var results = new[] { (Collider2D)target, target };
            Assert.Throws<ArgumentOutOfRangeException>(() => Sector(outer: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Sector(inner: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Sector(inner: 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => Sector(angle: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Sector(angle: 361));
            Assert.Throws<ArgumentOutOfRangeException>(() => Sector(rotation: float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => SectorWorkspace(shapes: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SectorWorkspace(vertices: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SectorWorkspace(tolerance: float.NaN));
            var invalidContact = new ContactFilter2D { useDepth = true, minDepth = 2, maxDepth = 1 };
            using var workspace = SectorWorkspace();
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Query(_physics, Sector(), new OverlapFilter2D(invalidContact), results, workspace));
            Assert.Throws<ArgumentNullException>(() => OverlapQuery2D.Query(_physics, Sector(), Filter(), null, workspace));
            Assert.Throws<ArgumentException>(() => OverlapQuery2D.Query(_physics, Sector(), Filter(), Array.Empty<Collider2D>(), workspace));
            Assert.That(results, Is.EqualTo(new[] { target, target }));
            var query = OverlapQuery2D.Query(_physics, Sector(), Filter(), results);
            Assert.That(query.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.WorkspaceRequired));
            Assert.That(query.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(results, Is.EqualTo(new Collider2D[2]));
            var hits = new RaycastHit2D[2];
            query = OverlapQuery2D.Cast(_physics, Sector(), Vector2.right, 3, Filter(), hits);
            Assert.That(query.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(query.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.UnsupportedCast));
            Assert.That(hits[0].collider, Is.Null);
            Assert.That((int)OverlapShapeKind2D.Point, Is.EqualTo(0));
            Assert.That((int)OverlapShapeKind2D.Capsule, Is.EqualTo(3));
            Assert.That((int)OverlapShapeKind2D.Sector, Is.EqualTo(4));
        }

        [Test]
        public void SectorGeometryToleranceIsWorldUnitsAndTheNativeBroadPhaseEnclosesIt()
        {
            var circle = NewObject(new Vector3(3.27f, 0)).AddComponent<CircleCollider2D>();
            circle.radius = 0.25f;
            Physics2D.SyncTransforms();
            using var fine = SectorWorkspace(tolerance: 0.001f);
            using var coarse = SectorWorkspace(tolerance: 0.03f);
            var results = new Collider2D[8];
            Assert.That(QuerySector(Sector(), results, fine).Count, Is.Zero);
            Assert.That(QuerySector(Sector(), results, coarse).Count, Is.EqualTo(1));
            circle.transform.position = new Vector3(0.73f, 0);
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, fine).Count, Is.Zero);
            Assert.That(QuerySector(Sector(), results, coarse).Count, Is.EqualTo(1));
        }

        [Test]
        public void SectorEdgeInteriorTangencyAndCompositeOutlineAreNotReducedToEndpointsOrFilledHulls()
        {
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            edge.points = new[] { new Vector2(3.1f, -4), new Vector2(3.1f, 4) };
            edge.edgeRadius = 0.1f;
            using var workspace = SectorWorkspace();
            var results = new Collider2D[8];
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.EqualTo(1));
            edge.points = new[] { new Vector2(-0.5f, 0), new Vector2(0.5f, 0) };
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(angle: 360), results, workspace).Count, Is.Zero);
            edge.enabled = false;
            var go = NewObject(new Vector3(2, 0));
            go.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Static;
            var composite = go.AddComponent<CompositeCollider2D>();
            composite.geometryType = CompositeCollider2D.GeometryType.Outlines;
            var box = go.AddComponent<BoxCollider2D>();
            box.size = new Vector2(0.2f, 4);
            box.usedByComposite = true;
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.EqualTo(1));
            Assert.That(results[0], Is.SameAs(composite));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectorDoesNotCreateHiddenObjectsOrChangeGlobalPhysicsEvenForTriggerRequests(bool globalTriggers)
        {
            bool previous = Physics2D.queriesHitTriggers;
            try
            {
                Physics2D.queriesHitTriggers = globalTriggers;
                var trigger = Target(new Vector3(2, 0));
                trigger.isTrigger = true;
                var ignored = NewObject(Vector3.zero);
                Target(new Vector3(2, 0), ignored.transform);
                Target(new Vector3(2, 0)).gameObject.layer = 2;
                Target(new Vector3(2, 0, 10));
                Physics2D.SyncTransforms();
                int roots = _scene.rootCount;
                bool starts = Physics2D.queriesStartInColliders;
                bool sync = Physics2D.autoSyncTransforms;
                var mode = Physics2D.simulationMode;
                bool collision = Physics2D.GetIgnoreLayerCollision(0, 2);
                using var workspace = SectorWorkspace();
                var contact = Filter(triggers: true).ContactFilter;
                contact.SetDepth(-1, 1);
                var result = QuerySector(Sector(), new Collider2D[8], workspace,
                    new OverlapFilter2D(contact, ignoredRoot: ignored.transform));
                Assert.That(result.Status, Is.EqualTo(globalTriggers ? OverlapQueryStatus.Complete : OverlapQueryStatus.Invalidated));
                Assert.That(result.Count, Is.EqualTo(globalTriggers ? 1 : 0));
                Assert.That(Physics2D.queriesHitTriggers, Is.EqualTo(globalTriggers));
                Assert.That(Physics2D.queriesStartInColliders, Is.EqualTo(starts));
                Assert.That(Physics2D.autoSyncTransforms, Is.EqualTo(sync));
                Assert.That(Physics2D.simulationMode, Is.EqualTo(mode));
                Assert.That(Physics2D.GetIgnoreLayerCollision(0, 2), Is.EqualTo(collision));
                Assert.That(_scene.rootCount, Is.EqualTo(roots));
            }
            finally
            {
                Physics2D.queriesHitTriggers = previous;
            }
        }

        [Test]
        public void SectorOwnersSharePureConfigurationButNeverGeometryStorageAndDisableDisposesOnlyTheOwner()
        {
            Target(new Vector3(0, 2));
            var first = NewObject(Vector3.zero).AddComponent<OverlapDetector2D>();
            var second = NewObject(Vector3.zero).AddComponent<OverlapDetector2D>();
            first.Composite = second.Composite = new OverlapComposite2D(Sector(rotation: 90), Sector(rotation: 90));
            first.Filter = second.Filter = Filter(groupByRigidbody: true);
            Physics2D.SyncTransforms();
            Assert.That(first.Sample(), Is.True);
            Assert.That(second.Sample(), Is.True);
            var field = typeof(OverlapDetector2D).GetField("_workspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var firstWorkspace = (OverlapQueryWorkspace2D)field.GetValue(first);
            var secondWorkspace = (OverlapQueryWorkspace2D)field.GetValue(second);
            Assert.That(firstWorkspace, Is.Not.SameAs(secondWorkspace));
            first.enabled = false;
            Assert.That(field.GetValue(first), Is.Null);
            Assert.Throws<ObjectDisposedException>(() => OverlapQuery2D.Query(_physics, Sector(), Filter(), new Collider2D[8], firstWorkspace));
            Assert.That(second.Sample(), Is.True);
            Assert.That(field.GetValue(second), Is.SameAs(secondWorkspace));
            Assert.That(second.LastComplete.Count, Is.EqualTo(1));
            second.Stop();
            Assert.That(field.GetValue(second), Is.Null);
            second.StartDetection();
            Assert.That(second.Sample(), Is.True);
            Assert.That(field.GetValue(second), Is.Not.SameAs(secondWorkspace));
            var restartedWorkspace = (OverlapQueryWorkspace2D)field.GetValue(second);
            Object.DestroyImmediate(second.gameObject);
            Assert.Throws<ObjectDisposedException>(() => OverlapQuery2D.Query(_physics, Sector(), Filter(), new Collider2D[8], restartedWorkspace));
        }

        [Test]
        public void SectorFullDiskAgreesWithIndependentNativeCircleForEverySupportedPrimitive()
        {
            Target(new Vector3(2, 0)).edgeRadius = 0.1f;
            NewObject(new Vector3(-2, 0)).AddComponent<CircleCollider2D>().radius = 0.3f;
            NewObject(new Vector3(0, 2)).AddComponent<CapsuleCollider2D>();
            NewObject(new Vector3(0, -2)).AddComponent<PolygonCollider2D>();
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            edge.points = new[] { new Vector2(-4, -4), new Vector2(4, 4) };
            Physics2D.SyncTransforms();
            var native = new Collider2D[16];
            int count = _physics.OverlapCircle(Vector2.zero, 3, Filter().ContactFilter, native);
            using var workspace = SectorWorkspace();
            var results = new Collider2D[16];
            var query = QuerySector(Sector(angle: 360, inner: 0), results, workspace);
            Assert.That(query.Count, Is.EqualTo(count));
            Assert.That(results, Is.EquivalentTo(native));
        }

        [Test]
        public void SectorShapeEditsAreResampledAndDefaultSceneOverloadUsesTheRequestedWorldCenter()
        {
            var circle = NewObject(Vector3.zero).AddComponent<CircleCollider2D>();
            circle.radius = 0.1f;
            using var workspace = SectorWorkspace();
            var results = new Collider2D[8];
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.Zero);
            circle.radius = 2;
            Physics2D.SyncTransforms();
            Assert.That(QuerySector(Sector(), results, workspace).Count, Is.EqualTo(1));
            var native = new GameObject("Owned.DefaultSector.Target").AddComponent<CircleCollider2D>();
            try
            {
                native.transform.position = new Vector3(21002, 21000);
                native.radius = 0.1f;
                Physics2D.SyncTransforms();
                var shape = OverlapShape2D.Sector(new Vector2(21000, 21000), 3, 90, 1);
                var query = OverlapQuery2D.Query(shape, Filter(), results, workspace);
                Assert.That(query.Status, Is.EqualTo(OverlapQueryStatus.Complete));
                Assert.That(query.Count, Is.EqualTo(1));
                Assert.That(results[0], Is.SameAs(native));
            }
            finally
            {
                Object.DestroyImmediate(native.gameObject);
            }
        }

        [Test]
        public void SectorProgrammaticToleranceReconfigurationReleasesWorkspaceAndReportsReconfiguredExit()
        {
            var circle = NewObject(new Vector3(3.27f, 0)).AddComponent<CircleCollider2D>();
            circle.radius = 0.25f;
            var owner = NewObject(Vector3.zero).AddComponent<OverlapDetector2D>();
            owner.Composite = new OverlapComposite2D(Sector());
            owner.Filter = Filter();
            owner.ConfigureGeometryWorkspace(geometryTolerance: 0.03f);
            Physics2D.SyncTransforms();
            Assert.That(owner.Sample(), Is.True);
            Assert.That(owner.LastComplete.Count, Is.EqualTo(1));
            OverlapExitReason? reason = null;
            owner.Exited += (_, actual) => reason = actual;
            owner.ConfigureGeometryWorkspace(geometryTolerance: 0.001f);
            Assert.That(owner.Sample(), Is.True);
            Assert.That(owner.LastComplete, Is.Empty);
            Assert.That(reason, Is.EqualTo(OverlapExitReason.Reconfigured));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectorFiniteLongNativeEdgeUsesActualArcRootsRatherThanRoundedMidpoint(bool horizontal)
        {
            const float Extent = 72057594037927936f;
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            edge.points = horizontal ? new[] { new Vector2(-Extent, 0), new Vector2(Extent, 0) } :
                new[] { new Vector2(0, -Extent), new Vector2(0, Extent) };
            Physics2D.SyncTransforms();
            var group = new PhysicsShapeGroup2D(1, 2);
            Assert.That(edge.GetShapes(group, 0, 1), Is.EqualTo(1));
            Assert.That(group.GetShape(0).shapeType, Is.EqualTo(PhysicsShapeType2D.Edges));
            Assert.That(group.GetShape(0).radius, Is.Zero);
            Assert.That(group.GetShapeVertex(0, 0), Is.EqualTo(edge.points[0]));
            Assert.That(group.GetShapeVertex(0, 1), Is.EqualTo(edge.points[1]));
            var results = new Collider2D[8];
            int native = _physics.OverlapCircle(Vector2.zero, 3, Filter().ContactFilter, results);
            TestContext.WriteLine("FIX1 I1 native horizontal=" + horizontal + "; extent=" + Extent.ToString("R") +
                "; vertices=" + group.GetShapeVertex(0, 0).ToString("R") + "," + group.GetShapeVertex(0, 1).ToString("R") +
                "; nativeCandidates=" + native);
            Assert.That(native, Is.EqualTo(1), "Native admission is a measured prerequisite, not a simulated candidate.");
            using var workspace = SectorWorkspace();
            var query = QuerySector(Sector(), results, workspace);
            TestContext.WriteLine("FIX1 I1 wrapper=" + query.Status + "/" + query.Count + "/" + query.RawCount);
            Assert.That(query.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(query.RawCount, Is.EqualTo(1));
            Assert.That(query.Count, Is.EqualTo(horizontal ? 1 : 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectorCommonHugeNativeBodyTranslationPreservesLocalOffsetAndHole(bool hugeTranslation)
        {
            float translation = hugeTranslation ? 72057594037927936f : 0;
            var body = NewObject(new Vector3(translation, 0)).AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Kinematic;
            var circle = body.gameObject.AddComponent<CircleCollider2D>();
            circle.offset = new Vector2(2, 0);
            circle.radius = 0.1f;
            Physics2D.SyncTransforms();
            var group = new PhysicsShapeGroup2D(1, 1);
            Assert.That(circle.GetShapes(group, 0, 1), Is.EqualTo(1));
            Assert.That(circle.attachedRigidbody, Is.SameAs(body));
            Assert.That(group.GetShapeVertex(0, 0), Is.EqualTo(new Vector2(2, 0)));
            Assert.That(group.GetShape(0).radius, Is.EqualTo(0.1f).Within(0.0000001f));
            Assert.That(group.localToWorldMatrix.m03, Is.EqualTo(translation));
            Assert.That(group.localToWorldMatrix.m00, Is.EqualTo(1));
            var center = new Vector2(translation, 0);
            var results = new Collider2D[8];
            int native = _physics.OverlapCircle(center, 3, Filter().ContactFilter, results);
            double oldRelative = (double)group.localToWorldMatrix.m00 * group.GetShapeVertex(0, 0).x +
                group.localToWorldMatrix.m03 - center.x;
            TestContext.WriteLine("FIX1 I2 native T=" + translation.ToString("R") + "; attachedLocal=" +
                group.GetShapeVertex(0, 0).ToString("R") + "; matrixT=" + group.localToWorldMatrix.m03.ToString("R") +
                "; nativeCandidates=" + native + "; oldAddThenSubtract=" + oldRelative.ToString("R"));
            Assert.That(native, Is.EqualTo(1), "The exported body-local offset must enter the real wrapper broad phase.");
            using var workspace = SectorWorkspace();
            var shape = OverlapShape2D.Sector(center, 3, 90, 1);
            var query = OverlapQuery2D.Query(_physics, shape, Filter(), results, workspace);
            TestContext.WriteLine("FIX1 I2 wrapper=" + query.Status + "/" + query.Count + "/" + query.RawCount);
            Assert.That(query.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(query.RawCount, Is.EqualTo(1));
            Assert.That(query.Count, Is.EqualTo(1));
            circle.offset = Vector2.zero;
            Physics2D.SyncTransforms();
            Assert.That(circle.GetShapes(group, 0, 1), Is.EqualTo(1));
            Assert.That(group.GetShapeVertex(0, 0), Is.EqualTo(Vector2.zero));
            query = OverlapQuery2D.Query(_physics, shape, Filter(), results, workspace);
            Assert.That(query.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(query.RawCount, Is.EqualTo(1));
            Assert.That(query.Count, Is.Zero, "A true body-local hole point must remain excluded at both translations.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectorPrivateFiniteLongSegmentKernelHasTheHandDerivedDistance(bool horizontal)
        {
            const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            var owner = typeof(OverlapQueryWorkspace2D);
            var pointType = owner.GetNestedType("Point", System.Reflection.BindingFlags.NonPublic);
            var regionType = owner.GetNestedType("Region", System.Reflection.BindingFlags.NonPublic);
            var pointConstructor = pointType.GetConstructor(Flags, null, new[] { typeof(double), typeof(double) }, null);
            var regionConstructor = regionType.GetConstructor(Flags, null, new[] { typeof(OverlapShape2D).MakeByRefType(), typeof(double) }, null);
            double extent = Math.Pow(2, 56);
            var a = pointConstructor.Invoke(horizontal ? new object[] { -extent, 0d } : new object[] { 0d, -extent });
            var b = pointConstructor.Invoke(horizontal ? new object[] { extent, 0d } : new object[] { 0d, extent });
            var region = regionConstructor.Invoke(new object[] { Sector(), 0.00001d });
            double actual = (double)regionType.GetMethod("SegmentDistanceSquared", Flags).Invoke(region, new[] { a, b });
            double first = 0.5d - 3d / (2d * extent);
            double second = 0.5d + 3d / (2d * extent);
            TestContext.WriteLine("FIX1 I1 pure kernel horizontal=" + horizontal + "; first=" + first.ToString("R") +
                "; second=" + second.ToString("R") + "; oldReconstructed=" + (-extent + 2 * extent * first).ToString("R") +
                "; actualDistanceSquared=" + actual.ToString("R") + "; analyticExpected=" + (horizontal ? "0" : "0.5"));
            Assert.That(actual, Is.EqualTo(horizontal ? 0 : 0.5d).Within(0.000000000001d));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectorInvalidUnionRetainsAllObservedRawCountsAndTheCommittedBaseline(bool budgetFailure)
        {
            var baseline = Target(new Vector3(2, 0));
            var owner = NewObject(Vector3.zero).AddComponent<OverlapDetector2D>();
            var circle = OverlapShape2D.Circle(Vector2.zero, 3);
            owner.Composite = new OverlapComposite2D(circle);
            owner.Filter = Filter();
            owner.ConfigureCapacity(8, 8);
            int enters = 0;
            int stays = 0;
            int exits = 0;
            owner.Entered += _ => enters++;
            owner.Stayed += _ => stays++;
            owner.Exited += (_, __) => exits++;
            Physics2D.SyncTransforms();
            Assert.That(owner.Sample(), Is.True);
            Assert.That(owner.LastComplete.Count, Is.EqualTo(1));
            baseline.enabled = false;
            if (budgetFailure)
            {
                var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
                var points = new Vector2[9];
                for (int i = 0; i < points.Length; i++)
                {
                    points[i] = new Vector2(2, i * 0.1f - 0.4f);
                }
                edge.points = points;
                owner.ConfigureGeometryWorkspace(maxVerticesPerShape: 8);
            }
            else
            {
                var custom = NewObject(Vector3.zero).AddComponent<CustomCollider2D>();
                var native = new PhysicsShapeGroup2D();
                native.AddCircle(new Vector2(2, 0), 0.1f);
                custom.SetCustomShapes(native);
            }
            owner.Composite = new OverlapComposite2D(circle, Sector(), circle);
            Physics2D.SyncTransforms();
            Assert.That(owner.Sample(), Is.False);
            TestContext.WriteLine("FIX1 M1 budget=" + budgetFailure + "; actual=" + owner.LastQuery.Status + "/" +
                owner.LastQuery.InvalidationReason + "; Count/Raw=" + owner.LastQuery.Count + "/" + owner.LastQuery.RawCount +
                "; retained=" + owner.LastComplete.Count + "; callbacks=" + enters + "/" + stays + "/" + exits);
            Assert.That(owner.LastQuery.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(owner.LastQuery.InvalidationReason, Is.EqualTo(budgetFailure ?
                OverlapQueryInvalidationReason.GeometryBudgetExceeded : OverlapQueryInvalidationReason.UnsupportedGeometry));
            Assert.That(owner.LastQuery.Count, Is.Zero);
            Assert.That(owner.LastQuery.RawCount, Is.EqualTo(2), "Sum only the first two executed children, never the unexecuted third.");
            Assert.That(owner.LastComplete.Count, Is.EqualTo(1));
            Assert.That(owner.LastComplete[0].Collider, Is.SameAs(baseline));
            Assert.That(enters, Is.EqualTo(1));
            Assert.That(stays, Is.Zero);
            Assert.That(exits, Is.Zero);
        }

        [Test]
        public void SectorUncertainNativeGeometryInvalidatesUnionWithObservedRawSumAndNoEvents()
        {
            var baseline = Target(new Vector3(2, 0));
            var owner = NewObject(Vector3.zero).AddComponent<OverlapDetector2D>();
            var circle = OverlapShape2D.Circle(Vector2.zero, 3);
            owner.Composite = new OverlapComposite2D(circle);
            owner.Filter = Filter();
            owner.ConfigureCapacity(8, 8);
            Physics2D.SyncTransforms();
            Assert.That(owner.Sample(), Is.True);
            baseline.enabled = false;
            const float Extent = 72057594037927936f;
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            edge.points = new[] { new Vector2(-Extent, -Extent), new Vector2(Extent, Extent) };
            Physics2D.SyncTransforms();
            var raw = new Collider2D[8];
            Assert.That(_physics.OverlapCircle(Vector2.zero, 3, Filter().ContactFilter, raw), Is.EqualTo(1));
            int callbacks = 0;
            owner.Entered += _ => callbacks++;
            owner.Stayed += _ => callbacks++;
            owner.Exited += (_, __) => callbacks++;
            owner.Composite = new OverlapComposite2D(circle, Sector(), circle);
            bool committed = owner.Sample();
            TestContext.WriteLine("FIX1 uncertain native diagonal: candidate=1; committed=" + committed + "; actual=" +
                owner.LastQuery.Status + "/" + owner.LastQuery.InvalidationReason + "; Count/Raw=" +
                owner.LastQuery.Count + "/" + owner.LastQuery.RawCount + "; retained=" + owner.LastComplete.Count +
                "; callbacks=" + callbacks);
            Assert.That(committed, Is.False, "Uncertified severe determinant cancellation must invalidate, not Complete.");
            Assert.That(owner.LastQuery.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(owner.LastQuery.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.InvalidGeometry));
            Assert.That(owner.LastQuery.Count, Is.Zero);
            Assert.That(owner.LastQuery.RawCount, Is.EqualTo(2));
            Assert.That(owner.LastComplete.Count, Is.EqualTo(1));
            Assert.That(owner.LastComplete[0].Collider, Is.SameAs(baseline));
            Assert.That(callbacks, Is.Zero);
        }

        [TestCase(false, 0, 1)]
        [TestCase(true, 0, 1)]
        [TestCase(false, 1, 0)]
        [TestCase(true, 1, 0)]
        [TestCase(false, 2, 1)]
        [TestCase(true, 2, 1)]
        public void SectorCertifiedZeroWitnessSurvivesUnrelatedBoundaryUncertainty(bool reversed, int layout, int expectedCount)
        {
            Vector2 a;
            Vector2 b;
            if (layout == 0)
            {
                a = new Vector2(50, 100);
                b = new Vector2(150, 25);
                var midpoint = (a + b) * 0.5f;
                Assert.That(midpoint, Is.EqualTo(new Vector2(100, 62.5f)));
                Assert.That(midpoint.sqrMagnitude, Is.GreaterThan(110 * 110).And.LessThan(120 * 120));
                Assert.That(midpoint.y, Is.GreaterThan(0).And.LessThan(midpoint.x));
                Assert.That(a.y, Is.GreaterThan(a.x), "The first endpoint is outside the aperture.");
                Assert.That(b.sqrMagnitude, Is.GreaterThan(120 * 120), "The other endpoint is outside the outer arc.");
            }
            else if (layout == 1)
            {
                a = new Vector2(-115, -5);
                b = new Vector2(-115, 5);
            }
            else
            {
                a = new Vector2(120, -20);
                b = new Vector2(120, 20);
            }
            var edge = NewObject(Vector3.zero).AddComponent<EdgeCollider2D>();
            edge.points = reversed ? new[] { b, a } : new[] { a, b };
            Physics2D.SyncTransforms();
            var group = new PhysicsShapeGroup2D(1, 2);
            Assert.That(edge.GetShapes(group, 0, 1), Is.EqualTo(1));
            Assert.That(group.GetShape(0).shapeType, Is.EqualTo(PhysicsShapeType2D.Edges));
            Assert.That(group.GetShape(0).vertexCount, Is.EqualTo(2));
            Assert.That(group.GetShape(0).radius, Is.Zero);
            Assert.That(group.GetShapeVertex(0, 0), Is.EqualTo(reversed ? b : a));
            Assert.That(group.GetShapeVertex(0, 1), Is.EqualTo(reversed ? a : b));
            var results = new Collider2D[8];
            int native = _physics.OverlapCircle(Vector2.zero, 120, Filter().ContactFilter, results);
            TestContext.WriteLine("FIX2 I-R1 native reversed=" + reversed + "; layout=" + layout + "; endpoints=" +
                group.GetShapeVertex(0, 0).ToString("R") + "," + group.GetShapeVertex(0, 1).ToString("R") +
                "; nativeCandidates=" + native);
            Assert.That(native, Is.EqualTo(1), "A real native circle candidate is required, not a pure-kernel substitute.");
            using var workspace = SectorWorkspace();
            var query = QuerySector(Sector(outer: 120, inner: 110), results, workspace);
            TestContext.WriteLine("FIX2 I-R1 wrapper=" + query.Status + "/" + query.InvalidationReason + "; Count/Raw=" +
                query.Count + "/" + query.RawCount + "; expected=" + expectedCount);
            Assert.That(query.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(query.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.None));
            Assert.That(query.Count, Is.EqualTo(expectedCount));
            Assert.That(query.RawCount, Is.EqualTo(1));
            if (expectedCount == 1)
            {
                Assert.That(results[0], Is.SameAs(edge));
            }
            for (int i = expectedCount; i < results.Length; i++)
            {
                Assert.That(results[i], Is.Null);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryShapeQueriesWorldGeometryAndClearsTail(int kind)
        {
            var target = Target(new Vector3(2, 3));
            Target(new Vector3(20, 3));
            var results = new Collider2D[] { target, target, target, target };
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(scene: _physics, shape: Shape(kind, new Vector2(2, 3)),
                filter: Filter(), results: results);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.RawCount, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(results, Is.EqualTo(new Collider2D[] { target, null, null, null }));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryShapeCastsNativeHitsInDistanceOrder(int kind)
        {
            var near = Target(new Vector3(2, 0));
            var far = Target(new Vector3(4, 0));
            var results = new RaycastHit2D[8];
            var native = new RaycastHit2D[8];
            var shape = Shape(kind, Vector2.zero);
            Physics2D.SyncTransforms();
            int raw = NativeCast(shape, Vector2.right, 6, Filter().ContactFilter, native);
            var result = OverlapQuery2D.Cast(scene: _physics, shape: shape,
                direction: Vector2.right * 7, distance: 6, filter: Filter(sortByDistance: true), results: results);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            Assert.That(results[0].collider, Is.SameAs(near));
            Assert.That(results[1].collider, Is.SameAs(far));
            Assert.That(results[0].distance, Is.GreaterThan(0));
            Assert.That(results[0].distance, Is.LessThan(results[1].distance));
            Assert.That(results[0].fraction, Is.EqualTo(results[0].distance / 6).Within(0.0001f));
            Assert.That(result.Count, Is.EqualTo(raw));
            for (int i = 0; i < raw; i++)
            {
                Assert.That(results[i].point, Is.EqualTo(native[i].point));
                Assert.That(results[i].normal, Is.EqualTo(native[i].normal));
                Assert.That(results[i].distance, Is.EqualTo(native[i].distance));
                Assert.That(results[i].fraction, Is.EqualTo(native[i].fraction));
            }
            Assert.That(results[2].collider, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FilterDoesNotMutateGlobalPhysics(bool globalTriggers)
        {
            bool oldTriggers = Physics2D.queriesHitTriggers;
            bool oldStart = Physics2D.queriesStartInColliders;
            var oldSimulation = Physics2D.simulationMode;
            bool oldLayer = Physics2D.GetIgnoreLayerCollision(0, 2);
            try
            {
                var solid = Target(Vector3.zero);
                var trigger = Target(Vector3.zero);
                trigger.isTrigger = true;
                var otherLayer = Target(Vector3.zero);
                otherLayer.gameObject.layer = 2;
                var otherDepth = Target(new Vector3(0, 0, 10));
                Physics2D.queriesHitTriggers = globalTriggers;
                var contact = new ContactFilter2D { useTriggers = true };
                contact.SetLayerMask(1);
                contact.SetDepth(-1, 1);
                var results = new Collider2D[8];
                var hits = new RaycastHit2D[8];
                Physics2D.SyncTransforms();
                int nativeCount = _physics.OverlapCircle(Vector2.zero, 1, contact, results);
                TestContext.WriteLine("Explicit trigger filter global=" + globalTriggers + " useTriggers=" + contact.useTriggers +
                    " nativeCount=" + nativeCount);
                var noDepth = contact;
                noDepth.useDepth = false;
                TestContext.WriteLine("Native without depth=" + _physics.OverlapCircle(Vector2.zero, 1, noDepth, results) +
                    " ray=" + _physics.Raycast(Vector2.left * 2, Vector2.right, 4, contact, hits) +
                    " point=" + _physics.OverlapPoint(Vector2.zero, contact, results) +
                    " trigger=" + trigger.isTrigger + " active=" + trigger.enabled);
                _physics.Simulate(0.02f);
                TestContext.WriteLine("Native after local simulation=" + _physics.OverlapCircle(Vector2.zero, 1, contact, results));
                var broad = new ContactFilter2D
                {
                    useTriggers = true,
                    minDepth = float.NegativeInfinity,
                    maxDepth = float.PositiveInfinity,
                    maxNormalAngle = 359.9999f
                };
                TestContext.WriteLine("Native broad filter=" + _physics.OverlapCircle(Vector2.zero, 1, broad, results));
                var defaultScene = SceneManager.GetActiveScene();
                var defaultTrigger = new GameObject("YL.Query2D.DefaultTrigger").AddComponent<BoxCollider2D>();
                try
                {
                    defaultTrigger.transform.position = new Vector3(21000, 21000);
                    defaultTrigger.isTrigger = true;
                    Physics2D.SyncTransforms();
                    TestContext.WriteLine("Native default scene trigger=" + Physics2D.defaultPhysicsScene.OverlapPoint(
                        new Vector2(21000, 21000), broad, results) + " scene=" + defaultScene.name);
                }
                finally
                {
                    Object.DestroyImmediate(defaultTrigger.gameObject);
                }
                Assert.That(contact.useTriggers, Is.True, "The input to the query must explicitly include triggers.");
                Assert.That(nativeCount, Is.EqualTo(globalTriggers ? 2 : 1), "Record the supported engines' native global-trigger prerequisite independently of the wrapper.");
                var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 1),
                    new OverlapFilter2D(contact), results);
                if (globalTriggers)
                {
                    Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
                    Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.None));
                    Assert.That(result.Count, Is.EqualTo(2));
                    Assert.That(new[] { results[0], results[1] }, Is.EquivalentTo(new[] { solid, trigger }));
                }
                else
                {
                    Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                    Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.TriggerQueriesDisabled));
                    Assert.That(result.Count, Is.Zero);
                    Assert.That(result.RawCount, Is.Zero);
                    Assert.That(results, Is.EqualTo(new Collider2D[8]));
                }
                contact.useTriggers = false;
                result = OverlapQuery2D.Query(_physics, OverlapShape2D.Point(Vector2.zero),
                    new OverlapFilter2D(contact), results);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(results[0], Is.SameAs(solid));
                result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4,
                    new OverlapFilter2D(contact), hits);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(hits[0].collider, Is.SameAs(solid));
                Assert.That(Physics2D.queriesHitTriggers, Is.EqualTo(globalTriggers));
                Assert.That(Physics2D.queriesStartInColliders, Is.EqualTo(oldStart));
                Assert.That(Physics2D.simulationMode, Is.EqualTo(oldSimulation));
                Assert.That(Physics2D.GetIgnoreLayerCollision(0, 2), Is.EqualTo(oldLayer));
                Assert.That(otherDepth, Is.Not.Null);
            }
            finally
            {
                Physics2D.queriesHitTriggers = oldTriggers;
            }
        }

        [Test]
        public void FullBufferNeverClaimsCompleteAfterRootOrColliderFiltering()
        {
            var root = NewObject(Vector3.zero);
            var self = root.AddComponent<BoxCollider2D>();
            Target(Vector3.zero, root.transform);
            Physics2D.SyncTransforms();
            var results = new Collider2D[2];
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 2), Filter(), results);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 2),
                Filter(ignoredRoot: root.transform), results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(results, Is.EqualTo(new Collider2D[2]));
            var hits = new RaycastHit2D[2];
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4,
                Filter(ignoredCollider: self), hits);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
            Assert.That(hits[1].collider, Is.Null);
            Target(Vector3.zero, root.transform);
            Physics2D.SyncTransforms();
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 2),
                Filter(ignoredRoot: root.transform), results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(result.RawCount, Is.EqualTo(2));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
        }

        [Test]
        public void GloballySuppressedTriggerRequestIsInvalidatedRatherThanACompleteMiss()
        {
            bool oldTriggers = Physics2D.queriesHitTriggers;
            try
            {
                var trigger = Target(new Vector3(2, 0));
                trigger.isTrigger = true;
                Target(new Vector3(3, 0));
                Physics2D.queriesHitTriggers = false;
                Physics2D.SyncTransforms();
                var results = new Collider2D[] { trigger, trigger, trigger, trigger };
                var shape = OverlapShape2D.Circle(new Vector2(2, 0), 2);
                var result = OverlapQuery2D.Query(_physics, shape, Filter(triggers: true), results);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.TriggerQueriesDisabled));
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.Zero);
                Assert.That(results, Is.EqualTo(new Collider2D[4]));
                var hits = new RaycastHit2D[4];
                OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 5, Filter(), hits);
                Assert.That(hits[0].collider, Is.Not.Null);
                result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 5,
                    Filter(triggers: true), hits);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.Zero);
                Assert.That(hits, Is.EqualTo(new RaycastHit2D[4]));
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.TriggerQueriesDisabled));
                Assert.That(Physics2D.queriesHitTriggers, Is.False);
            }
            finally
            {
                Physics2D.queriesHitTriggers = oldTriggers;
            }
        }

        [Test]
        public void BodyGroupingUsesActualBodyAndKeepsBodylessCollidersIndependent()
        {
            var root = NewObject(Vector3.zero);
            root.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            var far = Target(new Vector3(3, 0), root.transform);
            var near = Target(new Vector3(1, 0), root.transform);
            var independentA = Target(new Vector3(2, 0));
            var independentB = Target(new Vector3(4, 0));
            var results = new Collider2D[8];
            var hits = new RaycastHit2D[8];
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 8), Filter(), results);
            Assert.That(result.Count, Is.EqualTo(4));
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 8),
                Filter(groupByRigidbody: true, sortByDistance: true), results);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(results, Is.EqualTo(new Collider2D[] { near, independentA, independentB, null, null, null, null, null }));
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 6,
                Filter(groupByRigidbody: true, sortByDistance: true), hits);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(hits[0].collider, Is.SameAs(near));
            Assert.That(hits[1].collider, Is.SameAs(independentA));
            Assert.That(hits[2].collider, Is.SameAs(independentB));
            Assert.That(far.attachedRigidbody, Is.SameAs(near.attachedRigidbody));
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 8),
                Filter(groupByRigidbody: true), new Collider2D[4]);
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        [TestCase(3, false)]
        [TestCase(3, true)]
        public void CastInitialOverlapAndDistanceOrderFollowsQueriesStartInColliders(int kind, bool startInside)
        {
            bool oldStart = Physics2D.queriesStartInColliders;
            try
            {
                Physics2D.queriesStartInColliders = startInside;
                var inside = Target(Vector3.zero);
                var next = Target(new Vector3(2, 0));
                var hits = new RaycastHit2D[8];
                var native = new RaycastHit2D[8];
                Physics2D.SyncTransforms();
                var contact = Filter().ContactFilter;
                var shape = Shape(kind, Vector2.zero);
                int count = NativeCast(shape, Vector2.right, 4, contact, native);
                var result = OverlapQuery2D.Cast(_physics, shape, Vector2.right, 4,
                    Filter(sortByDistance: true), hits);
                Assert.That(result.Count, Is.EqualTo(count));
                if (kind == 0)
                {
                    Assert.That(result.Count, Is.EqualTo(startInside ? 2 : 1));
                    Assert.That(hits[0].collider, Is.SameAs(startInside ? inside : next));
                }
                for (int i = 0; i < count; i++)
                {
                    Assert.That(hits[i], Is.EqualTo(native[i]));
                }
                count = NativeCast(shape, Vector2.right, 0, contact, native);
                result = OverlapQuery2D.Cast(_physics, shape, Vector2.right, 0, Filter(), hits);
                Assert.That(result.Count, Is.EqualTo(count));
                if (kind == 0)
                {
                    Assert.That(result.Count, Is.EqualTo(startInside ? 1 : 0));
                }
                for (int i = 0; i < count; i++)
                {
                    Assert.That(hits[i], Is.EqualTo(native[i]));
                }
                Assert.That(hits[count], Is.EqualTo(default(RaycastHit2D)));
                TestContext.WriteLine("Native 2D initial kind=" + kind + " startInside=" + startInside +
                    " zeroDistanceCount=" + count);
                Assert.That(Physics2D.queriesStartInColliders, Is.EqualTo(startInside));
            }
            finally
            {
                Physics2D.queriesStartInColliders = oldStart;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OrientedBoxAndCapsuleUseFullDimensionsAndWorldAngle(bool capsule)
        {
            var inside = Target(new Vector3(1.5f, 0));
            Target(new Vector3(0, 1.5f));
            var shape = capsule
                ? OverlapShape2D.Capsule(Vector2.zero, new Vector2(1, 4), CapsuleDirection2D.Vertical, 90)
                : OverlapShape2D.Box(Vector2.zero, new Vector2(0.5f, 4), 90);
            var results = new Collider2D[4];
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(_physics, shape, Filter(), results);
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(results[0], Is.SameAs(inside));
            if (capsule)
            {
                shape = OverlapShape2D.Capsule(Vector2.zero, new Vector2(4, 1), CapsuleDirection2D.Horizontal);
                Assert.That(OverlapQuery2D.Query(_physics, shape, Filter(), results).Count, Is.EqualTo(1));
                Assert.That(results[0], Is.SameAs(inside));
            }
        }

        [Test]
        public void MultiplePolygonPathsUseColliderIdentityWithoutMergingBodylessTargets()
        {
            var polygon = NewObject(Vector3.zero).AddComponent<PolygonCollider2D>();
            polygon.pathCount = 2;
            polygon.SetPath(0, new[] { new Vector2(1, -1), new Vector2(2, -1), new Vector2(2, 1), new Vector2(1, 1) });
            polygon.SetPath(1, new[] { new Vector2(3, -1), new Vector2(4, -1), new Vector2(4, 1), new Vector2(3, 1) });
            var independent = Target(new Vector3(5, 0));
            var results = new Collider2D[8];
            var hits = new RaycastHit2D[8];
            Physics2D.SyncTransforms();
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Box(new Vector2(3, 0), new Vector2(8, 4)),
                Filter(), results);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(new[] { results[0], results[1] }, Is.EquivalentTo(new Collider2D[] { polygon, independent }));
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 6,
                Filter(sortByDistance: true), hits);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(hits[0].collider, Is.SameAs(polygon));
            Assert.That(hits[1].collider, Is.SameAs(independent));
        }

        [Test]
        public void CastValidationLeavesExistingNativeHitUntouchedAndNormalizesFiniteDirections()
        {
            var target = Target(new Vector3(2, 0));
            var shape = OverlapShape2D.Point(Vector2.zero);
            var filter = Filter();
            var hits = new RaycastHit2D[4];
            Physics2D.SyncTransforms();
            var snapshot = OverlapQuery2D.Cast(_physics, shape, Vector2.right, 4, filter, hits);
            var saved = hits[0];
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.zero, 1, filter, hits));
            Assert.That(hits[0], Is.EqualTo(saved));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, float.PositiveInfinity, filter, hits));
            Assert.That(hits[0], Is.EqualTo(saved));
            OverlapQuery2D.Cast(_physics, shape, new Vector2(float.MaxValue, 0), 4, filter, hits);
            Assert.That(hits[0], Is.EqualTo(saved));
            OverlapQuery2D.Cast(_physics, shape, new Vector2(float.Epsilon, 0), 4, filter, hits);
            Assert.That(hits[0], Is.EqualTo(saved));
            Object.DestroyImmediate(target.gameObject);
            Physics2D.SyncTransforms();
            Assert.That(OverlapQuery2D.Cast(_physics, shape, Vector2.right, 4, filter, hits).Count, Is.Zero);
            Assert.That(hits, Is.EqualTo(new RaycastHit2D[4]));
            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(snapshot.Status, Is.EqualTo(OverlapQueryStatus.Complete));
        }

        [Test]
        public void SameDistanceTiesUseSessionColliderIdAndDestroyedIgnoresAreSafe()
        {
            var first = Target(new Vector3(2, 0));
            var second = Target(new Vector3(2, 0));
            var ignored = Target(new Vector3(10, 0));
            var filter = Filter(ignoredCollider: ignored, ignoredRoot: ignored.transform, sortByDistance: true);
            Object.DestroyImmediate(ignored.gameObject);
            Physics2D.SyncTransforms();
            var results = new Collider2D[4];
            var hits = new RaycastHit2D[4];
            var result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 4), filter, results);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(results[0].GetInstanceID(), Is.EqualTo(Math.Min(first.GetInstanceID(), second.GetInstanceID())));
            result = OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 4, filter, hits);
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(hits[0].collider, Is.SameAs(results[0]));
            Object.DestroyImmediate(first.gameObject);
            Object.DestroyImmediate(second.gameObject);
            result = OverlapQuery2D.Query(_physics, OverlapShape2D.Circle(Vector2.zero, 4), filter, results);
            Assert.That(result.Count, Is.Zero);
            Assert.That(results, Is.EqualTo(new Collider2D[4]));
            OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.zero), Vector2.right, 4, filter, hits);
            Assert.That(hits[0].collider, Is.Null);
        }

        [UnityTest]
        public IEnumerator ScenesAndCallerBuffersStayIndependentAndUnloadedSceneInvalidates()
        {
            var target = Target(Vector3.zero);
            var other = SceneManager.CreateScene("YL.Query2D.Other." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            try
            {
                var otherTarget = new GameObject("YL.Query2D.OtherTarget").AddComponent<BoxCollider2D>();
                SceneManager.MoveGameObjectToScene(otherTarget.gameObject, other);
                var first = new Collider2D[4];
                var second = new Collider2D[4];
                var shape = OverlapShape2D.Point(Vector2.zero);
                Physics2D.SyncTransforms();
                OverlapQuery2D.Query(_physics, shape, Filter(), first);
                var otherPhysics = other.GetPhysicsScene2D();
                OverlapQuery2D.Query(otherPhysics, shape, Filter(), second);
                Assert.That(first[0], Is.SameAs(target));
                Assert.That(second[0], Is.SameAs(otherTarget));
                var firstHits = new RaycastHit2D[4];
                var secondHits = new RaycastHit2D[4];
                OverlapQuery2D.Cast(_physics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4, Filter(), firstHits);
                OverlapQuery2D.Cast(otherPhysics, OverlapShape2D.Point(Vector2.left * 2), Vector2.right, 4, Filter(), secondHits);
                Assert.That(firstHits[0].collider, Is.SameAs(target));
                Assert.That(secondHits[0].collider, Is.SameAs(otherTarget));
                yield return SceneManager.UnloadSceneAsync(other);
                var result = OverlapQuery2D.Query(otherPhysics, shape, Filter(), first);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(result.Count, Is.Zero);
                Assert.That(result.RawCount, Is.Zero);
                Assert.That(first, Is.EqualTo(new Collider2D[4]));
                result = OverlapQuery2D.Cast(otherPhysics, shape, Vector2.right, 1, Filter(), firstHits);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
                Assert.That(firstHits, Is.EqualTo(new RaycastHit2D[4]));
                result = OverlapQuery2D.Query(shape, new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 0 }), first);
                Assert.That(result.Status, Is.EqualTo(OverlapQueryStatus.Complete));
                Assert.That(result.Count, Is.Zero);
                result = OverlapQuery2D.Cast(shape, Vector2.right, 1,
                    new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 0 }), firstHits);
                Assert.That(result.Count, Is.Zero);
            }
            finally
            {
                if (other.IsValid() && other.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(other);
                }
            }
        }

        [Test]
        public void InvalidGeometryAndArgumentsAreRejectedBeforeOutputMutation()
        {
            var target = Target(Vector3.zero);
            var results = new Collider2D[] { target };
            var shape = OverlapShape2D.Point(Vector2.zero);
            var filter = Filter();
            Assert.Throws<ArgumentNullException>(() => OverlapQuery2D.Query(_physics, shape, filter, null));
            Assert.Throws<ArgumentException>(() => OverlapQuery2D.Query(_physics, shape, filter, new Collider2D[0]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Circle(Vector2.zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Circle(Vector2.zero, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Point(new Vector2(float.NaN, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Box(Vector2.zero, Vector2.zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Box(Vector2.zero, Vector2.one, float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Capsule(Vector2.zero, Vector2.one, (CapsuleDirection2D)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapShape2D.Capsule(Vector2.zero, new Vector2(2, 1), CapsuleDirection2D.Vertical));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.zero, 1, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, -1, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, new Vector2(float.PositiveInfinity, 0), 1, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, float.NaN, filter, new RaycastHit2D[1]));
            Assert.Throws<ArgumentNullException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, 1, filter, null));
            Assert.Throws<ArgumentException>(() => OverlapQuery2D.Cast(_physics, shape, Vector2.right, 1, filter, new RaycastHit2D[0]));
            var contact = new ContactFilter2D { useDepth = true, minDepth = float.NaN, maxDepth = 1 };
            Assert.Throws<ArgumentOutOfRangeException>(() => new OverlapFilter2D(contact));
            Assert.That(results[0], Is.SameAs(target));
        }
    }
}

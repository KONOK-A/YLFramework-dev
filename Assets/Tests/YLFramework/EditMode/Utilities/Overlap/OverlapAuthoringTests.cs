#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Editor.Overlap;
using YLFramework.Overlap;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Behavior
{
    [TestFixture(false)]
    [TestFixture(true)]
    [Category("YL.Module.Behavior")]
    public sealed class OverlapAuthoringTests
    {
        private readonly bool _threeD;
        private string _folder;
        private GameObject _root;

        public OverlapAuthoringTests(bool threeD) => _threeD = threeD;

        [SetUp]
        public void Setup()
        {
            _folder = "Assets/__YLOverlapAuthoring_" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(_folder), Is.False);
            AssetDatabase.CreateFolder("Assets", _folder.Substring(7));
            _root = new GameObject("OwnedAuthoring");
        }

        [TearDown]
        public void Cleanup()
        {
            if (_root)
            {
                Object.DestroyImmediate(_root);
            }
            Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
        }

        [Test]
        public void ProgrammaticFilterSurvivesRealPrefabUnloadAndColdImport()
        {
            var child = new GameObject("IgnoredChild");
            child.transform.SetParent(_root.transform, false);
            if (_threeD)
            {
                var collider = child.AddComponent<BoxCollider>();
                _root.AddComponent<OverlapDetector3D>().Filter = new OverlapFilter3D(1 << 9,
                    QueryTriggerInteraction.Collide, collider, child.transform, true, true);
            }
            else
            {
                var collider = child.AddComponent<BoxCollider2D>();
                _root.AddComponent<OverlapDetector2D>().Filter = new OverlapFilter2D(new ContactFilter2D
                {
                    useLayerMask = true, layerMask = 1 << 9, useTriggers = true,
                    useDepth = true, minDepth = -3, maxDepth = 7, useOutsideDepth = true
                }, collider, child.transform, true, true);
            }
            string path = _folder + "/Authored.prefab";
            Assert.That(PrefabUtility.SaveAsPrefabAsset(_root, path), Is.Not.Null);
            string guid = AssetDatabase.AssetPathToGUID(path);
            var bytes = File.ReadAllBytes(path);
            Object.DestroyImmediate(_root);
            _root = null;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var loaded = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (_threeD)
                {
                    var filter = loaded.GetComponent<OverlapDetector3D>().Filter;
                    Assert.That((int)filter.LayerMask, Is.EqualTo(1 << 9));
                    Assert.That(filter.TriggerInteraction, Is.EqualTo(QueryTriggerInteraction.Collide));
                    Assert.That(filter.GroupByRigidbody && filter.SortByDistance, Is.True);
                    Assert.That(filter.IgnoredCollider, Is.SameAs(loaded.transform.GetChild(0).GetComponent<BoxCollider>()));
                    Assert.That(filter.IgnoredRoot, Is.SameAs(loaded.transform.GetChild(0)));
                }
                else
                {
                    var filter = loaded.GetComponent<OverlapDetector2D>().Filter;
                    Assert.That((int)filter.ContactFilter.layerMask, Is.EqualTo(1 << 9));
                    Assert.That(filter.ContactFilter.useDepth && filter.ContactFilter.useTriggers && filter.ContactFilter.useOutsideDepth, Is.True);
                    Assert.That(filter.ContactFilter.minDepth, Is.EqualTo(-3));
                    Assert.That(filter.ContactFilter.maxDepth, Is.EqualTo(7));
                    Assert.That(filter.GroupByRigidbody && filter.SortByDistance, Is.True);
                    Assert.That(filter.IgnoredCollider, Is.SameAs(loaded.transform.GetChild(0).GetComponent<BoxCollider2D>()));
                    Assert.That(filter.IgnoredRoot, Is.SameAs(loaded.transform.GetChild(0)));
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(loaded);
            }
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "Cold inspection must not rewrite the authored prefab.");
        }

        [Test]
        public void CurvedGeometryColdPrefabUndoAndExplicitEditModePreviewBoundary()
        {
            Component component;
            if (_threeD)
            {
                var detector = _root.AddComponent<OverlapDetector3D>();
                detector.Composite = new OverlapComposite3D(OverlapShape3D.Sector(Vector3.zero, 3, 270, 2, 1),
                    OverlapShape3D.Cone(Vector3.right * 8, Quaternion.Euler(10, 20, 30), 4, 2));
                detector.ConfigureGeometryWorkspace(0.02f, 192, 192, 24);
                component = detector;
            }
            else
            {
                var detector = _root.AddComponent<OverlapDetector2D>();
                detector.Composite = new OverlapComposite2D(OverlapShape2D.Sector(Vector2.zero, 3, 270, 1),
                    OverlapShape2D.Sector(Vector2.right * 8, 3, 360, 1));
                component = detector;
            }
            string path = _folder + "/Curved.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(_root, path);
            byte[] before = File.ReadAllBytes(path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            Object.DestroyImmediate(_root);
            _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            component = _threeD ? (Component)_root.GetComponent<OverlapDetector3D>() : _root.GetComponent<OverlapDetector2D>();
            if (_threeD)
            {
                var detector = (OverlapDetector3D)component;
                Assert.That(detector.Composite.GetShape(0).InnerRadius, Is.EqualTo(1));
                Assert.That(detector.Composite.GetShape(1).Height, Is.EqualTo(4));
                Assert.That(detector.RequestedBoundaryError, Is.EqualTo(0.02f));
                Undo.IncrementCurrentGroup();
                OverlapDetectorInspector.SetRoundDimensions(detector, 0, 4, 0.5f, 240, 3);
                Undo.FlushUndoRecordObjects();
                Assert.That(detector.Composite.GetShape(0).Radius, Is.EqualTo(4));
                Undo.PerformUndo();
                Assert.That(detector.Composite.GetShape(0).Radius, Is.EqualTo(3));
                Undo.PerformRedo();
                Assert.That(detector.Composite.GetShape(0).AngleDegrees, Is.EqualTo(240));
                Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(_root, false), Is.True);
                Assert.That(detector.HasGeometryWorkspace, Is.False);
                int count = SceneManager.sceneCount;
                var buffer = new Collider[4];
                var result = OverlapQuery3D.Query(OverlapDetectorInspector.World3D(detector, 0), new OverlapFilter3D(~0), buffer);
                Assert.That(result.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.RequiresPlayMode));
                Assert.Throws<InvalidOperationException>(() => new OverlapQueryWorkspace3D());
                detector.StartDetection();
                Assert.That(detector.Sample(), Is.False);
                Assert.That(detector.LastQuery.InvalidationReason, Is.EqualTo(OverlapQueryInvalidationReason.RequiresPlayMode));
                Assert.That(detector.BufferCapacity, Is.Zero);
                Assert.That(SceneManager.sceneCount, Is.EqualTo(count));
                var previous = Selection.objects;
                var inspector = (OverlapDetectorInspector)UnityEditor.Editor.CreateEditor(detector);
                try
                {
                    Selection.activeGameObject = _root;
                    inspector.SamplePreview();
                    string status = (string)typeof(OverlapDetectorInspector).GetField("_previewStatus", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(inspector);
                    Assert.That(status, Does.Contain("RequiresPlayMode"));
                    Assert.That(SceneManager.sceneCount, Is.EqualTo(count));
                }
                finally
                {
                    Object.DestroyImmediate(inspector);
                    Selection.objects = previous;
                }
            }
            else
            {
                var detector = (OverlapDetector2D)component;
                Assert.That(detector.Composite.GetShape(0).InnerRadius, Is.EqualTo(1));
                Assert.That(detector.Composite.GetShape(1).AngleDegrees, Is.EqualTo(360));
            }
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }

        [Test]
        public void RoundHeightHandlePreservesEveryUneditedSerializedBitAndPrefabOverride()
        {
            for (int kind = 0; kind < 3; kind++)
            {
                if (_root) Object.DestroyImmediate(_root);
                _root = new GameObject("Fix1HandleAuthor");
                _root.transform.SetPositionAndRotation(new Vector3(5, -2, 4), Quaternion.Euler(23, 41, 17));
                _root.transform.localScale = Vector3.one * 2;
                var child = new GameObject("Geometry");
                child.transform.SetParent(_root.transform, false);
                child.transform.localPosition = new Vector3(0.2f, 0.3f, 0.4f);
                child.transform.localRotation = Quaternion.Euler(7, 11, 13);
                var detector = child.AddComponent<OverlapDetector3D>();
                detector.Composite = new OverlapComposite3D(kind == 2 ? OverlapShape3D.Cone(Vector3.zero, 2, 3) :
                    OverlapShape3D.Sector(Vector3.zero, 3, kind == 0 ? 0.0001f : 360, 2, 2.99999f));
                if (kind == 2)
                {
                    using var initial = new SerializedObject(detector);
                    var entry = initial.FindProperty("composite.shapes").GetArrayElementAtIndex(0);
                    entry.FindPropertyRelative("angleDegrees").floatValue = 42.125f;
                    entry.FindPropertyRelative("innerRadius").floatValue = 0.1234567f;
                    initial.ApplyModifiedPropertiesWithoutUndo();
                }
                string path = _folder + "/Fix1Handle" + kind + ".prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(_root, path);
                var bytes = File.ReadAllBytes(path);
                string guid = AssetDatabase.AssetPathToGUID(path);
                Object.DestroyImmediate(_root);
                _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                detector = _root.transform.GetChild(0).GetComponent<OverlapDetector3D>();
                int[] Bits()
                {
                    using var data = new SerializedObject(detector);
                    data.Update();
                    var entry = data.FindProperty("composite.shapes").GetArrayElementAtIndex(0);
                    return new[] { BitConverter.SingleToInt32Bits(entry.FindPropertyRelative("radius").floatValue),
                        BitConverter.SingleToInt32Bits(entry.FindPropertyRelative("innerRadius").floatValue),
                        BitConverter.SingleToInt32Bits(entry.FindPropertyRelative("angleDegrees").floatValue),
                        BitConverter.SingleToInt32Bits(entry.FindPropertyRelative("height").floatValue) };
                }
                int[] before = Bits();
                var world = OverlapDetectorInspector.World3D(detector, 0);
                Vector3 axis = world.Orientation * (kind == 2 ? Vector3.forward : Vector3.up);
                Vector3 point = world.Center + axis * (kind == 2 ? 5 : 2.5f);
                Undo.IncrementCurrentGroup();
                OverlapDetectorInspector.ApplyRoundHandlePoints(detector, 0, null, point, null, null);
                Undo.FlushUndoRecordObjects();
                int[] after = Bits();
                TestContext.WriteLine("FIX1 M1 kind=" + kind + " before=" + string.Join(",", before) + " after=" + string.Join(",", after));
                for (int i = 0; i < 3; i++) Assert.That(after[i], Is.EqualTo(before[i]), "height-only kind/field=" + kind + "/" + i);
                Assert.That(after[3], Is.Not.EqualTo(before[3]));
                var modifications = PrefabUtility.GetPropertyModifications(_root);
                var geometry = modifications.Where(value => value.target is OverlapDetector3D && value.propertyPath.StartsWith("composite.shapes.Array.data[0].", StringComparison.Ordinal)).ToArray();
                Assert.That(geometry.Select(value => value.propertyPath), Is.EquivalentTo(new[] { "composite.shapes.Array.data[0].height" }));
                Undo.PerformUndo();
                CollectionAssert.AreEqual(before, Bits());
                Undo.PerformRedo();
                CollectionAssert.AreEqual(after, Bits());
                for (int edited = 0; edited < (kind == 2 ? 1 : 3); edited++)
                {
                    int[] previous = Bits();
                    world = OverlapDetectorInspector.World3D(detector, 0);
                    Vector3 forward = world.Orientation * Vector3.forward;
                    Vector3 radial = world.Orientation * (kind == 2 ? Vector3.right : Vector3.forward);
                    Vector3 baseCenter = kind == 2 ? world.Center + forward * world.Height : world.Center;
                    Vector3? radiusPoint = edited == 0 ? baseCenter + radial * (world.Radius + 0.5f) : (Vector3?)null;
                    Vector3? innerPoint = edited == 1 ? world.Center + radial * (world.InnerRadius * 0.5f) : (Vector3?)null;
                    Vector3? anglePoint = edited == 2 ? world.Center + world.Orientation * Vector3.right * world.Radius : (Vector3?)null;
                    Undo.IncrementCurrentGroup();
                    Assert.That(OverlapDetectorInspector.ApplyRoundHandlePoints(detector, 0, radiusPoint, null, innerPoint, anglePoint), Is.True);
                    Undo.FlushUndoRecordObjects();
                    int[] changed = Bits();
                    for (int i = 0; i < 4; i++)
                        Assert.That(changed[i] == previous[i], Is.EqualTo(i != edited), "independent slider kind/edited/field=" + kind + "/" + edited + "/" + i);
                    string field = edited == 0 ? "radius" : edited == 1 ? "innerRadius" : "angleDegrees";
                    var overrides = PrefabUtility.GetPropertyModifications(_root).Where(value => value.target is OverlapDetector3D &&
                        value.propertyPath.StartsWith("composite.shapes.Array.data[0].", StringComparison.Ordinal)).Select(value => value.propertyPath);
                    Assert.That(overrides, Is.EquivalentTo(new[] { "composite.shapes.Array.data[0].height", "composite.shapes.Array.data[0]." + field }));
                    Undo.PerformUndo();
                    CollectionAssert.AreEqual(previous, Bits());
                    Undo.PerformRedo();
                    CollectionAssert.AreEqual(changed, Bits());
                    Undo.PerformUndo();
                }
                int[] untouched = Bits();
                Assert.That(OverlapDetectorInspector.ApplyRoundHandlePoints(detector, 0, null, null, null, null), Is.False);
                if (kind != 2)
                {
                    world = OverlapDetectorInspector.World3D(detector, 0);
                    Assert.That(OverlapDetectorInspector.ApplyRoundHandlePoints(detector, 0,
                        world.Center + world.Orientation * Vector3.forward * (world.InnerRadius * 0.5f), null, null, null), Is.False,
                        "an actually edited outer/inner conflict is rejected without modifying another dimension");
                }
                CollectionAssert.AreEqual(untouched, Bits());
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "instance handles must not rewrite cold prefab bytes");
                var cold = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Assert.That(cold.transform.GetChild(0).GetComponent<OverlapDetector3D>().Composite.GetShape(0).Height, Is.EqualTo(2));
                }
                finally { PrefabUtility.UnloadPrefabContents(cold); }
                TestContext.WriteLine("FIX1 M1 height-only kind=" + kind + " unchangedBits=" + string.Join(",", before.Take(3)) + " onlyHeightOverride=true");
            }
        }

        [Test]
        public void DetectorHasPackageOwnedInspectorWithoutSamplingOnCreation()
        {
            Component detector = _threeD ? (Component)_root.AddComponent<OverlapDetector3D>() : _root.AddComponent<OverlapDetector2D>();
            var inspector = UnityEditor.Editor.CreateEditor(detector);
            try
            {
                Assert.That(inspector.GetType().Assembly.GetName().Name, Is.EqualTo("YLFramework.Overlap.Editor"));
                Assert.That(_threeD ? _root.GetComponent<OverlapDetector3D>().BufferCapacity :
                    _root.GetComponent<OverlapDetector2D>().BufferCapacity, Is.Zero, "Creating an inspector is not consent to query physics.");
            }
            finally
            {
                Object.DestroyImmediate(inspector);
            }
        }

        [Test]
        public void BorrowedDiagnosticsDoNotQueryOrRelabelPartialAsComplete()
        {
            Component detector = _threeD ? (Component)_root.AddComponent<OverlapDetector3D>() : _root.AddComponent<OverlapDetector2D>();
            int enters = 0;
            if (_threeD)
            {
                var three = (OverlapDetector3D)detector;
                three.ConfigureCapacity(2, 2);
                three.Composite = new OverlapComposite3D(OverlapShape3D.Sphere(Vector3.zero, 2));
                three.Filter = new OverlapFilter3D(1 << 11);
                three.Entered += _ => enters++;
            }
            else
            {
                var two = (OverlapDetector2D)detector;
                two.ConfigureCapacity(2, 2);
                two.Composite = new OverlapComposite2D(OverlapShape2D.Circle(Vector2.zero, 2));
                two.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 });
                two.Entered += _ => enters++;
            }
            AddTarget();
            Sync();
            Assert.That(Sample(detector), Is.True);
            AddTarget();
            AddTarget();
            Sync();
            Assert.That(Sample(detector), Is.False);
            for (int i = 0; i < 10; i++)
            {
                var diagnostic = OverlapDetectorInspector.ReadDiagnostics(detector);
                Assert.That(diagnostic.Status, Is.EqualTo(OverlapQueryStatus.MayBeTruncated));
                Assert.That(diagnostic.Count, Is.EqualTo(2));
                Assert.That(diagnostic.RawCount, Is.EqualTo(2));
                Assert.That(diagnostic.LastCompleteCount, Is.EqualTo(1));
                Assert.That(diagnostic.Capacity, Is.EqualTo(2));
            }
            Assert.That(enters, Is.EqualTo(1), "Reading diagnostics must never perform another query or dispatch.");
        }

        [Test]
        public void CenterHandleUsesFullParentMatrixAndRealUndoPrefabOverrideWithoutAssetMutation()
        {
            _root.transform.SetPositionAndRotation(new Vector3(8, -2, 6), Quaternion.Euler(0, 0, 90));
            _root.transform.localScale = new Vector3(-2, 3, 4);
            var child = new GameObject("Detector");
            child.transform.SetParent(_root.transform, false);
            child.transform.localPosition = new Vector3(1, 2, 3);
            if (_threeD)
            {
                child.AddComponent<OverlapDetector3D>().Composite = new OverlapComposite3D(
                    OverlapShape3D.Box(Vector3.zero, Vector3.one, Quaternion.identity));
            }
            else
            {
                child.AddComponent<OverlapDetector2D>().Composite = new OverlapComposite2D(
                    OverlapShape2D.Box(Vector2.zero, Vector2.one));
            }
            string path = _folder + "/Handle.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(_root, path);
            byte[] bytes = File.ReadAllBytes(path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            Object.DestroyImmediate(_root);
            _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Component detector = _threeD ? (Component)_root.GetComponentInChildren<OverlapDetector3D>() : _root.GetComponentInChildren<OverlapDetector2D>();
            Undo.IncrementCurrentGroup();
            OverlapDetectorInspector.MoveCenter(detector, 0, new Vector3(5, -8, 18));
            Undo.FlushUndoRecordObjects();
            AssertLocalCenter(detector, new Vector3(2, -1, 0));
            Assert.That(PrefabUtility.GetPropertyModifications(_root), Has.Some.Matches<PropertyModification>(
                item => item.propertyPath.StartsWith("composite.shapes.Array.data[0].center", StringComparison.Ordinal)));
            Vector3 world = _threeD ? OverlapDetectorInspector.World3D((OverlapDetector3D)detector, 0).Center :
                (Vector3)OverlapDetectorInspector.World2D((OverlapDetector2D)detector, 0).Center;
            Assert.That(Vector3.Distance(world, new Vector3(5, -8, _threeD ? 18 : 0)), Is.LessThan(0.0001f));
            Undo.PerformUndo();
            AssertLocalCenter(detector, Vector3.zero);
            Undo.PerformRedo();
            AssertLocalCenter(detector, new Vector3(2, -1, 0));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }

        [Test]
        public void SerializedInvalidFilterIsVisibleAndNeverAllocatesNativeBuffer()
        {
            Component detector = _threeD ? (Component)_root.AddComponent<OverlapDetector3D>() : _root.AddComponent<OverlapDetector2D>();
            using var data = new SerializedObject(detector);
            if (_threeD)
            {
                data.FindProperty("triggerInteraction").intValue = (int)QueryTriggerInteraction.UseGlobal;
            }
            else
            {
                data.FindProperty("contactFilter.useDepth").boolValue = true;
                data.FindProperty("contactFilter.minDepth").floatValue = 4;
                data.FindProperty("contactFilter.maxDepth").floatValue = 2;
            }
            data.ApplyModifiedProperties();
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Is.Not.Empty);
            Assert.That(Sample(detector), Is.False);
            var diagnostic = OverlapDetectorInspector.ReadDiagnostics(detector);
            Assert.That(diagnostic.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(diagnostic.Error, Is.Not.Empty);
            Assert.That(diagnostic.Capacity, Is.Zero);
        }

        [Test]
        public void SerializedFilterUndoAndMultiEditDoNotOverwriteProgrammaticAssignmentOnSample()
        {
            Component first = _threeD ? (Component)_root.AddComponent<OverlapDetector3D>() : _root.AddComponent<OverlapDetector2D>();
            var child = new GameObject("OtherDetector");
            child.transform.SetParent(_root.transform, false);
            Component second = _threeD ? (Component)child.AddComponent<OverlapDetector3D>() : child.AddComponent<OverlapDetector2D>();
            if (_threeD) ((OverlapDetector3D)first).Filter = new OverlapFilter3D(1 << 11);
            else ((OverlapDetector2D)first).Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 });
            using (var data = new SerializedObject(new Object[] { first, second }))
            {
                data.FindProperty("frequency").floatValue = 17;
                data.FindProperty("samplingMode").intValue = (int)OverlapSamplingMode.FixedUpdate;
                data.FindProperty("initialCapacity").intValue = 3;
                data.FindProperty("maxCapacity").intValue = 9;
                data.FindProperty("growBuffer").boolValue = true;
                data.ApplyModifiedProperties();
            }
            AddTarget();
            Sync();
            Assert.That(Sample(first), Is.True);
            Assert.That(_threeD ? (int)((OverlapDetector3D)first).Filter.LayerMask :
                (int)((OverlapDetector2D)first).Filter.ContactFilter.layerMask, Is.EqualTo(1 << 11));
            OverlapExitReason? reason = null;
            if (_threeD) ((OverlapDetector3D)first).Exited += (_, exit) => reason = exit;
            else ((OverlapDetector2D)first).Exited += (_, exit) => reason = exit;
            Undo.IncrementCurrentGroup();
            using (var data = new SerializedObject(first))
            {
                data.FindProperty(_threeD ? "layerMask" : "contactFilter.layerMask").intValue = 0;
                data.ApplyModifiedProperties();
            }
            Undo.FlushUndoRecordObjects();
            Assert.That(Sample(first), Is.True);
            Assert.That(reason, Is.EqualTo(OverlapExitReason.FilterChanged));
            Undo.PerformUndo();
            Assert.That(Sample(first), Is.True);
            Assert.That(OverlapDetectorInspector.ReadDiagnostics(first).LastCompleteCount, Is.EqualTo(1));
            using var check = new SerializedObject(second);
            Assert.That(check.FindProperty("frequency").floatValue, Is.EqualTo(17));
            Assert.That(check.FindProperty("initialCapacity").intValue, Is.EqualTo(3));
            Assert.That(check.FindProperty("maxCapacity").intValue, Is.EqualTo(9));
            Assert.That(check.FindProperty("growBuffer").boolValue, Is.True);
        }

        [Test]
        public void ExplicitSelectedPreviewShowsRawFilteringWithoutWritingConfigOrRuntimeContacts()
        {
            Component detector = _threeD ? (Component)_root.AddComponent<OverlapDetector3D>() : _root.AddComponent<OverlapDetector2D>();
            AddTarget();
            AddTarget();
            var ignored = _root.transform.GetChild(0);
            if (_threeD) ((OverlapDetector3D)detector).Filter = new OverlapFilter3D(1 << 11, ignoredRoot: ignored);
            else ((OverlapDetector2D)detector).Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 }, ignoredRoot: ignored);
            Sync();
            var previous = Selection.objects;
            var inspector = (OverlapDetectorInspector)UnityEditor.Editor.CreateEditor(detector);
            try
            {
                string serialized = EditorJsonUtility.ToJson(detector);
                Selection.objects = Array.Empty<Object>();
                inspector.SamplePreview();
                Assert.That(PreviewCount(inspector, "_rawCount"), Is.Zero, "An unselected inspector cannot sample even through its preview action.");
                Selection.activeGameObject = _root;
                inspector.SamplePreview();
                Assert.That(PreviewCount(inspector, "_rawCount"), Is.EqualTo(2));
                Assert.That(PreviewCount(inspector, "_acceptedCount"), Is.EqualTo(1));
                Assert.That(OverlapDetectorInspector.ReadDiagnostics(detector).Capacity, Is.Zero);
                Assert.That(OverlapDetectorInspector.ReadDiagnostics(detector).LastCompleteCount, Is.Zero);
                Assert.That(EditorJsonUtility.ToJson(detector), Is.EqualTo(serialized), "Preview must never write back world geometry or runtime filters.");
                Object.DestroyImmediate(inspector);
                Assert.That(PreviewCount(inspector, "_rawCount"), Is.Zero, "Closing the inspector must release its borrowed preview references.");
            }
            finally
            {
                if (inspector) Object.DestroyImmediate(inspector);
                Selection.objects = previous;
            }
        }

        [Test]
        public void ShearedWorldGeometryIsADiagnosticRatherThanAnApproximatePreview()
        {
            _root.transform.localScale = new Vector3(2, 1, 1);
            var child = new GameObject("Sheared");
            child.transform.SetParent(_root.transform, false);
            child.transform.localRotation = Quaternion.Euler(0, 0, 45);
            Component detector;
            if (_threeD)
            {
                var three = child.AddComponent<OverlapDetector3D>();
                three.Composite = new OverlapComposite3D(OverlapShape3D.Box(Vector3.zero, Vector3.one, Quaternion.identity));
                detector = three;
            }
            else
            {
                var two = child.AddComponent<OverlapDetector2D>();
                two.Composite = new OverlapComposite2D(OverlapShape2D.Box(Vector2.zero, Vector2.one));
                detector = two;
            }
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Does.Contain("Sheared"));
            Assert.That(OverlapDetectorInspector.ReadDiagnostics(detector).Capacity, Is.Zero);
        }

        [Test]
        public void StopReleasesCommittedPolicyReferencesAfterAuthoritativeFilterChanges()
        {
            Component detector = _threeD ? (Component)_root.AddComponent<OverlapDetector3D>() : _root.AddComponent<OverlapDetector2D>();
            AddTarget();
            AddTarget();
            var ignored = _root.transform.GetChild(0);
            if (_threeD) ((OverlapDetector3D)detector).Filter = new OverlapFilter3D(1 << 11, ignoredRoot: ignored);
            else ((OverlapDetector2D)detector).Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 }, ignoredRoot: ignored);
            Sync();
            Assert.That(Sample(detector), Is.True);
            if (_threeD)
            {
                var three = (OverlapDetector3D)detector;
                three.Filter = new OverlapFilter3D(1 << 11);
                three.Stop();
                var committed = (OverlapFilter3D)detector.GetType().GetField("_committedFilter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(detector);
                Assert.That(committed.IgnoredRoot, Is.Null, "The old committed policy is transient observation state, not a second retained configuration.");
                Assert.That((int)three.Filter.LayerMask, Is.EqualTo(1 << 11));
            }
            else
            {
                var two = (OverlapDetector2D)detector;
                two.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 });
                two.Stop();
                var committed = (OverlapFilter2D)detector.GetType().GetField("_committedFilter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(detector);
                Assert.That(committed.IgnoredRoot, Is.Null, "The old committed policy is transient observation state, not a second retained configuration.");
                Assert.That((int)two.Filter.ContactFilter.layerMask, Is.EqualTo(1 << 11));
            }
        }

        [Test]
        public void PersistentMultiSelectionCannotPreviewOrRewriteSharedPrefabAssets()
        {
            if (_threeD) _root.AddComponent<OverlapDetector3D>();
            else _root.AddComponent<OverlapDetector2D>();
            string path = _folder + "/Shared.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(_root, path);
            Component shared = _threeD ? (Component)prefab.GetComponent<OverlapDetector3D>() : prefab.GetComponent<OverlapDetector2D>();
            string otherPath = _folder + "/Other.prefab";
            var other = PrefabUtility.SaveAsPrefabAsset(_root, otherPath);
            Component otherShared = _threeD ? (Component)other.GetComponent<OverlapDetector3D>() : other.GetComponent<OverlapDetector2D>();
            var bytes = File.ReadAllBytes(path);
            var otherBytes = File.ReadAllBytes(otherPath);
            var inspector = (OverlapDetectorInspector)UnityEditor.Editor.CreateEditor(new Object[] { shared, otherShared });
            try
            {
                Assert.That(inspector.targets.Length, Is.EqualTo(2));
                Assert.That(EditorUtility.IsPersistent(inspector.target), Is.True);
                inspector.SamplePreview();
                Assert.That(PreviewCount(inspector, "_rawCount"), Is.Zero);
                Assert.That(OverlapDetectorInspector.ReadDiagnostics(shared).Capacity, Is.Zero);
                Assert.That(OverlapDetectorInspector.ReadDiagnostics(otherShared).Capacity, Is.Zero);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(otherBytes, File.ReadAllBytes(otherPath));
            }
            finally
            {
                Object.DestroyImmediate(inspector);
            }
        }

        private static int PreviewCount(OverlapDetectorInspector inspector, string name) =>
            (int)typeof(OverlapDetectorInspector).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(inspector);

        private void AddTarget()
        {
            var child = new GameObject("NativeTarget") { layer = 11 };
            child.transform.SetParent(_root.transform, false);
            if (_threeD) child.AddComponent<BoxCollider>();
            else child.AddComponent<BoxCollider2D>();
        }

        private void Sync()
        {
            if (_threeD) Physics.SyncTransforms();
            else Physics2D.SyncTransforms();
        }

        private bool Sample(Component detector)
        {
            if (_threeD)
            {
                var three = (OverlapDetector3D)detector;
                if (!three.IsRunning) three.StartDetection();
                return three.Sample();
            }
            var two = (OverlapDetector2D)detector;
            if (!two.IsRunning) two.StartDetection();
            return two.Sample();
        }

        private void AssertLocalCenter(Component detector, Vector3 expected)
        {
            Vector3 actual = _threeD ? ((OverlapDetector3D)detector).Composite.GetShape(0).Center :
                (Vector3)((OverlapDetector2D)detector).Composite.GetShape(0).Center;
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(0.0001f));
        }
    }

    [TestFixture]
    [Category("YL.Module.Behavior")]
    public sealed class OverlapProjectedHandleTests
    {
        private GameObject _root;
        private string _folder;
        private Scene _scene;

        [SetUp]
        public void Setup()
        {
            _folder = "Assets/__YLProjectedHandle_" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(_folder), Is.False);
            AssetDatabase.CreateFolder("Assets", _folder.Substring(7));
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = new GameObject("OwnedProjectedHandle");
            SceneManager.MoveGameObjectToScene(_root, _scene);
            TestContext.WriteLine("Owned projected handle fixture: " + _folder);
        }

        [TearDown]
        public void Cleanup()
        {
            if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed)
            {
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }
        }

        [Test]
        public void TiltedPointHandleInvertsRuntimeXYAndPersistsCorrectLocalCoordinates()
        {
            _root.transform.rotation = Quaternion.Euler(60, 0, 0);
            _root.AddComponent<OverlapDetector2D>().Composite = new OverlapComposite2D(OverlapShape2D.Point(Vector2.up));
            string path = _folder + "/Tilted.prefab";
            var prefab = InstantiateOwnedPrefab(path);
            var detector = _root.GetComponent<OverlapDetector2D>();
            byte[] bytes = File.ReadAllBytes(path);
            byte[] meta = File.ReadAllBytes(path + ".meta");
            string guid = AssetDatabase.AssetPathToGUID(path);
            var before = OverlapDetectorInspector.World2D(detector, 0).Center;
            Assert.That(before.x, Is.EqualTo(0).Within(0.000001));
            Assert.That(before.y, Is.EqualTo(0.5f).Within(0.000001));
            Undo.IncrementCurrentGroup();
            OverlapDetectorInspector.MoveCenter(detector, 0, new Vector3(0, 1, 0));
            Undo.FlushUndoRecordObjects();
            Vector2 local = detector.Composite.GetShape(0).Center;
            Vector2 world = OverlapDetectorInspector.World2D(detector, 0).Center;
            TestContext.WriteLine("X60 handle: before=" + before.ToString("F8") + "; requested=(0,1,0); local=" + local.ToString("F8") + "; runtimeXY=" + world.ToString("F8"));
            Assert.That(local.x, Is.EqualTo(0).Within(0.000001));
            Assert.That(local.y, Is.EqualTo(2).Within(0.000001));
            Assert.That(world.y, Is.EqualTo(1).Within(0.000001));
            using (var data = new SerializedObject(detector))
            {
                Assert.That(data.FindProperty("composite.shapes").GetArrayElementAtIndex(0).FindPropertyRelative("center").prefabOverride, Is.True);
            }
            Undo.PerformUndo();
            Assert.That(detector.Composite.GetShape(0).Center.y, Is.EqualTo(1).Within(0.000001));
            Undo.PerformRedo();
            Assert.That(detector.Composite.GetShape(0).Center.y, Is.EqualTo(2).Within(0.000001));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "Handle edits must not save the shared prefab.");
            Assert.That(EditorUtility.IsDirty(prefab), Is.False);
            PrefabUtility.ApplyPrefabInstance(_root, InteractionMode.AutomatedAction);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var cold = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var loaded = cold.GetComponent<OverlapDetector2D>();
                Assert.That(loaded.Composite.GetShape(0).Center.y, Is.EqualTo(2).Within(0.000001));
                Assert.That(OverlapDetectorInspector.World2D(loaded, 0).Center.y, Is.EqualTo(1).Within(0.000001));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(cold);
            }
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
        }

        [TestCase(60, 0, false)]
        [TestCase(60, 0, true)]
        [TestCase(0, 60, false)]
        [TestCase(0, 60, true)]
        [TestCase(30, 45, true)]
        public void PointHandleUsesProjectedFullParentMatrixAndIgnoresWorldZ(float tiltX, float tiltY, bool mirror)
        {
            _root.transform.SetPositionAndRotation(new Vector3(7, -9, 13), Quaternion.Euler(tiltX, tiltY, 0));
            _root.transform.localScale = new Vector3(mirror ? -2 : 2, 3, 4);
            var child = new GameObject("TiltedChild");
            child.transform.SetParent(_root.transform, false);
            child.transform.localPosition = new Vector3(1, 2, 3);
            child.transform.localRotation = Quaternion.Euler(0, 0, 23);
            var detector = child.AddComponent<OverlapDetector2D>();
            detector.Composite = new OverlapComposite2D(OverlapShape2D.Point(new Vector2(-0.5f, 0.25f)));
            Vector3 requested = child.transform.localToWorldMatrix.MultiplyPoint3x4(new Vector3(2, -1, 0));
            requested.z = -1000;
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Is.Empty, "Point must not inherit volume axis restrictions.");
            OverlapDetectorInspector.MoveCenter(detector, 0, requested);
            var local = detector.Composite.GetShape(0).Center;
            var world = OverlapDetectorInspector.World2D(detector, 0).Center;
            TestContext.WriteLine("Projected parent handle: requested=" + requested.ToString("F8") + "; local=" + local.ToString("F8") + "; runtimeXY=" + world.ToString("F8"));
            Assert.That(local.x, Is.EqualTo(2).Within(0.00001));
            Assert.That(local.y, Is.EqualTo(-1).Within(0.00001));
            Assert.That(world.x, Is.EqualTo(requested.x).Within(0.00001));
            Assert.That(world.y, Is.EqualTo(requested.y).Within(0.00001));
        }

        [TestCase(1e-20f, 1e-20f, 1f)]
        [TestCase(1e20f, 1e20f, 1f)]
        [TestCase(1f, 0.001f, 0f)]
        [TestCase(1e-10f, 1e-6f, 2f)]
        public void PointHandleUsesRelativeConditioningRatherThanAnAbsoluteScaleCutoff(float x, float y, float z)
        {
            _root.transform.localScale = new Vector3(x, y, z);
            _root.transform.rotation = Quaternion.Euler(60, 0, 0);
            var detector = _root.AddComponent<OverlapDetector2D>();
            detector.Composite = new OverlapComposite2D(OverlapShape2D.Point(Vector2.zero));
            Vector3 requested = _root.transform.localToWorldMatrix.MultiplyPoint3x4(new Vector3(2, 2, 0));
            requested.z = 0;
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Is.Empty);
            OverlapDetectorInspector.MoveCenter(detector, 0, requested);
            Vector2 local = detector.Composite.GetShape(0).Center;
            Assert.That(local.x, Is.EqualTo(2).Within(0.00001));
            Assert.That(local.y, Is.EqualTo(2).Within(0.00001));
        }

        [Test]
        public void PointHandleRetainsAResolvableNearlyEdgeOnProjection()
        {
            _root.transform.rotation = Quaternion.Euler(89.99f, 0, 0);
            var detector = _root.AddComponent<OverlapDetector2D>();
            detector.Composite = new OverlapComposite2D(OverlapShape2D.Point(Vector2.up));
            Vector3 requested = _root.transform.localToWorldMatrix.MultiplyPoint3x4(new Vector3(0, 2, 0));
            requested.z = 0;
            OverlapDetectorInspector.MoveCenter(detector, 0, requested);
            Assert.That(detector.Composite.GetShape(0).Center.y, Is.EqualTo(2).Within(0.00001));
        }

        [Test]
        public void ZOnlyPointDragDoesNotChangeGeometryDirtyStateOrConsumeUndo()
        {
            _root.transform.SetPositionAndRotation(new Vector3(5, -7, 11), Quaternion.Euler(60, 25, 0));
            _root.transform.localScale = new Vector3(-2, 3, 4);
            var detector = _root.AddComponent<OverlapDetector2D>();
            detector.Composite = new OverlapComposite2D(OverlapShape2D.Point(new Vector2(0.37f, -1.23f)));
            string path = _folder + "/ZOnly.prefab";
            var prefab = InstantiateOwnedPrefab(path);
            detector = _root.GetComponent<OverlapDetector2D>();
            var sentinel = PrepareUndoSentinelAndSaveScene();
            string serialized = EditorJsonUtility.ToJson(detector);
            byte[] bytes = File.ReadAllBytes(path);
            Vector3 moved = OverlapDetectorInspector.World2D(detector, 0).Center;
            moved.z = 1000;
            OverlapDetectorInspector.MoveCenter(detector, 0, moved);
            Undo.FlushUndoRecordObjects();
            Assert.That(EditorJsonUtility.ToJson(detector), Is.EqualTo(serialized));
            Assert.That(_scene.isDirty, Is.False);
            Assert.That(EditorUtility.IsDirty(prefab), Is.False);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            Undo.PerformUndo();
            Assert.That(sentinel.localPosition, Is.EqualTo(Vector3.zero), "A z-only drag must leave the prior real Undo operation at the top of the stack.");
            TestContext.WriteLine("Z-only handle: requested=" + moved.ToString("F8") + "; unchanged local=" + detector.Composite.GetShape(0).Center.ToString("F8") + "; serialized/sceneDirty/assetDirty/bytes/Undo guards passed.");
        }

        [TestCase(90, 0, 1, 1)]
        [TestCase(0, 90, 1, 1)]
        [TestCase(89.99999f, 0, 1, 1)]
        [TestCase(0, 89.99999f, 1, 1)]
        [TestCase(0, 0, 0, 1)]
        [TestCase(0, 0, 1, 0)]
        public void SingularProjectedPointHandleDiagnosesWithoutWritingOrAddingUndo(float tiltX, float tiltY, float scaleX, float scaleY)
        {
            _root.transform.rotation = Quaternion.Euler(tiltX, tiltY, 0);
            _root.transform.localScale = new Vector3(scaleX, scaleY, 1);
            var detector = _root.AddComponent<OverlapDetector2D>();
            detector.Composite = new OverlapComposite2D(OverlapShape2D.Point(Vector2.up));
            string path = _folder + "/Singular.prefab";
            var prefab = InstantiateOwnedPrefab(path);
            detector = _root.GetComponent<OverlapDetector2D>();
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Is.Empty, "A singular authoring inverse must not invalidate the supported runtime Point.");
            detector.StartDetection();
            Assert.That(detector.Sample(), Is.True);
            Assert.That(detector.LastQuery.Status, Is.EqualTo(OverlapQueryStatus.Complete));
            var sentinel = PrepareUndoSentinelAndSaveScene();
            string serialized = EditorJsonUtility.ToJson(detector);
            byte[] bytes = File.ReadAllBytes(path);
            byte[] meta = File.ReadAllBytes(path + ".meta");
            string guid = AssetDatabase.AssetPathToGUID(path);
            var error = Assert.Throws<InvalidOperationException>(() => OverlapDetectorInspector.MoveCenter(detector, 0, new Vector3(1, 2, 100)));
            Assert.That(error.Message, Does.Contain("projection"));
            Undo.FlushUndoRecordObjects();
            Assert.That(EditorJsonUtility.ToJson(detector), Is.EqualTo(serialized));
            Assert.That(_scene.isDirty, Is.False);
            Assert.That(EditorUtility.IsDirty(prefab), Is.False);
            using (var data = new SerializedObject(detector))
            {
                Assert.That(data.FindProperty("composite.shapes").GetArrayElementAtIndex(0).FindPropertyRelative("center").prefabOverride, Is.False);
            }
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
            Undo.PerformUndo();
            Assert.That(sentinel.localPosition, Is.EqualTo(Vector3.zero), "Rejected projection must not add an invalid geometry Undo operation.");
            TestContext.WriteLine("Rejected projection: tilt=" + tiltX + "," + tiltY + "; scale=" + scaleX + "," + scaleY + "; serialized/sceneDirty/assetDirty/GUID/bytes/Undo guards passed; runtimePoint=" + detector.LastQuery.Status);
        }

        [Test]
        public void ThreeDCapsuleHandleKeepsFullInverseAndTranslatesBothCapsWithUndo()
        {
            _root.transform.SetPositionAndRotation(new Vector3(7, -5, 11), Quaternion.Euler(30, 45, 20));
            _root.transform.localScale = new Vector3(-2, 2, 2);
            _root.AddComponent<OverlapDetector3D>().Composite = new OverlapComposite3D(
                OverlapShape3D.Capsule(Vector3.down, Vector3.up, 0.25f));
            string path = _folder + "/Capsule.prefab";
            var prefab = InstantiateOwnedPrefab(path);
            var detector = _root.GetComponent<OverlapDetector3D>();
            byte[] bytes = File.ReadAllBytes(path);
            Vector3 delta = new Vector3(2, -1, 3);
            Vector3 requested = _root.transform.localToWorldMatrix.MultiplyPoint3x4(delta);
            Undo.IncrementCurrentGroup();
            OverlapDetectorInspector.MoveCenter(detector, 0, requested);
            Undo.FlushUndoRecordObjects();
            Assert.That(Vector3.Distance(detector.Composite.GetShape(0).Point1, Vector3.down + delta), Is.LessThan(0.00001f));
            Assert.That(Vector3.Distance(detector.Composite.GetShape(0).Point2, Vector3.up + delta), Is.LessThan(0.00001f));
            Assert.That(Vector3.Distance(OverlapDetectorInspector.World3D(detector, 0).Center, requested), Is.LessThan(0.00001f));
            Undo.PerformUndo();
            Assert.That(detector.Composite.GetShape(0).Point1, Is.EqualTo(Vector3.down));
            Assert.That(detector.Composite.GetShape(0).Point2, Is.EqualTo(Vector3.up));
            Undo.PerformRedo();
            Assert.That(Vector3.Distance(detector.Composite.GetShape(0).Point1, Vector3.down + delta), Is.LessThan(0.00001f));
            Assert.That(Vector3.Distance(detector.Composite.GetShape(0).Point2, Vector3.up + delta), Is.LessThan(0.00001f));
            Assert.That(EditorUtility.IsDirty(prefab), Is.False);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            TestContext.WriteLine("3D full-inverse capsule handle: requested=" + requested.ToString("F8") + "; point1=" + detector.Composite.GetShape(0).Point1.ToString("F8") + "; point2=" + detector.Composite.GetShape(0).Point2.ToString("F8") + "; Undo/Redo and shared asset guards passed.");
        }

        private GameObject InstantiateOwnedPrefab(string path)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(_root, path);
            Assert.That(prefab, Is.Not.Null);
            Object.DestroyImmediate(_root);
            _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
            return prefab;
        }

        private Transform PrepareUndoSentinelAndSaveScene()
        {
            var sentinel = new GameObject("PriorRealUndo").transform;
            SceneManager.MoveGameObjectToScene(sentinel.gameObject, _scene);
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(sentinel, "Owned prior real operation");
            sentinel.localPosition = Vector3.right;
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
            Assert.That(EditorSceneManager.SaveScene(_scene, _folder + "/Owned.unity"), Is.True);
            Assert.That(_scene.isDirty, Is.False);
            return sentinel;
        }
    }
    public sealed class OverlapSectorAuthoringTests
    {
        private string _folder;
        private GameObject _root;

        [SetUp]
        public void Setup()
        {
            _folder = "Assets/__YLSectorAuthoring_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _folder.Substring(7));
            _root = new GameObject("OwnedSectorAuthoring");
        }

        [TearDown]
        public void Cleanup()
        {
            if (_root)
            {
                Object.DestroyImmediate(_root);
            }
            Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
        }

        private OverlapDetector2D Detector()
        {
            var shape = OverlapShape2D.Sector(new Vector2(1, 2), 3, 270, 1);
            var detector = _root.AddComponent<OverlapDetector2D>();
            detector.Composite = new OverlapComposite2D(shape);
            return detector;
        }

        [Test]
        public void AnnularSectorFieldsSupportPrefabOverrideUndoAndColdReimportWithoutRuntimeState()
        {
            Detector();
            string path = _folder + "/Sector.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(_root, path);
            byte[] original = File.ReadAllBytes(path);
            Object.DestroyImmediate(_root);
            _root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var detector = _root.GetComponent<OverlapDetector2D>();
            Undo.IncrementCurrentGroup();
            using (var data = new SerializedObject(detector))
            {
                var shape = data.FindProperty("composite.shapes").GetArrayElementAtIndex(0);
                shape.FindPropertyRelative("outerRadius").floatValue = 7;
                shape.FindPropertyRelative("innerRadius").floatValue = 2;
                shape.FindPropertyRelative("angleDegrees").floatValue = 180;
                shape.FindPropertyRelative("rotationDegrees").floatValue = 37;
                data.FindProperty("maxShapesPerCollider").intValue = 7;
                data.FindProperty("maxVerticesPerShape").intValue = 64;
                data.FindProperty("geometryTolerance").floatValue = 0.001f;
                data.ApplyModifiedProperties();
            }
            Undo.FlushUndoRecordObjects();
            Assert.That(PrefabUtility.GetPropertyModifications(_root), Has.Some.Matches<PropertyModification>(
                item => item.propertyPath.EndsWith(".innerRadius", StringComparison.Ordinal)));
            Undo.PerformUndo();
            using (var data = new SerializedObject(detector))
            {
                Assert.That(data.FindProperty("composite.shapes").GetArrayElementAtIndex(0).FindPropertyRelative("innerRadius").floatValue, Is.EqualTo(1));
            }
            Undo.PerformRedo();
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            string coldPath = _folder + "/Cold.prefab";
            Assert.That(PrefabUtility.SaveAsPrefabAsset(_root, coldPath), Is.Not.Null);
            Object.DestroyImmediate(_root);
            _root = null;
            AssetDatabase.ImportAsset(coldPath, ImportAssetOptions.ForceUpdate);
            var cold = PrefabUtility.LoadPrefabContents(coldPath);
            try
            {
                var reloaded = cold.GetComponent<OverlapDetector2D>();
                using var data = new SerializedObject(reloaded);
                var shape = data.FindProperty("composite.shapes").GetArrayElementAtIndex(0);
                Assert.That(shape.FindPropertyRelative("outerRadius").floatValue, Is.EqualTo(7));
                Assert.That(shape.FindPropertyRelative("innerRadius").floatValue, Is.EqualTo(2));
                Assert.That(shape.FindPropertyRelative("angleDegrees").floatValue, Is.EqualTo(180));
                Assert.That(shape.FindPropertyRelative("rotationDegrees").floatValue, Is.EqualTo(37));
                Assert.That(reloaded.MaxShapesPerCollider, Is.EqualTo(7));
                Assert.That(reloaded.MaxVerticesPerShape, Is.EqualTo(64));
                Assert.That(reloaded.GeometryTolerance, Is.EqualTo(0.001f));
                Assert.That(OverlapDetectorInspector.ValidateConfiguration(reloaded), Is.Empty);
                Assert.That(reloaded.BufferCapacity, Is.Zero);
                string json = JsonUtility.ToJson(reloaded.Composite);
                Assert.That(json, Does.Not.Contain("instanceID"));
                Assert.That(json, Does.Not.Contain("workspace"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(cold);
            }
        }

        [Test]
        public void AnnularSectorWorldConversionAcceptsUniformMirrorsButRejectsEllipseAndShear()
        {
            var detector = Detector();
            _root.transform.SetPositionAndRotation(new Vector3(8, -2, 6), Quaternion.Euler(0, 0, 90));
            _root.transform.localScale = new Vector3(-2, 2, 1);
            var world = OverlapDetectorInspector.World2D(detector, 0);
            Assert.That(Vector2.Distance(world.Center, new Vector2(4, -4)), Is.LessThan(0.0001f));
            Assert.That(world.Radius, Is.EqualTo(6).Within(0.00001f));
            Assert.That(world.Angle, Is.EqualTo(-90).Within(0.0001f));
            _root.transform.localScale = new Vector3(2, 3, 1);
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Is.Not.Empty);
            _root.transform.rotation = Quaternion.Euler(20, 0, 0);
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Is.Not.Empty);
        }

        [Test]
        public void SectorPreviewUsesOuterCircleCandidatesAndIndependentWorkspaceAndDiagnosesCast()
        {
            var detector = Detector();
            detector.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 });
            foreach (var position in new[] { new Vector3(1, 2), new Vector3(3, 2) })
            {
                var child = new GameObject("OwnedPreviewTarget");
                child.transform.SetParent(_root.transform, false);
                child.transform.position = position;
                child.layer = 11;
                child.AddComponent<CircleCollider2D>().radius = 0.1f;
            }
            Physics2D.SyncTransforms();
            var previous = Selection.objects;
            var inspector = (OverlapDetectorInspector)UnityEditor.Editor.CreateEditor(detector);
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
            OverlapQueryWorkspace2D preview = null;
            try
            {
                Selection.activeGameObject = _root;
                string serialized = EditorJsonUtility.ToJson(detector);
                typeof(OverlapDetectorInspector).GetField("_cast", Flags).SetValue(inspector, true);
                inspector.SamplePreview();
                Assert.That(typeof(OverlapDetectorInspector).GetField("_rawCount", Flags).GetValue(inspector), Is.EqualTo(2));
                Assert.That(typeof(OverlapDetectorInspector).GetField("_acceptedCount", Flags).GetValue(inspector), Is.EqualTo(1));
                Assert.That((string)typeof(OverlapDetectorInspector).GetField("_previewStatus", Flags).GetValue(inspector), Does.Contain("UnsupportedCast"));
                preview = (OverlapQueryWorkspace2D)typeof(OverlapDetectorInspector).GetField("_workspace2D", Flags).GetValue(inspector);
                Assert.That(preview, Is.Not.Null);
                inspector.SamplePreview();
                Assert.That(typeof(OverlapDetectorInspector).GetField("_workspace2D", Flags).GetValue(inspector), Is.SameAs(preview));
                Assert.That(detector.BufferCapacity, Is.Zero);
                Assert.That(detector.LastComplete, Is.Empty);
                Assert.That(EditorJsonUtility.ToJson(detector), Is.EqualTo(serialized));
            }
            finally
            {
                Object.DestroyImmediate(inspector);
                Selection.objects = previous;
            }
            Assert.Throws<ObjectDisposedException>(() => OverlapQuery2D.Query(OverlapShape2D.Sector(Vector2.zero, 3, 90),
                detector.Filter, new Collider2D[8], preview));
        }

        [Test]
        public void InvalidSectorWorkspaceBudgetIsVisibleBeforeAnyNativeQuery()
        {
            var detector = Detector();
            using var data = new SerializedObject(detector);
            data.FindProperty("maxVerticesPerShape").intValue = 0;
            data.ApplyModifiedProperties();
            Assert.That(OverlapDetectorInspector.ValidateConfiguration(detector), Does.Contain("maxVerticesPerShape"));
            Assert.That(detector.Sample(), Is.False);
            Assert.That(detector.LastQuery.Status, Is.EqualTo(OverlapQueryStatus.Invalidated));
            Assert.That(detector.BufferCapacity, Is.Zero);
        }
    }
    public sealed class OverlapSectorNumericsTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SectorRelativeCoordinateKernelCancelsSharedTranslationAndRejectsRemainingLostTerms(int layout)
        {
            double extent = Math.Pow(2, 56);
            double first = 2;
            double second = 0;
            double translation = extent;
            double center = extent;
            if (layout == 1)
            {
                const float RotationComponent = 0.70710677f;
                first = RotationComponent * extent;
                second = -(double)RotationComponent * 2;
                translation = 0;
                center = (float)first;
            }
            else if (layout == 2)
            {
                first = extent;
                translation = 0;
            }
            var arguments = new object[] { first, second, translation, center, 0.00001d, 0d };
            var method = typeof(OverlapQueryWorkspace2D).GetMethod("TryRelativeCoordinate", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            bool reliable = (bool)method.Invoke(null, arguments);
            double coordinate = (double)arguments[5];
            TestContext.WriteLine("FIX1 I2 pure coordinate layout=" + layout + "; first=" + first.ToString("R") +
                "; second=" + second.ToString("R") + "; translation=" + translation.ToString("R") +
                "; center=" + center.ToString("R") + "; coordinate=" + coordinate.ToString("R") + "; reliable=" + reliable);
            Assert.That(reliable, Is.EqualTo(layout != 1));
            if (reliable)
            {
                Assert.That(coordinate, Is.EqualTo(layout == 0 ? 2 : 0));
            }
            else
            {
                Assert.That(first, Is.EqualTo(center));
                Assert.That(Math.Abs(second), Is.GreaterThan(1), "The discarded finite term is not harmless tolerance noise.");
            }
        }
    }
}
#endif

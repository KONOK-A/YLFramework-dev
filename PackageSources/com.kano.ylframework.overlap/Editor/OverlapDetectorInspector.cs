using System;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Editor.Common;
using YLFramework.Overlap;

[assembly: InternalsVisibleTo("YLFramework.Overlap.Tests.EditMode")]

namespace YLFramework.Editor.Overlap
{
    [CustomEditor(typeof(OverlapDetector2D)), CanEditMultipleObjects]
    internal sealed class OverlapDetector2DInspector : OverlapDetectorInspector
    {
    }

    [CustomEditor(typeof(OverlapDetector3D)), CanEditMultipleObjects]
    internal sealed class OverlapDetector3DInspector : OverlapDetectorInspector
    {
    }

    internal abstract class OverlapDetectorInspector : UnityEditor.Editor
    {
        internal readonly struct Diagnostics
        {
            internal readonly int Count;
            internal readonly int RawCount;
            internal readonly int LastCompleteCount;
            internal readonly int Capacity;
            internal readonly OverlapQueryStatus Status;
            internal readonly OverlapQueryInvalidationReason Reason;
            internal readonly string Error;

            internal Diagnostics(int count, int rawCount, int lastComplete, int capacity,
                OverlapQueryStatus status, OverlapQueryInvalidationReason reason, string error)
            {
                Count = count;
                RawCount = rawCount;
                LastCompleteCount = lastComplete;
                Capacity = capacity;
                Status = status;
                Reason = reason;
                Error = error;
            }
        }

        private int _child;
        private int _previewCapacity = 32;
        private bool _cast;
        private Vector3 _direction = Vector3.right;
        private float _distance = 3;
        private bool _autoSample;
        private float _previewFrequency = 2;
        private double _nextSample;
        private bool _showContacts;
        private string _previewStatus = "Not sampled. Preview is independent of runtime contacts.";
        private Collider2D[] _raw2D;
        private Collider2D[] _accepted2D;
        private RaycastHit2D[] _hits2D;
        private Collider[] _raw3D;
        private Collider[] _accepted3D;
        private RaycastHit[] _hits3D;
        private int _rawCount;
        private int _acceptedCount;
        private int _hitCount;
        private const string ProjectionError = "2D center handle disabled: the local XY to world XY projection is singular or numerically unstable. Point queries remain valid; edit the local center in the Inspector.";
        private bool TwoD => target is OverlapDetector2D;
        private Component Detector => (Component)target;

        private void OnEnable() => EditorApplication.update += AutoSample;

        private void OnDisable()
        {
            EditorApplication.update -= AutoSample;
            ClearPreview();
        }

        internal static Diagnostics ReadDiagnostics(Component detector)
        {
            if (detector is OverlapDetector2D two)
            {
                var query = two.LastQuery;
                return new Diagnostics(query.Count, query.RawCount, two.LastComplete.Count, two.BufferCapacity,
                    query.Status, query.InvalidationReason, two.ConfigurationError);
            }
            var three = (OverlapDetector3D)detector;
            var result = three.LastQuery;
            return new Diagnostics(result.Count, result.RawCount, three.LastComplete.Count, three.BufferCapacity,
                result.Status, result.InvalidationReason, three.ConfigurationError);
        }

        internal static OverlapShape2D World2D(OverlapDetector2D detector, int index) =>
            detector.Composite.ToWorld(index, detector.transform.localToWorldMatrix);

        internal static OverlapShape3D World3D(OverlapDetector3D detector, int index) =>
            detector.Composite.ToWorld(index, detector.transform.localToWorldMatrix);

        internal static string ValidateConfiguration(Component detector)
        {
            try
            {
                using var data = new SerializedObject(detector);
                OverlapDetectionGeometry.ValidateFrequency(data.FindProperty("frequency").floatValue);
                OverlapDetectionGeometry.ValidateMode((OverlapSamplingMode)data.FindProperty("samplingMode").intValue);
                OverlapDetectionGeometry.ValidateCapacity(data.FindProperty("initialCapacity").intValue,
                    data.FindProperty("maxCapacity").intValue);
                if (detector is OverlapDetector2D two)
                {
                    _ = two.Filter;
                    for (int i = 0; i < two.Composite.Count; i++)
                    {
                        World2D(two, i);
                    }
                    if (two.Filter.ContactFilter.useTriggers && !Physics2D.queriesHitTriggers)
                    {
                        return "TriggerQueriesDisabled: project-wide 2D trigger queries are disabled. This tool never overrides the global setting.";
                    }
                }
                else
                {
                    var three = (OverlapDetector3D)detector;
                    _ = three.Filter;
                    for (int i = 0; i < three.Composite.Count; i++)
                    {
                        World3D(three, i);
                    }
                }
                return string.Empty;
            }
            catch (ArgumentException error)
            {
                return error.Message;
            }
            catch (InvalidOperationException error)
            {
                return error.Message;
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(EditorUtility.IsPersistent(target)))
            {
                DrawPropertiesExcluding(serializedObject, "m_Script");
                if (serializedObject.ApplyModifiedProperties())
                {
                    ClearPreview();
                }
            }
            if (targets.Length != 1)
            {
                EditorGUILayout.HelpBox(EditorUtility.IsPersistent(target) ?
                    "Shared prefab assets are read-only. Select scene instances or open Prefab Mode to author settings." :
                    "Multi-edit applies serialized settings only. Handles, contacts and explicit preview require one selected scene instance.", MessageType.Info);
                return;
            }
            if (EditorUtility.IsPersistent(target))
            {
                EditorGUILayout.HelpBox("Shared prefab asset is read-only here. Open Prefab Mode or select an instance to author geometry.", MessageType.Info);
                return;
            }
            string error = ValidateConfiguration(Detector);
            if (error.Length != 0)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
            var diagnostic = ReadDiagnostics(Detector);
            EditorGUILayout.LabelField("Actual Runtime State", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Running / sampling", TwoD ?
                ((OverlapDetector2D)target).IsRunning + " / " + ((OverlapDetector2D)target).IsSampling :
                ((OverlapDetector3D)target).IsRunning + " / " + ((OverlapDetector3D)target).IsSampling);
            EditorGUILayout.LabelField("Latest query (default before first sample)", diagnostic.Status + " / " + diagnostic.Reason);
            EditorGUILayout.LabelField("Count / RawCount", diagnostic.Count + " / " + diagnostic.RawCount);
            EditorGUILayout.LabelField("Allocated buffer", diagnostic.Capacity.ToString());
            EditorGUILayout.LabelField("Last complete contacts (borrowed)", diagnostic.LastCompleteCount.ToString());
            EditorGUILayout.HelpBox("LastComplete is the last committed baseline, not a claim that the latest partial/invalid observation is complete. RawCount includes repeated Union children; RawCount - Count is not a rejected-collider count.", MessageType.Info);
            if (!string.IsNullOrEmpty(diagnostic.Error))
            {
                EditorGUILayout.HelpBox(diagnostic.Error, MessageType.Error);
            }
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Sample Running Detector Once"))
                {
                    if (TwoD)
                        ((OverlapDetector2D)target).Sample();
                    else
                        ((OverlapDetector3D)target).Sample();
                }
            }
            _showContacts = EditorGUILayout.Foldout(_showContacts, "Borrowed last-complete targets", true);
            if (_showContacts)
            {
                if (TwoD)
                {
                    foreach (var contact in ((OverlapDetector2D)target).LastComplete)
                    {
                        EditorGUILayout.ObjectField("Identity " + contact.Identity, contact.Target, typeof(UnityEngine.Object), true);
                    }
                }
                else
                {
                    foreach (var contact in ((OverlapDetector3D)target).LastComplete)
                    {
                        EditorGUILayout.ObjectField("Identity " + contact.Identity, contact.Target, typeof(UnityEngine.Object), true);
                    }
                }
            }
            EditorGUILayout.LabelField("Selected-Child Preview (Editor Only)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _child = EditorGUILayout.IntField("Child / center handle", _child);
            if (TwoD && error.Length == 0 && _child >= 0 && _child < ((OverlapDetector2D)target).Composite.Count &&
                !TryLocalCenter2D(Detector.transform.localToWorldMatrix, World2D((OverlapDetector2D)target, _child).Center, out _))
            {
                EditorGUILayout.HelpBox(ProjectionError, MessageType.Warning);
            }
            _previewCapacity = EditorGUILayout.IntField("Caller buffer capacity", _previewCapacity);
            _cast = EditorGUILayout.Toggle("Draw / sample Cast", _cast);
            _direction = EditorGUILayout.Vector3Field("World Cast direction (2D: XY)", _direction);
            _distance = EditorGUILayout.FloatField("World Cast distance", _distance);
            if (EditorGUI.EndChangeCheck())
            {
                ClearPreview();
                SceneView.RepaintAll();
            }
            _autoSample = EditorGUILayout.Toggle("Opt-in selected auto sample", _autoSample);
            _previewFrequency = EditorGUILayout.FloatField("Preview samples / second", _previewFrequency);
            if (!OverlapShape3D.IsFinite(_previewFrequency) || _previewFrequency <= 0)
            {
                EditorGUILayout.HelpBox("Preview frequency must be finite and positive; auto sampling is suspended.", MessageType.Error);
            }
            using (new EditorGUI.DisabledScope(error.Length != 0 || !Selection.Contains(Detector.gameObject)))
            {
                if (GUILayout.Button("Sample Selected Child / Cast Once"))
                {
                    SamplePreview();
                }
            }
            EditorGUILayout.HelpBox("Last explicit preview (not live runtime state): " + _previewStatus, MessageType.Info);
            EditorGUILayout.LabelField("Raw uses native layer/trigger/depth policy; orange means ignored or body-grouped, not an exhaustive scene scan.", EditorStyles.wordWrappedLabel);
            for (int i = 0; i < _rawCount; i++)
            {
                UnityEngine.Object item = TwoD ? _raw2D[i] : (UnityEngine.Object)_raw3D[i];
                bool accepted = false;
                for (int j = 0; j < _acceptedCount; j++)
                {
                    accepted |= item == (TwoD ? _accepted2D[j] : (UnityEngine.Object)_accepted3D[j]);
                }
                EditorGUILayout.ObjectField(accepted ? "Accepted" : "Ignored / grouped", item, typeof(UnityEngine.Object), true);
            }
        }

        private void AutoSample()
        {
            if (!_autoSample || targets.Length != 1 || !target || EditorUtility.IsPersistent(target) ||
                !Selection.Contains(Detector.gameObject) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                !OverlapShape3D.IsFinite(_previewFrequency) || _previewFrequency <= 0 || EditorApplication.timeSinceStartup < _nextSample)
            {
                return;
            }
            _nextSample = EditorApplication.timeSinceStartup + 1d / _previewFrequency;
            SamplePreview();
            Repaint();
            SceneView.RepaintAll();
        }

        internal void SamplePreview()
        {
            ClearPreview();
            if (targets.Length != 1 || EditorUtility.IsPersistent(target) || !Selection.Contains(Detector.gameObject))
            {
                _previewStatus = "Select one scene or Prefab Mode instance before explicitly sampling.";
                return;
            }
            string error = ValidateConfiguration(Detector);
            if (error.Length != 0)
            {
                _previewStatus = "Invalidated: " + error;
                return;
            }
            try
            {
                if (_previewCapacity <= 0)
                {
                    throw new ArgumentOutOfRangeException("previewCapacity");
                }
                if (TwoD)
                {
                    var detector = (OverlapDetector2D)target;
                    var shape = World2D(detector, _child);
                    var filter = detector.Filter;
                    if (_raw2D == null || _raw2D.Length != _previewCapacity)
                    {
                        _raw2D = new Collider2D[_previewCapacity];
                        _accepted2D = new Collider2D[_previewCapacity];
                        _hits2D = new RaycastHit2D[_previewCapacity];
                    }
                    var scene = detector.gameObject.scene.GetPhysicsScene2D();
                    var raw = OverlapQuery2D.Query(scene, shape, new OverlapFilter2D(filter.ContactFilter), _raw2D);
                    var accepted = OverlapQuery2D.Query(scene, shape, filter, _accepted2D);
                    _rawCount = raw.Count;
                    _acceptedCount = accepted.Count;
                    _previewStatus = accepted.Status + " / " + accepted.InvalidationReason + "; Count/RawCount " + accepted.Count + "/" + accepted.RawCount;
                    if (_cast)
                    {
                        var cast = OverlapQuery2D.Cast(scene, shape, _direction, _distance, filter, _hits2D);
                        _hitCount = cast.Count;
                        _previewStatus += "; Cast " + cast.Status + " " + cast.Count + "/" + cast.RawCount;
                    }
                }
                else
                {
                    var detector = (OverlapDetector3D)target;
                    var shape = World3D(detector, _child);
                    var filter = detector.Filter;
                    if (_raw3D == null || _raw3D.Length != _previewCapacity)
                    {
                        _raw3D = new Collider[_previewCapacity];
                        _accepted3D = new Collider[_previewCapacity];
                        _hits3D = new RaycastHit[_previewCapacity];
                    }
                    var scene = detector.gameObject.scene.GetPhysicsScene();
                    var raw = OverlapQuery3D.Query(scene, shape, new OverlapFilter3D(filter.LayerMask, filter.TriggerInteraction), _raw3D);
                    var accepted = OverlapQuery3D.Query(scene, shape, filter, _accepted3D);
                    _rawCount = raw.Count;
                    _acceptedCount = accepted.Count;
                    _previewStatus = accepted.Status + "; Count/RawCount " + accepted.Count + "/" + accepted.RawCount;
                    if (_cast)
                    {
                        var cast = OverlapQuery3D.Cast(scene, shape, _direction, _distance, filter, _hits3D);
                        _hitCount = cast.Count;
                        _previewStatus += "; Cast " + cast.Status + " " + cast.Count + "/" + cast.RawCount;
                    }
                }
            }
            catch (ArgumentException failure)
            {
                _previewStatus = "Invalidated: " + failure.Message;
            }
            catch (IndexOutOfRangeException)
            {
                _previewStatus = "Invalidated: select an existing Union child.";
            }
        }

        private void ClearPreview()
        {
            _rawCount = _acceptedCount = _hitCount = 0;
            foreach (var array in new Array[] { _raw2D, _accepted2D, _hits2D, _raw3D, _accepted3D, _hits3D })
            {
                if (array != null)
                    Array.Clear(array, 0, array.Length);
            }
            _previewStatus = "Not sampled (or preview inputs changed).";
        }

        internal static void MoveCenter(Component detector, int index, Vector3 world)
        {
            if (EditorUtility.IsPersistent(detector))
            {
                throw new InvalidOperationException("Do not edit shared prefab assets through a scene handle.");
            }
            Vector3 local;
            if (detector is OverlapDetector2D two)
            {
                if (!TryLocalCenter2D(detector.transform.localToWorldMatrix, world, out var projected))
                {
                    throw new InvalidOperationException(ProjectionError);
                }
                if (((Vector2)world).Equals(World2D(two, index).Center))
                {
                    return;
                }
                local = projected;
            }
            else
            {
                local = detector.transform.worldToLocalMatrix.MultiplyPoint3x4(world);
            }
            using var data = new SerializedObject(detector);
            data.Update();
            var shape = data.FindProperty("composite.shapes").GetArrayElementAtIndex(index);
            if (detector is OverlapDetector2D)
            {
                shape.FindPropertyRelative("center").vector2Value = local;
            }
            else if ((OverlapShapeKind3D)shape.FindPropertyRelative("kind").intValue == OverlapShapeKind3D.Capsule)
            {
                var first = shape.FindPropertyRelative("point1");
                var second = shape.FindPropertyRelative("point2");
                Vector3 delta = local - (first.vector3Value * 0.5f + second.vector3Value * 0.5f);
                first.vector3Value += delta;
                second.vector3Value += delta;
            }
            else
            {
                shape.FindPropertyRelative("center").vector3Value = local;
            }
            data.ApplyModifiedProperties();
        }

        private static bool TryLocalCenter2D(Matrix4x4 matrix, Vector2 world, out Vector2 local)
        {
            local = default;
            double a = matrix.m00, b = matrix.m01, c = matrix.m10, d = matrix.m11;
            double determinant = a * d - b * c;
            double norm = Math.Max(Math.Abs(a) + Math.Abs(b), Math.Abs(c) + Math.Abs(d));
            double adjugateNorm = Math.Max(Math.Abs(d) + Math.Abs(b), Math.Abs(c) + Math.Abs(a));
            // Relative inverse conditioning for float matrix inputs, independent of world scale.
            const double tolerance = 8d * 1.1920928955078125e-7d;
            if (!(Math.Abs(determinant) > tolerance * norm * adjugateNorm))
            {
                return false;
            }
            double x = (double)world.x - matrix.m03;
            double y = (double)world.y - matrix.m13;
            local = new Vector2((float)((d * x - b * y) / determinant), (float)((a * y - c * x) / determinant));
            return OverlapShape3D.IsFinite(local.x) && OverlapShape3D.IsFinite(local.y);
        }

        private void OnSceneGUI()
        {
            if (targets.Length != 1 || EditorUtility.IsPersistent(target) || !Selection.Contains(Detector.gameObject) || ValidateConfiguration(Detector).Length != 0)
            {
                return;
            }
            int count = TwoD ? ((OverlapDetector2D)target).Composite.Count : ((OverlapDetector3D)target).Composite.Count;
            if (_child < 0 || _child >= count)
                return;
            Vector3 center = TwoD ? (Vector3)World2D((OverlapDetector2D)target, _child).Center : World3D((OverlapDetector3D)target, _child).Center;
            if (TwoD && !TryLocalCenter2D(Detector.transform.localToWorldMatrix, center, out _))
            {
                Handles.Label(center, ProjectionError);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(center, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    MoveCenter(Detector, _child, moved);
                    ClearPreview();
                }
            }
            using var scope = new Handles.DrawingScope(Color.cyan);
            Vector3 direction = TwoD ? new Vector3(_direction.x, _direction.y, 0) : _direction;
            if (_cast && OverlapShape3D.IsFinite(_distance) && _distance >= 0 && direction.sqrMagnitude > 0 &&
                OverlapShape3D.IsFinite(direction.sqrMagnitude))
            {
                Vector3 offset = direction.normalized * _distance;
                Handles.DrawDottedLine(center, center + offset, 4);
                if (TwoD)
                    Draw(World2D((OverlapDetector2D)target, _child), offset);
                else
                    Draw(World3D((OverlapDetector3D)target, _child), offset);
                Handles.Label(center + offset, "Explicit child Cast end (not a compound Cast)");
            }
            for (int i = 0; i < _hitCount; i++)
            {
                Vector3 point = TwoD ? (Vector3)_hits2D[i].point : _hits3D[i].point;
                Vector3 normal = TwoD ? (Vector3)_hits2D[i].normal : _hits3D[i].normal;
                float distance = TwoD ? _hits2D[i].distance : _hits3D[i].distance;
                Handles.DrawLine(point, point + normal * 0.3f);
                Handles.Label(point, "Native hit d=" + distance);
            }
            for (int i = 0; i < _rawCount; i++)
            {
                bool accepted = false;
                for (int j = 0; j < _acceptedCount; j++)
                {
                    accepted |= TwoD ? _raw2D[i] == _accepted2D[j] : _raw3D[i] == _accepted3D[j];
                }
                using var color = new Handles.DrawingScope(accepted ? Color.green : new Color(1, 0.5f, 0));
                if (TwoD && _raw2D[i])
                    Handles.DrawWireCube(_raw2D[i].bounds.center, _raw2D[i].bounds.size);
                else if (!TwoD && _raw3D[i])
                    Handles.DrawWireCube(_raw3D[i].bounds.center, _raw3D[i].bounds.size);
            }
        }

        [DrawGizmo(GizmoType.Selected)]
        private static void DrawSelected2D(OverlapDetector2D detector, GizmoType _) => DrawSelected(detector);

        [DrawGizmo(GizmoType.Selected)]
        private static void DrawSelected3D(OverlapDetector3D detector, GizmoType _) => DrawSelected(detector);

        private static void DrawSelected(Component detector)
        {
            string error = ValidateConfiguration(detector);
            if (error.Length != 0)
            {
                Handles.Label(detector.transform.position, "Invalid geometry/configuration: " + error);
                return;
            }
            using var scope = new Handles.DrawingScope(Color.green, Matrix4x4.identity);
            if (detector is OverlapDetector2D two)
            {
                for (int i = 0; i < two.Composite.Count; i++)
                    Draw(World2D(two, i), Vector3.zero);
            }
            else
            {
                var three = (OverlapDetector3D)detector;
                for (int i = 0; i < three.Composite.Count; i++)
                    Draw(World3D(three, i), Vector3.zero);
            }
        }

        private static void Draw(OverlapShape2D shape, Vector3 offset)
        {
            using var scope = new Handles.DrawingScope(Matrix4x4.TRS((Vector3)shape.Center + offset,
                Quaternion.Euler(0, 0, shape.Angle), Vector3.one));
            switch (shape.Kind)
            {
                case OverlapShapeKind2D.Point:
                    Handles.Label(Vector3.zero, "Point");
                    break;
                case OverlapShapeKind2D.Circle:
                    Handles.DrawWireDisc(Vector3.zero, Vector3.forward, shape.Radius);
                    break;
                case OverlapShapeKind2D.Box:
                    Handles.DrawWireCube(Vector3.zero, shape.Size);
                    break;
                case OverlapShapeKind2D.Capsule:
                    HandlesExtensions.DrawCapsule2D(Vector3.zero, shape.Size, shape.CapsuleDirection);
                    break;
            }
        }

        private static void Draw(OverlapShape3D shape, Vector3 offset)
        {
            if (shape.Kind == OverlapShapeKind3D.Box)
            {
                using var scope = new Handles.DrawingScope(Matrix4x4.TRS(shape.Center + offset, shape.Orientation, Vector3.one));
                Handles.DrawWireCube(Vector3.zero, shape.Size);
                return;
            }
            if (shape.Kind == OverlapShapeKind3D.Sphere)
            {
                Sphere(shape.Center + offset, shape.Radius);
                return;
            }
            Vector3 first = shape.Point1 + offset;
            Vector3 second = shape.Point2 + offset;
            Vector3 axis = second - first;
            if (axis.sqrMagnitude == 0)
            {
                Sphere(first, shape.Radius);
                return;
            }
            axis.Normalize();
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 other = Vector3.Cross(axis, side);
            Handles.DrawWireDisc(first, axis, shape.Radius);
            Handles.DrawWireDisc(second, axis, shape.Radius);
            foreach (var radial in new[] { side, -side, other, -other })
            {
                Handles.DrawLine(first + radial * shape.Radius, second + radial * shape.Radius);
            }
            Handles.DrawWireArc(first, side, other, -180, shape.Radius);
            Handles.DrawWireArc(first, other, side, 180, shape.Radius);
            Handles.DrawWireArc(second, side, other, 180, shape.Radius);
            Handles.DrawWireArc(second, other, side, -180, shape.Radius);
        }

        private static void Sphere(Vector3 center, float radius)
        {
            Handles.DrawWireDisc(center, Vector3.up, radius);
            Handles.DrawWireDisc(center, Vector3.right, radius);
            Handles.DrawWireDisc(center, Vector3.forward, radius);
        }
    }
}

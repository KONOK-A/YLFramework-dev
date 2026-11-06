using System;
using UnityEditor;
using UnityEngine;

namespace YLFramework.RedDots.Editor
{
    /// <summary>Read-only, opt-in diagnostic viewer. Never creates a runtime host or edits the graph.</summary>
    public sealed class ReddotDebugWindow : EditorWindow
    {
        private const double SampleInterval = 0.25;
        [SerializeField] private bool autoSample;
        private string _filter = string.Empty;
        private Vector2 _scroll;
        private double _nextSample;
        private double _sampleTime;
        private ReddotDiagnosticSnapshot _snapshot;

        [MenuItem("YLFramework/Red Dots/Diagnostics")]
        private static void Open() => GetWindow<ReddotDebugWindow>("Red Dots");

        private void OnEnable()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
            _nextSample = 0;
        }

        private void OnDisable() => EditorApplication.update -= OnEditorUpdate;
        private void OnDestroy() => EditorApplication.update -= OnEditorUpdate;

        private void OnEditorUpdate()
        {
            if ((!autoSample && focusedWindow != this) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < _nextSample)
            {
                return;
            }
            Sample();
        }

        private void Sample()
        {
            _snapshot = ReddotManager.CaptureDiagnostics();
            _sampleTime = EditorApplication.timeSinceStartup;
            _nextSample = _sampleTime + SampleInterval;
            Repaint();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                autoSample = GUILayout.Toggle(autoSample, "Auto Sample When Unfocused", EditorStyles.toolbarButton);
                using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
                {
                    if (GUILayout.Button("Capture", EditorStyles.toolbarButton))
                    {
                        Sample();
                    }
                }
                _filter = GUILayout.TextField(_filter, EditorStyles.toolbarSearchField);
            }
            EditorGUILayout.HelpBox("Read only. Samples at most 4 Hz while focused or with Auto Sample enabled. " +
                "No implicit root, runtime host, source labels or persistence is created.", MessageType.Info);
            if (_snapshot == null)
            {
                EditorGUILayout.LabelField("Focus this window or capture explicitly.");
                return;
            }
            EditorGUILayout.LabelField($"Generation {_snapshot.Generation} | capture {_snapshot.CaptureSequence} | sampled {_sampleTime:F2}s");
            EditorGUILayout.LabelField($"Nodes {_snapshot.Nodes.Count} | global subscribers {_snapshot.GlobalSubscriberCount} | " +
                $"dirty {_snapshot.DirtyNodeCount} | batch {_snapshot.BatchDepth} | initial {_snapshot.PendingInitialCount}");
            EditorGUILayout.LabelField($"Pending global {_snapshot.GlobalPending} | flushing {_snapshot.IsFlushing}");
            using var scroll = new EditorGUILayout.ScrollViewScope(_scroll);
            _scroll = scroll.scrollPosition;
            foreach (var node in _snapshot.Nodes)
            {
                if (_filter.Length != 0 && node.Path.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var depth = 0;
                foreach (var character in node.Path)
                {
                    if (character == '/')
                    {
                        depth++;
                    }
                }
                using var row = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
                var previousIndent = EditorGUI.indentLevel;
                try
                {
                    EditorGUI.indentLevel = depth - 1;
                    EditorGUILayout.LabelField(node.Path, $"value {node.Value} | {node.Aggregation}");
                    EditorGUILayout.LabelField($"Own {node.OwnValue} | collection {(node.HasPath ? 1 : 0)} | " +
                        $"leases {node.LeaseCount} | numeric sources {node.Sources.Count} | subscribers {node.SubscriberCount} | dirty {node.IsDirty}");
                    EditorGUI.indentLevel++;
                    foreach (var source in node.Sources)
                    {
                        EditorGUILayout.LabelField("source [" + source.SourceId + "]", source.Value.ToString());
                    }
                }
                finally
                {
                    EditorGUI.indentLevel = previousIndent;
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>官方配置的校验与导航增强；打开或绘制窗口不会创建、修复或保存配置。</summary>
    public sealed class ResourceWorkbenchWindow : EditorWindow
    {
        [SerializeField] private AddressableAssetSettings settings;
        [SerializeField] private string referenceDirectory = ResourceReferenceOutput.DirectoryPath;
        private string _message;
        private MessageType _messageType;
        private Vector2 _scroll;
        private int _selectedTab;
        private Vector2 _diagnosticsScroll;
        private string _diagnosticsFilter = "";
        private ResourceDiagnosticsReport _snapshot;
        private IReadOnlyList<ResourceDiagnosticsReport> _history;
        private bool _showHistory;

        private long _displayRevision = -1;
        internal string[] DisplayedEvents { get; private set; } = Array.Empty<string>();

        [MenuItem("YLFramework/Resource/Record Next Play")]
        internal static void ArmNextPlay() => SessionState.SetBool(ResourceEventHistory.NextPlayFlag, true);

        [MenuItem("YLFramework/Resource/Record Next Play", true)]
        private static bool CanArmNextPlay() => !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("YLFramework/Resource/Workbench %#r")]
        public static void OpenWorkbench()
        {
            var window = GetWindow<ResourceWorkbenchWindow>("YL Addressables");
            window.minSize = new Vector2(540, 360);
            window.settings = Selection.activeObject as AddressableAssetSettings ?? AddressableAssetSettingsDefaultObject.GetSettings(false);
            if (EditorApplication.isPlaying && (ResourceEventHistory.IsRecording || ResourceEventHistory.Count > 0))
                window._selectedTab = 1;
            window.Show();
        }

        private void OnGUI()
        {
            _selectedTab = GUILayout.Toolbar(_selectedTab, new[] { "Authoring", "Runtime Diagnostics" });
            if (_selectedTab == 1)
            {
                DrawDiagnostics();
                return;
            }

            using var scroll = new EditorGUILayout.ScrollViewScope(_scroll);
            _scroll = scroll.scrollPosition;
            GUILayout.Label("Addressables Authoring", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Settings, Groups, Labels, Profiles and AssetReference are the only authoring authority. This window never builds a private Catalog or publishes snapshots.", MessageType.Info);
            settings = (AddressableAssetSettings)EditorGUILayout.ObjectField("Official Settings", settings, typeof(AddressableAssetSettings), false);
            if (GUILayout.Button("Open Official Addressables Groups"))
            {
                EditorApplication.ExecuteMenuItem("Window/Asset Management/Addressables/Groups");
            }
            if (settings && GUILayout.Button("Select Official Settings / Profiles"))
            {
                Selection.activeObject = settings;
                EditorGUIUtility.PingObject(settings);
            }
            if (GUILayout.Button("Validate Official Settings (Read Only)"))
            {
                Run(() =>
                {
                    var publication = ResourceAddressablesAuthoring.Inspect(settings, validateStartup: true);
                    _messageType = publication.Errors.Count != 0 ? MessageType.Error
                        : publication.Warnings.Count != 0 ? MessageType.Warning : MessageType.Info;
                    var messages = publication.Errors.Count != 0 ? publication.Errors
                        : new List<string> { "Authoring validation passed. Content build and Player validation have not run." };
                    _message = string.Join("\n", messages.Concat(publication.Warnings));
                });
            }
            referenceDirectory = EditorGUILayout.TextField("Reference Directory", referenceDirectory);
            if (GUILayout.Button("Generate Official Address / Label Constants"))
            {
                Run(() =>
                {
                    var changed = ResourceCodeGenerator.GenerateReferences(settings, referenceDirectory);
                    _message = changed ? "Generated source updated. Unity compilation is a separate check." : "Generated source is unchanged.";
                    _messageType = MessageType.Info;
                    if (changed)
                    {
                        AssetDatabase.Refresh();
                    }
                });
            }
            if (GUILayout.Button("Full Official Content Build + Archive State"))
            {
                var path = EditorUtility.SaveFilePanel("Archive state for this Player / platform", "", "addressables_content_state.bin", "bin");
                if (!string.IsNullOrEmpty(path))
                {
                    Run(() => Show(ResourceEditorOperations.BuildContent(settings, path)));
                }
            }
            if (GUILayout.Button("Official Content Update From Archived State"))
            {
                var path = EditorUtility.OpenFilePanel("Select the target Player's official content state", "", "bin");
                if (!string.IsNullOrEmpty(path))
                {
                    Run(() => Show(ResourceEditorOperations.BuildContentUpdate(settings, path)));
                }
            }
            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.HelpBox(_message, _messageType);
            }
        }

        private void OnInspectorUpdate()
        {
            if (_selectedTab == 1 && _displayRevision != ResourceEventHistory.Revision)
                Repaint();
        }

        private void DrawEvents()
        {
            GUILayout.Label("请求事件 / Request Events", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"最近 {ResourceEventHistory.Count} 条，已淘汰 {ResourceEventHistory.Dropped} 条。资产定位与提供、官方交付、场景句柄显示工作结果；批次子项、实例创建与租用、图片排队与缓存等子操作仅显示请求终态。", EditorStyles.wordWrappedLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying ||
                    (!ResourceEventHistory.IsRecording && ResourceSystem.Current == null)))
                {
                    if (GUILayout.Button(ResourceEventHistory.IsRecording ? "停止录制" : "开始录制"))
                        ResourceEventHistory.SetRecording(!ResourceEventHistory.IsRecording);
                }
                if (GUILayout.Button("清空事件"))
                    ResourceEventHistory.Clear();
            }
            if (_displayRevision != ResourceEventHistory.Revision)
            {
                DisplayedEvents = ResourceEventHistory.Read().Reverse().Select(FormatEvent).ToArray();
                _displayRevision = ResourceEventHistory.Revision;
            }
            foreach (var line in DisplayedEvents)
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
        }

        private static string FormatEvent(ResourceEventRow row)
        {
            string Fingerprint(ulong value) => value == 0 ? "(empty)" : value.ToString("X16");
            string diagnostic = row.DiagnosticId == 0 ? "" : $" Snapshot DiagnosticId {row.DiagnosticId}";
            return $"#{row.Sequence} session {row.SessionId} request {row.RequestId}{diagnostic} load {row.LoadId} work {row.WorkId} | " +
                $"{row.Source} {row.Phase} {row.Kind} {row.Role} | key {Fingerprint(row.KeyFingerprint)} owner {Fingerprint(row.OwnerFingerprint)} | " +
                $"{row.ElapsedMilliseconds:F2} ms | {row.Status} {row.Error}{(row.Late ? " (late)" : "")}";
        }

        private void DrawDiagnostics()
        {
            using var scroll = new EditorGUILayout.ScrollViewScope(_diagnosticsScroll);
            _diagnosticsScroll = scroll.scrollPosition;
            DrawEvents();
            EditorGUILayout.HelpBox("Manual snapshots may contain original keys, owners and error details. Snapshots copy YL responsibilities only. No automatic sampling, asset loading, or native reference-count estimate.", MessageType.Info);
            bool canCapture = EditorApplication.isPlaying && ResourceSystem.Current != null;
            using (new EditorGUI.DisabledScope(!canCapture))
            {
                if (GUILayout.Button("Capture Snapshot (Read Only)"))
                {
                    Run(() =>
                    {
                        var resources = ResourceSystem.Current;
                        if (resources == null)
                        {
                            return;
                        }
                        _snapshot = resources.GetDiagnostics();
                        _history = resources.GetDiagnosticsHistory();
                        _message = null;
                    });
                }
            }
            if (!canCapture)
            {
                EditorGUILayout.LabelField("No active YL resource session. A previous capture remains a historical snapshot.", EditorStyles.wordWrappedLabel);
            }
            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.HelpBox(_message, _messageType);
            }
            if (_snapshot == null)
            {
                return;
            }

            _diagnosticsFilter = EditorGUILayout.TextField("Owner / Scope / Key", _diagnosticsFilter);
            EditorGUILayout.LabelField(_snapshot.CapturedAtUtc.ToString("u") + "   " + Summary(_snapshot), EditorStyles.wordWrappedLabel);
            DrawRows("Requests", _snapshot.Requests, row => $"#{row.Id} {row.Operation} | {row.Key} | {row.Owner} | {row.Status}", row => row.Error);
            DrawRows("Scopes", _snapshot.Scopes, row => $"{row.Owner} | leases {row.LeaseCount} | {(row.Closing ? "Closing" : "Open")}", row => row.Error);
            DrawRows("Leases", _snapshot.Leases, row => $"#{row.Id} {row.Kind} | {row.Key} | {row.Owner} | " +
                $"{(row.Internal ? "Internal" : "External")} / {(row.Settled ? "Settled" : row.Disposed ? "Closing" : "Held")}");
            DrawRows("Prefab Pools", _snapshot.Pools, row => $"{row.Key} | {row.Policy} | {row.Scene} | {(row.Retired ? "Retired" : "Open")} | " +
                $"idle {row.IdleCount}, active {row.ActiveCount}, cleanup {row.CleanupCount}, transitions {row.Transitions}");
            DrawRows("Scene Intents", _snapshot.Scenes, row => $"#{row.Sequence} {row.Key} | {row.Owner} | {row.Scene} | {row.State}", row => row.Error);
            DrawRows("Draining Handles", _snapshot.DrainingHandles, row => $"{row.Operation} | {row.Key} | {row.Owner}");
            DrawRows("Cleanup Errors", _snapshot.Errors, error => error);
            _showHistory = EditorGUILayout.Foldout(_showHistory, $"Captured History ({_history?.Count ?? 0})", true);
            if (_showHistory && _history != null)
            {
                foreach (var item in _history)
                {
                    EditorGUILayout.LabelField(item.CapturedAtUtc.ToString("u") + "   " + Summary(item), EditorStyles.wordWrappedLabel);
                }
            }
        }

        private static string Summary(ResourceDiagnosticsReport report) =>
            $"{report.State} | {report.RequestCount} requests, {report.ScopeCount} scopes, {report.LeaseCount} leases, " +
            $"{report.PendingWorkCount} jobs, {report.DrainingHandleCount} draining handles";

        private void DrawRows<T>(string title, IReadOnlyList<T> rows, Func<T, string> label, Func<T, string> error = null)
        {
            const int Limit = 100;
            GUILayout.Label($"{title} ({rows.Count})", EditorStyles.boldLabel);
            var shown = 0;
            foreach (var row in rows)
            {
                var text = label(row);
                var cause = error?.Invoke(row);
                if (!string.IsNullOrEmpty(_diagnosticsFilter) && text.IndexOf(_diagnosticsFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (cause == null || cause.IndexOf(_diagnosticsFilter, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                if (shown == Limit)
                {
                    EditorGUILayout.LabelField($"Showing the first {Limit} matches. Refine the filter to see other rows.", EditorStyles.wordWrappedLabel);
                    break;
                }
                shown++;
                EditorGUILayout.LabelField(new GUIContent(text, text), EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty(cause))
                {
                    EditorGUILayout.HelpBox(cause, MessageType.Error);
                }
            }
            if (shown == 0)
            {
                EditorGUILayout.LabelField("No matching responsibilities.");
            }
        }

        private void Show(ResourceEditorBuildReport report)
        {
            _message = report.Summary + "\nNo upload or deployment was performed.";
            _messageType = MessageType.Info;
        }

        private void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error) when (!(error is ExitGUIException))
            {
                _message = error.ToString();
                _messageType = MessageType.Error;
            }
        }
    }
}

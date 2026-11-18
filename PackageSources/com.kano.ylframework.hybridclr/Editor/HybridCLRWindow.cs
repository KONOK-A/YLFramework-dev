using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>显式编排新版基座和内容更新；不安装运行库、不改旧配方、不执行私有发布或部署。</summary>
    public sealed class HybridCLRWindow : EditorWindow
    {
        [SerializeField] private HybridCLRBuildProfile profile;
        private Vector2 _scroll;
        private string _message;
        private MessageType _messageType;
        private HybridCLRBuildReport _report;

        [MenuItem("YLFramework/Integrations/HybridCLR")]
        private static void Open() => GetWindow<HybridCLRWindow>("HybridCLR");

        private void OnGUI()
        {
            using var scroll = new EditorGUILayout.ScrollViewScope(_scroll);
            _scroll = scroll.scrollPosition;
            EditorGUILayout.HelpBox("Schema 2 uses official Addressables entries and content builds. Legacy profiles and native bases are rejected, never upgraded in place. Downloading new DLLs does not make loaded code replaceable; changing code after load requires restart.", MessageType.Info);
            if (GUILayout.Button("Official HybridCLR Settings"))
            {
                SettingsService.OpenProjectSettings("Project/HybridCLR Settings");
            }
            if (GUILayout.Button("Addressables Workbench"))
            {
                ResServiceEditorWindow.OpenResServiceWindow();
            }
            profile = (HybridCLRBuildProfile)EditorGUILayout.ObjectField("Profile", profile, typeof(HybridCLRBuildProfile), false);
            if (GUILayout.Button("Create New Addressables Profile"))
            {
                var path = EditorUtility.SaveFilePanelInProject("New HybridCLR Profile", "HybridCLRBuildProfile", "asset", "Choose a new profile path.");
                if (!string.IsNullOrEmpty(path))
                {
                    Run(() => profile = HybridCLRProfileTools.Create(path));
                }
            }
            if (profile)
            {
                using (var data = new SerializedObject(profile))
                {
                    data.Update();
                    var property = data.GetIterator();
                    var children = true;
                    while (property.NextVisible(children))
                    {
                        children = false;
                        using (new EditorGUI.DisabledScope(property.name == "m_Script"))
                        {
                            EditorGUILayout.PropertyField(property, true);
                        }
                    }
                    data.ApplyModifiedProperties();
                }
                if (GUILayout.Button("Save This Profile"))
                {
                    Run(() => AssetDatabase.SaveAssetIfDirty(profile));
                }
                if (GUILayout.Button("Inspect Inputs (Read Only)"))
                {
                    Run(() =>
                    {
                        var result = HybridCLRProfileTools.Inspect(profile);
                        _message = string.Join("\n", result.Errors.Concat(result.Warnings)) + "\nAssemblies=" + string.Join(", ", result.Assemblies);
                        _messageType = result.HasSucceeded ? MessageType.Info : MessageType.Error;
                    });
                }
                if (GUILayout.Button("Register Existing Generated Official Entries"))
                {
                    Run(() =>
                    {
                        HybridCLRProfileTools.MapGeneratedResources(profile);
                        _message = "Generated entries registered. Save their official group and Settings explicitly; no content build has run.";
                        _messageType = MessageType.Info;
                    });
                }
                if (GUILayout.Button("Validate Formal Build Preconditions"))
                {
                    Run(() => _report = HybridCLRBuildPipeline.Validate(profile));
                }
                if (GUILayout.Button("Build New Native Base + Official Content"))
                {
                    var path = EditorUtility.SaveFilePanel("New Windows Native Player", "", "YLHybridCLR.exe", "exe");
                    if (!string.IsNullOrEmpty(path) && EditorUtility.DisplayDialog("Build New Base", "Build official Addressables content, a native Player and a new immutable baseline. No upload is performed; existing baselines are never overwritten.", "Build", "Cancel"))
                    {
                        Run(() => _report = HybridCLRBuildPipeline.BuildBasePlayer(profile, new BuildPlayerOptions
                        {
                            target = profile.target,
                            targetGroup = BuildTargetGroup.Standalone,
                            subtarget = (int)StandaloneBuildSubtarget.Player,
                            scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
                            options = profile.development ? BuildOptions.Development : BuildOptions.None,
                            locationPathName = Path.GetFullPath(path)
                        }));
                    }
                }
                if (GUILayout.Button("Build Official Content Update Against Frozen Base") &&
                    EditorUtility.DisplayDialog("Build Content Update", "Compile code, validate the frozen native contract, and use its archived official content state. This does not replace code already loaded in a running process, deploy, or upload.", "Build Update", "Cancel"))
                {
                    Run(() => _report = HybridCLRBuildPipeline.PublishPatch(profile));
                }
            }
            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.HelpBox(_message, _messageType);
            }
            if (_report != null)
            {
                EditorGUILayout.HelpBox(_report.Summary, _report.HasSucceeded ? MessageType.Info : MessageType.Error);
            }
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

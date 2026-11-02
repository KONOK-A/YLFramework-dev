using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using YLFramework.Editor.Help;
using YLFramework.Audio;

namespace YLFramework.Editor.Audio
{
    /// <summary>复用运行时诊断绘制器的编辑器窗口，读取音频状态而不主动初始化播放服务。</summary>
    [MovedFrom(true, "Project.AudioKit.Editor", "Project.AudioKit.Editor", "AudioKitDebugEditorWindow")]
    internal sealed class AudioKitDebugEditorWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private double _nextRepaint;

        /// <summary>打开或复用音频诊断窗口，并设置标题和最小尺寸。</summary>
        [MenuItem("YLFramework/Audio/Debug")]
        private static void Open()
        {
            var window = GetWindow<AudioKitDebugEditorWindow>();
            window.titleContent = new GUIContent("AudioKit Debug");
            window.minSize = new Vector2(360, 240);
            window.Show();
        }

        /// <summary>播放期间以 0.2 秒间隔节流重绘请求，不订阅音频事件或推进播放。</summary>
        private void Update()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < _nextRepaint)
            {
                return;
            }

            _nextRepaint = EditorApplication.timeSinceStartup + 0.2;
            Repaint();
        }

        /// <summary>绘制帮助入口、运行时 GUI 显示开关和当前诊断数据。</summary>
        /// <remarks>诊断数据被动读取；绘制器保留显式统计清零按钮，不提供播放控制，也不为清零登记 Undo。</remarks>
        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("AudioKit 帮助", EditorStyles.toolbarButton))
                {
                    HelpWindow.Open(AudioKitHelpProvider.ModuleId);
                }

                using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
                {
                    if (GUILayout.Button(AudioKitDebugWindow.IsVisible ? "隐藏运行时 GUI" : "显示运行时 GUI", EditorStyles.toolbarButton))
                    {
                        AudioKitDebugWindow.Toggle();
                    }
                }
            }
            AudioKitDiagnostics.Draw(ref _scrollPosition);
        }
    }
}

using System;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Audio
{
    /// <summary>默认关闭的运行时音频诊断窗口，支持 F1、Android 返回键和 iOS 四指入口。</summary>
    /// <remarks>运行初始化只创建隐藏的入口宿主，不访问 AudioKit；显式打开后才绘制诊断。宿主跨场景保留。快捷键入口和绘制仅在 Editor 或 Development Build 编译。</remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("YLFramework/Audio/AudioKit Debug Window")]
    public sealed class AudioKitDebugWindow : MonoSingletonHost<AudioKitDebugWindow>
    {
        private bool _isVisible;
#if UNITY_IOS && !UNITY_EDITOR
        private bool _isTouching;
#endif
        private Vector2 _scrollPosition;
        private Rect _windowRect = new Rect(12, 12, 640, 560);

#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !ENABLE_LEGACY_INPUT_MANAGER && ENABLE_INPUT_SYSTEM
        // 诊断 UI 只读取当前输入后端；反射避免 Runtime 程序集新增可选 Input System 包依赖。
        private static readonly System.Reflection.PropertyInfo _buttonReleased =
            Type.GetType("UnityEngine.InputSystem.Controls.ButtonControl, Unity.InputSystem", true).GetProperty("wasReleasedThisFrame");
#if UNITY_EDITOR || UNITY_STANDALONE || UNITY_WEBGL || UNITY_ANDROID
        private static readonly Type _keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", true);
        private static readonly System.Reflection.PropertyInfo _keyboardCurrent = _keyboardType.GetProperty("current");
        private static readonly System.Reflection.PropertyInfo _shortcutKey = _keyboardType.GetProperty(
#if UNITY_ANDROID && !UNITY_EDITOR
            "escapeKey");
#else
            "f1Key");
#endif
#elif UNITY_IOS
        private static readonly Type _touchscreenType = Type.GetType("UnityEngine.InputSystem.Touchscreen, Unity.InputSystem", true);
        private static readonly System.Reflection.PropertyInfo _touchscreenCurrent = _touchscreenType.GetProperty("current");
        private static readonly System.Reflection.PropertyInfo _touches = _touchscreenType.GetProperty("touches");
        private static readonly System.Reflection.PropertyInfo _touchPress =
            Type.GetType("UnityEngine.InputSystem.Controls.TouchControl, Unity.InputSystem", true).GetProperty("press");
        private static readonly System.Reflection.PropertyInfo _buttonPressed =
            Type.GetType("UnityEngine.InputSystem.Controls.ButtonControl, Unity.InputSystem", true).GetProperty("isPressed");
#endif
#endif

        /// <summary>当前运行时窗口是否可见；读取不会创建宿主或初始化音频。</summary>
        public static bool IsVisible => TryGetInstance(out var instance) && instance._isVisible;

        /// <summary>在播放模式显示诊断窗口，必要时创建宿主。</summary>
        /// <remarks>运行时诊断仅在 Editor 或 Development Build 可用。</remarks>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前构建不包含运行时诊断。</exception>
        public static void Show() => EnsureInstance()._isVisible = true;

        /// <summary>隐藏现有诊断窗口，不停止音频，也不创建宿主。</summary>
        public static void Hide()
        {
            if (TryGetInstance(out var instance))
            {
                instance._isVisible = false;
            }
        }

        /// <summary>在播放模式切换诊断窗口的显示状态。</summary>
        /// <remarks>运行时诊断仅在 Editor 或 Development Build 可用。</remarks>
        /// <exception cref="InvalidOperationException">当前不在播放模式，或当前构建不包含运行时诊断。</exception>
        public static void Toggle()
        {
            var instance = EnsureInstance();
            instance._isVisible = !instance._isVisible;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>场景加载前准备隐藏的快捷键宿主，不初始化音频播放。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallShortcuts() => EnsureInstance()._isVisible = false;
#endif

        /// <summary>仅在支持诊断的播放环境取得窗口宿主，退出中或不支持时明确抛错。</summary>
        private static AudioKitDebugWindow EnsureInstance()
        {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            throw new InvalidOperationException("AudioKit diagnostics are available only in development builds.");
#else
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException("AudioKit 运行时诊断窗口只能在播放模式打开。");
            }

            var instance = GetInstance(includeInactive: false);
            if (!instance)
            {
                throw new InvalidOperationException("AudioKit diagnostics are shutting down.");
            }

            return instance;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>按当前平台和已编译输入后端读取快捷入口，切换窗口可见性。</summary>
        private void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
#if UNITY_EDITOR || UNITY_STANDALONE || UNITY_WEBGL
            if (Input.GetKeyUp(KeyCode.F1))
            {
                Toggle();
            }
#elif UNITY_ANDROID
            if (Input.GetKeyUp(KeyCode.Escape))
            {
                Toggle();
            }
#elif UNITY_IOS
            if (!_isTouching && Input.touchCount == 4)
            {
                _isTouching = true;
                Toggle();
            }
            else if (Input.touchCount == 0)
            {
                _isTouching = false;
            }
#endif
#elif ENABLE_INPUT_SYSTEM
#if UNITY_EDITOR || UNITY_STANDALONE || UNITY_WEBGL || UNITY_ANDROID
            var keyboard = _keyboardCurrent.GetValue(null);
            if (keyboard != null && (bool)_buttonReleased.GetValue(_shortcutKey.GetValue(keyboard)))
            {
                Toggle();
            }
#elif UNITY_IOS
            var touchscreen = _touchscreenCurrent.GetValue(null);
            int count = 0;
            if (touchscreen != null)
            {
                foreach (var touch in (System.Collections.IEnumerable)_touches.GetValue(touchscreen))
                {
                    if ((bool)_buttonPressed.GetValue(_touchPress.GetValue(touch)))
                    {
                        count++;
                    }
                }
            }
            if (!_isTouching && count == 4)
            {
                _isTouching = true;
                Toggle();
            }
            else if (count == 0)
            {
                _isTouching = false;
            }
#endif
#endif
        }

        /// <summary>窗口可见时按屏幕边界约束窗口大小和位置，再绘制诊断内容。</summary>
        private void OnGUI()
        {
            if (!_isVisible)
            {
                return;
            }

            _windowRect.width = Mathf.Min(640, Mathf.Max(1, Screen.width - 24));
            _windowRect.height = Mathf.Min(560, Mathf.Max(1, Screen.height - 24));
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Mathf.Max(0, Screen.width - _windowRect.width));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Mathf.Max(0, Screen.height - _windowRect.height));
            _windowRect = GUILayout.Window(GetInstanceID(), _windowRect, DrawWindow, "AudioKit Debug");
        }

        /// <summary>绘制关闭按钮与被动诊断，并允许拖动窗口标题区域。</summary>
        private void DrawWindow(int windowId)
        {
            if (GUILayout.Button("关闭"))
            {
                Hide();
            }

            AudioKitDiagnostics.Draw(ref _scrollPosition);
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }
#endif
    }
}

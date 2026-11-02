using System;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
#if UNITY_6000_2_OR_NEWER
using TreeView = UnityEditor.IMGUI.Controls.TreeView<int>;
#endif

namespace YLFramework.Tests.Extensions.Shared
{
    public class E0ReorderableTestWindow : EditorWindow
    {
        public TreeView Tree;
        [global::UnityEngine.Serialization.FormerlySerializedAs("GuiCalls")]
        public int guiCalls;
        private Action _pending;
        private EventType _eventType;
        private Exception _failure;

        public void Dispatch(EventType type, Action action)
        {
            _eventType = type;
            _pending = action;
            _failure = null;
            SendEvent(new Event { type = type, mousePosition = new Vector2(20, 20), button = 0 });
            if (_pending != null)
            {
                throw new InvalidOperationException("窗口未处理请求的IMGUI事件。");
            }

            if (_failure != null)
            {
                throw new InvalidOperationException("窗口IMGUI操作失败。", _failure);
            }
        }

        private void OnGUI()
        {
            guiCalls++;
            if (_pending != null && Event.current.type == _eventType)
            {
                var action = _pending;
                _pending = null;
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    _failure = error;
                }
                Event.current.Use();
            }
            Tree?.OnGUI(new Rect(0, 0, position.width, position.height));
        }

        private void OnDisable()
        {
            (Tree as IDisposable)?.Dispose();
            Tree = null;
        }
    }
}

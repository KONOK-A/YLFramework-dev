using System;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>提供当前项目的定位、身份复制和文档入口，不编辑或保存项目配置。</summary>
    internal sealed class ResourceProjectActionsPopup : PopupWindowContent
    {
        private const float Width = 220;
        private const float RowHeight = 28;
        private readonly ResourceProject _project;
        private readonly string _id;
        private readonly string _path;
        private readonly Action<string> _reportError;
        private readonly GUIStyle _itemStyle;
        private readonly string[] _labels;
        private int _highlighted = -1;

        /// <summary>捕获打开菜单时的项目标识和路径，供后续复制而非重新推断目标。</summary>
        internal ResourceProjectActionsPopup(ResourceProject selected, Action<string> error)
        {
            _project = selected;
            _id = selected ? selected.projectId : "";
            _path = selected ? AssetDatabase.GetAssetPath(selected) : "";
            _reportError = error;
            _labels = selected ? new[] { "定位当前项目配置", "复制项目标识", "复制配置路径", "帮助（Resource 文档）" } : new[] { "帮助（Resource 文档）" };
            _itemStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(12, 12, 0, 0),
                margin = new RectOffset()
            };
        }

        // 使用已知弹层宽度右对齐，而不是猜测原生系统菜单的实际宽度。
        internal static Rect GetAnchor(Rect button) => new(button.xMax - Width, button.y + 4, Width, button.height);
        /// <summary>按当前可用动作数量计算弹层高度。</summary>
        public override Vector2 GetWindowSize() => new(Width, _labels.Length * RowHeight + 12 + (_labels.Length > 1 ? 9 : 0));
        /// <summary>启用鼠标移动事件以更新菜单高亮。</summary>
        public override void OnOpen() => editorWindow.wantsMouseMove = true;

        /// <summary>处理键盘和鼠标导航，将选中动作交给统一菜单执行入口。</summary>
        public override void OnGUI(Rect rect)
        {
            var input = Event.current;
            if (input.type == EventType.KeyDown)
            {
                if (input.keyCode == KeyCode.DownArrow || input.keyCode == KeyCode.UpArrow)
                {
                    _highlighted = _highlighted < 0 ? (input.keyCode == KeyCode.DownArrow ? 0 : _labels.Length - 1) :
                        (_highlighted + (input.keyCode == KeyCode.DownArrow ? 1 : _labels.Length - 1)) % _labels.Length;
                    input.Use();
                    editorWindow.Repaint();
                }
                else if ((input.keyCode == KeyCode.Return || input.keyCode == KeyCode.KeypadEnter) && _highlighted >= 0)
                {
                    Execute(_highlighted);
                }
                else if (input.keyCode == KeyCode.Escape)
                {
                    editorWindow.Close();
                    GUIUtility.ExitGUI();
                }
            }
            for (var index = 0; index < _labels.Length; index++)
            {
                var row = new Rect(6, 6 + index * RowHeight + (index == 3 ? 9 : 0), rect.width - 12, RowHeight);
                if (input.type == EventType.MouseMove && row.Contains(input.mousePosition))
                {
                    _highlighted = index;
                    editorWindow.Repaint();
                }
                if (_highlighted == index)
                {
                    EditorGUI.DrawRect(row, EditorGUIUtility.isProSkin ? new Color(.25f, .39f, .53f) : new Color(.7f, .81f, .92f));
                }

                if (index == 3)
                {
                    EditorGUI.DrawRect(new Rect(12, row.y - 5, rect.width - 24, 1), ResourceWorkbenchGui.Divider);
                }

                if (GUI.Button(row, _labels[index], _itemStyle))
                {
                    Execute(index);
                }
            }
        }

        /// <summary>关闭菜单后执行定位、复制或打开文档，不触发构建和发布。</summary>
        private void Execute(int index)
        {
            editorWindow.Close();
            switch (_labels.Length == 1 ? 3 : index)
            {
                case 0:
                    if (_project)
                    {
                        Selection.activeObject = _project;
                        EditorGUIUtility.PingObject(_project);
                    }
                    break;
                case 1:
                    EditorGUIUtility.systemCopyBuffer = _id;
                    break;
                case 2:
                    EditorGUIUtility.systemCopyBuffer = _path;
                    break;
                case 3:
                    var document = global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(
                        typeof(ResourceProjectActionsPopup).Assembly, "Documentation~/Modules/Resource.md");
                    if (System.IO.File.Exists(document))
                    {
                        Application.OpenURL(new System.Uri(document).AbsoluteUri);
                    }
                    else
                    {
                        _reportError("Resource.md 不在当前工程中。");
                    }

                    break;
            }
            GUIUtility.ExitGUI();
        }
    }
}

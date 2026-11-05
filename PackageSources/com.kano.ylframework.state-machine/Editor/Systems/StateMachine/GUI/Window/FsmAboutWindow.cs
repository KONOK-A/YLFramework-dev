using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>显示状态机模块名称的关于窗口。</summary>
    public class FsmAboutWindow : EditorWindow
    {
        /// <summary>绘制模块标识，不执行配置或运行时操作。</summary>
        private void OnGUI()
        {
            EditorGUILayout.LabelField("YL Asset FSM", EditorStyles.boldLabel);
        }
    }
}

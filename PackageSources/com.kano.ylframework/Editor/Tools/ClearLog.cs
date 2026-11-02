#if UNITY_EDITOR

using System.Reflection;
using UnityEditor;

namespace YLFramework.Editor.Tools
{
    /// <summary>通过编辑器菜单清空 Unity Console 的现有日志。</summary>
    public static class ClearLog
    {
        /// <summary>反射调用 Unity 内部日志清理入口；未找到 Clear 方法时不执行操作。</summary>
        [MenuItem("YLFramework/Toolkit/清空控制台", false, priority = 0)] // # 代表Shift
        public static void MenuClicked()
        {
            //LogEntries 为 internal 权限 所以需要利用反射来处理
            var assembly = Assembly.GetAssembly(typeof(SceneView));
            var type = assembly.GetType("UnityEditor.LogEntries");
            var method = type.GetMethod("Clear");
            if (method != null)
            {
                method.Invoke(new object(), null);
            }
        }
    }
}

#endif

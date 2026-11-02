#if UNITY_EDITOR

using System.IO;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Tools
{
    /// <summary>在系统文件浏览器中打开当前工程或应用持久化数据目录，不创建目录内容。</summary>
    public static class OpenFolder
    {
        /// <summary>打开 Assets 的父目录，即当前 Unity 项目根目录，而非 UPM 缓存或导出包目录。</summary>
        [MenuItem("YLFramework/Toolkit/打开目录/Unity Package", priority = 1)]
        public static void OpenPackageFolder()
        {
            Application.OpenURL("file:///" + Path.Combine(Application.dataPath, "../"));
        }
        /// <summary>打开当前应用的 persistentDataPath，由系统处理该目录的显示。</summary>
        [MenuItem("YLFramework/Toolkit/打开目录/PersistentDataPath", priority = 2)]
        public static void OpenPersistentFolder()
        {
            Application.OpenURL("file:///" + Application.persistentDataPath);
        }

    }
}

#endif

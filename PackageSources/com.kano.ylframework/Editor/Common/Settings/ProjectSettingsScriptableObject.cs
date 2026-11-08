using System;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace YLFramework.Editor.Common
{
    /// <summary>
    /// 项目全局设置，按类型全名保存到 ProjectSettings/YLFramework/Core，而非 Assets 资产数据库。
    /// </summary>
    /// <typeparam name="T">实际加载和保存的 ScriptableObject 设置类型。</typeparam>
    public class ProjectSettingsScriptableObject<T> : ScriptableObject where T : ScriptableObject
    {
        private static T _instance;

        /// <summary>取得本类型缓存的设置实例；尚未加载时读取配置，文件不存在则创建并保存默认实例。</summary>
        /// <remarks>只访问当前类型的精确路径，不扫描、迁移或删除其他设置文件。</remarks>
        public static T Instance
        {
            get
            {
                if (!_instance)
                {
                    LoadOrCreate();
                }
                return _instance;
            }
        }
        /// <summary>重新读取现有配置，或创建并保存默认实例；不会先销毁原有的缓存实例。</summary>
        /// <exception cref="IOException">当前配置不是唯一且类型完全匹配的对象。</exception>
        public static void LoadOrCreate()
        {
            string filePath = GetFilePath();

            if (File.Exists(filePath))
            {
                _instance = LoadExisting(filePath);
            }
            else
            {
                _instance = CreateInstance<T>();
                Save();
            }

        }

        /// <summary>将已加载的缓存实例写入本类型配置文件；不注册 Undo，也不提供原子替换或失败回滚。</summary>
        /// <param name="saveAsText">是否使用 Unity 文本序列化格式。</param>
        /// <exception cref="InvalidOperationException">未加载有效实例，或类型全名不能安全用作文件名。</exception>
        /// <exception cref="IOException">既有当前文件类型不兼容，或目标路径不安全。</exception>
        public static void Save(bool saveAsText = true)
        {
            if (!_instance)
            {
                throw new InvalidOperationException("Load YL settings before saving them.");
            }

            string filePath = GetFilePath();
            if (!string.IsNullOrEmpty(filePath))
            {
                // 仅允许更新唯一且类型完全一致的配置；校验读出的临时对象不作为缓存，检查后立即销毁。
                if (File.Exists(filePath))
                {
                    var existing = InternalEditorUtility.LoadSerializedFileAndForget(filePath);
                    bool compatible = existing.Length == 1 && existing[0] != null && existing[0].GetType() == typeof(T);
                    foreach (var loadedObject in existing)
                    {
                        if (loadedObject)
                        {
                            DestroyImmediate(loadedObject);
                        }
                    }
                    if (!compatible)
                    {
                        throw new IOException("Refusing to overwrite incompatible YL settings: " + filePath);
                    }
                }

                string directoryName = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
                UnityEngine.Object[] obj = new T[1] { _instance };
                filePath = GetFilePath();
                InternalEditorUtility.SaveToSerializedFileAndForget(obj, filePath, saveAsText);
            }
        }

        /// <summary>从文件取得唯一且类型匹配的设置对象；不兼容时销毁本次加载的全部临时对象并拒绝使用。</summary>
        private static T LoadExisting(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new IOException("Cannot reload missing YL settings: " + filePath);
            }

            var objects = InternalEditorUtility.LoadSerializedFileAndForget(filePath);
            if (objects.Length == 1 && objects[0] && objects[0].GetType() == typeof(T))
            {
                return (T)objects[0];
            }

            foreach (var loadedObject in objects)
            {
                if (loadedObject)
                {
                    DestroyImmediate(loadedObject);
                }
            }
            throw new IOException("Refusing to replace incompatible YL settings: " + filePath);
        }

        /// <summary>按当前类型全名构造精确项目设置路径，并检查现有路径各层是否可安全写入。</summary>
        /// <returns>ProjectSettings/YLFramework/Core 下的 .asset 配置路径。</returns>
        protected static string GetFilePath()
        {
            return BuildFilePath(typeof(T).FullName);
        }

        /// <summary>将安全的类型全名映射到专用设置文件，并拒绝链接及文件/目录类型冲突。</summary>
        private static string BuildFilePath(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || fullName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                fullName.IndexOf('/') >= 0 || fullName.IndexOf('\\') >= 0)
            {
                throw new InvalidOperationException("A safe, unique FullName is required for YL settings.");
            }

            string filePath = string.Format("ProjectSettings/YLFramework/Core/{0}.asset", fullName);
            string fullPath = Path.GetFullPath(filePath);
            for (string parent = fullPath; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(parent);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Linked settings paths are not supported: " + parent);
                }

                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (parent == fullPath ? directory : !directory)
                {
                    throw new IOException("Expected a settings file under real directories: " + parent);
                }
            }
            return filePath;
        }


        /// <summary>定位 Unity 项目设置页面，不触发本类型设置的加载或保存。</summary>
        /// <param name="path">SettingsProvider 注册的项目设置页面路径。</param>
        public void OpenProjectSettings(string path)
        {
            ProjectSettingTools.OpenProjectSettings(path);
        }

    }
}

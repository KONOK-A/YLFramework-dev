using System;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace YLFramework.Save
{
    /// <summary>Unity 默认 Store 的路径、配置与会话所有者；业务读写直接使用 SaveStore。</summary>
    /// <remarks>
    /// 默认 Store 是借用对象，调用方不得 Dispose；自建 SaveStore 则由创建者释放。
    /// 配置须在并发使用前完成；关闭或换根取消并排空旧 owner，不迁移数据，也不重置默认配置。
    /// 默认路径只在 Unity 主线程捕获，显式根可用于纯 BCL 环境；不支持历史格式导入或自动清空损坏档。
    /// </remarks>
    public static class SaveManager
    {
        /// <summary>当前索引格式版本，与存储引擎一致，不表示支持历史格式导入。</summary>
        public const int CurrentDataVersion = SaveStore.FormatVersion;
        private static readonly object _sync = new object();
        private static SaveStore _store;
        private static string _storageRootOverride;
        private static string _defaultStorageRoot;

        /// <summary>默认 Store 共用的配置原实例；并发前配置，Close 和换根不会重建。</summary>
        public static JsonSerializerSettings DefaultSerializerSettings { get; } = new JsonSerializerSettings();

        /// <summary>只在默认 Store 创建时应用的 JSON 排版；当前实例排版由 Store.SerializationFormat 管理。</summary>
        public static Formatting DefaultSerializationFormat { get; set; } = Formatting.Indented;

        /// <summary>当前显式根或在 Unity 主线程捕获的默认持久路径；读取本身不创建 Store。</summary>
        public static string StorageRoot
        {
            get
            {
                lock (_sync)
                {
                    if (!string.IsNullOrEmpty(_storageRootOverride))
                    {
                        return _storageRootOverride;
                    }
                    if (_defaultStorageRoot != null)
                    {
                        return _defaultStorageRoot;
                    }
                    if (SynchronizationContext.Current?.GetType().FullName != "UnityEngine.UnitySynchronizationContext")
                    {
                        throw new InvalidOperationException("Capture the default save root on the Unity main thread, or call SetStorageRoot with an explicit path before worker use.");
                    }
                    return _defaultStorageRoot = Application.persistentDataPath;
                }
            }
        }

        /// <summary>借用当前根的唯一默认 Store，按需创建；不得由调用方 Dispose，读取不会覆盖实例排版。</summary>
        public static SaveStore Store
        {
            get
            {
                lock (_sync)
                {
                    if (_store == null)
                    {
                        _store = new SaveStore(StorageRoot, DefaultSerializerSettings, DefaultSerializationFormat);
                    }
                    return _store;
                }
            }
        }

        /// <summary>只切换路径，不复制或迁移数据。同根重设也关闭并排空旧 owner；默认配置保留。</summary>
        /// <param name="rootPath">下次创建 Store 的根；null 或空字符串恢复默认路径，合法性在创建时校验。</param>
        public static void SetStorageRoot(string rootPath)
        {
            SaveStore previous;
            lock (_sync)
            {
                previous = _store;
                _store = null;
                _storageRootOverride = string.IsNullOrEmpty(rootPath) ? null : rootPath;
            }
            previous?.Dispose();
        }

        /// <summary>解除当前 Store 并等待释放，不修改根或默认配置；后续可重新打开同根。</summary>
        public static void Close()
        {
            SaveStore previous;
            lock (_sync)
            {
                previous = _store;
                _store = null;
            }
            previous?.Dispose();
        }

        /// <summary>新 Unity 会话关闭旧 Store，捕获主线程默认路径，并去重安装退出清理。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginUnitySession()
        {
            Close();
            lock (_sync)
            {
                _storageRootOverride = null;
                _defaultStorageRoot = Application.persistentDataPath;
            }
            Application.quitting -= Close;
            Application.quitting += Close;
        }
    }
}

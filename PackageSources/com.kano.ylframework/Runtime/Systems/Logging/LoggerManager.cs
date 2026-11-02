using System;
using System.IO;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Logging
{
    /// <summary>显式主线程启用。仅持有自己的日志处理器和宿主，默认无自动创建、写盘或接管。</summary>
    public sealed class LoggerManager : MonoBehaviour
    {
        private static LoggerManager _host;
        private static LoggerSession _current;
        private LoggerSession _session;
        private ILogHandler _previous;

        /// <summary>注册执行会话重置清理，不在静态初始化时自动安装日志处理器。</summary>
        static LoggerManager() => SingletonSessionState.RegisterSessionReset(Disable);
        /// <summary>是否仍登记着当前会话；不保证其他代码尚未替换 Unity 当前日志处理器。</summary>
        public static bool IsEnabled => _current != null;
        /// <summary>已登记的日志会话；未启用或释放后为 null，读取不会自动创建宿主。</summary>
        public static LoggerSession Current => _current;

        /// <summary>当前会话重复启用返回同一对象，不重复安装。更换配置先 Disable。</summary>
        /// <param name="options">会话配置；null 使用不写文件的默认配置，目录未提供时取持久化路径下的日志目录。</param>
        /// <returns>持有跨场景宿主的当前日志会话，借用启用前的处理器作为输出目标。</returns>
        /// <remarks>仅在有效运行会话的主线程显式调用；重复启用忽略新配置，不重新接管被外部替换的处理器。</remarks>
        public static LoggerSession Enable(LoggerOptions options = null)
        {
            if (!SingletonSessionState.CanRunExecution)
            {
                throw new InvalidOperationException("Logger只能在有效Play会话中显式启用。");
            }

            if (_current != null)
            {
                return _current;
            }

            options ??= new LoggerOptions();
            var resolved = new LoggerOptions
            {
                WriteToFile = options.WriteToFile,
                Directory = options.Directory ?? Path.Combine(Application.persistentDataPath, "YLFramework", "Logs"),
                ConsoleFilter = options.ConsoleFilter,
                FileFilter = options.FileFilter
            };
            var previous = Debug.unityLogger.logHandler;
            var session = new LoggerSession(previous, resolved);
            GameObject obj = null;
            try
            {
                obj = new GameObject("YLFramework.Logging.LoggerManager");
                var host = obj.AddComponent<LoggerManager>();
                host._previous = previous;
                host._session = session;
                DontDestroyOnLoad(obj);
                _host = host;
                _current = session;
                Debug.unityLogger.logHandler = session;
                return session;
            }
            catch
            {
                session.Dispose();
                if (obj)
                {
                    Destroy(obj);
                }

                throw;
            }
        }

        /// <summary>在主线程关闭本方会话并销毁本方宿主；仅当 Unity 仍使用本方处理器时恢复前一个处理器。</summary>
        public static void Disable()
        {
            var host = _host;
            _host = null;
            if (host)
            {
                host.Release();
                Destroy(host.gameObject);
            }
            else
            {
                _current?.Dispose();
                _current = null;
            }
        }

        /// <summary>释放本宿主的会话并清理匹配的静态引用，不覆盖其他代码后来安装的处理器。</summary>
        private void Release()
        {
            var session = _session;
            if (session == null)
            {
                return;
            }

            _session = null;
            if (ReferenceEquals(Debug.unityLogger.logHandler, session))
            {
                Debug.unityLogger.logHandler = _previous;
            }

            session.Dispose();
            if (ReferenceEquals(_current, session))
            {
                _current = null;
            }

            if (ReferenceEquals(_host, this))
            {
                _host = null;
            }
        }

        /// <summary>宿主停用时立即释放本方日志会话。</summary>
        private void OnDisable() => Release();
        /// <summary>宿主销毁时补做幂等释放。</summary>
        private void OnDestroy() => Release();
        /// <summary>应用退出时关闭文件句柄并交还仍由本方持有的处理器位置。</summary>
        private void OnApplicationQuit() => Release();
    }
}

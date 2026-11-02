using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace YLFramework.Logging
{
    /// <summary>
    /// 同步过滤、转发、事件与文件会话，不自行安装 Unity 日志处理器。
    /// 回调在原日志线程顺序执行，不可等待另一条会记录日志的线程；嵌套日志丢弃并计数。
    /// </summary>
    /// <remarks>
    /// 本类不按编译符号裁剪输出，也不改 Unity 的日志与调用栈开关，只处理实际到达处理器的日志。
    /// 普通日志不补采调用栈；异常文本使用 Exception.ToString，原异常及上下文继续传给目标处理器。
    /// </remarks>
    public sealed class LoggerSession : ILogHandler, IDisposable
    {
        [ThreadStatic] private static HashSet<LoggerSession> _activeSessions;
        private readonly object _gate = new();
        private readonly ILogHandler _destination;
        private readonly bool[] _console;
        private readonly bool[] _file;
        private readonly bool _writeToFile;
        private readonly string _directory;
        private StreamWriter _writer;
        private bool _hasFileFailure;
        private bool _isDisposed;
        private Action<LogType, string> _onLog;
        private Exception _lastError;
        private long _recursiveCount;
        private string _logFilePath;

        /// <summary>借用目标处理器并复制过滤配置，尚不创建输出目录或文件。</summary>
        /// <param name="destination">接收通过控制台过滤日志的目标处理器，不能为 null；会话不负责释放它。</param>
        /// <param name="options">待复制的配置，不能为 null；开启文件输出时必须提供非空白目录。</param>
        public LoggerSession(ILogHandler destination, LoggerOptions options)
        {
            _destination = destination ?? throw new ArgumentNullException(nameof(destination));
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _console = Snapshot(options.ConsoleFilter);
            _file = Snapshot(options.FileFilter);
            _writeToFile = options.WriteToFile;
            if (_writeToFile && string.IsNullOrWhiteSpace(options.Directory))
            {
                throw new ArgumentException("文件日志需要显式输出目录。", nameof(options));
            }

            _directory = options.Directory;
        }

        /// <summary>本会话成功创建的日志文件路径；首次创建前为 null，关闭后仍保留该路径。</summary>
        public string LogFilePath
        {
            get
            {
                lock (_gate)
                {
                    return _logFilePath;
                }
            }
        }
        /// <summary>最近一次被捕获的格式、转发、文件或订阅错误；后续成功不会自动清空它。</summary>
        public Exception LastError
        {
            get
            {
                lock (_gate)
                {
                    return _lastError;
                }
            }
        }
        /// <summary>同一线程重入本会话而被丢弃的日志总数，包括格式化及回调产生的嵌套日志。</summary>
        public long SuppressedRecursiveCount => Interlocked.Read(ref _recursiveCount);
        /// <summary>是否已经关闭文件与事件；关闭后仍可透明转发被其他处理器保留的调用。</summary>
        public bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _isDisposed;
                }
            }
        }
        /// <summary>同步报告类型与格式化文本，不受控制台或文件过滤限制；订阅异常逐个记录，不阻断后续订阅。</summary>
        /// <remarks>回调持有会话锁并运行在日志原线程；关闭时清空订阅，关闭后新增订阅抛出异常。</remarks>
        public event Action<LogType, string> OnLogEvent
        {
            add
            {
                lock (_gate)
                {
                    if (_isDisposed)
                    {
                        throw new ObjectDisposedException(nameof(LoggerSession));
                    }
                    _onLog += value;
                }
            }
            remove
            {
                lock (_gate)
                {
                    _onLog -= value;
                }
            }
        }

        /// <summary>把五类日志的过滤开关复制为固定数组，避免输出期间读取外部可变字典。</summary>
        private static bool[] Snapshot(Dictionary<LogType, bool> filter)
        {
            var result = new bool[5];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = filter == null || filter.TryGetValue((LogType)i, out var enabled) && enabled;
            }

            return result;
        }

        /// <summary>按异常类型分发原异常，文本取其 ToString 结果，保留目标处理器的异常处理入口。</summary>
        /// <param name="exception">要记录的异常，不能为 null。</param>
        /// <param name="context">传给目标处理器的 Unity 上下文；允许为 null，不写入文件或事件参数。</param>
        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            Dispatch(LogType.Exception, context, null, null, exception);
        }

        /// <summary>按当前区域格式化日志，再依次尝试目标转发、文件输出与事件通知。</summary>
        /// <param name="logType">日志类别；未知类别不进入类型过滤输出，但仍可通知事件。</param>
        /// <param name="context">传给目标处理器的 Unity 上下文；允许为 null。</param>
        /// <param name="format">复合格式字符串；null 按空字符串处理，格式错误记录到 LastError。</param>
        /// <param name="args">格式化参数；null 按空数组处理。</param>
        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            Dispatch(logType, context, format, args, null);
        }

        /// <summary>以线程级重入保护和会话锁串行处理日志，隔离目标处理器及各事件订阅的异常。</summary>
        private void Dispatch(LogType type, UnityEngine.Object context, string format, object[] args, Exception exception)
        {
            // 格式化可能执行用户 ToString，必须在生成消息前阻止同线程递归。
            var active = _activeSessions ??= new HashSet<LoggerSession>();
            if (!active.Add(this))
            {
                Interlocked.Increment(ref _recursiveCount);
                return;
            }
            try
            {
                lock (_gate)
                {
                    string message;
                    try
                    {
                        message = exception != null ? exception.ToString() : string.Format(CultureInfo.CurrentCulture, format ?? string.Empty, args ?? Array.Empty<object>());
                    }
                    catch (FormatException error)
                    {
                        _lastError = error;
                        message = (format ?? string.Empty) + " [Logger: invalid format]";
                    }
                    var index = (int)type;
                    var knownType = index >= 0 && index < _console.Length;
                    // 后续处理器可能仍串联本对象。关闭后只透明转发，不重开文件或事件。
                    if (_isDisposed || knownType && _console[index])
                    {
                        try
                        {
                            if (exception != null)
                            {
                                _destination.LogException(exception, context);
                            }
                            else
                            {
                                _destination.LogFormat(type, context, "{0}", message);
                            }
                        }
                        catch (Exception error)
                        {
                            _lastError = error;
                        }
                    }
                    if (_isDisposed)
                    {
                        return;
                    }

                    if (knownType && _writeToFile && _file[index] && !_hasFileFailure)
                    {
                        WriteToFile(type, message);
                    }

                    var listeners = _onLog;
                    if (listeners == null)
                    {
                        return;
                    }

                    foreach (Action<LogType, string> listener in listeners.GetInvocationList())
                    {
                        if (_isDisposed)
                        {
                            break;
                        }

                        try
                        {
                            listener(type, message);
                        }
                        catch (Exception error)
                        {
                            _lastError = error;
                        }
                    }
                }
            }
            finally
            {
                active.Remove(this);
            }
        }

        /// <summary>延迟独占创建 UTF-8 无 BOM 日志文件并逐条刷新；已捕获的文件失败会关闭本会话后续写盘。</summary>
        private void WriteToFile(LogType type, string message)
        {
            try
            {
                if (_writer == null)
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    var path = Path.Combine(_directory, "player_log_" + DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss-fffffff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".txt");
                    _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                    _logFilePath = path;
                }
                _writer.WriteLine("[{0:O}]\t[{1}]\t{2}", DateTime.UtcNow, type, message);
                _writer.Flush();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
            {
                _lastError = error;
                _hasFileFailure = true;
                ReleaseWriter();
            }
        }

        /// <summary>清空并释放本会话文件写入器，将可捕获的关闭错误留在 LastError。</summary>
        private void ReleaseWriter()
        {
            var writer = _writer;
            _writer = null;
            try
            {
                writer?.Dispose();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                _lastError = error;
            }
        }

        /// <summary>幂等关闭文件并清空事件，不释放借用的目标处理器，也不修改 Unity 当前日志处理器。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _onLog = null;
                ReleaseWriter();
            }
        }
    }
}

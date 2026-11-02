using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Logging
{
    /// <summary>创建日志会话时复制的配置。文件输出必须显式开启；空过滤表表示全部关闭，null 表示全部开启。</summary>
    public sealed class LoggerOptions
    {
        /// <summary>是否写入文件，默认关闭；开启后也要等首条通过文件过滤的日志才创建文件。</summary>
        public bool WriteToFile
        {
            get; set;
        }
        /// <summary>文件日志目录；直接创建会话且开启写盘时必须显式提供，Manager 启用时可补默认目录。</summary>
        public string Directory
        {
            get; set;
        }
        /// <summary>传给原日志处理器的类型过滤；仅值为 true 的类型通过，不影响事件通知。</summary>
        public Dictionary<LogType, bool> ConsoleFilter
        {
            get; set;
        }
        /// <summary>文件输出的类型过滤；仅值为 true 的类型通过，不影响事件通知。</summary>
        public Dictionary<LogType, bool> FileFilter
        {
            get; set;
        }
    }
}

using System.IO;
using System.Text;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>以 UTF-8 无 BOM 新建源码文件，不覆盖已有路径。</summary>
    internal static class FsmStateFileWriter
    {
        /// <summary>独占新建目标文件，仅将路径已存在的 IO 错误转换为失败结果，其他写入异常继续传播。</summary>
        /// <param name="path">父目录已存在的目标文件路径。</param>
        /// <param name="content">原样写入的生成内容，不在此转换换行。</param>
        /// <param name="error">路径冲突说明；写入成功时为 null。</param>
        /// <returns>成功写入时为 true，目标路径已被占用时为 false。</returns>
        public static bool TryWriteNew(string path, string content, out string error)
        {
            FileStream stream;
            try
            {
                // CreateNew同时保护尚未导入的文件及检查/写入之间的竞争，不覆盖现有源码。
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(path))
            {
                error = $"Script file already exists: {path}";
                return false;
            }
            using (stream)
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }

            error = null;
            return true;
        }
    }
}

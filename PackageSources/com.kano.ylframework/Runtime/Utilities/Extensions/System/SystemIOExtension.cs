using System.IO;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 文件、目录操作与路径处理扩展；路径解析和拼接遵循运行平台的 <see cref="System.IO.Path"/> 规则。
    /// 创建、删除和清空方法会实际修改文件系统，未捕获其 IO 异常；其余方法只处理路径文本。
    /// </summary>
    public static class SystemIOExtension
    {
        /// <summary>
        /// 目录不存在时创建（含多级父目录），返回传入路径。
        /// </summary>
        public static string CreateDirIfNotExists(this string dirFullPath)
        {
            if (!Directory.Exists(dirFullPath))
            {
                Directory.CreateDirectory(dirFullPath);
            }

            return dirFullPath;
        }

        /// <summary>
        /// 目录存在时递归删除；不存在时为空操作。
        /// </summary>
        public static void DeleteDirIfExists(this string dirFullPath)
        {
            if (Directory.Exists(dirFullPath))
            {
                Directory.Delete(dirFullPath, true);
            }
        }

        /// <summary>
        /// 清空目录内容；目录不存在时会直接创建该目录。副作用：递归删除目录后重建。
        /// </summary>
        public static void EmptyDirIfExists(this string dirFullPath)
        {
            if (Directory.Exists(dirFullPath))
            {
                Directory.Delete(dirFullPath, true);
            }

            Directory.CreateDirectory(dirFullPath);
        }

        /// <summary>
        /// 文件存在时删除并返回 true；不存在返回 false。
        /// </summary>
        public static bool DeleteFileIfExists(this string fileFullPath)
        {
            if (File.Exists(fileFullPath))
            {
                File.Delete(fileFullPath);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 拼接路径。分隔符处理与非法字符校验由 <see cref="System.IO.Path.Combine(string, string)"/> 负责。
        /// </summary>
        public static string CombinePath(this string selfPath, string toCombinePath)
        {
            return Path.Combine(selfPath, toCombinePath);
        }

        /// <summary>
        /// 获取路径中的文件名（含扩展名）；path 为 null 时返回 null。
        /// </summary>
        public static string GetFileName(this string filePath)
        {
            return Path.GetFileName(filePath);
        }


        /// <summary>
        /// 获取去掉扩展名的文件名；path 为 null 时返回 null。
        /// </summary>
        public static string GetFileNameWithoutExtend(this string filePath)
        {
            return Path.GetFileNameWithoutExtension(filePath);
        }

        /// <summary>
        /// 获取扩展名（含 "."）；无扩展名时返回空字符串，path 为 null 时返回 null。
        /// </summary>
        public static string GetFileExtendName(this string filePath)
        {
            return Path.GetExtension(filePath);
        }



        /// <summary>
        /// 获取所在目录路径。null 或空字符串返回 <see cref="string.Empty"/>；其他输入由 Path.GetDirectoryName 解析。
        /// </summary>
        public static string GetFolderPath(this string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }
            return Path.GetDirectoryName(path);
        }
    }
}

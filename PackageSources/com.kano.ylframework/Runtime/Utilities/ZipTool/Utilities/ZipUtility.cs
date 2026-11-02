using System;
using System.IO;
using YLFramework.Utilities.ZipTool;
using YLFramework.Utilities.ZipTool.Zip;
using YLFramework.Utilities.ZipTool.Zip.Compression;

namespace YLFramework.Compression
{
    /// <summary>
    /// zip 打包 / 解压工具（无 Unity 依赖），基于模块内置的 SharpZipLib 源码实现。
    /// 路径使用 <see cref="Path"/> 处理；临时文件用于隔离写出过程，不保证原子覆盖或整批回滚。
    /// </summary>
    /// <remarks>包装层不提供密码入口，打包不设置密码，也不能传入密码解密条目；CRC 校验不等于来源认证。</remarks>
    public class ZipUtility
    {
        private static readonly StringComparison _pathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// 打包目录为 zip 文件（递归、无过滤），语义同六参重载的默认配置。
        /// </summary>
        public static void ZipFolder(string folderPathToZip, string outputZipFileName)
        {
            ZipFolder(folderPathToZip, outputZipFileName, true, string.Empty);
        }

        /// <summary>
        /// 打包目录为 zip 文件。fileFilter/directoryFilter 为 SharpZipLib NameFilter 表达式
        /// （分号分隔的 .NET 正则，可用 + / - 前缀表示包含 / 排除），null 或空表示不过滤；
        /// compressionLevel 为 null 时使用库默认压缩级别。仅写入文件条目，不写入目录条目。
        /// 输出文件位于待压缩目录内部或已存在时，先打包到系统临时文件、成功后再复制到目标路径：
        /// 避免把正在写出的 zip 再次打包进去，也避免打包阶段的失败破坏已有输出。
        /// 目录中已存在的旧输出仍参与普通扫描，不会自动排除；最终复制允许覆盖，复制失败不保证保留旧输出。
        /// </summary>
        /// <exception cref="ArgumentNullException">路径参数为 null。</exception>
        /// <exception cref="DirectoryNotFoundException">待压缩目录或输出文件所在目录不存在。</exception>
        public static void ZipFolder(string folderPathToZip, string outputZipFileName, bool recurse, string fileFilter, string directoryFilter = null, Deflater.CompressionLevel? compressionLevel = null)
        {
            if (folderPathToZip == null)
            {
                throw new ArgumentNullException(nameof(folderPathToZip));
            }

            if (outputZipFileName == null)
            {
                throw new ArgumentNullException(nameof(outputZipFileName));
            }

            if (!Directory.Exists(folderPathToZip))
            {
                throw new DirectoryNotFoundException("待压缩目录不存在：" + folderPathToZip);
            }

            var fastZip = new FastZip();
            if (compressionLevel.HasValue)
            {
                fastZip.CompressionLevel = compressionLevel.Value;
            }

            string outputFullPath = Path.GetFullPath(outputZipFileName);
            bool outputInsideSource = IsPathInside(outputFullPath, Path.GetFullPath(folderPathToZip));
            if (outputInsideSource || File.Exists(outputFullPath))
            {
                string tempPath = Path.Combine(Path.GetTempPath(), "ylfw_zip_" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    fastZip.CreateZip(tempPath, folderPathToZip, recurse, fileFilter ?? string.Empty, directoryFilter);
                    File.Copy(tempPath, outputFullPath, true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
            else
            {
                fastZip.CreateZip(outputFullPath, folderPathToZip, recurse, fileFilter ?? string.Empty, directoryFilter);
            }
        }

        /// <summary>
        /// 解压 zip 到指定目录，返回实际写出的文件数（不含目录条目与 overwrite=false 跳过的文件）。
        /// 目录条目自动创建，父目录不存在则创建；overwrite=false 时已存在的文件跳过；
        /// 进度按已处理字节/总字节回调（0..1，正常完成时最后一次必为 1）。
        /// 解压前预扫描全部条目：规范化后的字面路径越界（Zip Slip）直接抛 <see cref="InvalidDataException"/>，
        /// 此时不创建目标目录、不写出任何文件；不解析符号链接或重解析点，输出目录须由调用方控制。
        /// 每个文件先写同目录临时文件，再删除旧文件并改名，非原子覆盖；失败时尝试清理临时文件，已提交文件不回滚。
        /// nameCodePage 非 null 时临时切换 <see cref="ZipConstants.DefaultCodePage"/> 解码条目名，
        /// 该值为进程级静态，调用期间非线程安全，方法结束（含异常）后恢复。
        /// </summary>
        public static int UnzipToDirectory(string zipFilePath, string destDirectory, bool overwrite = true, Action<float> onProgress = null, int? nameCodePage = null)
        {
            using (var reader = new ZipArchiveReader(zipFilePath, nameCodePage))
            {
                return reader.ExtractAll(destDirectory, null, overwrite, onProgress);
            }
        }

        /// <summary>
        /// 带配额解压：options.MaxEntryCount/MaxEntryUncompressedBytes/MaxTotalUncompressedBytes 为 0 表示不限制，
        /// 负数抛 <see cref="ArgumentOutOfRangeException"/>。其余语义同
        /// <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>；
        /// 配额先按条目元数据预扫描（此阶段不写任何文件），拷贝过程中再按实际写出字节复核，
        /// 因此条目元数据不可信（谎报大小）时仍会被实际写出量护栏拦截；跳过的已存在文件仍按元数据计入累计量。
        /// </summary>
        public static int UnzipToDirectory(string zipFilePath, string destDirectory, UnzipOptions options, bool overwrite = true, Action<float> onProgress = null, int? nameCodePage = null)
        {
            using (var reader = new ZipArchiveReader(zipFilePath, nameCodePage))
            {
                return reader.ExtractAll(destDirectory, options, overwrite, onProgress);
            }
        }

        /// <summary>
        /// 解压单个条目到指定文件，返回 true 表示文件已写出；条目不存在、条目为目录、
        /// 或 overwrite=false 且目标文件已存在时返回 false。父目录不存在则创建。
        /// entryName 为 zip 内部条目路径（查找不区分大小写）；destFilePath 为调用方指定的最终文件路径，
        /// 条目名不参与目标路径拼接；不限制调用方传入路径所属的目录，也不检查路径上的链接。
        /// 写入采用"临时文件 + 成功后替换"（删除旧文件 + 改名，非原子），改名失败可能丢失旧文件，临时文件会尝试清理。
        /// nameCodePage 说明同 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>。
        /// </summary>
        public static bool ExtractEntry(string zipFilePath, string entryName, string destFilePath, bool overwrite = true, int? nameCodePage = null)
        {
            using (var reader = new ZipArchiveReader(zipFilePath, nameCodePage))
            {
                return reader.ExtractEntry(entryName, destFilePath, overwrite);
            }
        }

        /// <summary>
        /// 按 zip 内顺序返回全部条目名（含目录条目，分隔符保持存储原样）。
        /// nameCodePage 说明同 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>。
        /// </summary>
        public static string[] GetEntryNames(string zipFilePath, int? nameCodePage = null)
        {
            using (var reader = new ZipArchiveReader(zipFilePath, nameCodePage))
            {
                return reader.GetEntryNames();
            }
        }

        /// <summary>
        /// 判断 zip 中是否存在指定条目（比较不区分大小写）。
        /// nameCodePage 说明同 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>。
        /// </summary>
        public static bool ContainsEntry(string zipFilePath, string entryName, int? nameCodePage = null)
        {
            using (var reader = new ZipArchiveReader(zipFilePath, nameCodePage))
            {
                return reader.ContainsEntry(entryName);
            }
        }

        /// <summary>
        /// 读取条目全部字节；条目不存在抛 <see cref="FileNotFoundException"/>（消息含条目名）。
        /// nameCodePage 说明同 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>。
        /// </summary>
        public static byte[] ReadEntryBytes(string zipFilePath, string entryName, int? nameCodePage = null)
        {
            using (var reader = new ZipArchiveReader(zipFilePath, nameCodePage))
            {
                return reader.ReadEntryBytes(entryName);
            }
        }

        /// <summary>
        /// 尝试读取条目全部字节；条目不存在时返回 false 且 bytes 为 null。
        /// nameCodePage 说明同 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>。
        /// </summary>
        public static bool TryReadEntryBytes(string zipFilePath, string entryName, out byte[] bytes, int? nameCodePage = null)
        {
            using (var reader = new ZipArchiveReader(zipFilePath, nameCodePage))
            {
                return reader.TryReadEntryBytes(entryName, out bytes);
            }
        }

        /// <summary>
        /// 打开条目读取流；条目不存在抛 <see cref="FileNotFoundException"/>。
        /// 实现为读取到 MemoryStream 后返回，调用方负责 Dispose；大文件请改用 <see cref="ExtractEntry"/>，
        /// 或使用 <see cref="ZipArchiveReader.OpenEntryStream"/> 流式读取。
        /// nameCodePage 说明同 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>。
        /// </summary>
        public static Stream OpenEntryReadStream(string zipFilePath, string entryName, int? nameCodePage = null)
        {
            return new MemoryStream(ReadEntryBytes(zipFilePath, entryName, nameCodePage), false);
        }

        /// <summary>
        /// 校验 zip 完整性。testData=true 时完整解压并校验 CRC（耗时与解压相当）；false 仅校验头部/结构。
        /// 文件不存在、数据损坏等打开/校验失败返回 false（捕获 SharpZipBaseException / IOException / InvalidDataException），
        /// error 为 null 表示校验通过；zipFilePath 为 null 仍会抛 <see cref="ArgumentNullException"/>。
        /// 注意：普通解压 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>
        /// 不校验 CRC，需要完整性校验时使用本方法；本方法不认证发布者，也不证明归档内容可安全执行。
        /// nameCodePage 说明同 <see cref="UnzipToDirectory(string, string, bool, Action{float}, int?)"/>。
        /// </summary>
        public static bool VerifyArchive(string zipFilePath, out string error, bool testData = true, int? nameCodePage = null)
        {
            error = null;
            using (new CodePageScope(nameCodePage))
            {
                try
                {
                    using (var zipFile = new ZipFile(zipFilePath))
                    {
                        string firstError = null;
                        bool result = zipFile.TestArchive(testData, TestStrategy.FindFirstError, (status, message) =>
                        {
                            if (message != null && firstError == null)
                            {
                                firstError = message;
                            }
                        });

                        error = firstError;
                        if (!result && string.IsNullOrEmpty(error))
                        {
                            error = "zip 校验失败。";
                        }

                        return result;
                    }
                }
                catch (Exception ex) when (ex is SharpZipBaseException || ex is IOException || ex is InvalidDataException)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// 校验 zip 完整性，失败时丢弃错误信息；语义同 <see cref="VerifyArchive(string, out string, bool, int?)"/>。
        /// </summary>
        public static bool VerifyArchive(string zipFilePath, bool testData = true, int? nameCodePage = null)
        {
            return VerifyArchive(zipFilePath, out _, testData, nameCodePage);
        }

        /// <summary>以带末尾分隔符的目录前缀判断规范化路径是否位于其下，不解析文件系统链接。</summary>
        private static bool IsPathInside(string candidateFullPath, string directoryFullPath)
        {
            return candidateFullPath.StartsWith(AppendDirectorySeparator(directoryFullPath), _pathComparison);
        }

        /// <summary>补齐目录末尾分隔符，避免目录前缀比较误认名称相近的同级目录。</summary>
        private static string AppendDirectorySeparator(string directoryPath)
        {
            if (directoryPath.Length > 0)
            {
                char last = directoryPath[directoryPath.Length - 1];
                if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                {
                    return directoryPath;
                }
            }

            return directoryPath + Path.DirectorySeparatorChar;
        }
    }
}

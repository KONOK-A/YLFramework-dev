using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace YLFramework.Resource
{
    /// <summary>服务端未确认续传表示或范围，需要丢弃原分片并从零重试的错误。</summary>
    internal sealed class ResourceRangeException : IOException
    {
        /// <summary>创建续传合同不成立的错误，不自行执行分片重置。</summary>
        internal ResourceRangeException() : base("The server did not confirm the resumable representation; restart from zero.") { }
    }

    /// <summary>解析下载响应的字节表示与范围合同，不执行网络或磁盘操作。</summary>
    internal static class ResourceDownloadResponse
    {
        /// <summary>判断 ETag 是否符合本下载器接受的有界、带引号强校验器形式。</summary>
        internal static bool StrongETag(string value) => value != null && value.Length >= 2 && value.Length <= 256 && value[0] == '"' && value[value.Length - 1] == '"' &&
            !value.Substring(1, value.Length - 2).Contains("\"") && !Array.Exists(value.ToCharArray(), c => c < 0x21 || c > 0x7e);

        /// <summary>验证 identity 编码、长度及续传范围；有效 206 保留偏移，完整 200 从零写入。</summary>
        internal static long Offset(long status, long offset, long length, string validator, Func<string, string> header)
        {
            var encoding = header("Content-Encoding");
            if (!string.IsNullOrEmpty(encoding) && !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Encoded release responses are not accepted.");
            }

            if (status != 200 && status != 206)
            {
                throw new IOException("Release GET did not return 200 or a verified 206.");
            }

            if (status == 206)
            {
                if (offset <= 0 || length <= offset || !StrongETag(validator) || header("ETag") != validator)
                {
                    throw new ResourceRangeException();
                }

                var match = Regex.Match(header("Content-Range") ?? "", @"\Abytes ([0-9]+)-([0-9]+)/([0-9]+)\z", RegexOptions.CultureInvariant);
                if (!match.Success || !Number(match.Groups[1].Value, out var start) || !Number(match.Groups[2].Value, out var end) ||
                    !Number(match.Groups[3].Value, out var total) || start != offset || total != length || end != length - 1)
                {
                    throw new ResourceRangeException();
                }
            }
            else
            {
                offset = 0;
                if (!string.IsNullOrEmpty(header("Content-Range")))
                {
                    throw new InvalidDataException("An unsolicited Content-Range is not a full response.");
                }
            }
            var contentLength = header("Content-Length");
            if (contentLength != null && (!Number(contentLength, out var count) || length > 0 && count != length - offset))
            {
                throw new InvalidDataException("Release response Content-Length mismatch.");
            }

            return offset;
        }

        /// <summary>按固定区域性解析无符号格式的非负十进制长度或秒数。</summary>
        internal static bool Number(string value, out long number) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    }
}

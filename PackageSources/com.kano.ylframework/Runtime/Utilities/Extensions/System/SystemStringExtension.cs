using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace YLFramework.Extensions
{
    /// <summary>
    /// string 与 StringBuilder 扩展。
    /// 注意：ToInt / ToLong / ToFloat / ToDateTime 使用当前 Culture 解析；解析失败返回默认值，不抛异常。
    /// </summary>
    public static class SystemStringExtension
    {

        /// <summary>判断字符串是否为 null 或空串（等价于 <see cref="string.IsNullOrEmpty"/>）。</summary>
        public static bool IsNullOrEmpty(this string selfStr)
        {
            return string.IsNullOrEmpty(selfStr);
        }

        /// <summary>判断字符串是否非 null 且非空串。</summary>
        public static bool IsNotNullAndEmpty(this string selfStr)
        {
            return !string.IsNullOrEmpty(selfStr);
        }


        /// <summary>
        /// 为 null 时返回 true，否则用 Trim 去掉两端空白字符后判断是否为空串，不修改原字符串。
        /// </summary>
        public static bool IsTrimNullOrEmpty(this string selfStr)
        {
            return selfStr == null || string.IsNullOrEmpty(selfStr.Trim());
        }

        /// <summary>
        /// 为 null 时返回 false，否则用 Trim 去掉两端空白字符后判断是否非空，不修改原字符串。
        /// </summary>
        public static bool IsTrimNotNullAndEmpty(this string selfStr)
        {
            return selfStr != null && !string.IsNullOrEmpty(selfStr.Trim());
        }


        /// <summary>
        /// 按单个字符分割字符串（默认 StringSplitOptions.None）；null 输入保持历史语义，抛 <see cref="NullReferenceException"/>。
        /// 注：当前 .NET Standard 2.1 下 <c>"a,b".Split(',')</c> 会优先绑定 BCL 实例方法；
        /// 本扩展可通过 <c>SystemStringExtension.Split(s, ',')</c> 显式调用。
        /// </summary>
        public static string[] Split(this string selfStr, char splitSymbol)
        {
            // 每次调用单独分配，避免共享静态数组在并发/重入时互相污染
            return selfStr.Split(new[] { splitSymbol });
        }

        /// <summary>
        /// 使用当前 Culture 做 string.Format 填充。
        /// </summary>
        public static string FillFormat(this string selfStr, params object[] args)
        {
            return string.Format(selfStr, args);
        }


        /// <summary>
        /// 用当前字符串创建一个新的 StringBuilder。
        /// </summary>
        public static StringBuilder Builder(this string selfStr)
        {
            return new StringBuilder(selfStr);
        }


        /// <summary>
        /// 在 StringBuilder 开头插入前缀，返回原实例。
        /// </summary>
        public static StringBuilder AddPrefix(this StringBuilder self, string prefixString)
        {
            self.Insert(0, prefixString);
            return self;
        }

        /// <summary>
        /// 解析为 int，失败返回 defaultValue。使用当前 Culture。
        /// </summary>
        public static int ToInt(this string selfStr, int defaultValue = 0)
        {
            return int.TryParse(selfStr, out var retValue) ? retValue : defaultValue;
        }

        /// <summary>
        /// 解析为 long，失败返回 defaultValue。使用当前 Culture。
        /// </summary>
        public static long ToLong(this string selfStr, long defaultValue = 0)
        {
            return long.TryParse(selfStr, out var retValue) ? retValue : defaultValue;
        }
        /// <summary>
        /// 解析为 DateTime，失败返回 defaultValue。使用当前 Culture。
        /// </summary>
        public static DateTime ToDateTime(this string selfStr, DateTime defaultValue = default)
        {
            return DateTime.TryParse(selfStr, out var retValue) ? retValue : defaultValue;
        }

        /// <summary>
        /// 解析为 float，失败返回 defaultValue。使用当前 Culture。
        /// </summary>
        public static float ToFloat(this string selfStr, float defaultValue = 0)
        {
            return float.TryParse(selfStr, out var retValue) ? retValue : defaultValue;
        }
        /// <summary>
        /// 是否包含中文字符（仅匹配 CJK 基本区 \u4e00-\u9fa5）。
        /// </summary>
        public static bool HasChinese(this string input)
        {
            return Regex.IsMatch(input, @"[\u4e00-\u9fa5]");
        }

        /// <summary>
        /// 是否包含普通空格字符 U+0020，不匹配制表符、换行等其他空白；null 输入会抛异常。
        /// </summary>
        public static bool HasSpace(this string input)
        {
            return input.Contains(" ");
        }

        /// <summary>
        /// 移除类名中的非法字符：/ @ ! &amp; - ( ) # 会被删除，空格替换为 _，连续下划线做三轮折叠（极长连续下划线可能残留多个）。
        /// 仅执行这些固定替换，不保证结果是合法的 C# 标识符。
        /// </summary>
        public static string RemoveInvalidCharsFromClassName(this string className)
        {
            return className
                .Replace("/", "")
                .Replace("@", "")
                .Replace("!", "")
                .Replace(" ", "_")
                .Replace("__", "_")
                .Replace("__", "_")
                .Replace("__", "_")
                .Replace("&", "")
                .Replace("-", "")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("#", "");
        }

        /// <summary>
        /// 依次移除字符串中的多个目标子串。
        /// </summary>
        public static string RemoveString(this string str, params string[] targets)
        {
            return targets.Aggregate(str, (current, t) => current.Replace(t, string.Empty));
        }

        /// <summary>
        /// 用分隔符连接字符串序列。
        /// </summary>
        public static string StringJoin(this IEnumerable<string> self, string separator)
        {
            return string.Join(separator, self);
        }
    }
}

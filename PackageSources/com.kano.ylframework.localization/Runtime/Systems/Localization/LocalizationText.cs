using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YLFramework.Localization
{
    /// <summary>格式参数或追加片段的内容来源。</summary>
    public enum AppendType
    {
        /// <summary>通过本地化 ID 查询文本。</summary>
        Id,
        /// <summary>直接使用片段中保存的字符串。</summary>
        String
    }
    /// <summary>描述一个格式参数或追加片段，以及可选的单值格式包装。</summary>
    [Serializable]
    public struct AppendData
    {
        /// <summary>决定从本地化查询还是字面字符串取值。</summary>
        public AppendType type;
        /// <summary>按 ID 取值时使用的本地化条目键。</summary>
        public int id;
        /// <summary>直接字符串取值时的片段内容，空引用按空串处理。</summary>
        public string str;
        /// <summary>可选格式模板，以 {0} 接收片段文本；这里只格式化，不解析或校验富文本标签。</summary>
        public string richText;
    }

    /// <summary>原参数、追加文本和richText工作流的纯逻辑部分；每个包装只格式化一次。</summary>
    public static class LocalizationText
    {
        /// <summary>解析参数后格式化主模板，再按顺序追加片段，使用当前文化执行格式化。</summary>
        /// <param name="id">主文本模板的本地化 ID。</param>
        /// <param name="lookup">文本查询委托，查询结果为 null 时按空串处理。</param>
        /// <param name="args">主模板格式参数；无参数时不格式化主模板。</param>
        /// <param name="appends">主文本后的追加片段，可为空引用。</param>
        /// <exception cref="FormatException">模板或片段包装格式不合法，异常包含主文本 ID。</exception>
        public static string Compose(int id, Func<int, string> lookup, IList<AppendData> args, IList<AppendData> appends)
        {
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }

            try
            {
                var template = lookup(id) ?? string.Empty;
                var values = GetArguments(lookup, args);
                var builder = new StringBuilder(values.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, values));
                if (appends != null)
                {
                    foreach (var item in appends)
                    {
                        builder.Append(GetValue(lookup, item));
                    }
                }

                return builder.ToString();
            }
            catch (FormatException error)
            {
                throw new FormatException($"Localization ID={id}: invalid argument/append format.", error);
            }
        }

        /// <summary>按列表顺序解析参数内容并应用各自包装，不格式化主文本。</summary>
        /// <param name="lookup">ID 参数使用的文本查询委托。</param>
        /// <param name="args">待解析的参数列表，空引用或空列表返回空数组。</param>
        public static string[] GetArguments(Func<int, string> lookup, IList<AppendData> args)
        {
            if (args == null || args.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[args.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = GetValue(lookup, args[i]);
            }

            return result;
        }

        /// <summary>取得单个片段的查询值或字面值，并在配置了包装时仅格式化该包装一次。</summary>
        private static string GetValue(Func<int, string> lookup, AppendData item)
        {
            var value = item.type == AppendType.Id ? lookup(item.id) ?? string.Empty : item.str ?? string.Empty;
            return string.IsNullOrEmpty(item.richText) ? value : string.Format(CultureInfo.CurrentCulture, item.richText, value);
        }
    }
}

using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 将 HTML 颜色文本解析为 Unity 颜色，并为解析失败提供黑色结果。
    /// </summary>
    public static class HtmlColorExtensions
    {
        /// <summary>
        /// 将 HTML 颜色字符串（如 "#RRGGBB"、"#RRGGBBAA"）解析为 Color，解析失败返回 <see cref="Color.black"/>。
        /// 解析由 <see cref="ColorUtility"/> 完成，不受当前 Culture 影响。
        /// </summary>
        /// <param name="htmlString">HTML 颜色字符串。</param>
        /// <returns>解析结果或黑色。</returns>
        public static Color HtmlStringToColor(this string htmlString)
        {
            var parseSucceed = ColorUtility.TryParseHtmlString(htmlString, out var retColor);
            return parseSucceed ? retColor : Color.black;
        }
    }
}

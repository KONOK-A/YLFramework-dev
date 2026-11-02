using UnityEngine;


namespace YLFramework.Utilities
{
    /// <summary>
    /// 将字节长度和进度数值格式化为用于显示的字符串。
    /// </summary>
    public class StringTools
    {

        private const int Kilobyte = 1024;
        private const int Megabyte = Kilobyte * 1024;
        private const int Gigabyte = Megabyte * 1024;
        private const long Terabyte = Gigabyte * (long)1024;


        /// <summary>
        /// 按 1024 进制和当前 Culture 格式化字节，单位为 b/kb/mb/gb/tb；TB 以上继续以 tb 显示。
        /// </summary>
        /// <param name="length">字节长度</param>
        public static string FormatByte(long length)
        {
            if (length < Kilobyte)
            {
                return string.Format("{0}b", length);
            }
            else if (length >= Kilobyte && length < Megabyte)
            {
                return string.Format("{0:N2}kb", length / (double)Kilobyte);
            }
            else if (length >= Megabyte && length < Gigabyte)
            {
                return string.Format("{0:N2}mb", length / (double)Megabyte);
            }
            else if (length >= Gigabyte && length < Terabyte)
            {
                return string.Format("{0:N2}gb", length / (double)Gigabyte);
            }

            return string.Format("{0:N2}tb", length / (double)Terabyte);
        }



        /// <summary>
        /// 通过 Mathf.Lerp 将进度换算为百分数字符串，有限的区间外数值按 0 或 100 处理。
        /// 数字使用当前 Culture 的默认格式，不固定小数位数。
        /// </summary>
        /// <param name="progress">进度值，通常为 0..1。</param>
        /// <returns>以百分号结尾的进度文本。</returns>
        public static string FormatProgress(float progress)
        {
            return string.Format("{0}%", Mathf.Lerp(0, 100, progress));
        }

    }

}

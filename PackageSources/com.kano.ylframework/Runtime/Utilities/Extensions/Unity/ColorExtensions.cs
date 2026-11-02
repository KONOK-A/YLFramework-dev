using UnityEngine;

namespace YLFramework.Extensions
{

    /// <summary>
    /// 将 RGBA 颜色作为四维向量，按分量候选比例匹配插值颜色。
    /// </summary>
    public static class ColorExtensions
    {

        /// <summary>
        /// 依次从 r、g、b、a 分量求 0..1 的候选比例，返回首个大于 0 且重建颜色的四维距离不超过 0.01 的比例。
        /// 未匹配时返回 0；不进行颜色空间转换，结果不是感知色差或投影比例。
        /// </summary>
        /// <param name="from">插值起始颜色。</param>
        /// <param name="to">插值结束颜色。</param>
        /// <param name="current">待匹配颜色，须与端点采用相同的颜色空间。</param>
        /// <returns>首个通过 RGBA 距离校验的正比例，或未匹配时的 0。</returns>
        public static float InverseLerp(Color from, Color to, Color current)
        {

            Vector4 fromColor = new Vector4(from.r, from.g, from.b, from.a);
            Vector4 toColor = new Vector4(to.r, to.g, to.b, to.a);
            Vector4 currentColor = new Vector4(current.r, current.g, current.b, current.a);

            float lerp = Mathf.InverseLerp(fromColor.x, toColor.x, currentColor.x);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector4.Distance(currentColor, Vector4.Lerp(fromColor, toColor, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            lerp = Mathf.InverseLerp(fromColor.y, toColor.y, currentColor.y);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector4.Distance(currentColor, Vector4.Lerp(fromColor, toColor, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            lerp = Mathf.InverseLerp(fromColor.z, toColor.z, currentColor.z);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector4.Distance(currentColor, Vector4.Lerp(fromColor, toColor, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            lerp = Mathf.InverseLerp(fromColor.w, toColor.w, currentColor.w);

            if (lerp > 0 && lerp <= 1)
            {
                if (Vector4.Distance(currentColor, Vector4.Lerp(fromColor, toColor, lerp)) <= 0.01f)
                {
                    return lerp;
                }
            }

            return 0;
        }

    }

}

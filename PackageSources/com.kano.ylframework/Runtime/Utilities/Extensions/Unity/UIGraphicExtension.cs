using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Extensions
{
    /// <summary>
    /// UI Graphic 与 SpriteRenderer 的颜色、填充扩展。
    /// </summary>
    public static class UIGraphicExtension
    {
        /// <summary>
        /// 修改 Graphic 的 alpha（保留 RGB），返回原对象以支持链式调用。
        /// </summary>
        /// <param name="selfGraphic">目标 Graphic。</param>
        /// <param name="alpha">新的 alpha 值，直接写入不做范围限制。</param>
        public static T ColorAlpha<T>(this T selfGraphic, float alpha) where T : Graphic
        {
            var color = selfGraphic.color;
            color.a = alpha;
            selfGraphic.color = color;
            return selfGraphic;
        }

        /// <summary>
        /// 设置 Image.fillAmount，返回原对象以支持链式调用；填充显示仅适用于 Filled 类型的 Image。
        /// </summary>
        /// <param name="selfImage">目标 Image。</param>
        /// <param name="fillAmount">传给 Image.fillAmount 的填充比例，本扩展不额外校验。</param>
        public static Image FillAmount(this Image selfImage, float fillAmount)
        {
            selfImage.fillAmount = fillAmount;
            return selfImage;
        }

        /// <summary>
        /// 修改 SpriteRenderer 的 alpha（保留 RGB），返回原对象以支持链式调用。
        /// </summary>
        /// <param name="selfRenderer">目标 SpriteRenderer。</param>
        /// <param name="alpha">新的 alpha 值，直接写入不做范围限制。</param>
        public static SpriteRenderer ColorAlpha(this SpriteRenderer selfRenderer, float alpha)
        {
            var color = selfRenderer.color;
            color.a = alpha;
            selfRenderer.color = color;
            return selfRenderer;
        }
    }
}

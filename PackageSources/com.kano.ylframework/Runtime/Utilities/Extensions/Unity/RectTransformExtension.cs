using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 以 anchoredPosition 链式修改 RectTransform 相对于锚点参考点的位置，而非世界坐标。
    /// </summary>
    public static class RectTransformExtension
    {
        /// <summary>
        /// 只修改 anchoredPosition.y（保留 x），返回原对象。
        /// </summary>
        /// <param name="selfRectTrans">目标 RectTransform。</param>
        /// <param name="anchoredPositionY">新的 anchoredPosition.y。</param>
        public static RectTransform AnchoredPositionY(this RectTransform selfRectTrans, float anchoredPositionY)
        {
            var anchorPos = selfRectTrans.anchoredPosition;
            anchorPos.y = anchoredPositionY;
            selfRectTrans.anchoredPosition = anchorPos;
            return selfRectTrans;
        }
    }
}

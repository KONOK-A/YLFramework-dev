using UnityEngine;

namespace YLFramework.Foundation
{
    /// <summary>
    /// LayerMask 判断工具。
    /// </summary>
    public static class LayerMaskUtility
    {
        /// <summary>
        /// 判断层掩码是否包含指定层；非法层号返回 false，不进行位移取模。
        /// </summary>
        public static bool Contains(this LayerMask layerMask, int layer)
        {
            return IsInLayerMask(layer, layerMask);
        }

        /// <summary>按层名判断；未定义层名返回 false。</summary>
        public static bool Contains(this LayerMask layerMask, string layerName)
        {
            return layerMask.Contains(LayerMask.NameToLayer(layerName));
        }

        /// <summary>
        /// 判断 layer 是否包含在 layerMask 中。合法层号为 0..31，越界直接返回 false（避免位移按 32 取模）。
        /// </summary>
        /// <param name="layer">层号。</param>
        /// <param name="layerMask">层掩码。</param>
        public static bool IsInLayerMask(int layer, LayerMask layerMask)
        {
            // Unity 只有 32 个层；-1 / 32 会让 C# 位移按 32 取模，产生错误掩码
            if (layer < 0 || layer > 31)
            {
                return false;
            }

            var objLayerMask = 1 << layer;
            return (layerMask.value & objLayerMask) == objLayerMask;
        }

        /// <summary>
        /// 判断 GameObject 所在层是否包含在 layerMask 中；gameObj 为 C# null 或已销毁对象时返回 false。
        /// </summary>
        /// <param name="gameObj">目标对象。</param>
        /// <param name="layerMask">层掩码。</param>
        public static bool IsInLayerMask(GameObject gameObj, LayerMask layerMask)
        {
            if (gameObj == null)
            {
                return false;
            }

            return IsInLayerMask(gameObj.layer, layerMask);
        }
    }
}

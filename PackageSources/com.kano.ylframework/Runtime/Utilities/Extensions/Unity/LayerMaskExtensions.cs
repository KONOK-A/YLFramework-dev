using System;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.Extensions
{
    /// <summary>
    /// 原地修改层掩码的指定位，或用当前物理层碰撞关系生成掩码；不修改物理设置。
    /// </summary>
    public static class LayerMaskExtensions
    {
        /// <summary>
        /// 打开或关闭某个层级；未定义层名抛 ArgumentOutOfRangeException，掩码不变。
        /// </summary>
        /// <param name="layerMask">按引用修改的层掩码。</param>
        /// <param name="layerName">层级名称</param>
        /// <param name="open">true 表示置位，false 表示清除该位。</param>
        public static void Set(this ref LayerMask layerMask, string layerName, bool open)
        {
            layerMask.Set(LayerMask.NameToLayer(layerName), open);
        }

        /// <summary>
        /// 打开或关闭 0..31 的层级；非法层号抛 ArgumentOutOfRangeException，掩码不变。
        /// </summary>
        /// <param name="layerMask">按引用修改的层掩码。</param>
        /// <param name="layer">层级</param>
        /// <param name="open">true 表示置位，false 表示清除该位。</param>
        public static void Set(this ref LayerMask layerMask, int layer, bool open)
        {
            ValidateLayer(layer);
            if (open)
            {
                // 打开某个层级
                layerMask = layerMask.value | (1 << layer);
            }
            else
            {
                // 关闭某个层级
                layerMask = layerMask.value & ~(1 << layer);
            }

        }

        /// <summary>
        /// 遍历 32 层，用当前 Physics2D 设置中与 layer 可碰撞且已命名的层覆盖掩码。
        /// 非法层号抛 ArgumentOutOfRangeException，原掩码不变；未命名的层不会纳入结果。
        /// </summary>
        /// <param name="layerMask">接收碰撞层集合的掩码，原有位不保留。</param>
        /// <param name="layer">待查询碰撞关系的层号，范围为 0..31。</param>
        public static void Physics2DSetting(this ref LayerMask layerMask, int layer)
        {
            ValidateLayer(layer);

            List<string> layers = new List<string>();

            // 遍历所有层级
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                // 跳过空的层级
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                bool ignore = Physics2D.GetIgnoreLayerCollision(layer, i);
                // 跳过不能碰撞的层级
                if (ignore)
                {
                    continue;
                }

                layers.Add(name);
            }

            layerMask = LayerMask.GetMask(layers.ToArray());
        }

        /// <summary>
        /// 遍历 32 层，用当前 Physics 设置中与 layer 可碰撞且已命名的层覆盖掩码。
        /// 非法层号抛 ArgumentOutOfRangeException，原掩码不变；未命名的层不会纳入结果。
        /// </summary>
        /// <param name="layerMask">接收碰撞层集合的掩码，原有位不保留。</param>
        /// <param name="layer">待查询碰撞关系的层号，范围为 0..31。</param>
        public static void PhysicsSetting(this ref LayerMask layerMask, int layer)
        {
            ValidateLayer(layer);

            List<string> layers = new List<string>();

            // 遍历所有层级
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                // 跳过空的层级
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                bool ignore = Physics.GetIgnoreLayerCollision(layer, i);
                // 跳过不能碰撞的层级
                if (ignore)
                {
                    continue;
                }

                layers.Add(name);
            }

            layerMask = LayerMask.GetMask(layers.ToArray());
        }

        /// <summary>在位运算或物理查询前校验层号属于 0..31，越界时抛出异常。</summary>
        private static void ValidateLayer(int layer)
        {
            if (layer < 0 || layer > 31)
            {
                throw new ArgumentOutOfRangeException(nameof(layer), layer, "层号必须在 0..31 范围内。");
            }
        }

    }

}

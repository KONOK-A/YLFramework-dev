using System;

namespace YLFramework.Save
{
    using UnityEngine;

    /// <summary>
    /// <see cref="Color"/> 的可序列化替身：以四个 float 分量存储，便于存档 JSON 或 Unity 序列化读写颜色。
    /// 支持与 <see cref="Color"/> 的隐式互转。
    /// </summary>
    [Serializable]
    public struct SerializedColor
    {
        /// <summary>红色分量。</summary>
        public float r;

        /// <summary>绿色分量。</summary>
        public float g;

        /// <summary>蓝色分量。</summary>
        public float b;

        /// <summary>Alpha 分量。</summary>
        public float a;

        /// <summary>
        /// 按分量创建颜色。
        /// </summary>
        /// <param name="r">红色分量。</param>
        /// <param name="g">绿色分量。</param>
        /// <param name="b">蓝色分量。</param>
        /// <param name="a">Alpha 分量。</param>
        public SerializedColor(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        /// <summary>
        /// 返回 <c>(r, g, b, a)</c> 形式的字符串，便于调试输出。
        /// </summary>
        public override string ToString()
        {
            return $"({r}, {g}, {b}, {a})";
        }

        /// <summary>
        /// 哈希值委托给等值 <see cref="Color"/> 的 <see cref="Color.GetHashCode"/>。
        /// </summary>
        public override int GetHashCode()
        {
            return this.ConvertToColor().GetHashCode();
        }


        /// <summary>从 <see cref="Color"/> 隐式转换（逐分量复制）。</summary>
        /// <param name="color">要复制各分量的 Unity 颜色。</param>
        public static implicit operator SerializedColor(Color color)
        {
            return new SerializedColor(color.r, color.g, color.b, color.a);
        }

        /// <summary>隐式转换回 <see cref="Color"/>（逐分量复制）。</summary>
        /// <param name="color">保存颜色分量的序列化值。</param>
        public static implicit operator Color(SerializedColor color)
        {
            return new Color(color.r, color.g, color.b, color.a);
        }
    }

    /// <summary>
    /// <see cref="SerializedColor"/> 与 <see cref="Color"/> 的显式转换扩展方法；
    /// 与隐式转换运算符等价，提供可读性更强的调用形式。
    /// </summary>
    public static class SerializedColorExtensions
    {
        /// <summary>
        /// 转换回 <see cref="Color"/>（等价于隐式转换运算符）。
        /// </summary>
        /// <param name="serializedColor">源颜色。</param>
        /// <returns>等值的 <see cref="Color"/>。</returns>
        public static Color ConvertToColor(this SerializedColor serializedColor)
        {
            return new Color(serializedColor.r, serializedColor.g, serializedColor.b, serializedColor.a);
        }

        /// <summary>
        /// 转换为 <see cref="SerializedColor"/>（等价于隐式转换运算符）。
        /// </summary>
        /// <param name="color">源颜色。</param>
        /// <returns>等值的 <see cref="SerializedColor"/>。</returns>
        public static SerializedColor ConvertToSerializedColor(this Color color)
        {
            return new SerializedColor(color.r, color.g, color.b, color.a);
        }
    }
}

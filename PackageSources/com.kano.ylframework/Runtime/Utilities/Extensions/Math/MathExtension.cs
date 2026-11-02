using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 数学与数字扩展：随机取项、角度/弧度换算、三角函数、方向换算。
    /// </summary>
    public static class MathExtension
    {
        /// <summary>
        /// 随机返回 List 中的一个元素；list 为空时抛 <see cref="System.ArgumentOutOfRangeException"/>。
        /// </summary>
        public static T GetRandomItem<T>(this List<T> list)
        {
            return list[UnityEngine.Random.Range(0, list.Count)];
        }

        /// <summary>
        /// 随机取出 List 中的一个元素，并将其从 List 中移除；list 为空时抛 <see cref="System.ArgumentOutOfRangeException"/>。
        /// </summary>
        public static T GetAndRemoveRandomItem<T>(this List<T> list)
        {
            var randomIndex = UnityEngine.Random.Range(0, list.Count);
            var randomItem = list[randomIndex];
            list.RemoveAt(randomIndex);
            return randomItem;
        }

        /// <summary>
        /// 修改 SpriteRenderer 的 alpha（保留 RGB），等价于 <see cref="UIGraphicExtension.ColorAlpha(SpriteRenderer, float)"/>，返回原对象。
        /// </summary>
        public static SpriteRenderer Alpha(this SpriteRenderer self, float alpha)
        {
            return self.ColorAlpha(alpha);
        }

        /// <summary>
        /// 以 self 作为插值系数 t，在 a 与 b 之间插值；t 会被 clamp 到 0~1。
        /// </summary>
        public static float Lerp(this float self, float a, float b)
        {
            return Mathf.Lerp(a, b, self);
        }

        /// <summary>绝对值。</summary>
        public static float Abs(this float self)
        {
            return Mathf.Abs(self);
        }

        /// <summary>绝对值，结果为 float。</summary>
        public static float Abs(this int self)
        {
            return Mathf.Abs(self);
        }

        /// <summary>符号：负数返回 -1，其余（含 0）返回 1。</summary>
        public static float Sign(this float self)
        {
            return Mathf.Sign(self);
        }

        /// <summary>符号：负数返回 -1，其余（含 0）返回 1。</summary>
        public static float Sign(this int self)
        {
            return Mathf.Sign(self);
        }

        /// <summary>把 self 视为弧度求 cosine。</summary>
        public static float CosAsRad(this float self)
        {
            return Mathf.Cos(self);
        }

        /// <summary>把 self 视为弧度求 cosine。</summary>
        public static float CosAsRad(this int self)
        {
            return Mathf.Cos(self);
        }

        /// <summary>把 self 视为弧度求 sine。</summary>
        public static float SinAsRad(this float self)
        {
            return Mathf.Sin(self);
        }

        /// <summary>把 self 视为弧度求 sine。</summary>
        public static float SinAsRad(this int self)
        {
            return Mathf.Sin(self);
        }

        /// <summary>把 self 视为角度求 cosine。</summary>
        public static float CosAsDeg(this float self)
        {
            return Mathf.Cos(self * Mathf.Deg2Rad);
        }

        /// <summary>把 self 视为角度求 cosine。</summary>
        public static float CosAsDeg(this int self)
        {
            return Mathf.Cos(self * Mathf.Deg2Rad);
        }

        /// <summary>把 self 视为角度求 sine。</summary>
        public static float SinAsDeg(this float self)
        {
            return Mathf.Sin(self * Mathf.Deg2Rad);
        }

        /// <summary>把 self 视为角度求 sine。</summary>
        public static float SinAsDeg(this int self)
        {
            return Mathf.Sin(self * Mathf.Deg2Rad);
        }

        /// <summary>角度转弧度。</summary>
        public static float Deg2Rad(this float self)
        {
            return self * Mathf.Deg2Rad;
        }

        /// <summary>角度转弧度。</summary>
        public static float Deg2Rad(this int self)
        {
            return self * Mathf.Deg2Rad;
        }

        /// <summary>弧度转角度。</summary>
        public static float Rad2Deg(this float self)
        {
            return self * Mathf.Rad2Deg;
        }

        /// <summary>弧度转角度。</summary>
        public static float Rad2Deg(this int self)
        {
            return self * Mathf.Rad2Deg;
        }

        /// <summary>把角度值转为 2D 单位方向向量。</summary>
        public static Vector2 AngleToDirection2D(this int self)
        {
            return new Vector2(self.CosAsDeg(), self.SinAsDeg());
        }

        /// <summary>把角度值转为 2D 单位方向向量。</summary>
        public static Vector2 AngleToDirection2D(this float self)
        {
            return new Vector2(self.CosAsDeg(), self.SinAsDeg());
        }

        /// <summary>把弧度值转为 2D 单位方向向量。</summary>
        public static Vector2 RadiusToDirection2D(this int self)
        {
            return new Vector2(self.CosAsRad(), self.SinAsRad());
        }

        /// <summary>把弧度值转为 2D 单位方向向量。</summary>
        public static Vector2 RadiusToDirection2D(this float self)
        {
            return new Vector2(self.CosAsRad(), self.SinAsRad());
        }

        /// <summary>求 2D 方向向量的角度（Atan2，角度制）；零向量返回 0。</summary>
        public static float ToAngle(this Vector2 self)
        {
            return Mathf.Atan2(self.y, self.x).Rad2Deg();
        }

        /// <summary>求 2D 方向向量的弧度（Atan2）；零向量返回 0。</summary>
        public static float ToRadius(this Vector2 self)
        {
            return Mathf.Atan2(self.y, self.x);
        }
    }
}

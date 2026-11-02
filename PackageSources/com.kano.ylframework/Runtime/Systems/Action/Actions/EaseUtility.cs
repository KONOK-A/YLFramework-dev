using System;
using UnityEngine;

namespace YLFramework.Actions
{
    /// <summary>可选的缓动曲线；保留现有成员名和数值，Count 表示曲线数量。</summary>
    public enum EaseType
    {
        /// <summary>线性插值，进度钳制到 0 到 1。</summary>
        Linear,
        /// <summary>先在起点回退、再在终点超出的缓入缓出曲线。</summary>
        InOutBack,
        /// <summary>起点附近先回退再加速的缓入曲线。</summary>
        InBack,
        /// <summary>超出终点后回落的缓出曲线。</summary>
        OutBack,
        /// <summary>组合前后半程弹跳的缓入缓出曲线。</summary>
        InOutBounce,
        /// <summary>终点附近多次弹跳的缓出曲线。</summary>
        OutBounce,
        /// <summary>沿用现有反向弹跳公式的曲线，不保证精确落在端点。</summary>
        InBounce,
        /// <summary>圆弧形状的缓入缓出曲线。</summary>
        InOutCircle,
        /// <summary>圆弧形状的缓出曲线。</summary>
        OutCircle,
        /// <summary>圆弧形状的缓入曲线。</summary>
        InCircle,
        /// <summary>三次幂形状的缓入缓出曲线。</summary>
        InOutCubic,
        /// <summary>三次幂形状的缓出曲线。</summary>
        OutCubic,
        /// <summary>三次幂形状的缓入曲线。</summary>
        InCubic,
        /// <summary>前后半程均可超出端点的弹性缓入缓出曲线。</summary>
        InOutElastic,
        /// <summary>在终点附近振荡并收敛的弹性缓出曲线。</summary>
        OutElastic,
        /// <summary>在起点附近振荡后加速的弹性缓入曲线。</summary>
        InElastic,
        /// <summary>指数形状的缓入缓出曲线，沿用未精确归一到端点的公式。</summary>
        InOutExpo,
        /// <summary>指数形状的缓出曲线，终点仍保留公式中的微小偏差。</summary>
        OutExpo,
        /// <summary>指数形状的缓入曲线，起点仍保留公式中的微小偏差。</summary>
        InExpo,
        /// <summary>二次幂形状的缓入缓出曲线。</summary>
        InOutQuad,
        /// <summary>二次幂形状的缓出曲线。</summary>
        OutQuad,
        /// <summary>二次幂形状的缓入曲线。</summary>
        InQuad,
        /// <summary>四次幂形状的缓入缓出曲线。</summary>
        InoutQuart,
        /// <summary>四次幂形状的缓出曲线。</summary>
        OutQuart,
        /// <summary>四次幂形状的缓入曲线。</summary>
        InQuart,
        /// <summary>五次幂形状的缓入缓出曲线。</summary>
        InOutQuint,
        /// <summary>五次幂形状的缓出曲线。</summary>
        OutQuint,
        /// <summary>五次幂形状的缓入曲线。</summary>
        InQuint,
        /// <summary>正弦形状的缓入缓出曲线。</summary>
        InOutSine,
        /// <summary>正弦形状的缓出曲线。</summary>
        OutSine,
        /// <summary>正弦形状的缓入曲线。</summary>
        InSine,
        /// <summary>曲线数量的哨兵值，不表示可计算的缓动曲线。</summary>
        Count
    }

    /// <summary>根据起点、终点和归一化进度计算缓动插值。</summary>
    /// <remarks>
    /// 各方法参数含义一致：start 为起始值，end 为结束值，pos 为归一化进度（通常取 0 到 1）；
    /// 除 Linear 使用 Mathf.Lerp 外，其余曲线不统一钳制输入。bounciness、elasticity、duration 是曲线形状参数，
    /// 只影响计算结果，不参与动作计时。In、Out、InOut 分别表示缓入、缓出、缓入缓出；弹性和回退曲线可超出端点范围。
    /// 此类为无状态纯计算工具，不依赖执行器，也不缓存结果。
    /// </remarks>
    public class EaseUtility
    {
        /// <summary>计算线性插值，将进度钳制到 0 到 1。</summary>
        /// <param name="start">起始值。</param>
        /// <param name="end">结束值。</param>
        /// <param name="pos">归一化插值进度。</param>
        public static float Linear(float start, float end, float pos)
        {
            return Mathf.Lerp(start, end, pos);
        }

        /// <summary>计算带回退的缓入缓出插值，bounciness 控制回退幅度。</summary>
        public static float InOutBack(float start, float end, float pos, float bounciness = 1.5f)
        {
            var mid = (start + end) * 0.5f;
            return pos < 0.5f
                ? InBack(start, mid, pos * 2, bounciness)
                : OutBack(mid, end, (pos - 0.5f) * 2, bounciness);
        }

        /// <summary>计算起点附近先回退再加速的插值，bounciness 控制回退幅度。</summary>
        public static float InBack(float start, float end, float pos, float bounciness = 1.5f)
        {
            var distance = end - start;
            return distance * pos * pos * ((bounciness + 1) * pos - bounciness) + start;
        }

        /// <summary>计算超出终点后回落的缓出插值，bounciness 控制超出幅度。</summary>
        public static float OutBack(float start, float end, float pos, float bounciness = 1.5f)
        {
            var distance = end - start;
            pos -= 1;
            return distance * (pos * pos * ((bounciness + 1) * pos + bounciness) + 1) + start;
        }

        /// <summary>组合前后半程的弹跳插值；此方法保留 bounciness 参数，但当前实现使用子曲线默认值。</summary>
        public static float InOutBounce(float start, float end, float pos, float bounciness = 7.5625f)
        {
            return pos < 0.5f
                ? InBounce(start, (start + end) / 2, pos * 2)
                : OutBounce((start + end) / 2, end, (pos - 0.5f) * 2);
        }

        /// <summary>计算在终点附近多次弹跳的缓出插值，bounciness 控制各段幅度。</summary>
        public static float OutBounce(float start, float end, float pos, float bounciness = 7.5625f)
        {
            var distance = end - start;
            if (pos < 1f / 2.75f)
            {
                return distance * (bounciness * pos * pos) + start;
            }

            if (pos < 2f / 2.75f)
            {
                pos -= 1.5f / 2.75f;
                return distance * (bounciness * pos * pos + 3f / 4f) + start;
            }

            if (pos < 2.5f / 2.75f)
            {
                pos -= 2.25f / 2.75f;
                return distance * (bounciness * pos * pos + 15f / 16f) + start;
            }

            pos -= 2.625f / 2.75f;
            return distance * (bounciness * pos * pos + 63f / 64f) + start;
        }

        /// <summary>按现有反向弹跳公式计算插值，不对进度作钳制。</summary>
        public static float InBounce(float start, float end, float pos, float bounciness = 7.5625f)
        {
            var distance = end - pos;
            pos = 1 - pos;
            return distance - OutBounce(start, end, pos, bounciness) + start;
        }

        /// <summary>计算圆弧曲线的缓入缓出插值，进度应处于 0 到 1。</summary>
        public static float InOutCircle(float start, float end, float pos)
        {
            return pos < 0.5f
                ? InCircle(start, (start + end) / 2, pos * 2)
                : OutCircle((start + end) / 2, end, (pos - 0.5f) * 2);
        }

        /// <summary>计算圆弧曲线的缓出插值，进度应处于 0 到 1。</summary>
        public static float OutCircle(float start, float end, float pos)
        {
            var distance = end - start;
            pos -= 1;
            return distance * Mathf.Sqrt(1 - pos * pos) + start;
        }

        /// <summary>计算圆弧曲线的缓入插值，进度应处于 0 到 1。</summary>
        public static float InCircle(float start, float end, float pos)
        {
            var distance = end - start;
            return -distance * (Mathf.Sqrt(1 - pos * pos) - 1) + start;
        }

        /// <summary>计算三次曲线的缓入缓出插值。</summary>
        public static float InOutCubic(float start, float end, float pos)
        {
            return pos < 0.5f
                ? InCubic(start, (start + end) * 0.5f, pos * 2)
                : OutCubic((start + end) * 0.5f, end, (pos - 0.5f) * 2);
        }

        /// <summary>计算三次曲线的缓出插值。</summary>
        public static float OutCubic(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * (Mathf.Pow(pos - 1, 3) + 1) + start;
        }

        /// <summary>计算三次曲线的缓入插值。</summary>
        public static float InCubic(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * Mathf.Pow(pos, 3) + start;
        }

        /// <summary>计算弹性振荡的缓入缓出插值，允许结果超出端点范围。</summary>
        /// <param name="start">起始值。</param>
        /// <param name="end">结束值。</param>
        /// <param name="pos">归一化插值进度。</param>
        /// <param name="elasticity">与 duration 一起决定振荡周期的系数。</param>
        /// <param name="duration">曲线计算用的时间尺度，不负责动作计时。</param>
        public static float InOutElastic(float start, float end, float pos, float elasticity = 0.3f, float duration = 5.0f)
        {
            return pos < 0.5f
                ? InElastic(start, (start + end) * 0.5f, pos * 2, elasticity, duration)
                : OutElastic((start + end) * 0.5f, end, (pos - 0.5f) * 2, elasticity, duration);
        }

        /// <summary>计算在终点附近振荡收敛的弹性缓出插值。</summary>
        /// <inheritdoc cref="InOutElastic" />
        public static float OutElastic(float start, float end, float pos, float elasticity = 0.3f, float duration = 5.0f)
        {
            var distance = end - start;

            if (pos == 0 || distance == 0)
            {
                return start;
            }

            if (Math.Abs(pos - 1.0f) < 0.01f)
            {
                return end;
            }

            var p = duration * elasticity;
            var s = Mathf.Sign(distance) < 0 ? p * 0.25f : p / (2 * Mathf.PI) * Mathf.Asin(1);

            return distance * Mathf.Pow(2, -10 * pos) * Mathf.Sin((pos * duration - s) * (2 * Mathf.PI) / p) +
                   distance + start;
        }

        /// <summary>计算起点附近振荡后加速的弹性缓入插值。</summary>
        /// <inheritdoc cref="InOutElastic" />
        public static float InElastic(float start, float end, float pos, float elasticity = 0.3f, float duration = 5.0f)
        {
            var distance = end - start;

            if (pos == 0 || distance == 0)
            {
                return start;
            }

            if (Math.Abs(pos - 1) < 0.01f)
            {
                return end;
            }

            var p = duration * elasticity;
            var s = Mathf.Sign(distance) < -1 ? p * 0.25f : p / (2 * Mathf.PI) * Mathf.Asin(1);

            return -(distance * Mathf.Pow(2, 10 * --pos) * Mathf.Sin((pos * duration - s) * (Mathf.PI * 2) / p)) +
                   start;
        }

        /// <summary>计算指数曲线的缓入缓出插值。</summary>
        public static float InOutExpo(float start, float end, float pos)
        {
            return pos < 0.5f
                ? InExpo(start, (start + end) * 0.5f, pos * 2)
                : OutExpo((start + end) * 0.5f, end, (pos - 0.5f) * 2);
        }

        /// <summary>计算指数曲线的缓出插值。</summary>
        public static float OutExpo(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * (-Mathf.Pow(2, -10 * pos) + 1) + start;
        }

        /// <summary>计算指数曲线的缓入插值。</summary>
        public static float InExpo(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * Mathf.Pow(2, 10 * (pos - 1)) + start;
        }

        /// <summary>计算二次曲线的缓入缓出插值。</summary>
        public static float InOutQuad(float start, float end, float pos)
        {
            return pos < 0.5f
                ? InQuad(start, (start + end) * 0.5f, pos * 2)
                : OutQuad((start + end) * 0.5f, end, (pos - 0.5f) * 2);
        }

        /// <summary>计算二次曲线的缓出插值。</summary>
        public static float OutQuad(float start, float end, float pos)
        {
            var distance = end - start;
            return -distance * pos * (pos - 2) + start;
        }

        /// <summary>计算二次曲线的缓入插值。</summary>
        public static float InQuad(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * pos * pos + start;
        }

        /// <summary>计算四次曲线的缓入缓出插值。</summary>
        public static float InOutQuart(float start, float end, float pos)
        {
            return pos < 0.5f
                ? InQuart(start, (start + end) * 0.5f, pos * 2)
                : OutQuart((start + end) * 0.5f, end, (pos - 0.5f) * 2);
        }

        /// <summary>计算四次曲线的缓出插值。</summary>
        public static float OutQuart(float start, float end, float pos)
        {
            var distance = end - start;
            return -distance * ((pos - 1) * (pos - 1) * (pos - 1) * (pos - 1) - 1) + start;
        }

        /// <summary>计算四次曲线的缓入插值。</summary>
        public static float InQuart(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * (pos * pos * pos * pos) + start;
        }

        /// <summary>计算五次曲线的缓入缓出插值。</summary>
        public static float InOutQuint(float start, float end, float pos)
        {
            return pos < 0.5f
                ? InQuint(start, (start + end) * 0.5f, pos * 2)
                : OutQuint((start + end) * 0.5f, end, (pos - 0.5f) * 2);
        }

        /// <summary>计算五次曲线的缓出插值。</summary>
        public static float OutQuint(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * ((pos - 1) * (pos - 1) * (pos - 1) * (pos - 1) * (pos - 1) + 1) + start;
        }

        /// <summary>计算五次曲线的缓入插值。</summary>
        public static float InQuint(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * pos * pos * pos * pos * pos + start;
        }

        /// <summary>计算正弦曲线的缓入缓出插值。</summary>
        public static float InOutSine(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * 0.5f * (1 - Mathf.Cos(Mathf.PI * pos)) + start;
        }

        /// <summary>计算正弦曲线的缓出插值。</summary>
        public static float OutSine(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * Mathf.Sin(pos * 1.570796f) + start;
        }

        /// <summary>计算正弦曲线的缓入插值。</summary>
        public static float InSine(float start, float end, float pos)
        {
            var distance = end - start;
            return distance * (1 - Mathf.Cos(pos * 1.570796f)) + start;
        }
    }
}

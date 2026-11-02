using System;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Extensions;

namespace YLFramework.Tests.Extensions
{
    public class ParabolicMotionContractTests
    {
        [Test]
        public void LargeFiniteEffectiveGravityHasAFiniteReachableContinuousSolution()
        {
            // 原生 gravityScale 有范围限制；直接验证实际纯计算内核，不修改全局重力。
            var calculate = typeof(RigidbodyExtensions).GetMethod("CalculateVelocity",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Vector3 velocity = default;
            Assert.DoesNotThrow(() => velocity = (Vector3)calculate.Invoke(null,
                new object[] { Vector3.zero, 0f, new Vector3(1, -1, 0), new Vector3(0, -2e38f, 0) }));
            Assert.That(float.IsNaN(velocity.x) || float.IsInfinity(velocity.x), Is.False);
            Assert.That(velocity.x, Is.GreaterThan(0));
            // X 位移为 1，因此 t=1/vx；独立代入竖直运动方程验证目标高度。
            double time = 1.0 / velocity.x;
            Assert.That(0.5 * -2e38f * time * time, Is.EqualTo(-1).Within(1e-5));
            Assert.That(velocity.y, Is.Zero);
        }

        [Test]
        public void UnreachableTargetCannotOverwriteExistingNativeVelocity()
        {
            var go = new GameObject("YL.Tests.Unreachable.53");
            try
            {
                var body = go.AddComponent<Rigidbody>();
                body.linearVelocity = new Vector3(1, 2, 3);
                Assert.Throws<InvalidOperationException>(() => body.ParabolicMotion(0, Vector3.up));
                Assert.That(body.linearVelocity, Is.EqualTo(new Vector3(1, 2, 3)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ThreeDimensionalBallisticsUsesLatestReachableTimeAndRejectsAboveApex()
        {
            var go = new GameObject("YL.Tests.Ballistics.53");
            try
            {
                var body = go.AddComponent<Rigidbody>();
                float g = -Physics.gravity.y;
                Assert.That(g, Is.GreaterThan(0), "夹具需要当前向下重力，不修改全局物理设置");
                Assert.That(Physics.gravity.x, Is.Zero);
                Assert.That(Physics.gravity.z, Is.Zero);
                Vector3 velocity = body.ParabolicMotionVelocity(g, new Vector3(10, 0, 6));
                Assert.That(Vector3.Distance(velocity, new Vector3(5, g, 3)), Is.LessThan(1e-4));
                Vector3 apex = body.ParabolicMotionVelocity(g, new Vector3(10, g / 2, 0));
                Assert.That(apex.x, Is.EqualTo(10).Within(1e-4));
                body.linearVelocity = new Vector3(1, 2, 3);
                Assert.Throws<InvalidOperationException>(() => body.ParabolicMotion(g, new Vector3(10, g + 1, 0)));
                Assert.That(body.linearVelocity, Is.EqualTo(new Vector3(1, 2, 3)));
                Assert.Throws<InvalidOperationException>(() => body.ParabolicMotionVelocity(0, Vector3.right));
                var falling = body.ParabolicMotionVelocity(0, new Vector3(10, -2 * g, 0));
                Assert.That(falling.x, Is.EqualTo(5).Within(1e-4));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DisabledGravitySupportsStraightFlightButRejectsUnderdeterminedOrUnreachableTime()
        {
            var go = new GameObject("YL.Tests.NoGravity.53");
            try
            {
                var body = go.AddComponent<Rigidbody>();
                body.useGravity = false;
                Assert.That(body.ParabolicMotionVelocity(2, new Vector3(6, 4, 8)), Is.EqualTo(new Vector3(3, 2, 4)));
                Assert.Throws<InvalidOperationException>(() => body.ParabolicMotionVelocity(0, Vector3.right));
                Assert.Throws<InvalidOperationException>(() => body.ParabolicMotionVelocity(2, Vector3.down));
                Assert.Throws<ArgumentException>(() => body.ParabolicMotionVelocity(float.NaN, Vector3.one));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TwoDimensionalBallisticsRespectsZeroAndNegativeGravityScale()
        {
            var go = new GameObject("YL.Tests.Ballistics2D.53");
            try
            {
                var body = go.AddComponent<Rigidbody2D>();
                body.gravityScale = 0;
                Assert.That(body.ParabolicMotionVelocity(2, new Vector3(6, 4, 999)), Is.EqualTo(new Vector2(3, 2)));
                body.linearVelocity = new Vector2(1, 2);
                Assert.Throws<InvalidOperationException>(() => body.ParabolicMotion(0, Vector3.right));
                Assert.That(body.linearVelocity, Is.EqualTo(new Vector2(1, 2)));
                float g = -Physics2D.gravity.y;
                Assert.That(g, Is.GreaterThan(0));
                Assert.That(Physics2D.gravity.x, Is.Zero);
                body.gravityScale = -1;
                var rising = body.ParabolicMotionVelocity(0, new Vector3(10, 2 * g, 0));
                Assert.That(rising.x, Is.EqualTo(5).Within(1e-4));
                Assert.That(rising.y, Is.Zero);
                Assert.Throws<InvalidOperationException>(() => body.ParabolicMotionVelocity(0, Vector3.down));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}

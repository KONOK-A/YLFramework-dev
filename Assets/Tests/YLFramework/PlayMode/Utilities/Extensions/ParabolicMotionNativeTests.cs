using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Extensions;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions
{
    public class ParabolicMotionNativeTests
    {
        private Scene _scene;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
        }

        [UnityTest]
        public IEnumerator ComputedVelocityRunsInOwnedPhysicsSceneAndFailureLeavesMotionUnchanged()
        {
            _scene = SceneManager.CreateScene("YL.Tests.Ballistics3D.53", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var go = new GameObject("Ballistic body");
            SceneManager.MoveGameObjectToScene(go, _scene);
            var body = go.AddComponent<Rigidbody>();
            float g = -Physics.gravity.y;
            Assert.That(g, Is.GreaterThan(0));
            Assert.That(Physics.gravity.x, Is.Zero);
            Assert.That(Physics.gravity.z, Is.Zero);
            body.linearDamping = 0;
            body.ParabolicMotion(g, new Vector3(10, 0, 6));
            Assert.That(Vector3.Distance(body.linearVelocity, new Vector3(5, g, 3)), Is.LessThan(1e-4));
            Assert.Throws<InvalidOperationException>(() => body.ParabolicMotion(0, Vector3.up));
            Assert.That(Vector3.Distance(body.linearVelocity, new Vector3(5, g, 3)), Is.LessThan(1e-4));
            var physics = _scene.GetPhysicsScene();
            for (int i = 0; i < 100; i++)
            {
                physics.Simulate(0.02f);
            }

            Assert.That(body.position.x, Is.EqualTo(10).Within(0.002));
            Assert.That(body.position.z, Is.EqualTo(6).Within(0.002));
            // 原生离散积分相对连续抛物线有一个固定步长的偏差，不声称连续解可精确落点。
            Assert.That(body.position.y, Is.EqualTo(-g * 0.02f).Within(0.002));
            body.useGravity = false;
            body.position = Vector3.zero;
            body.ParabolicMotion(2, new Vector3(6, 4, 8));
            for (int i = 0; i < 100; i++)
            {
                physics.Simulate(0.02f);
            }

            Assert.That(Vector3.Distance(body.position, new Vector3(6, 4, 8)), Is.LessThan(0.002));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ZeroScaleTwoDimensionalFlightWritesFiniteVelocityAndRejectsIndeterminateTime()
        {
            _scene = SceneManager.CreateScene("YL.Tests.Ballistics2D.53", new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            var go = new GameObject("Ballistic body 2D");
            SceneManager.MoveGameObjectToScene(go, _scene);
            var body = go.AddComponent<Rigidbody2D>();
            body.gravityScale = 0;
            body.linearDamping = 0;
            body.ParabolicMotion(2, new Vector3(6, 4, 999));
            Assert.That(body.linearVelocity, Is.EqualTo(new Vector2(3, 2)));
            Assert.Throws<InvalidOperationException>(() => body.ParabolicMotion(0, Vector3.right));
            Assert.That(body.linearVelocity, Is.EqualTo(new Vector2(3, 2)));
            var physics = _scene.GetPhysicsScene2D();
            for (int i = 0; i < 100; i++)
            {
                Assert.That(physics.Simulate(0.02f), Is.True);
            }

            Assert.That(Vector2.Distance(body.position, new Vector2(6, 4)), Is.LessThan(0.002));
            yield return null;
        }
    }
}

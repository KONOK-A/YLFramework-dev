using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;

namespace YLFramework.Tests.Actions
{
    public class EaseUtilityPlayModeTests
    {
        [UnityTest]
        public IEnumerator CurvesRemainDeterministicAcrossFramesWithTimeScaleZero()
        {
            var originalTimeScale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;
                Assert.That(EaseUtility.InOutQuad(0f, 1f, 0.25f), Is.EqualTo(0.125f));
                yield return null;
                yield return null;
                Assert.That(EaseUtility.InOutQuad(0f, 1f, 0.25f), Is.EqualTo(0.125f));
                Assert.That(EaseUtility.OutQuint(0f, 1f, 0.25f), Is.EqualTo(0.7626953125f));
            }
            finally
            {
                Time.timeScale = originalTimeScale;
            }
        }

        [UnityTest]
        public IEnumerator CurveValuesCanDriveAUnityTransformWithoutAModuleHost()
        {
            var target = new GameObject("ActionKit.Ease.PlayModeTest");
            try
            {
                var positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
                var expected = new[] { 2f, 2.25f, 4f, 5.75f, 6f };
                for (var index = 0; index < positions.Length; index++)
                {
                    target.transform.position = new Vector3(
                        EaseUtility.InOutCubic(2f, 6f, positions[index]), 3f, -1f);
                    yield return null;
                    Assert.That(target.transform.position.x, Is.EqualTo(expected[index]).Within(0.00001f));
                    Assert.That(target.transform.position.y, Is.EqualTo(3f));
                    Assert.That(target.transform.position.z, Is.EqualTo(-1f));
                }
            }
            finally
            {
                Object.Destroy(target);
            }

            yield return null;
            Assert.That(target == null, Is.True);
        }
    }
}

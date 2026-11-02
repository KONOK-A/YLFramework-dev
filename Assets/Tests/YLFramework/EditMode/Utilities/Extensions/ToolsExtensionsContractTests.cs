using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Utilities;
using YLFramework.Extensions;

namespace YLFramework.Tests.Extensions
{
    public class ToolsExtensionsContractTests
    {
        [Test]
        public void ObjectLivenessHasOneUnityFakeNullEntryForGameObjectsAndComponents()
        {
            var method = typeof(ObjectExtension).GetMethod("IsDestroy", new[] { typeof(UnityEngine.Object) });
            Assert.That(method, Is.Not.Null);
            var target = new GameObject("YL.Tests.ObjectLiveness.54");
            var component = target.transform;
            try
            {
                Assert.That(method.Invoke(null, new object[] { null }), Is.EqualTo(true));
                Assert.That(method.Invoke(null, new object[] { target }), Is.EqualTo(false));
                Assert.That(method.Invoke(null, new object[] { component }), Is.EqualTo(false));
                UnityEngine.Object.DestroyImmediate(target);
                Assert.That(ReferenceEquals(target, null), Is.False);
                Assert.That(ReferenceEquals(component, null), Is.False);
                Assert.That(method.Invoke(null, new object[] { target }), Is.EqualTo(true));
                Assert.That(method.Invoke(null, new object[] { component }), Is.EqualTo(true));
            }
            finally
            {
                if (target)
                {
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
        }

        [Test]
        public void RecursiveLayerSettingKeepsActiveDescendantScopeDistinctFromSingleObjectLayer()
        {
            var set = typeof(GameObjectExtension).GetMethod("SetLayer", new[] { typeof(GameObject), typeof(int) });
            var byName = typeof(GameObjectExtension).GetMethod("SetLayer", new[] { typeof(GameObject), typeof(string) });
            Assert.That(set, Is.Not.Null);
            Assert.That(byName, Is.Not.Null);
            var root = new GameObject("YL.Tests.RecursiveLayer.54");
            var active = new GameObject("active");
            var inactive = new GameObject("inactive");
            active.transform.SetParent(root.transform);
            inactive.transform.SetParent(root.transform);
            inactive.SetActive(false);
            try
            {
                root.Layer(2);
                Assert.That(active.layer, Is.Zero, "Layer 不递归。");
                set.Invoke(null, new object[] { root, 31 });
                Assert.That(root.layer, Is.EqualTo(31));
                Assert.That(active.layer, Is.EqualTo(31));
                Assert.That(inactive.layer, Is.Zero, "SetLayer 保留 GetComponentsInChildren 默认 inactive 排除契约。");
                byName.Invoke(null, new object[] { root, "Default" });
                Assert.That(root.layer, Is.Zero);
                Assert.That(active.layer, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [TestCase(-1)]
        [TestCase(32)]
        [TestCase(63)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void InvalidLayerCannotAliasAValidBitOrMutateMask(int layer)
        {
            LayerMask mask = -1;
            Assert.That(mask.Contains(layer), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Set(layer, false));
            Assert.That(mask.value, Is.EqualTo(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.PhysicsSetting(layer));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Physics2DSetting(layer));
            Assert.That(mask.value, Is.EqualTo(-1));
        }

        [Test]
        public void UndefinedLayerNameCannotReadOrWriteBit31()
        {
            const string Missing = "YL.Tests.Missing.Layer.53";
            Assert.That(LayerMask.NameToLayer(Missing), Is.EqualTo(-1));
            LayerMask mask = int.MinValue;
            Assert.That(mask.Contains(Missing), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Set(Missing, false));
            Assert.That(mask.value, Is.EqualTo(int.MinValue));
        }

        [Test]
        public void InvalidLayerWritesAreRejectedBeforeChangingMaskOrCallingPhysics()
        {
            LayerMask mask = 17;
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Set(-1, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Set(32, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.PhysicsSetting(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => mask.Physics2DSetting(32));
            Assert.That(mask.value, Is.EqualTo(17));
        }

        [Test]
        public void LayerEndpointsAndCollisionReadbackPreserveValidSemantics()
        {
            LayerMask mask = 0;
            mask.Set(0, true);
            mask.Set(31, true);
            Assert.That(mask.value, Is.EqualTo(unchecked((int)0x80000001)));
            mask.Set(0, false);
            Assert.That(mask.value, Is.EqualTo(int.MinValue));
            mask.Set(31, false);
            Assert.That(mask.value, Is.Zero);
            foreach (int layer in new[] { 0, 31 })
            {
                int expected3D = 0, expected2D = 0;
                for (int i = 0; i < 32; i++)
                {
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    {
                        continue;
                    }

                    if (!Physics.GetIgnoreLayerCollision(layer, i))
                    {
                        expected3D |= 1 << i;
                    }

                    if (!Physics2D.GetIgnoreLayerCollision(layer, i))
                    {
                        expected2D |= 1 << i;
                    }
                }
                mask.PhysicsSetting(layer);
                Assert.That(mask.value, Is.EqualTo(expected3D));
                mask.Physics2DSetting(layer);
                Assert.That(mask.value, Is.EqualTo(expected2D));
            }
        }

        [TestCase(0L, "0b")]
        [TestCase(1023L, "1023b")]
        [TestCase(1024L, "1.00kb")]
        [TestCase(1048576L, "1.00mb")]
        [TestCase(1073741824L, "1.00gb")]
        [TestCase(1099511627776L, "1.00tb")]
        [TestCase(1125899906842624L, "1,024.00tb")]
        [TestCase(long.MaxValue, "8,388,608.00tb")]
        public void ByteFormattingHasANonemptyUnitAcrossTheLongRange(long bytes, string expected)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                Assert.That(StringTools.FormatByte(bytes), Is.EqualTo(expected));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void ByteFormattingKeepsHumanCurrentCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Assert.That(StringTools.FormatByte(1536), Is.EqualTo("1,50kb"));
                Assert.That(StringTools.FormatByte(1649267441664L), Is.EqualTo("1,50tb"));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void SkewLineDistanceIsNonnegativeAndSymmetric()
        {
            var a = new Ray(Vector3.zero, Vector3.right);
            var b = new Ray(new Vector3(0, 0, 3), Vector3.up);
            Assert.That(a.MinimumDistance(b), Is.EqualTo(3).Within(1e-5));
            Assert.That(b.MinimumDistance(a), Is.EqualTo(3).Within(1e-5));
            a.GetClosestPoints(b, out Vector3 pa, out Vector3 pb);
            Assert.That(Vector3.Distance(pa, pb), Is.EqualTo(3).Within(1e-5));
        }

        [Test]
        public void NearlyParallelCoplanarLinesDoNotMeasureClosestPointRoundingAsDistance()
        {
            var a = new Ray(Vector3.zero, new Vector3(1, 0.1f, 0));
            var b = new Ray(new Vector3(1, 2, 0), new Vector3(1, 0.1000001f, 0));
            Assert.That(a.MinimumDistance(b), Is.EqualTo(0).Within(1e-5));
            Assert.That(b.MinimumDistance(a), Is.EqualTo(0).Within(1e-5));
        }

        [Test]
        public void LargeFiniteGeometryDistancesDoNotOverflowTheirSquaredLength()
        {
            var a = new Ray(Vector3.zero, Vector3.right);
            var point = new Vector3(0, 1e20f, 0);
            Assert.That(float.IsNaN(a.Distance(point)) || float.IsInfinity(a.Distance(point)), Is.False);
            Assert.That(float.IsNaN(a.DistanceToRay(point)) || float.IsInfinity(a.DistanceToRay(point)), Is.False);
            Assert.That(a.Distance(point), Is.EqualTo(1e20f).Within(1e14));
            Assert.That(a.DistanceToRay(point), Is.EqualTo(1e20f).Within(1e14));
            Assert.That(a.MinimumDistance(new Ray(point, Vector3.left)), Is.EqualTo(1e20f).Within(1e14));
            Assert.That(a.MinimumDistance(new Ray(new Vector3(0, 0, 1e20f), Vector3.up)),
                Is.EqualTo(1e20f).Within(1e14));
        }

        [TestCase(1)]
        [TestCase(-1)]
        public void ParallelLinesHaveFiniteClosestPointsForBothOrientations(int sign)
        {
            var a = new Ray(Vector3.zero, Vector3.right);
            var b = new Ray(new Vector3(2, 3, 0), sign * Vector3.right);
            Assert.That(a.MinimumDistance(b), Is.EqualTo(3).Within(1e-5));
            a.GetClosestPoints(b, out Vector3 pa, out Vector3 pb);
            Assert.That(Vector3.Distance(pa, pb), Is.EqualTo(3).Within(1e-5));
            Assert.That(Vector3.Dot(pa - pb, a.direction), Is.EqualTo(0).Within(1e-5));
        }

        [Test]
        public void ClosestLinePointsKeepSignedParametersBehindOrigin()
        {
            var a = new Ray(Vector3.zero, Vector3.right);
            var b = new Ray(new Vector3(-2, 3, 0), Vector3.up);
            a.GetClosestPoints(b, out float ta, out float tb);
            Assert.That(ta, Is.EqualTo(-2).Within(1e-5));
            Assert.That(tb, Is.EqualTo(-3).Within(1e-5));
            Assert.That(a.Distance(new Vector3(-3, 4, 0)), Is.EqualTo(4).Within(1e-5));
            Assert.That(a.DistanceToRay(new Vector3(-3, 4, 0)), Is.EqualTo(5).Within(1e-5));
            Assert.That(a.DistanceToRay(new Vector3(3, 4, 0)), Is.EqualTo(4).Within(1e-5));
        }

        [Test]
        public void ZeroDirectionBehavesAsAPointNotAnInvalidLine()
        {
            var point = new Ray(new Vector3(2, 3, 0), Vector3.zero);
            var line = new Ray(Vector3.zero, Vector3.right);
            point.GetClosestPoints(line, out Vector3 pa, out Vector3 pb);
            Assert.That(pa, Is.EqualTo(new Vector3(2, 3, 0)));
            Assert.That(pb, Is.EqualTo(new Vector3(2, 0, 0)));
            Assert.That(point.MinimumDistance(line), Is.EqualTo(3).Within(1e-5));
            Assert.That(point.Distance(Vector3.zero), Is.EqualTo(Mathf.Sqrt(13)).Within(1e-5));
        }

        [Test]
        public void QuaternionInverseLerpIsInvariantUnderEveryEquivalentSign()
        {
            var from = Quaternion.Euler(10, 20, 30);
            var to = from * Quaternion.AngleAxis(120, Vector3.up);
            var current = Quaternion.Slerp(from, to, 0.25f);
            foreach (int a in new[] { -1, 1 })
            {
                foreach (int b in new[] { -1, 1 })
                {
                    foreach (int c in new[] { -1, 1 })
                    {
                        Assert.That(QuaternionExtensions.InverseLerp(Scale(from, a), Scale(to, b), Scale(current, c)),
                    Is.EqualTo(0.25f).Within(1e-4));
                    }
                }
            }

            Assert.That(QuaternionExtensions.InverseLerp(from, Scale(from, -1), to), Is.Zero);
            Assert.That(QuaternionExtensions.InverseLerp(Scale(from, 2), Scale(to, 3), Scale(current, 4)),
                Is.EqualTo(0.25f).Within(1e-4));
        }

        [Test]
        public void QuaternionInvalidRotationIsRejectedRatherThanSilentlyCalculating()
        {
            Assert.Throws<ArgumentException>(() => QuaternionExtensions.InverseLerp(default, Quaternion.identity, Quaternion.identity));
            Assert.Throws<ArgumentException>(() => QuaternionExtensions.InverseLerp(Quaternion.identity, Quaternion.identity,
                new Quaternion(float.NaN, 0, 0, 1)));
        }

        [Test]
        public void QuaternionSmallNonzeroArcRetainsEndpointAndMidpointRatios()
        {
            var from = Quaternion.identity;
            var to = Quaternion.AngleAxis(0.1f, Vector3.up);
            var middle = Quaternion.AngleAxis(0.05f, Vector3.up);
            Assert.That(QuaternionExtensions.InverseLerp(from, to, to), Is.EqualTo(1).Within(1e-5));
            Assert.That(QuaternionExtensions.InverseLerp(from, to, middle), Is.EqualTo(0.5f).Within(1e-5));
            Assert.That(QuaternionExtensions.InverseLerp(from, Scale(to, -1), Scale(middle, -1)),
                Is.EqualTo(0.5f).Within(1e-5));
        }

        [Test]
        public void QuaternionUnrepresentableAngleRatioDoesNotReturnInfinity()
        {
            Assert.Throws<InvalidOperationException>(() => QuaternionExtensions.InverseLerp(Quaternion.identity,
                new Quaternion(float.Epsilon, 0, 0, 1), new Quaternion(1, 0, 0, 0)));
        }

        [Test]
        public void TypeDisplaySupportsGenericParametersAndOpenAndPartialTypes()
        {
            var parameter = typeof(Action<>).GetGenericArguments()[0];
            Assert.That(parameter.FullName, Is.Null);
            Assert.That(parameter.GetDelegateDisplayName(), Is.EqualTo("T()"));
            Assert.That(typeof(Action<>).GetDelegateDisplayName(), Is.EqualTo("System.Action(T)"));
            Assert.That(typeof(Action<int, string>).GetDelegateDisplayName(false), Is.EqualTo("Action(Int32,String)"));
            var partial = typeof(Dictionary<,>).MakeGenericType(typeof(string), parameter);
            Assert.That(partial.FullName, Is.Null);
            Assert.That(partial.GetDelegateDisplayName(), Is.EqualTo("System.Collections.Generic.Dictionary(String,T)"));
            Assert.Throws<ArgumentNullException>(() => TypeExtensions.GetDelegateDisplayName(null));
        }

        [Test]
        public void ComponentMaskWrapperRespectsDestroyedUnityObjects()
        {
            var target = new GameObject("YL.Tests.Layer.53");
            var component = target.transform;
            try
            {
                UnityEngine.Object.DestroyImmediate(target);
                Assert.That(ReferenceEquals(component, null), Is.False);
                Assert.That(component == null, Is.True);
                Assert.That(component.IsInLayerMask(-1), Is.False);
                Assert.That(((Component)null).IsInLayerMask(-1), Is.False);
            }
            finally
            {
                if (target)
                {
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
        }

        private static Quaternion Scale(Quaternion q, float scale) =>
            new Quaternion(q.x * scale, q.y * scale, q.z * scale, q.w * scale);
    }
}

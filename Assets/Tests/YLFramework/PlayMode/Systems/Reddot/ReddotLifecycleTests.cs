using NUnit.Framework;
using UnityEngine;
using YLFramework.RedDots;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Reddot")]
    public class ReddotLifecycleTests
    {
        [Test]
        public void DisplayExposesRefreshButNoBusinessMutation()
        {
            var methods = typeof(Reddot).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly);
            Assert.That(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(methods, method => !method.IsSpecialName), method => method.Name),
                Is.EquivalentTo(new[] { nameof(Reddot.Refresh) }));
            foreach (var field in typeof(Reddot).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly))
                Assert.That(field.GetCustomAttributes(typeof(UnityEngine.Serialization.FormerlySerializedAsAttribute), false), Is.Empty);
        }

        [Test]
        public void NumericPathSubscriptionsRebindWithoutUnrelatedOrUnchangedRedraws()
        {
            var owner = new GameObject("Domain.Reddot.NumericOwner");
            var indicator = new GameObject("Domain.Reddot.NumericIndicator");
            try
            {
                using var mail = ReddotManager.RegisterSource("Mail/Gift", "mail", 2);
                using var other = ReddotManager.RegisterSource("Other", "other");
                var reddot = owner.AddComponent<Reddot>();
                reddot.Path = "Mail";
                reddot.Indicator = indicator;
                Assert.That(indicator.activeSelf, Is.True);
                indicator.SetActive(false);
                other.SetValue(1);
                mail.SetValue(2);
                Assert.That(indicator.activeSelf, Is.False, "Unrelated and unchanged values must not redraw this path.");
                mail.SetValue(3);
                Assert.That(indicator.activeSelf, Is.True);
                reddot.Path = "Other";
                other.SetValue(0);
                Assert.That(indicator.activeSelf, Is.False);
                mail.SetValue(4);
                Assert.That(indicator.activeSelf, Is.False, "The old path subscription must have been disposed.");
                other.SetValue(5);
                Assert.That(indicator.activeSelf, Is.True);
                Object.DestroyImmediate(owner);
                indicator.SetActive(false);
                other.SetValue(6);
                Assert.That(indicator.activeSelf, Is.False, "Destroying the host must unsubscribe, not destroy external indicators.");
                Assert.That(other.Value, Is.EqualTo(6));
            }
            finally
            {
                if (owner)
                {
                    Object.DestroyImmediate(owner);
                }
                Object.DestroyImmediate(indicator);
                ReddotManager.Reset();
            }
        }

        [Test]
        public void SettersRefreshWhileDisabledAndSelfOrAncestorIndicatorsStayProtected()
        {
            var ancestor = new GameObject("Domain.Reddot.Ancestor");
            var owner = new GameObject("Domain.Reddot.Child");
            var indicator = new GameObject("Domain.Reddot.ExternalIndicator");
            try
            {
                owner.transform.SetParent(ancestor.transform);
                var reddot = owner.AddComponent<Reddot>();
                reddot.Path = "Mail";
                reddot.Indicator = ancestor;
                Assert.That(ancestor.activeSelf, Is.True);
                reddot.Indicator = owner;
                Assert.That(owner.activeSelf, Is.True);
                reddot.Indicator = indicator;
                Assert.That(indicator.activeSelf, Is.False);
                using var mail = ReddotManager.RegisterSource("Mail", "id", 1);
                Assert.That(indicator.activeSelf, Is.True);
                reddot.enabled = false;
                mail.SetValue(0);
                Assert.That(indicator.activeSelf, Is.True, "OnDisable must unsubscribe without resetting the previous display.");
                reddot.Path = "Other";
                Assert.That(indicator.activeSelf, Is.False, "The Path setter must refresh even while disabled.");
                indicator.SetActive(true);
                reddot.Indicator = indicator;
                Assert.That(indicator.activeSelf, Is.False);
                mail.SetValue(2);
                reddot.Path = "Mail";
                reddot.enabled = true;
                Assert.That(indicator.activeSelf, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(ancestor);
                Object.DestroyImmediate(indicator);
                ReddotManager.Reset();
            }
        }

        [TestCase("path", "_path", typeof(string))]
        [TestCase("indicator", "_indicator", typeof(GameObject))]
        public void CurrentSerializedFieldsRemainConsumableWithoutOldReaders(string name, string oldName, System.Type type)
        {
            var field = typeof(Reddot).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            Assert.That(field.FieldType, Is.EqualTo(type));
            Assert.That(field.IsDefined(typeof(SerializeField), false), Is.True);
            var former = (UnityEngine.Serialization.FormerlySerializedAsAttribute)System.Attribute.GetCustomAttribute(
                field, typeof(UnityEngine.Serialization.FormerlySerializedAsAttribute));
            Assert.That(former, Is.Null);
            Assert.That(typeof(Reddot).GetField(oldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), Is.Null);
        }

        [Test]
        public void RealUguiIndicatorDoesNotSwitchBusinessButton()
        {
            var canvas = new GameObject("Domain.Reddot.Canvas", typeof(Canvas));
            var image = new GameObject("Domain.Reddot.Image", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            var button = new GameObject("Domain.Reddot.Button", typeof(RectTransform), typeof(UnityEngine.UI.Button));
            try
            {
                image.transform.SetParent(canvas.transform);
                button.transform.SetParent(canvas.transform);
                var reddot = canvas.AddComponent<Reddot>();
                reddot.Path = "Domain/UI";
                reddot.Indicator = image;
                using (ReddotManager.RegisterPath("Domain/UI/Badge"))
                {
                    Assert.That(image.activeInHierarchy, Is.True);
                }

                Assert.That(image.activeSelf, Is.False);
                Assert.That(button.GetComponent<UnityEngine.UI.Button>().isActiveAndEnabled, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(canvas);
                ReddotManager.Reset();
            }
        }
        [Test]
        public void OnlyExplicitIndicatorChangesAndDisabledHostUnsubscribes()
        {
            var owner = new GameObject("Domain.Reddot.owner");
            var indicator = new GameObject("Domain.Reddot.indicator");
            var business = new GameObject("Domain.Reddot.business");
            try
            {
                indicator.transform.SetParent(owner.transform);
                business.transform.SetParent(owner.transform);
                var reddot = owner.AddComponent<Reddot>();
                reddot.Path = "Mail";
                reddot.Indicator = indicator;
                Assert.That(indicator.activeSelf, Is.False);
                using (ReddotManager.RegisterPath("Mail/Gift"))
                {
                    Assert.That(indicator.activeSelf, Is.True);
                    Assert.That(business.activeSelf, Is.True);
                    reddot.enabled = false;
                    ReddotManager.Reset();
                    Assert.That(indicator.activeSelf, Is.True, "停用后不再接收路径回调。");
                    reddot.enabled = true;
                    Assert.That(indicator.activeSelf, Is.False);
                }
                reddot.Indicator = owner;
                Assert.That(owner.activeSelf, Is.True);
                Assert.That(business.activeSelf, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(owner);
                ReddotManager.Reset();
            }
        }
    }
}

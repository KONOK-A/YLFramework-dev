using NUnit.Framework;
using UnityEngine;
using YLFramework.RedDots;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Reddot")]
    public class ReddotLifecycleTests
    {
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

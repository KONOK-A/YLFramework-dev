using System;
using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.RedDots;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Reddot")]
    [Category("YL.Module.Domain")]
    public class ReddotTests
    {
        [TearDown]
        public void Clean() => ReddotManager.Reset();

        [TestCase("Mail", true)]
        [TestCase("Mail/Gift", true)]
        [TestCase("Mai", false)]
        [TestCase("Mailbox", false)]
        [TestCase(null, false)]
        public void PathsMatchWholeSegments(string path, bool expected)
        {
            ReddotManager.AddPath(" /Mail//Gift/ ");
            Assert.That(ReddotManager.ShouldBeDisplayed(path), Is.EqualTo(expected));
        }

        [Test]
        public void ResetNotifiesAfterClearingAndDuplicateAddDoesNotNotify()
        {
            var displays = new List<bool>();
            Action changed = () => displays.Add(ReddotManager.ShouldBeDisplayed("Mail"));
            var token = ReddotManager.RegisterChanged(changed);
            try
            {
                ReddotManager.AddPath("Mail");
                ReddotManager.AddPath("/Mail/");
                ReddotManager.Reset();
                Assert.That(displays, Is.EqualTo(new[] { true, false }));
                token.Unregister();
                ReddotManager.AddPath("Mail");
                Assert.That(displays.Count, Is.EqualTo(2));
            }
            finally
            {
                token.Unregister();
            }
        }

        [Test]
        public void SamePathSourcesAreReleasedIndependently()
        {
            using var first = ReddotManager.RegisterPath("Mail/Gift");
            using var second = ReddotManager.RegisterPath("Mail/Gift/");
            first.Dispose();
            first.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            second.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
        }

        [Test]
        public void OldRegistrationCannotRemoveNewPathAfterReset()
        {
            using var old = ReddotManager.RegisterPath("Mail");
            ReddotManager.Reset();
            using var current = ReddotManager.RegisterPath("Mail");
            old.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            current.Dispose();
        }

        [Test]
        public void CollectionRemoveDoesNotStealIndependentSource()
        {
            using var source = ReddotManager.RegisterPath("Mail");
            ReddotManager.AddPath("Mail");
            ReddotManager.RemovePath("Mail");
            ReddotManager.RemovePath(null);
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.True);
            source.Dispose();
            Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
        }

        [Test]
        public void FailedRegistrationPublishesRollbackAndPreservesBothObserverErrors()
        {
            var visible = false;
            var display = ReddotManager.RegisterChanged(() => visible = ReddotManager.ShouldBeDisplayed("Mail"));
            var throwing = ReddotManager.RegisterChanged(() => throw new InvalidOperationException("observer-marker"));
            try
            {
                var error = Assert.Throws<AggregateException>(() => ReddotManager.RegisterPath("Mail"));
                Assert.That(ReddotManager.ShouldBeDisplayed("Mail"), Is.False);
                Assert.That(visible, Is.False, "回滚后的状态也必须送给已经刷新过的观察者。");
                Assert.That(error.InnerExceptions, Has.Count.EqualTo(2));
                Assert.That(error.InnerExceptions, Has.All.Message.EqualTo("observer-marker"));
            }
            finally
            {
                display.Unregister();
                throwing.Unregister();
            }
        }
    }
}

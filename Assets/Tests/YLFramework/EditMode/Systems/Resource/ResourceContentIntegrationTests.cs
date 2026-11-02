using System;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceContentIntegrationTests
    {
        [Test]
        public void ConfigurationExposesExplicitContentOptionsWithoutInitializing()
        {
            var property = typeof(ResourceConfiguration).GetProperty("Decryption");
            Assert.That(property, Is.Not.Null, "The formal configuration must carry application-owned content options.");
            Assert.That(property.GetValue(new ResourceConfiguration()), Is.Null);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [TestCase("ContentKeyUnavailable")]
        [TestCase("ContentAuthenticationFailed")]
        [TestCase("ContentFormatUnsupported")]
        [TestCase("ContentBufferUnavailable")]
        public void ContentFailuresHaveDistinctDomainErrors(string name)
            => Assert.That(Enum.IsDefined(typeof(ResourceError), name), Is.True);

        [Test]
        public void BufferBudgetRejectsAnEntireClosureWithoutPartialReservations()
        {
            var type = typeof(ResourceSystem).Assembly.GetType("YLFramework.Resource.ResourceContentBudget");
            Assert.That(type, Is.Not.Null, "A single subject-wide budget must cover live and reserved encrypted inputs.");
            var budget = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { 196576L * 2 }, null);
            var reserve = type.GetMethod("Reserve", BindingFlags.Instance | BindingFlags.NonPublic);
            var first = (Array)reserve.Invoke(budget, new object[] { 1 });
            var error = Assert.Throws<TargetInvocationException>(() => reserve.Invoke(budget, new object[] { 2 }));
            Assert.That(error.InnerException, Is.TypeOf<ResourceException>());
            Assert.That(((ResourceException)error.InnerException).Kind.ToString(), Is.EqualTo("ContentBufferUnavailable"));
            Assert.That(type.GetProperty("ReservedBytes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(budget), Is.EqualTo(196576L));
            ((IDisposable)first.GetValue(0)).Dispose();
            Assert.That(type.GetProperty("ReservedBytes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(budget), Is.EqualTo(0L));
            var retry = (Array)reserve.Invoke(budget, new object[] { 2 });
            foreach (IDisposable ticket in retry)
            {
                ticket.Dispose();
            }

            Assert.That(type.GetProperty("ReservedBytes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(budget), Is.EqualTo(0L));
        }

        [Test]
        public void CanceledPrivateGenerationKeepsOneReservationForTheExplicitRetry()
        {
            var budget = new ResourceContentBudget(ResourceDecryptStream.DataBufferBytes);
            using var ticket = budget.Reserve(1)[0];
            ticket.Allocate();
            ticket.PrepareRetry();
            Assert.That(budget.AllocatedBytes, Is.Zero);
            Assert.That(budget.ReservedBytes, Is.EqualTo(ResourceDecryptStream.DataBufferBytes));
            Assert.That(budget.LiveInputs, Is.Zero);
            ticket.PrepareRetry();
            ticket.Allocate();
            Assert.That(budget.LiveInputs, Is.EqualTo(1));
            Assert.That(budget.PeakBytes, Is.EqualTo(ResourceDecryptStream.DataBufferBytes));
            ticket.Dispose();
            Assert.That(budget.AllocatedBytes + budget.ReservedBytes, Is.Zero);
            Assert.Throws<ObjectDisposedException>(ticket.PrepareRetry);
        }
    }
}

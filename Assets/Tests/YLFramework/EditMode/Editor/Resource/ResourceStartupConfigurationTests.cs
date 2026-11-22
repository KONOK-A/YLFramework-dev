using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupConfigurationTests
    {
        [Test]
        public void StartupOutsidePlayFailsWithoutCreatingOrBindingAService()
        {
            Assert.That(Application.isPlaying, Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(YLFramework.Core.CoreResourceAccess.Provider, Is.Null);
            using var startup = new ResourceStartup(new ResourceConfiguration());
            using var start = startup.StartAsync();
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(start.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(startup.Resources, Is.Null);
            Assert.That(startup.OwnsResourceSystem, Is.False);
            Assert.That(ResourceSystem.Current, Is.Null, "A rejected start cannot silently initialize a fallback authority.");
            Assert.That(YLFramework.Core.CoreResourceAccess.Provider, Is.Null);
        }

        [Test]
        public void ConstructingAnApplicationStartupDoesNotInitializeOrBindAnything()
        {
            var startup = new ResourceStartup(new ResourceConfiguration());
            Assert.That(startup.Resources, Is.Null);
            Assert.That(startup.OwnsResourceSystem, Is.False);
            Assert.That(startup.CoreProvider, Is.Null);
            Assert.That(startup.AudioProviders, Is.Empty);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(YLFramework.Core.CoreResourceAccess.Provider, Is.Null);
        }
    }
}

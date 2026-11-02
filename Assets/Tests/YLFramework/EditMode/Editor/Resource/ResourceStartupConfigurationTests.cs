using System;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupConfigurationTests
    {
        [Test]
        public void RuntimeConfigurationExplicitlyRejectsSimulationInsteadOfFallingBack()
        {
            var failure = Assert.Throws<ResourceException>(() => ResourceConfiguration.Create(
                ResourceLoadMode.EditorSimulation, Array.Empty<ResourceProjectMount>()));
            Assert.That(failure.Kind, Is.EqualTo(ResourceError.Unsupported));
            Assert.That(ResourceSystem.Current, Is.Null, "Configuration selection must not initialize a resource authority.");
        }

        [Test]
        public void ConstructingAnApplicationStartupDoesNotInitializeOrBindAnything()
        {
            var startup = new ResourceStartup(new ResourceConfiguration());
            Assert.That(startup.Resources, Is.Null);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(YLFramework.Core.CoreResourceAccess.Provider, Is.Null);
        }
    }
}

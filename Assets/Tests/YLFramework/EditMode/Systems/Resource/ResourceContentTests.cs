using System;
using System.Linq;
using NUnit.Framework;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceContentTests
    {
        [Test]
        public void ContentKernelProvidesTheNarrowProductContract()
        {
            foreach (var name in new[] { "IResourceContentKeyProvider", "ResourceDecryptionOptions",
                         "ResourceBundleContext", "ResourceBundleEnvelope", "ResourceDecryptStream", "ResourceBundleInput" })
            {
                Assert.That(AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                    assembly.GetType("YLFramework.Resource." + name) != null), Is.True,
                    "Missing formal content contract: " + name);
            }
        }
    }
}

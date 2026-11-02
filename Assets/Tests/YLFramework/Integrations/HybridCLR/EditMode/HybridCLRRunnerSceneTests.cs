using System.Collections.Generic;
using NUnit.Framework;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRRunnerSceneTests
    {
        [TestCase(false, "EditMode", true, true, false)]
        [TestCase(true, "PlayMode", true, true, false)]
        [TestCase(true, "EditMode", false, true, false)]
        [TestCase(true, "EditMode", true, false, false)]
        [TestCase(true, "EditMode", true, true, true)]
        public void RegistrationRequiresAnExplicitOwnedBatchEditMode(bool batch, string mode, bool root, bool owner, bool expected)
        {
            var arguments = new List<string> { "-runTests", "-testPlatform", mode, "-hclr-run", "guard" };
            if (root)
            {
                arguments.AddRange(new[] { "-hclr-root", "owned-evidence" });
            }
            if (owner)
            {
                arguments.AddRange(new[] { "-hclr-owner-sha256", new string('a', 64) });
            }
            Assert.That(HybridCLRTestFixtures.ShouldRegisterRunnerSceneSetup(batch, arguments.ToArray()), Is.EqualTo(expected));
        }
    }
}

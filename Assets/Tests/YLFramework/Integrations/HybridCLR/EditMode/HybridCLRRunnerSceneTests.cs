using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRRunnerSceneTests
    {
        [Test]
        public void CurrentFixtureDoesNotInstallAnotherRunnerOrHistoricalExitException()
        {
            var methods = typeof(HybridCLRTestFixtures).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(methods.Where(method => method.IsDefined(typeof(InitializeOnLoadMethodAttribute), false)), Is.Empty);
            Assert.That(methods.Where(method => method.IsPublic).Select(method => method.Name), Is.EqualTo(new[] { "Prepare" }));
            Assert.That(typeof(HybridCLRTestFixtures).GetNestedTypes(BindingFlags.NonPublic).Any(type =>
                typeof(UnityEditor.TestTools.TestRunner.Api.ICallbacks).IsAssignableFrom(type)), Is.False);
        }
    }
}

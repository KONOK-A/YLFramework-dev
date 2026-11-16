using System;
using NUnit.Framework;

namespace YLFramework.Tests.Localization
{
    public class UnityLocalizationIntegrationTests
    {
        [Test]
        public void LocalizationPackageMakesOfficialReferencesAvailableToConsumers()
        {
            var referenceType = Type.GetType("UnityEngine.Localization.LocalizedString, Unity.Localization");
            Assert.That(referenceType, Is.Not.Null,
                "The optional Localization package must install the official Unity Localization runtime.");
        }

        [TestCase("LocalizationManager")]
        [TestCase("MultiLanguageConfigs")]
        [TestCase("LanguageConfig")]
        [TestCase("LanguageInfo")]
        [TestCase("LocalizeString")]
        [TestCase("LocalizeImage")]
        public void OfficialKernelDoesNotShipASecondLegacyEngine(string name)
        {
            Assert.That(Type.GetType("YLFramework.Localization." + name + ", YLFramework.Localization"), Is.Null,
                "The migration must remove the old kernel rather than leave two supported systems.");
        }
    }
}

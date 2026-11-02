using System;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Localization;

namespace YLFramework.Tests.Extensions.Data
{
    public class LocalizationUnityTests
    {
        private MultiLanguageConfigs _root;
        private LanguageConfig _english;
        private LanguageConfig _chinese;

        [SetUp]
        public void SetUp()
        {
            Assert.That(LocalizationManager.Configs, Is.Empty, "Do not close another localization owner.");
            LocalizationManager.Shutdown();
            _root = ScriptableObject.CreateInstance<MultiLanguageConfigs>();
            _root.name = "Localization-test-root";
            _english = ScriptableObject.CreateInstance<LanguageConfig>();
            _english.language = SystemLanguage.English;
            _english.languagesInfo.Add(new LanguageInfo { id = 1, str = "English {0}" });
            _chinese = ScriptableObject.CreateInstance<LanguageConfig>();
            _chinese.language = SystemLanguage.Chinese;
            _chinese.languagesInfo.Add(new LanguageInfo { id = 1, str = "Chinese" });
            _root.Languages.Add(_chinese);
            _root.Languages.Add(_english);
        }

        [TearDown]
        public void TearDown()
        {
            LocalizationManager.Shutdown();
            UnityEngine.Object.DestroyImmediate(_root);
            UnityEngine.Object.DestroyImmediate(_english);
            UnityEngine.Object.DestroyImmediate(_chinese);
        }

        [Test]
        public void RootRegistrationRefreshesAndTwoOwnersReleaseIndependently()
        {
            var refreshes = 0;
            var token = LocalizationManager.Changed.Register(() => refreshes++);
            using var first = LocalizationManager.AddConfig(_root);
            using var second = LocalizationManager.AddConfig(_root);
            Assert.That(refreshes, Is.EqualTo(2));
            Assert.That(LocalizationManager.Configs.Count, Is.EqualTo(1));
            first.Dispose();
            Assert.That(LocalizationManager.GetLanguageInfo(1, SystemLanguage.Japanese).str, Is.EqualTo("English {0}"));
            second.Dispose();
            Assert.That(LocalizationManager.GetLanguageInfo(1, SystemLanguage.Japanese), Is.Null);
            token.Unregister();
        }

        [Test]
        public void DuplicateValidationChecksEveryLanguageAndRollsBackFailedRegistration()
        {
            var other = ScriptableObject.CreateInstance<MultiLanguageConfigs>();
            var duplicate = ScriptableObject.CreateInstance<LanguageConfig>();
            duplicate.language = SystemLanguage.English;
            duplicate.languagesInfo.Add(new LanguageInfo { id = 1, str = "duplicate" });
            other.Languages.Add(duplicate);
            try
            {
                using var handle = LocalizationManager.AddConfig(_root);
                Assert.Throws<InvalidOperationException>(() => LocalizationManager.AddConfig(other));
                Assert.That(LocalizationManager.Configs.Count, Is.EqualTo(1));
                Assert.That(LocalizationManager.GetLanguageInfo(1, SystemLanguage.English).str, Is.EqualTo("English {0}"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(other);
                UnityEngine.Object.DestroyImmediate(duplicate);
            }
        }

        [Test]
        public void EmptyImageEntryFallsBackRatherThanDereferencingNull()
        {
            _chinese.languagesInfo[0].type = MultiLanguageType.Image;
            _chinese.languagesInfo[0].image = null;
            using var handle = LocalizationManager.AddConfig(_root);
            Assert.That(LocalizationManager.GetLanguageInfo(1, SystemLanguage.Chinese).str, Is.EqualTo("English {0}"));
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.Util;
using UnityEngine.Scripting;
using UnityEngine.TestTools;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    // Registered by the fixture through the public Addressables InitializationObjects extension.
    // Only this initializer is held; runtime-data, catalog, Locale and table loading stay official.
    [Preserve]
    public sealed class ResourceLocalizationInitializationGate : IInitializableObject
    {
        private static bool _armed;
        private static GateOperation _pending;
        internal static int Initializations
        {
            get; private set;
        }
        internal static int SynchronousWaits
        {
            get; private set;
        }
        internal static bool IsWaiting => _pending != null;

        internal static void Arm()
        {
            if (_armed || _pending != null || Initializations != 0)
                throw new InvalidOperationException("The native initialization overlap case requires a fresh isolated test process.");
            _armed = true;
        }

        internal static void Open()
        {
            _armed = false;
            var pending = _pending;
            _pending = null;
            pending?.Finish();
        }

        public bool Initialize(string id, string data) => true;

        public AsyncOperationHandle<bool> InitializeAsync(ResourceManager resourceManager, string id, string data)
        {
            var operation = new GateOperation();
            var handle = resourceManager.StartOperation(operation, default);
            operation.HandleObserver = handle;
            return handle;
        }

        private sealed class GateOperation : AsyncOperationBase<bool>
        {
            internal AsyncOperationHandle<bool> HandleObserver;

            protected override void Execute()
            {
                Initializations++;
                if (_armed)
                    _pending = this;
                else
                    Finish();
            }

            internal void Finish() => Complete(true, true, (string)null);

            protected override bool InvokeWaitForCompletion()
            {
                SynchronousWaits++;
                return HandleObserver.IsValid() && HandleObserver.IsDone;
            }
        }
    }

    // Run alone in a fresh prepared host. Later cases deliberately keep the official AA facilities warm.
    [Category("YL.Resource.Addressables.Localization")]
    public sealed class ResourceLocalizationCoexistenceTests
    {
        private const string SettingsName = "YL Addressables Localization Coexistence";
        private const string StringTable = "YL_AA_Coexistence_Strings";
        private const string AssetTable = "YL_AA_Coexistence_Assets";
        private const string MessageKey = "ownership/message";
        private const string EnglishMessage = "official addressables content";
        private const string EnglishGreeting = "Hello from Localization";
        private const string ChineseMessage = "\u4e2d\u6587\u672c\u5730\u5316\u8d44\u4ea7";
        private const string ChineseGreeting = "\u4f60\u597d\uff0c\u5171\u5b58";
        private readonly List<AsyncOperationHandle> _ownedHandles = new();
        private ResourceStartup _startup;
        private LocalizationSettings _previousSettings;
        private LocalizationSettings _settings;
        private AsyncOperationHandle<LocalizationSettings> _localizationInitialization;

        [SetUp]
        public void SetUp()
        {
            _startup = null;
            _settings = null;
            _previousSettings = null;
            _localizationInitialization = default;
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over an external YL resource authority.");
            var previous = LocalizationSettings.GetInstanceDontCreateDefault();
            _previousSettings = Resources.Load<LocalizationSettings>(SettingsName);
            Assert.That(_previousSettings, Is.Not.Null,
                "Prepare ResourceAddressablesTestContent.Build(), then ResourceLocalizationFixture.Build() in the isolated host.");
            if (previous)
                Assert.That(previous, Is.SameAs(_previousSettings), "Do not replace another application's Localization settings.");
            Assert.That(_previousSettings.name, Is.EqualTo(SettingsName), "Do not replace another application's Localization settings.");
            Assert.That(_previousSettings.GetType(), Is.EqualTo(typeof(LocalizationSettings)));
            _settings = Object.Instantiate(_previousSettings);
            _settings.name = SettingsName + " (Runtime)";
            _settings.ResetState();
            LocalizationSettings.Instance = _settings;
            LocalizationSettings.InitializeSynchronously = false;
            Assert.That(_settings.GetAvailableLocales().GetType(), Is.EqualTo(typeof(LocalesProvider)));
            Assert.That(_settings.GetStringDatabase().GetType(), Is.EqualTo(typeof(LocalizedStringDatabase)));
            Assert.That(_settings.GetAssetDatabase().GetType(), Is.EqualTo(typeof(LocalizedAssetDatabase)));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            ResourceLocalizationInitializationGate.Open();
            foreach (var handle in _ownedHandles)
            {
                if (handle.IsValid() && !handle.IsDone)
                    yield return Wait(() => !handle.IsValid() || handle.IsDone, "owned native operation cleanup");
            }
            if (_localizationInitialization.IsValid() && !_localizationInitialization.IsDone)
                yield return Wait(_localizationInitialization, "Localization cleanup initialization");
            if (_startup?.Resources != null)
                yield return StopYL();
            if (_settings)
            {
                yield return Wait(_settings.GetSelectedLocaleAsync(), "owned selected Locale cleanup");
                ((IDisposable)_settings).Dispose();
                LocalizationSettings.Instance = _previousSettings;
                Object.Destroy(_settings);
                _settings = null;
            }
            int lostOwners = _ownedHandles.Count(handle => !handle.IsValid());
            int lostAssets = _ownedHandles.Count(handle => handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded &&
                handle.Result is Object asset && !asset);
            for (int i = _ownedHandles.Count - 1; i >= 0; i--)
            {
                if (_ownedHandles[i].IsValid())
                    Addressables.Release(_ownedHandles[i]);
            }
            _ownedHandles.Clear();
            Assert.That(lostOwners, Is.Zero, "Closing YL and Localization must leave every independent AA reference valid until its owner releases it.");
            Assert.That(lostAssets, Is.Zero, "A retained localized or official asset cannot unload when another client closes.");
            yield return null;
        }

        [UnityTest]
        [Order(0)]
        public IEnumerator ExternalOfficialInitializationInFlight_YLAndLocalizationJoinWithoutTakingOwnership()
        {
            Assert.That(Addressables.ResourceLocators, Is.Empty, "Run this fixture first in a fresh isolated PlayMode host.");
            ResourceLocalizationInitializationGate.Arm();
            var nativeInitialization = Own(Addressables.InitializeAsync(false));
            yield return Wait(() => ResourceLocalizationInitializationGate.IsWaiting || nativeInitialization.IsDone,
                "official InitializationObjects fixture gate");
            Assert.That(ResourceLocalizationInitializationGate.IsWaiting, Is.True,
                "The built runtime data must include this fixture's initializer; do not substitute an already initialized AA instance.");
            Assert.That(nativeInitialization.IsDone, Is.False);

            using var start = BeginYL();
            var localization = BeginLocalization();
            yield return null;
            yield return null;
            Assert.That(start.IsDone, Is.False, "YL must wait for the official initialization already in progress.");
            Assert.That(localization.IsDone, Is.False);
            Assert.That(ResourceLocalizationInitializationGate.Initializations, Is.EqualTo(1));
            Assert.That(ResourceLocalizationInitializationGate.SynchronousWaits, Is.Zero);

            ResourceLocalizationInitializationGate.Open();
            yield return Wait(nativeInitialization, "external official initialization");
            yield return Wait(start, "YL joined initialization");
            Assert.That(start.TakeResult().Resources, Is.SameAs(_startup.Resources));
            yield return Wait(localization, "Localization joined initialization");
            AssertLocales();
            yield return ProbeYLMessage();
            var external = Own(Addressables.LoadAssetAsync<TextAsset>(MessageKey));
            yield return Wait(external, "external TextAsset");
            var providers = Addressables.ResourceManager.ResourceProviders.ToArray();
            var manager = Addressables.ResourceManager;

            yield return StopYL();
            Assert.That(nativeInitialization.IsValid(), Is.True, "YL does not own the caller's InitializeAsync(false) reference.");
            Assert.That(Addressables.ResourceLocators, Does.Contain(nativeInitialization.Result));
            Assert.That(Addressables.ResourceManager, Is.SameAs(manager));
            CollectionAssert.AreEqual(providers, Addressables.ResourceManager.ResourceProviders.ToArray());
            AssertText(external, EnglishMessage);
            yield return AssertGreeting(EnglishGreeting);
        }

        [UnityTest]
        [Order(1)]
        public IEnumerator YLInitializesFirst_StoppingYLStillAllowsTheFirstOfficialLocalizationInitialization()
        {
            yield return StartYL();
            yield return ProbeYLMessage();
            var external = Own(Addressables.LoadAssetAsync<TextAsset>(MessageKey));
            yield return Wait(external, "external TextAsset before Localization");
            yield return StopYL();

            yield return Wait(BeginLocalization(), "first Localization initialization after YL stop");
            AssertLocales();
            var localized = RetainLocalization(LocalizationSettings.AssetDatabase.GetLocalizedAssetAsync<TextAsset>(AssetTable, "message"));
            yield return Wait(localized, "English localized asset after YL stop");
            Assert.That(localized.Result, Is.SameAs(external.Result));
            AssertText(external, EnglishMessage);
            yield return AssertGreeting(EnglishGreeting);
        }

        [UnityTest]
        [Order(2)]
        public IEnumerator LocalizationInitializesFirst_ItsLocalizedAssetAndIndependentAAHandleSurviveYLStop()
        {
            yield return Wait(BeginLocalization(), "Localization before YL");
            AssertLocales();
            var localized = RetainLocalization(LocalizationSettings.AssetDatabase.GetLocalizedAssetAsync<TextAsset>(AssetTable, "message"));
            var external = Own(Addressables.LoadAssetAsync<TextAsset>(MessageKey));
            yield return Wait(localized, "English localized asset");
            yield return Wait(external, "independent AA TextAsset");
            Assert.That(localized.Result, Is.SameAs(external.Result));

            yield return StartYL();
            yield return ProbeYLMessage();
            yield return StopYL();
            AssertText(localized, EnglishMessage);
            AssertText(external, EnglishMessage);
            Assert.That(_localizationInitialization.IsValid(), Is.True);
            Assert.That(LocalizationSettings.Instance, Is.SameAs(_settings));
            yield return AssertGreeting(EnglishGreeting);
        }

        [UnityTest]
        [Order(3)]
        public IEnumerator LocaleSwitchesWhileYLIsActiveAndAfterStop_KeepIndependentlyHeldAssetsAlive()
        {
            yield return Wait(BeginLocalization(), "Localization before locale switching");
            AssertLocales();
            yield return StartYL();
            var english = RetainLocalization(LocalizationSettings.AssetDatabase.GetLocalizedAssetAsync<TextAsset>(AssetTable, "message"));
            var external = Own(Addressables.LoadAssetAsync<TextAsset>(MessageKey));
            yield return Wait(english, "held English localized asset");
            yield return Wait(external, "held official English asset");
            var changes = new List<string>();
            Action<Locale> changed = locale => changes.Add(locale.Identifier.Code);
            _settings.OnSelectedLocaleChanged += changed;
            try
            {
                LocalizationSettings.SelectedLocale = LocalizationSettings.AvailableLocales.GetLocale("zh-Hans");
                yield return Wait(BeginLocalization(), "Chinese locale initialization with YL active");
                yield return Wait(() => changes.Contains("zh-Hans"), "Chinese locale notification");
                yield return AssertGreeting(ChineseGreeting);
                var chinese = RetainLocalization(LocalizationSettings.AssetDatabase.GetLocalizedAssetAsync<TextAsset>(AssetTable, "message"));
                yield return Wait(chinese, "Chinese localized asset");
                AssertText(chinese, ChineseMessage);
                AssertText(english, EnglishMessage);
                AssertText(external, EnglishMessage);
                yield return ProbeYLMessage();
                yield return StopYL();

                LocalizationSettings.SelectedLocale = LocalizationSettings.AvailableLocales.GetLocale("en");
                yield return Wait(BeginLocalization(), "English locale initialization after YL stop");
                yield return Wait(() => changes.Contains("en"), "English locale notification");
                yield return AssertGreeting(EnglishGreeting);
                AssertText(chinese, ChineseMessage);
                AssertText(external, EnglishMessage);
            }
            finally
            {
                _settings.OnSelectedLocaleChanged -= changed;
            }
        }

        [UnityTest]
        [Order(4)]
        public IEnumerator ANewYLSessionKeepsTheOfficialSelectedLocaleAndExternallyOwnedHandles()
        {
            yield return Wait(BeginLocalization(), "Localization before YL sessions");
            AssertLocales();
            var external = Own(Addressables.LoadAssetAsync<TextAsset>(MessageKey));
            yield return Wait(external, "AA owner across YL sessions");
            yield return StartYL();
            var first = _startup.Resources;
            yield return ProbeYLMessage();
            yield return StopYL();
            LocalizationSettings.SelectedLocale = LocalizationSettings.AvailableLocales.GetLocale("zh-Hans");
            yield return Wait(BeginLocalization(), "Chinese locale between YL sessions");
            yield return AssertGreeting(ChineseGreeting);

            yield return StartYL();
            Assert.That(_startup.Resources, Is.Not.SameAs(first));
            Assert.That(LocalizationSettings.Instance, Is.SameAs(_settings));
            Assert.That(LocalizationSettings.SelectedLocale.Identifier.Code, Is.EqualTo("zh-Hans"));
            yield return ProbeYLMessage();
            var chinese = RetainLocalization(LocalizationSettings.AssetDatabase.GetLocalizedAssetAsync<TextAsset>(AssetTable, "message"));
            yield return Wait(chinese, "Chinese asset in a new YL session");
            yield return StopYL();
            AssertText(chinese, ChineseMessage);
            AssertText(external, EnglishMessage);
            yield return AssertGreeting(ChineseGreeting);
        }

        private ResourceRequest<ResourceStartup> BeginYL()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            var configuration = new ResourceConfiguration();
            Assert.That(configuration.EnableStartupCatalogUpdates, Is.False);
            _startup = new ResourceStartup(configuration, bindCoreResourceProvider: false);
            return _startup.StartAsync();
        }

        private IEnumerator StartYL()
        {
            using var start = BeginYL();
            yield return Wait(start, "YL default load-only startup");
            Assert.That(start.TakeResult().Resources, Is.SameAs(_startup.Resources));
        }

        private IEnumerator StopYL()
        {
            using var stop = _startup.StopAsync();
            yield return Wait(stop, "YL owned cleanup");
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        private AsyncOperationHandle<LocalizationSettings> BeginLocalization()
            => _localizationInitialization = LocalizationSettings.InitializationOperation;

        private void AssertLocales()
        {
            CollectionAssert.AreEquivalent(new[] { "en", "zh-Hans" },
                LocalizationSettings.AvailableLocales.Locales.Select(locale => locale.Identifier.Code).ToArray());
            Assert.That(LocalizationSettings.SelectedLocale.Identifier.Code, Is.EqualTo("en"));
        }

        private IEnumerator ProbeYLMessage()
        {
            using var request = _startup.Resources.AcquireAsync<TextAsset>(MessageKey);
            yield return Wait(request, "YL official TextAsset");
            using var lease = request.TakeResult();
            Assert.That(lease.Asset.text, Is.EqualTo(EnglishMessage));
        }

        private IEnumerator AssertGreeting(string expected)
        {
            var text = RetainLocalization(LocalizationSettings.StringDatabase.GetLocalizedStringAsync(StringTable, "greeting"));
            yield return Wait(text, "official localized string");
            Assert.That(text.Result, Is.EqualTo(expected));
        }

        private AsyncOperationHandle<T> Own<T>(AsyncOperationHandle<T> owned)
        {
            _ownedHandles.Add(owned);
            return owned;
        }

        // Localization database result operations auto-release next frame; retain a real public owner.
        private AsyncOperationHandle<T> RetainLocalization<T>(AsyncOperationHandle<T> borrowed)
            => Own(Addressables.ResourceManager.Acquire(borrowed));

        private static void AssertText(AsyncOperationHandle<TextAsset> handle, string expected)
        {
            Assert.That(handle.IsValid(), Is.True, "Only the owner releases its independent AA reference.");
            Assert.That(handle.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            Assert.That(handle.Result, Is.Not.Null);
            Assert.That(handle.Result.text, Is.EqualTo(expected));
        }

        private static IEnumerator Wait<T>(AsyncOperationHandle<T> handle, string operation)
        {
            yield return Wait(() => !handle.IsValid() || handle.IsDone, operation);
            Assert.That(handle.IsValid(), Is.True, operation + " lost its owner.");
            Assert.That(handle.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), handle.OperationException?.ToString());
        }

        private static IEnumerator Wait<T>(ResourceRequest<T> request, string operation)
        {
            yield return Wait(() => request.IsDone, operation);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
        }

        private static IEnumerator Wait(Func<bool> completed, string operation)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 30;
            while (!completed() && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.That(completed(), Is.True, operation + " timed out.");
        }
    }
}

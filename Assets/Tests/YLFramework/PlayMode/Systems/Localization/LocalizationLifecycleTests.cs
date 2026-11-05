using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.Events;
using YLFramework.Localization;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Data
{
    public class LocalizationLifecycleTests
    {
        private readonly List<Object> _objects = new();
        private bool _hadPreference;
        private int _preference;
        private ResourceSystem _resources;
        private ControlledSource _source;
        private Scene _scene;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another Resource owner.");
            Assert.That(LocalizationManager.Configs, Is.Empty, "Do not close another localization owner.");
            LocalizationManager.Shutdown();
            _hadPreference = PlayerPrefs.HasKey(LocalizationManager.LanguagePreferenceKey);
            _preference = PlayerPrefs.GetInt(LocalizationManager.LanguagePreferenceKey);
            LocalizationManager.Language = SystemLanguage.English;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                yield return SceneManager.UnloadSceneAsync(_scene);
            }

            _scene = default;
            LocalizationManager.Shutdown();
            if (_source != null)
            {
                foreach (var operation in _source.Operations)
                {
                    if (!operation.IsDone)
                    {
                        operation.Finish(null, new Exception("test cleanup"));
                    }
                }
            }

            if (_resources != null)
            {
                using var closed = _resources.ShutdownAsync();
                Assert.That(closed.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
            foreach (var value in _objects)
            {
                if (value)
                {
                    Object.DestroyImmediate(value);
                }
            }

            _objects.Clear();
            if (_hadPreference)
            {
                PlayerPrefs.SetInt(LocalizationManager.LanguagePreferenceKey, _preference);
            }
            else
            {
                PlayerPrefs.DeleteKey(LocalizationManager.LanguagePreferenceKey);
            }

            PlayerPrefs.Save();
        }

        private MultiLanguageConfigs Root(string value)
        {
            var root = ScriptableObject.CreateInstance<MultiLanguageConfigs>();
            var language = ScriptableObject.CreateInstance<LanguageConfig>();
            language.language = SystemLanguage.English;
            language.languagesInfo.Add(new LanguageInfo { id = 1, str = value });
            root.Languages.Add(language);
            _objects.Add(root);
            _objects.Add(language);
            return root;
        }

        [Test]
        public void EnabledTextReceivesLateConfigWithoutRestartingBusinessObject()
        {
            var go = new GameObject("YL localization lifecycle", typeof(RectTransform));
            _objects.Add(go);
            go.SetActive(false);
            var text = go.AddComponent<Text>();
            var counter = go.AddComponent<LocalizationActivationCounter>();
            var localize = go.AddComponent<LocalizeString>();
            localize.Id = 1;
            LogAssert.Expect(LogType.Warning, new Regex("Localization.*ID=1.*missing text"));
            go.SetActive(true);
            Assert.That(counter.enables, Is.EqualTo(1));
            using var registered = LocalizationManager.AddConfig(Root("arrived"));
            Assert.That(text.text, Is.EqualTo("arrived"));
            localize.Refresh();
            LocalizationManager.Refresh();
            Assert.That(counter.enables, Is.EqualTo(1));
            Assert.That(counter.disables, Is.Zero);
            go.SetActive(false);
            registered.Dispose();
            Assert.That(counter.disables, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReflectedStringMemberUsesArgsAppendsAndRefreshWithoutRestartingObject(bool useProperty)
        {
            var root = Root("Hello {0}");
            root.Languages[0].languagesInfo.Add(new LanguageInfo { id = 2, str = "world" });
            root.Languages[0].languagesInfo.Add(new LanguageInfo { id = 3, str = "tail" });
            using var registration = LocalizationManager.AddConfig(root);
            var go = new GameObject("YL reflected localization");
            _objects.Add(go);
            go.SetActive(false);
            var target = go.AddComponent<LocalizationReflectionTarget>();
            target.text = "original field";
            target.Text = "original property";
            Func<string> read = () => useProperty ? target.Text : target.text;
            var counter = go.AddComponent<LocalizationActivationCounter>();
            var localize = go.AddComponent<LocalizeString>();
            localize.Id = 1;
            typeof(LocalizeString).GetField("componentType", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(localize, ComponentType.Other);
            typeof(LocalizeString).GetField("componentFullName", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(localize, typeof(LocalizationReflectionTarget).FullName);
            typeof(LocalizeString).GetField("fieldName", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(localize, useProperty ? nameof(LocalizationReflectionTarget.Text) : nameof(LocalizationReflectionTarget.text));
            localize.SetArgs(new[] { new AppendData { type = AppendType.Id, id = 2, richText = "<b>{0}</b>" } });
            Assert.That(read(), Is.EqualTo(useProperty ? "original property" : "original field"));

            go.SetActive(true);
            Assert.That(read(), Is.EqualTo("Hello <b>world</b>"));
            localize.Append(3, " [{0}]");
            Assert.That(read(), Is.EqualTo("Hello <b>world</b> [tail]"));
            localize.Append("!", "({0})");
            Assert.That(read(), Is.EqualTo("Hello <b>world</b> [tail](!)"));
            localize.SetArgs(new[] { new AppendData { type = AppendType.String, str = "reader", richText = "<i>{0}</i>" } });
            Assert.That(read(), Is.EqualTo("Hello <i>reader</i> [tail](!)"));
            localize.Appends.Add(new AppendData { type = AppendType.String, str = "?" });
            Assert.That(read(), Is.EqualTo("Hello <i>reader</i> [tail](!)"), "Direct list edits wait for Refresh.");
            localize.Refresh();
            LocalizationManager.Refresh();
            Assert.That(read(), Is.EqualTo("Hello <i>reader</i> [tail](!)?"));
            Assert.That(useProperty ? target.text : target.Text,
                Is.EqualTo(useProperty ? "original field" : "original property"));
            Assert.That(counter.enables, Is.EqualTo(1));
            Assert.That(counter.disables, Is.Zero);
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.EqualTo(1));
            go.SetActive(false);
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.Zero);
        }

        [Test]
        public void ShutdownCancelsPendingRootAndLateProducerCannotRegister()
        {
            var root = Root("late");
            var address = new ResourceAddress("localization-test", "root");
            _source = new ControlledSource();
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog(address.ProjectId, new[] { new ResourceCatalogEntry("root", address, new ResourceLocation(_source.Id, "root", typeof(MultiLanguageConfigs))) })
            }, new IResourceSource[] { _source }));
            _resources = initialize.TakeResult();
            using var handle = LocalizationManager.LoadConfigAsync(_resources, address);
            Assert.That(handle.IsLoading, Is.True);
            LocalizationManager.Shutdown();
            Assert.That(handle.IsDisposed, Is.True);
            _source.Operations[0].Finish(root);
            Assert.That(LocalizationManager.Configs.Count, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void ReentrantShutdownDuringSourceStartDisposesRequestReturnedAfterOwnerClosed()
        {
            var root = Root("late");
            var address = new ResourceAddress("localization-test", "root");
            _source = new ControlledSource { OnLoad = LocalizationManager.Shutdown };
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog(address.ProjectId, new[] { new ResourceCatalogEntry("root", address, new ResourceLocation(_source.Id, "root", typeof(MultiLanguageConfigs))) })
            }, new IResourceSource[] { _source }));
            _resources = initialize.TakeResult();
            using var handle = LocalizationManager.LoadConfigAsync(_resources, address);
            Assert.That(handle.IsDisposed, Is.True);
            Assert.That(handle.IsLoading, Is.False, "A closed owner must not retain a new pending consumer.");
            _source.Operations[0].Finish(root);
            Assert.That(LocalizationManager.Configs.Count, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void ShutdownRejectsRegistrationAndLoadingFromRootReleaseAndClearsReentrantListeners()
        {
            var root = Root("first");
            var other = Root("must not register");
            other.Languages[0].languagesInfo[0].id = 2;
            var address = new ResourceAddress("localization-test", "root");
            _source = new ControlledSource();
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog(address.ProjectId, new[] { new ResourceCatalogEntry("root", address, new ResourceLocation(_source.Id, "root", typeof(MultiLanguageConfigs))) })
            }, new IResourceSource[] { _source }));
            _resources = initialize.TakeResult();
            using var handle = LocalizationManager.LoadConfigAsync(_resources, address);
            _source.Operations[0].Finish(root);
            Exception addError = null;
            Exception loadError = null;
            var calls = 0;
            _source.OnRelease = () =>
            {
                try
                {
                    LocalizationManager.AddConfig(other);
                }
                catch (InvalidOperationException error)
                {
                    addError = error;
                }
                try
                {
                    LocalizationManager.LoadConfigAsync(_resources, address);
                }
                catch (InvalidOperationException error)
                {
                    loadError = error;
                }
                LocalizationManager.Changed.Register(() => calls++);
            };
            LocalizationManager.Shutdown();
            Assert.That(addError, Is.TypeOf<InvalidOperationException>());
            Assert.That(loadError, Is.TypeOf<InvalidOperationException>());
            Assert.That(_source.Operations.Count, Is.EqualTo(1), "Closing must reject before a second native producer starts.");
            Assert.That(handle.IsDisposed, Is.True);
            Assert.That(LocalizationManager.Configs, Is.Empty);
            LocalizationManager.Refresh();
            Assert.That(calls, Is.Zero, "Listeners added from closing callbacks must not escape the closing owner.");
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
        }

        [Test]
        public void ShutdownAttemptsEveryOwnerWhenNativeRetentionReleaseThrows()
        {
            var first = Root("first");
            var second = Root("second");
            second.Languages[0].languagesInfo[0].id = 2;
            var firstAddress = new ResourceAddress("localization-test", "first");
            var secondAddress = new ResourceAddress("localization-test", "second");
            _source = new ControlledSource();
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog("localization-test", new[]
                {
                    new ResourceCatalogEntry("first", firstAddress, new ResourceLocation(_source.Id, "first", typeof(MultiLanguageConfigs))),
                    new ResourceCatalogEntry("second", secondAddress, new ResourceLocation(_source.Id, "second", typeof(MultiLanguageConfigs)))
                })
            }, new IResourceSource[] { _source }));
            _resources = initialize.TakeResult();
            using var firstHandle = LocalizationManager.LoadConfigAsync(_resources, firstAddress);
            using var secondHandle = LocalizationManager.LoadConfigAsync(_resources, secondAddress);
            var released = 0;
            _source.Operations[0].Finish(first, retention: new CallbackRetention(() =>
            {
                released++;
                throw new InvalidOperationException("first retention failed");
            }));
            _source.Operations[1].Finish(second, retention: new CallbackRetention(() =>
            {
                released++;
                throw new InvalidOperationException("second retention failed");
            }));
            var error = Assert.Throws<AggregateException>(LocalizationManager.Shutdown);
            Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(released, Is.EqualTo(2));
            Assert.That(firstHandle.IsDisposed && secondHandle.IsDisposed, Is.True);
            Assert.That(LocalizationManager.Configs, Is.Empty);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            using var next = LocalizationManager.AddConfig(Root("new owner after close"));
            Assert.That(next.Config, Is.Not.Null, "A failed release must not permanently poison the closing barrier.");
        }

        private MultiLanguageConfigs InitializeImages()
        {
            var root = Root("text");
            root.Languages[0].languagesInfo.Add(new LanguageInfo
            {
                id = 2,
                type = MultiLanguageType.Image,
                image = new ImageInfo { projectName = "localization-test", imageName = "english" }
            });
            var french = ScriptableObject.CreateInstance<LanguageConfig>();
            _objects.Add(french);
            french.language = SystemLanguage.French;
            french.languagesInfo.Add(new LanguageInfo
            {
                id = 2,
                type = MultiLanguageType.Image,
                image = new ImageInfo { projectName = "localization-test", imageName = "french" }
            });
            root.Languages.Add(french);
            _source = new ControlledSource();
            var englishAddress = new ResourceAddress("localization-test", "english");
            var frenchAddress = new ResourceAddress("localization-test", "french");
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog("localization-test", new[]
                {
                    new ResourceCatalogEntry("english", englishAddress, new ResourceLocation(_source.Id, "english", typeof(Texture2D))),
                    new ResourceCatalogEntry("french", frenchAddress, new ResourceLocation(_source.Id, "french", typeof(Texture2D)))
                })
            }, new IResourceSource[] { _source }));
            Assert.That(initialize.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialize.Error?.ToString());
            _resources = initialize.TakeResult();
            return root;
        }

        [UnityTest]
        public IEnumerator ImageLanguageSwitchLateOldProducerCannotReplaceNewDisplay()
        {
            using var registration = LocalizationManager.AddConfig(InitializeImages());
            _resources.ConfigureDiagnosticsHistory(8);
            var go = new GameObject("YL localized image", typeof(RectTransform));
            _objects.Add(go);
            go.SetActive(false);
            var image = go.AddComponent<Image>();
            var originalTexture = new Texture2D(2, 2);
            var originalSprite = Sprite.Create(originalTexture, new Rect(0, 0, 2, 2), Vector2.one * .5f);
            _objects.Add(originalSprite);
            _objects.Add(originalTexture);
            image.sprite = originalSprite;
            var localize = go.AddComponent<LocalizeImage>();
            var loader = go.GetComponent<ResourceImage>();
            Assert.That(loader, Is.Not.Null, "LocalizeImage must install its ResourceImage dependency.");
            localize.Id = 2;
            Assert.That(_source.Operations, Is.Empty, "Inactive bindings must not start image requests.");
            go.SetActive(true);
            Assert.That(_source.Operations.Count, Is.EqualTo(1));
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.EqualTo(1));
            Assert.That(_resources.GetDiagnosticsReport().History, Is.Empty,
                "Enabling must not start and cancel a duplicate consumer against the shared producer.");
            Assert.That(image.sprite, Is.SameAs(originalSprite));
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.EqualTo(1));
            LocalizationManager.Language = SystemLanguage.French;
            Assert.That(_source.Operations.Count, Is.EqualTo(2));
            var oldTexture = new Texture2D(2, 2);
            var newTexture = new Texture2D(2, 2);
            _objects.Add(oldTexture);
            _objects.Add(newTexture);
            _source.Operations[1].Finish(newTexture);
            Assert.That(image.sprite, Is.Not.Null.And.Not.SameAs(originalSprite));
            var displayed = image.sprite;
            Assert.That(displayed.texture, Is.SameAs(newTexture));
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            _source.Operations[0].Finish(oldTexture);
            Assert.That(image.sprite, Is.SameAs(displayed));
            Assert.That(_source.Releases, Is.EqualTo(1));
            localize.enabled = false;
            Assert.That(loader.IsLoading, Is.False);
            Assert.That(image.sprite, Is.SameAs(originalSprite));
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(2));
            LocalizationManager.Refresh();
            Assert.That(_source.Operations.Count, Is.EqualTo(2), "Disabling the binding must detach its subscription.");
            yield return null;
            Assert.That(displayed == null, Is.True, "The display Sprite is owned by ResourceImage, not the borrowed texture.");
            Assert.That(originalSprite && originalTexture && newTexture, Is.True);
            localize.enabled = true;
            Assert.That(_source.Operations.Count, Is.EqualTo(3), "Re-enabling must start only one new consumer.");
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.EqualTo(1));
            go.SetActive(false);
            Assert.That(loader.IsLoading, Is.False);
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            _source.Operations[2].Finish(newTexture);
            Assert.That(image.sprite, Is.SameAs(originalSprite), "A producer completing after disable cannot restore its display.");
            Assert.That(_resources.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyingLocalizedImageRestoresSurvivingTargetAndReleasesLease()
        {
            using var registration = LocalizationManager.AddConfig(InitializeImages());
            var display = new GameObject("YL surviving image target", typeof(RectTransform));
            _objects.Add(display);
            var image = display.AddComponent<RawImage>();
            var originalTexture = new Texture2D(2, 2);
            var loadedTexture = new Texture2D(2, 2);
            _objects.Add(originalTexture);
            _objects.Add(loadedTexture);
            image.texture = originalTexture;
            var go = new GameObject("YL destroyed localization owner");
            _objects.Add(go);
            go.SetActive(false);
            var localize = go.AddComponent<LocalizeImage>();
            localize.Id = 2;
            var loader = go.GetComponent<ResourceImage>();
            Assert.That(loader, Is.Not.Null);
            loader.Target = image;
            go.SetActive(true);
            Assert.That(_source.Operations.Count, Is.EqualTo(1));
            _source.Operations[0].Finish(loadedTexture);
            Assert.That(image.texture, Is.SameAs(loadedTexture));
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            Object.Destroy(go);
            yield return null;
            Assert.That(go == null, Is.True);
            Assert.That(image.texture, Is.SameAs(originalTexture));
            Assert.That(originalTexture && loadedTexture, Is.True, "Destroy must not destroy borrowed display assets.");
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            LocalizationManager.Language = SystemLanguage.French;
            LocalizationManager.Refresh();
            Assert.That(_source.Operations.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator UnloadingOwnedSceneReleasesDisplayedAndPendingImagesWithoutRevivingUi()
        {
            var root = InitializeImages();
            root.Languages[0].languagesInfo.Add(new LanguageInfo
            {
                id = 3,
                type = MultiLanguageType.Image,
                image = new ImageInfo { projectName = "localization-test", imageName = "french" }
            });
            using var registration = LocalizationManager.AddConfig(root);
            var display = new GameObject("YL scene survivor target", typeof(RectTransform));
            _objects.Add(display);
            var survivingImage = display.AddComponent<RawImage>();
            var originalTexture = new Texture2D(2, 2);
            var loadedTexture = new Texture2D(2, 2);
            var lateTexture = new Texture2D(2, 2);
            _objects.Add(originalTexture);
            _objects.Add(loadedTexture);
            _objects.Add(lateTexture);
            survivingImage.texture = originalTexture;
            var previousScene = SceneManager.GetActiveScene();
            _scene = SceneManager.CreateScene("YL-Localization-" + Guid.NewGuid().ToString("N"));
            var displayedGo = new GameObject("YL scene displayed localization", typeof(RectTransform));
            _objects.Add(displayedGo);
            displayedGo.SetActive(false);
            SceneManager.MoveGameObjectToScene(displayedGo, _scene);
            var displayedImage = displayedGo.AddComponent<RawImage>();
            displayedGo.AddComponent<LocalizeImage>().Id = 2;
            var pendingGo = new GameObject("YL scene pending localization");
            _objects.Add(pendingGo);
            pendingGo.SetActive(false);
            SceneManager.MoveGameObjectToScene(pendingGo, _scene);
            pendingGo.AddComponent<LocalizeImage>().Id = 3;
            pendingGo.GetComponent<ResourceImage>().Target = survivingImage;
            displayedGo.SetActive(true);
            pendingGo.SetActive(true);
            Assert.That(_source.Operations.Count, Is.EqualTo(2));
            _source.Operations[0].Finish(loadedTexture);
            Assert.That(displayedImage.texture, Is.SameAs(loadedTexture));
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.EqualTo(1));
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.EqualTo(2));

            yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null;
            Assert.That(_scene.isLoaded, Is.False);
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(previousScene));
            Assert.That(displayedGo == null && pendingGo == null, Is.True);
            Assert.That(((EventChannelBase)LocalizationManager.Changed).ListenerCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(survivingImage.texture, Is.SameAs(originalTexture));
            LocalizationManager.Language = SystemLanguage.French;
            LocalizationManager.Refresh();
            Assert.That(_source.Operations.Count, Is.EqualTo(2));
            _source.Operations[1].Finish(lateTexture);
            Assert.That(survivingImage.texture, Is.SameAs(originalTexture), "A late producer cannot write through a destroyed scene owner.");
            Assert.That(_resources.GetDiagnosticsSnapshot().WaiterCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(_resources.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(ResourceSystem.Current, Is.SameAs(_resources), "Scene consumers do not own the resource service.");
            Assert.That(LocalizationManager.Configs, Has.Count.EqualTo(1), "The external registration outlives its scene consumers.");
        }

        private sealed class ControlledOperation : ResourceSourceOperation
        {
            public void Finish(Object value, Exception error = null, IDisposable retention = null) => Complete(value, error, retention);
        }

        private sealed class CallbackRetention : IDisposable
        {
            private Action _close;
            public CallbackRetention(Action close) => this._close = close;
            public void Dispose()
            {
                var callback = _close;
                _close = null;
                callback?.Invoke();
            }
        }

        private sealed class ControlledSource : IResourceSource
        {
            public ResourceSourceId Id => new("localization-controlled");
            public bool SupportsSynchronousLoad => false;
            public readonly List<ControlledOperation> Operations = new();
            public int Releases;
            public Action OnLoad;
            public Action OnRelease;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => throw new NotSupportedException();
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type)
            {
                OnLoad?.Invoke();
                var operation = new ControlledOperation();
                Operations.Add(operation);
                return operation;
            }
            public void Release(Object value)
            {
                Releases++;
                OnRelease?.Invoke();
            }
        }
    }

    public class LocalizationActivationCounter : MonoBehaviour
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Enables")]
        public int enables;
        [global::UnityEngine.Serialization.FormerlySerializedAs("Disables")]
        public int disables;
        private void OnEnable() => enables++;
        private void OnDisable() => disables++;
    }
}

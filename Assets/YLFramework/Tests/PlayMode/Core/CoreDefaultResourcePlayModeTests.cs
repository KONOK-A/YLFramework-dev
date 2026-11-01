using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace YLFramework.Core.Tests
{
    public class CoreDefaultResourcePlayModeTests
    {
        private static readonly FieldInfo _instanceField = typeof(PanelManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo _ownedEventSystemField = typeof(PanelManager).GetField("_eventSystem", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo _refresh = typeof(PanelManager).GetMethod("RefreshEventSystem", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly List<GameObject> _ownedObjects = new List<GameObject>();
        private readonly List<UIRootContext> _ownedRoots = new List<UIRootContext>();
        private object _previousManager;
        private object _manager;
        private CoreUpdateHostScope _updateHosts;

        [SetUp]
        public void SetUp()
        {
            Assert.That(EventSystem.current, Is.Null, "This fixture requires an empty test scene; never remove external input objects.");
            _updateHosts = new CoreUpdateHostScope();
            _previousManager = _instanceField.GetValue(null);
            // Isolate the resource paths without registering or clearing shared manager callbacks.
            _manager = Activator.CreateInstance(typeof(PanelManager), true);
            _instanceField.SetValue(null, _manager);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_manager != null)
            {
                foreach (var root in _ownedRoots)
                {
                    if (root)
                    {
                        typeof(PanelManager).GetMethod("UnregisterRoot", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { root });
                    }
                }

                typeof(PanelManager).GetMethod("ReleaseOwnedInput", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_manager, null);
            }
            foreach (var go in _ownedObjects)
            {
                if (go)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }

            _ownedObjects.Clear();
            yield return null;
            _ownedRoots.Clear();
            _instanceField.SetValue(null, _previousManager);
            _manager = null;
            _updateHosts.ReleaseOwnedIdleHosts();
            yield return null;
        }

        [UnityTest]
        public IEnumerator PreboundHostDefaultRootNotifiesOnceButCustomRegistrationNeverNotifies()
        {
            Assert.That(UnityEngine.Object.FindObjectsByType<YLFrameworkRoot>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty,
                "此用例不覆盖或销毁外部宿主。");
            var previous = PanelManager.CanvasCreated;
            int notifications = 0;
            try
            {
                PanelManager.CanvasCreated = _ => notifications++;
                var custom = CreateRoot();
                PanelManager.RegisterRoot(custom);
                Assert.That(notifications, Is.Zero);
                var hostObject = new GameObject("YLCore.PreboundHost");
                _ownedObjects.Add(hostObject);
                var host = hostObject.AddComponent<YLFrameworkRoot>();
                host.GetModuleRoot("Audio");
                host.GetModuleRoot("Action");
                Assert.That(host.GetComponentInChildren<UIRootContext>(true), Is.Null, "非 UI 宿主层级访问不能创建默认 UI。");
                var bound = CreateRoot();
                bound.transform.SetParent(host.transform, false);
                typeof(YLFrameworkRoot).GetField("panelRoot", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, bound);
                var prepare = typeof(YLFrameworkRoot).GetMethod("GetPanelRoot", BindingFlags.Instance | BindingFlags.NonPublic);
                PanelManager.CanvasCreated = canvas =>
                {
                    notifications++;
                    Assert.That(canvas, Is.SameAs(bound.Canvas));
                    var recursive = Assert.Throws<TargetInvocationException>(() => prepare.Invoke(host, null));
                    Assert.That(recursive.InnerException, Is.TypeOf<InvalidOperationException>());
                };
                Assert.That(prepare.Invoke(host, null), Is.SameAs(bound));
                Assert.That(prepare.Invoke(host, null), Is.SameAs(bound));
                PanelManager.RegisterRoot(custom);
                Assert.That(notifications, Is.EqualTo(1));
                yield return null;
                Assert.That(prepare.Invoke(host, null), Is.SameAs(bound));
                Assert.That(notifications, Is.EqualTo(1));
            }
            finally
            {
                PanelManager.CanvasCreated = previous;
            }
        }

        [UnityTest]
        public IEnumerator DefaultEventSystemIsCreatedOnlyOnceWhenNoneExists()
        {
            var root = CreateRoot();
            var created = (GameObject)_ownedEventSystemField.GetValue(_manager);
            Assert.That(created, Is.Not.Null);
            Assert.That(created.name, Is.EqualTo("EventSystem(Created By YLFramework.Core)"));
            Assert.That(EventSystem.current, Is.SameAs(created.GetComponent<EventSystem>()));
            yield return null;
            _refresh.Invoke(null, null);
            PanelManager.RegisterRoot(root);
            Assert.That(_ownedEventSystemField.GetValue(_manager), Is.SameAs(created));
            Assert.That(UnityEngine.Object.FindObjectsOfType<EventSystem>(), Has.Length.EqualTo(1));
            Assert.That(created.GetComponents<BaseInputModule>(), Has.Length.EqualTo(1));
#if ENABLE_INPUT_SYSTEM
            Assert.That(EventSystem.current.currentInputModule.GetType().FullName,
                Is.EqualTo("UnityEngine.InputSystem.UI.InputSystemUIInputModule"));
#else
            Assert.That(EventSystem.current.currentInputModule, Is.SameAs(created.GetComponent<StandaloneInputModule>()));
#endif
            root.gameObject.SetActive(false);
            yield return null;
            Assert.That(created == null, Is.True, "The framework releases only its own input when the last root is suspended.");
            Assert.That(EventSystem.current, Is.Null);
        }

        [UnityTest]
        public IEnumerator OwnedInputLivesUntilLastServicedRootEnds()
        {
            var first = CreateRoot();
            var second = CreateRoot(persistent: true);
            var created = (GameObject)_ownedEventSystemField.GetValue(_manager);
            UnityEngine.Object.Destroy(first.gameObject);
            yield return null;
            yield return null;
            Assert.That(created, Is.Not.Null);
            Assert.That(EventSystem.current, Is.SameAs(created.GetComponent<EventSystem>()));
            UnityEngine.Object.Destroy(second.gameObject);
            yield return null;
            yield return null;
            Assert.That(created == null, Is.True, "Only the self-created system must end with the last UI root.");
            Assert.That(EventSystem.current, Is.Null);
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        [UnityTest]
        public IEnumerator LaterExternalSystemIsBorrowedWithoutOwningOrChangingItsModule()
        {
            CreateRoot();
            var owned = (GameObject)_ownedEventSystemField.GetValue(_manager);
            var external = new GameObject("LaterExternalInput", typeof(EventSystem), typeof(StandaloneInputModule));
            _ownedObjects.Add(external);
            var input = external.GetComponent<StandaloneInputModule>();
            input.repeatDelay = 0.91f;
            yield return null;
            yield return null;
            Assert.That(EventSystem.current, Is.SameAs(external.GetComponent<EventSystem>()));
            Assert.That(owned == null, Is.True);
            Assert.That(input.enabled, Is.True);
            Assert.That(input.repeatDelay, Is.EqualTo(0.91f));
        }
#endif

#if ENABLE_INPUT_SYSTEM
        [UnityTest]
        public IEnumerator BorrowingLaterInputSystemKeepsItsRuntimeDefaultActionsAlive()
        {
            CreateRoot();
            var owned = (GameObject)_ownedEventSystemField.GetValue(_manager);
            var external = new GameObject("LaterInputSystem", typeof(EventSystem));
            _ownedObjects.Add(external);
            var type = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            var module = external.AddComponent(type);
            var asset = (UnityEngine.Object)type.GetProperty("actionsAsset").GetValue(module);
            Assert.That(asset != null, Is.True);
            yield return null;
            yield return null;
            Assert.That(EventSystem.current, Is.SameAs(external.GetComponent<EventSystem>()));
            Assert.That(owned == null, Is.True);
            Assert.That(asset != null, Is.True, "Input System 1.14 shares runtime defaults; ending our module must not dispose borrowed actions.");
            var point = type.GetProperty("point").GetValue(module);
            var action = point.GetType().GetProperty("action").GetValue(point);
            Assert.That((bool)action.GetType().GetProperty("enabled").GetValue(action), Is.True);
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        [UnityTest]
        public IEnumerator ExistingExternalEventSystemIsReusedWithoutTakingOwnershipOrChangingInput()
        {
            var external = new GameObject("ExistingExternalInput", typeof(EventSystem), typeof(StandaloneInputModule));
            _ownedObjects.Add(external);
            var eventSystem = external.GetComponent<EventSystem>();
            var input = external.GetComponent<StandaloneInputModule>();
            var selected = new GameObject("ExternalSelection");
            _ownedObjects.Add(selected);
            eventSystem.firstSelectedGameObject = selected;
            eventSystem.SetSelectedGameObject(selected);
            eventSystem.pixelDragThreshold = 23;
            input.horizontalAxis = "Vertical";
            input.repeatDelay = 0.73f;
            yield return null;

            var components = external.GetComponents<Component>();
            var currentModule = eventSystem.currentInputModule;
            for (int i = 0; i < 3; i++)
            {
                _refresh.Invoke(null, null);
            }

            var parent = CreateRoot();
            Assert.That(parent.GetComponent<AutomaticAdaptationCanvasScaler>(), Is.Not.Null);
            Assert.That(_ownedEventSystemField.GetValue(_manager), Is.Null);
            Assert.That(EventSystem.current, Is.SameAs(eventSystem));
            Assert.That(eventSystem.currentInputModule, Is.SameAs(currentModule));
            Assert.That(external.GetComponents<Component>(), Is.EqualTo(components));
            Assert.That(eventSystem.firstSelectedGameObject, Is.SameAs(selected));
            Assert.That(eventSystem.currentSelectedGameObject, Is.SameAs(selected));
            Assert.That(eventSystem.pixelDragThreshold, Is.EqualTo(23));
            Assert.That(input.horizontalAxis, Is.EqualTo("Vertical"));
            Assert.That(input.repeatDelay, Is.EqualTo(0.73f));
            Assert.That(UnityEngine.Object.FindObjectsOfType<EventSystem>(), Has.Length.EqualTo(1));

            UnityEngine.Object.Destroy(parent.gameObject);
            yield return null;
            _refresh.Invoke(null, null);
            Assert.That(external && input && input.enabled && external.activeInHierarchy, Is.True);
            Assert.That(EventSystem.current, Is.SameAs(eventSystem));
            Assert.That(_ownedEventSystemField.GetValue(_manager), Is.Null);
        }
#endif

        [UnityTest]
        public IEnumerator ExplicitRootsPreserveNativeConfigurationAlongsideExternalCanvas()
        {
            var external = new GameObject("ExternalCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            _ownedObjects.Add(external);
            var externalCanvas = external.GetComponent<Canvas>();
            externalCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            externalCanvas.sortingOrder = 0;
            var externalComponents = external.GetComponents<Component>();
            var ui = CreateRoot(order: 23);
            var persistent = CreateRoot(persistent: true, order: 71);
            yield return null;

            Assert.That(ui, Is.Not.SameAs(persistent));
            Assert.That(ui.Canvas.sortingOrder, Is.EqualTo(23));
            Assert.That(persistent.Canvas.sortingOrder, Is.EqualTo(71));
            Assert.That(ui.gameObject.scene.name, Is.Not.EqualTo("DontDestroyOnLoad"),
                "Registering a custom root must not change its scene lifetime.");
            Assert.That(persistent.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            foreach (var parent in new[] { ui, persistent })
            {
                Assert.That(parent.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Assert.That(parent.GetComponent<AutomaticAdaptationCanvasScaler>().enabled, Is.True);
                Assert.That(parent.GetComponent<CanvasScaler>().referenceResolution, Is.EqualTo(new Vector2(1920, 1080)));
                Assert.That(parent.GetComponent<GraphicRaycaster>().enabled, Is.True);
                Assert.That(parent.GetComponents<UIRootContext>(), Has.Length.EqualTo(1));
                Assert.That(parent.GetComponents<CanvasScaler>(), Has.Length.EqualTo(1));
                Assert.That(parent.GetLayer(0).parent, Is.SameAs(parent.transform));
                Assert.That(parent.GetLayer(1).parent, Is.SameAs(parent.transform));
            }
            PanelManager.RegisterRoot(ui);
            PanelManager.RegisterRoot(persistent);
            Assert.That(ui.Canvas.sortingOrder, Is.EqualTo(23));
            Assert.That(persistent.Canvas.sortingOrder, Is.EqualTo(71));
            Assert.That(external.GetComponents<Component>(), Is.EqualTo(externalComponents));
            Assert.That(externalCanvas.sortingOrder, Is.Zero);
            Assert.That(UnityEngine.Object.FindObjectsOfType<EventSystem>(), Has.Length.EqualTo(1));
        }

        private UIRootContext CreateRoot(bool persistent = false, int order = 0)
        {
            var host = new GameObject("YLCore.InputRoot", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _ownedObjects.Add(host);
            host.SetActive(false);
            var canvas = host.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = host.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            host.AddComponent<AutomaticAdaptationCanvasScaler>();
            var root = CorePanelTestRoots.Create(host);
            _ownedRoots.Add(root);
            if (persistent)
            {
                UnityEngine.Object.DontDestroyOnLoad(host);
            }

            return root;
        }
    }
}

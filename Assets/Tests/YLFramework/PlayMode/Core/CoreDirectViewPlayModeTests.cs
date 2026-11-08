using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Events;
using YLFramework.Timing;
using Object = UnityEngine.Object;
using YLFramework.UI;

namespace YLFramework.Core.Tests
{
    public sealed class YLCoreDirectViewModule : YLFramework.Core.Module
    {
        public Action StartAction;
        public Action CloseAction;
        public override void OnStart() => StartAction?.Invoke();
        protected override void OnClose() => CloseAction?.Invoke();
    }

    public sealed class YLCoreUnobservedView : View
    {
    }

    public sealed class YLCoreTypedInitializationView : View
    {
        internal int InitializeCalls;
        internal object Payload;

        public override void OnLoaded(LoadViewArgs args)
        {
            InitializeCalls++;
            Payload = args.GetObject("payload");
            Assert.That(args.GetObject("nullable"), Is.Null);
            Assert.Throws<InvalidOperationException>(() => args.SetObject("payload", new object()));
            var reclaim = Assert.Throws<InvalidOperationException>(() =>
                Module.LoadView<YLCoreTypedInitializationView>("must-not-reach-resource-loading", loadViewArgs: args));
            StringAssert.Contains("Claimed LoadViewArgs", reclaim.Message);
        }
    }

    public sealed class YLCoreSelectionInputProbe : MonoBehaviour, UnityEngine.EventSystems.ICancelHandler, UnityEngine.EventSystems.IDeselectHandler, UnityEngine.EventSystems.ISelectHandler
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Cancels")]
        public int cancels;
        public Action Deselected;
        public Action Selected;
        public void OnCancel(UnityEngine.EventSystems.BaseEventData data) => cancels++;
        public void OnDeselect(UnityEngine.EventSystems.BaseEventData data) => Deselected?.Invoke();
        public void OnSelect(UnityEngine.EventSystems.BaseEventData data) => Selected?.Invoke();
    }

    public sealed class YLCoreOtherInputGate : MonoBehaviour
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Allowed")]
        public bool isAllowed;
        [global::UnityEngine.Serialization.FormerlySerializedAs("Reads")]
        public int reads;
        public bool IsInputAllowed
        {
            get
            {
                reads++;
                return isAllowed;
            }
        }
    }

    public sealed class YLCorePointerClickProbe : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler, UnityEngine.EventSystems.IPointerDownHandler
    {
        public readonly int[] Clicks = new int[3];
        public readonly List<UnityEngine.EventSystems.PointerEventData> Downs = new List<UnityEngine.EventSystems.PointerEventData>();
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData data) => Downs.Add(data);
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData data) => Clicks[(int)data.button]++;
    }

    public sealed class YLCoreNativeDragProbe : MonoBehaviour, UnityEngine.EventSystems.IDragHandler
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Drags")]
        public int drags;
        public void OnDrag(UnityEngine.EventSystems.PointerEventData data) => drags++;
    }

    public sealed class YLCoreParentChangeProbe : MonoBehaviour
    {
        public Action Changed;
        private void OnTransformParentChanged() => Changed?.Invoke();
    }

#if ENABLE_LEGACY_INPUT_MANAGER
    public sealed class YLCoreLegacyPointerInput : UnityEngine.EventSystems.BaseInput
    {
        private Vector2 _position;
        private readonly bool[] _pressed = new bool[3];
        private readonly int[] _downFrame = { -1, -1, -1 };
        private readonly int[] _upFrame = { -1, -1, -1 };
        public void Set(Vector2 position, bool left, bool right = false, bool middle = false)
        {
            this._position = position;
            var next = new[] { left, right, middle };
            for (int button = 0; button < 3; button++)
            {
                if (next[button] != _pressed[button])
                {
                    if (next[button])
                    {
                        _downFrame[button] = Time.frameCount + 1;
                    }
                    else
                    {
                        _upFrame[button] = Time.frameCount + 1;
                    }
                }
                _pressed[button] = next[button];
            }
        }
        public override bool mousePresent => true;
        public override Vector2 mousePosition => _position;
        public override bool touchSupported => false;
        public override bool GetMouseButton(int button) => _pressed[button];
        public override bool GetMouseButtonDown(int button) => Time.frameCount == _downFrame[button];
        public override bool GetMouseButtonUp(int button) => Time.frameCount == _upFrame[button];
        public override float GetAxisRaw(string axisName) => 0;
        public override bool GetButtonDown(string buttonName) => false;
    }
#endif

    public class CoreDirectViewPlayModeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<int> _prefabKeys = new List<int>();
        private readonly List<int> _instanceKeys = new List<int>();
        private readonly List<Scene> _scenes = new List<Scene>();
        private YLCoreDirectViewModule _module;
        private CoreClassPoolScope _classPool;
        private CoreUpdateHostScope _updateHosts;
        private readonly HashSet<GameObject> _priorEventSystems = new HashSet<GameObject>();
        private Action _restoreInputFocus;

        [SetUp]
        public void SetUp()
        {
            YLCoreSmokePanel.ResetProbes();
            YLCoreSmokeView.LoadingProbe = null;
            YLCoreSmokeView.ArgsProbe = null;
            YLCoreSmokeView.EnabledProbe = null;
            YLCoreSmokeView.DisabledProbe = null;
            YLCoreSmokeView.SharedEventHandler = null;
            YLCoreSmokeView.DuplicateProbeHandler = false;
            YLCoreSmokeView.SignatureConflictEvent = null;
            Assert.That(ModuleManager.AllModules, Is.Empty, "Use the independent test scene, not a running business module.");
            _classPool = new CoreClassPoolScope();
            _updateHosts = new CoreUpdateHostScope();
            _priorEventSystems.Clear();
            foreach (var system in Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                _priorEventSystems.Add(system.gameObject);
            }

            _module = new YLCoreDirectViewModule();
#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var settingsProperty = inputType.GetProperty("settings", BindingFlags.Public | BindingFlags.Static);
            var backgroundProperty = inputType.GetProperty("runInBackground", BindingFlags.Public | BindingFlags.Static);
            var previousSettings = (Object)settingsProperty.GetValue(null);
            var previousFlags = previousSettings.hideFlags;
            var previousBackground = backgroundProperty.GetValue(null);
            var settings = Object.Instantiate(previousSettings);
            _restoreInputFocus = () =>
            {
                settingsProperty.SetValue(null, previousSettings);
                previousSettings.hideFlags = previousFlags;
                backgroundProperty.SetValue(null, previousBackground);
                if (settings)
                {
                    Object.DestroyImmediate(settings);
                }
            };
            // batchmode 没有 Game View 焦点；仅为自建设备克隆测试设置，不修改工程的输入资产。
            var focus = settings.GetType().GetProperty("editorInputBehaviorInPlayMode");
            var background = settings.GetType().GetProperty("backgroundBehavior");
            focus.SetValue(settings, Enum.Parse(focus.PropertyType, "AllDeviceInputAlwaysGoesToGameView"));
            background.SetValue(settings, Enum.Parse(background.PropertyType, "IgnoreFocus"));
            // InputManager 会销毁精确 HideAndDontSave 的旧临时设置，先保留其身份再替换。
            previousSettings.hideFlags = previousFlags | HideFlags.HideInInspector;
            settingsProperty.SetValue(null, settings);
            backgroundProperty.SetValue(null, true);
#endif
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                if (ReferenceEquals(ModuleManager.GetModule<YLCoreDirectViewModule>(), _module))
                {
                    _module.Close();
                }
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
                YLCoreSmokeView.LoadingProbe = null;
                YLCoreSmokeView.ArgsProbe = null;
                YLCoreSmokeView.EnabledProbe = null;
                YLCoreSmokeView.DisabledProbe = null;
                YLCoreSmokeView.SharedEventHandler = null;
                YLCoreSmokeView.DuplicateProbeHandler = false;
                YLCoreSmokeView.SignatureConflictEvent = null;
                try
                {
                    _classPool?.Restore(new Module[] { _module });
                }
                finally
                {
                    try
                    {
                        ReleaseObjects();
                        _updateHosts?.ReleaseOwnedIdleHosts();
                    }
                    finally
                    {
                        var restoreInputFocus = _restoreInputFocus;
                        _restoreInputFocus = null;
                        restoreInputFocus?.Invoke();
                    }
                }
            }
            yield return null;
            foreach (var scene in _scenes)
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }

            _scenes.Clear();
            Assert.That(ModuleManager.GetModule<YLCoreDirectViewModule>(), Is.Null);
        }

        private void ReleaseObjects()
        {
            var policy = typeof(PanelManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var inputHost = policy == null ? null : (GameObject)typeof(PanelManager).GetField("_eventSystem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(policy);
            if (inputHost && !_priorEventSystems.Contains(inputHost))
            {
                _objects.Add(inputHost);
            }

            foreach (var obj in _objects)
            {
                if (obj)
                {
                    Object.Destroy(obj);
                }
            }

            var pool = Object.FindFirstObjectByType<YLFramework.Pooling.GameObjectPool>();
            if (pool)
            {
                foreach (var key in _prefabKeys)
                {
                    pool.Clear("YL.Core.Direct/" + key);
                }
            }

            _objects.Clear();
            _prefabKeys.Clear();
            _instanceKeys.Clear();
        }

        [UnityTest]
        public IEnumerator TypedInitializationKeepsLegalObjectValuesBorrowedAndReturnsTheContainerOnce()
        {
            yield return StartModule();
            var payload = new object();
            var args = LoadViewArgs.New();
            args.SetObject("payload", payload);
            args.SetObject("nullable", null);
            var prefab = Template<YLCoreTypedInitializationView>("YLView.TypedInitialization");
            var view = Track(_module.LoadView<YLCoreTypedInitializationView>(prefab, loadViewArgs: args));
            Assert.That(view.InitializeCalls, Is.EqualTo(1));
            Assert.That(view.Payload, Is.SameAs(payload));
            Assert.Throws<ObjectDisposedException>(() => args.GetObject("payload"));
            var next = LoadViewArgs.New();
            Assert.That(next, Is.SameAs(args));
            Assert.That(next.GetObject("payload"), Is.Null);
            Assert.That(next.GetObject("nullable"), Is.Null);
            var empty = Track(_module.LoadView<YLCoreTypedInitializationView>(prefab, loadViewArgs: next));
            Assert.That(empty.InitializeCalls, Is.EqualTo(1));
            Assert.That(empty.Payload, Is.Null);
            empty.Close();
            view.Close();
            Assert.That(view.Payload, Is.SameAs(payload), "Returning args does not dispose or claim a borrowed business value.");
        }

        [UnityTest]
        public IEnumerator RemovingAnUnobservedViewDoesNotCreateTheSharedEventContext()
        {
            yield return StartModule();
            var field = typeof(EventBus).GetField("_default", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = field.GetValue(null);
            field.SetValue(null, null);
            YLCoreUnobservedView view = null;
            try
            {
                var prefab = Template<YLCoreUnobservedView>("YLView.Unobserved");
                view = Track(_module.LoadView<YLCoreUnobservedView>(prefab));
                Assert.That(EventBus.TryGetDefault(out _), Is.False);
                view.Close();
                Assert.That(view.Module, Is.Null);
                Assert.That(EventBus.TryGetDefault(out _), Is.False,
                    "Publishing an unobserved ViewRemoved must not create Default merely to do nothing.");
            }
            finally
            {
                try
                {
                    if (view && view.Module != null)
                    {
                        view.Close();
                    }
                }
                finally
                {
                    EventBus.ResetDefault();
                    field.SetValue(null, previous);
                }
            }
        }

        [UnityTest]
        public IEnumerator PanelDefaultsRulesAndTemporaryOverridesDriveARealReopen()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPanel.RuleRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPanel.RulePrefab", true);
            var defaults = new PanelOverrides { Layer = 1, Priority = 9, Modal = true }.ApplyTo(PanelConfig.Default);
            SetPrivate(prefab.GetComponent<YLCoreSmokePanel>(), "settings", defaults);
            _module.BindPanelRules(new[] { new PanelRule(prefab, new PanelOverrides { Priority = 17, Modal = true,
                CoversRoot = true, ParticipatesInTop = false, InstanceMode = PanelInstanceMode.Single, CacheOnClose = false, Layer = 0 }) });
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root, overrides: new PanelOverrides { Priority = 0, Modal = false }));
            Assert.That(panel.EffectiveSettings.priority, Is.Zero);
            Assert.That(panel.EffectiveSettings.modal, Is.False);
            Assert.That(panel.EffectiveSettings.coversRoot, Is.True);
            Assert.That(panel.EffectiveSettings.participatesInTop, Is.False);
            Assert.That(panel.EffectiveSettings.cacheOnClose, Is.False);
            Assert.That(panel.EffectiveSettings.instanceMode, Is.EqualTo(PanelInstanceMode.Single));
            Assert.That(panel.EffectiveSettings.layer, Is.Zero);
            Assert.That(panel.Root, Is.SameAs(root));
            Assert.That(panel.transform.parent, Is.SameAs(root.GetLayer(0)));
            Assert.That(panel.LoadedCount, Is.EqualTo(1));
            Assert.That(panel.ArgsCount, Is.EqualTo(1));
            Assert.That(panel.OpenCount, Is.EqualTo(1));
            int initial = panel.LoadedCount;
            Assert.That(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root), Is.SameAs(panel));
            Assert.That(panel.LoadedCount, Is.EqualTo(initial));
            Assert.That(panel.ArgsCount, Is.EqualTo(1));
            Assert.That(panel.OpenCount, Is.EqualTo(2));
            Assert.That(panel.EffectiveSettings.priority, Is.EqualTo(17), "临时覆盖不能成为重开默认。");
            Assert.That(panel.EffectiveSettings.modal, Is.True);
            Assert.Throws<ArgumentException>(() => _module.BindPanelRules(new[] { new PanelRule(prefab, default), new PanelRule(prefab, default) }));
            IEnumerable<PanelRule> BrokenRules()
            {
                yield return new PanelRule(prefab, new PanelOverrides { Priority = 99 });
                throw new InvalidOperationException("rule enumeration failed");
            }
            Assert.Throws<InvalidOperationException>(() => _module.BindPanelRules(BrokenRules()));
            Assert.That(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root).EffectiveSettings.priority, Is.EqualTo(17));
            var pending = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, root: root);
            _module.BindPanelRules(new[] { new PanelRule(prefab, new PanelOverrides { Priority = 34 }) });
            Assert.That(panel.EffectiveSettings.priority, Is.EqualTo(17), "重绑不能刷新现有会话。");
            yield return pending;
            Assert.That(pending.Status, Is.EqualTo(LoadViewStatus.Succeeded));
            Assert.That(pending.Panel, Is.SameAs(panel));
            Assert.That(panel.EffectiveSettings.priority, Is.EqualTo(17), "在途请求固定接受时规则快照。");
            Assert.That(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root).EffectiveSettings.priority, Is.EqualTo(34));
            _module.BindPanelRules(null);
            Assert.That(panel.EffectiveSettings.priority, Is.EqualTo(34));
            Assert.That(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root).EffectiveSettings, Is.EqualTo(defaults));
            Assert.That(panel.transform.parent, Is.SameAs(root.GetLayer(1)));
            Assert.That(prefab.GetComponent<YLCoreSmokePanel>().EffectiveSettings, Is.EqualTo(default(PanelConfig)), "源 Prefab 不得被写成会话配置。");
        }

        [UnityTest]
        public IEnumerator PanelAsyncConsumersKeepOriginalSessionAcrossCompletionReuse()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPanel.ConsumerRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPanel.ConsumerPrefab", true);
            var firstArgs = NewArgs();
            firstArgs.SetInt("token", 11);
            var latestArgs = NewArgs();
            latestArgs.SetInt("token", 22);
            var a = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, firstArgs, root);
            var b = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, latestArgs, root);
            Assert.That(a.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.That(b.Status, Is.EqualTo(LoadViewStatus.Pending));
            YLCoreSmokePanel replacement = null;
            object originalSession = null;
            a.AddCompleteEvent(done =>
            {
                Assert.That(done.Panel.Token, Is.EqualTo(22), "合流批次使用最新有效参数。");
                Assert.That(done.Panel.OpenCount, Is.EqualTo(1));
                originalSession = ViewSession(done.Panel);
                AssertArgsReleased(firstArgs);
                AssertArgsReleased(latestArgs);
                Track(done.Panel).Close();
                replacement = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            });
            yield return a;
            yield return b;
            Assert.That(a.Status, Is.EqualTo(LoadViewStatus.Succeeded));
            Assert.That(replacement, Is.SameAs(a.Panel));
            Assert.That(ViewSession(replacement), Is.Not.SameAs(originalSession));
            Assert.That(b.Status, Is.EqualTo(LoadViewStatus.Invalidated), "不能把同一 GameObject 的新 Session 交给旧消费者。");
        }

        [UnityTest]
        public IEnumerator OnOpenRejectsDisplayCommandsButEnabledAdjustmentsSurvive()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPanel.CallbackRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPanel.CallbackPrefab", true);
            var input = NewArgs();
            input.SetInt("token", 73);
            var calls = new List<string>();
            YLCoreSmokePanel.LoadingProbe = panel =>
            {
                Assert.That(panel.IsLoaded, Is.False);
                Assert.That(panel.Root, Is.SameAs(root));
                Assert.That(panel.Canvas, Is.Not.Null);
                panel.UpdateDisplay(new PanelOverrides { Layer = 1, Priority = 7 });
                panel.Hide();
                EventBus.Default.Send(YLCoreSmokePanel.ProbeEvent);
                Assert.That(panel.EventCount, Is.Zero);
                calls.Add("loaded");
            };
            YLCoreSmokePanel.ArgsProbe = (_, args) =>
            {
                Assert.That(args, Is.SameAs(input));
                Assert.That(args.GetInt("token"), Is.EqualTo(73));
                calls.Add("args");
            };
            YLCoreSmokePanel.UpdateProbe = (panel, args) =>
            {
                Assert.That(panel.IsLoaded, Is.True);
                Assert.That(args, Is.SameAs(input));
                Assert.That(args.GetInt("token"), Is.EqualTo(73));
                Assert.Throws<InvalidOperationException>(panel.Hide);
                Assert.Throws<InvalidOperationException>(panel.Show);
                Assert.Throws<InvalidOperationException>(panel.BringToFront);
                Assert.Throws<InvalidOperationException>(() => panel.UpdateDisplay(default));
                Assert.Throws<InvalidOperationException>(panel.Close);
                Assert.Throws<InvalidOperationException>(_module.Close);
                Assert.Throws<InvalidOperationException>(ModuleManager.Shutdown);
                Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
                calls.Add("open");
            };
            YLCoreSmokePanel.EnabledProbe = panel =>
            {
                Assert.Throws<InvalidOperationException>(() => input.GetInt("token"));
                Assert.Throws<InvalidOperationException>(panel.Close);
                Assert.Throws<InvalidOperationException>(_module.Close);
                panel.UpdateDisplay(new PanelOverrides { Priority = 23 });
                panel.BringToFront();
                panel.Hide();
                calls.Add("enabled");
            };
            var result = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, input, root));
            Assert.That(calls, Is.EqualTo(new[] { "loaded", "args", "open", "enabled" }));
            AssertArgsReleased(input);
            Assert.That(result.IsLoaded, Is.True);
            Assert.That(result.IsVisible, Is.False);
            Assert.That(result.EffectiveSettings.priority, Is.EqualTo(23));
            Assert.That(result.EffectiveSettings.layer, Is.EqualTo(1));
            Assert.That(result.transform.parent, Is.SameAs(root.GetLayer(1)));
            Assert.Throws<ArgumentException>(() => result.UpdateDisplay(new PanelOverrides { InstanceMode = PanelInstanceMode.Single }));
            Assert.Throws<ArgumentException>(() => result.UpdateDisplay(new PanelOverrides { CacheOnClose = true }));
            result.Show();
            Assert.That(result.IsVisible, Is.True);
            Assert.That(calls, Has.Count.EqualTo(4), "显示调整不能重放数据或启用轮次。");
            EventBus.Default.Send(YLCoreSmokePanel.ProbeEvent);
            Assert.That(result.EventCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ReopeningOnOpenRejectsEveryClearViewBeforeRemovingAnySession()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPanel.OpenClearRoot");
            var sibling = Track(_module.LoadView<YLCoreSmokeView>(Template<YLCoreSmokeView>("YLPanel.ClearSibling", true), root.GetLayer(0), loadViewArgs: null));
            var source = Template<YLCoreSmokePanel>("YLPanel.ClearCurrent", true);
            var current = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
            object session = ViewSession(current);
            int removed = current.RemovedCount, closed = current.ClosedCount;
            YLCoreSmokePanel.UpdateProbe = (panel, args) =>
            {
                Assert.Throws<InvalidOperationException>(() => _module.ClearView<YLCoreSmokePanel>());
                Assert.Throws<InvalidOperationException>(() => _module.ClearView(source.name));
                Assert.Throws<InvalidOperationException>(_module.ClearView);
                Assert.That(ViewSession(panel), Is.SameAs(session));
                Assert.That(panel.IsLoaded, Is.True);
                Assert.That(panel.RemovedCount, Is.EqualTo(removed));
                Assert.That(panel.ClosedCount, Is.EqualTo(closed));
                Assert.That(sibling.Module, Is.SameAs(_module), "批量移除须在任何退出副作用前拒绝。");
            };
            Assert.That(_module.LoadPanel<YLCoreSmokePanel>(source, root: root), Is.SameAs(current));
        }

        [UnityTest]
        public IEnumerator ReopeningOnOpenRejectsRetirementOfItsBackendAncestor()
        {
            yield return StartModule();
            var source = Template<YLCoreSmokeView>("YLPanel.BackendAncestor", true);
            var canvas = new GameObject("NestedRoot", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
            canvas.SetActive(false);
            canvas.transform.SetParent(source.transform, false);
            CorePanelTestRoots.Create(canvas);
            var parent = Track(_module.LoadView<YLCoreSmokeView>(source, loadViewArgs: null));
            var root = parent.GetComponentInChildren<UIRootContext>(true);
            var panelSource = Template<YLCoreSmokePanel>("YLPanel.NestedOpen", true);
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(panelSource, root: root));
            var parentLease = (CoreInstanceLease)typeof(View).GetField("BackendLease", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parent);
            Assert.That(typeof(View).GetField("BackendRoot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel), Is.SameAs(parent));
            YLCoreSmokePanel.UpdateProbe = (current, args) =>
            {
                Assert.Throws<InvalidOperationException>(parent.Close);
                Assert.Throws<InvalidOperationException>(() => _module.ClearView(source.name));
                Assert.That(parent.Module, Is.SameAs(_module));
                Assert.That(parentLease.IsValid, Is.True);
                Assert.That(current.RemovedCount, Is.Zero);
            };
            Assert.That(_module.LoadPanel<YLCoreSmokePanel>(panelSource, root: root), Is.SameAs(panel));
            Assert.That(parentLease.IsValid && panel.IsLoaded, Is.True);
        }

        [UnityTest]
        public IEnumerator LayerUpdateCannotWriteOldSettingsIntoAReusedObjectAtAnotherRoot() => ChangeLayerAcrossNativeReuse(false);
        [UnityTest]
        public IEnumerator LayerReopenCannotCommitOldIntentIntoAReusedObjectAtAnotherRoot() => ChangeLayerAcrossNativeReuse(true);

        private IEnumerator ChangeLayerAcrossNativeReuse(bool reopen)
        {
            yield return StartModule();
            var root = PolicyRoot("YLPanel.ParentCallbackRoot");
            var targetHost = new GameObject("YLPanel.ParentCallbackTarget", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
            targetHost.SetActive(false);
            _objects.Add(targetHost);
            var target = CorePanelTestRoots.Create(targetHost, 1);
            var prefab = Template<YLCoreSmokePanel>("YLPanel.ParentCallbackPanel", true);
            prefab.AddComponent<YLCoreParentChangeProbe>();
            var original = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            object firstSession = ViewSession(original);
            YLCoreSmokePanel replacement = null;
            original.ClosedAction = () =>
            {
                original.ClosedAction = null;
                replacement = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: target,
                    overrides: new PanelOverrides { Layer = 0, Priority = 41, Modal = true }));
            };
            var probe = original.GetComponent<YLCoreParentChangeProbe>();
            probe.Changed = () =>
            {
                probe.Changed = null;
                original.Close();
            };
            var changes = new PanelOverrides { Layer = 1, Priority = 999, Modal = false };
            if (reopen)
            {
                Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab, root: root, overrides: changes));
            }
            else
            {
                Assert.DoesNotThrow(() => original.UpdateDisplay(changes));
            }

            Assert.That(replacement, Is.SameAs(original), "必须真正覆盖同一 GameObject 的新 Session。");
            Assert.That(ViewSession(replacement), Is.Not.SameAs(firstSession));
            Assert.That(replacement.Root, Is.SameAs(target));
            Assert.That(replacement.transform.parent, Is.SameAs(target.GetLayer(0)));
            Assert.That(replacement.EffectiveSettings.layer, Is.Zero);
            Assert.That(replacement.EffectiveSettings.priority, Is.EqualTo(41));
            Assert.That(replacement.EffectiveSettings.modal, Is.True);
            Assert.That(replacement.IsLoaded, Is.True);
        }

        [UnityTest]
        public IEnumerator ViewQueriesAndClearStayLocalAndDoNotBorrowBaseModuleViews()
        {
            yield return StartModule();
            var baseModule = new YLCoreBaseProbeModule();
            try
            {
                var request = ModuleManager.StartUpModule(baseModule);
                var deadline = Time.realtimeSinceStartup + 10;
                while (!request.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Error, Is.Null.Or.Empty);
                var model = baseModule.LoadModel<YLCoreProbeModel>();
                var controller = baseModule.LoadController<YLCoreProbeController>();
                var prefab = Template<YLCoreSmokeView>("YLCoreBaseLookupPrefab");
                var view = Track(baseModule.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));

                Assert.That(_module.GetModel<YLCoreProbeModel>(), Is.Null);
                Assert.That(_module.GetController<YLCoreProbeController>(), Is.Null);
                Assert.That(_module.GetView<YLCoreSmokeView>(prefab.name, view.Id), Is.Null);
                Assert.That(_module.GetView<YLCoreSmokeView>(view.Id), Is.Null);
                Assert.That(_module.GetView(prefab.name), Is.Null);
                Assert.That(_module.GetView<YLCoreSmokeView>(prefab.name), Is.Null);
                Assert.That(_module.GetView<YLCoreSmokeView>(), Is.Null);
                Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
                Assert.That(_module.GetViews(prefab.name), Is.Empty);
                Assert.That(_module.GetViews<YLCoreSmokeView>(prefab.name), Is.Empty);
                var results = new List<View> { view };
                _module.GetViewsNonAlloc(results);
                Assert.That(results, Is.Empty);
                Assert.That(view.Module, Is.SameAs(baseModule));
                var local = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                Assert.That(_module.GetView<YLCoreSmokeView>(), Is.SameAs(local));
                _module.ClearView(prefab.name);
                Assert.That(local.Module, Is.Null);
                local = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                _module.ClearView<View>();
                Assert.That(local.Module, Is.Null);
                _module.ClearView();
                _module.Close();
                Assert.That(view.gameObject.activeSelf, Is.True);
                Assert.That(view.RemovedCount, Is.Zero);
                Assert.That(model.ExitCount, Is.Zero);
                Assert.That(controller.ExitCount, Is.Zero);
            }
            finally
            {
                baseModule.AllowClose = true;
                try
                {
                    if (ReferenceEquals(ModuleManager.GetModule(baseModule.ModuleName), baseModule))
                    {
                        baseModule.Close();
                    }
                }
                finally
                {
                    _classPool.Restore(new Module[] { baseModule });
                }
            }
        }

        [UnityTest]
        public IEnumerator ViewCloseUnbindsBorrowedAndLocalModelsButOnlyExplicitRemovalExitsThem()
        {
            yield return StartModule();
            var owner = new YLCoreLifecycleModule();
            try
            {
                var request = ModuleManager.StartUpModule(owner);
                var deadline = Time.realtimeSinceStartup + 10;
                while (!request.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Error, Is.Null.Or.Empty);
                var shared = new YLCoreProbeModel { Id = 41 };
                var local = new YLCoreProbeModel { Id = 41 };
                owner.AddModel(shared);
                _module.AddModel(local);
                var prefab = Template<YLCoreSmokeView>("YLCoreModelRemovalPrefab");
                var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                view.BoundModel = shared;
                view.Close();
                Assert.That(view.Model, Is.Null);
                Assert.That(view.UnboundCount, Is.EqualTo(1));
                Assert.That(shared.Module, Is.SameAs(owner));
                Assert.That(shared.ExitCount, Is.Zero);
                Assert.That(_module.GetModel<YLCoreProbeModel>(41), Is.SameAs(local));
                view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                Assert.Throws<InvalidOperationException>(() => view.BoundModel = new YLCoreProbeModel { Id = 41 });
                Assert.That(view.Model, Is.Null);
                view.Close();
                Assert.That(local.ExitCount, Is.Zero);
                Assert.That(_module.GetModel<YLCoreProbeModel>(41), Is.SameAs(local));
                view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                view.BoundModel = local;
                view.Close();
                Assert.That(local.Module, Is.SameAs(_module));
                Assert.That(local.ExitCount, Is.Zero);
                Assert.That(_module.GetModel<YLCoreProbeModel>(41), Is.SameAs(local));
                _module.RemoveModel(local);
                _module.RemoveModel(local);
                Assert.That(local.Module, Is.Null);
                Assert.That(local.ExitCount, Is.EqualTo(1));
                view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                Assert.Throws<InvalidOperationException>(() => view.BoundModel = local);
                view.Close();
                Assert.That(local.ExitCount, Is.EqualTo(1));
                Assert.That(shared.ExitCount, Is.Zero);
            }
            finally
            {
                try
                {
                    if (ReferenceEquals(ModuleManager.GetModule(owner.ModuleName), owner))
                    {
                        owner.Close();
                    }
                }
                finally
                {
                    _classPool.Restore(new Module[] { owner });
                }
            }
        }

        [UnityTest]
        public IEnumerator RegistrationRequiresExactRegisteredOwnerAndAllowsStartingOwner()
        {
            Assert.That(typeof(View).GetMethod(nameof(View.Close)).IsVirtual, Is.False);
            var modelProperty = typeof(View).GetProperty(nameof(View.Model));
            Assert.That(modelProperty.GetMethod.IsVirtual, Is.False);
            Assert.That(modelProperty.SetMethod, Is.Null);
            var prefab = Template<YLCoreSmokeView>("YLCoreOwnerStatePrefab");
            var unattached = prefab.GetComponent<YLCoreSmokeView>();
            typeof(View).GetProperty("AssetName", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(unattached, prefab.name);
            Assert.Throws<InvalidOperationException>(() => _module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            Assert.Throws<InvalidOperationException>(() => InvokeViewManager("AddView", _module, unattached));
            Assert.Throws<InvalidOperationException>(() => unattached.BoundModel = new YLCoreProbeModel());
            unattached.Close();
            Assert.That(unattached.RemovedCount, Is.Zero);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            YLCoreSmokeView loaded = null;
            _module.StartAction = () =>
            {
                Assert.That(_module.State, Is.EqualTo(ModuleState.Starting));
                Assert.That(ModuleManager.GetModule(_module.ModuleName), Is.SameAs(_module));
                loaded = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            };
            yield return StartModule();
            Assert.That(loaded.IsLoaded, Is.True);
            var unregistered = new YLCoreDirectViewModule();
            try
            {
                Assert.Throws<InvalidOperationException>(() => unregistered.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                Assert.Throws<InvalidOperationException>(() => InvokeViewManager("AddView", unregistered, loaded));
                Assert.That(loaded.Module, Is.SameAs(_module));
                Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { loaded }));
                Assert.That(unregistered.GetViews<View>(), Is.Empty);
            }
            finally
            {
                _classPool.Restore(new Module[] { unregistered });
            }
        }

        [UnityTest]
        public IEnumerator DuplicateRegistrationIsIdempotentAndForeignOwnerCannotAddOrRemoveView()
        {
            yield return StartModule();
            var other = new YLCoreConsumerModule();
            try
            {
                var request = ModuleManager.StartUpModule(other);
                var deadline = Time.realtimeSinceStartup + 10;
                while (!request.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Error, Is.Null.Or.Empty);
                var prefab = Template<YLCoreSmokeView>("YLCoreExactOwnerPrefab");
                var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                var model = _module.LoadModel<YLCoreProbeModel>();
                view.BoundModel = model;
                var session = ViewSession(view);
                InvokeViewManager("AddView", _module, view);
                InvokeViewManager("AddView", _module, view);
                Assert.That(ViewSession(view), Is.SameAs(session));
                Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { view }));
                Assert.That(_module.GetViews(prefab.name), Is.EqualTo(new View[] { view }));
                Assert.That(view.LoadedCount, Is.EqualTo(1));
                Assert.That(view.Model, Is.SameAs(model));
                Assert.Throws<InvalidOperationException>(() => InvokeViewManager("AddView", other, view));
                InvokeViewManager("RemoveView", other, view);
                Assert.That(other.GetViews<View>(), Is.Empty);
                Assert.That(view.Module, Is.SameAs(_module));
                Assert.That(view.RemovedCount, Is.Zero);
                Assert.That(view.gameObject.activeSelf, Is.True);

                var assetName = typeof(View).GetProperty("AssetName", BindingFlags.Instance | BindingFlags.NonPublic);
                assetName.SetValue(view, prefab.name + "Conflict");
                try
                {
                    Assert.Throws<InvalidOperationException>(() => InvokeViewManager("AddView", _module, view));
                    Assert.That(_module.GetView(prefab.name, view.Id), Is.SameAs(view));
                    Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { view }));
                    Assert.That(_module.GetViews(prefab.name + "Conflict"), Is.Empty);
                }
                finally
                {
                    assetName.SetValue(view, prefab.name);
                }

                InvokeViewManager("RemoveView", _module, view);
                InvokeViewManager("RemoveView", _module, view);
                Assert.That(_module.GetViews<View>(), Is.Empty);
                Assert.That(view.Module, Is.Null);
                Assert.That(view.Model, Is.Null);
                Assert.That(view.RemovedCount, Is.EqualTo(1));
                Assert.That(view.UnboundCount, Is.EqualTo(1));
                Assert.That(view.ClosedCount, Is.EqualTo(1));
                Assert.That(model.ExitCount, Is.Zero);
            }
            finally
            {
                try
                {
                    other.Close();
                }
                finally
                {
                    _classPool.Restore(new Module[] { other });
                }
            }
        }

        [UnityTest]
        public IEnumerator SharedModelBindingsDoNotOwnModelAndOldRegistrationCannotRevive()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            var prefab = Template<YLCoreSmokeView>("YLCoreSharedBindingPrefab");
            var first = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            var second = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            first.BoundModel = model;
            first.BoundModel = model;
            second.BoundModel = model;
            first.Close();
            Assert.That(first.UnboundCount, Is.EqualTo(1));
            Assert.That(first.Model, Is.Null);
            Assert.That(second.Model, Is.SameAs(model));
            Assert.That(model.ExitCount, Is.Zero);
            first = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            first.BoundModel = model;
            _module.RemoveModel(model);
            Assert.That(first.Model, Is.Null);
            Assert.That(second.Model, Is.Null);
            Assert.That(model.ExitCount, Is.EqualTo(1));
            _module.AddModel(model);
            Assert.That(model.InitCount, Is.EqualTo(2));
            Assert.That(first.Model, Is.Null, "The same Model instance has a new registration token.");
            Assert.That(second.Model, Is.Null);
            first.BoundModel = null;
            first.BoundModel = model;
            Assert.That(first.Model, Is.SameAs(model));
            Assert.That(second.Model, Is.Null);
            second.Close();
            Assert.That(second.UnboundCount, Is.EqualTo(1));
            Assert.That(first.Model, Is.SameAs(model));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            first.Close();
            Assert.That(model.Module, Is.SameAs(_module));
            Assert.That(model.ExitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExplicitUnbindFailureClearsSlotPreservesModelAndMarksInstanceForDiscard()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            var prefab = Template<YLCoreSmokeView>("YLCoreExplicitUnbindPrefab");
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            view.BoundModel = model;
            var failure = new InvalidOperationException("explicit unbind");
            view.UnboundAction = previous =>
            {
                Assert.That(previous, Is.SameAs(model));
                Assert.That(view.Model, Is.Null);
                Assert.Throws<InvalidOperationException>(() => view.BoundModel = model);
                Assert.Throws<InvalidOperationException>(() => view.Close());
                Assert.Throws<InvalidOperationException>(() => InvokeViewManager("AddView", _module, view));
                Assert.Throws<InvalidOperationException>(() => _module.Close());
                view.BoundModel = null;
                throw failure;
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => view.BoundModel = null), Is.SameAs(failure));
            Assert.That(view.UnboundCount, Is.EqualTo(1));
            Assert.That(view.Model, Is.Null);
            Assert.That(view.Module, Is.SameAs(_module));
            Assert.That(view.IsLoaded, Is.True);
            Assert.That(view.gameObject.activeSelf, Is.True);
            Assert.That(model.ExitCount, Is.Zero);
            Assert.That(_module.GetModel<YLCoreProbeModel>(model.Id), Is.SameAs(model));
            view.UnboundAction = null;
            view.BoundModel = model;
            view.Close();
            Assert.That(view.UnboundCount, Is.EqualTo(2));
            Assert.That(model.ExitCount, Is.Zero);
            yield return null;
            Assert.That(view == null, Is.True, "A caught explicit unbind failure still contaminates the pooled instance.");
            var fresh = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            Assert.That(fresh, Is.Not.SameAs(view));
            Assert.That(fresh.LoadedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CloseIsReentrantAndEveryObserverRunsBeforeNecessaryCleanupAndCompletion()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            var prefab = Template<YLCoreSmokeView>("YLCoreExitObserversPrefab");
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            view.BoundModel = model;
            var events = new List<string>();
            var firstError = new InvalidOperationException("first observer");
            var lastError = new ArgumentException("last observer");
            var broadcastError = new InvalidOperationException("first broadcast observer");
            view.ViewRemoved = () =>
            {
                events.Add("first");
                throw firstError;
            };
            view.ViewRemoved += () =>
            {
                events.Add("reentrant");
                Assert.That(view.IsLoaded, Is.False);
                view.Close();
                Assert.Throws<InvalidOperationException>(() => InvokeViewManager("AddView", _module, view));
                Assert.Throws<InvalidOperationException>(() => view.BoundModel = model);
            };
            view.ViewRemoved += () =>
            {
                events.Add("last");
                throw lastError;
            };
            view.RemoveAction = () =>
            {
                events.Add("remove");
                view.Close();
            };
            view.UnboundAction = previous =>
            {
                events.Add("unbind");
                Assert.That(previous, Is.SameAs(model));
                Assert.That(view.Model, Is.Null);
                int before = view.EventCount;
                EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                Assert.That(view.EventCount, Is.EqualTo(before), "Framework subscriptions end before the unbind hook.");
            };
            Action<View> broadcast = removed =>
            {
                if (!ReferenceEquals(removed, view))
                {
                    return;
                }

                events.Add("broadcast");
                Assert.That(view.Module, Is.Null);
                Assert.That(view.Model, Is.Null);
                Assert.That(view.gameObject.activeSelf, Is.False);
                Assert.That(_module.GetViews<View>(), Is.Empty);
            };
            view.AfterClose = () =>
            {
                events.Add("closed");
                Assert.That(view.Module, Is.Null);
                Assert.That(view.gameObject.activeSelf, Is.False);
                view.Close();
            };
            var firstBroadcastToken = EventBus.Default.Register<View>(CoreEvents.ViewRemoved, removed =>
            {
                if (!ReferenceEquals(removed, view))
                {
                    return;
                }

                events.Add("broadcast.throw");
                throw broadcastError;
            });
            var broadcastToken = EventBus.Default.Register(CoreEvents.ViewRemoved, broadcast);
            try
            {
                var error = Assert.Throws<AggregateException>(() => view.Close());
                Assert.That(error.InnerExceptions, Is.EqualTo(new Exception[] { firstError, lastError, broadcastError }));
                Assert.That(events, Is.EqualTo(new[] { "first", "reentrant", "last", "remove", "unbind", "broadcast.throw", "broadcast", "closed" }));
                Assert.That(view.RemovedCount, Is.EqualTo(1));
                Assert.That(view.UnboundCount, Is.EqualTo(1));
                Assert.That(view.ClosedCount, Is.EqualTo(1));
                Assert.That(model.Module, Is.SameAs(_module));
                Assert.That(model.ExitCount, Is.Zero);
                view.Close();
                Assert.That(events, Has.Count.EqualTo(8));
            }
            finally
            {
                firstBroadcastToken.Unregister();
                broadcastToken.Unregister();
                view.ViewRemoved = null;
                view.RemoveAction = null;
                view.UnboundAction = null;
                view.AfterClose = null;
            }
            Assert.That(Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null)), Is.SameAs(view), "Observer errors do not contaminate an otherwise healthy pooled instance.");
            var completionError = new InvalidOperationException("completion observer");
            view.AfterClose = () => throw completionError;
            try
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => view.Close()), Is.SameAs(completionError));
                Assert.That(view.ClosedCount, Is.EqualTo(2));
                Assert.That(view.Module, Is.Null);
                Assert.That(view.gameObject.activeSelf, Is.False);
            }
            finally
            {
                view.AfterClose = null;
            }
            Assert.That(Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null)), Is.SameAs(view));
        }

        [UnityTest]
        public IEnumerator GlobalRemovalObserverReplacementSessionDoesNotReceiveOldCompletionHook()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            for (int phase = 0; phase < 2; phase++)
            {
                var prefab = Template<YLCoreSmokeView>("YLCoreBroadcastReplacementPrefab" + phase);
                var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                view.BoundModel = model;
                var oldSession = ViewSession(view);
                YLCoreSmokeView replacement = null;
                var completed = 0;
                view.AfterClose = () =>
                {
                    completed++;
                    view.BoundModel = null;
                    view.Close();
                };
                Action<View> broadcast = removed =>
                {
                    if (!ReferenceEquals(removed, view) || !ReferenceEquals(replacement, null))
                    {
                        return;
                    }

                    replacement = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                    replacement.BoundModel = model;
                    Assert.That(ViewSession(replacement), Is.Not.SameAs(oldSession));
                    if (phase == 1)
                    {
                        replacement.Close();
                    }
                };
                EventBus.Default.Register(CoreEvents.ViewRemoved, broadcast);
                try
                {
                    view.Close();
                    Assert.That(replacement, Is.SameAs(view));
                    Assert.That(completed, Is.EqualTo(phase), "Only a newly closed replacement session may invoke its own completion hook.");
                    Assert.That(view.ClosedCount, Is.EqualTo(phase));
                    Assert.That(view.RemovedCount, Is.EqualTo(phase + 1));
                    Assert.That(view.IsLoaded, Is.EqualTo(phase == 0));
                    if (phase == 0)
                    {
                        Assert.That(view.Model, Is.SameAs(model));
                        Assert.That(view.Module, Is.SameAs(_module));
                        Assert.That(_module.GetView<YLCoreSmokeView>(view.Id), Is.SameAs(view));
                        Assert.That(view.gameObject.activeSelf, Is.True);
                    }
                    else
                    {
                        Assert.That(view.Model, Is.Null);
                        Assert.That(view.Module, Is.Null);
                        Assert.That(_module.GetViews<View>(), Is.Empty);
                    }
                    Assert.That(model.ExitCount, Is.Zero);
                }
                finally
                {
                    EventBus.Default.Unregister(CoreEvents.ViewRemoved, broadcast);
                    view.AfterClose = null;
                    view.Close();
                }
            }
        }

        [UnityTest]
        public IEnumerator NecessaryCleanupFailuresFinishExitButDestroyInstanceRatherThanPoolIt()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            for (int phase = 0; phase < 3; phase++)
            {
                var prefab = Template<YLCoreSmokeView>("YLCoreNecessaryFailurePrefab" + phase);
                var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                view.BoundModel = model;
                var failure = new InvalidOperationException(phase == 0 ? "OnRemove failure" : "unbind failure");
                if (phase == 0)
                {
                    view.RemoveAction = () => throw failure;
                }

                if (phase == 2)
                {
                    view.ViewRemoved = () => view.BoundModel = null;
                }

                view.UnboundAction = previous =>
                {
                    Assert.That(view.Model, Is.Null);
                    Assert.That(previous, Is.SameAs(model));
                    if (phase != 0)
                    {
                        throw failure;
                    }
                };
                var closed = 0;
                view.AfterClose = () =>
                {
                    closed++;
                    Assert.That(view.Module, Is.Null);
                    Assert.That(view.Model, Is.Null);
                    Assert.That(view.gameObject.activeSelf, Is.False);
                };
                Assert.That(Assert.Throws<InvalidOperationException>(() => view.Close()), Is.SameAs(failure));
                Assert.That(view.RemovedCount, Is.EqualTo(1));
                Assert.That(view.UnboundCount, Is.EqualTo(1));
                Assert.That(closed, Is.EqualTo(1));
                Assert.That(_module.GetViews<View>(), Is.Empty);
                Assert.That(model.Module, Is.SameAs(_module));
                Assert.That(model.ExitCount, Is.Zero);
                view.Close();
                yield return null;
                Assert.That(view == null, Is.True, "Necessary cleanup failure must never re-enter the backend's reusable pool.");
                var fresh = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                Assert.That(fresh, Is.Not.SameAs(view));
                Assert.That(fresh.LoadedCount, Is.EqualTo(1));
                fresh.Close();
            }
        }

        [UnityTest]
        public IEnumerator StaleSessionRemovalCannotCloseReusedViewSession()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreStaleSessionPrefab");
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            var previousSession = ViewSession(view);
            var id = view.Id;
            view.Close();
            var reused = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            Assert.That(reused, Is.SameAs(view));
            Assert.That(reused.Id, Is.EqualTo(id));
            Assert.That(ViewSession(reused), Is.Not.SameAs(previousSession));
            var model = _module.LoadModel<YLCoreProbeModel>();
            reused.BoundModel = model;
            InvokeViewManager("RemoveView", _module, reused, previousSession, false);
            Assert.That(reused.Module, Is.SameAs(_module));
            Assert.That(reused.Model, Is.SameAs(model));
            Assert.That(reused.IsLoaded, Is.True);
            Assert.That(reused.gameObject.activeSelf, Is.True);
            Assert.That(_module.GetView<YLCoreSmokeView>(id), Is.SameAs(reused));
            Assert.That(reused.RemovedCount, Is.EqualTo(1));
            Assert.That(reused.ClosedCount, Is.EqualTo(1));
            InvokeViewManager("RemoveView", _module, reused);
            Assert.That(reused.RemovedCount, Is.EqualTo(2));
            Assert.That(reused.Model, Is.Null);
            Assert.That(model.ExitCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyedViewsEndBindingsAndUnactivatedWrappersArePrunedBySavedIdentity()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            var prefab = Template<YLCoreSmokeView>("YLCoreDestroyedViewPrefab");
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            view.BoundModel = model;
            var id = view.Id;
            Object.Destroy(view.gameObject);
            yield return null;
            Assert.That(view == null, Is.True);
            Assert.That(view.Module, Is.Null);
            Assert.That(view.Model, Is.Null);
            Assert.That(view.RemovedCount, Is.EqualTo(1));
            Assert.That(view.UnboundCount, Is.EqualTo(1));
            Assert.That(_module.GetView<View>(id), Is.Null);
            Assert.That(_module.GetViews(prefab.name), Is.Empty);
            Assert.That(model.Module, Is.SameAs(_module));
            Assert.That(model.ExitCount, Is.Zero);

            // 从未激活的对象不保证收到OnDestroy，索引扫描和查询均依靠保存身份收尾。
            var inactive = Template<YLCoreSmokeView>("YLCoreUnactivatedView").GetComponent<YLCoreSmokeView>();
            typeof(View).GetProperty("AssetName", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(inactive, "YLCoreUnactivatedView");
            InvokeViewManager("AddView", _module, inactive);
            id = inactive.Id;
            Object.Destroy(inactive.gameObject);
            yield return null;
            yield return null;
            Assert.That(inactive == null, Is.True);
            Assert.That(ViewSession(inactive), Is.Null, "Destruction cleanup does not require a business query.");
            Assert.That(_module.GetView<View>(id), Is.Null);
            Assert.That(inactive.Module, Is.Null);
            Assert.That(ViewSession(inactive), Is.Null);
            Assert.That(inactive.RemovedCount, Is.EqualTo(1));
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(_module.GetViews("YLCoreUnactivatedView"), Is.Empty);
        }

        [UnityTest]
        public IEnumerator AllClearOverloadsContinueAfterSiblingFailureAndPreserveModelRegistration()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            for (int phase = 0; phase < 3; phase++)
            {
                var prefab = Template<YLCoreSmokeView>("YLCoreClearFailurePrefab" + phase);
                var first = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                var sibling = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                first.BoundModel = model;
                sibling.BoundModel = model;
                var failure = new InvalidOperationException("clear observer " + phase);
                first.ViewRemoved = () => throw failure;
                try
                {
                    var error = Assert.Throws<InvalidOperationException>(() =>
                    {
                        if (phase == 0)
                        {
                            _module.ClearView();
                        }
                        else if (phase == 1)
                        {
                            _module.ClearView<View>();
                        }
                        else
                        {
                            _module.ClearView(prefab.name);
                        }
                    });
                    Assert.That(error, Is.SameAs(failure));
                    Assert.That(first.Module, Is.Null);
                    Assert.That(sibling.Module, Is.Null);
                    Assert.That(first.Model, Is.Null);
                    Assert.That(sibling.Model, Is.Null);
                    Assert.That(first.RemovedCount, Is.EqualTo(1));
                    Assert.That(sibling.RemovedCount, Is.EqualTo(1));
                    Assert.That(sibling.ClosedCount, Is.EqualTo(1));
                    Assert.That(sibling.gameObject.activeSelf, Is.False);
                    Assert.That(_module.GetViews<View>(), Is.Empty);
                    Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
                    Assert.That(model.Module, Is.SameAs(_module));
                    Assert.That(model.ExitCount, Is.Zero);
                }
                finally
                {
                    first.ViewRemoved = null;
                }
            }
        }

        [UnityTest]
        public IEnumerator InitializationPublishesLoadedOnlyAfterCallbacksAndRejectsCloseAndDuplicateAdd()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreInitializingPrefab");
            var view = prefab.GetComponent<YLCoreSmokeView>();
            prefab.SetActive(true);
            yield return null;
            Assert.That(view.Module, Is.Null, "Start must not guess the current Module for a scene View.");
            Assert.That(view.IsLoaded, Is.False);
            Assert.That(view.LoadedCount, Is.Zero);
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.Zero);
            view.LoadedAction = () =>
            {
                Assert.That(view.IsLoaded, Is.False);
                Assert.That(view.Module, Is.SameAs(_module));
                Assert.Throws<InvalidOperationException>(() => view.Close());
                Assert.Throws<InvalidOperationException>(() => InvokeViewManager("AddView", _module, view));
            };
            InvokeViewManager("InitView", _module, view, prefab.name, null);
            Assert.That(view.IsLoaded, Is.True);
            Assert.That(view.LoadedCount, Is.EqualTo(1));
            Assert.That(view.ArgsCount, Is.EqualTo(1));
            Assert.That(view.RemovedCount, Is.Zero);
            view.Close();
            Assert.That(view.IsLoaded, Is.False);
            Assert.That(view.Module, Is.Null);
        }

        [UnityTest]
        public IEnumerator FailedInitializationRollsBackRegistrationAndBindingWithoutExitingModel()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            var prefab = Template<YLCoreSmokeView>("YLCoreInitializationRollbackPrefab");
            var view = prefab.GetComponent<YLCoreSmokeView>();
            var failure = new InvalidOperationException("view initialization");
            view.LoadedAction = () =>
            {
                view.BoundModel = model;
                throw failure;
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => InvokeViewManager("InitView", _module, view, prefab.name, null)), Is.SameAs(failure));
            Assert.That(view.Module, Is.Null);
            Assert.That(view.Model, Is.Null);
            Assert.That(view.IsLoaded, Is.False);
            Assert.That(view.UnboundCount, Is.EqualTo(1));
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(model.Module, Is.SameAs(_module));
            Assert.That(model.ExitCount, Is.Zero);
            yield return null;
            Assert.That(view == null, Is.True);
        }

        [UnityTest]
        public IEnumerator PanelRegistrationCleanupDoesNotDependOnListenerSwitchOrSuccessfulGlobalBroadcast()
        {
            yield return StartModule();
            var type = typeof(PanelManager);
            var instanceField = type.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
            var previousManager = instanceField.GetValue(null);
            // 独立构造不经过Instance属性，不注册场景或编辑器事件，也不污染已有manager。
            var manager = Activator.CreateInstance(type, true);
            instanceField.SetValue(null, manager);
            var states = (IDictionary)type.GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            UIRootContext root = null;
            List<Panel> Queue()
            {
                var panels = new List<Panel>();
                PanelManager.GetAllPanels(root, panels);
                return panels;
            }
            try
            {
                root = PolicyRoot("YLCore.CleanupRoot");
                for (int phase = 0; phase < 2; phase++)
                {
                    var prefab = Template<YLCoreSmokePanel>("YLCorePanelCleanupPrefab" + phase, true);
                    var previous = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                        overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                    var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                        overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                    Assert.That(states.Contains(previous), Is.True);
                    Assert.That(states.Contains(panel), Is.True);
                    previous.UpdateDisplay(new PanelOverrides { Priority = 10 });
                    panel.UpdateDisplay(new PanelOverrides { Priority = 20 });
                    PanelManager.CullCoveredPanels = phase == 1;
                    var failure = new InvalidOperationException("global panel observer " + phase);
                    var followingCalls = 0;
                    Action<View> broadcast = removed =>
                    {
                        if (!ReferenceEquals(removed, panel))
                        {
                            return;
                        }

                        Assert.That(states.Contains(panel), Is.False);
                        Assert.That(Queue(), Is.EqualTo(new Panel[] { previous }));
                        Assert.That(PanelManager.GetTopPanel(previous.Root), Is.SameAs(previous));
                        Assert.That(previous.Canvas.enabled, Is.True);
                        Assert.That(panel.Module, Is.Null);
                        throw failure;
                    };
                    var broadcastToken = EventBus.Default.Register(CoreEvents.ViewRemoved, broadcast);
                    var followingToken = EventBus.Default.Register<View>(CoreEvents.ViewRemoved, removed =>
                    {
                        if (!ReferenceEquals(removed, panel))
                        {
                            return;
                        }

                        followingCalls++;
                        Assert.That(panel.IsLoaded, Is.False);
                        Assert.That(panel.Module, Is.Null);
                        Assert.That(panel.gameObject.activeSelf, Is.False);
                        Assert.That(states.Contains(panel), Is.False);
                        Assert.That(Queue(), Is.EqualTo(new Panel[] { previous }));
                        Assert.That(PanelManager.GetTopPanel(previous.Root), Is.SameAs(previous));
                        Assert.That(previous.Canvas.enabled, Is.True);
                    });
                    try
                    {
                        Assert.That(Assert.Throws<InvalidOperationException>(() => panel.Close()), Is.SameAs(failure));
                        Assert.That(panel.gameObject.activeSelf, Is.False);
                        Assert.That(panel.RemovedCount, Is.EqualTo(1));
                        Assert.That(panel.ClosedCount, Is.EqualTo(1));
                        Assert.That(followingCalls, Is.EqualTo(1), "A failed removal observer must not skip the next observer.");
                        Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
                        Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EqualTo(new[] { previous }));
                    }
                    finally
                    {
                        broadcastToken.Unregister();
                        followingToken.Unregister();
                    }
                    Assert.That(Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                        overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true })), Is.SameAs(panel));
                    panel.Close();
                    previous.Close();
                    Assert.That(states, Is.Empty);
                    Assert.That(Queue(), Is.Empty);
                    Assert.That(PanelManager.GetTopPanel(root), Is.Null);
                }
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
                try
                {
                    ((IUnregister)typeof(PanelManager).GetField("_lateSubscription", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager))?.Unregister();
                    typeof(PanelManager).GetMethod("ReleaseOwnedInput", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
                }
                finally
                {
                    instanceField.SetValue(null, previousManager);
                }
            }
        }

        [UnityTest]
        public IEnumerator DirectPrefabInitializesOncePerLoadAndCloseReturnsSameObjectToBackendCache()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreDirectViewPrefab");
            var args = LoadViewArgs.New();
            _classPool.Track(args);
            args.SetInt("token", 41);
            var first = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: args));
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Module, Is.SameAs(_module));
            Assert.That(first.LoadedCount, Is.EqualTo(1));
            Assert.That(first.IsLoaded, Is.True);
            Assert.That(first.ArgsCount, Is.EqualTo(1));
            Assert.That(first.Token, Is.EqualTo(41));
            Assert.That(first.gameObject.activeSelf, Is.True);
            Assert.That(_module.GetView<YLCoreSmokeView>(prefab.name, first.Id), Is.SameAs(first));
            yield return null;
            Assert.That(first.LoadedCount, Is.EqualTo(1), "Start must not initialize an already loaded View again.");
            var removed = 0;
            first.ViewRemoved += () => removed++;
            first.Close();
            Assert.That(first.gameObject.activeSelf, Is.False);
            Assert.That(first != null, Is.True, "Close is cache return, not destruction or asset release.");
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
            Assert.That(_module.GetViews(prefab.name), Is.Empty);
            Assert.That(first.RemovedCount, Is.EqualTo(1));
            Assert.That(first.Module, Is.Null);
            Assert.That(first.IsLoaded, Is.False);
            Assert.That(first.ClosedCount, Is.EqualTo(1));
            first.Close();
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(first.ClosedCount, Is.EqualTo(1));
            var secondArgs = LoadViewArgs.New();
            secondArgs.SetObject("parameter", "reused");
            var second = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: secondArgs));
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.LoadedCount, Is.EqualTo(2));
            Assert.That(second.Parameter, Is.EqualTo("reused"));
            Assert.That(second.Token, Is.Zero, "Recycled load arguments must not carry previous values.");
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Has.Count.EqualTo(1));
            _module.Close();
            Assert.That(second.gameObject.activeSelf, Is.False);
            Assert.That(second.RemovedCount, Is.EqualTo(2));
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator EnableDisableAndCacheReuseSubscribeExactlyOnceAndUnsubscribe()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreDirectEventsPrefab");
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.EqualTo(1));
            view.gameObject.SetActive(false);
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.EqualTo(1));
            view.gameObject.SetActive(true);
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.EqualTo(2));
            view.Close();
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.EqualTo(2));
            Assert.That(Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null)), Is.SameAs(view));
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.EqualTo(3));
            Assert.That(view.EnabledCount, Is.EqualTo(3));
            Assert.That(view.DisabledCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator OrdinaryBusSnapshotCannotEnterReopenedCachedViewSession()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreBusSnapshotSessionPrefab");
            YLCoreSmokeView view = null;
            YLCoreSmokeView replacement = null;
            var reopen = true;
            var token = EventBus.Default.Register(YLCoreSmokeView.ProbeEvent, () =>
            {
                if (!reopen)
                {
                    return;
                }

                reopen = false;
                view.Close();
                replacement = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            });
            try
            {
                view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                var session = ViewSession(view);
                EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                Assert.That(replacement, Is.SameAs(view));
                Assert.That(ViewSession(replacement), Is.Not.SameAs(session));
                Assert.That(replacement.EventCount, Is.Zero, "The old ordinary snapshot must not enter the replacement session.");
                Assert.That(replacement.IsLoaded, Is.True);
                Assert.That(replacement.Module, Is.SameAs(_module));
                Assert.That(replacement.RemovedCount, Is.EqualTo(1));
                Assert.That(replacement.LoadedCount, Is.EqualTo(2));
                EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                Assert.That(replacement.EventCount, Is.EqualTo(1), "The replacement subscription receives the next round exactly once.");
            }
            finally
            {
                token.Unregister();
            }
        }

        [UnityTest]
        public IEnumerator ViewCallbackCloseOrDisableEnableSkipsRemainingSameRoundCallbacks()
        {
            yield return StartModule();
            YLCoreSmokeView.DuplicateProbeHandler = true;
            try
            {
                for (int phase = 0; phase < 2; phase++)
                {
                    var prefab = Template<YLCoreSmokeView>("YLCoreReentrantEventRoundPrefab" + phase);
                    var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                    var session = ViewSession(view);
                    YLCoreSmokeView replacement = null;
                    view.EventAction = () =>
                    {
                        view.EventAction = null;
                        if (phase == 0)
                        {
                            view.Close();
                            replacement = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                        }
                        else
                        {
                            view.gameObject.SetActive(false);
                            view.gameObject.SetActive(true);
                            replacement = view;
                        }
                    };
                    EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                    Assert.That(replacement, Is.SameAs(view));
                    Assert.That(ViewSession(replacement), phase == 0 ? Is.Not.SameAs(session) : Is.SameAs(session));
                    Assert.That(replacement.EventCount, Is.EqualTo(1), "Remaining callbacks belong to the old session or subscription epoch.");
                    Assert.That(replacement.IsLoaded, Is.True);
                    Assert.That(replacement.Module, Is.SameAs(_module));
                    EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                    Assert.That(replacement.EventCount, Is.EqualTo(3), "Both handlers remain registered exactly once in the new round.");
                    replacement.Close();
                }
            }
            finally
            {
                YLCoreSmokeView.DuplicateProbeHandler = false;
            }
        }

        [UnityTest]
        public IEnumerator PartialEventRegistrationSignatureFailureRollsBackOnlyItsOwnTokens()
        {
            yield return StartModule();
            var conflict = "YLCore.PartialRegistration." + Guid.NewGuid().ToString("N");
            var sharedCalls = 0;
            var conflictCalls = 0;
            Action shared = () => sharedCalls++;
            IUnregister sharedToken = null;
            IUnregister conflictToken = null;
            YLCoreSmokeView failed = null;
            try
            {
                YLCoreSmokeView.SharedEventHandler = shared;
                var prefab = Template<YLCoreSmokeView>("YLCorePartialEventRegistrationPrefab");
                var sibling = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                sharedToken = EventBus.Default.Register(YLCoreSmokeView.ProbeEvent, shared);
                conflictToken = EventBus.Default.Register(conflict, () => conflictCalls++);
                YLCoreSmokeView.SignatureConflictEvent = conflict;
                YLCoreSmokeView.LoadingProbe = view => failed = Track(view);
                Assert.Throws<InvalidOperationException>(() => _module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                Assert.That(failed, Is.Not.Null);
                Assert.That(failed.Module, Is.Null);
                Assert.That(failed.IsLoaded, Is.False);
                Assert.That(failed.RemovedCount, Is.EqualTo(1));
                Assert.That(failed.ClosedCount, Is.Zero);
                Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.EqualTo(new[] { sibling }));
                Assert.That(sibling.IsLoaded, Is.True);
                Assert.That(sibling.RemovedCount, Is.Zero);
                Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
                Assert.That(EventBus.Default.ListenerCount(YLCoreSmokeView.ProbeEvent), Is.EqualTo(2));
                EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                Assert.That(sharedCalls, Is.EqualTo(2), "Rollback preserves both the sibling View and the equal direct Default callback.");
                EventBus.Default.Send(conflict);
                Assert.That(conflictCalls, Is.EqualTo(1));
                sibling.Close();
                EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                Assert.That(sharedCalls, Is.EqualTo(3), "Closing the sibling removes only its own subscription token.");
            }
            finally
            {
                YLCoreSmokeView.SignatureConflictEvent = null;
                YLCoreSmokeView.SharedEventHandler = null;
                YLCoreSmokeView.LoadingProbe = null;
                sharedToken?.Unregister();
                conflictToken?.Unregister();
                EventBus.Default.RemoveKey(conflict);
            }
            yield return null;
            Assert.That(failed == null, Is.True, "An activation failure discards the acquired instance rather than pooling it.");
        }

        [UnityTest]
        public IEnumerator MultipleDirectViewsHaveIndependentIdsAndClosingOneKeepsOtherRegistered()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreDirectMultiPrefab");
            var first = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            var second = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            Assert.That(first.Id, Is.Not.EqualTo(second.Id));
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Has.Count.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => _module.GetView<YLCoreSmokeView>());
            Assert.Throws<InvalidOperationException>(() => _module.GetView(prefab.name));
            Assert.Throws<InvalidOperationException>(() => _module.GetView<YLCoreSmokeView>(prefab.name));
            Assert.That(_module.GetView<View>(), Is.Null, "Type-only lookup uses the exact runtime type.");
            Assert.That(_module.GetView<YLCoreSmokeView>(first.Id), Is.SameAs(first), "The prefab name is deliberately not the component type name.");
            Assert.That(_module.GetView<View>(second.Id), Is.SameAs(second));
            Assert.Throws<InvalidCastException>(() => _module.GetView<YLCoreSmokePanel>(prefab.name, first.Id));
            first.Close();
            Assert.That(_module.GetView<YLCoreSmokeView>(prefab.name, first.Id), Is.Null);
            Assert.That(_module.GetView<YLCoreSmokeView>(prefab.name, second.Id), Is.SameAs(second));
            Assert.That(second.gameObject.activeSelf, Is.True);
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.EqualTo(new[] { second }));
            Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { second }));
            Assert.That(_module.GetView<YLCoreSmokeView>(prefab.name), Is.SameAs(second));
            Assert.Throws<InvalidCastException>(() => _module.GetView<YLCoreSmokePanel>(prefab.name));
        }

        [UnityTest]
        public IEnumerator PanelSwitchDoesNotDuplicateTokenAndLastRootEndsInputLifetimeSubscription()
        {
            yield return StartModule();
            var stopper = YLFramework.Update.UpdateManager.AddLateUpdateEvent(() => PanelManager.CullCoveredPanels = false);
            try
            {
                var prefab = Template<YLCoreSmokePanel>("YLCorePanelUpdateRound", true);
                var root = PolicyRoot("YLCore.LateRoundRoot");
                var previous = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                var current = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                current.enabled = false;
                current.gameObject.SetActive(false);
                var host = UnityEngine.Object.FindFirstObjectByType<YLFramework.Update.UpdateManager>();
                host.SendMessage("LateUpdate");
                Assert.That(previous.Canvas.enabled, Is.True, "关闭遮挡策略应立即恢复显示。");
                stopper.Unregister();
                PanelManager.CullCoveredPanels = true;
                PanelManager.CullCoveredPanels = true;
                host.SendMessage("LateUpdate");
                Assert.That(previous.Canvas.enabled, Is.True);
                var channel = (EventChannel)typeof(YLFramework.Update.UpdateManager)
                    .GetField("_lateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
                Assert.That(channel.ListenerCount, Is.EqualTo(1), "同值启用不能重复建立连接。");
                current.Close();
                previous.Close();
                Assert.That(channel.ListenerCount, Is.EqualTo(1), "窗口队列归零后仍服务有效输入根，不销毁其他 Module 可继续使用的输入。");
                Object.Destroy(root.gameObject);
                yield return null;
                yield return null;
                Assert.That(channel.ListenerCount, Is.Zero, "最后的 UI 根退出后释放自身输入寿命订阅，保留共享宿主。");
                Assert.That(host != null, Is.True);
            }
            finally
            {
                stopper.Unregister();
                PanelManager.CullCoveredPanels = true;
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator ViewScannerResetPreservesDomainOwnershipAndRejectsOldSnapshot()
        {
            yield return StartModule();
            var template = Template<YLCoreSmokeView>("YLCoreViewUpdateRound");
            YLCoreSmokeView first = null;
            YLCoreSmokeView replacement = null;
            object firstSession = null;
            IUnregister resetter = null;
            resetter = YLFramework.Update.UpdateManager.AddUpdateEvent(() =>
            {
                resetter.Unregister();
                InvokeViewManager("ResetSession");
                Assert.That(first.Module, Is.SameAs(_module));
                Assert.That(ViewSession(first), Is.SameAs(firstSession), "执行会话重置不调用业务 Close 或清 Module 索引。");
                var obj = new GameObject("YLCoreNewInactiveViewRound");
                _objects.Add(obj);
                obj.SetActive(false);
                replacement = obj.AddComponent<YLCoreSmokeView>();
                _module.AttachView(replacement);
                UnityEngine.Object.DestroyImmediate(obj);
            });
            try
            {
                first = Track(_module.LoadView<YLCoreSmokeView>(template, loadViewArgs: null));
                firstSession = ViewSession(first);
                var host = UnityEngine.Object.FindFirstObjectByType<YLFramework.Update.UpdateManager>();
                host.SendMessage("Update");
                Assert.That(ViewSession(replacement), Is.Not.Null, "旧扫描快照不能扫描新订阅轮次的索引。");
                yield return null;
                yield return null;
                Assert.That(ViewSession(replacement), Is.Null, "新轮次继续保留从未激活对象的必要扫描。");
                Assert.That(first.Module, Is.SameAs(_module));
                first.Close();
            }
            finally
            {
                resetter.Unregister();
            }
        }

        [UnityTest]
        public IEnumerator DefaultPanelListenerRestoresPreviousPanelWhenDisabledComponentMissesDeactivation()
        {
            yield return StartModule();
            Assert.That(PanelManager.CullCoveredPanels, Is.True);
            try
            {
                var prefab = Template<YLCoreSmokePanel>("YLCorePanelLateFallback", true);
                var root = PolicyRoot("YLCore.LateFallbackRoot");
                var previous = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                var current = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                Assert.That(previous.Canvas.enabled, Is.False);
                // 停用保留会话；Late 扫描也必须收敛到实际可见性。
                current.enabled = false;
                current.gameObject.SetActive(false);
                yield return null;
                yield return null;
                Assert.That(previous.Canvas.enabled, Is.True);
                Assert.That(PanelManager.GetTopPanel(previous.Root), Is.SameAs(previous));
                Assert.That(current.IsLoaded, Is.True);
                current.Close();
                previous.Close();
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator InactivePanelAttachmentKeepsVisiblePanelUntilActivationAndRetainsOrderOnDisable()
        {
            yield return StartModule();
            try
            {
                var prefab = Template<YLCoreSmokePanel>("YLCoreInactivePanelPrefab", true);
                var root = PolicyRoot("YLCore.InactiveRoot");
                var visible = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
                var parent = new GameObject("YLCoreInactivePanelParent");
                _objects.Add(parent);
                parent.transform.SetParent(root.transform, false);
                parent.SetActive(false);
                var obj = new GameObject("YLCoreInactiveScenePanel", typeof(RectTransform));
                _objects.Add(obj);
                obj.transform.SetParent(parent.transform);
                var inactive = obj.AddComponent<YLCoreSmokePanel>();
                SetPrivate(inactive, "settings", new PanelOverrides { CoversRoot = true }.ApplyTo(PanelConfig.Default));
                _module.AttachView(inactive);
                Assert.That(inactive.IsLoaded, Is.True);
                Assert.That(inactive.gameObject.activeSelf, Is.True);
                Assert.That(inactive.gameObject.activeInHierarchy, Is.False);
                Assert.That(visible.Canvas.enabled, Is.True, "Inactive preparation cannot hide the current Panel.");
                var orders = (IDictionary)typeof(PanelManager).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(typeof(PanelManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
                Assert.That(orders.Contains(inactive), Is.True);
                inactive.UpdateDisplay(new PanelOverrides { Priority = 91 });
                var preparedOrder = inactive.SortingOrder;
                Assert.That(inactive.EffectiveSettings.priority, Is.EqualTo(91));
                parent.SetActive(true);
                Assert.That(visible.Canvas.enabled, Is.False);
                Assert.That(inactive.Canvas.enabled, Is.True);
                if (!inactive.Canvas.isRootCanvas)
                {
                    Assert.That(inactive.Canvas.overrideSorting, Is.True);
                }

                Assert.That(inactive.Canvas.sortingOrder, Is.EqualTo(preparedOrder), "First native Canvas activation must restore the prepared order.");
                YLCoreSmokePanel.VisibleProbe = panel => EventBus.Default.Send(YLCoreSmokePanel.ProbeEvent);
                parent.SetActive(false);
                Assert.That(inactive.EventCount, Is.Zero, "The disabled Panel must unsubscribe before restoring the previous Panel's business callback.");
                Assert.That(visible.EventCount, Is.EqualTo(1));
                Assert.That(visible.Canvas.enabled, Is.True);
                Assert.That(inactive.IsLoaded, Is.True);
                Assert.That(inactive.Module, Is.SameAs(_module));
                Assert.That(orders.Contains(inactive), Is.True, "Disable pauses display, not business registration or sorting ownership.");
                inactive.Close();
                Assert.That(orders.Contains(inactive), Is.False);
                visible.Close();
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator PanelActivationCallbackCannotRemoveAnotherPanelsSharedEventSubscription()
        {
            yield return StartModule();
            YLCoreSmokePanel.UseSharedHandler = true;
            try
            {
                var prefab = Template<YLCoreSmokePanel>("YLCoreSharedPanelEventPrefab", true);
                var root = PolicyRoot("YLCore.SharedEventRoot");
                var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                YLCoreSmokePanel.InvisibleProbe = panel =>
                {
                    Assert.That(panel, Is.SameAs(first));
                    var activating = _module.GetViews<YLCoreSmokePanel>().Find(item => item != first);
                    Assert.That(activating, Is.Not.Null);
                    YLCoreSmokePanel.InvisibleProbe = null;
                    activating.gameObject.SetActive(false);
                };
                var second = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                Assert.That(second.IsLoaded, Is.True);
                Assert.That(second.gameObject.activeSelf, Is.False);
                Assert.That(first.Canvas.enabled, Is.True);
                EventBus.Default.Send(YLCoreSmokePanel.ProbeEvent);
                Assert.That(YLCoreSmokePanel.SharedEventCount, Is.EqualTo(1), "Rollback of an activating Panel cannot remove the other Panel's equal delegate.");
                first.Close();
                second.Close();
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator DirectPanelUsesInheritedViewPrefabContractAndInitializesCanvasOnlyOnce()
        {
            yield return StartModule();
            foreach (var name in new[] { nameof(Panel.EffectiveSettings), nameof(Panel.Root), nameof(Panel.Canvas), nameof(Panel.SortingOrder) })
            {
                Assert.That(typeof(Panel).GetProperty(name).GetSetMethod(), Is.Null, name + " 不能提供公开写入口。");
            }

            var prefab = Template<YLCoreSmokePanel>("YLCoreDirectPanelPrefab", true);
            var parent = PolicyRoot("YLCoreDirectPanelParent");
            parent.gameObject.SetActive(false);
            var args = LoadViewArgs.New();
            _classPool.Track(args);
            args.SetInt("token", 17);
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, args, root: parent));
            Assert.That(panel.IsLoaded, Is.True);
            Assert.That(panel.gameObject.activeInHierarchy, Is.False);
            parent.gameObject.SetActive(true);
            Assert.That(panel.Canvas, Is.SameAs(panel.GetComponent<Canvas>()));
            Assert.That(panel.Canvas.overrideSorting, Is.True);
            Assert.That(panel.Canvas.sortingOrder, Is.EqualTo(panel.SortingOrder));
            Assert.That(panel.GetComponent<UnityEngine.UI.GraphicRaycaster>(), Is.Not.Null);
            Assert.That(panel.Token, Is.EqualTo(17));
            panel.UpdateDisplay(new PanelOverrides { Priority = 30 });
            Assert.That(panel.EffectiveSettings.priority, Is.EqualTo(30));
            var canvas = panel.Canvas;
            panel.Close();
            Assert.That(panel.RemovedCount, Is.EqualTo(1));
            var reused = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: parent));
            Assert.That(reused, Is.SameAs(panel));
            Assert.That(reused.Canvas, Is.SameAs(canvas));
            Assert.That(reused.GetComponents<Canvas>(), Has.Length.EqualTo(1));
            Assert.That(reused.LoadedCount, Is.EqualTo(2));
            Assert.That(reused.ArgsCount, Is.EqualTo(2));
            Assert.That(reused.OpenCount, Is.EqualTo(2));
            yield return null;
            Assert.That(reused.LoadedCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator DirectCheckoutDiscardsTheWrongPrefabIdentityBeforeInitialization() => RejectWrongDirectCheckout(false);
        [UnityTest]
        public IEnumerator DirectCheckoutDiscardsADestroyedSourceMarkerBeforeInitialization() => RejectWrongDirectCheckout(true);

        private IEnumerator RejectWrongDirectCheckout(bool destroySource)
        {
            yield return StartModule();
            var root = PolicyRoot("YLPanel.WrongCheckoutRoot");
            var expected = Template<YLCoreSmokePanel>("SamePanelName", true);
            var other = Template<YLCoreSmokePanel>("SamePanelName", true);
            var wrong = Object.Instantiate(other);
            _objects.Add(wrong);
            var wrongPanel = wrong.GetComponent<YLCoreSmokePanel>();
            typeof(View).GetField("DirectSource", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(wrongPanel, other);
            if (destroySource)
            {
                Object.DestroyImmediate(other);
            }

            string key = "YL.Core.Direct/" + expected.GetInstanceID();
            var pool = YLFramework.Pooling.GameObjectPool.Instance;
            Assert.That(pool.Recycle(key, wrong), Is.True);
            int wrongCallbacks = 0;
            YLCoreSmokePanel.LoadingProbe = panel =>
            {
                if (ReferenceEquals(panel, wrongPanel))
                {
                    wrongCallbacks++;
                }
            };
            var result = Track(_module.LoadPanel<YLCoreSmokePanel>(expected, root: root));
            Assert.That(result, Is.Not.SameAs(wrongPanel));
            Assert.That(wrongCallbacks, Is.Zero, "不能先初始化错源实例再事后修正来源。");
            Assert.That(typeof(View).GetField("DirectSource", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result), Is.SameAs(expected));
            yield return null;
            Assert.That(wrong == null, Is.True);
            result.Close();
            Assert.That(pool.GetCachedCount(key), Is.EqualTo(1), "错误来源不得留回正确来源的桶。");
        }

        [UnityTest]
        public IEnumerator OrdinaryViewEntryRejectsActualPanelBeforeCheckoutEvenForGenericView()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokePanel>("YLCore.PanelThroughView", true);
            var args = NewArgs();
            var error = Assert.Throws<InvalidOperationException>(() => _module.LoadView<View>(prefab, loadViewArgs: args));
            StringAssert.Contains("LoadPanel", error.Message);
            AssertArgsReleased(args);
            args = NewArgs();
            // 普通 View 没有公开 direct Async 重载，直接覆盖已有生产内核的同一拒绝边界。
            var start = typeof(View).Assembly.GetType("YLFramework.Core.ViewManager", true)
                .GetMethod("StartAsync", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(typeof(View));
            var pending = (LoadViewRequest<View>)start.Invoke(null, new object[] { _module, prefab.name, null, args, prefab });
            yield return pending;
            Assert.That(pending.Status, Is.EqualTo(LoadViewStatus.Failed));
            Assert.That(pending.Exception, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("LoadPanel", pending.Exception.Message);
            AssertArgsReleased(args);
            Assert.That(prefab.GetComponent<YLCoreSmokePanel>().LoadedCount, Is.Zero);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            var pool = Object.FindFirstObjectByType<YLFramework.Pooling.GameObjectPool>();
            if (pool)
            {
                var buckets = (IDictionary)pool.GetType().GetField("_pools", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pool);
                Assert.That(buckets.Contains("YL.Core.Direct/" + prefab.GetInstanceID()), Is.False, "拒绝必须先于后端取得。");
            }
        }

        [UnityTest]
        public IEnumerator ModuleCloseOrdersControllerViewAndRemainingModelsAndDetachesViewOwner()
        {
            yield return StartModule();
            var events = new List<string>();
            var bound = new YLCoreProbeModel { Id = 1, ExitAction = _ => events.Add("bound.exit") };
            var remaining = new YLCoreProbeModel { Id = 2, ExitAction = _ => events.Add("remaining.exit") };
            _module.AddModel(bound);
            _module.AddModel(remaining);
            var controller = _module.LoadController<YLCoreProbeController>();
            controller.ExitAction = _ => events.Add("controller.exit");
            var prefab = Template<YLCoreSmokeView>("YLCoreCloseOrderPrefab");
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            view.BoundModel = bound;
            view.ViewRemoved = () => events.Add("view.remove");
            view.RemoveAction = () => events.Add("view.onRemove");
            view.UnboundAction = previous =>
            {
                Assert.That(previous, Is.SameAs(bound));
                Assert.That(view.Model, Is.Null);
                Assert.That(bound.ExitCount, Is.Zero);
                events.Add("view.unbound");
            };
            view.AfterClose = () =>
            {
                Assert.That(view.Module, Is.Null);
                Assert.That(view.Model, Is.Null);
                Assert.That(view.IsLoaded, Is.False);
                Assert.That(view.gameObject.activeSelf, Is.False);
                Assert.That(_module.GetViews<View>(), Is.Empty);
                events.Add("view.closed");
            };
            _module.CloseAction = () =>
            {
                events.Add("module.close");
                Assert.That(_module.GetView(prefab.name), Is.SameAs(view));
                Assert.That(controller.ExitCount, Is.Zero);
                Assert.That(bound.ExitCount, Is.Zero);
            };
            _module.Close();
            Assert.That(events, Is.EqualTo(new[] { "module.close", "controller.exit", "view.remove", "view.onRemove", "view.unbound", "view.closed", "bound.exit", "remaining.exit" }));
            Assert.That(bound.ExitCount, Is.EqualTo(1));
            Assert.That(remaining.ExitCount, Is.EqualTo(1));
            Assert.That(view.Module, Is.Null);
            Assert.That(view.gameObject.activeSelf, Is.False);
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator ViewsRemovedInsideCloseHooksStillLoseOwnerAtModuleClose()
        {
            for (int phase = 0; phase < 2; phase++)
            {
                yield return StartModule();
                var prefab = Template<YLCoreSmokeView>("YLCoreHookClosePrefab" + phase);
                var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                var controller = _module.LoadController<YLCoreProbeController>();
                _module.CloseAction = phase == 0 ? (Action)(() => view.Close()) : null;
                controller.ExitAction = _ => view.Close();
                view.ViewRemoved = () => Assert.That(view.Module, Is.SameAs(_module));
                _module.Close();
                Assert.That(view.RemovedCount, Is.EqualTo(1));
                Assert.That(view.Module, Is.Null);
                Assert.That(view.gameObject.activeSelf, Is.False);
                Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator ViewReloadedByAnotherModuleDuringCloseKeepsNewOwnerAndIndexes()
        {
            yield return StartModule();
            var other = new YLCoreConsumerModule();
            YLCoreSmokeView view = null;
            try
            {
                var request = ModuleManager.StartUpModule(other);
                var deadline = Time.realtimeSinceStartup + 10;
                while (!request.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(request.IsDone, Is.True);
                Assert.That(request.Error, Is.Null.Or.Empty);
                var prefab = Template<YLCoreSmokeView>("YLCoreTransferOnClosePrefab");
                view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                var sibling = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                var bound = _module.LoadModel<YLCoreProbeModel>();
                view.BoundModel = bound;
                YLCoreSmokeView reloaded = null;
                YLCoreSmokeView reloadedSibling = null;
                view.AfterClose = () =>
                {
                    Assert.That(view.Module, Is.Null);
                    Assert.That(view.Model, Is.Null);
                    Assert.That(view.gameObject.activeSelf, Is.False);
                    Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { sibling }));
                    sibling.Close();
                    reloaded = Track(other.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                    reloadedSibling = Track(other.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                };
                _module.Close();
                view.AfterClose = null;
                Assert.That(reloaded, Is.SameAs(view));
                Assert.That(reloadedSibling, Is.SameAs(sibling));
                Assert.That(view.Module, Is.SameAs(other));
                Assert.That(sibling.Module, Is.SameAs(other));
                Assert.That(sibling.RemovedCount, Is.EqualTo(1), "The old Module snapshot must not close the sibling's replacement session.");
                Assert.That(bound.ExitCount, Is.EqualTo(1));
                Assert.That(view.ClosedCount, Is.EqualTo(1));
                Assert.That(other.GetView(prefab.name, view.Id), Is.SameAs(view));
                Assert.That(other.GetView(prefab.name, sibling.Id), Is.SameAs(sibling));
                Assert.That(view.gameObject.activeSelf, Is.True);
                Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
                view.Close();
                Assert.That(other.GetViews<YLCoreSmokeView>(), Is.EqualTo(new[] { sibling }));
                Assert.That(view.RemovedCount, Is.EqualTo(2));
                sibling.Close();
                Assert.That(other.GetViews<YLCoreSmokeView>(), Is.Empty);
                Assert.That(sibling.RemovedCount, Is.EqualTo(2));
            }
            finally
            {
                if (view)
                {
                    view.AfterClose = null;
                }

                other.Close();
                _classPool.Restore(new Module[] { other });
            }
        }

        [UnityTest]
        public IEnumerator ViewCleanupErrorDoesNotBlockSiblingViewsOrRemainingModelCleanup()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            var prefab = Template<YLCoreSmokeView>("YLCoreCloseFailurePrefab");
            var first = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            var sibling = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            first.BoundModel = model;
            var viewError = new InvalidOperationException("view cleanup");
            first.ViewRemoved = () => throw viewError;
            var error = Assert.Throws<AggregateException>(() => _module.Close());
            Assert.That(error.InnerExceptions, Is.EqualTo(new[] { viewError }));
            Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(ModuleManager.GetModule(_module.ModuleName), Is.Null);
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(model.Module, Is.Null);
            Assert.That(first.Module, Is.Null);
            Assert.That(sibling.Module, Is.Null);
            Assert.That(sibling.RemovedCount, Is.EqualTo(1));
            Assert.That(sibling.gameObject.activeSelf, Is.False);
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
            first.Close();
            _module.Close();
            Assert.That(model.ExitCount, Is.EqualTo(1));
            first.ViewRemoved = null;
        }

        [UnityTest]
        public IEnumerator ModuleCloseFlattensViewErrorsAndContinuesCompletionHooksAndRemainingCleanup()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreAggregatedExitPrefab");
            var first = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            var sibling = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            var controller = _module.LoadController<YLCoreProbeController>();
            var model = _module.LoadModel<YLCoreProbeModel>();
            first.BoundModel = model;
            var controllerError = new InvalidOperationException("controller exit");
            var observerError = new ArgumentException("view observer");
            var removeError = new InvalidOperationException("view OnRemove");
            var unbindError = new InvalidOperationException("view unbind");
            var completedError = new ArgumentException("view OnClosed");
            var siblingError = new InvalidOperationException("sibling observer");
            var modelError = new InvalidOperationException("model exit");
            controller.ExitAction = _ => throw controllerError;
            first.ViewRemoved = () => throw observerError;
            first.RemoveAction = () => throw removeError;
            first.UnboundAction = previous =>
            {
                Assert.That(first.Model, Is.Null);
                throw unbindError;
            };
            first.AfterClose = () =>
            {
                Assert.That(first.Module, Is.Null);
                Assert.That(first.gameObject.activeSelf, Is.False);
                throw completedError;
            };
            sibling.ViewRemoved = () => throw siblingError;
            model.ExitAction = _ => throw modelError;
            var error = Assert.Throws<AggregateException>(() => _module.Close());
            Assert.That(error.InnerExceptions, Is.EquivalentTo(new Exception[]
            {
                controllerError, observerError, removeError, unbindError, completedError, siblingError, modelError
            }));
            Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(ModuleManager.GetModule(_module.ModuleName), Is.Null);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(first.Model, Is.Null);
            Assert.That(first.UnboundCount, Is.EqualTo(1));
            Assert.That(first.ClosedCount, Is.EqualTo(1));
            Assert.That(sibling.RemovedCount, Is.EqualTo(1));
            Assert.That(sibling.ClosedCount, Is.EqualTo(1));
            Assert.That(controller.ExitCount, Is.EqualTo(1));
            Assert.That(model.ExitCount, Is.EqualTo(1));
            Assert.That(controller.Module, Is.Null);
            Assert.That(model.Module, Is.Null);
            first.Close();
            _module.Close();
            yield return null;
            Assert.That(first == null, Is.True);
            Assert.That(sibling != null, Is.True);
        }

        [UnityTest]
        public IEnumerator GenericMismatchWithDefaultArgsIsRejectedBeforeInitialization()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreLegacyMismatchPrefab");
            Assert.Throws<InvalidCastException>(() => _module.LoadView<YLCoreSmokePanel>(prefab, loadViewArgs: null));
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(prefab.GetComponent<YLCoreSmokeView>().LoadedCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RejectedSynchronousInputsConsumeArgumentsWithoutCreatingViewsOrPools()
        {
            var prefab = Template<YLCoreSmokeView>("YLCoreInvalidInputsPrefab");
            var args = NewArgs();
            args.SetObject("retained", prefab);
            Assert.Throws<InvalidOperationException>(() => _module.LoadView(prefab, loadViewArgs: args));
            AssertArgsReleased(args);
            args = NewArgs();
            Assert.Throws<InvalidOperationException>(() => _module.LoadPanel("unused", loadViewArgs: args));
            AssertArgsReleased(args);
            yield return StartModule();
            args = NewArgs();
            Assert.Throws<ArgumentNullException>(() => _module.LoadView((GameObject)null, loadViewArgs: args));
            AssertArgsReleased(args);
            args = NewArgs();
            Assert.Throws<ArgumentException>(() => _module.LoadView(" ", loadViewArgs: args));
            AssertArgsReleased(args);
            args = NewArgs();
            Assert.Throws<InvalidCastException>(() => _module.LoadView<YLCoreSmokePanel>(prefab, loadViewArgs: args));
            AssertArgsReleased(args);
            var noView = new GameObject("YLCoreNoViewPrefab");
            _objects.Add(noView);
            args = NewArgs();
            Assert.Throws<InvalidOperationException>(() => _module.LoadView(noView, loadViewArgs: args));
            AssertArgsReleased(args);
            var deadParent = new GameObject("YLCoreDeadParent").transform;
            Object.Destroy(deadParent.gameObject);
            yield return null;
            args = NewArgs();
            Assert.Throws<ArgumentException>(() => _module.LoadView(prefab, deadParent, loadViewArgs: args));
            AssertArgsReleased(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            var loader = Object.FindFirstObjectByType<YLFramework.Pooling.GameObjectPool>();
            if (loader)
            {
                var pools = (IDictionary)loader.GetType().GetField("_pools", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(loader);
                Assert.That(pools.Contains("YL.Core.Direct/" + prefab.GetInstanceID()), Is.False, "Prefab validation precedes backend acquisition.");
            }
        }

        [UnityTest]
        public IEnumerator InitializationIsTemporarilyQueryableAndArgsCannotBeReenteredOrRetained()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreLoadTransactionPrefab");
            var siblingPrefab = Template<YLCoreSmokePanel>("YLCoreNestedInitializationPrefab", true);
            var siblingRoot = PolicyRoot("YLCoreNestedInitializationRoot");
            var args = NewArgs();
            args.SetInt("token", 91);
            YLCoreSmokeView.LoadingProbe = view =>
            {
                Track(view);
                Assert.That(view.IsLoaded, Is.False);
                Assert.That(_module.GetView(prefab.name, view.Id), Is.SameAs(view));
                Assert.That(_module.GetView<YLCoreSmokeView>(view.Id), Is.SameAs(view));
                Assert.Throws<InvalidOperationException>(() => InvokeViewManager("InitView", _module, view, prefab.name, null));
                Assert.Throws<InvalidOperationException>(() => view.Close());
                Assert.Throws<InvalidOperationException>(() => _module.Close());
                Assert.Throws<InvalidOperationException>(() => ModuleManager.Shutdown());
                Assert.That(args.GetInt("token"), Is.EqualTo(91), "The single typed initialization owns the borrowing window from entry.");
                Assert.Throws<InvalidOperationException>(() => args.SetInt("token", 2));
                Assert.Throws<InvalidOperationException>(() => _module.LoadView(prefab, loadViewArgs: args));
                EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                Assert.That(view.EventCount, Is.Zero);
                var sibling = Track(_module.LoadPanel<YLCoreSmokePanel>(siblingPrefab, root: siblingRoot));
                Assert.That(sibling.IsLoaded, Is.True, "Other valid objects may initialize inside the transaction.");
            };
            YLCoreSmokeView.ArgsProbe = (view, borrowed) =>
            {
                Assert.That(borrowed, Is.SameAs(args));
                Assert.That(borrowed.GetInt("token"), Is.EqualTo(91));
                Assert.Throws<InvalidOperationException>(() => borrowed.SetInt("token", 2));
                Assert.Throws<InvalidOperationException>(() => _module.LoadView(prefab, loadViewArgs: borrowed));
                Assert.That(borrowed.GetInt("token"), Is.EqualTo(91), "Rejected second takeover cannot recycle the first operation's input.");
                Assert.That(view.IsLoaded, Is.False);
            };
            YLCoreSmokeView.EnabledProbe = view =>
            {
                Assert.That(view.IsLoaded, Is.True);
                Assert.Throws<InvalidOperationException>(() => args.GetInt("token"), "The borrowing window ended before event activation.");
            };
            var loaded = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: args));
            Assert.That(loaded.Token, Is.EqualTo(91));
            AssertArgsReleased(args);
            Assert.That(loaded.LoadedCount, Is.EqualTo(1));
            Assert.That(loaded.ArgsCount, Is.EqualTo(1));
            Assert.That(_module.GetViews<View>(), Has.Count.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => InvokeViewManager("InitView", _module, loaded, prefab.name, null));
            Assert.That(loaded.LoadedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator BusinessInitializationAndEventActivationFailuresRollbackWithoutReusingDamagedInstance()
        {
            yield return StartModule();
            var model = _module.LoadModel<YLCoreProbeModel>();
            for (int phase = 0; phase < 3; phase++)
            {
                var prefab = Template<YLCoreSmokeView>("YLCoreLoadFailurePrefab" + phase);
                var args = NewArgs();
                args.SetObject("model", model);
                var failure = new InvalidOperationException("load failure " + phase);
                var cleanupError = new ArgumentException("load rollback cleanup");
                YLCoreSmokeView failed = null;
                YLCoreSmokeView.LoadingProbe = view =>
                {
                    failed = Track(view);
                    view.BoundModel = model;
                    view.ViewRemoved = () => Assert.Fail("Failed initialization must not publish normal removal notifications.");
                    if (phase == 0)
                    {
                        view.RemoveAction = () => throw cleanupError;
                        throw failure;
                    }
                };
                YLCoreSmokeView.ArgsProbe = (view, borrowed) =>
                {
                    if (phase == 1)
                    {
                        throw failure;
                    }
                };
                YLCoreSmokeView.EnabledProbe = view =>
                {
                    if (phase == 2)
                    {
                        throw failure;
                    }
                };
                Assert.That(Assert.Throws<InvalidOperationException>(() => _module.LoadView(prefab, loadViewArgs: args)), Is.SameAs(failure));
                AssertArgsReleased(args);
                Assert.That(failed.Module, Is.Null);
                Assert.That(failed.Model, Is.Null);
                Assert.That(failed.IsLoaded, Is.False);
                Assert.That(failed.RemovedCount, Is.EqualTo(1));
                Assert.That(failed.UnboundCount, Is.EqualTo(1));
                Assert.That(failed.ClosedCount, Is.Zero);
                Assert.That(failed.ViewRemoved, Is.Null);
                Assert.That(_module.GetViews<View>(), Is.Empty);
                Assert.That(model.ExitCount, Is.Zero);
                Assert.That(_module.GetModel<YLCoreProbeModel>(), Is.SameAs(model));
                Assert.That(failed.gameObject.activeSelf, Is.False);
                Assert.That(BackendInUse(prefab, failed.gameObject), Is.True, "Do not expose a pending Destroy as idle in the pool.");
                if (phase == 0)
                {
                    Assert.That(failure.Data["YLFramework.Core.CleanupException"], Is.SameAs(cleanupError));
                }

                YLCoreSmokeView.LoadingProbe = null;
                YLCoreSmokeView.ArgsProbe = null;
                YLCoreSmokeView.EnabledProbe = null;
                var replacement = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                Assert.That(replacement, Is.Not.SameAs(failed), "Same-frame load cannot reuse a damaged instance.");
                Assert.That(replacement.IsLoaded, Is.True);
                yield return null;
                Assert.That(failed == null, Is.True);
                replacement.Close();
            }
        }

        [UnityTest]
        public IEnumerator FrameworkPreparationFailureReleasesArgsAndDoesNotLeaveTemporaryRegistration()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokePanel>("YLCoreInvalidCanvasPrefab", true);
            prefab.AddComponent<Canvas>();
            var root = PolicyRoot("YLCore.InvalidCanvasRoot");
            root.Canvas.sortingOrder = short.MaxValue;
            var args = NewArgs();
            Assert.Throws<ArgumentOutOfRangeException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab, args, root));
            AssertArgsReleased(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            var all = Resources.FindObjectsOfTypeAll<YLCoreSmokePanel>().Where(view => view && view.gameObject.scene.IsValid() &&
                view.gameObject.name == prefab.name + "(Clone)").ToArray();
            Assert.That(all, Is.Empty, "排序空间预校验必须先于实例取得。初始化中失败的回滚由独立用例覆盖。");
            Assert.That(prefab.GetComponent<YLCoreSmokePanel>().LoadedCount, Is.Zero);
            Assert.That(prefab.GetComponent<YLCoreSmokePanel>().ArgsCount, Is.Zero);
            yield return null;
        }

        [UnityTest]
        public IEnumerator InactiveParentInitializesBeforeAwakeAndNewCacheSessionStartsWithoutOldBindings()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreInactiveParentPrefab");
            var parent = new GameObject("YLCoreInactiveParent");
            parent.SetActive(false);
            _objects.Add(parent);
            var model = _module.LoadModel<YLCoreProbeModel>();
            var args = NewArgs();
            args.SetInt("token", 51);
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, parent.transform, loadViewArgs: args));
            Assert.That(view.IsLoaded, Is.True);
            Assert.That(view.AwakeCount, Is.Zero);
            Assert.That(view.EnabledCount, Is.Zero);
            Assert.That(view.Token, Is.EqualTo(51));
            view.BoundModel = model;
            view.ViewRemoved = () => { };
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.Zero);
            parent.SetActive(true);
            Assert.That(view.AwakeCount, Is.EqualTo(1));
            Assert.That(view.EnabledCount, Is.EqualTo(1));
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.EqualTo(1));
            parent.SetActive(false);
            Assert.That(view.Module, Is.SameAs(_module));
            Assert.That(view.Model, Is.SameAs(model));
            view.Close();
            var session = ViewSession(view);
            Assert.That(session, Is.Null);
            var reused = Track(_module.LoadView<YLCoreSmokeView>(prefab, parent.transform, loadViewArgs: null));
            Assert.That(reused, Is.SameAs(view));
            Assert.That(reused.Model, Is.Null);
            Assert.That(reused.ViewRemoved, Is.Null);
            Assert.That(reused.Token, Is.Zero);
            Assert.That(reused.LoadedCount, Is.EqualTo(2));
            Assert.That(reused.AwakeCount, Is.EqualTo(1));
            Assert.That(reused.EnabledCount, Is.EqualTo(1));
            parent.SetActive(true);
            parent.SetActive(true);
            Assert.That(reused.EnabledCount, Is.EqualTo(2));
            EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
            Assert.That(reused.EventCount, Is.EqualTo(2));
            Assert.That(model.ExitCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CachedActualInstanceIsRevalidatedWhenItsViewComponentWasRemoved()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreMutatedCachePrefab");
            var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            var instance = view.gameObject;
            view.Close();
            Object.DestroyImmediate(view);
            var args = NewArgs();
            Assert.Throws<InvalidOperationException>(() => _module.LoadView(prefab, loadViewArgs: args));
            AssertArgsReleased(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(BackendInUse(prefab, instance), Is.True);
            Assert.That(instance.activeSelf, Is.False);
            var fresh = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            Assert.That(fresh.gameObject, Is.Not.SameAs(instance));
            yield return null;
            Assert.That(instance == null, Is.True);
        }

        [UnityTest]
        public IEnumerator OldEnableDisableHookErrorsCannotAffectReplacementSessionOrSubscriptionRound()
        {
            yield return StartModule();
            for (int phase = 0; phase < 3; phase++)
            {
                var prefab = Template<YLCoreSmokeView>("YLCoreEventReplacementPrefab" + phase);
                var view = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                var session = ViewSession(view);
                var failure = new InvalidOperationException("old event hook " + phase);
                YLCoreSmokeView replacement = null;
                bool once = true;
                view.AfterClose = () =>
                {
                    view.AfterClose = null;
                    replacement = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
                };
                Action<YLCoreSmokeView> hook = current =>
                {
                    if (!once)
                    {
                        return;
                    }

                    once = false;
                    if (phase == 2)
                    {
                        InvokeViewInstance("StopEvents", current);
                        InvokeViewInstance("ResumeEvents", current);
                        replacement = current;
                    }
                    else
                    {
                        current.Close();
                    }

                    throw failure;
                };
                if (phase != 1)
                {
                    InvokeViewInstance("StopEvents", view);
                    YLCoreSmokeView.EnabledProbe = hook;
                }
                else
                {
                    YLCoreSmokeView.DisabledProbe = hook;
                }

                var error = Assert.Throws<InvalidOperationException>(() => InvokeViewInstance(phase == 1 ? "StopEvents" : "ResumeEvents", view));
                Assert.That(error, Is.SameAs(failure));
                Assert.That(replacement, Is.SameAs(view));
                Assert.That(ViewSession(replacement), phase == 2 ? Is.SameAs(session) : Is.Not.SameAs(session));
                Assert.That(replacement.IsLoaded, Is.True);
                Assert.That(replacement.Module, Is.SameAs(_module));
                var count = replacement.EventCount;
                EventBus.Default.Send(YLCoreSmokeView.ProbeEvent);
                Assert.That(replacement.EventCount, Is.EqualTo(count + 1), "Old hook cleanup must not stop the replacement's listeners.");
                YLCoreSmokeView.EnabledProbe = null;
                YLCoreSmokeView.DisabledProbe = null;
                replacement.AfterClose = null;
                replacement.Close();
                Assert.That(Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null)), Is.SameAs(replacement), "The replacement stays healthy and reusable.");
                replacement.Close();
            }
        }

        [UnityTest]
        public IEnumerator FirstEventHookDestroyedComponentFailsLoadAndDiscardsAcquiredRoot()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreDestroyDuringEventOpenPrefab");
            var model = _module.LoadModel<YLCoreProbeModel>();
            var args = NewArgs();
            YLCoreSmokeView destroyed = null;
            GameObject root = null;
            YLCoreSmokeView.LoadingProbe = view => view.BoundModel = model;
            YLCoreSmokeView.EnabledProbe = view =>
            {
                destroyed = Track(view);
                root = view.gameObject;
                Object.DestroyImmediate(view);
            };
            Assert.Throws<InvalidOperationException>(() => _module.LoadView(prefab, loadViewArgs: args));
            AssertArgsReleased(args);
            Assert.That(destroyed == null, Is.True);
            Assert.That(destroyed.Module, Is.Null);
            Assert.That(destroyed.Model, Is.Null);
            Assert.That(destroyed.IsLoaded, Is.False);
            Assert.That(destroyed.RemovedCount, Is.EqualTo(1));
            Assert.That(destroyed.UnboundCount, Is.EqualTo(1));
            Assert.That(destroyed.ClosedCount, Is.Zero);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(model.Module, Is.SameAs(_module));
            Assert.That(model.ExitCount, Is.Zero);
            Assert.That(root.activeSelf, Is.False);
            Assert.That(BackendInUse(prefab, root), Is.True);
            yield return null;
            Assert.That(root == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ModelRegistrationChangedDuringLoadCannotCommitTheOldBinding()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCoreModelChangesDuringLoadPrefab");
            var model = _module.LoadModel<YLCoreProbeModel>();
            var args = NewArgs();
            YLCoreSmokeView failed = null;
            YLCoreSmokeView.LoadingProbe = view =>
            {
                failed = Track(view);
                view.BoundModel = model;
                _module.RemoveModel(model);
                _module.AddModel(model);
                Assert.That(view.Model, Is.Null);
            };
            Assert.Throws<InvalidOperationException>(() => _module.LoadView(prefab, loadViewArgs: args));
            AssertArgsReleased(args);
            Assert.That(failed.Module, Is.Null);
            Assert.That(failed.Model, Is.Null);
            Assert.That(failed.IsLoaded, Is.False);
            Assert.That(failed.UnboundCount, Is.EqualTo(1));
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(_module.GetModel<YLCoreProbeModel>(), Is.SameAs(model));
            Assert.That(model.ExitCount, Is.EqualTo(1), "Only the explicit business removal ends a registration.");
            yield return null;
            Assert.That(failed == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ExplicitAttachInitializesInactiveInstanceOnceAndConsumesDuplicateArgs()
        {
            yield return StartModule();
            var view = Template<YLCoreSmokeView>("YLCore.SceneOwned").GetComponent<YLCoreSmokeView>();
            var args = NewArgs();
            args.SetInt("token", 23);
            Assert.That(_module.AttachView(view, args), Is.SameAs(view));
            AssertArgsReleased(args);
            Assert.That(view.IsLoaded, Is.True);
            Assert.That(view.Module, Is.SameAs(_module));
            Assert.That(view.Token, Is.EqualTo(23));
            Assert.That(view.AwakeCount, Is.Zero);
            Assert.That(view.EnabledCount, Is.Zero);
            Assert.That(view.gameObject.activeSelf, Is.False);
            Assert.That(_module.GetView<YLCoreSmokeView>(view.Id), Is.SameAs(view));
            Assert.That(_module.GetViews(view.gameObject.name), Is.Empty, "Scene names are not fabricated resource keys.");
            args = NewArgs();
            _module.AttachView(view, args);
            AssertArgsReleased(args);
            _module.AttachViews(view.gameObject);
            Assert.That(view.LoadedCount, Is.EqualTo(1));
            view.gameObject.SetActive(true);
            Assert.That(view.AwakeCount, Is.EqualTo(1));
            Assert.That(view.EnabledCount, Is.EqualTo(1));
            view.Close();
            Assert.That(view, Is.Not.Null);
            Assert.That(view.gameObject.activeSelf, Is.False);
            Assert.That(view.Module, Is.Null);
            _module.AttachView(view);
            Assert.That(view.LoadedCount, Is.EqualTo(2));
            _module.Close();
            Assert.That(view.RemovedCount, Is.EqualTo(2));
            Assert.That(view, Is.Not.Null, "Caller instances are never discarded or pooled by Core.");
        }

        [UnityTest]
        public IEnumerator HierarchyAttachFailureRollsBackOnlyNewSessionsWithoutHidingExistingViews()
        {
            yield return StartModule();
            var root = Template<YLCoreSmokeView>("YLCore.AttachRoot").GetComponent<YLCoreSmokeView>();
            var existing = Template<YLCoreSmokeView>("YLCore.ExistingChild").GetComponent<YLCoreSmokeView>();
            existing.transform.SetParent(root.transform);
            existing.gameObject.SetActive(true);
            _module.AttachView(existing);
            var oldSession = ViewSession(existing);
            var failing = Template<YLCoreSmokeView>("YLCore.FailingChild").GetComponent<YLCoreSmokeView>();
            failing.transform.SetParent(root.transform);
            var failure = new InvalidOperationException("hierarchy attach");
            failing.LoadedAction = () => throw failure;
            Assert.That(Assert.Throws<InvalidOperationException>(() => _module.AttachViews(root.gameObject)), Is.SameAs(failure));
            Assert.That(root.Module, Is.Null);
            Assert.That(failing.Module, Is.Null);
            Assert.That(root.RemovedCount, Is.EqualTo(1));
            Assert.That(failing.RemovedCount, Is.EqualTo(1));
            Assert.That(root.ClosedCount, Is.Zero);
            Assert.That(root.gameObject.activeSelf, Is.False);
            Assert.That(existing.gameObject.activeSelf, Is.True);
            Assert.That(ViewSession(existing), Is.SameAs(oldSession));
            Assert.That(existing.IsLoaded, Is.True);
            Assert.That(existing.RemovedCount, Is.Zero);
            Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { existing }));
            failing.LoadedAction = null;
            Assert.That(_module.AttachViews(root.gameObject).Count, Is.EqualTo(3));
            Assert.That(existing.LoadedCount, Is.EqualTo(1));
            Assert.That(root.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator HierarchyOwnerConflictIsRejectedBeforeInitializingAnyNewView()
        {
            yield return StartModule();
            var other = new YLCoreConsumerModule();
            try
            {
                var request = ModuleManager.StartUpModule(other);
                while (!request.IsDone)
                {
                    yield return null;
                }

                Assert.That(request.Error, Is.Null.Or.Empty);
                var root = Template<YLCoreSmokeView>("YLCore.ConflictRoot").GetComponent<YLCoreSmokeView>();
                var child = Template<YLCoreSmokeView>("YLCore.ForeignChild").GetComponent<YLCoreSmokeView>();
                child.transform.SetParent(root.transform);
                other.AttachView(child);
                Assert.Throws<InvalidOperationException>(() => _module.AttachView(child));
                Assert.Throws<InvalidOperationException>(() => _module.AttachViews(root.gameObject));
                Assert.That(root.LoadedCount, Is.Zero);
                Assert.That(root.Module, Is.Null);
                Assert.That(child.Module, Is.SameAs(other));
                Assert.That(child.RemovedCount, Is.Zero);
                Assert.That(_module.GetViews<View>(), Is.Empty);
            }
            finally
            {
                if (ReferenceEquals(ModuleManager.GetModule(other.ModuleName), other))
                {
                    other.Close();
                }

                _classPool.Restore(new Module[] { other });
            }
        }

        [UnityTest]
        public IEnumerator LoadedHierarchyIncludesInactiveChildrenAndOnlyRootReturnsToBackend()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCore.NestedPrefab");
            var active = Template<YLCoreSmokeView>("activeChild");
            var inactive = Template<YLCoreSmokeView>("inactiveChild");
            active.transform.SetParent(prefab.transform);
            inactive.transform.SetParent(prefab.transform);
            active.SetActive(true);
            var args = NewArgs();
            args.SetInt("token", 47);
            var root = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: args));
            var child = root.transform.Find("activeChild").GetComponent<YLCoreSmokeView>();
            var dormant = root.transform.Find("inactiveChild").GetComponent<YLCoreSmokeView>();
            Assert.That(_module.GetViews<View>().Count, Is.EqualTo(3));
            Assert.That(root.Token, Is.EqualTo(47));
            Assert.That(child.Token, Is.Zero);
            Assert.That(dormant.Token, Is.Zero);
            Assert.That(dormant.IsLoaded, Is.True);
            Assert.That(dormant.AwakeCount, Is.Zero);
            Assert.That(_module.GetViews(prefab.name), Is.EqualTo(new View[] { root }));
            child.Close();
            Assert.That(root.IsLoaded, Is.True);
            Assert.That(dormant.IsLoaded, Is.True);
            Assert.That(BackendInUse(prefab, root.gameObject), Is.True);
            Assert.That(child.gameObject.activeSelf, Is.False);
            _module.AttachView(child);
            child.gameObject.SetActive(true);
            child.AfterClose = () => Assert.Throws<InvalidOperationException>(() => _module.AttachView(child));
            _module.ClearView();
            child.AfterClose = null;
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(child.RemovedCount, Is.EqualTo(2));
            Assert.That(dormant.RemovedCount, Is.EqualTo(1));
            Assert.That(child.gameObject.activeSelf, Is.True, "Batch root return preserves nested activeSelf.");
            Assert.That(dormant.gameObject.activeSelf, Is.False);
            Assert.That(BackendInUse(prefab, root.gameObject), Is.False);
            Assert.That(Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null)), Is.SameAs(root));
            Assert.That(child.LoadedCount, Is.EqualTo(3));
            Assert.That(dormant.LoadedCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator NestedInitializationOrCleanupFailureDiscardsOnlyAcquiredRootAndEndsAllSessions()
        {
            yield return StartModule();
            var prefab = Template<YLCoreSmokeView>("YLCore.BadNestedPrefab");
            var childTemplate = Template<YLCoreSmokeView>("badChild");
            childTemplate.transform.SetParent(prefab.transform);
            var failure = new InvalidOperationException("nested initialization");
            YLCoreSmokeView failedRoot = null;
            YLCoreSmokeView.LoadingProbe = view =>
            {
                if (!view.transform.parent)
                {
                    failedRoot = Track(view);
                }
                else
                {
                    throw failure;
                }
            };
            Assert.That(Assert.Throws<InvalidOperationException>(() => _module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null)), Is.SameAs(failure));
            YLCoreSmokeView.LoadingProbe = null;
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(failedRoot.Module, Is.Null);
            Assert.That(failedRoot.ClosedCount, Is.Zero);
            Assert.That(BackendInUse(prefab, failedRoot.gameObject), Is.True);
            var root = Track(_module.LoadView<YLCoreSmokeView>(prefab, loadViewArgs: null));
            Assert.That(root, Is.Not.SameAs(failedRoot));
            var child = root.transform.Find("badChild").GetComponent<YLCoreSmokeView>();
            child.RemoveAction = () => throw failure;
            Assert.That(Assert.Throws<InvalidOperationException>(() => child.Close()), Is.SameAs(failure));
            Assert.That(root.IsLoaded, Is.True);
            root.Close();
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(BackendInUse(prefab, root.gameObject), Is.True);
            yield return null;
            Assert.That(root == null && failedRoot == null, Is.True);
        }

        [UnityTest]
        public IEnumerator IndependentlyLoadedNestedRootReturnsSeparatelyWithoutBecomingOuterCachedChild()
        {
            yield return StartModule();
            var outerPrefab = Template<YLCoreSmokeView>("YLCore.OuterPrefab");
            var innerPrefab = Template<YLCoreSmokeView>("YLCore.InnerPrefab");
            var outer = Track(_module.LoadView<YLCoreSmokeView>(outerPrefab, loadViewArgs: null));
            var inner = Track(_module.LoadView<YLCoreSmokeView>(innerPrefab, outer.transform, loadViewArgs: null));
            outer.Close();
            Assert.That(inner.Module, Is.Null);
            Assert.That(inner.RemovedCount, Is.EqualTo(1));
            Assert.That(inner.transform.IsChildOf(outer.transform), Is.False, "Independent checkout must leave the outer cached hierarchy.");
            Assert.That(BackendInUse(innerPrefab, inner.gameObject), Is.False);
            Assert.That(BackendInUse(outerPrefab, outer.gameObject), Is.False);
            Assert.That(Track(_module.LoadView<YLCoreSmokeView>(outerPrefab, loadViewArgs: null)), Is.SameAs(outer));
            Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { outer }));
            Assert.That(Track(_module.LoadView<YLCoreSmokeView>(innerPrefab, loadViewArgs: null)), Is.SameAs(inner));
        }

        [UnityTest]
        public IEnumerator SameNameSceneUnloadCleansOnlyDeadSessionsIncludingNeverActivatedViews()
        {
            yield return StartModule();
            var first = OwnScene("YLCore.SameName");
            var second = OwnScene("YLCore.SecondName");
            second.name = first.name;
            Assert.That(first.handle, Is.Not.EqualTo(second.handle));
            var dead = Template<YLCoreSmokeView>("YLCore.DeadInactive").GetComponent<YLCoreSmokeView>();
            var survivor = Template<YLCoreSmokeView>("YLCore.Survivor").GetComponent<YLCoreSmokeView>();
            SceneManager.MoveGameObjectToScene(dead.gameObject, first);
            SceneManager.MoveGameObjectToScene(survivor.gameObject, second);
            _module.AttachViews(first);
            _module.AttachViews(second);
            Assert.That(dead.AwakeCount, Is.Zero);
            yield return SceneManager.UnloadSceneAsync(first);
            Assert.That(dead == null, Is.True);
            Assert.That(dead.Module, Is.Null, "sceneUnloaded cleans inactive wrappers without query-side pruning.");
            Assert.That(dead.RemovedCount, Is.EqualTo(1));
            Assert.That(survivor.Module, Is.SameAs(_module));
            Assert.That(survivor.IsLoaded, Is.True);
            Assert.That(survivor.RemovedCount, Is.Zero);
            Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { survivor }));
            Assert.Throws<ArgumentException>(() => _module.AttachViews(first));
        }

        [UnityTest]
        public IEnumerator SceneMigrationAndDdolKeepOwnerUntilActualDestructionOrModuleClose()
        {
            yield return StartModule();
            var original = OwnScene("YLCore.Original");
            var destination = OwnScene("YLCore.Destination");
            var migrated = Template<YLCoreSmokeView>("YLCore.Migrated").GetComponent<YLCoreSmokeView>();
            var persistent = Template<YLCoreSmokeView>("YLCore.Persistent").GetComponent<YLCoreSmokeView>();
            SceneManager.MoveGameObjectToScene(migrated.gameObject, original);
            SceneManager.MoveGameObjectToScene(persistent.gameObject, original);
            _module.AttachViews(original);
            SceneManager.MoveGameObjectToScene(migrated.gameObject, destination);
            Object.DontDestroyOnLoad(persistent.gameObject);
            yield return SceneManager.UnloadSceneAsync(original);
            Assert.That(migrated.IsLoaded && persistent.IsLoaded, Is.True);
            Assert.That(migrated.Module, Is.SameAs(_module));
            Assert.That(persistent.Module, Is.SameAs(_module));
            Assert.That(migrated.RemovedCount + persistent.RemovedCount, Is.Zero);
            yield return SceneManager.UnloadSceneAsync(destination);
            Assert.That(migrated.Module, Is.Null);
            Assert.That(migrated.RemovedCount, Is.EqualTo(1));
            Assert.That(persistent.IsLoaded, Is.True);
            _module.Close();
            Assert.That(persistent.Module, Is.Null);
            Assert.That(persistent.RemovedCount, Is.EqualTo(1));
            Assert.That(persistent, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator ExternalDestroyEndsActiveSessionAndQueriesPruneNeverActivatedDestroyedInstance()
        {
            yield return StartModule();
            var active = Template<YLCoreSmokeView>("YLCore.DestroyActive").GetComponent<YLCoreSmokeView>();
            var dormant = Template<YLCoreSmokeView>("YLCore.DestroyInactive").GetComponent<YLCoreSmokeView>();
            _module.AttachView(active);
            _module.AttachView(dormant);
            active.gameObject.SetActive(true);
            int dormantId = dormant.Id;
            var model = _module.LoadModel<YLCoreProbeModel>();
            active.BoundModel = model;
            Object.Destroy(active.gameObject);
            Object.Destroy(dormant.gameObject);
            yield return null;
            yield return null;
            Assert.That(active.Module, Is.Null);
            Assert.That(active.RemovedCount, Is.EqualTo(1));
            Assert.That(dormant.Module, Is.Null, "Never-activated external destruction is pruned without a business query.");
            Assert.That(_module.GetView<YLCoreSmokeView>(dormantId), Is.Null);
            Assert.That(dormant.Module, Is.Null);
            Assert.That(dormant.RemovedCount, Is.EqualTo(1));
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(model.ExitCount, Is.Zero);
            Assert.That(model.Module, Is.SameAs(_module));
            active.Close();
            dormant.Close();
            Assert.That(active.RemovedCount + dormant.RemovedCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator StartingAttachFailureUsesModuleRollbackWithoutClosingSuccessfulNestedOwner()
        {
            var inner = new YLCoreConsumerModule();
            var outerView = Template<YLCoreSmokeView>("YLCore.OuterSceneView").GetComponent<YLCoreSmokeView>();
            var innerView = Template<YLCoreSmokeView>("YLCore.InnerSceneView").GetComponent<YLCoreSmokeView>();
            var failure = new InvalidOperationException("starting attach");
            Module nestedOwner = null;
            _module.StartAction = () =>
            {
                var innerStart = ModuleManager.StartUpModule(inner);
                Assert.That(innerStart.IsDone, Is.True);
                Assert.That(innerStart.Error, Is.Null.Or.Empty);
                inner.AttachView(innerView);
                nestedOwner = innerView.Module;
                _module.AttachView(outerView);
                var failing = Template<YLCoreSmokeView>("YLCore.StartingFailView").GetComponent<YLCoreSmokeView>();
                failing.LoadedAction = () => throw failure;
                _module.AttachView(failing);
            };
            try
            {
                var request = ModuleManager.StartUpModule(_module);
                while (!request.IsDone)
                {
                    yield return null;
                }

                Assert.That(request.Exception, Is.SameAs(failure));
                Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(_module.GetViews<View>(), Is.Empty);
                Assert.That(outerView.Module, Is.Null);
                Assert.That(outerView.RemovedCount, Is.EqualTo(1));
                Assert.That(innerView.Module, Is.SameAs(nestedOwner));
                Assert.That(innerView.IsLoaded, Is.True);
                Assert.That(innerView.RemovedCount, Is.Zero);
                Assert.That(inner.State, Is.EqualTo(ModuleState.Running));
            }
            finally
            {
                if (ReferenceEquals(ModuleManager.GetModule(inner.ModuleName), inner))
                {
                    inner.Close();
                }

                _classPool.Restore(new Module[] { inner });
            }
        }

        private Scene OwnScene(string name)
        {
            var scene = SceneManager.CreateScene(name);
            _scenes.Add(scene);
            return scene;
        }

        private static void InvokeViewInstance(string name, View view)
        {
            try
            {
                typeof(View).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private LoadViewArgs NewArgs()
        {
            var args = LoadViewArgs.New();
            _classPool.Track(args);
            return args;
        }

        private static void AssertArgsReleased(LoadViewArgs args)
        {
            Assert.Throws<ObjectDisposedException>(() => args.GetObject("retained"));
            Assert.Throws<ObjectDisposedException>(() => args.SetInt("token", 1));
            foreach (var field in typeof(LoadViewArgs).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.GetValue(args) is IDictionary values)
                {
                    Assert.That(values.Count, Is.Zero, field.Name);
                }
            }
        }

        private static bool BackendInUse(GameObject prefab, GameObject instance)
        {
            var loader = Object.FindFirstObjectByType<YLFramework.Pooling.GameObjectPool>();
            if (!loader)
            {
                return true;
            }

            var pools = (IDictionary)loader.GetType().GetField("_pools", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(loader);
            var pool = (YLFramework.Pooling.GameObjectPoolData)pools["YL.Core.Direct/" + prefab.GetInstanceID()];
            return pool == null || !pool.PoolQueue.Contains(instance);
        }

        [UnityTest]
        public IEnumerator ViewDisableKeepsExplicitTimerScopeAndCloseOnlyDisposesItsOwnSession()
        {
            var driverType = typeof(YLFramework.Timing.Timer).Assembly.GetType("YLFramework.Timing.Internal.TimerDriver", true);
            Assert.That(Object.FindObjectsByType(driverType, FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty,
                "不接管已有 TimerDriver。");
            yield return StartModule();
            using var scope = new TimerScope();
            Component ownedHost = null;
            TimerHandle global = default;
            try
            {
                var view = Template<YLCoreSmokeView>("YLTimer.ViewSession").GetComponent<YLCoreSmokeView>();
                _module.AttachView(view);
                view.gameObject.SetActive(true);
                var cancelled = 0;
                view.RemoveAction = scope.Dispose;
                var scoped = scope.PostNamed("view", 60, onCancelled: _ => cancelled++);
                var completed = 0;
                global = YLFramework.Timing.Timer.DelayFrames(4, () => completed++);
                ownedHost = (Component)Object.FindObjectsByType(driverType, FindObjectsInactive.Include, FindObjectsSortMode.None)[0];
                view.gameObject.SetActive(false);
                yield return null;
                Assert.That(view.IsLoaded, Is.True, "临时停用不是表现会话结束。");
                Assert.That(scoped.IsActive, Is.True);
                Assert.That(scope.IsDisposed, Is.False);
                Assert.That(cancelled, Is.Zero);
                view.gameObject.SetActive(true);
                view.Close();
                Assert.That(scoped.Status, Is.EqualTo(TimerState.Cancelled));
                Assert.That(scope.IsDisposed, Is.True);
                Assert.That(cancelled, Is.EqualTo(1));
                Assert.That(global.IsActive, Is.True, "View.Close 不清外部全局 Timer。");
                using var nextScope = new TimerScope();
                _module.AttachView(view);
                view.gameObject.SetActive(true);
                view.RemoveAction = nextScope.Dispose;
                var next = nextScope.PostNamed("view", 60);
                Assert.That(next.IsActive, Is.True);
                _module.Close();
                Assert.That(next.Status, Is.EqualTo(TimerState.Cancelled));
                Assert.That(cancelled, Is.EqualTo(1), "旧 Scope 不参与重启后的 View 会话。");
                for (var frame = 0; frame < 8 && completed == 0; frame++)
                {
                    yield return null;
                }

                Assert.That(global.Status, Is.EqualTo(TimerState.Completed));
                Assert.That(completed, Is.EqualTo(1));
            }
            finally
            {
                global.Cancel();
                if (ownedHost)
                {
                    Object.Destroy(ownedHost.gameObject);
                }
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyUsesConfiguredPriorityAndStableFrontSequence()
        {
            yield return StartModule();
            var other = new YLCoreConsumerModule();
            try
            {
                var request = ModuleManager.StartUpModule(other);
                while (!request.IsDone)
                {
                    yield return null;
                }

                Assert.That(request.Error, Is.Null.Or.Empty);
                var root = PolicyRoot("YLPolicy.SharedRoot");
                var prefab = Template<YLCoreSmokePanel>("YLPolicy.Order", true);
                var high = Track(other.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { Priority = 210, CoversRoot = true, InstanceMode = PanelInstanceMode.Multiple }));
                var low = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { Priority = 50, CoversRoot = true, InstanceMode = PanelInstanceMode.Multiple }));
                Assert.That(high.Canvas.sortingOrder, Is.GreaterThan(low.Canvas.sortingOrder));
                Assert.That(high.IsTop(), Is.True);
                Assert.That(low.IsTop(), Is.False);
                Assert.That(PanelManager.GetTopPanel(root), Is.SameAs(high));
                Assert.That(high.IsVisible, Is.True);
                Assert.That(low.IsVisible, Is.False, "跨 Module 的固定 Priority 高于接受顺序。");
                low.BringToFront();
                Assert.That(high.IsTop(), Is.True, "BringToFront 不跨固定 Priority。");
                var third = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                var stable = high.Canvas.sortingOrder;
                Assert.That(third.IsVisible, Is.False);
                third.Close();
                int visibleAtActivation = 0, hiddenAfterTransition = 0;
                YLCoreSmokePanel.VisibleProbe = _ => visibleAtActivation++;
                third = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
                Assert.That(third.IsVisible, Is.False, "低优先级窗口首开即受覆盖，不能短暂发布 Visible。");
                Assert.That(visibleAtActivation, Is.Zero);
                YLCoreSmokePanel.VisibleProbe = null;
                YLCoreSmokePanel.InvisibleProbe = panel =>
                {
                    if (panel == third)
                    {
                        hiddenAfterTransition++;
                    }
                };
                third.UpdateDisplay(new PanelOverrides { Priority = 220 });
                third.UpdateDisplay(new PanelOverrides { Priority = 0 });
                Assert.That(hiddenAfterTransition, Is.EqualTo(1));
                YLCoreSmokePanel.InvisibleProbe = null;
                Assert.That(high.Canvas.sortingOrder, Is.EqualTo(stable), "重复重排不累加生成顺序。");
                low.UpdateDisplay(new PanelOverrides { Modal = true, CoversRoot = false });
                Assert.That(low.Canvas.sortingOrder, Is.GreaterThan(high.Canvas.sortingOrder), "固定 Priority 和置顶不能跨模态分组。");
                Assert.That(high.IsInputBlocked, Is.True);
                Assert.That(low.HasFocus, Is.True);
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
                if (ReferenceEquals(ModuleManager.GetModule(other.ModuleName), other))
                {
                    other.Close();
                }

                _classPool.Restore(new Module[] { other });
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyIsolatesIndependentRootsAndControlsNestedWindowCanvas()
        {
            yield return StartModule();
            try
            {
                var rootA = PolicyRoot("YLPolicy.RootA");
                var rootB = PolicyRoot("YLPolicy.RootB");
                var container = new GameObject("YLPolicy.Container", typeof(RectTransform));
                _objects.Add(container);
                container.transform.SetParent(rootB.transform, false);
                var prefab = Template<YLCoreSmokePanel>("YLPolicy.RootWindow", true);
                var a = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootA,
                    overrides: new PanelOverrides { CoversRoot = true }));
                var obj = Object.Instantiate(prefab, container.transform, false);
                var b = Track(_module.AttachView(obj.GetComponent<YLCoreSmokePanel>()));
                obj.SetActive(true);
                Assert.That(b.transform.parent, Is.SameAs(container.transform), "Attach 原位，不偷偷使用 LoadPanel 的层容器。");
                Assert.That(b.Root, Is.SameAs(rootB));
                Assert.That(a.Canvas.enabled, Is.True, "A different effective root cannot occlude this Panel.");
                Assert.That(b.Canvas, Is.SameAs(b.GetComponent<Canvas>()), "A nested root container is not another Panel's Canvas.");
                Assert.That(rootA.GetComponent<Canvas>().enabled, Is.True);
                Assert.That(rootB.GetComponent<Canvas>().enabled, Is.True);
                Assert.That(a.IsTop(), Is.True);
                Assert.That(b.IsTop(), Is.True);
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyAcceptsPresetCanvasAndRejectsOverflowBeforeMutation()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.PresetRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.Preset", true);
            prefab.AddComponent<Canvas>();
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Priority = int.MaxValue }));
            Assert.That(panel.EffectiveSettings.priority, Is.EqualTo(int.MaxValue), "业务 Priority 不受 Canvas 的 short 排序空间限制。");
            var sibling = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Priority = int.MaxValue }));
            Assert.That(sibling.Canvas.sortingOrder, Is.GreaterThan(panel.Canvas.sortingOrder));
            Assert.That(sibling.IsTop(), Is.True, "同 Priority 按接受顺序排序，而非 Dictionary 顺序。");
            root.Canvas.sortingOrder = short.MaxValue - 2;
            panel.UpdateDisplay(default);
            var previous = panel.SortingOrder;
            var display = sibling.SortingOrder;
            var parent = panel.transform.parent;
            Assert.Throws<ArgumentOutOfRangeException>(() => panel.UpdateDisplay(new PanelOverrides { Modal = true, Layer = 1 }));
            Assert.That(panel.SortingOrder, Is.EqualTo(previous));
            Assert.That(panel.EffectiveSettings.modal, Is.False);
            Assert.That(panel.transform.parent, Is.SameAs(parent));
            Assert.That(sibling.SortingOrder, Is.EqualTo(display));
            panel.Close();
            var retired = panel.SortingOrder;
            Assert.DoesNotThrow(() =>
            {
                panel.Show();
                panel.BringToFront();
                panel.UpdateDisplay(new PanelOverrides { Priority = int.MinValue });
            });
            Assert.That(panel.SortingOrder, Is.EqualTo(retired));
            Assert.That(panel.IsVisible, Is.False, "已结束会话不能复活缓存对象。");
            Assert.That(sibling.IsLoaded && sibling.IsVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator PanelPolicyModalStackRestoresOnlyEffectiveFocusAndIgnoresOverlay()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.ModalRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.Modal", true);
            var content = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Modal = true }));
            var second = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Modal = true }));
            var overlay = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 1, ParticipatesInTop = false }));
            Assert.That(content.IsVisible, Is.True, "Modal does not imply full-screen rendering occlusion.");
            Assert.That(content.IsInputBlocked, Is.True);
            Assert.That(first.IsInputBlocked, Is.True);
            Assert.That(second.HasFocus, Is.True);
            Assert.That(PanelManager.GetTopPanel(root), Is.SameAs(second));
            second.gameObject.SetActive(false);
            Assert.That(first.HasFocus, Is.True);
            Assert.That(PanelManager.GetModalPanel(content.Root), Is.SameAs(first));
            first.Close();
            Assert.That(content.HasFocus, Is.True);
            Assert.That(content.IsInputBlocked, Is.False);
            second.gameObject.SetActive(true);
            Assert.That(second.HasFocus, Is.True);
            second.Close();
            Assert.That(content.HasFocus, Is.True);
            Assert.That(overlay.HasFocus, Is.False);
            PanelManager.CullCoveredPanels = false;
            try
            {
                first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Modal = true }));
                Assert.That(content.IsInputBlocked, Is.True);
                first.enabled = false;
                first.gameObject.SetActive(false);
                yield return null;
                yield return null;
                Assert.That(content.HasFocus, Is.True, "Occlusion switch cannot disable modal/focus lifecycle maintenance.");
                Assert.That(content.IsInputBlocked, Is.False);
            }
            finally
            {
                PanelManager.CullCoveredPanels = true;
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyChildOwnsCanvasAndDoesNotInheritParentCoverage()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.BorrowRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.BorrowOwner", true);
            var owner = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true }));
            var childObject = new GameObject("YLPolicy.BorrowedChild", typeof(RectTransform));
            _objects.Add(childObject);
            childObject.transform.SetParent(owner.transform, false);
            var child = _module.AttachView(childObject.AddComponent<YLCoreSmokePanel>());
            Assert.That(child.Canvas, Is.Not.SameAs(owner.Canvas));
            Assert.That(child.Root, Is.SameAs(root));
            Assert.That(child.EffectiveSettings.coversRoot, Is.False);
            child.UpdateDisplay(new PanelOverrides { Priority = int.MaxValue });
            Assert.That(child.IsTop(), Is.True);
            var covering = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 1, CoversRoot = true }));
            Assert.That(owner.IsVisible, Is.False);
            Assert.That(child.IsVisible, Is.False, "独立子 Panel 仍受同根实际更高的 CoversRoot 遮挡。");
            Assert.That(child.IsInputBlocked, Is.True);
            child.Close();
            Assert.That(root.GetComponent<Canvas>().enabled, Is.True);
            Assert.That(covering.Canvas.enabled, Is.True);
            covering.Close();
            Assert.That(owner.Canvas.enabled, Is.True);
            Assert.That(owner.IsLoaded, Is.True);
        }

        [UnityTest]
        public IEnumerator FirstInactiveRootPreparesWithoutOnEnableAndResumesTheSameSession()
        {
            yield return StartModule();
            var host = new GameObject("YLPanel.FirstInactiveRoot", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
            host.SetActive(false);
            _objects.Add(host);
            var root = CorePanelTestRoots.Create(host, activate: false);
            Assert.That(typeof(UIRootContext).GetField("canvas", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(root), Is.Null,
                "必须覆盖从未 OnEnable 的根，不能以先激活再停用代替首次 inactive 使用。");
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLPanel.InitiallyInactive", true), root: root));
            var session = ViewSession(panel);
            Assert.That(panel.IsLoaded, Is.True);
            Assert.That(panel.Root, Is.SameAs(root));
            Assert.That(panel.Canvas, Is.Not.Null);
            Assert.That(panel.transform.parent, Is.SameAs(root.GetLayer(0)));
            Assert.That(panel.SortingOrder, Is.GreaterThan(root.Canvas.sortingOrder));
            Assert.That(host.activeSelf, Is.False);
            Assert.That(panel.IsVisible, Is.False);
            Assert.That(panel.IsInputBlocked, Is.True);
            host.SetActive(true);
            yield return null;
            Assert.That(panel.IsVisible && !panel.IsInputBlocked, Is.True);
            host.SetActive(false);
            yield return null;
            Assert.That(panel.IsLoaded && panel.IsInputBlocked, Is.True);
            host.SetActive(true);
            yield return null;
            Assert.That(ViewSession(panel), Is.SameAs(session));
            Assert.That(panel.LoadedCount, Is.EqualTo(1));
            Assert.That(panel.OpenCount, Is.EqualTo(1));
            var manager = typeof(PanelManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var ownedSystem = (GameObject)typeof(PanelManager).GetField("_eventSystem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Object.DestroyImmediate(host.GetComponent<UnityEngine.UI.GraphicRaycaster>());
            yield return null;
            yield return null;
            Assert.That(panel.Module, Is.Null);
            Assert.That(panel.IsLoaded, Is.False);
            Assert.That(host.GetComponent<UnityEngine.UI.GraphicRaycaster>(), Is.Null, "必需组件损坏不得被运行时自修复。");
            if (!ReferenceEquals(ownedSystem, null))
            {
                Assert.That(ownedSystem == null, Is.True, "最后有效根损坏后不得仅因其 Canvas 仍启用而保留自建输入系统。");
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyCustomSceneRootsEndLocallyAndPersistentRootIsIndependent()
        {
            yield return StartModule();
            var previousScene = SceneManager.GetActiveScene();
            var previousHook = PanelManager.CanvasCreated;
            var sceneA = OwnScene("YLPolicy.SceneA");
            var sceneB = OwnScene("YLPolicy.SceneB");
            var system = new GameObject("YLPolicy.OwnEventSystem", typeof(UnityEngine.EventSystems.EventSystem));
            _objects.Add(system);
            int defaultCreations = 0;
            PanelManager.CanvasCreated = _ => defaultCreations++;
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.ScenePanel", true);
            var persistent = PolicyRoot("YLPolicy.PersistentRoot");
            Object.DontDestroyOnLoad(persistent.gameObject);
            try
            {
                SceneManager.SetActiveScene(sceneA);
                var rootA = PolicyRoot("YLPolicy.SceneRootA");
                SceneManager.SetActiveScene(sceneB);
                var rootB = PolicyRoot("YLPolicy.SceneRootB");
                Assert.That(rootA, Is.Not.SameAs(rootB));
                Assert.That(rootA.gameObject.scene, Is.EqualTo(sceneA));
                Assert.That(rootB.gameObject.scene, Is.EqualTo(sceneB));
                SceneManager.SetActiveScene(sceneA);
                _module.PanelRoot = rootA;
                var a = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab));
                _module.PanelRoot = rootB;
                var b = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab));
                var d = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: persistent,
                    overrides: new PanelOverrides { Priority = 100, CoversRoot = true }));
                Assert.That(a.Root, Is.SameAs(rootA), "重新绑定只影响之后接受的请求。");
                Assert.That(b.Root, Is.SameAs(rootB));
                Assert.That(d.Root, Is.SameAs(persistent));
                Assert.That(defaultCreations, Is.Zero, "自定义根登记不能抢占或创建宿主默认根。");
                Assert.That(a.Canvas.enabled && b.Canvas.enabled && d.Canvas.enabled, Is.True);
                Assert.That(a.IsTop() && b.IsTop() && d.IsTop(), Is.True);
                SceneManager.SetActiveScene(previousScene);
                yield return SceneManager.UnloadSceneAsync(sceneA);
                Assert.That(a == null, Is.True);
                Assert.That(a.IsTop(), Is.False, "Unity fake-null cannot compare equal to an empty Top query.");
                Assert.That(a.HasFocus, Is.False);
                Assert.That(b.IsLoaded && b.IsVisible && d.IsLoaded && d.IsVisible, Is.True);
                Assert.That(b.Module, Is.SameAs(_module));
                Assert.That(d.Module, Is.SameAs(_module));
                _module.Close();
                Assert.That(persistent != null, Is.True, "Module 退出不销毁共享根。");
            }
            finally
            {
                if (previousScene.IsValid() && previousScene.isLoaded)
                {
                    SceneManager.SetActiveScene(previousScene);
                }

                PanelManager.CanvasCreated = previousHook;
                _module.PanelRoot = null;
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyReparentingAcrossRootsEndsOriginalSessionWithoutMigratingIdentity()
        {
            yield return StartModule();
            var rootA = PolicyRoot("YLPolicy.MovingRootA");
            var rootB = PolicyRoot("YLPolicy.MovingRootB");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.Moving", true);
            var moving = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootA));
            var other = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootB));
            var session = ViewSession(moving);
            moving.transform.SetParent(rootB.GetLayer(0), false);
            Object.DestroyImmediate(rootA.gameObject);
            yield return null;
            Assert.That(moving.IsVisible, Is.False);
            Assert.That(moving.Module, Is.Null);
            Assert.That(moving.Root, Is.Null);
            Assert.That(ViewSession(moving), Is.Not.SameAs(session));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EqualTo(new[] { other }));
            Assert.That(other.IsTop() && other.IsVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator PanelPolicySortingFailureInsideOwnInitializationStillRollsBack()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.InitializationRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.Initialization", true);
            var failure = new InvalidOperationException("YLPolicy.InitializationSortingFailure");
            YLCoreSmokePanel prepared = null;
            var args = NewArgs();
            args.SetObject("retained", _module);
            root.Canvas.sortingOrder = 98;
            YLCoreSmokePanel.SortingProbe = (_, order) =>
            {
                if (order == 100)
                {
                    throw failure;
                }
            };
            YLCoreSmokePanel.LoadingProbe = panel =>
            {
                prepared = Track(panel);
                panel.UpdateDisplay(new PanelOverrides { Modal = true });
            };
            try
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab, args, root)), Is.SameAs(failure));
                Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty);
                Assert.That(prepared.IsLoaded, Is.False);
                Assert.That(prepared.RemovedCount, Is.EqualTo(1));
                AssertArgsReleased(args);
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyNestedInitializationDoesNotTreatOuterSortingObserverAsChildFailure()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.NestedInitRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.NestedInit", true);
            YLCoreSmokePanel outer = null;
            YLCoreSmokePanel child = null;
            YLCoreSmokePanel.SortingProbe = (panel, order) =>
            {
                if (panel == outer && order > 1)
                {
                    throw new InvalidOperationException("YLPolicy.OuterObserver");
                }
            };
            YLCoreSmokePanel.LoadingProbe = panel =>
            {
                if (outer == null)
                {
                    outer = Track(panel);
                    LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: YLPolicy.OuterObserver"));
                    child = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                        overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Priority = -100 }));
                }
            };
            try
            {
                var loaded = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
                Assert.That(loaded, Is.SameAs(outer));
                Assert.That(outer.IsLoaded && child.IsLoaded, Is.True);
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator DisplayLayerMovePreservesLocalRectParametersAcrossDifferentContainers() => MoveAcrossDifferentLayerLayouts(false);
        [UnityTest]
        public IEnumerator ReopenLayerMovePreservesLocalRectParametersAcrossDifferentContainers() => MoveAcrossDifferentLayerLayouts(true);
        [UnityTest]
        public IEnumerator BusinessGroupOnPanelRootPreservesLocalParametersDuringLayerMove() => MoveAcrossDifferentLayerLayouts(false, true);
        [UnityTest]
        public IEnumerator BusinessGroupOnPanelRootPreservesLocalParametersDuringLayerReopen() => MoveAcrossDifferentLayerLayouts(true, true);

        private IEnumerator MoveAcrossDifferentLayerLayouts(bool reopen, bool businessGate = false)
        {
            yield return StartModule();
            var root = PolicyRoot("YLPanel.DifferentLayerLayouts");
            var target = (RectTransform)root.GetLayer(1);
            target.anchorMin = new Vector2(.2f, .1f);
            target.anchorMax = new Vector2(.85f, .75f);
            target.pivot = new Vector2(.7f, .3f);
            target.anchoredPosition = new Vector2(50, -30);
            target.sizeDelta = new Vector2(-60, -40);
            var prefab = Template<YLCoreSmokePanel>("YLPanel.DifferentLayerPanel", true);
            if (businessGate)
            {
                prefab.AddComponent<CanvasGroup>().alpha = .6f;
                prefab.AddComponent<UnityEngine.UI.Button>();
            }
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            var rect = (RectTransform)panel.transform;
            rect.anchorMin = new Vector2(.1f, .3f);
            rect.anchorMax = new Vector2(.6f, .8f);
            rect.pivot = new Vector2(.4f, .7f);
            rect.anchoredPosition = new Vector2(17, 29);
            rect.sizeDelta = new Vector2(170, 80);
            rect.localScale = new Vector3(.8f, .9f, 1);
            var session = ViewSession(panel);
            var changes = new PanelOverrides { Layer = 1, Priority = 23, Modal = true };
            if (reopen)
            {
                Assert.That(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root, overrides: changes), Is.SameAs(panel));
            }
            else
            {
                panel.UpdateDisplay(changes);
            }

            var placement = businessGate ? panel.transform.parent.parent : panel.transform;
            Assert.That(placement.parent, Is.SameAs(target));
            Assert.That(panel.Root, Is.SameAs(root));
            Assert.That(ViewSession(panel), Is.SameAs(session));
            Assert.That(panel.EffectiveSettings.layer, Is.EqualTo(1));
            Assert.That(panel.EffectiveSettings.modal, Is.True);
            Assert.That(rect.anchorMin, Is.EqualTo(new Vector2(.1f, .3f)));
            Assert.That(rect.anchorMax, Is.EqualTo(new Vector2(.6f, .8f)));
            Assert.That(rect.pivot, Is.EqualTo(new Vector2(.4f, .7f)));
            Assert.That(rect.anchoredPosition, Is.EqualTo(new Vector2(17, 29)));
            Assert.That(rect.sizeDelta, Is.EqualTo(new Vector2(170, 80)));
            Assert.That(rect.localScale, Is.EqualTo(new Vector3(.8f, .9f, 1)));
            var before = panel.EffectiveSettings;
            Assert.Throws<ArgumentOutOfRangeException>(() => panel.UpdateDisplay(new PanelOverrides { Layer = 9, Modal = false }));
            Assert.That(panel.EffectiveSettings, Is.EqualTo(before));
            Assert.That(placement.parent, Is.SameAs(target));
            if (businessGate)
            {
                Assert.That(panel.GetComponents<CanvasGroup>(), Has.Length.EqualTo(1));
                Assert.That(panel.GetComponent<CanvasGroup>().alpha, Is.EqualTo(.6f));
            }
        }

        [UnityTest]
        public IEnumerator PanelPolicyLayerAndSortingFailuresPreserveAllDisplayState()
        {
            yield return StartModule();
            var rootA = PolicyRoot("YLPolicy.AtomicRootA");
            var rootB = PolicyRoot("YLPolicy.AtomicRootB");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.Atomic", true);
            var a = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootA));
            var b = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootB));
            var rect = (RectTransform)a.transform;
            rect.anchorMin = new Vector2(.2f, .3f);
            rect.anchorMax = new Vector2(.7f, .9f);
            rect.anchoredPosition = new Vector2(23, 47);
            rect.sizeDelta = new Vector2(140, 90);
            a.UpdateDisplay(new PanelOverrides { Layer = 1 });
            Assert.That(a.transform.parent, Is.SameAs(rootA.GetLayer(1)));
            Assert.That(rect.anchorMin, Is.EqualTo(new Vector2(.2f, .3f)));
            Assert.That(rect.anchorMax, Is.EqualTo(new Vector2(.7f, .9f)));
            Assert.That(rect.anchoredPosition, Is.EqualTo(new Vector2(23, 47)));
            Assert.That(rect.sizeDelta, Is.EqualTo(new Vector2(140, 90)));
            var settings = a.EffectiveSettings;
            var parent = a.transform.parent;
            var otherOrder = b.SortingOrder;
            a.Hide();
            Assert.Throws<ArgumentOutOfRangeException>(() => a.UpdateDisplay(new PanelOverrides { Layer = 9, Priority = 19, CoversRoot = true }));
            Assert.That(a.EffectiveSettings, Is.EqualTo(settings));
            Assert.That(a.transform.parent, Is.SameAs(parent));
            Assert.That(a.IsVisible, Is.False);
            rootA.Canvas.sortingOrder = short.MaxValue - 1;
            a.UpdateDisplay(default);
            var order = a.SortingOrder;
            Assert.Throws<ArgumentOutOfRangeException>(() => a.UpdateDisplay(new PanelOverrides { Layer = 0, Modal = true, Priority = 29 }));
            Assert.That(a.EffectiveSettings, Is.EqualTo(settings));
            Assert.That(a.transform.parent, Is.SameAs(parent));
            Assert.That(a.SortingOrder, Is.EqualTo(order));
            Assert.That(a.IsVisible, Is.False);
            Assert.That(b.SortingOrder, Is.EqualTo(otherOrder));
            Assert.That(b.IsVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator PanelPolicyLiveChildReparentWithinRootKeepsIndependentCanvasAndSettings()
        {
            yield return StartModule();
            var rootA = PolicyRoot("YLPolicy.ChildMovingRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.BorrowMoving", true);
            var a = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootA,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            var b = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootA,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            var obj = new GameObject("YLPolicy.LiveBorrowed", typeof(RectTransform));
            _objects.Add(obj);
            obj.transform.SetParent(a.transform, false);
            var child = _module.AttachView(obj.AddComponent<YLCoreSmokePanel>());
            var canvas = child.Canvas;
            var session = ViewSession(child);
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: rootA,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Modal = true }));
            obj.transform.SetParent(b.transform, false);
            yield return null;
            yield return null;
            Assert.That(child.Canvas, Is.SameAs(canvas).And.Not.SameAs(b.Canvas));
            Assert.That(child.Root, Is.SameAs(rootA));
            Assert.That(ViewSession(child), Is.SameAs(session));
            Assert.That(child.EffectiveSettings.modal, Is.False);
            Assert.That(child.IsVisible, Is.True);
            Assert.That(child.IsInputBlocked, Is.True);
            Assert.That(a.Canvas.enabled && b.Canvas.enabled, Is.True);
            modal.Close();
            Assert.That(child.IsInputBlocked, Is.False);
        }

        [UnityTest]
        public IEnumerator PanelPolicyHighModalPriorityDoesNotConsumeGeneratedSortingSpace()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.PriorityGroupRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.PriorityGroup", true);
            var content = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Modal = true, Priority = int.MaxValue }));
            var next = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            Assert.That(next.SortingOrder, Is.GreaterThan(content.SortingOrder));
            Assert.That(modal.SortingOrder, Is.GreaterThan(next.SortingOrder));
            Assert.That(modal.EffectiveSettings.priority, Is.EqualTo(int.MaxValue));
            Assert.That(modal.SortingOrder, Is.LessThan(10));
            Assert.That(next.IsInputBlocked && content.IsInputBlocked, Is.True);
        }

        [UnityTest]
        public IEnumerator PanelPolicyConfiguredLayerWinsOverPriorityAndUsesRootSortingLayer()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.SortingLayerRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.DefaultLayer", true);
            var low = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { Priority = int.MaxValue, CoversRoot = true }));
            var layeredPrefab = Template<YLCoreSmokePanel>("YLPolicy.UpperLayer", true);
            var layer = SortingLayer.layers.FirstOrDefault(candidate =>
                SortingLayer.GetLayerValueFromID(candidate.id) > SortingLayer.GetLayerValueFromID(0)).id;
            Assert.That(SortingLayer.GetLayerValueFromID(layer), Is.GreaterThan(SortingLayer.GetLayerValueFromID(0)),
                "使用工程已有 SortingLayer，不修改 TagManager。");
            root.Canvas.renderMode = RenderMode.WorldSpace;
            root.Canvas.sortingLayerID = layer;
            Assert.That(root.Canvas.sortingLayerID, Is.EqualTo(layer), "先确认原生根接受 SortingLayer，不能用忽略该属性的屏幕空间根作输入。");
            layeredPrefab.AddComponent<Canvas>().sortingLayerID = 0;
            var high = Track(_module.LoadPanel<YLCoreSmokePanel>(layeredPrefab, root: root,
                overrides: new PanelOverrides { Layer = 1, Priority = int.MinValue, CoversRoot = true }));
            Assert.That(high.IsTop(), Is.True);
            Assert.That(low.IsVisible, Is.False);
            Assert.That(high.IsVisible, Is.True);
            var next = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLPolicy.NextLayer", true), root: root,
                overrides: new PanelOverrides { Priority = int.MaxValue }));
            Assert.That(next.SortingOrder, Is.GreaterThan(low.SortingOrder).And.LessThan(high.SortingOrder));
            Assert.That(low.Canvas.sortingLayerID, Is.EqualTo(layer));
            Assert.That(high.Canvas.sortingLayerID, Is.EqualTo(layer));
            Assert.That(PanelManager.GetTopPanel(root), Is.SameAs(high));
        }

        [UnityTest]
        public IEnumerator PanelPolicyExplicitNestedRootsRemainIndependentWithinOneOuterCanvas()
        {
            yield return StartModule();
            var bare = new GameObject("YLPolicy.BareCanvas", typeof(RectTransform), typeof(Canvas));
            _objects.Add(bare);
            var barePanel = new GameObject("YLPolicy.BarePanel", typeof(RectTransform), typeof(YLCoreSmokePanel));
            barePanel.transform.SetParent(bare.transform, false);
            Assert.Throws<InvalidOperationException>(() => _module.AttachView(barePanel.GetComponent<YLCoreSmokePanel>()));
            Assert.That(barePanel != null, Is.True, "拒绝裸 Canvas 不销毁调用方的外部对象。");
            Assert.That(bare.GetComponent<UIRootContext>(), Is.Null, "Attach 不自动补上下文。");
            var outer = PolicyRoot("YLPolicy.OuterRoot");
            var firstRoot = PolicyRoot("YLPolicy.ExplicitFirst");
            var secondRoot = PolicyRoot("YLPolicy.ExplicitSecond");
            firstRoot.transform.SetParent(outer.transform, false);
            secondRoot.transform.SetParent(outer.transform, false);
            PanelManager.RegisterRoot(firstRoot);
            PanelManager.RegisterRoot(secondRoot);
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.ExplicitRootPanel", true);
            var a = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: firstRoot, overrides: new PanelOverrides { CoversRoot = true }));
            var b = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: secondRoot, overrides: new PanelOverrides { CoversRoot = true }));
            Assert.That(a.Root, Is.SameAs(firstRoot));
            Assert.That(b.Root, Is.SameAs(secondRoot));
            Assert.That(a.IsVisible && b.IsVisible && a.IsTop() && b.IsTop(), Is.True);
            Assert.That(outer.GetComponent<Canvas>().enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator PanelPolicySortingObserverFailureContinuesOtherNotificationsAfterCommit()
        {
            yield return StartModule();
            var root = PolicyRoot("YLPolicy.SortObserverRoot");
            var prefab = Template<YLCoreSmokePanel>("YLPolicy.SortObserver", true);
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Priority = 100 }));
            var second = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Priority = 100 }));
            int following = 0;
            YLCoreSmokePanel.SortingProbe = (panel, _) =>
            {
                if (panel == first)
                {
                    Assert.That(second.Canvas.sortingOrder, Is.EqualTo(root.Canvas.sortingOrder + 3), "All Canvas orders must be committed before notifying business code.");
                    throw new InvalidOperationException("YLPolicy.SortObserverFailure");
                }
                if (panel == second)
                {
                    following++;
                }
            };
            try
            {
                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: YLPolicy.SortObserverFailure"));
                var third = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
                Assert.That(following, Is.EqualTo(1));
                Assert.That(first.IsLoaded && second.IsLoaded && third.IsLoaded, Is.True);
                Assert.That(PanelManager.GetTopPanel(root), Is.SameAs(second));
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator PanelEntryReopensSingleSessionWithoutReloadAndCloseEndsIdentity()
        {
            yield return StartModule();
            var root = PolicyRoot("YLWindow.UniqueRoot");
            var prefab = Template<YLCoreSmokePanel>("YLWindow.Unique", true);
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            Assert.That(first.gameObject.activeSelf && first.IsVisible, Is.True, "Open must show even an initially inactive prefab.");
            var session = ViewSession(first);
            var again = _module.LoadPanel<YLCoreSmokePanel>(prefab, root: root);
            Assert.That(again, Is.SameAs(first));
            Assert.That(ViewSession(again), Is.SameAs(session));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Has.Count.EqualTo(1));
            Assert.That(first.LoadedCount, Is.EqualTo(1));
            Assert.That(first.OpenCount, Is.EqualTo(2));
            first.Close();
            Assert.That(first.Module, Is.Null);
            var reopened = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            Assert.That(reopened, Is.SameAs(first));
            Assert.That(ViewSession(reopened), Is.Not.SameAs(session));
            Assert.That(reopened.Module, Is.SameAs(_module));
        }

        [UnityTest]
        public IEnumerator PanelEntryAsyncKeepsConsumersIndependentAndRejectsSynchronousPendingTakeover()
        {
            yield return StartModule();
            var root = PolicyRoot("YLWindow.AsyncRoot");
            var prefab = Template<YLCoreSmokePanel>("YLWindow.Async", true);
            var request = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, root: root);
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
            var rejectedArgs = NewArgs();
            Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab, rejectedArgs, root));
            AssertArgsReleased(rejectedArgs);
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty, "同步拒绝不能接管异步生产或交付半初始化对象。");
            var nextArgs = NewArgs();
            nextArgs.SetInt("token", 42);
            var next = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, nextArgs, root);
            Assert.That(((IEnumerable)typeof(Module).GetField("PendingViews", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_module)).Cast<object>().Count(), Is.EqualTo(2));
            request.Abort();
            request.Abort();
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Canceled));
            Assert.That(next.Status, Is.EqualTo(LoadViewStatus.Pending));
            yield return next;
            Assert.That(next.Status, Is.EqualTo(LoadViewStatus.Succeeded), next.Error);
            var panel = Track(next.Panel);
            Assert.That(panel.Token, Is.EqualTo(42));
            AssertArgsReleased(nextArgs);
            Assert.That(panel.gameObject.activeSelf && panel.IsVisible, Is.True);
            var session = ViewSession(panel);
            var again = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, root: root);
            Assert.That(again.Status, Is.EqualTo(LoadViewStatus.Pending), "有效实例也先返回 Pending，数据回调最早下一帧。");
            Assert.That(panel.OpenCount, Is.EqualTo(1));
            yield return again;
            Assert.That(again.Status, Is.EqualTo(LoadViewStatus.Succeeded));
            Assert.That(again.Panel, Is.SameAs(panel));
            Assert.That(ViewSession(panel), Is.SameAs(session));
            Assert.That(panel.LoadedCount, Is.EqualTo(1));
            Assert.That(panel.OpenCount, Is.EqualTo(2));
            panel.Close();
            var canceledArgs = NewArgs();
            var canceled = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, canceledArgs, root);
            canceled.Abort();
            yield return null;
            Assert.That(canceled.Status, Is.EqualTo(LoadViewStatus.Canceled));
            Assert.That(canceled.Panel, Is.Null);
            AssertArgsReleased(canceledArgs);
            Assert.That(panel.OpenCount, Is.EqualTo(2), "全部消费者提交前取消不能调用业务钩子。");
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty);
            var pending = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, root: root);
            var pendingSibling = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, root: root);
            _module.Close();
            Assert.That(pending.Status, Is.EqualTo(LoadViewStatus.Invalidated));
            Assert.That(pendingSibling.Status, Is.EqualTo(LoadViewStatus.Invalidated));
            Assert.That(((IEnumerable)typeof(Module).GetField("PendingViews", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_module)).Cast<object>().Count(), Is.Zero);
            yield return null;
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator PanelEntryInvalidatedPrefabDoesNotFallBackToNamedLoading()
        {
            yield return StartModule();
            var root = PolicyRoot("YLWindow.DeadPrefabRoot");
            var prefab = Template<YLCoreSmokePanel>("YLWindow.DeadPrefab", true);
            var request = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, root: root);
            Object.DestroyImmediate(prefab);
            yield return request;
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Invalidated));
            Assert.That(request.Panel, Is.Null);
        }

        [UnityTest]
        public IEnumerator PanelEntryMultipleAndUncachedKeepOwnerAndDiscardContract()
        {
            yield return StartModule();
            var root = PolicyRoot("YLWindow.MultiRoot");
            var prefab = Template<YLCoreSmokePanel>("YLWindow.Multi", true);
            _module.BindPanelRules(new[] { new PanelRule(prefab, new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CacheOnClose = false }) });
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            var second = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            Assert.That(first, Is.Not.SameAs(second));
            first.Close();
            Assert.That(first.Module, Is.Null);
            Assert.That(second.Module, Is.SameAs(_module));
            yield return null;
            Assert.That(first == null, Is.True, "Non-reusable acquired instance must be destroyed, not cached.");
        }

        [UnityTest]
        public IEnumerator TipsAreModuleOwnedAndFinishPausedWithoutLeakingActiveItems()
        {
            yield return StartModule();
            var root = PolicyRoot("YLTips.Root");
            var presenter = CreateTips(root);
            var oldScale = Time.timeScale;
            try
            {
                Time.timeScale = 0;
                var handle = presenter.Show(_module, "Paused tip", duration: 0.05f, fade: false);
                var item = handle.Item;
                Assert.That(item.Module, Is.SameAs(_module));
                var deadline = Time.realtimeSinceStartup + 2;
                while (!handle.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(handle.IsDone, Is.True);
                Assert.That(handle.Result, Is.EqualTo(UITipResult.Completed));
                Assert.That(item.Module, Is.Null);
                Assert.That(item.gameObject.activeSelf, Is.False);
                Assert.That(presenter.ActiveCount, Is.Zero);
            }
            finally
            {
                Time.timeScale = oldScale;
            }
        }

        [UnityTest]
        public IEnumerator TipsCancelReuseModuleExitAndHostEndReleaseOnlyTheirSessions()
        {
            yield return StartModule();
            var root = PolicyRoot("YLTips.CancelRoot");
            var presenter = CreateTips(root);
            var first = presenter.Show(_module, "first");
            first.Dispose();
            Assert.That(first.IsDone, Is.True);
            var next = presenter.Show(_module, "next");
            Assert.That(next.Item, Is.SameAs(first.Item));
            first.Dispose();
            Assert.That(next.IsDone, Is.False, "Old handle cannot close the reused item.");
            var window = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLTips.RealWindow", true), root: root));
            Assert.That(window.Module, Is.SameAs(_module));
            Assert.That(_module.GetViews<Panel>(), Has.Count.EqualTo(2), "Tips 原位 Attach 会话不占用资源 Single 范围。");
            _module.Close();
            Assert.That(next.IsDone, Is.True);
            Assert.That(presenter.ActiveCount, Is.Zero);
            yield return StartModule();
            var ending = presenter.Show(_module, "host");
            presenter.enabled = false;
            Assert.That(ending.IsDone, Is.True);
            Assert.That(ending.Result, Is.EqualTo(UITipResult.HostEnded));
            Assert.That(ending.Item.Module, Is.Null);
            Assert.That(presenter.ActiveCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator TipsCompletionFailureStillReturnsItemAndAllowsReentrantReuse()
        {
            yield return StartModule();
            var presenter = CreateTips(PolicyRoot("YLTips.CallbackRoot"));
            UITipHandle reentrant = null;
            Assert.Throws<InvalidOperationException>(() => presenter.Show(_module, "zero", duration: 0, completed: _ =>
            {
                reentrant = presenter.Show(_module, "new session");
                throw new InvalidOperationException("YLTips.CallbackFailure");
            }));
            Assert.That(presenter.ActiveCount, Is.EqualTo(1));
            Assert.That(reentrant.IsDone, Is.False);
            Assert.That(reentrant.Item.IsLoaded, Is.True);
            reentrant.Dispose();
            Assert.That(presenter.ActiveCount, Is.Zero);
        }

        private UITipsPresenter CreateTips(UIRootContext root)
        {
            var prefab = Template<UITipsItem>("YLTips.Template", true);
            SetPrivate(prefab.GetComponent<UITipsItem>(), "settings", new PanelOverrides
            {
                ParticipatesInTop = false,
                InstanceMode = PanelInstanceMode.Multiple
            }.ApplyTo(PanelConfig.Default));
            var bg = prefab.AddComponent<UnityEngine.UI.Image>();
            var label = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
            label.transform.SetParent(prefab.transform, false);
            var text = label.GetComponent<UnityEngine.UI.Text>();
            SetPrivate(prefab.GetComponent<UITipsItem>(), "bg", bg);
            SetPrivate(prefab.GetComponent<UITipsItem>(), "text", text);
            var presenter = root.gameObject.AddComponent<UITipsPresenter>();
            SetPrivate(presenter, "uiTipItemPrefab", prefab);
            SetPrivate(presenter, "uiTipItemPool", root.transform);
            foreach (var field in new[] { "middleTipRoot", "leftTopTipRoot", "leftDownTipRoot", "rightTopTipRoot", "rightDownTipRoot" })
            {
                SetPrivate(presenter, field, root.transform);
            }

            return presenter;
        }

        private static void SetPrivate(object target, string name, object value)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null)
                {
                    continue;
                }

                field.SetValue(target, value);
                return;
            }
            throw new MissingFieldException(target.GetType().FullName, name);
        }

        [UnityTest]
        public IEnumerator FormalColorWindowUsesPanelPolicyAndDropsBlockedCapture()
        {
            yield return StartModule();
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.kano.ylframework/Prefabs/ColorSelector.prefab");
            var root = PolicyRoot("YLColor.MixedGateRoot");
            for (int order = 0; order < 2; order++)
            {
                var prefab = Object.Instantiate(asset);
                prefab.SetActive(false);
                _objects.Add(prefab);
                _prefabKeys.Add(prefab.GetHashCode());
                var rootGate = prefab.AddComponent<YLCoreOtherInputGate>();
                rootGate.isAllowed = false;
                if (order == 1)
                {
                    while (UnityEditorInternal.ComponentUtility.MoveComponentUp(rootGate))
                    {
                    }
                }

                var child = prefab.GetComponentInChildren<ColorSelector>(true).gameObject;
                child.AddComponent<YLCoreOtherInputGate>().isAllowed = true;
                prefab.SetActive(true);
                var unowned = prefab.GetComponent<ColorSelectorWindow>();
                try
                {
                    var unownedSelector = child.GetComponent<ColorSelector>();
                    unownedSelector.BeginSession(Color.red, null);
                    var unownedRect = child.GetComponent<RectTransform>();
                    unownedSelector.OnPointerDown(new UnityEngine.EventSystems.PointerEventData(null)
                    {
                        pointerId = 9,
                        position = RectTransformUtility.WorldToScreenPoint(null, unownedRect.TransformPoint(
                            unownedRect.rect.center + new Vector2(-.44f, .7621024f) * Mathf.Min(unownedRect.rect.width, unownedRect.rect.height) * .5f))
                    });
                    Assert.That(unowned.IsLoaded, Is.False);
                    Assert.That(unowned.CurrentColor, Is.EqualTo(Color.red), "A nearer allowing gate cannot bypass the unloaded formal Panel either.");
                }
                finally
                {
                    child.GetComponent<ColorSelector>().EndSession();
                    prefab.SetActive(false);
                }
                int changes = 0;
                var window = Track(_module.LoadPanel<ColorSelectorWindow>(prefab,
                    ColorSelectorWindow.Arguments(Color.red, _ => changes++), root));
                window.gameObject.SetActive(true);
                yield return null;
                Canvas.ForceUpdateCanvases();
                var selector = window.GetComponentInChildren<ColorSelector>();
                var rect = selector.GetComponent<RectTransform>();
                Vector2 Point(Vector2 normalized) => RectTransformUtility.WorldToScreenPoint(null,
                    rect.TransformPoint(rect.rect.center + normalized * Mathf.Min(rect.rect.width, rect.rect.height) * .5f));
                var data = new UnityEngine.EventSystems.PointerEventData(null)
                {
                    pointerId = 23,
                    button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                    position = Point(new Vector2(-.44f, .7621024f))
                };
                selector.OnPointerDown(data);
                Assert.That(window.CurrentColor.g, Is.EqualTo(1).Within(.001f), "An unrelated denying gate on the Panel must not change its original priority.");
                Assert.That(changes, Is.EqualTo(1));
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLColor.MixedModal" + order, true), root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
                modal.gameObject.SetActive(true);
                modal.UpdateDisplay(new PanelOverrides { Modal = true });
                Assert.That(window.IsInputBlocked, Is.True);
                data.position = Point(new Vector2(-.44f, -.7621024f));
                selector.OnDrag(data);
                Assert.That(window.CurrentColor.g, Is.EqualTo(1).Within(.001f), "A nearer allowing gate must not bypass the actual modal Panel.");
                Assert.That(changes, Is.EqualTo(1));
                modal.Close();
                selector.OnDrag(data);
                Assert.That(changes, Is.EqualTo(1), "Unblocking must not revive the old pointer capture.");
                selector.OnPointerDown(data);
                Assert.That(window.CurrentColor.b, Is.EqualTo(1).Within(.001f));
                Assert.That(changes, Is.EqualTo(2));
                foreach (var gate in window.GetComponentsInChildren<YLCoreOtherInputGate>(true))
                {
                    Assert.That(gate.reads, Is.Zero, "The explicit formal Panel owns this session, in either component order.");
                }

                window.Close();
            }
#endif
        }

        [UnityTest]
        public IEnumerator FormalColorPrefabSupportsCameraCoordinatesCallbacksAndReuse()
        {
            yield return StartModule();
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.kano.ylframework/Prefabs/ColorSelector.prefab");
            Assert.That(asset, Is.Not.Null);
            var prefab = Object.Instantiate(asset);
            prefab.SetActive(false);
            _objects.Add(prefab);
            _prefabKeys.Add(prefab.GetHashCode());
            var root = PolicyRoot("YLColor.CameraRoot");
            var cameraObject = new GameObject("YLColor.Camera", typeof(Camera));
            _objects.Add(cameraObject);
            var camera = cameraObject.GetComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -10);
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1;
            int old = 0, current = 0;
            var first = Track(_module.LoadPanel<ColorSelectorWindow>(prefab,
                ColorSelectorWindow.Arguments(new Color(1, 0, 0, 0.35f), _ => old++), root));
            first.gameObject.SetActive(true);
            first.GetComponent<RectTransform>().anchoredPosition = new Vector2(120, -80);
            first.transform.localScale = new Vector3(0.8f, 0.8f, 1);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var selector = first.GetComponentInChildren<YLFramework.UI.ColorSelector>();
            var wheel = selector.GetComponent<RectTransform>();
            var local = wheel.rect.center + new Vector2(-0.44f, 0.7621024f) * (wheel.rect.width * 0.5f);
            var data = new UnityEngine.EventSystems.PointerEventData(null)
            {
                pointerId = 3,
                position = RectTransformUtility.WorldToScreenPoint(camera, wheel.TransformPoint(local)),
                pointerPressRaycast = new UnityEngine.EventSystems.RaycastResult { module = first.GetComponent<UnityEngine.UI.GraphicRaycaster>() }
            };
            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
            UnityEngine.EventSystems.EventSystem.current.RaycastAll(data, hits);
            Assert.That(hits, Is.Not.Empty, "The actual Camera canvas must expose the wheel through UGUI raycasting.");
            Assert.That(hits[0].gameObject, Is.SameAs(selector.gameObject));
            Assert.That(UIPointer.CheckPositionOnUI(data.position), Is.True);
            Assert.That(UIPointer.CheckPositionOnUI(new Vector2(-1, -1)), Is.False);
            var system = UnityEngine.EventSystems.EventSystem.current;
            try
            {
                system.enabled = false;
                Assert.That(UIPointer.CheckPositionOnUI(data.position), Is.False);
            }
            finally
            {
                system.enabled = true;
            }
            Assert.That(UIPointer.CheckPositionOnUI(data.position), Is.True, "Cache must follow the actual current EventSystem.");
            data.pointerPressRaycast = hits[0];
            UnityEngine.EventSystems.ExecuteEvents.Execute(hits[0].gameObject, data, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
            Assert.That(first.CurrentColor.g, Is.EqualTo(1).Within(0.001f));
            Assert.That(first.CurrentColor.r, Is.EqualTo(0).Within(0.001f));
            Assert.That(first.CurrentColor.a, Is.EqualTo(0.35f));
            Assert.That(old, Is.EqualTo(1));
            first.Close();
            var second = Track(_module.LoadPanel<ColorSelectorWindow>(prefab,
                ColorSelectorWindow.Arguments(Color.blue, _ => current++), root));
            second.gameObject.SetActive(true);
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.CurrentColor, Is.EqualTo(Color.blue));
            selector.OnDrag(data);
            Assert.That(current, Is.Zero, "Old pointer cannot change the next session.");
            data.position = RectTransformUtility.WorldToScreenPoint(camera, wheel.TransformPoint(local));
            selector.OnPointerDown(data);
            Assert.That(current, Is.EqualTo(1));
            Assert.That(old, Is.EqualTo(1));
            second.GetComponentInChildren<UnityEngine.UI.Button>().onClick.Invoke();
            Assert.That(second.Module, Is.Null);
#endif
        }

        [UnityTest]
        public IEnumerator ModalBlocksActualRaycastsSelectionAndRestoresValidFocus()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.ModalRoot");
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.Lower", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            var buttonObject = new GameObject("LowerButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            buttonObject.transform.SetParent(lower.transform, false);
            var button = buttonObject.GetComponent<UnityEngine.UI.Button>();
            var buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.sizeDelta = new Vector2(100, 100);
            buttonRect.anchoredPosition = new Vector2(-150, 0);
            int clicks = 0;
            button.onClick.AddListener(() => clicks++);
            var system = UnityEngine.EventSystems.EventSystem.current;
            if (!system)
            {
                var input = new GameObject("YLInput.TestSystem", typeof(UnityEngine.EventSystems.EventSystem));
                _objects.Add(input);
                system = input.GetComponent<UnityEngine.EventSystems.EventSystem>();
            }
            system.SetSelectedGameObject(buttonObject);
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.Modal", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(new PanelOverrides { Modal = true });
            Canvas.ForceUpdateCanvases();
            Assert.That(button.IsInteractable(), Is.False, "Logical blocking must also constrain UGUI navigation and submit.");
            Assert.That(system.currentSelectedGameObject, Is.Not.SameAs(buttonObject));
            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
            system.RaycastAll(new UnityEngine.EventSystems.PointerEventData(system)
            {
                position = RectTransformUtility.WorldToScreenPoint(null, buttonRect.position)
            }, hits);
            Assert.That(hits.Exists(hit => hit.gameObject == buttonObject), Is.False);
            UnityEngine.EventSystems.ExecuteEvents.Execute(buttonObject, new UnityEngine.EventSystems.BaseEventData(system),
                UnityEngine.EventSystems.ExecuteEvents.submitHandler);
            Assert.That(clicks, Is.Zero);
            modal.Close();
            Assert.That(button.IsInteractable(), Is.True);
            Assert.That(system.currentSelectedGameObject, Is.SameAs(buttonObject));
            lower.Close();
            Assert.That(system.currentSelectedGameObject, Is.Null, "Closed sessions cannot remain selected.");
        }

        [UnityTest]
        public IEnumerator DeselectCloseDoesNotLetOldInputCoveragePolluteReusedPanel()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.ReentrantRoot");
            var prefab = Template<YLCoreSmokePanel>("YLInput.ReentrantLower", true);
            foreach (var name in new[] { "First", "Second" })
            {
                var child = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCoreSelectionInputProbe));
                child.transform.SetParent(prefab.transform, false);
            }
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            var system = UnityEngine.EventSystems.EventSystem.current;
            var buttons = lower.GetComponentsInChildren<UnityEngine.UI.Button>();
            buttons[0].GetComponent<YLCoreSelectionInputProbe>().Deselected = lower.Close;
            system.SetSelectedGameObject(buttons[0].gameObject);
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ReentrantModal", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(new PanelOverrides { Modal = true });
            Assert.That(lower.Module, Is.Null);
            modal.Close();
            var next = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            Assert.That(next, Is.SameAs(lower));
            next.gameObject.SetActive(true);
            foreach (var button in next.GetComponentsInChildren<UnityEngine.UI.Button>())
            {
                Assert.That(button.IsInteractable(), Is.True, "Old Apply must stop as soon as Deselect ends its session.");
            }

            Assert.That(next.GetComponent<UnityEngine.UI.GraphicRaycaster>().enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator NestedIndependentModalSelectionIsNotOwnedByBlockedAncestor()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.NestedRoot");
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.NestedLower", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            var modalPrefab = Template<YLCoreSmokePanel>("YLInput.NestedModal", true);
            modalPrefab.AddComponent<Canvas>();
            var modalObject = Object.Instantiate(modalPrefab, lower.transform, false);
            var modal = Track(_module.AttachView(modalObject.GetComponent<YLCoreSmokePanel>()));
            var button = new GameObject("NestedModalButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCoreSelectionInputProbe));
            button.transform.SetParent(modal.transform, false);
            int deselections = 0;
            button.GetComponent<YLCoreSelectionInputProbe>().Deselected = () => deselections++;
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(new PanelOverrides { Modal = true });
            var system = UnityEngine.EventSystems.EventSystem.current;
            system.SetSelectedGameObject(button);
            yield return null;
            yield return null;
            Assert.That(system.currentSelectedGameObject, Is.SameAs(button));
            Assert.That(deselections, Is.Zero, "A blocked ancestor must not deselect an independent descendant modal each frame.");
        }

        [UnityTest]
        public IEnumerator OnSelectCloseDefersSelectionCleanupWithoutLosingPreviousFocus()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.SelectCloseRoot");
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.SelectCloseLower", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            var previous = new GameObject("Previous", typeof(RectTransform), typeof(UnityEngine.UI.Button));
            previous.transform.SetParent(lower.transform, false);
            var system = UnityEngine.EventSystems.EventSystem.current;
            system.SetSelectedGameObject(previous);
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.SelectCloseModal", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            var selected = new GameObject("CloseOnSelect", typeof(RectTransform), typeof(UnityEngine.UI.Button), typeof(YLCoreSelectionInputProbe));
            selected.transform.SetParent(modal.transform, false);
            selected.GetComponent<YLCoreSelectionInputProbe>().Selected = modal.Close;
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(new PanelOverrides { Modal = true });
            yield return null;
            yield return null;
            Assert.That(modal.Module, Is.Null);
            Assert.That(system.currentSelectedGameObject, Is.SameAs(previous));
        }

        [UnityTest]
        public IEnumerator IndependentPanelUnderAnotherPanelsSelectableIsRejectedWithoutChangingBusinessGroups()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.SelectableBoundaryRoot");
            var outer = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.SelectableBoundaryOuter", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            var parent = new GameObject("ParentSelectable", typeof(RectTransform), typeof(UnityEngine.UI.Button));
            parent.transform.SetParent(outer.transform, false);
            var child = new GameObject("IndependentPanel", typeof(RectTransform), typeof(YLCoreSmokePanel));
            child.SetActive(false);
            child.transform.SetParent(parent.transform, false);
            new GameObject("ChildButton", typeof(RectTransform), typeof(UnityEngine.UI.Button)).transform.SetParent(child.transform, false);
            var error = Assert.Throws<InvalidOperationException>(() => _module.AttachView(child.GetComponent<YLCoreSmokePanel>()));
            StringAssert.Contains("Selectable", error.Message);
            Assert.That(child.GetComponent<YLCoreSmokePanel>().Module, Is.Null);
            Assert.That(parent.GetComponent<UnityEngine.UI.Button>().interactable, Is.True);
        }

        [UnityTest]
        public IEnumerator NativeLayoutCallbackCannotWriteAcrossAReopenedSession()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.LayoutReentryRoot");
            var prefab = Template<YLCoreSmokePanel>("YLInput.LayoutReentryPanel", true);
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            var surfaceObject = new GameObject("LayoutSurface", typeof(RectTransform), typeof(UnityEngine.UI.HorizontalLayoutGroup));
            surfaceObject.transform.SetParent(panel.transform, false);
            var surface = (RectTransform)surfaceObject.transform;
            surface.anchorMin = surface.anchorMax = new Vector2(.5f, .5f);
            surface.sizeDelta = new Vector2(120, 80);
            var layout = surfaceObject.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            layout.padding = new RectOffset(7, 11, 5, 9);
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var buttonObject = new GameObject("ReentrantButton", typeof(RectTransform), typeof(CanvasGroup),
                typeof(UnityEngine.UI.Button), typeof(UnityEngine.UI.LayoutElement), typeof(YLCoreNativeLayoutReentryProbe));
            buttonObject.transform.SetParent(surface, false);
            var target = (RectTransform)buttonObject.transform;
            target.sizeDelta = new Vector2(40, 20);
            var element = buttonObject.GetComponent<UnityEngine.UI.LayoutElement>();
            element.minWidth = element.preferredWidth = 40;
            element.flexibleWidth = 1;
            element.minHeight = element.preferredHeight = 20;
            yield return null;
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(surface);
            Assert.That(target.parent, Is.Not.SameAs(surface), "先确认业务组控件已生成真实隔离结构。");
            Assert.That(target.parent.parent.parent, Is.SameAs(surface));
            float oldWidth = target.sizeDelta.x;
            Assert.That(oldWidth, Is.GreaterThan(0));
            var originalSession = ViewSession(panel);
            Assert.That(originalSession, Is.Not.Null);
            var probe = buttonObject.GetComponent<YLCoreNativeLayoutReentryProbe>();
            var expectedMin = new Vector2(.2f, .3f);
            var expectedMax = new Vector2(.7f, .8f);
            var expectedSize = new Vector2(137, 53);
            var expectedPosition = new Vector2(211, -73);
            YLCoreSmokePanel replacement = null;
            object reopenedSession = null;
            int callbacks = 0, opens = 0, callbackFrame = -1;
            probe.DimensionsChanged = () =>
            {
                // 父尺寸通知不算触发；必须进入实际目标宽度写入后的原生回调。
                if (target.sizeDelta.x <= oldWidth)
                {
                    return;
                }

                probe.DimensionsChanged = null;
                callbacks++;
                callbackFrame = Time.frameCount;
                YLCoreSmokePanel.UpdateProbe = (opened, _) =>
                {
                    YLCoreSmokePanel.UpdateProbe = null;
                    opens++;
                    reopenedSession = ViewSession(opened);
                    layout.enabled = false;
                    target.anchorMin = expectedMin;
                    target.anchorMax = expectedMax;
                    target.sizeDelta = expectedSize;
                    target.anchoredPosition = expectedPosition;
                };
                panel.Close();
                replacement = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            };
            void AssertReopenedLayout()
            {
                Assert.That(replacement, Is.SameAs(panel), "必须复用同一物理实例，而不是检查另一个新对象。");
                Assert.That(replacement.IsLoaded, Is.True);
                Assert.That(replacement.Root, Is.SameAs(root));
                Assert.That(reopenedSession, Is.Not.Null.And.Not.SameAs(originalSession));
                Assert.That(ViewSession(replacement), Is.SameAs(reopenedSession));
                Assert.That(layout.enabled, Is.False);
                Assert.That(target.anchorMin, Is.EqualTo(expectedMin));
                Assert.That(target.anchorMax, Is.EqualTo(expectedMax));
                Assert.That(target.sizeDelta, Is.EqualTo(expectedSize));
                Assert.That(target.anchoredPosition, Is.EqualTo(expectedPosition));
            }
            try
            {
                int triggerFrame = Time.frameCount;
                surface.sizeDelta = new Vector2(360, 80);
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(surface);
                Assert.That(callbacks, Is.EqualTo(1), "真实布局写入必须触发一次重入。");
                Assert.That(opens, Is.EqualTo(1));
                Assert.That(callbackFrame, Is.EqualTo(triggerFrame));
                AssertReopenedLayout();
                yield return null;
                AssertReopenedLayout();
            }
            finally
            {
                if (probe)
                {
                    probe.DimensionsChanged = null;
                }

                YLCoreSmokePanel.UpdateProbe = null;
            }
        }

        [UnityTest]
        public IEnumerator OwnedGateCleanupKeepsForeignChildrenAcrossNativeReentry()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.GateCleanupRoot");
            var prefab = Template<YLCoreSmokePanel>("YLInput.GateCleanupPanel", true);
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            foreach (bool destroyButton in new[] { false, true })
            {
                string phase = destroyButton ? "Retire" : "ReleaseMount";
                var parent = new GameObject(phase + "Parent", typeof(RectTransform));
                parent.transform.SetParent(panel.transform, false);
                var destination = new GameObject(phase + "Destination", typeof(RectTransform));
                destination.transform.SetParent(panel.transform, false);
                var button = new GameObject(phase + "Button", typeof(RectTransform), typeof(CanvasGroup), typeof(UnityEngine.UI.Button));
                button.transform.SetParent(parent.transform, false);
                yield return null;
                var oldFrame = button.transform.parent;
                Assert.That(oldFrame, Is.Not.SameAs(parent.transform), phase);
                var oldSlot = oldFrame.parent;
                Assert.That(oldSlot.parent, Is.SameAs(parent.transform), phase);
                var foreign = new GameObject(phase + "Foreign", typeof(RectTransform), typeof(YLCoreNativeLayoutReentryProbe));
                _objects.Add(foreign);
                foreign.transform.SetParent(oldFrame, false);
                var probe = foreign.GetComponent<YLCoreNativeLayoutReentryProbe>();
                GameObject replacement = null;
                int callbacks = 0;
                probe.ParentChanged = () =>
                {
                    if (foreign.transform.parent == oldFrame)
                    {
                        return;
                    }

                    probe.ParentChanged = null;
                    callbacks++;
                    replacement = new GameObject(phase + "Replacement", typeof(RectTransform));
                    _objects.Add(replacement);
                    replacement.transform.SetParent(oldFrame, false);
                };
                void AssertForeignPreserved()
                {
                    Assert.That(foreign && replacement, Is.True, phase + " 不能随自有壳销毁 foreign 子树。");
                    if (oldFrame)
                    {
                        Assert.That(foreign.transform.IsChildOf(oldFrame), Is.False, phase);
                        Assert.That(replacement.transform.IsChildOf(oldFrame), Is.False, phase);
                    }
                }
                try
                {
                    if (destroyButton)
                    {
                        Object.Destroy(button);
                    }
                    else
                    {
                        button.transform.SetParent(destination.transform, false);
                    }

                    for (int frame = 0; frame < 4; frame++)
                    {
                        yield return null;
                        if (!oldSlot)
                        {
                            AssertForeignPreserved();
                        }
                    }
                    Assert.That(callbacks, Is.EqualTo(1), phase + " 必须通过实际父级回调挂入替代节点。");
                    AssertForeignPreserved();
                    Assert.That(!oldSlot, Is.True, phase + " 应在有限帧内只清理自身结构。");
                }
                finally
                {
                    if (probe)
                    {
                        probe.ParentChanged = null;
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator BusinessCanvasGroupKeepsNativeLayoutAndSameFrameGateReuse()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.BusinessLayoutRoot");
            var prefab = Template<YLCoreSmokePanel>("YLInput.BusinessLayoutPanel", true);
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            foreach (var groupType in new[] { typeof(UnityEngine.UI.HorizontalLayoutGroup), typeof(UnityEngine.UI.VerticalLayoutGroup), typeof(UnityEngine.UI.GridLayoutGroup), typeof(YLCoreDerivedHorizontalLayoutGroup) })
            {
                RectTransform Surface(string name)
                {
                    var go = new GameObject(name, typeof(RectTransform));
                    go.transform.SetParent(panel.transform, false);
                    var rect = (RectTransform)go.transform;
                    rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                    rect.pivot = new Vector2(.2f, .7f);
                    rect.anchoredPosition = new Vector2(17, 29);
                    rect.sizeDelta = new Vector2(420, 220);
                    var layout = (UnityEngine.UI.LayoutGroup)go.AddComponent(groupType);
                    layout.padding = new RectOffset(9, 11, 13, 15);
                    if (layout is UnityEngine.UI.HorizontalOrVerticalLayoutGroup linear)
                    {
                        linear.spacing = 7;
                        linear.childControlWidth = linear.childControlHeight = true;
                        linear.childForceExpandWidth = linear.childForceExpandHeight = false;
                        linear.childScaleWidth = linear.childScaleHeight = true;
                    }
                    if (layout is UnityEngine.UI.GridLayoutGroup grid)
                    {
                        grid.cellSize = new Vector2(83, 37);
                        grid.spacing = new Vector2(4, 9);
                        grid.constraint = UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount;
                        grid.constraintCount = 2;
                    }
                    return rect;
                }
                UnityEngine.UI.Button Control(RectTransform parent, string name, bool business)
                {
                    var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Text), typeof(UnityEngine.UI.Button));
                    go.transform.SetParent(parent, false);
                    var rect = (RectTransform)go.transform;
                    rect.pivot = new Vector2(.3f, .6f);
                    rect.localScale = new Vector3(.8f, 1.2f, 1);
                    rect.localRotation = Quaternion.Euler(0, 0, 17);
                    var text = go.GetComponent<UnityEngine.UI.Text>();
                    text.font = font;
                    text.fontSize = 14;
                    text.text = name;
                    var fitter = go.AddComponent<UnityEngine.UI.ContentSizeFitter>();
                    fitter.horizontalFit = fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                    if (business)
                    {
                        var group = go.AddComponent<CanvasGroup>();
                        group.alpha = .37f;
                        group.blocksRaycasts = false;
                    }
                    return go.GetComponent<UnityEngine.UI.Button>();
                }
                var baseline = Surface("Baseline");
                var actual = Surface("Business");
                var controls = new[] { Control(baseline, "First", false), Control(baseline, "Second", false),
                    Control(actual, "First", true), Control(actual, "Second", true) };
                var businessGroup = controls[2].GetComponent<CanvasGroup>();
                void Compare()
                {
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(baseline);
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(actual);
                    for (int i = 0; i < 2; i++)
                    {
                        var a = (RectTransform)controls[i].transform;
                        var b = (RectTransform)controls[i + 2].transform;
                        var expected = new Vector3[4];
                        var observed = new Vector3[4];
                        a.GetWorldCorners(expected);
                        b.GetWorldCorners(observed);
                        for (int corner = 0; corner < 4; corner++)
                        {
                            Assert.That(Vector3.Distance(observed[corner], expected[corner]), Is.LessThan(.01f), groupType.Name + " corner " + corner);
                        }

                        Assert.That(b.anchorMin, Is.EqualTo(a.anchorMin));
                        Assert.That(b.anchorMax, Is.EqualTo(a.anchorMax));
                        Assert.That(b.anchoredPosition, Is.EqualTo(a.anchoredPosition));
                        Assert.That(b.sizeDelta, Is.EqualTo(a.sizeDelta));
                        Assert.That(b.pivot, Is.EqualTo(a.pivot));
                        Assert.That(b.localScale, Is.EqualTo(a.localScale));
                        Assert.That(b.localRotation, Is.EqualTo(a.localRotation));
                    }
                    Assert.That(businessGroup.interactable, Is.True);
                    Assert.That(businessGroup.alpha, Is.EqualTo(.37f));
                    Assert.That(businessGroup.blocksRaycasts || businessGroup.ignoreParentGroups, Is.False);
                    Assert.That(controls[2].GetComponents<CanvasGroup>(), Has.Length.EqualTo(1));
                }
                yield return null;
                Compare();
                Assert.That(panel.IsLoaded, Is.True);
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.LayoutModal", true), root: root,
                    overrides: new PanelOverrides { Modal = true }));
                Assert.That(controls[2].IsInteractable(), Is.False);
                Compare();
                controls[0].GetComponent<UnityEngine.UI.Text>().text = controls[2].GetComponent<UnityEngine.UI.Text>().text = "A longer dynamic text sample";
                baseline.sizeDelta = actual.sizeDelta = new Vector2(95, 280);
                baseline.pivot = actual.pivot = new Vector2(.6f, .3f);
                yield return null;
                Compare();
                var baselineIgnore = controls[0].gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                var actualIgnore = controls[2].gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                baselineIgnore.ignoreLayout = actualIgnore.ignoreLayout = true;
                yield return null;
                Compare();
                baselineIgnore.ignoreLayout = actualIgnore.ignoreLayout = false;
                yield return null;
                Compare();
                controls[0].gameObject.SetActive(false);
                controls[2].gameObject.SetActive(false);
                yield return null;
                Compare();
                controls[0].gameObject.SetActive(true);
                controls[2].gameObject.SetActive(true);
                modal.Close();
                yield return null;
                Compare();
                Assert.That(controls[2].IsInteractable(), Is.True);
                var retainedGate = controls[0].GetComponent<CanvasGroup>();
                var previous = panel;
                panel.Close();
                panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
                Assert.That(panel, Is.SameAs(previous));
                Assert.That(controls[0].GetComponent<CanvasGroup>(), Is.SameAs(retainedGate));
                Assert.That(panel.IsLoaded && controls[2].IsInteractable(), Is.True);
                Compare();
                Object.Destroy(baseline.gameObject);
                Object.Destroy(actual.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator EarlierIgnoreParentGroupUsesActualUguiCapabilityWithoutChangingBusinessFlags()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.IgnoreGroupRoot");
            var prefab = Template<YLCoreSmokePanel>("YLInput.IgnoreGroupPanel", true);
            var child = new GameObject("ConflictingButton", typeof(RectTransform), typeof(CanvasGroup), typeof(UnityEngine.UI.Button));
            child.GetComponent<CanvasGroup>().ignoreParentGroups = true;
            child.transform.SetParent(prefab.transform, false);
            var nativeHost = new GameObject("NativeGroupCapability", typeof(RectTransform), typeof(CanvasGroup));
            _objects.Add(nativeHost);
            nativeHost.transform.SetParent(root.GetLayer(0), false);
            var native = new GameObject("NativeControl", typeof(RectTransform), typeof(CanvasGroup), typeof(UnityEngine.UI.Button));
            native.transform.SetParent(nativeHost.transform, false);
            native.GetComponent<CanvasGroup>().ignoreParentGroups = true;
            nativeHost.GetComponent<CanvasGroup>().interactable = false;
            yield return null;
            bool nativeCanGate = !native.GetComponent<UnityEngine.UI.Button>().IsInteractable();
            nativeHost.SetActive(false);
            if (!nativeCanGate)
            {
                Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
                Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty, "原生准备已不能隔离时，首次打开必须原子拒绝。");
                Assert.That(child.GetComponent<CanvasGroup>().ignoreParentGroups, Is.True);
                child.GetComponent<CanvasGroup>().ignoreParentGroups = false;
            }
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            lower.gameObject.SetActive(true);
            yield return null;
            var business = lower.GetComponentInChildren<UnityEngine.UI.Button>().GetComponent<CanvasGroup>();
            if (!nativeCanGate)
            {
                business.ignoreParentGroups = true;
                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: .*ignoreParentGroups"));
            }
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.IgnoreGroupModal", true), root: root,
                overrides: new PanelOverrides { Modal = true, Layer = 1 }));
            modal.gameObject.SetActive(true);
            if (nativeCanGate)
            {
                var button = lower.GetComponentInChildren<UnityEngine.UI.Button>();
                Assert.That(button.IsInteractable(), Is.False);
                Assert.That(button.interactable, Is.True);
                Assert.That(button.GetComponent<CanvasGroup>().ignoreParentGroups, Is.True);
                modal.Close();
                Assert.That(button.IsInteractable(), Is.True);
            }
            else
            {
                Assert.That(lower.Module, Is.Null, "原生结构不能隔离时必须诊断并结束会话，不能假成功或写业务字段。");
            }

            Assert.That(business.ignoreParentGroups, Is.True, "失败清理也不能回写业务组。");
            Assert.That(child.GetComponent<CanvasGroup>().ignoreParentGroups, Is.EqualTo(nativeCanGate));
        }

        [UnityTest]
        public IEnumerator BlockingPreservesBusinessWritesIncludingTheForcedValue()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.BusinessWritesRoot");
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.BusinessWritesLower", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            var nested = new GameObject("NestedSortingCanvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
            nested.transform.SetParent(lower.transform, false);
            nested.GetComponent<Canvas>().overrideSorting = true;
            var child = new GameObject("BusinessControl", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            child.transform.SetParent(nested.transform, false);
            var button = child.GetComponent<UnityEngine.UI.Button>();
            var graphic = child.GetComponent<UnityEngine.UI.Image>();
            var raycaster = lower.GetComponent<UnityEngine.UI.GraphicRaycaster>();
            var nestedRaycaster = nested.GetComponent<UnityEngine.UI.GraphicRaycaster>();
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.BusinessWritesModal", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(new PanelOverrides { Modal = true });
            Assert.That(button.interactable, Is.True, "门禁不能写业务 interactable。");
            Assert.That(raycaster.enabled, Is.True, "门禁不能写业务 enabled。");
            Assert.That(nestedRaycaster.enabled && graphic.raycastTarget, Is.True);
            Assert.That(button.IsInteractable(), Is.False);
            var system = UnityEngine.EventSystems.EventSystem.current;
            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
            void RaycastControl()
            {
                Canvas.ForceUpdateCanvases();
                hits.Clear();
                system.RaycastAll(new UnityEngine.EventSystems.PointerEventData(system)
                {
                    position = RectTransformUtility.WorldToScreenPoint(null, child.transform.position)
                }, hits);
            }
            RaycastControl();
            Assert.That(hits.Exists(hit => hit.gameObject == child), Is.False, "nested overrideSorting 不能绕过射线门禁。");
            button.interactable = false;
            raycaster.enabled = false;
            graphic.raycastTarget = false;
            yield return null;
            modal.Close();
            Assert.That(button.interactable, Is.False, "同值 false 写入不能被旧快照恢复。");
            Assert.That(raycaster.enabled, Is.False);
            Assert.That(graphic.raycastTarget, Is.False);
            Assert.That(button.IsInteractable(), Is.False, "解除框架门禁仍保留业务 false。");
            button.interactable = true;
            raycaster.enabled = true;
            graphic.raycastTarget = true;
            modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.BusinessWritesModal2", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(new PanelOverrides { Modal = true });
            button.interactable = false;
            button.interactable = true;
            raycaster.enabled = false;
            raycaster.enabled = true;
            graphic.raycastTarget = false;
            graphic.raycastTarget = true;
            yield return null;
            Assert.That(button.interactable, Is.True);
            Assert.That(raycaster.enabled, Is.True);
            Assert.That(nestedRaycaster.enabled && graphic.raycastTarget, Is.True);
            Assert.That(button.IsInteractable(), Is.False);
            RaycastControl();
            Assert.That(hits.Exists(hit => hit.gameObject == child), Is.False);
            modal.Close();
            Assert.That(button.IsInteractable(), Is.True);
            Assert.That(raycaster.enabled && nestedRaycaster.enabled && graphic.raycastTarget, Is.True);
            RaycastControl();
            Assert.That(hits.Exists(hit => hit.gameObject == child), Is.True);
        }

        [UnityTest]
        public IEnumerator ReparentedControlsReleaseOldInputCoverageBeforeNewOwnerConfiguration()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.ControlMoveRoot");
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ControlMoveLower", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            var child = new GameObject("MovedButton", typeof(RectTransform), typeof(CanvasGroup), typeof(UnityEngine.UI.Button));
            child.transform.SetParent(lower.transform, false);
            var button = child.GetComponent<UnityEngine.UI.Button>();
            var business = child.GetComponent<CanvasGroup>();
            business.alpha = .4f;
            business.blocksRaycasts = false;
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ControlMoveModal", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(new PanelOverrides { Modal = true });
            Assert.That(button.interactable, Is.True);
            Assert.That(button.IsInteractable(), Is.False);
            child.transform.SetParent(modal.transform, false);
            yield return null;
            Assert.That(button.interactable, Is.True);
            Assert.That(button.IsInteractable(), Is.True, "重挂后必须由新 owner 的有效状态恢复交互，而不只是保留业务 true。");
            Assert.That(business.interactable, Is.True);
            Assert.That(business.blocksRaycasts, Is.False);
            Assert.That(business.ignoreParentGroups, Is.False);
            Assert.That(business.alpha, Is.EqualTo(.4f));
            button.interactable = false;
            lower.Close();
            Assert.That(button.interactable, Is.False, "The old state must not write attributes after ownership has moved.");
        }

        [UnityTest]
        public IEnumerator RootMigrationEndsOldPanelWithoutTearingDownAnotherRootsInput()
        {
            yield return StartModule();
            var first = PolicyRoot("YLInput.MoveRootA");
            var second = PolicyRoot("YLInput.MoveRootB");
            var prefab = Template<YLCoreSmokePanel>("YLInput.MovedPanel", true);
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: first));
            var survivor = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: second));
            panel.gameObject.SetActive(true);
            var system = UnityEngine.EventSystems.EventSystem.current;
            panel.transform.SetParent(second.GetLayer(0), false);
            yield return null;
            Object.Destroy(first.gameObject);
            yield return null;
            yield return null;
            Assert.That(panel.Module, Is.Null);
            Assert.That(survivor.Root, Is.SameAs(second));
            Assert.That(survivor.IsLoaded && survivor.IsVisible, Is.True);
            Assert.That(system && system.isActiveAndEnabled, Is.True);
            Assert.That(UnityEngine.EventSystems.EventSystem.current, Is.SameAs(system));
        }

#if ENABLE_INPUT_SYSTEM || ENABLE_LEGACY_INPUT_MANAGER
#if ENABLE_INPUT_SYSTEM
        [UnityTest]
        public IEnumerator InputSystemMouseDrivesFormalColorDragAndCloseButton()
            => DriveFormalColorWithInputSystem(false);

        [UnityTest]
        public IEnumerator InputSystemMouseDrivesCameraColorAfterResolutionChange()
            => DriveFormalColorWithInputSystem(true);
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        [UnityTest]
        public IEnumerator BorrowedStandaloneModuleDrivesFormalColorViaBaseInputOverride()
            => DriveFormalColorWithInputSystem(false, true);
#endif

        private IEnumerator DriveFormalColorWithInputSystem(bool cameraMode, bool legacy = false)
        {
            yield return StartModule();
#if UNITY_EDITOR
            var root = PolicyRoot("YLInput.NativeMouseRoot");
#if ENABLE_LEGACY_INPUT_MANAGER
            YLCoreLegacyPointerInput scriptedInput = null;
            if (legacy)
            {
                var external = new GameObject("YLInput.LegacySystem", typeof(UnityEngine.EventSystems.EventSystem), typeof(UnityEngine.EventSystems.StandaloneInputModule), typeof(YLCoreLegacyPointerInput));
                _objects.Add(external);
                scriptedInput = external.GetComponent<YLCoreLegacyPointerInput>();
                external.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>().inputOverride = scriptedInput;
            }
#endif
            var scaler = root.gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(800, 600);
            Camera camera = null;
            if (cameraMode)
            {
                var cameraObject = new GameObject("YLInput.NativeCamera", typeof(Camera));
                _objects.Add(cameraObject);
                camera = cameraObject.GetComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -10);
            }
            root.Canvas.renderMode = cameraMode ? RenderMode.ScreenSpaceCamera : RenderMode.ScreenSpaceOverlay;
            root.Canvas.worldCamera = camera;
            root.Canvas.planeDistance = 5;
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.kano.ylframework/Prefabs/ColorSelector.prefab");
            var prefab = Object.Instantiate(asset);
            prefab.SetActive(false);
            _objects.Add(prefab);
            _prefabKeys.Add(prefab.GetHashCode());
            int changes = 0;
            var window = Track(_module.LoadPanel<ColorSelectorWindow>(prefab,
                ColorSelectorWindow.Arguments(new Color(1, 0, 0, 0.4f), _ => changes++), root));
            window.gameObject.SetActive(true);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var system = UnityEngine.EventSystems.EventSystem.current;
            Assert.That(system.currentInputModule.GetType().FullName, Is.EqualTo(legacy ? "UnityEngine.EventSystems.StandaloneInputModule" : "UnityEngine.InputSystem.UI.InputSystemUIInputModule"));
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            var stateType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");
            var mouse = legacy ? null : inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Mouse", "YLInput.OwnedMouse", null });
            Action<Vector2, bool> queue = (position, pressed) =>
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                if (scriptedInput)
                {
                    scriptedInput.Set(position, pressed);
                    return;
                }
#endif
                QueueMouse(inputType, stateType, mouse, position, pressed);
            };
            Action restoreDevices = null;
            try
            {
                if (mouse != null)
                {
                    restoreDevices = UseOwnedInputDevices(system.currentInputModule, mouse);
                }

                var wheel = window.GetComponentInChildren<ColorSelector>().GetComponent<RectTransform>();
                var center = wheel.rect.center;
                float radius = Mathf.Min(wheel.rect.width, wheel.rect.height) * 0.5f;
                Vector2 green = RectTransformUtility.WorldToScreenPoint(camera, wheel.TransformPoint(center + new Vector2(-0.44f, 0.7621024f) * radius));
                queue(green, false);
                yield return null;
                queue(green, true);
                yield return null;
                string pointerEvidence = "Legacy override";
                if (mouse != null)
                {
                    var inputModule = system.currentInputModule;
                    var pointReference = inputModule.GetType().GetProperty("point").GetValue(inputModule);
                    var action = pointReference.GetType().GetProperty("action").GetValue(pointReference);
                    var value = action.GetType().GetMethod("ReadValueAsObject").Invoke(action, null);
                    int id = (int)mouse.GetType().GetProperty("deviceId").GetValue(mouse);
                    var hit = (UnityEngine.EventSystems.RaycastResult)inputModule.GetType().GetMethod("GetLastRaycastResult").Invoke(inputModule, new object[] { id });
                    pointerEvidence = $"focused={system.isFocused} requested={green} action={value} hit={hit.gameObject}";
                }
                Assert.That(window.CurrentColor.g, Is.EqualTo(1).Within(0.01f), pointerEvidence);
                Assert.That(changes, Is.EqualTo(1));
                scaler.referenceResolution = new Vector2(600, 800);
                scaler.matchWidthOrHeight = .25f;
                yield return null;
                Canvas.ForceUpdateCanvases();
                Vector2 blue = RectTransformUtility.WorldToScreenPoint(camera, wheel.TransformPoint(center + new Vector2(-0.44f, -0.7621024f) * radius));
                queue(blue, true);
                yield return null;
                Assert.That(window.CurrentColor.b, Is.EqualTo(1).Within(0.01f));
                Assert.That(window.CurrentColor.a, Is.EqualTo(0.4f));
                queue(blue, false);
                yield return null;
                var close = window.GetComponentInChildren<UnityEngine.UI.Button>();
                Vector2 closePoint = RectTransformUtility.WorldToScreenPoint(camera, close.transform.position);
                queue(closePoint, false);
                yield return null;
                queue(closePoint, true);
                yield return null;
                queue(closePoint, false);
                yield return null;
                Assert.That(window.Module, Is.Null, "Close must be dispatched by the input module, not invoked directly.");
            }
            finally
            {
                restoreDevices?.Invoke();
                if (mouse != null)
                {
                    inputType.GetMethod("RemoveDevice").Invoke(null, new[] { mouse });
                }
            }
#endif
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        [UnityTest] public IEnumerator StandaloneThreeMouseButtonsCancelAndAcceptANewGesture() => DriveThreeMouseButtons(true);
        [UnityTest] public IEnumerator StandaloneScrollbarAndInputFieldUseNativeCancellation() => DriveScrollbarAndInputField(true);
        [UnityTest] public IEnumerator StandaloneSelectionLockCancelsPendingInputFieldAndEndsEditingOnce() => DriveFieldSelectionLock(true);
        [UnityTest] public IEnumerator StandaloneElasticScrollRectStopsOldDragAndInertia() => DriveScrollCancellation(true, UnityEngine.UI.ScrollRect.MovementType.Elastic);
        [UnityTest] public IEnumerator StandaloneClampedScrollRectStopsOldDragAndInertia() => DriveScrollCancellation(true, UnityEngine.UI.ScrollRect.MovementType.Clamped);
#endif
#if ENABLE_INPUT_SYSTEM
        [UnityTest]
        public IEnumerator IndependentChildPanelReceivesNativeClicksAboveItsBlockedParent()
        {
            yield return StartModule();
            using (var pointer = CreateNativePointer(false))
            {
                var root = PolicyRoot("YLInput.ChildNativeRoot");
                var parent = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ChildNativeParent", true), root: root));
                var childObject = new GameObject("IndependentChild", typeof(RectTransform));
                childObject.SetActive(false);
                childObject.transform.SetParent(parent.transform, false);
                var child = childObject.AddComponent<YLCoreSmokePanel>();
                SetPrivate(child, "settings", new PanelOverrides { Layer = 1 }.ApplyTo(PanelConfig.Default));
                var button = new GameObject("Button", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCorePointerClickProbe));
                button.transform.SetParent(child.transform, false);
                ((RectTransform)button.transform).sizeDelta = new Vector2(150, 70);
                _module.AttachView(child);
                parent.gameObject.SetActive(true);
                childObject.SetActive(true);
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ChildNativeModal", true), root: root,
                    overrides: new PanelOverrides { Modal = true }));
                modal.gameObject.SetActive(true);
                yield return null;
                yield return null;
                pointer.Bind();
                Assert.That(parent.IsInputBlocked, Is.True);
                Assert.That(child.IsInputBlocked, Is.False);
                Assert.That(button.GetComponent<UnityEngine.UI.Button>().IsInteractable(), Is.True);
                Canvas.ForceUpdateCanvases();
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, button.transform.position);
                var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                var system = UnityEngine.EventSystems.EventSystem.current;
                system.RaycastAll(new UnityEngine.EventSystems.PointerEventData(system) { position = point }, hits);
                Assert.That(hits.Exists(hit => hit.gameObject == button), Is.True);
                pointer.Queue(point, 0);
                yield return null;
                pointer.Queue(point, 1);
                yield return null;
                pointer.Queue(point, 0);
                yield return null;
                Assert.That(button.GetComponent<YLCorePointerClickProbe>().Clicks[0], Is.EqualTo(1), "父 Panel 门禁不得跨独立子 Canvas 阻断实际点击。");
            }
        }

        [UnityTest]
        public IEnumerator InputSystemTouchIdentityAndPooledEventDataDoNotCancelExternalGestures()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.TouchOwnedRoot");
            var externalRoot = PolicyRoot("YLInput.TouchExternalRoot");
            externalRoot.Canvas.sortingOrder = 100;
            var source = Template<YLCoreSmokePanel>("YLInput.TouchOwnedPanel", true);
            var buttonObject = new GameObject("OwnedButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCorePointerClickProbe));
            buttonObject.transform.SetParent(source.transform, false);
            ((RectTransform)buttonObject.transform).sizeDelta = new Vector2(100, 80);
            ((RectTransform)buttonObject.transform).anchoredPosition = new Vector2(-120, 0);
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
            panel.gameObject.SetActive(true);
            var external = new GameObject("ExternalButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCorePointerClickProbe));
            external.transform.SetParent(externalRoot.GetLayer(0), false);
            ((RectTransform)external.transform).sizeDelta = new Vector2(100, 80);
            ((RectTransform)external.transform).anchoredPosition = new Vector2(120, 0);
            yield return null;
            yield return null;
            Canvas.ForceUpdateCanvases();
            var ownProbe = panel.GetComponentInChildren<YLCorePointerClickProbe>();
            var externalProbe = external.GetComponent<YLCorePointerClickProbe>();
            Vector2 ownPoint = RectTransformUtility.WorldToScreenPoint(null, ownProbe.transform.position);
            Vector2 externalPoint = RectTransformUtility.WorldToScreenPoint(null, external.transform.position);
            var input = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var stateType = Type.GetType("UnityEngine.InputSystem.LowLevel.TouchState, Unity.InputSystem", true);
            var phaseType = Type.GetType("UnityEngine.InputSystem.TouchPhase, Unity.InputSystem", true);
            var device = input.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) }).Invoke(null, new object[] { "Touchscreen", "YLInput.OwnedTouchscreen", null });
            Action restore = null;
            void Queue(int id, Vector2 point, string phase)
            {
                var state = Activator.CreateInstance(stateType);
                stateType.GetField("touchId").SetValue(state, id);
                stateType.GetField("position").SetValue(state, point);
                stateType.GetProperty("phase").SetValue(state, Enum.Parse(phaseType, phase));
                QueueInputState(input, stateType, device, state);
            }
            try
            {
                restore = UseOwnedInputDevices(UnityEngine.EventSystems.EventSystem.current.currentInputModule, device);
                Queue(11, ownPoint, "Began");
                Queue(22, externalPoint, "Began");
                yield return null;
                Assert.That(ownProbe.Downs.Count, Is.EqualTo(1));
                Assert.That(externalProbe.Downs.Count, Is.EqualTo(1));
                var originalData = ownProbe.Downs[0];
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.TouchModal", true), root: root,
                    overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                modal.gameObject.SetActive(true);
                Canvas.ForceUpdateCanvases();
                var system = UnityEngine.EventSystems.EventSystem.current;
                var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                system.RaycastAll(new UnityEngine.EventSystems.PointerEventData(system) { position = externalPoint }, hits);
                Assert.That(hits, Is.Not.Empty);
                Assert.That(hits[0].gameObject, Is.SameAs(external), "外部触点的释放射线必须仍首命中外部按钮，而非本根的模态遮罩。");
                Queue(11, ownPoint, "Ended");
                Queue(22, externalPoint, "Ended");
                yield return null;
                Assert.That(ownProbe.Clicks[0], Is.Zero);
                Assert.That(externalProbe.Clicks[0], Is.EqualTo(1), "只能清理本 Panel 捕获，不能重置同一模块的外部触点。");
                // Input System 1.14.2 在释放后的下一帧才将触点事件回池。
                yield return null;
                Queue(33, externalPoint, "Began");
                Queue(44, externalPoint, "Began");
                yield return null;
                Assert.That(externalProbe.Downs.Exists(data => ReferenceEquals(data, originalData)), Is.True,
                    "本用例必须真正经历事件池复用，布局变化需更新明确版本适配而非把未覆盖报通过。");
                Queue(33, externalPoint, "Ended");
                Queue(44, externalPoint, "Ended");
                yield return null;
                Assert.That(externalProbe.Clicks[0], Is.EqualTo(3), "旧捕获不能按复用 eventData 的新 pointer/touchId 误取消外部手势。");
                modal.Close();
                Queue(55, ownPoint, "Began");
                yield return null;
                Queue(55, ownPoint, "Ended");
                yield return null;
                Assert.That(ownProbe.Clicks[0], Is.EqualTo(1));
            }
            finally
            {
                restore?.Invoke();
                input.GetMethod("RemoveDevice").Invoke(null, new[] { device });
            }
        }

        [UnityTest] public IEnumerator InputSystemThreeMouseButtonsCancelAndAcceptANewGesture() => DriveThreeMouseButtons(false);
        [UnityTest] public IEnumerator InputSystemScrollbarAndInputFieldUseNativeCancellation() => DriveScrollbarAndInputField(false);
        [UnityTest] public IEnumerator InputSystemSelectionLockCancelsPendingInputFieldAndEndsEditingOnce() => DriveFieldSelectionLock(false);
        [UnityTest] public IEnumerator InputSystemElasticScrollRectStopsOldDragAndInertia() => DriveScrollCancellation(false, UnityEngine.UI.ScrollRect.MovementType.Elastic);
        [UnityTest] public IEnumerator InputSystemClampedScrollRectStopsOldDragAndInertia() => DriveScrollCancellation(false, UnityEngine.UI.ScrollRect.MovementType.Clamped);
#endif
        private sealed class NativePointerDriver : IDisposable
        {
            internal Action<Vector2, ushort> Queue;
            internal Action Bind;
            internal Action Release;
            public void Dispose() => Release?.Invoke();
        }
        private NativePointerDriver CreateNativePointer(bool legacy)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (legacy)
            {
                var host = new GameObject("YLInput.StandardLegacy", typeof(UnityEngine.EventSystems.EventSystem), typeof(UnityEngine.EventSystems.StandaloneInputModule), typeof(YLCoreLegacyPointerInput));
                _objects.Add(host);
                var input = host.GetComponent<YLCoreLegacyPointerInput>();
                var nativeModule = host.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                nativeModule.inputOverride = input;
                nativeModule.forceModuleActive = true;
                return new NativePointerDriver
                {
                    Queue = (point, mask) => input.Set(point, (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0),
                    Bind = () => Assert.That(UnityEngine.EventSystems.EventSystem.current.currentInputModule, Is.SameAs(nativeModule))
                };
            }
#endif
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var stateType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem", true);
            var mouse = inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) }).Invoke(null, new object[] { "Mouse", "YLInput.StandardMouse", null });
            Action restore = null;
            return new NativePointerDriver
            {
                Queue = (point, mask) =>
                {
                    var state = Activator.CreateInstance(stateType);
                    stateType.GetField("position").SetValue(state, point);
                    stateType.GetField("buttons").SetValue(state, mask);
                    QueueInputState(inputType, stateType, mouse, state);
                },
                Bind = () =>
                {
                    var nativeModule = UnityEngine.EventSystems.EventSystem.current.currentInputModule;
                    Assert.That(nativeModule.GetType().FullName, Is.EqualTo("UnityEngine.InputSystem.UI.InputSystemUIInputModule"));
                    restore = UseOwnedInputDevices(nativeModule, mouse);
                },
                Release = () =>
                {
                    restore?.Invoke();
                    inputType.GetMethod("RemoveDevice").Invoke(null, new[] { mouse });
                }
            };
        }
        private IEnumerator DriveThreeMouseButtons(bool legacy)
        {
            yield return StartModule();
            using (var pointer = CreateNativePointer(legacy))
            {
                var root = PolicyRoot("YLInput.ThreeButtonsRoot");
                var source = Template<YLCoreSmokePanel>("YLInput.ThreeButtonsPanel", true);
                var target = new GameObject("Button", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCorePointerClickProbe));
                target.transform.SetParent(source.transform, false);
                ((RectTransform)target.transform).sizeDelta = new Vector2(200, 80);
                var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
                lower.gameObject.SetActive(true);
                yield return null;
                yield return null;
                pointer.Bind();
                Canvas.ForceUpdateCanvases();
                var button = lower.GetComponentInChildren<UnityEngine.UI.Button>();
                Assert.That(button.GetComponent<UnityEngine.EventSystems.IDragHandler>(), Is.Null, "普通 Button 不能被捕获组件变成拖拽目标。");
                var probe = button.GetComponent<YLCorePointerClickProbe>();
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, button.transform.position);
                for (int index = 0; index < 3; index++)
                {
                    ushort mask = (ushort)(1 << index);
                    pointer.Queue(point, 0);
                    yield return null;
                    pointer.Queue(point, mask);
                    yield return null;
                    var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ButtonModal" + index, true), root: root,
                        overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                    modal.gameObject.SetActive(true);
                    Assert.That(button.interactable && button.enabled, Is.True);
                    Assert.That(button.IsInteractable(), Is.False);
                    yield return null;
                    modal.Close();
                    pointer.Queue(point, 0);
                    yield return null;
                    Assert.That(probe.Clicks[index], Is.Zero, "旧按压不能在解除模态后生成 click。");
                    pointer.Queue(point, mask);
                    yield return null;
                    pointer.Queue(point, 0);
                    yield return null;
                    Assert.That(probe.Clicks[index], Is.EqualTo(1), "同一设备的新手势必须恢复。");
                }
            }
        }
        private IEnumerator DriveScrollbarAndInputField(bool legacy)
        {
            yield return StartModule();
            using (var pointer = CreateNativePointer(legacy))
            {
                var root = PolicyRoot("YLInput.StandardControlsRoot");
                var source = Template<YLCoreSmokePanel>("YLInput.StandardControls", true);
                var bar = new GameObject("Scrollbar", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Scrollbar));
                bar.transform.SetParent(source.transform, false);
                ((RectTransform)bar.transform).sizeDelta = new Vector2(360, 40);
                var handle = new GameObject("Handle", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                handle.transform.SetParent(bar.transform, false);
                var sourceBar = bar.GetComponent<UnityEngine.UI.Scrollbar>();
                sourceBar.handleRect = (RectTransform)handle.transform;
                sourceBar.targetGraphic = handle.GetComponent<UnityEngine.UI.Image>();
                sourceBar.size = .2f;
                var field = new GameObject("InputField", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.InputField));
                field.transform.SetParent(source.transform, false);
                ((RectTransform)field.transform).anchoredPosition = new Vector2(0, -100);
                ((RectTransform)field.transform).sizeDelta = new Vector2(360, 50);
                var text = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                text.transform.SetParent(field.transform, false);
                var label = text.GetComponent<UnityEngine.UI.Text>();
                label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                ((RectTransform)text.transform).sizeDelta = new Vector2(350, 45);
                field.GetComponent<UnityEngine.UI.InputField>().textComponent = label;
                var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
                lower.gameObject.SetActive(true);
                yield return null;
                yield return null;
                pointer.Bind();
                Canvas.ForceUpdateCanvases();
                var scrollbar = lower.GetComponentInChildren<UnityEngine.UI.Scrollbar>();
                var input = lower.GetComponentInChildren<UnityEngine.UI.InputField>();
                Vector2 barPoint = RectTransformUtility.WorldToScreenPoint(null, scrollbar.transform.TransformPoint(new Vector3(140, 0)));
                pointer.Queue(barPoint, 0);
                yield return null;
                pointer.Queue(barPoint, 1);
                yield return null;
                yield return null;
                Assert.That(scrollbar.value, Is.GreaterThan(0), "必须先证明原生点击持续滚动实际发生。");
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.StandardModal", true), root: root, overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                modal.gameObject.SetActive(true);
                float blockedValue = scrollbar.value;
                yield return null;
                yield return null;
                Assert.That(scrollbar.enabled && scrollbar.interactable, Is.True);
                Assert.That(scrollbar.value, Is.EqualTo(blockedValue), "取消必须结束 Scrollbar 的持续点击协程。");
                modal.Close();
                pointer.Queue(barPoint, 0);
                yield return null;
                Vector2 fieldPoint = RectTransformUtility.WorldToScreenPoint(null, input.transform.position);
                pointer.Queue(fieldPoint, 0);
                yield return null;
                pointer.Queue(fieldPoint, 1);
                yield return null;
                pointer.Queue(fieldPoint, 0);
                yield return null;
                yield return null;
                Assert.That(input.isFocused, Is.True, "必须由真实模块激活 InputField。");
                int edits = 0;
                input.onEndEdit.AddListener(_ => edits++);
                modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.FieldModal", true), root: root, overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                modal.gameObject.SetActive(true);
                Assert.That(input.isFocused, Is.False);
                Assert.That(edits, Is.EqualTo(1), "EndEdit 必须在应用物理门禁前正常收尾。");
                Assert.That(input.enabled && input.interactable, Is.True);
                modal.Close();
                pointer.Queue(fieldPoint, 1);
                yield return null;
                pointer.Queue(fieldPoint, 0);
                yield return null;
                yield return null;
                Assert.That(input.isFocused, Is.True, "解除后原生新手势必须重新激活输入。");
            }
        }
        private IEnumerator DriveScrollCancellation(bool legacy, UnityEngine.UI.ScrollRect.MovementType movement)
        {
            yield return StartModule();
            using (var pointer = CreateNativePointer(legacy))
            {
                var root = PolicyRoot("YLInput.ScrollInertiaRoot");
                var source = Template<YLCoreSmokePanel>("YLInput.ScrollInertia", true);
                var viewport = new GameObject("ScrollRect", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.ScrollRect), typeof(YLCoreNativeDragProbe));
                viewport.transform.SetParent(source.transform, false);
                ((RectTransform)viewport.transform).sizeDelta = new Vector2(240, 240);
                var content = new GameObject("Content", typeof(RectTransform));
                content.transform.SetParent(viewport.transform, false);
                var contentRect = (RectTransform)content.transform;
                contentRect.anchorMin = contentRect.anchorMax = contentRect.pivot = Vector2.up;
                contentRect.sizeDelta = new Vector2(800, 800);
                var configured = viewport.GetComponent<UnityEngine.UI.ScrollRect>();
                configured.viewport = (RectTransform)viewport.transform;
                configured.content = contentRect;
                configured.movementType = movement;
                configured.inertia = true;
                var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
                lower.gameObject.SetActive(true);
                yield return null;
                yield return null;
                pointer.Bind();
                Canvas.ForceUpdateCanvases();
                var scroll = lower.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
                var dragProbe = scroll.GetComponent<YLCoreNativeDragProbe>();
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, scroll.transform.position);
                pointer.Queue(point, 0);
                yield return null;
                pointer.Queue(point, 1);
                yield return null;
                pointer.Queue(point + new Vector2(-40, 40), 1);
                yield return null;
                pointer.Queue(point + new Vector2(-80, 80), 1);
                yield return null;
                // 保持按压，跨过真实 LateUpdate 后再检查拖拽速度。
                yield return null;
                Assert.That(scroll.content.anchoredPosition.sqrMagnitude, Is.GreaterThan(0), "先证明真实拖拽实际移动内容。");
                Assert.That(scroll.velocity.sqrMagnitude, Is.GreaterThan(0));
                if (movement == UnityEngine.UI.ScrollRect.MovementType.Elastic)
                {
                    pointer.Queue(point + new Vector2(180, -180), 1);
                    yield return null;
                }
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.InertiaModal", true), root: root, overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                modal.gameObject.SetActive(true);
                Vector2 stopped = scroll.content.anchoredPosition;
                int stoppedDrags = dragProbe.drags;
                Assert.That(scroll.velocity, Is.EqualTo(Vector2.zero));
                pointer.Queue(point + new Vector2(-60, 60), 1);
                yield return null;
                yield return null;
                Assert.That(dragProbe.drags, Is.EqualTo(stoppedDrags));
                if (movement == UnityEngine.UI.ScrollRect.MovementType.Clamped)
                {
                    Assert.That(scroll.content.anchoredPosition, Is.EqualTo(stopped));
                }

                Assert.That(scroll.enabled && scroll.inertia, Is.True);
                Assert.That(scroll.movementType, Is.EqualTo(movement));
                modal.Close();
                pointer.Queue(point + new Vector2(-20, 20), 1);
                yield return null;
                Assert.That(dragProbe.drags, Is.EqualTo(stoppedDrags), "解除后旧按压不能续拖；Elastic 自然回位不是续拖。");
                pointer.Queue(point, 0);
                yield return null;
                pointer.Queue(point, 1);
                yield return null;
                pointer.Queue(point + new Vector2(-30, 30), 1);
                yield return null;
                pointer.Queue(point + new Vector2(-70, 70), 1);
                yield return null;
                Assert.That(dragProbe.drags, Is.GreaterThan(stoppedDrags), "新拖拽应恢复。");
                pointer.Queue(point, 0);
                yield return null;
            }
        }

        private IEnumerator DriveFieldSelectionLock(bool legacy)
        {
            yield return StartModule();
            using (var pointer = CreateNativePointer(legacy))
            {
                var root = PolicyRoot("YLInput.FieldSelectionLockRoot");
                var source = Template<YLCoreSmokePanel>("YLInput.FieldSelectionLock", true);
                var fieldObject = new GameObject("Field", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.InputField), typeof(YLCoreSelectionInputProbe));
                fieldObject.transform.SetParent(source.transform, false);
                ((RectTransform)fieldObject.transform).sizeDelta = new Vector2(260, 50);
                var text = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                text.transform.SetParent(fieldObject.transform, false);
                ((RectTransform)text.transform).sizeDelta = new Vector2(250, 45);
                text.GetComponent<UnityEngine.UI.Text>().font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                fieldObject.GetComponent<UnityEngine.UI.InputField>().textComponent = text.GetComponent<UnityEngine.UI.Text>();
                var other = new GameObject("Next", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
                other.transform.SetParent(source.transform, false);
                ((RectTransform)other.transform).sizeDelta = new Vector2(100, 50);
                ((RectTransform)other.transform).anchoredPosition = new Vector2(0, -100);
                var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
                lower.gameObject.SetActive(true);
                yield return null;
                yield return null;
                pointer.Bind();
                var input = lower.GetComponentInChildren<UnityEngine.UI.InputField>();
                var button = lower.GetComponentInChildren<UnityEngine.UI.Button>();
                bool firstSelect = true;
                YLCoreSmokePanel modal = null;
                input.GetComponent<YLCoreSelectionInputProbe>().Selected = () =>
                {
                    if (!firstSelect)
                    {
                        return;
                    }

                    firstSelect = false;
                    modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.PendingFieldModal", true), root: root,
                        overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                    modal.gameObject.SetActive(true);
                    Assert.That(input.IsInteractable(), Is.False);
                };
                int edits = 0;
                input.onEndEdit.AddListener(_ => edits++);
                Canvas.ForceUpdateCanvases();
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, input.transform.position);
                pointer.Queue(point, 0);
                yield return null;
                pointer.Queue(point, 1);
                yield return null;
                pointer.Queue(point, 0);
                yield return null;
                yield return null;
                Assert.That(modal && lower.IsInputBlocked, Is.True);
                Assert.That(input.isFocused, Is.False, "原生 Select 已排队的延迟激活必须一并取消。");
                Assert.That(edits, Is.Zero, "尚未激活的字段不能伪造 EndEdit。");
                modal.Close();
                yield return null;
                yield return null;
                Assert.That(input.isFocused, Is.True, "解除后合法焦点恢复可重新激活字段。");
                bool onEndEdit = true;
                input.onEndEdit.AddListener(_ =>
                {
                    if (!onEndEdit)
                    {
                        return;
                    }

                    onEndEdit = false;
                    modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.EndEditSelectionModal", true), root: root,
                        overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                    modal.gameObject.SetActive(true);
                });
                Vector2 next = RectTransformUtility.WorldToScreenPoint(null, button.transform.position);
                pointer.Queue(next, 0);
                yield return null;
                pointer.Queue(next, 1);
                yield return null;
                pointer.Queue(next, 0);
                yield return null;
                yield return null;
                Assert.That(input.isFocused, Is.False);
                Assert.That(edits, Is.EqualTo(1), "原生 Deselect 的 EndEdit 内重入阻挡仍只能通知一次。");
                Assert.That(input.interactable && input.enabled, Is.True);
                modal.Close();
                pointer.Queue(point, 1);
                yield return null;
                pointer.Queue(point, 0);
                yield return null;
                yield return null;
                Assert.That(input.isFocused, Is.True);
                Assert.That(edits, Is.EqualTo(1));
            }
        }

        private static void QueueMouse(Type inputType, Type stateType, object mouse, Vector2 position, bool pressed, bool rightPressed = false)
        {
            var state = Activator.CreateInstance(stateType);
            stateType.GetField("position").SetValue(state, position);
            stateType.GetField("buttons").SetValue(state, (ushort)((pressed ? 1 : 0) | (rightPressed ? 2 : 0)));
            QueueInputState(inputType, stateType, mouse, state);
        }

        // 真实原生鼠标也会向默认 PassThrough actions 推送位置；仅限制本测试自建系统的 assets，不重置全局输入。
        private Action UseOwnedInputDevices(object module, params object[] devices)
        {
            var host = ((UnityEngine.EventSystems.BaseInputModule)module).gameObject;
            Assert.That(_priorEventSystems.Contains(host), Is.False, "Never change a pre-existing EventSystem's device filter.");
            var manager = typeof(PanelManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var owned = manager == null ? null : typeof(PanelManager).GetField("_eventSystem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Assert.That(ReferenceEquals(owned, host) || _objects.Contains(host), Is.True, "Only this fixture's input host may be filtered.");
            var asset = module.GetType().GetProperty("actionsAsset").GetValue(module);
#if UNITY_EDITOR
            Assert.That(UnityEditor.AssetDatabase.Contains((Object)asset), Is.False, "Only the owned runtime actions may be filtered, never persistent assets.");
#endif
            var property = asset.GetType().GetProperty("devices");
            var previous = property.GetValue(asset);
            var readonlyArray = property.PropertyType.GetGenericArguments()[0];
            var deviceType = readonlyArray.GetGenericArguments()[0];
            var array = Array.CreateInstance(deviceType, devices.Length);
            for (int i = 0; i < devices.Length; i++)
            {
                array.SetValue(devices[i], i);
            }

            property.SetValue(asset, Activator.CreateInstance(readonlyArray, new object[] { array }));
            return () => property.SetValue(asset, previous);
        }

        private static void QueueInputState(Type inputType, Type stateType, object device, object state)
        {
            foreach (var method in inputType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "QueueStateEvent" && method.IsGenericMethodDefinition)
                {
                    method.MakeGenericMethod(stateType).Invoke(null, new[] { device, state, (object)(-1d) });
                    return;
                }
            }

            Assert.Fail("Installed Input System does not expose QueueStateEvent<TState>.");
        }

#if ENABLE_INPUT_SYSTEM
        private static void QueueKeyboard(Type inputType, object keyboard, params string[] keyNames)
        {
            var keyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            var stateType = Type.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState, Unity.InputSystem");
            keyNames = keyNames ?? Array.Empty<string>();
            var keys = Array.CreateInstance(keyType, keyNames.Length);
            for (int i = 0; i < keyNames.Length; i++)
            {
                keys.SetValue(Enum.Parse(keyType, keyNames[i]), i);
            }

            var state = Activator.CreateInstance(stateType, new object[] { keys });
            QueueInputState(inputType, stateType, keyboard, state);
        }

        [UnityTest]
        public IEnumerator OnSelectOpeningModalBlocksInputSystemsSameFrameMoveAndSubmit()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.SelectSubmitRoot");
            var source = Template<YLCoreSmokePanel>("YLInput.SelectSubmitPanel", true);
            var a = new GameObject("From", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            var b = new GameObject("To", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCoreSelectionInputProbe));
            a.transform.SetParent(source.transform, false);
            b.transform.SetParent(source.transform, false);
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
            lower.gameObject.SetActive(true);
            yield return null;
            yield return null;
            var buttons = lower.GetComponentsInChildren<UnityEngine.UI.Button>();
            var from = buttons[0];
            var to = buttons[1];
            from.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.Explicit, selectOnRight = to };
            int clicks = 0;
            to.onClick.AddListener(() => clicks++);
            YLCoreSmokePanel modal = null;
            to.GetComponent<YLCoreSelectionInputProbe>().Selected = () =>
            {
                if (modal)
                {
                    return;
                }

                modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.SelectSubmitModal", true), root: root,
                    overrides: new PanelOverrides { Modal = true, Layer = 1 }));
                modal.gameObject.SetActive(true);
                Assert.That(lower.IsInputBlocked, Is.True);
                Assert.That(to.IsInteractable(), Is.False, "不能把逻辑阻挡当作选择锁期间的成功物理门禁。");
            };
            var system = UnityEngine.EventSystems.EventSystem.current;
            var input = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var keyboard = input.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) }).Invoke(null, new object[] { "Keyboard", "YLInput.SelectSubmitKeyboard", null });
            Action restore = null;
            try
            {
                restore = UseOwnedInputDevices(system.currentInputModule, keyboard);
                system.SetSelectedGameObject(from.gameObject);
                QueueKeyboard(input, keyboard, "RightArrow", "Enter");
                yield return null;
                QueueKeyboard(input, keyboard);
                yield return null;
                Assert.That(modal && lower.IsInputBlocked, Is.True);
                Assert.That(clicks, Is.Zero, "同一原生模块帧内的后续 Submit 不能到达下层 Button。");
                modal.Close();
                system.SetSelectedGameObject(to.gameObject);
                QueueKeyboard(input, keyboard, "Enter");
                yield return null;
                QueueKeyboard(input, keyboard);
                yield return null;
                Assert.That(clicks, Is.EqualTo(1));
            }
            finally
            {
                restore?.Invoke();
                input.GetMethod("RemoveDevice").Invoke(null, new[] { keyboard });
            }
        }

#if !ENABLE_LEGACY_INPUT_MANAGER
        [UnityTest]
        public IEnumerator InputSystemOnlyDiagnosticShortcutUsesF1WithoutLegacyPolling()
        {
            var type = Type.GetType("YLFramework.Audio.AudioKitDebugWindow, YLFramework.Runtime");
            Assert.That(type, Is.Not.Null);
            bool visible = (bool)type.GetProperty("IsVisible").GetValue(null);
            type.GetMethod("Hide").Invoke(null, null);
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            var keyboard = inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Keyboard", "YLInput.DiagnosticKeyboard", null });
            try
            {
                yield return null;
                QueueKeyboard(inputType, keyboard, "F1"); yield return null;
                QueueKeyboard(inputType, keyboard); yield return null;
                Assert.That((bool)type.GetProperty("IsVisible").GetValue(null), Is.True);
            }
            finally
            {
                type.GetMethod(visible ? "Show" : "Hide").Invoke(null, null);
                inputType.GetMethod("RemoveDevice").Invoke(null, new[] { keyboard });
            }
        }
#endif

        [UnityTest]
        public IEnumerator NewModalCancelsAlreadyCapturedScrollRectDrag()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.ScrollRoot");
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ScrollLower", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.GetComponent<RectTransform>().sizeDelta = new Vector2(400, 400);
            var scroll = lower.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            lower.gameObject.AddComponent<UnityEngine.UI.Image>();
            var content = new GameObject("Content", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            content.transform.SetParent(lower.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(600, 600);
            scroll.content = contentRect;
            scroll.viewport = lower.GetComponent<RectTransform>();
            scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Unrestricted;
            scroll.inertia = false;
            lower.gameObject.SetActive(true);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            var mouseType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");
            var mouse = inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Mouse", "YLInput.ScrollMouse", null });
            Action restoreDevices = null;
            try
            {
                restoreDevices = UseOwnedInputDevices(UnityEngine.EventSystems.EventSystem.current.currentInputModule, mouse);
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, lower.transform.position);
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, true);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point + new Vector2(40, 40), true);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point + new Vector2(60, 60), true);
                yield return null;
                Assert.That(contentRect.anchoredPosition.sqrMagnitude, Is.GreaterThan(1));
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.ScrollModal", true), root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
                modal.gameObject.SetActive(true);
                modal.UpdateDisplay(new PanelOverrides { Modal = true });
                Vector2 stopped = contentRect.anchoredPosition;
                QueueMouse(inputType, mouseType, mouse, point + new Vector2(100, 100), true);
                yield return null;
                Assert.That(contentRect.anchoredPosition, Is.EqualTo(stopped));
                modal.Close();
                QueueMouse(inputType, mouseType, mouse, point + new Vector2(120, 120), true);
                yield return null;
                Assert.That(contentRect.anchoredPosition, Is.EqualTo(stopped), "An old capture must not resume after the modal ends.");
                Assert.That(scroll.enabled, Is.True);
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
            }
            finally
            {
                restoreDevices?.Invoke();
                inputType.GetMethod("RemoveDevice").Invoke(null, new[] { mouse });
            }
        }

        [UnityTest]
        public IEnumerator RemovedMouseEndsHeldScrollbarRepeatAndAcceptsNewDevice() => RemoveDeviceDuringNativeGesture(false, false);
        [UnityTest]
        public IEnumerator RemovedTouchscreenEndsHeldScrollbarRepeatAndAcceptsNewDevice() => RemoveDeviceDuringNativeGesture(true, false);
        [UnityTest]
        public IEnumerator RemovedMouseEndsScrollDragAndInertiaAndAcceptsNewDevice() => RemoveDeviceDuringNativeGesture(false, true);
        [UnityTest]
        public IEnumerator RemovedTouchscreenEndsScrollDragAndInertiaAndAcceptsNewDevice() => RemoveDeviceDuringNativeGesture(true, true);

        private IEnumerator RemoveDeviceDuringNativeGesture(bool touch, bool scrolling)
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.RemovalCleanupRoot");
            var source = Template<YLCoreSmokePanel>("YLInput.RemovalCleanupPanel", true);
            var control = new GameObject("Control", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            control.transform.SetParent(source.transform, false);
            ((RectTransform)control.transform).sizeDelta = new Vector2(360, scrolling ? 240 : 40);
            if (scrolling)
            {
                var scroll = control.AddComponent<UnityEngine.UI.ScrollRect>();
                control.AddComponent<YLCoreNativeDragProbe>();
                var content = new GameObject("Content", typeof(RectTransform));
                content.transform.SetParent(control.transform, false);
                var rect = (RectTransform)content.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.up;
                rect.sizeDelta = new Vector2(800, 800);
                scroll.content = rect;
                scroll.viewport = (RectTransform)control.transform;
                scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Unrestricted;
                scroll.inertia = true;
            }
            else
            {
                var bar = control.AddComponent<UnityEngine.UI.Scrollbar>();
                var handle = new GameObject("Handle", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                handle.transform.SetParent(control.transform, false);
                bar.handleRect = (RectTransform)handle.transform;
                bar.targetGraphic = handle.GetComponent<UnityEngine.UI.Image>();
                bar.size = .1f;
            }
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(source, root: root));
            panel.gameObject.SetActive(true);
            yield return null;
            yield return null;
            Canvas.ForceUpdateCanvases();
            var scrollRect = panel.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
            var scrollbar = panel.GetComponentInChildren<UnityEngine.UI.Scrollbar>();
            var target = scrolling ? scrollRect.transform : scrollbar.transform;
            Vector2 point = RectTransformUtility.WorldToScreenPoint(null, target.TransformPoint(scrolling ? Vector3.zero : new Vector3(145, 0)));
            var input = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var stateType = Type.GetType(touch ? "UnityEngine.InputSystem.LowLevel.TouchState, Unity.InputSystem" : "UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem", true);
            var phaseType = touch ? Type.GetType("UnityEngine.InputSystem.TouchPhase, Unity.InputSystem", true) : null;
            var add = input.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) });
            var remove = input.GetMethod("RemoveDevice");
            object device = add.Invoke(null, new object[] { touch ? "Touchscreen" : "Mouse", "YLInput.RemovalCleanupDevice", null });
            Action restore = null;
            void Queue(Vector2 position, bool down, bool began = false)
            {
                var state = Activator.CreateInstance(stateType);
                stateType.GetField("position").SetValue(state, position);
                if (touch)
                {
                    stateType.GetField("touchId").SetValue(state, 42);
                    stateType.GetProperty("phase").SetValue(state, Enum.Parse(phaseType, down ? began ? "Began" : "Moved" : "Ended"));
                }
                else
                {
                    stateType.GetField("buttons").SetValue(state, (ushort)(down ? 1 : 0));
                }

                QueueInputState(input, stateType, device, state);
            }
            try
            {
                var moduleInput = UnityEngine.EventSystems.EventSystem.current.currentInputModule;
                restore = UseOwnedInputDevices(moduleInput, device);
                if (!touch)
                {
                    Queue(point, false);
                    yield return null;
                }
                Queue(point, true, true);
                yield return null;
                if (scrolling)
                {
                    Queue(point + new Vector2(-35, 35), true);
                    yield return null;
                    Queue(point + new Vector2(-70, 70), true);
                    yield return null;
                    // 保持按压，跨过真实 LateUpdate 后再检查拖拽速度。
                    yield return null;
                    Assert.That(scrollRect.GetComponent<YLCoreNativeDragProbe>().drags, Is.GreaterThan(0));
                    Assert.That(scrollRect.velocity.sqrMagnitude, Is.GreaterThan(0));
                }
                else
                {
                    Assert.That(scrollbar.value, Is.GreaterThan(0), "先确认真实按住重复已启动。");
                }

                remove.Invoke(null, new[] { device });
                device = null;
                yield return null;
                yield return null;
                int drags = scrolling ? scrollRect.GetComponent<YLCoreNativeDragProbe>().drags : 0;
                if (scrolling)
                {
                    Assert.That(typeof(UnityEngine.UI.ScrollRect).GetField("m_Dragging", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(scrollRect), Is.False);
                    Assert.That(scrollRect.velocity, Is.EqualTo(Vector2.zero));
                    Assert.That(scrollRect.enabled && scrollRect.inertia, Is.True);
                }
                else
                {
                    scrollbar.value = .3f;
                    yield return null;
                    yield return null;
                    Assert.That(scrollbar.value, Is.EqualTo(.3f), "移除设备后旧持续点击不能改写业务新值。");
                    Assert.That(scrollbar.enabled && scrollbar.interactable, Is.True);
                }
                restore();
                restore = null;
                device = add.Invoke(null, new object[] { touch ? "Touchscreen" : "Mouse", "YLInput.ReplacementDevice", null });
                restore = UseOwnedInputDevices(moduleInput, device);
                if (!touch)
                {
                    Queue(point, false);
                    yield return null;
                }
                Queue(point, true, true);
                yield return null;
                if (scrolling)
                {
                    Queue(point + new Vector2(-30, 30), true);
                    yield return null;
                    Queue(point + new Vector2(-60, 60), true);
                    yield return null;
                    Assert.That(scrollRect.GetComponent<YLCoreNativeDragProbe>().drags, Is.GreaterThan(drags));
                }
                else
                {
                    Assert.That(scrollbar.value, Is.GreaterThan(.3f));
                }

                Queue(point, false);
                yield return null;
            }
            finally
            {
                restore?.Invoke();
                if (device != null)
                {
                    remove.Invoke(null, new[] { device });
                }
            }
        }

        [UnityTest]
        public IEnumerator RemovingPressedDeviceReleasesInvalidControlCapture()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.RemovedDeviceRoot");
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.RemovedDeviceLower", true), root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            var sliderObject = new GameObject("CapturedSlider", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Slider));
            sliderObject.transform.SetParent(lower.transform, false);
            var slider = sliderObject.GetComponent<UnityEngine.UI.Slider>();
            lower.gameObject.SetActive(true);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            var stateType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");
            var mouse = inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Mouse", "YLInput.RemovedMouse", null });
            var remove = inputType.GetMethod("RemoveDevice");
            Action restore = null;
            bool removed = false;
            try
            {
                restore = UseOwnedInputDevices(UnityEngine.EventSystems.EventSystem.current.currentInputModule, mouse);
                var point = RectTransformUtility.WorldToScreenPoint(null, slider.transform.position);
                QueueMouse(inputType, stateType, mouse, point, false);
                yield return null;
                QueueMouse(inputType, stateType, mouse, point, true);
                yield return null;
                var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(Template<YLCoreSmokePanel>("YLInput.RemovedDeviceModal", true), root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
                modal.gameObject.SetActive(true);
                modal.UpdateDisplay(new PanelOverrides { Modal = true });
                Assert.That(slider.enabled, Is.True);
                Assert.That(slider.IsInteractable(), Is.False);
                remove.Invoke(null, new[] { mouse });
                removed = true;
                yield return null;
                modal.Close();
                yield return null;
                Assert.That(slider.enabled, Is.True, "Removed controls cannot retain an invalid capture or read device state.");
            }
            finally
            {
                restore?.Invoke();
                if (!removed)
                {
                    remove.Invoke(null, new[] { mouse });
                }
            }
        }

        [UnityTest]
        public IEnumerator OldPointerPressCannotClickAReusedWindowSession()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.OldPressRoot");
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var prefab = Template<YLCoreSmokePanel>("YLInput.OldPressPanel", true);
            var child = new GameObject("Button", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            child.GetComponent<RectTransform>().sizeDelta = new Vector2(100, 100);
            child.transform.SetParent(prefab.transform, false);
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            int clicks = 0;
            var button = lower.GetComponentInChildren<UnityEngine.UI.Button>();
            button.onClick.AddListener(() => clicks++);
            yield return null;
            Canvas.ForceUpdateCanvases();
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            var mouseType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");
            var mouse = inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Mouse", "YLInput.OldPressMouse", null });
            Action restoreDevices = null;
            try
            {
                restoreDevices = UseOwnedInputDevices(UnityEngine.EventSystems.EventSystem.current.currentInputModule, mouse);
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, button.transform.position);
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, true);
                yield return null;
                lower.Close();
                var next = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
                Assert.That(next, Is.SameAs(lower));
                next.gameObject.SetActive(true);
                QueueMouse(inputType, mouseType, mouse, point, true, true);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, false, true);
                yield return null;
                Assert.That(clicks, Is.Zero, "Up from an old session cannot invoke a new session's Button.");
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, true);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                Assert.That(clicks, Is.EqualTo(1));
            }
            finally
            {
                restoreDevices?.Invoke();
                inputType.GetMethod("RemoveDevice").Invoke(null, new[] { mouse });
            }
        }

        [UnityTest]
        public IEnumerator NativeDownOnSelectReuseCannotAuthorizeOldPressForNewSession()
        {
            yield return StartModule();
            var root = PolicyRoot("YLInput.DownSelectRoot");
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var prefab = Template<YLCoreSmokePanel>("YLInput.DownSelectPanel", true);
            var child = new GameObject("Button", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCoreSelectionInputProbe));
            child.GetComponent<RectTransform>().sizeDelta = new Vector2(100, 100);
            child.transform.SetParent(prefab.transform, false);
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
            lower.gameObject.SetActive(true);
            int clicks = 0;
            var button = lower.GetComponentInChildren<UnityEngine.UI.Button>();
            button.onClick.AddListener(() => clicks++);
            YLCoreSmokePanel replacement = null;
            button.GetComponent<YLCoreSelectionInputProbe>().Selected = () =>
            {
                lower.Close();
                replacement = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root,
                    overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple }));
                replacement.GetComponentInChildren<YLCoreSelectionInputProbe>().Selected = null;
                replacement.gameObject.SetActive(true);
            };
            yield return null;
            Canvas.ForceUpdateCanvases();
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            var mouseType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");
            var mouse = inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(null, new object[] { "Mouse", "YLInput.DownSelectMouse", null });
            Action restoreDevices = null;
            try
            {
                restoreDevices = UseOwnedInputDevices(UnityEngine.EventSystems.EventSystem.current.currentInputModule, mouse);
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, button.transform.position);
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, true);
                yield return null;
                Assert.That(replacement, Is.SameAs(lower));
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                Assert.That(clicks, Is.Zero);
                QueueMouse(inputType, mouseType, mouse, point, true);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                Assert.That(clicks, Is.EqualTo(1));
            }
            finally
            {
                restoreDevices?.Invoke();
                inputType.GetMethod("RemoveDevice").Invoke(null, new[] { mouse });
            }
        }

        [UnityTest]
        public IEnumerator InputSystemModalMaskSubmitCancelAndSliderNavigationUseCurrentSession()
        {
            yield return StartModule();
#if UNITY_EDITOR
            var template = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.kano.ylframework/Prefabs/YLFramework.prefab");
            var host = Object.Instantiate(template);
            _objects.Add(host);
            var context = host.GetComponentInChildren<UIRootContext>();
            var prefab = Template<YLCoreSmokePanel>("YLInput.NavigationWindow", true);
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: context,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 0 }));
            lower.gameObject.SetActive(true);
            var lowerButton = new GameObject("LowerButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            lowerButton.transform.SetParent(lower.transform, false);
            int lowerClicks = 0, modalClicks = 0;
            lowerButton.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => lowerClicks++);
            var sliderObject = new GameObject("Slider", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Slider));
            sliderObject.transform.SetParent(lower.transform, false);
            var slider = sliderObject.GetComponent<UnityEngine.UI.Slider>();
            var system = UnityEngine.EventSystems.EventSystem.current;
            yield return null;
            system.SetSelectedGameObject(lowerButton);
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: context,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 3, Modal = true }));
            var modalButton = new GameObject("ModalButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(YLCoreSelectionInputProbe));
            modalButton.transform.SetParent(modal.transform, false);
            modalButton.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => modalClicks++);
            modal.gameObject.SetActive(true);
            modal.UpdateDisplay(default);
            yield return null;
            Assert.That(system.currentSelectedGameObject, Is.SameAs(modalButton));
            Assert.That(lowerButton.GetComponent<UnityEngine.UI.Button>().IsInteractable(), Is.False);
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            var mouseType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");
            var add = inputType.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) });
            var mouse = add.Invoke(null, new object[] { "Mouse", "YLInput.MaskMouse", null });
            var keyboard = add.Invoke(null, new object[] { "Keyboard", "YLInput.NavigationKeyboard", null });
            Action restoreDevices = null;
            try
            {
                restoreDevices = UseOwnedInputDevices(system.currentInputModule, mouse, keyboard);
                Canvas.ForceUpdateCanvases();
                var point = new Vector2(10, 10);
                var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                system.RaycastAll(new UnityEngine.EventSystems.PointerEventData(system) { position = point }, hits);
                Assert.That(hits, Is.Not.Empty);
                Assert.That(hits[0].gameObject.name, Is.EqualTo("Mask"));
                Assert.That(UIPointer.CheckPositionOnUI(point), Is.True, "Mask is a world-input blocker, not an empty UI result.");
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, true);
                yield return null;
                QueueMouse(inputType, mouseType, mouse, point, false);
                yield return null;
                Assert.That(lowerClicks, Is.Zero);
                // 点击非 Selectable 遮罩可清空选择；显式选择模态按钮后验证真正后端的导航事件。
                system.SetSelectedGameObject(modalButton);
                QueueKeyboard(inputType, keyboard, "Enter");
                yield return null;
                QueueKeyboard(inputType, keyboard);
                yield return null;
                Assert.That(modalClicks, Is.EqualTo(1));
                Assert.That(lowerClicks, Is.Zero);
                QueueKeyboard(inputType, keyboard, "Escape");
                yield return null;
                QueueKeyboard(inputType, keyboard);
                yield return null;
                Assert.That(modalButton.GetComponent<YLCoreSelectionInputProbe>().cancels, Is.EqualTo(1));
                modal.Close();
                Assert.That(system.currentSelectedGameObject, Is.SameAs(lowerButton));
                QueueKeyboard(inputType, keyboard, "Enter");
                yield return null;
                QueueKeyboard(inputType, keyboard);
                yield return null;
                Assert.That(lowerClicks, Is.EqualTo(1));
                system.SetSelectedGameObject(sliderObject);
                float oldValue = slider.value;
                QueueKeyboard(inputType, keyboard, "RightArrow");
                yield return null;
                QueueKeyboard(inputType, keyboard);
                yield return null;
                Assert.That(slider.value, Is.GreaterThan(oldValue));
                var tip = host.GetComponentInChildren<UITipsPresenter>().Show(_module, "Adapted input", UITipPos.RightTop);
                Assert.That(((RectTransform)tip.Item.transform.parent).anchorMin, Is.EqualTo(Vector2.one));
                tip.Dispose();
            }
            finally
            {
                restoreDevices?.Invoke();
                var remove = inputType.GetMethod("RemoveDevice");
                remove.Invoke(null, new[] { mouse });
                remove.Invoke(null, new[] { keyboard });
            }
#endif
        }
#endif
#endif

        [UnityTest]
        public IEnumerator MigratedRootTemplateHasLayersMasksAndBoundTipsWithoutLegacyComponents()
        {
            yield return StartModule();
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.kano.ylframework/Prefabs/YLFramework.prefab");
            var instance = Object.Instantiate(prefab);
            _objects.Add(instance);
            var context = instance.GetComponentInChildren<UIRootContext>(true);
            var presenter = instance.GetComponentInChildren<UITipsPresenter>(true);
            Assert.That(context && presenter, Is.True);
            var panelPrefab = Template<YLCoreSmokePanel>("YLWindow.LayerTemplate", true);
            panelPrefab.AddComponent<UnityEngine.UI.Image>();
            panelPrefab.SetActive(true);
            var lower = Track(_module.LoadPanel<YLCoreSmokePanel>(panelPrefab, root: context,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 0, Priority = int.MaxValue }));
            var modal = Track(_module.LoadPanel<YLCoreSmokePanel>(panelPrefab, root: context,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 3, Modal = true }));
            Assert.That(modal.Root, Is.SameAs(lower.Root));
            Assert.That(modal.SortingOrder, Is.GreaterThan(lower.SortingOrder), "业务 Priority 不得跨配置 Layer。");
            Assert.That(lower.IsInputBlocked, Is.True);
            var masks = context.GetLayer(3).GetComponentsInChildren<UnityEngine.UI.Image>(true);
            UnityEngine.UI.Image mask = null;
            foreach (var image in masks)
            {
                if (image.gameObject.name == "Mask")
                {
                    mask = image;
                }
            }

            Assert.That(mask, Is.Not.Null);
            Assert.That(mask.gameObject.activeSelf && mask.raycastTarget, Is.True);
            Assert.That(mask.GetComponent<Canvas>().sortingOrder, Is.EqualTo(modal.SortingOrder - 1));
            var placement = presenter.Show(_module, "Right top", UITipPos.RightTop);
            Assert.That(((RectTransform)placement.Item.transform.parent).anchorMin, Is.EqualTo(Vector2.one));
            Assert.That(placement.Item.SortingOrder, Is.GreaterThan(modal.SortingOrder), "模板 Tips Variant 的 Layer 必须高于示例模态层。");
            placement.Dispose();
            var tip = presenter.Show(_module, "Migrated template", UITipPos.RightTop, duration: 0);
            Assert.That(tip.IsDone, Is.True);
            Assert.That(presenter.ActiveCount, Is.Zero);
            modal.Close();
            Assert.That(mask.gameObject.activeSelf, Is.False);
            Assert.That(lower.IsInputBlocked, Is.False);
            lower.Close();
            modal = Track(_module.LoadPanel<YLCoreSmokePanel>(panelPrefab, root: context,
                overrides: new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 3, Modal = true }));
            int order = modal.SortingOrder;
            context.Canvas.sortingOrder = short.MaxValue - 2;
            modal.UpdateDisplay(default);
            order = modal.SortingOrder;
            var other = NewArgs();
            Assert.Throws<ArgumentOutOfRangeException>(() => _module.LoadPanel<YLCoreSmokePanel>(panelPrefab, other, context,
                new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, Layer = 0 }));
            AssertArgsReleased(other);
            Assert.That(modal.SortingOrder, Is.EqualTo(order), "模态遮罩空间不足应在新实例提交前拒绝。");
            modal.Close();
            foreach (var component in instance.GetComponentsInChildren<MonoBehaviour>(true))
            {
                Assert.That(component, Is.Not.Null, "Migrated template must import without missing scripts.");
                Assert.That(component.GetType().Namespace, Is.Not.EqualTo("YLFramework.Services.UI"));
            }
#endif
        }

        [UnityTest]
        public IEnumerator PanelEnableReentryNeverAppliesOldArgumentsToReplacementSession()
        {
            yield return StartModule();
            var root = PolicyRoot("YLWindow.ActivationRoot");
            var prefab = Template<YLCoreSmokePanel>("YLWindow.Activation", true);
            var old = NewArgs();
            old.SetInt("token", 11);
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, old, root));
            var session = ViewSession(first);
            first.gameObject.SetActive(false);
            YLCoreSmokePanel replacement = null;
            int updates = 0;
            YLCoreSmokePanel.UpdateProbe = (_, __) => updates++;
            YLCoreSmokePanel.EnabledProbe = panel =>
            {
                YLCoreSmokePanel.EnabledProbe = null;
                panel.Close();
                var next = NewArgs();
                next.SetInt("token", 22);
                replacement = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, next, root));
            };
            try
            {
                first.gameObject.SetActive(true);
                Assert.That(replacement, Is.SameAs(first));
                Assert.That(ViewSession(replacement), Is.Not.SameAs(session));
                Assert.That(replacement.Token, Is.EqualTo(22));
                Assert.That(updates, Is.EqualTo(1), "只执行 replacement 本次 OnOpen，旧激活栈不能重放数据。");
                AssertArgsReleased(old);
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator SinglePendingRootIsFrozenAcrossRebindingAndInvalidatesBeforeCommit()
        {
            yield return StartModule();
            var a = PolicyRoot("YLWindow.PendingA");
            var b = PolicyRoot("YLWindow.PendingB");
            var prefab = Template<YLCoreSmokePanel>("YLWindow.PendingMigration", true);
            _module.PanelRoot = a;
            var pending = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab);
            _module.PanelRoot = b;
            var current = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab));
            Object.DestroyImmediate(a.gameObject);
            yield return pending;
            Assert.That(pending.Status, Is.EqualTo(LoadViewStatus.Invalidated));
            Assert.That(pending.Panel, Is.Null);
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Has.Count.EqualTo(1));
            Assert.That(current.Root, Is.SameAs(b));
            _module.PanelRoot = null;
        }

        [UnityTest]
        public IEnumerator SingleReopenRespectsConfiguredPriorityAndBringsEqualPriorityToFront()
        {
            yield return StartModule();
            var other = new YLCoreBaseProbeModule();
            yield return ModuleManager.StartUpModule(other);
            try
            {
                var root = PolicyRoot("YLWindow.OffsetRoot");
                var prefab = Template<YLCoreSmokePanel>("YLWindow.Offset", true);
                _module.BindPanelRules(new[] { new PanelRule(prefab, new PanelOverrides { Priority = 10 }) });
                other.BindPanelRules(new[] { new PanelRule(prefab, new PanelOverrides { Priority = 20 }) });
                var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
                var second = Track(other.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
                first.gameObject.SetActive(true);
                second.gameObject.SetActive(true);
                Assert.That(second.IsTop(), Is.True);
                _module.LoadPanel<YLCoreSmokePanel>(prefab, root: root);
                Assert.That(second.IsTop(), Is.True, "重开不能跨其他 Module 的固定更高 Priority。");
                _module.BindPanelRules(new[] { new PanelRule(prefab, new PanelOverrides { Priority = 20 }) });
                _module.LoadPanel<YLCoreSmokePanel>(prefab, root: root);
                Assert.That(first.IsTop(), Is.True);
                second.BringToFront();
                Assert.That(second.IsTop(), Is.True);
                _module.LoadPanel<YLCoreSmokePanel>(prefab, root: root);
                Assert.That(first.IsTop(), Is.True);
            }
            finally
            {
                other.AllowClose = true;
                other.Close();
                _classPool.Restore(new Module[] { other });
            }
        }

        [UnityTest]
        public IEnumerator TipsHostReactivationDuringExitCannotBorrowFromTheRetiringPool()
        {
            yield return StartModule();
            var presenter = CreateTips(PolicyRoot("YLTips.Reactivation"));
            var handle = presenter.Show(_module, "old host", completed: _ =>
            {
                presenter.enabled = true;
                Assert.Throws<InvalidOperationException>(() => presenter.Show(_module, "nested host"));
            });
            presenter.enabled = false;
            Assert.That(handle.IsDone, Is.True);
            Assert.That(presenter.ActiveCount, Is.Zero);
            var next = presenter.Show(_module, "new host");
            next.Dispose();
            Assert.That(next.IsDone, Is.True);
            Assert.That(presenter.ActiveCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ReopenCallbackEndingItsSessionDoesNotPublishAClosedWindowAsSuccess()
        {
            yield return StartModule();
            var root = PolicyRoot("YLWindow.UpdateExitRoot");
            var prefab = Template<YLCoreSmokePanel>("YLWindow.UpdateExit", true);
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
            YLCoreSmokePanel.UpdateProbe = (panel, _) => panel.Close();
            try
            {
                var request = _module.LoadPanelAsync<YLCoreSmokePanel>(prefab, root: root);
                Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
                yield return request;
                Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Failed));
                Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(request.Panel, Is.Null);
                Assert.That(first.Module, Is.Null);
                Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty);
                YLCoreSmokePanel.UpdateProbe = null;
                var replacement = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, root: root));
                Assert.That(replacement.IsLoaded && replacement.IsVisible, Is.True, "OnOpen 标记失败后必须准确解除。");
            }
            finally
            {
                YLCoreSmokePanel.ResetProbes();
            }
        }

        private UIRootContext PolicyRoot(string name)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
            root.SetActive(false);
            _objects.Add(root);
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            return CorePanelTestRoots.Create(root);
        }

        private IEnumerator StartModule()
        {
            var request = ModuleManager.StartUpModule(_module);
            var deadline = Time.realtimeSinceStartup + 10;
            while (!request.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Error, Is.Null.Or.Empty);
        }

        private static object ViewSession(View view) => typeof(View).GetField("Session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);

        private static object InvokeViewManager(string methodName, params object[] arguments)
        {
            var type = typeof(View).Assembly.GetType("YLFramework.Core.ViewManager", true);
            MethodInfo method = null;
            foreach (var candidate in type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (candidate.Name != methodName || candidate.IsGenericMethod || candidate.GetParameters().Length != arguments.Length)
                {
                    continue;
                }

                method = candidate;
                break;
            }
            Assert.That(method, Is.Not.Null, methodName + " overload must exist.");
            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private GameObject Template<T>(string name, bool rect = false) where T : View
        {
            var prefab = rect ? new GameObject(name, typeof(RectTransform)) : new GameObject(name);
            prefab.SetActive(false);
            prefab.AddComponent<T>();
            _objects.Add(prefab);
            _prefabKeys.Add(prefab.GetHashCode());
            return prefab;
        }

        private T Track<T>(T view) where T : View
        {
            if (view && !_objects.Contains(view.gameObject))
            {
                _objects.Add(view.gameObject);
                _instanceKeys.Add(view.gameObject.GetHashCode());
            }
            return view;
        }

    }
}

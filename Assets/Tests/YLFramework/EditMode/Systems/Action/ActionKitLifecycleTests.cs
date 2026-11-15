using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Timing;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Actions
{
    public abstract class ActionTimingSceneFixture
    {
        protected const string OwnedScene = "Assets/YLTestingProfile/Empty.unity";
        private bool _optionsEnabled;
        private EnterPlayModeOptions _options;
        private bool _background;
        private SceneSetup[] _previousScenes;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _optionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _options = EditorSettings.enterPlayModeOptions;
            _background = Application.runInBackground;
            if (!Application.isBatchMode)
            {
                // Manual visual runs must let Editor startup finish before changing scenes or entering Play.
                var ready = false;
                EditorApplication.CallbackFunction fence = () => ready = true;
                EditorApplication.delayCall += fence;
                try
                {
                    while (!ready)
                        yield return null;
                }
                finally
                {
                    EditorApplication.delayCall -= fence;
                }
            }
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount)
                .All(i => !SceneManager.GetSceneAt(i).isDirty), Is.True, "Never save or discard dirty scenes.");
            var preparation = SceneManager.GetSceneByPath(OwnedScene);
            Assert.That(preparation.IsValid() && preparation.isLoaded, Is.True, "Use the tool-owned saved preparation scene.");
            _previousScenes = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.OpenScene(OwnedScene, OpenSceneMode.Single);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                if (EditorApplication.isPlaying)
                    yield return new ExitPlayMode();
            }
            finally
            {
                try
                {
                    if (_previousScenes != null)
                        EditorSceneManager.RestoreSceneManagerSetup(_previousScenes);
                    _previousScenes = null;
                }
                finally
                {
                    EditorSettings.enterPlayModeOptionsEnabled = _optionsEnabled;
                    EditorSettings.enterPlayModeOptions = _options;
                    Application.runInBackground = _background;
                }
            }
        }

    }

    [Category("YL.Special.ActionTimingSession")]
    public class ActionTimingSessionTests : ActionTimingSceneFixture
    {

        private static int ActionOwners => ((IDictionary)typeof(MonoUpdateActionExecutor)
            .GetField("_owners", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count;

        [Test]
        public void ManualVisualEntryRejectsBatchWithoutSchedulingOrChangingScenes()
        {
            Assert.That(Application.isBatchMode, Is.True);
            var entry = typeof(ActionVisualTests).GetMethod("RunManualAcceptance", BindingFlags.Public | BindingFlags.Static);
            Assert.That(entry, Is.Not.Null, "Manual acceptance needs its own non-exiting entry, not YLTestEntry.Execute.");
            var scenes = EditorSceneManager.GetSceneManagerSetup();
            var playing = EditorApplication.isPlayingOrWillChangePlaymode;
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Manual visual acceptance requires an interactive owned host"));
            entry.Invoke(null, null);
            CollectionAssert.AreEqual(scenes.Select(scene => scene.path), EditorSceneManager.GetSceneManagerSetup().Select(scene => scene.path));
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.EqualTo(playing));
            Assert.That(typeof(ActionVisualTests).GetField("_manualApi", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.Null);
        }

        [Test]
        public void ManualCaptureTargetsTheGameViewsOwnRenderTexture()
        {
            var gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView", true);
            var texture = gameView.GetField("m_RenderTexture", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(texture, Is.Not.Null, "Do not fall back to an unrelated active framebuffer.");
            Assert.That(texture.FieldType, Is.EqualTo(typeof(RenderTexture)));
        }

        [TestCase("default")]
        [TestCase("dirty")]
        [TestCase("extra")]
        public void ManualRunStartedNormalizesOnlyThePristineUtfBootstrapScene(string kind)
        {
            var callbacks = typeof(ActionVisualTests).GetNestedType("ManualCallbacks", BindingFlags.NonPublic);
            var instance = Activator.CreateInstance(callbacks, true);
            var started = callbacks.GetMethod("RunStarted");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            try
            {
                if (kind == "dirty")
                    EditorSceneManager.MarkSceneDirty(scene);
                if (kind == "extra")
                    new GameObject("Unrecognized").transform.SetParent(scene.GetRootGameObjects()[0].transform);
                if (kind == "default")
                {
                    started.Invoke(instance, new object[] { null });
                    Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(OwnedScene));
                    Assert.That(SceneManager.GetActiveScene().rootCount, Is.Zero);
                }
                else
                {
                    var error = Assert.Throws<TargetInvocationException>(() => started.Invoke(instance, new object[] { null }));
                    Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                    Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene), "Unknown/dirty scenes must not be replaced.");
                }
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void InteractiveBootstrapOnlyRecognizesTheUntouchedDefaultScene()
        {
            var entry = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YLFramework.Testing.Editor.YLTestEntry"))
                .First(type => type != null);
            var predicate = entry.GetMethod("IsUntouchedDefaultScene", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(predicate, Is.Not.Null);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            try
            {
                Assert.That(predicate.Invoke(null, new object[] { scene }), Is.True);
                EditorSceneManager.MarkSceneDirty(scene);
                Assert.That(predicate.Invoke(null, new object[] { scene }), Is.False, "A dirty scene must never be replaced.");
                scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                new GameObject("Extra").transform.SetParent(scene.GetRootGameObjects()[0].transform);
                Assert.That(predicate.Invoke(null, new object[] { scene }), Is.False, "Default names cannot authorize unknown content.");
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [UnityTest]
        public IEnumerator TwoRealPlaysKeepActionHandlesAndTimerWaitSessionBoundariesIsolated()
        {
            yield return new EnterPlayMode(false);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(OwnedScene));
            var owner = new GameObject("ActionTiming.FirstOwner").AddComponent<MonoUpdateActionExecutor>();
            var completed = 0;
            var old = Kit.Condition(() => false).StartHandle(owner, () => completed++);
            var named = Kit.Condition(() => false).StartNamed(owner, "session", () => completed++);
            var timer = Timing.Timer.Delay(1000, () => completed++);
            Assert.That(timer.Pause(), Is.True);
            var wait = timer.WaitForTermination();
            yield return null;
            Assert.That(old.IsActive && named.IsActive && wait.keepWaiting, Is.True);
            yield return new ExitPlayMode();

            Assert.That(old.IsActive || named.IsActive, Is.False);
            Assert.That(ActionOwners, Is.Zero, "Exit must remove ownership, not merely invalidate IsActive.");
            Assert.That(timer.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(wait.keepWaiting, Is.False);
            Assert.That(wait.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(completed, Is.Zero);

            yield return new EnterPlayMode(false);
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(OwnedScene));
            var current = Kit.Condition(() => false).StartHandleGlobal();
            var currentTimerCalls = 0;
            Timing.Timer.Delay(0, () => currentTimerCalls++);
            Assert.That(old.Cancel() || named.Cancel() || timer.Cancel(), Is.False);
            for (var frame = 0; frame < 4; frame++)
                yield return null;
            Assert.That(currentTimerCalls, Is.EqualTo(1));
            Assert.That(current.IsActive, Is.True);
            Assert.That(completed, Is.Zero);
            Assert.That(wait.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(current.Cancel(), Is.True);
            yield return new ExitPlayMode();
            Assert.That(ActionOwners, Is.Zero);
        }

        private static IEnumerator CountFrames(Action tick)
        {
            while (true)
            {
                tick();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator ModuleShutdownDoesNotTakeOverTheOtherModuleOrSharedUpdateWork()
        {
            yield return new EnterPlayMode(false);
            var updates = 0;
            var frames = 0;
            var subscription = Update.UpdateManager.AddUpdateEvent(() => updates++);
            var shared = Object.FindObjectOfType<Update.UpdateManager>();
            var foreign = shared.StartCoroutine(CountFrames(() => frames++));
            try
            {
                var first = Kit.Condition(() => false).StartHandleGlobal();
                var timer = Timing.Timer.Delay(1000);
                timer.Pause();
                Kit.Shutdown();
                Assert.That(first.IsActive, Is.False);
                Assert.That(timer.IsActive, Is.True);
                var second = Kit.Condition(() => false).StartHandleGlobal();
                Timing.Timer.Shutdown();
                Assert.That(timer.Status, Is.EqualTo(TimerState.Invalidated));
                Assert.That(second.IsActive, Is.True);
                var beforeUpdates = updates;
                var beforeFrames = frames;
                yield return null;
                yield return null;
                Assert.That(shared != null, Is.True);
                Assert.That(updates, Is.GreaterThan(beforeUpdates));
                Assert.That(frames, Is.GreaterThan(beforeFrames));
                second.Cancel();
            }
            finally
            {
                subscription.Unregister();
                if (shared)
                    shared.StopCoroutine(foreign);
            }
            yield return new ExitPlayMode();
        }

    }

    [Category("YL.Manual.ActionVisual")]
    [Explicit("Run manually in the tool-owned interactive host, with Game View visible; not an automatic CLI gate.")]
    public class ActionVisualTests : ActionTimingSceneFixture
    {
        private static TestRunnerApi _manualApi;
        private static ManualCallbacks _manualCallbacks;
        private static string _manualOutput;

        // This separate entry deliberately has no quit/exit callback. The user owns the window lifetime.
        public static void RunManualAcceptance()
        {
            if (_manualApi)
            {
                Debug.LogWarning("Manual visual acceptance is already running.");
                return;
            }
            try
            {
                if (Application.isBatchMode)
                    throw new InvalidOperationException("Manual visual acceptance requires an interactive owned host.");
                var entry = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YLFramework.Testing.Editor.YLTestEntry"))
                    .First(type => type != null);
                entry.GetMethod("VerifyOwnedProject", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                if (!string.IsNullOrEmpty(SessionState.GetString("YLTesting.Request", string.Empty)))
                    throw new InvalidOperationException("Do not share an active automatic CLI host.");
                if (Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty))
                    throw new InvalidOperationException("Do not save or discard dirty scenes for manual acceptance.");
                var arguments = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(arguments, "-ylActionVisualRun");
                if (index < 0 || index + 1 >= arguments.Length || !Guid.TryParseExact(arguments[index + 1], "N", out var run))
                    throw new ArgumentException("Manual acceptance needs a unique -ylActionVisualRun value.");
                _manualOutput = Path.Combine("Logs", "ActionVisual-" + run.ToString("N"));
                if (new[] { ".xml", "-background.png", "-opaque.png", "-revealed.png" }.Any(suffix => File.Exists(_manualOutput + suffix)))
                    throw new IOException("Refusing to overwrite existing manual acceptance artifacts.");
                if (!File.Exists(OwnedScene))
                    throw new FileNotFoundException("Prepare the tool-owned saved empty scene first.");
                EditorSceneManager.OpenScene(OwnedScene, OpenSceneMode.Single);
                if (!EditorApplication.ExecuteMenuItem("Window/General/Game"))
                    throw new InvalidOperationException("Game View is unavailable.");
                _manualApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                _manualApi.hideFlags = HideFlags.HideAndDontSave;
                _manualCallbacks = new ManualCallbacks();
                _manualApi.RegisterCallbacks(_manualCallbacks);
                EditorApplication.update += PumpManualRunner;
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        _manualApi.Execute(new ExecutionSettings(new Filter
                        {
                            testMode = UnityEditor.TestTools.TestRunner.Api.TestMode.EditMode,
                            assemblyNames = new[] { typeof(ActionVisualTests).Assembly.GetName().Name },
                            testNames = new[] { typeof(ActionVisualTests).FullName + "." + nameof(FormalOverlayReallyCoversTheScreenAndRevealsTheBackground) }
                        }));
                    }
                    catch (Exception error)
                    {
                        Debug.LogException(error);
                        ReleaseManualRunner();
                    }
                };
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                ReleaseManualRunner();
            }
        }

        private static void PumpManualRunner()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static void ReleaseManualRunner()
        {
            EditorApplication.update -= PumpManualRunner;
            if (_manualApi)
            {
                _manualApi.UnregisterCallbacks(_manualCallbacks);
                Object.DestroyImmediate(_manualApi);
            }
            _manualApi = null;
            _manualCallbacks = null;
            _manualOutput = null;
        }

        private sealed class ManualCallbacks : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor tests)
            {
                // UTF creates its own default scene after Execute; prepare the owned saved scene at this callback boundary.
                var entry = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YLFramework.Testing.Editor.YLTestEntry"))
                    .First(type => type != null);
                entry.GetMethod("VerifyOwnedProject", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var active = SceneManager.GetActiveScene();
                var pristineDefault = (bool)entry.GetMethod("IsUntouchedDefaultScene", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { active });
                if (SceneManager.sceneCount != 1 || active.isDirty ||
                    (active.path != OwnedScene && !pristineDefault) || (active.path == OwnedScene && active.rootCount != 0))
                    throw new InvalidOperationException("Manual acceptance only replaces the pristine UTF bootstrap or its owned empty scene.");
                EditorSceneManager.OpenScene(OwnedScene, OpenSceneMode.Single);
                if (SceneManager.GetActiveScene().rootCount != 0)
                    throw new InvalidOperationException("The declared owned scene is not empty.");
                Debug.Log("Manual visual acceptance started; Editor will remain open.");
            }
            public void TestStarted(ITestAdaptor test)
            {
            }
            public void TestFinished(ITestResultAdaptor result)
            {
            }
            public void OnError(string message)
            {
                Debug.LogError(message);
                EditorApplication.delayCall += ReleaseManualRunner;
            }
            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    if (File.Exists(_manualOutput + ".xml"))
                        throw new IOException("Refusing to overwrite a manual result.");
                    TestRunnerApi.SaveResultToFile(result, _manualOutput + ".xml");
                    Debug.Log("Manual visual acceptance " + result.TestStatus + "; result: " + _manualOutput + ".xml. Editor remains open.");
                }
                finally
                {
                    EditorApplication.delayCall += ReleaseManualRunner;
                }
            }
        }

        private static IEnumerator AssertFramePixels(MonoBehaviour renderer, Color expected, string captureName)
        {
            Texture2D image = null;
            var captured = false;
            Exception captureError = null;
            IEnumerator Capture()
            {
                yield return new WaitForEndOfFrame();
                var previous = RenderTexture.active;
                try
                {
                    var viewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView", true);
                    var view = EditorWindow.GetWindow(viewType);
                    var target = viewType.GetField("m_RenderTexture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view) as RenderTexture;
                    if (!target || !target.IsCreated())
                        throw new InvalidOperationException("Game View has no rendered texture.");
                    RenderTexture.active = target;
                    image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    image.Apply();
                    TestContext.WriteLine("Game View capture " + captureName + ": " + target.width + "x" + target.height + ", " + SystemInfo.graphicsDeviceType);
                }
                catch (Exception error)
                {
                    captureError = error;
                }
                finally
                {
                    RenderTexture.active = previous;
                    captured = true;
                }
            }
            var capture = renderer.StartCoroutine(Capture());
            try
            {
                var deadline = Time.realtimeSinceStartup + 3;
                while (!captured && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.That(captured, Is.True, "The interactive Game View did not deliver a rendered frame.");
                if (captureError != null)
                    throw new InvalidOperationException("Game View read-back failed.", captureError);
                Assert.That(image, Is.Not.Null);
                Assert.That(image.width, Is.GreaterThan(4));
                Assert.That(image.height, Is.GreaterThan(4));
                if (_manualOutput != null)
                {
                    var artifact = _manualOutput + "-" + captureName + ".png";
                    if (File.Exists(artifact))
                        throw new IOException("Refusing to overwrite a manual capture.");
                    File.WriteAllBytes(artifact, image.EncodeToPNG());
                }
                foreach (var point in new[] { new Vector2Int(1, 1), new Vector2Int(image.width - 2, 1),
                    new Vector2Int(1, image.height - 2), new Vector2Int(image.width - 2, image.height - 2),
                    new Vector2Int(image.width / 2, image.height / 2) })
                {
                    var pixel = image.GetPixel(point.x, point.y);
                    Assert.That(pixel.r, Is.EqualTo(expected.r).Within(0.08f), "red at " + point);
                    Assert.That(pixel.g, Is.EqualTo(expected.g).Within(0.08f), "green at " + point);
                    Assert.That(pixel.b, Is.EqualTo(expected.b).Within(0.08f), "blue at " + point);
                }
            }
            finally
            {
                if (renderer)
                    renderer.StopCoroutine(capture);
                if (image)
                    Object.Destroy(image);
            }
        }

        [UnityTest]
        [Timeout(20000)]
        public IEnumerator FormalOverlayReallyCoversTheScreenAndRevealsTheBackground()
        {
            Assert.That(Application.isBatchMode, Is.False, "Pixel acceptance requires the owned interactive Special host.");
            yield return new EnterPlayMode(false);
            Application.runInBackground = true;
            Assert.That(EditorApplication.ExecuteMenuItem("Window/General/Game"), Is.True);
            Assert.That(Camera.allCameras, Is.Empty, "Do not replace another camera's rendering responsibility.");
            var camera = new GameObject("ActionTiming.PixelBackground").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.magenta;
            camera.cullingMask = 0;
            var canvas = ScreenTransitionCanvas.Instance;
            Assert.That(canvas.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            var enabled = canvas.colorImage.enabled;
            try
            {
                canvas.colorImage.enabled = false;
                yield return null;
                yield return AssertFramePixels(canvas, Color.magenta, "background");
            }
            finally
            {
                canvas.colorImage.enabled = enabled;
            }
            var fadeIn = Kit.ScreenTransition.FadeIn().Duration(0);
            try
            {
                fadeIn.Execute(0);
                fadeIn.Execute(0);
                Canvas.ForceUpdateCanvases();
                yield return null;
                TestContext.WriteLine("Runtime overlay scale: " + canvas.transform.localScale);
                TestContext.WriteLine("Overlay color: " + canvas.colorImage.color + "; camera background: " + camera.backgroundColor);
                yield return AssertFramePixels(canvas, Color.black, "opaque");
            }
            finally
            {
                fadeIn.Deinit();
            }
            var fadeOut = Kit.ScreenTransition.FadeOut().Duration(0);
            try
            {
                fadeOut.Execute(0);
                fadeOut.Execute(0);
                yield return null;
                yield return AssertFramePixels(canvas, Color.magenta, "revealed");
            }
            finally
            {
                fadeOut.Deinit();
            }
            yield return new ExitPlayMode();
        }
    }

    public class ActionKitLifecycleTests
    {
        private Action<Kit> _previousPatch;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _previousPatch = Kit.OnRegisterPatch;
            Kit.Shutdown();
            Kit.OnRegisterPatch = _ => { };
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Kit.Shutdown();
            }
            finally
            {
                Kit.OnRegisterPatch = _previousPatch;
                if (_root)
                {
                    Object.DestroyImmediate(_root);
                }
            }
        }

        [Test]
        public void InitializeRunsThePatchOncePerGeneration()
        {
            var calls = 0;
            Kit observed = null;
            Kit.OnRegisterPatch = context =>
            {
                calls++;
                observed = context;
                Assert.That(Kit.Interface, Is.SameAs(context));
            };
            Kit.Initialize();
            Kit.Initialize();
            Assert.That(Kit.IsInitialized, Is.True);
            Assert.That(Kit.Interface, Is.SameAs(observed));
            Assert.That(calls, Is.EqualTo(1));
            Kit.Shutdown();
            Kit.Initialize();
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void ShutdownDoesNotResetActionIdsOrTheRegistrationPatch()
        {
            var action = Kit.Callback(null);
            var nextId = Kit.IdGenerator;
            action.Deinit();
            var patch = Kit.OnRegisterPatch;
            Kit.Shutdown();
            Assert.That(Kit.IdGenerator, Is.EqualTo(nextId));
            Assert.That(Kit.OnRegisterPatch, Is.SameAs(patch));
            Assert.DoesNotThrow(() => Kit.Shutdown());
        }

        [Test]
        public void FailedInitializationIsVisibleAndCanBeRetried()
        {
            Kit.OnRegisterPatch = _ => throw new InvalidOperationException("expected");
            Assert.Throws<InvalidOperationException>(() => Kit.Initialize());
            Assert.That(Kit.IsInitialized, Is.False);
            Kit.OnRegisterPatch = _ => { };
            Assert.DoesNotThrow(() => Kit.Initialize());
            Assert.That(Kit.IsInitialized, Is.True);
        }

        [Test]
        public void AStaleContextCannotCloseTheNewGeneration()
        {
            var previous = Kit.Interface;
            previous.Deinit();
            var current = Kit.Interface;
            Assert.That(current, Is.Not.SameAs(previous));
            previous.Deinit();
            Assert.That(Kit.IsInitialized, Is.True);
            Assert.That(Kit.Interface, Is.SameAs(current));
        }

        [Test]
        public void ShutdownCancelsLocalExecutorsWithoutDestroyingTheUsersGameObject()
        {
            _root = new GameObject("ActionKit.Lifecycle.LocalOwnerTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var calls = 0;
            var action = Kit.Condition(() => false);
            var controller = action.Start(executor, () => calls++);
            Kit.Shutdown();
            Assert.That(_root != null, Is.True);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(calls, Is.Zero);
            Assert.That(_root.GetComponent<MonoUpdateActionExecutor>(), Is.Null);
        }

        [Test]
        public void ShutdownFromASynchronousStartCallbackDoesNotMutateAnActiveIterator()
        {
            _root = new GameObject("ActionKit.Lifecycle.ReentrantTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var action = Kit.Callback(Kit.Shutdown);
            IActionController result = null;
            Assert.DoesNotThrow(() => result = action.Start(executor));
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(result.Action, Is.Null);
            Assert.That(Kit.IsInitialized, Is.False);
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        public void ShutdownFromACompositeChildDoesNotRunLaterCallbacks(int composition, bool deferred)
        {
            _root = new GameObject("ActionKit.Lifecycle.CompositeShutdownTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var calls = 0;
            var finished = 0;
            ISequence action = composition == 0 ? ActionKit.Sequence() :
                composition == 1 ? ActionKit.Parallel() : ActionKit.Repeat(2);
            if (deferred)
            {
                action.Delay(0.01f);
            }

            action.Callback(ActionKit.Shutdown).Callback(() => calls++);
            var controller = action.Start(executor, () => finished++);
            for (var tick = 0; tick < 3 && !action.IsDeinitialized; tick++)
            {
                executor.Tick(1, 1);
            }

            Assert.That(calls, Is.Zero);
            Assert.That(finished, Is.Zero);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(ActionKit.IsInitialized, Is.False);
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [Test]
        public void ShutdownFromAConditionPredicateDoesNotRunItsSuccessCallback()
        {
            _root = new GameObject("ActionKit.Lifecycle.ConditionShutdownTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var calls = 0;
            var action = ActionKit.Condition(() =>
            {
                ActionKit.Shutdown();
                return true;
            }, () => calls++);
            var controller = action.Start(executor, () => calls++);
            Assert.DoesNotThrow(() => executor.Tick(1, 1));
            Assert.That(calls, Is.Zero);
            Assert.That(action.IsDeinitialized, Is.True);
            Assert.That(controller.Action, Is.Null);
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }

        [Test]
        public void ShutdownFromCompletionDoesNotRunAnotherScheduledAction()
        {
            _root = new GameObject("ActionKit.Lifecycle.CompletionShutdownTest");
            var executor = _root.AddComponent<MonoUpdateActionExecutor>();
            var ready = false;
            var first = Kit.Condition(() => ready);
            var secondCalls = 0;
            var second = Kit.Custom(api => api.OnExecute(_ => secondCalls++));
            var firstController = first.Start(executor, Kit.Shutdown);
            var secondController = second.Start(executor);
            ready = true;
            Assert.DoesNotThrow(() => executor.Tick(1, 1));
            Assert.That(secondCalls, Is.Zero);
            Assert.That(firstController.Action, Is.Null);
            Assert.That(secondController.Action, Is.Null);
        }

        [Test]
        public void ShutdownDrainsPendingRecyclesOnlyOnce()
        {
            var calls = 0;
            var pool = new ActionPool<object>(() => new object(), _ => calls++);
            var item = pool.Allocate();
            ActionQueue.AddCallback(new ActionQueueRecycleCallback<object>(pool, item));
            Assert.That(calls, Is.Zero);
            Assert.That(ActionQueue.PendingCount, Is.EqualTo(1));
            Kit.Shutdown();
            Kit.Shutdown();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(pool.Allocate(), Is.SameAs(item));
            Assert.That(ActionQueue.PendingCount, Is.Zero);
        }
    }
}

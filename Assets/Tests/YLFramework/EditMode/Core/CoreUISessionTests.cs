using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace YLFramework.Core.Tests
{
    [Category("YL.Special.CoreSession")]
    public class CoreUISessionTests
    {
        private SceneSetup[] _previousScenes;
        private Action _restoreInputFocus;

        [UnityTearDown]
        public IEnumerator RestoreOwnedSession()
        {
            RestoreInputFocus();
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPaused = false;
                yield return new ExitPlayMode();
            }
            EditorApplication.isPaused = false;
            if (_previousScenes != null)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_previousScenes);
                _previousScenes = null;
            }
        }

        [UnityTest]
        public IEnumerator OriginalUIInputAndPendingOwnershipSurviveTwoActualPlaysWithoutDomainReload()
        {
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled &&
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0, Is.True,
                "The explicit owned session profile must prepare Domain Reload settings before discovery.");
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount).All(index => !SceneManager.GetSceneAt(index).isDirty), Is.True,
                "Do not discard or automatically save a dirty scene to run acceptance.");
            _previousScenes = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.OpenScene("Assets/Tests/YLFramework/Acceptance/Core/UIAcceptance.unity", OpenSceneMode.Single);
            var entryType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YLFramework.Core.Tests.UIAcceptanceEntry"))
                .Single(type => type != null);
            var roundField = entryType.GetField("Round");
            var resultField = entryType.GetField("Result");
            roundField.SetValue(null, 0);
            resultField.SetValue(null, null);
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(false);
                ConfigureOwnedInputFocus();
                var deadline = EditorApplication.timeSinceStartup + 30;
                while ((string)resultField.GetValue(null) != "READY round=" + round && EditorApplication.timeSinceStartup < deadline)
                {
                    var result = (string)resultField.GetValue(null);
                    Assert.That(result == null || !result.StartsWith("FAILED", StringComparison.Ordinal), Is.True, result);
                    yield return null;
                }
                Assert.That(resultField.GetValue(null), Is.EqualTo("READY round=" + round));
                Assert.That(roundField.GetValue(null), Is.EqualTo(round), "No intervening compile or Domain Reload may reset the original probe.");
                RestoreInputFocus();
                var entry = UnityEngine.Object.FindObjectOfType(entryType);
                Assert.That(entry, Is.Not.Null);
                EditorApplication.isPaused = true;
                entryType.GetMethod("PrepareExit").Invoke(entry, null);
                yield return new ExitPlayMode();
                EditorApplication.isPaused = false;
                Assert.That(entryType.GetMethod("VerifyExit").Invoke(null, null), Is.EqualTo("EXIT_PASS round=" + round));
            }
        }

        private void ConfigureOwnedInputFocus()
        {
            var input = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var property = input.GetProperty("settings");
            var backgroundProperty = input.GetProperty("runInBackground");
            var previous = (UnityEngine.Object)property.GetValue(null);
            var flags = previous.hideFlags;
            var background = backgroundProperty.GetValue(null);
            var settings = UnityEngine.Object.Instantiate(previous);
            _restoreInputFocus = () =>
            {
                property.SetValue(null, previous);
                previous.hideFlags = flags;
                backgroundProperty.SetValue(null, background);
                if (settings)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            };
            // 与既有 Core 原生输入回归相同，只配置自建设备的临时设置，不改工程输入资产。
            var focus = settings.GetType().GetProperty("editorInputBehaviorInPlayMode");
            var backgroundMode = settings.GetType().GetProperty("backgroundBehavior");
            focus.SetValue(settings, Enum.Parse(focus.PropertyType, "AllDeviceInputAlwaysGoesToGameView"));
            backgroundMode.SetValue(settings, Enum.Parse(backgroundMode.PropertyType, "IgnoreFocus"));
            previous.hideFlags = flags | HideFlags.HideInInspector;
            property.SetValue(null, settings);
            backgroundProperty.SetValue(null, true);
        }

        private void RestoreInputFocus()
        {
            var restore = _restoreInputFocus;
            _restoreInputFocus = null;
            restore?.Invoke();
        }
    }
}

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Audio;

namespace YLFramework.Tests.AudioSession
{
    // 单次真实退出，不改 Domain Reload 设置或原包日志路由；证据跨域保存在 Editor SessionState。
    public class AudioExitTests
    {
        private const string Key = "YL.Audio.4.1.Exit.";
        [Serializable]
        private class Scenes
        {
            public SceneRecord[] Items;
        }
        [Serializable]
        private class SceneRecord
        {
            public string Path;
            public bool Loaded;
            public bool Active;
        }
        private static AudioClip _clip;
        private static AudioHandle _handle;

        private sealed class Resource : IDisposable
        {
            public void Dispose()
            {
                SessionState.SetInt(Key + "releases", SessionState.GetInt(Key + "releases", 0) + 1);
                try
                {
                    AudioKit.PlaySound(_clip);
                }
                catch (InvalidOperationException)
                {
                    SessionState.SetBool(Key + "releaseRejected", true);
                }
            }
        }

        [SetUp]
        public void SetUp()
        {
            if (SessionState.GetBool(Key + "configured", false))
            {
                return;
            }

            Assert.IsFalse(EditorApplication.isPlayingOrWillChangePlaymode);
            Assert.IsFalse(EditorSettings.enterPlayModeOptionsEnabled, "本窄退出测试使用当前正常域重载设置，不接管双 Play 门禁。");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var opened = SceneManager.GetSceneAt(i);
                // 同批先前测试会标脏 Runner 的无路径空场景；不保存或覆盖任何用户场景。
                if (!string.IsNullOrEmpty(opened.path))
                {
                    Assert.IsFalse(opened.isDirty, opened.path);
                }
            }
            Assert.IsFalse(AudioManager.HasInstance, "不接管其他音频 owner。");
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var snapshot = new Scenes
            {
                Items = setup.Where(s => !string.IsNullOrEmpty(s.path)).Select(s =>
                new SceneRecord { Path = s.path, Loaded = s.isLoaded, Active = s.isActive }).ToArray()
            };
            SessionState.SetString(Key + "scenes", JsonUtility.ToJson(snapshot));
            var path = "Temp/YLAudio.Exit." + Guid.NewGuid().ToString("N") + ".unity";
            SessionState.SetString(Key + "path", path);
            SessionState.SetBool(Key + "configured", true);
            SessionState.SetInt(Key + "releases", 0);
            SessionState.SetBool(Key + "releaseRejected", false);
            SessionState.SetBool(Key + "exitRejected", false);
            SessionState.SetBool(Key + "quitClean", false);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!SessionState.GetBool(Key + "configured", false))
            {
                yield break;
            }

            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            EditorApplication.playModeStateChanged -= OnExit;
            Application.quitting -= OnQuit;
            var snapshot = JsonUtility.FromJson<Scenes>(SessionState.GetString(Key + "scenes", ""));
            if (snapshot.Items.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(snapshot.Items.Select(s =>
                    new SceneSetup { path = s.Path, isLoaded = s.Loaded, isActive = s.Active }).ToArray());
            }
            else
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }

            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "path", "")));
            SessionState.SetBool(Key + "configured", false);
            // 实际退出的断言已经结束；恢复本 fixture 的 Audio 静态状态，避免污染后续 EditMode 测试。
            // 此 reset 仅用于测试清理，不作为退出或资源释放的验证证据。
            typeof(AudioKit).GetMethod("ResetSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }

        private static void OnExit(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            var hosts = UnityEngine.Object.FindObjectsOfType<AudioManager>(true).Length;
            var rejected = false;
            try
            {
                AudioKit.PlaySound(_clip);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            SessionState.SetBool(Key + "exitRejected", rejected && !_handle.IsValid &&
                hosts == UnityEngine.Object.FindObjectsOfType<AudioManager>(true).Length);
            EditorApplication.playModeStateChanged -= OnExit;
        }

        private static void OnQuit()
        {
            SessionState.SetBool(Key + "quitClean", !_handle.IsValid && AudioKit.SoundCount == 0 &&
                !AudioManager.HasInstance && AudioKit.CurrentSettings == null && SessionState.GetInt(Key + "releases", 0) == 1);
            Application.quitting -= OnQuit;
            if (_clip)
            {
                UnityEngine.Object.DestroyImmediate(_clip);
            }

            _clip = null;
        }

        [UnityTest]
        public IEnumerator RealExitRejectsPlaybackAndReleasesLiveRequestBeforeDomainUnload()
        {
            yield return new EnterPlayMode();
            var listener = new GameObject("AudioExit.Listener");
            listener.AddComponent<AudioListener>();
            _clip = AudioClip.Create("audio-exit", 44100, 1, 44100, false);
            var options = AudioPlaybackOptions.Defaults;
            options.Loop = true;
            // 不改持久设置；此处只在本测试的运行时快照启用 Sound。
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            _handle = AudioKit.PlaySoundWithSnapshot(_clip, options, resource: new AudioResourceLease(new Resource()));
            Assert.IsTrue(_handle.IsValid);
            Application.quitting += OnQuit;
            EditorApplication.playModeStateChanged += OnExit;
            yield return new ExitPlayMode();
            Assert.IsTrue(SessionState.GetBool(Key + "exitRejected", false), "实际 ExitingPlayMode 不得重建宿主。");
            Assert.IsTrue(SessionState.GetBool(Key + "releaseRejected", false), "退出释放中的重入请求必须拒绝。");
            Assert.IsTrue(SessionState.GetBool(Key + "quitClean", false), "Application.quitting 返回前应结束自有请求与设置。");
            Assert.AreEqual(1, SessionState.GetInt(Key + "releases", 0));
            Assert.IsFalse(AudioManager.HasInstance);
        }
    }
}

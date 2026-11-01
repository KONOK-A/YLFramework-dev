using System;
using System.Collections;
using System.IO;
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
    [Category("YL.Special.AudioSession")]
    public class AudioContinuousPlayTests
    {
        private static readonly object _domainIdentity = new object();
        private SceneSetup[] _scenes;
        private bool _isConfigured;
        private bool _wereOptionsEnabled;
        private EnterPlayModeOptions _options;
        private string _path;
        private AudioClip _clip;
        private AudioHandle _handle;
        private AudioChannel _channel;
        private AudioPlayRequest _pending;
        private Provider _provider;
        private AudioKitSettings _settings;
        private Action _unregister;
        private int _notifications;
        private int _quitCount;
        private bool _didQuitCleanly;
        private bool _wasExitRejected;
        private ILogHandler _originalHandler;
        private ILogHandler _nativeHandler;

        private sealed class Owner : IDisposable
        {
            internal int Releases;
            internal Action OnRelease;
            public void Dispose()
            {
                Releases++;
                OnRelease?.Invoke();
            }
        }
        private sealed class Provider : IAudioClipProvider
        {
            internal Action<AudioClipLease, Exception> Complete;
            internal readonly Owner ActiveOwner = new Owner();
            public bool CanCache => false;
            public AudioClipLease Load(string key) => new AudioClipLease(CurrentClip, ActiveOwner);
            internal AudioClip CurrentClip;
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                Complete = completed;
                progress(.25f);
            }
        }

        [SetUp]
        public void SetUp()
        {
            Assert.IsFalse(EditorApplication.isPlayingOrWillChangePlaymode);
            Assert.IsFalse(AudioManager.HasInstance, "不接管其他 Audio owner。");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                Assert.IsFalse(SceneManager.GetSceneAt(i).isDirty, "不得覆盖未保存场景。");
            }

            _scenes = EditorSceneManager.GetSceneManagerSetup();
            _wereOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _options = EditorSettings.enterPlayModeOptions;
            _originalHandler = Debug.unityLogger.logHandler;
            _nativeHandler = (ILogHandler)Activator.CreateInstance(typeof(Debug).Assembly.GetType("UnityEngine.DebugLogHandler"), true);
            _isConfigured = true;
            _path = "Temp/YLAudio.Continuous." + Guid.NewGuid().ToString("N") + ".unity";
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, _path));
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorApplication.playModeStateChanged += OnState;
        }

        private void OnState(PlayModeStateChange state)
        {
            // 连续会话使用测试捕获的原生日志输出，不依赖外部包装器的退出生命周期。
            Debug.unityLogger.logHandler = _nativeHandler;
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
            _wasExitRejected = rejected && hosts == UnityEngine.Object.FindObjectsOfType<AudioManager>(true).Length;
        }
        private void OnQuit()
        {
            _quitCount++;
            _didQuitCleanly = !_handle.IsValid && !_channel.IsValid && _pending.IsDone &&
                _pending.EndReason == AudioPlayEnd.Shutdown && _provider.ActiveOwner.Releases == 1 &&
                !AudioManager.HasInstance && AudioKit.CurrentSettings == null && AudioKit.SoundCount == 0;
            if (_clip)
            {
                UnityEngine.Object.DestroyImmediate(_clip);
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isConfigured)
            {
                yield break;
            }

            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            Application.quitting -= OnQuit;
            EditorApplication.playModeStateChanged -= OnState;
            AudioKit.Shutdown();
            _unregister?.Invoke();
            if (_clip)
            {
                UnityEngine.Object.DestroyImmediate(_clip);
            }

            EditorSettings.enterPlayModeOptions = _options;
            EditorSettings.enterPlayModeOptionsEnabled = _wereOptionsEnabled;
            EditorSceneManager.RestoreSceneManagerSetup(_scenes);
            File.Delete(_path);
            Debug.unityLogger.logHandler = _originalHandler;
            // 两轮真实退出的断言已结束；仅恢复 fixture 静态环境，不作为 Play 或退出证据。
            typeof(AudioKit).GetMethod("ResetSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }

        [UnityTest]
        public IEnumerator ConsecutiveRealPlayRejectsExitReentryAndIsolatesOldIdentities()
        {
            var domain = _domainIdentity;
            yield return new EnterPlayMode();
            Assert.AreSame(domain, _domainIdentity, "必须在同一脚本域内。");
            Assert.AreEqual(EnterPlayModeOptions.DisableDomainReload, EditorSettings.enterPlayModeOptions);
            new GameObject("AudioContinuous.Listener").AddComponent<AudioListener>();
            _clip = AudioClip.Create("continuous-play", 44100, 1, 44100, false);
            _settings = AudioKit.Settings;
            _settings.IsSoundOn.SetValueWithoutCallback(true);
            _settings.SoundVolume.SetValueWithoutCallback(1f);
            var token = _settings.IsSoundMuted.Register(_ => _notifications++);
            _unregister = token.Unregister;
            _provider = new Provider { CurrentClip = _clip };
            _channel = AudioKit.RegisterChannel("continuous-play", AudioCategory.Sound, AudioChannelMode.Overlap);
            AudioKit.PrewarmSounds(3);
            _handle = _channel.PlayNamed(new AudioClipAddress(_provider, "active"), AudioSoundOptions.MusicDefaults);
            _pending = _channel.PlayAsync(new AudioClipAddress(_provider, "pending"));
            Assert.IsTrue(_handle.IsValid);
            Assert.IsFalse(_pending.IsDone);
            var releaseRejected = false;
            _provider.ActiveOwner.OnRelease = () =>
            {
                try
                {
                    AudioKit.PlaySound(_clip);
                }
                catch (InvalidOperationException)
                {
                    releaseRejected = true;
                }
            };
            Application.quitting += OnQuit;
            yield return null;
            yield return new ExitPlayMode();
            Assert.AreEqual(1, _quitCount);
            Assert.IsTrue(_didQuitCleanly, "Application.quitting 返回前应同步终结请求/租约/设置/宿主。");
            Assert.IsTrue(_wasExitRejected);
            Assert.IsTrue(releaseRejected);
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsOfType<AudioManager>(true).Length);
            var oldHandle = _handle;
            var oldChannel = _channel;
            var oldPending = _pending;
            var oldSettings = _settings;
            var oldProvider = _provider;
            Application.quitting -= OnQuit;

            yield return new EnterPlayMode();
            Assert.AreSame(domain, _domainIdentity);
            new GameObject("AudioContinuous.Listener").AddComponent<AudioListener>();
            _clip = AudioClip.Create("continuous-play-second", 44100, 1, 44100, false);
            _settings = AudioKit.Settings;
            Assert.AreNotSame(oldSettings, _settings);
            _settings.IsSoundOn.SetValueWithoutCallback(true);
            _settings.SoundVolume.SetValueWithoutCallback(1f);
            _provider = new Provider { CurrentClip = _clip };
            _channel = AudioKit.RegisterChannel("continuous-play", AudioCategory.Sound, AudioChannelMode.Overlap);
            _handle = _channel.PlayNamed(new AudioClipAddress(_provider, "active"), AudioSoundOptions.MusicDefaults);
            _pending = _channel.PlayAsync(new AudioClipAddress(_provider, "pending"));
            var source = _handle.Voice.Source;
            Assert.IsTrue(_handle.IsPlaying);
            Assert.IsFalse(oldHandle.IsValid);
            Assert.IsFalse(oldChannel.IsValid);
            oldHandle.Stop();
            oldChannel.Stop();
            oldChannel.Pause();
            oldChannel.Dispose();
            oldPending.Cancel();
            oldSettings.IsSoundMuted.Value = true;
            _unregister();
            Assert.AreEqual(0, _notifications, "旧 Settings 订阅必须退出，旧 token 不能伤及新会话。");
            Assert.IsFalse(source.mute);
            var lateOwner = new Owner();
            oldProvider.Complete(new AudioClipLease(_clip, lateOwner), null);
            yield return null;
            Assert.AreEqual(1, lateOwner.Releases, "旧 producer 迟到结果成对归还。");
            Assert.IsTrue(_handle.IsPlaying);
            Assert.AreEqual(1, _channel.RequestCount);
            Assert.AreEqual(1, UnityEngine.Object.FindObjectsOfType<AudioManager>(true).Length);
            _settings.IsSoundMuted.Value = true;
            Assert.IsTrue(source.mute);
            _settings.IsSoundMuted.Value = false;
            Assert.IsFalse(source.mute);
            Application.quitting += OnQuit;
            _didQuitCleanly = false;
            _wasExitRejected = false;
            yield return new ExitPlayMode();
            Assert.AreEqual(2, _quitCount);
            Assert.IsTrue(_didQuitCleanly);
            Assert.IsTrue(_wasExitRejected);
            Assert.AreEqual(0, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsOfType<AudioManager>(true).Length);
        }
    }
}

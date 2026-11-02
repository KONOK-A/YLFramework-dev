using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioDefaultsPlaybackTests
    {
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private AudioClip _clip;
        private GameObject _listener;
        private bool _ownsActionSession;

        [SetUp]
        public void SetUp()
        {
            AudioKit.Shutdown();
            _ownsActionSession = false;
            for (var i = 0; i < _keys.Length; i++)
            {
                _hadKeys[i] = PlayerPrefs.HasKey(_keys[i]);
            }

            for (var i = 0; i < 3; i++)
            {
                _switches[i] = PlayerPrefs.GetInt(_keys[i]);
                _volumes[i] = PlayerPrefs.GetFloat(_keys[i + 3]);
            }
            foreach (var key in _keys)
            {
                PlayerPrefs.DeleteKey(key);
            }

            _clip = AudioClip.Create("defaults-playback", 441000, 1, 44100, false);
            var hasListener = false;
            foreach (var listener in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                hasListener |= listener.isActiveAndEnabled;
            }

            if (!hasListener)
            {
                _listener = new GameObject("AudioDefaults.Listener");
                _listener.AddComponent<AudioListener>();
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            if (_ownsActionSession)
            {
                ActionKit.Shutdown();
            }

            for (var i = 0; i < 3; i++)
            {
                if (_hadKeys[i])
                {
                    PlayerPrefs.SetInt(_keys[i], _switches[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(_keys[i]);
                }

                if (_hadKeys[i + 3])
                {
                    PlayerPrefs.SetFloat(_keys[i + 3], _volumes[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(_keys[i + 3]);
                }
            }
            Object.Destroy(_clip);
            if (_listener)
            {
                Object.Destroy(_listener);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator MinimalPlaybackAndPartialConfigurationWorkWithoutMixerOrInitialization()
        {
            Assert.IsFalse(AudioManager.HasInstance);
            var music = AudioKit.PlayMusic(_clip);
            var voice = AudioKit.PlayVoice(_clip);
            var sound = AudioKit.PlaySound(_clip);
            yield return null;
            foreach (var handle in new[] { music, voice, sound })
            {
                Assert.IsTrue(handle.IsValid);
                Assert.IsTrue(handle.IsPlaying);
                Assert.AreEqual(1f, handle.Voice.Source.volume);
                Assert.AreEqual(1f, handle.Voice.Source.pitch);
                Assert.AreEqual(0f, handle.Voice.Source.spatialBlend);
                Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
            }
            Assert.IsTrue(music.Voice.Source.loop);
            Assert.IsFalse(voice.Voice.Source.loop);
            Assert.IsFalse(sound.Voice.Source.loop);
            var options = new AudioSoundOptions { Volume = .5f };
            var first = AudioKit.PlaySound(_clip, options);
            var second = AudioKit.PlaySound(_clip, options);
            options.Volume = 0f;
            options.Pitch = 0f;
            first.SetVolumeScale(.25f);
            first.SetPitch(2f);
            yield return null;
            Assert.AreEqual(.25f, first.Voice.Source.volume);
            Assert.AreEqual(2f, first.Voice.Source.pitch);
            Assert.AreEqual(.5f, second.Voice.Source.volume);
            Assert.AreEqual(1f, second.Voice.Source.pitch);
            Assert.AreEqual(0f, options.Volume);
            Assert.AreEqual(0f, options.Pitch);
            foreach (var key in _keys)
            {
                Assert.IsFalse(PlayerPrefs.HasKey(key), "Playback must not create preferences.");
            }
        }

        [UnityTest]
        public IEnumerator ActionKeepsAllocationSnapshotAcrossFramesAndReset()
        {
            Assert.IsFalse(ActionKit.IsInitialized, "Do not release another owner's Action session.");
            Assert.IsEmpty(Object.FindObjectsByType<ActionRecycleDriver>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            Assert.IsEmpty(Object.FindObjectsByType<MonoUpdateActionExecutor>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            _ownsActionSession = true;
            var options = new AudioSoundOptions { Volume = .4f };
            var action = PlaySoundAction.AllocateWithOptions(_clip, options);
            try
            {
                options.Pitch = 0f;
                options.Volume = 0f;
                Assert.IsFalse(action.Execute(0f));
                var first = AudioKit.GetPlayingSounds()[0];
                first.SetVolumeScale(.2f);
                yield return null;
                action.Reset();
                Assert.IsFalse(first.IsValid);
                Assert.IsFalse(action.Execute(0f));
                var second = AudioKit.GetPlayingSounds()[0];
                yield return null;
                Assert.IsTrue(second.IsPlaying);
                Assert.AreEqual(.4f, second.Voice.Source.volume);
                Assert.AreEqual(1f, second.Voice.Source.pitch);
                Assert.AreEqual(0f, options.Volume);
            }
            finally
            {
                action.Deinit();
            }
        }
    }
}

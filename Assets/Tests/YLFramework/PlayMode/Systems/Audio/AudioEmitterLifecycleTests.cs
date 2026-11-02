using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using AudioKit = YLFramework.Audio.AudioKit;
using YLFramework.Audio;

namespace YLFramework.Tests.Audio
{
    public class AudioEmitterLifecycleTests
    {
        [TestCase(AudioEmitter.PositionMode.TwoDimensional)]
        [TestCase(AudioEmitter.PositionMode.FixedPosition)]
        public void OneShotsOverlapAndSurviveDisable(AudioEmitter.PositionMode mode)
        {
            var soundOn = AudioKit.Settings.IsSoundOn.Value;
            var owner = new GameObject("Emitter one-shot test");
            var clip = AudioClip.Create("Emitter test", 441000, 1, 44100, false);
            var emitter = owner.AddComponent<AudioEmitter>();
            SetField(emitter, "clips", new List<AudioClip> { null, clip });
            SetField(emitter, "positionMode", mode);
            AudioKit.Settings.IsSoundOn.Value = true;
            try
            {
                var before = AudioKit.SoundCount;
                emitter.Play();
                emitter.Play();
                Assert.AreEqual(before + 2, AudioKit.SoundCount);
                owner.SetActive(false);
                Assert.AreEqual(before + 2, AudioKit.SoundCount);
                emitter.Stop();
                Assert.AreEqual(before, AudioKit.SoundCount);
                Assert.IsNull(owner.GetComponent<AudioSource>());
            }
            finally
            {
                emitter.Stop();
                AudioKit.Settings.IsSoundOn.Value = soundOn;
                Object.Destroy(owner);
                Object.Destroy(clip);
            }
        }

        [TestCase(AudioEmitter.PositionMode.TwoDimensional)]
        [TestCase(AudioEmitter.PositionMode.FixedPosition)]
        [TestCase(AudioEmitter.PositionMode.FollowTransform)]
        public void LoopReplayReplacesOwnedPlaybackAndDisableStopsIt(AudioEmitter.PositionMode mode)
        {
            var soundOn = AudioKit.Settings.IsSoundOn.Value;
            var owner = new GameObject("Emitter loop test");
            var clip = AudioClip.Create("Emitter loop", 441000, 1, 44100, false);
            var emitter = owner.AddComponent<AudioEmitter>();
            SetField(emitter, "clips", new List<AudioClip> { clip });
            SetField(emitter, "positionMode", mode);
            SetField(emitter, "loop", true);
            AudioKit.Settings.IsSoundOn.Value = true;
            try
            {
                var before = AudioKit.SoundCount;
                emitter.Play();
                var first = AudioKit.GetPlayingSounds();
                emitter.Play();
                Assert.AreEqual(before + 1, AudioKit.SoundCount);
                var stopped = 0;
                foreach (var handle in first)
                {
                    if (!handle.IsValid)
                    {
                        stopped++;
                    }
                }
                Assert.AreEqual(1, stopped, "循环重播只能停止此组件的原句柄");
                owner.SetActive(false);
                Assert.AreEqual(before, AudioKit.SoundCount);
            }
            finally
            {
                emitter.Stop();
                AudioKit.Settings.IsSoundOn.Value = soundOn;
                Object.Destroy(owner);
                Object.Destroy(clip);
            }
        }

        [Test]
        public void DisableStopsAllFollowingOneShots()
        {
            var soundOn = AudioKit.Settings.IsSoundOn.Value;
            var owner = new GameObject("Emitter follow test");
            var clip = AudioClip.Create("Emitter follow", 441000, 1, 44100, false);
            var emitter = owner.AddComponent<AudioEmitter>();
            SetField(emitter, "clips", new List<AudioClip> { clip });
            SetField(emitter, "positionMode", AudioEmitter.PositionMode.FollowTransform);
            AudioKit.Settings.IsSoundOn.Value = true;
            try
            {
                var before = AudioKit.SoundCount;
                emitter.Play();
                emitter.Play();
                Assert.AreEqual(before + 2, AudioKit.SoundCount);
                owner.SetActive(false);
                Assert.AreEqual(before, AudioKit.SoundCount);
            }
            finally
            {
                emitter.Stop();
                AudioKit.Settings.IsSoundOn.Value = soundOn;
                Object.Destroy(owner);
                Object.Destroy(clip);
            }
        }

        [Test]
        public void DisablingMusicComponentDoesNotStopReplacementMusic()
        {
            var musicOn = AudioKit.Settings.IsMusicOn.Value;
            var owner = new GameObject("Music component test");
            var clip = AudioClip.Create("Music component", 441000, 1, 44100, false);
            var player = owner.AddComponent<AudioMusicPlayer>();
            SetField(player, "clip", clip);
            SetField(player, "playOnAwake", false);
            AudioKit.Settings.IsMusicOn.Value = true;
            AudioHandle replacement = default;
            try
            {
                player.Play();
                var ownedField = typeof(AudioMusicPlayer).GetField("_owned", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(ownedField, "MusicPlayer should track its own leases, including crossfade tails.");
                var ownedHandles = (List<AudioHandle>)ownedField.GetValue(player);
                Assert.AreEqual(1, ownedHandles.Count);
                var owned = ownedHandles[0];
                Assert.AreEqual(owned, AudioKit.Music);
                Assert.IsTrue(owned.IsValid);
                replacement = AudioKit.PlayMusic(clip);
                Assert.IsFalse(owned.IsValid);
                Assert.IsTrue(replacement.IsValid);
                owner.SetActive(false);
                player.Stop();
                Assert.IsTrue(replacement.IsValid, "旧组件不可停止已经替换的全局音乐");
                Assert.IsNull(owner.GetComponent<AudioSource>());
            }
            finally
            {
                player.Stop();
                replacement.Stop();
                AudioKit.Settings.IsMusicOn.Value = musicOn;
                Object.Destroy(owner);
                Object.Destroy(clip);
            }
        }

        private static void SetField(Object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, name);
            field.SetValue(target, value);
        }
    }
}

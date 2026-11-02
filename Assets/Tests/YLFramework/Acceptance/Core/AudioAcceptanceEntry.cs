using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Core.Tests
{
    /// <summary>4.5 Editor/Mono Player 共用的窄音频验收入口，不参与框架分发。</summary>
    public sealed class AudioAcceptanceEntry : MonoBehaviour
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Group")]
        public AudioGroupPlayer group;
        [global::UnityEngine.Serialization.FormerlySerializedAs("Signal")]
        public AudioClip signal;
        [global::UnityEngine.Serialization.FormerlySerializedAs("SecondSignal")]
        public AudioClip secondSignal;
        [global::UnityEngine.Serialization.FormerlySerializedAs("MixerConfig")]
        public AudioMixerConfig mixerConfig;
        [global::UnityEngine.Serialization.FormerlySerializedAs("ResourceKey")]
        public string resourceKey;
        public static string Result;
        public static string Stage;
        private AudioHandle _exitHandle;
        private AudioPlayRequest _exitPending;
        private Provider _exitProvider;
        private bool _ownsAudioSession;
        private readonly float[] _samples = new float[2048];

        private sealed class Owner : IDisposable
        {
            internal int Releases;
            public void Dispose()
            {
                Releases++;
            }
        }
        private sealed class Provider : IAudioClipProvider
        {
            internal AudioClip Clip;
            internal Action<AudioClipLease, Exception> Complete;
            internal readonly Owner Owner = new Owner();
            public bool CanCache => false;
            public AudioClipLease Load(string key) => new AudioClipLease(Clip, Owner);
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                Complete = completed;
                progress(.2f);
            }
        }

        private IEnumerator Start()
        {
            Result = "RUNNING";
            DontDestroyOnLoad(gameObject);
            var stack = new Stack<IEnumerator>();
            stack.Push(RunAudio());
            try
            {
                while (stack.Count > 0)
                {
                    object current = null;
                    bool next = false;
                    Exception failure = null;
                    try
                    {
                        next = stack.Peek().MoveNext();
                        if (next)
                        {
                            current = stack.Peek().Current;
                        }
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    if (failure != null)
                    {
                        Result = "FAILED: " + failure;
                        Debug.LogError("AUDIO45 " + Result);
                        if (!Application.isEditor)
                        {
                            Application.Quit(1);
                        }

                        yield break;
                    }
                    if (!next)
                    {
                        (stack.Pop() as IDisposable)?.Dispose();
                        continue;
                    }
                    if (current is IEnumerator nested)
                    {
                        stack.Push(nested);
                    }
                    else
                    {
                        yield return current;
                    }
                }
                Result = "AUDIO_PASS";
                Debug.Log("AUDIO45 " + Result);
                if (!Application.isEditor)
                {
                    Application.Quit(0);
                }
            }
            finally
            {
                while (stack.Count > 0)
                {
                    (stack.Pop() as IDisposable)?.Dispose();
                }

                if (_ownsAudioSession && Result.StartsWith("FAILED", StringComparison.Ordinal))
                {
                    Application.quitting -= VerifyQuit;
                    if (group)
                    {
                        group.Stop();
                    }

                    AudioKit.Shutdown();
                    Debug.Log("AUDIO45 FAILURE_CLEANUP");
                }
            }
        }

        private IEnumerator RunAudio()
        {
            var keys = new[] { "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON", "KEY_AUDIO_MANAGER_SOUND_ON",
                "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME", "KEY_AUDIO_MANAGER_SOUND_VOLUME" };
            var existed = new bool[6];
            var values = new float[6];
            for (var i = 0; i < 6; i++)
            {
                existed[i] = PlayerPrefs.HasKey(keys[i]);
                values[i] = i < 3 ? PlayerPrefs.GetInt(keys[i]) : PlayerPrefs.GetFloat(keys[i]);
            }
            var scale = Time.timeScale;
            ResourceSystem resources = null;
            var completed = false;
            try
            {
                Check(signal && secondSignal && group && mixerConfig, "formal serialized fixture dependencies");
                Check(!AudioKit.Music.IsValid && !AudioKit.Voice.IsValid && AudioKit.SoundCount == 0 &&
                    AudioKit.SoundStats.OwnedSourceCount == 0, "fresh isolated audio fixture");
                Check(ResourceSystem.Current == null, "fresh isolated Resource fixture");
                // 历史验收夹具的 Resources 物理路径显式登记到独立 Catalog，不推断任何旧 Bundle 身份。
                var locator = resourceKey;
                if (locator.StartsWith("resources://", StringComparison.OrdinalIgnoreCase))
                {
                    locator = locator.Substring("resources://".Length);
                }
                else if (locator.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
                {
                    locator = locator.Substring("resources/".Length);
                }

                using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
                {
                    new ResourceCatalog("yl-audio-acceptance", new[] { new ResourceCatalogEntry("signal",
                        new ResourceAddress("yl-audio-acceptance", "signal"),
                        new YLFramework.Resource.ResourceLocation(ResourceSourceId.Resources, locator, typeof(AudioClip))) })
                })))
                {
                    resources = initialized.TakeResult();
                }

                _ownsAudioSession = true;
                AudioKit.Settings.IsOn = true;
                AudioKit.Settings.MusicVolume.Value = 1;
                AudioKit.Settings.VoiceVolume.Value = 1;
                AudioKit.Settings.SoundVolume.Value = 1;
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
                AudioKit.PrewarmSounds(3);
                group.Play();
                yield return Wait(() => group.LastHandle.IsPlaying, "formal GroupPlayer async Resources submission");
                Check(group.LastRequest.IsAssetReady && group.LastRequest.IsDataReady, "named asset/data readiness");
                double audible = 0;
                yield return Measure("group-signal", value => audible = value);
                Check(audible > .001, "nonzero real listener output for recognizable PCM");
                var playing = group.LastHandle;
                var source = FindSource();
                Check(source && source.loop, "real Source loop");
                group.Channel.Volume = .5f;
                double half = 0;
                yield return Measure("half-volume", value => half = value);
                Check(half > audible * .35 && half < audible * .65, "channel volume changes actual output");
                group.Channel.Volume = 1;
                group.Channel.IsMuted = true;
                var time = playing.Time;
                double silence = 1;
                yield return Measure("muted", value => silence = value);
                Check(playing.Time > time && silence < .00001, "mute silences output without pausing");
                group.Channel.IsMuted = false;
                playing.Pause();
                time = playing.Time;
                yield return Measure("paused", value => silence = value);
                Check(playing.IsPaused && Mathf.Abs(playing.Time - time) < .02f && silence < .00001, "pause freezes time and output");
                playing.Resume();
                yield return Measure("resumed", value => Check(value > .001, "resume restores output"));
                playing.FadeOut(.25f, false);
                yield return new WaitForSecondsRealtime(.35f);
                yield return Measure("faded-out", value => Check(value < .00001 && playing.IsValid, "fade silence keeps original lease"));
                playing.FadeIn(.25f);
                yield return new WaitForSecondsRealtime(.35f);
                yield return Measure("faded-in", value => Check(value > .001, "fade-in restores output"));
                group.Stop();
                Check(!playing.IsValid, "component Stop terminates original handle");
                yield return Measure("stopped", value => Check(value < .00001, "Stop silences output"));

                var address = new AudioClipAddress(resources.GetAudioClipProvider("yl-audio-acceptance"), "signal");
                var loop = AudioSoundOptions.MusicDefaults;
                var first = AudioKit.PlayMusicNamed(address, loop);
                var namedClip = FindSource().clip;
                Check(first.IsPlaying && resources.GetDiagnosticsSnapshot().LeaseCount > 0, "sync Resources holds a real Resource lease");
                AudioKit.ApplyMixerConfig(mixerConfig);
                Check(FindSource().outputAudioMixerGroup == mixerConfig.MusicGroup, "formal Mixer inherited route");
                double mixerFull = 0, mixerHalf = 0;
                yield return Measure("mixer-full", value => mixerFull = value);
                Check(AudioMixerControl.TrySetVolume(mixerConfig.Mixer, "MusicVolume", .5f), "native exposed Mixer write");
                yield return Measure("mixer-half", value => mixerHalf = value);
                Check(mixerHalf > mixerFull * .35 && mixerHalf < mixerFull * .65, "native Mixer changes actual output");
                AudioMixerControl.TryClearFloat(mixerConfig.Mixer, "MusicVolume");
                var second = AudioKit.CrossFadeMusic(secondSignal, .4f);
                yield return new WaitForSecondsRealtime(.12f);
                Check(first.IsValid && second.IsPlaying && resources.GetDiagnosticsSnapshot().LeaseCount > 0, "two real crossfade voices retain tail resource");
                yield return Measure("crossfade", value => Check(value > .001, "crossfade has real output"), .1f);
                yield return new WaitForSecondsRealtime(.45f);
                Check(!first.IsValid && second.IsPlaying && resources.GetDiagnosticsSnapshot().RecordCount == 0, "tail completion returns its Resource lease");
                Check(namedClip, "returning a Resources lease preserves the shared borrowed Clip");
                second.Stop();
                AudioKit.ClearMixerRouting();

                var spatial = AudioKit.PlaySoundAt(signal, new Vector3(0, 0, 1), loop);
                source = FindSource(signal);
                Check(source && source.spatialBlend == 1f, "real spatial Source");
                double near = 0, far = 0;
                yield return Measure("spatial-near", value => near = value);
                // 固定点请求每帧保持其提交位置；移动本 fixture 的 Listener，而非改写框架自有 Source。
                transform.position = new Vector3(0, 0, 1000);
                yield return Measure("spatial-far", value => far = value);
                Check(near > .001 && far < near * .1, "distance affects actual output");
                transform.position = Vector3.zero;
                spatial.Stop();
                var canceled = AudioKit.PlaySoundAsync(address, loop);
                canceled.Cancel();
                yield return null;
                yield return null;
                Check(canceled.IsDone && !canceled.Handle.IsValid && canceled.EndReason == AudioPlayEnd.Canceled, "named async logical cancel");
                var async = AudioKit.PlaySoundAsync(address, loop);
                yield return Wait(() => async.Handle.IsPlaying, "real named async retry");
                namedClip = FindSource().clip;
                Check(resources.GetDiagnosticsSnapshot().LeaseCount > 0, "async resource kept while playing");
                async.Cancel();
                Check(resources.GetDiagnosticsSnapshot().RecordCount == 0, "cancel returns resource ownership");
                Check(namedClip, "cancel does not unload a borrowed Resources Clip");
                var old = AudioKit.PlaySound(signal, loop);
                old.Stop();
                var current = AudioKit.PlaySound(signal, loop);
                old.Stop();
                Check(current.IsPlaying, "pool reuse rejects old generation Stop");
                current.Stop();
                AudioKit.Shutdown();
                Check(AudioKit.SoundStats.OwnedSourceCount == 0, "Shutdown releases owned Source pool");
                _exitProvider = new Provider { Clip = signal };
                _exitHandle = AudioKit.PlaySoundNamed(new AudioClipAddress(_exitProvider, "active"), loop);
                _exitPending = AudioKit.PlayVoiceAsync(new AudioClipAddress(_exitProvider, "pending"));
                Application.quitting += VerifyQuit;
                completed = true;
            }
            finally
            {
                try
                {
                    if (!completed && _ownsAudioSession)
                    {
                        if (group)
                        {
                            group.Stop();
                        }

                        AudioKit.Shutdown();
                    }
                    if (mixerConfig && mixerConfig.Mixer)
                    {
                        AudioMixerControl.TryClearFloat(mixerConfig.Mixer, "MusicVolume");
                    }
                }
                finally
                {
                    try
                    {
                        if (resources != null && resources.IsAvailable)
                        {
                            // 在途 native 可晚于 iterator 退出；关闭请求继续负责收尾，不用 Busy 断言覆盖原始失败。
                            var shutdown = resources.ShutdownAsync();
                            shutdown.Completed += done =>
                            {
                                try
                                {
                                    if (done.Status != ResourceRequestStatus.Succeeded)
                                    {
                                        Debug.LogError("AUDIO45 Resource cleanup failed: " + done.Error);
                                    }
                                }
                                finally
                                {
                                    done.Dispose();
                                }
                            };
                        }
                    }
                    finally
                    {
                        Time.timeScale = scale;
                        for (var i = 0; i < 6; i++)
                        {
                            if (!existed[i])
                            {
                                PlayerPrefs.DeleteKey(keys[i]);
                            }
                            else if (i < 3)
                            {
                                PlayerPrefs.SetInt(keys[i], (int)values[i]);
                            }
                            else
                            {
                                PlayerPrefs.SetFloat(keys[i], values[i]);
                            }
                        }
                    }
                }
            }
        }

        private void VerifyQuit()
        {
            Application.quitting -= VerifyQuit;
            var rejected = false;
            try
            {
                AudioKit.PlaySound(signal);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            var passed = rejected && !_exitHandle.IsValid && _exitPending.IsDone &&
                _exitPending.EndReason == AudioPlayEnd.Shutdown && _exitProvider.Owner.Releases == 1 && AudioKit.SoundStats.OwnedSourceCount == 0;
            if (passed)
            {
                Debug.Log("AUDIO45 QUIT_PASS");
            }
            else
            {
                Result = "FAILED: real quit cleanup";
                Debug.LogError("AUDIO45 QUIT_FAILED");
                // quitting 已不可撤销；Mono fixture 的失败必须以非零码结束，不能被先前 Quit(0) 覆盖。
                if (!Application.isEditor)
                {
                    Environment.Exit(1);
                }
            }
        }
        private static AudioSource FindSource(AudioClip clip = null)
        {
            foreach (var source in FindObjectsOfType<AudioSource>())
            {
                if ((!clip || source.clip == clip) && source.isPlaying)
                {
                    return source;
                }
            }

            return null;
        }
        private IEnumerator Measure(string label, Action<double> consume, float duration = .2f)
        {
            // Listener 混音输出不等于 OS loopback，更不等于人工听感。
            Stage = label;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-audio45-listen") >= 0)
            {
                duration = Mathf.Max(duration, .8f);
            }

            yield return new WaitForSecondsRealtime(.15f);
            double energy = 0;
            var count = 0;
            var deadline = Time.realtimeSinceStartup + duration;
            do
            {
                AudioListener.GetOutputData(_samples, 0);
                foreach (var sample in _samples)
                {
                    energy += sample * sample;
                }

                count += _samples.Length;
                yield return null;
            } while (Time.realtimeSinceStartup < deadline);
            var rms = Math.Sqrt(energy / count);
            Debug.Log("AUDIO45 OUTPUT " + label + " rms=" + rms.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
            consume(rms);
        }
        private void OnGUI() => GUI.Box(new Rect(10, 10, 900, 45), "AUDIO45 " + Stage + " / " + Result);
        private static IEnumerator Wait(Func<bool> ready, string label)
        {
            var deadline = Time.realtimeSinceStartup + 15;
            while (!ready() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Check(ready(), label);
        }
        private static void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException("Audio45 contract: " + label);
            }

            Debug.Log("AUDIO45 CHECK " + label);
        }
    }
}

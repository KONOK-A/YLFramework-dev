using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Logging;

namespace YLFramework.Tests.LoggingOnly
{
    [Category("YL.Special.LoggingOnly")]
    public class MainOnlyLoggingSessionTests
    {
        private bool _configured;
        private bool _enabled;
        private EnterPlayModeOptions _options;
        private string _directory;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (!_configured) { yield break; }
            if (EditorApplication.isPlaying) { yield return new ExitPlayMode(); }
            LoggerManager.Disable();
            EditorSettings.enterPlayModeOptions = _options;
            EditorSettings.enterPlayModeOptionsEnabled = _enabled;
            if (_directory != null && Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
        }

        [UnityTest]
        public IEnumerator MainOnlyLoggingSessionClosesWithoutGameplayEditor()
        {
            Assert.That(Application.isBatchMode, Is.True);
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
            Assert.That(CompilationPipeline.GetAssemblies(AssembliesType.Editor).Any(assembly => assembly.name == "YLFramework.Editor" || assembly.name == "YLFramework.Gameplay.Editor"), Is.False);
            Assert.That(AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetType("YLFramework.Editor.Domain.DomainSessionLifecycle") != null), Is.False,
                "This source-slice fixture must not contain the domain exit coordinator.");
            _enabled = EditorSettings.enterPlayModeOptionsEnabled; _options = EditorSettings.enterPlayModeOptions; _configured = true;
            EditorSettings.enterPlayModeOptionsEnabled = true; EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            _directory = Path.Combine(Application.temporaryCachePath, "YL.LoggingOnly." + Guid.NewGuid().ToString("N"));
            string firstFile = null;
            for (int round = 1; round <= 2; round++)
            {
                yield return new EnterPlayMode(false);
                Assert.That(LoggerManager.IsEnabled, Is.False, "Entry must not reuse a previous session.");
                var session = LoggerManager.Enable(new LoggerOptions { WriteToFile = true, Directory = _directory });
                session.LogFormat(LogType.Log, null, "main-only-logging-round-{0}", round);
                string file = session.LogFilePath;
                Assert.That(File.Exists(file), Is.True);
                yield return new ExitPlayMode();
                Assert.That(LoggerManager.IsEnabled, Is.False, "Only the real Logger host lifecycle may close this session.");
                Assert.That(session.IsDisposed, Is.True);
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { Assert.That(stream.Length, Is.GreaterThan(0)); }
                if (round == 1) { firstFile = file; }
                else
                {
                    Assert.That(file, Is.Not.EqualTo(firstFile));
                    Assert.That(File.ReadAllText(firstFile), Does.Not.Contain("round-2"));
                }
            }
        }
    }
}

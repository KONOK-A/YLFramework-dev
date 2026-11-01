using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Logging;

namespace YLFramework.Tests.Extensions.Domain
{
    public class LoggerLifecycleTests
    {
        private sealed class LaterHandler : ILogHandler
        {
            public void LogException(Exception error, UnityEngine.Object context)
            {
            }
            public void LogFormat(LogType type, UnityEngine.Object context, string format, params object[] args)
            {
            }
        }

        private sealed class ForwardingHandler : ILogHandler
        {
            private readonly ILogHandler _destination;
            internal readonly List<LogType> Kinds = new List<LogType>();
            internal Exception Exception;

            internal ForwardingHandler(ILogHandler destination) => _destination = destination;

            public void LogException(Exception error, UnityEngine.Object context)
            {
                Kinds.Add(LogType.Exception);
                Exception = error;
                _destination.LogException(error, context);
            }

            public void LogFormat(LogType type, UnityEngine.Object context, string format, params object[] args)
            {
                Kinds.Add(type);
                _destination.LogFormat(type, context, format, args);
            }
        }

        [Test]
        public void DefaultIsPassiveAndLaterHandlerKeepsOwnershipOnClose()
        {
            var original = Debug.unityLogger.logHandler;
            Assert.That(LoggerManager.IsEnabled, Is.False);
            Assert.That(Debug.unityLogger.logHandler, Is.SameAs(original));
            Assert.That(GameObject.Find("YLFramework.Logging.LoggerManager"), Is.Null);
            try
            {
                var session = LoggerManager.Enable();
                Assert.That(Debug.unityLogger.logHandler, Is.SameAs(session));
                Assert.That(LoggerManager.Enable(), Is.SameAs(session));
                Assert.That(session.LogFilePath, Is.Null);
                var later = new LaterHandler();
                Debug.unityLogger.logHandler = later;
                LoggerManager.Disable();
                Assert.That(Debug.unityLogger.logHandler, Is.SameAs(later));
                Assert.That(session.IsDisposed, Is.True);
                Assert.That(LoggerManager.IsEnabled, Is.False);
            }
            finally
            {
                LoggerManager.Disable();
                Debug.unityLogger.logHandler = original;
            }
        }

        [Test]
        public void FiveRealDebugKindsReachEventsFileAndPreviousHandler()
        {
            Assert.That(LoggerManager.IsEnabled, Is.False);
            var original = Debug.unityLogger.logHandler;
            var previous = new ForwardingHandler(original);
            var directory = Path.Combine(Path.GetTempPath(), "YLDomainC-RealDebug-" + Guid.NewGuid().ToString("N"));
            try
            {
                Debug.unityLogger.logHandler = previous;
                var session = LoggerManager.Enable(new LoggerOptions { WriteToFile = true, Directory = directory });
                var kinds = new List<LogType>();
                session.OnLogEvent += (type, message) => kinds.Add(type);
                LogAssert.Expect(LogType.Log, new Regex("YL_DOMAIN_REAL_LOG"));
                Debug.Log("YL_DOMAIN_REAL_LOG");
                LogAssert.Expect(LogType.Warning, new Regex("YL_DOMAIN_REAL_WARNING"));
                Debug.LogWarning("YL_DOMAIN_REAL_WARNING");
                LogAssert.Expect(LogType.Error, new Regex("YL_DOMAIN_REAL_ERROR"));
                Debug.LogError("YL_DOMAIN_REAL_ERROR");
                LogAssert.Expect(LogType.Assert, new Regex("YL_DOMAIN_REAL_ASSERT"));
                Debug.Assert(false, "YL_DOMAIN_REAL_ASSERT");
                LogAssert.Expect(LogType.Exception, new Regex("YL_DOMAIN_REAL_EXCEPTION"));
                var error = new InvalidOperationException("YL_DOMAIN_REAL_EXCEPTION");
                Debug.LogException(error);
                Assert.That(kinds, Is.EqualTo(new[] { LogType.Log, LogType.Warning, LogType.Error, LogType.Assert, LogType.Exception }));
                Assert.That(previous.Kinds, Is.EqualTo(kinds), "Each real Debug call must reach the actual predecessor exactly once.");
                Assert.That(previous.Exception, Is.SameAs(error));
                var file = session.LogFilePath;
                LoggerManager.Disable();
                Assert.That(Debug.unityLogger.logHandler, Is.SameAs(previous));
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.That(stream.Length, Is.GreaterThan(0));
                }

                var text = File.ReadAllText(file);
                foreach (var marker in new[] { "LOG", "WARNING", "ERROR", "ASSERT", "EXCEPTION" })
                {
                    Assert.That(text, Does.Contain("YL_DOMAIN_REAL_" + marker));
                }
            }
            finally
            {
                LoggerManager.Disable();
                Debug.unityLogger.logHandler = original;
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }
}

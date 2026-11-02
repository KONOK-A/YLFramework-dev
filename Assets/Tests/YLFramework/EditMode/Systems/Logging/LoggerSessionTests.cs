using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Events;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Domain")]
    public class LoggerSessionTests
    {
        private Type SessionType => typeof(EventChannel).Assembly.GetType("YLFramework.Logging.LoggerSession");
        private readonly List<IDisposable> _owned = new();
        private string _directory;

        private sealed class Sink : ILogHandler
        {
            internal readonly List<string> Messages = new();
            public void LogException(Exception error, UnityEngine.Object context)
            {
                lock (Messages)
                {
                    Messages.Add(error.ToString());
                }
            }
            public void LogFormat(LogType type, UnityEngine.Object context, string format, params object[] args)
            {
                lock (Messages)
                {
                    Messages.Add(string.Format(format, args));
                }
            }
        }

        private sealed class LoggingValue
        {
            internal ILogHandler Handler;
            public override string ToString()
            {
                Handler.LogFormat(LogType.Log, null, "nested-format");
                return "formatted-value";
            }
        }

        private sealed class Forwarder : ILogHandler
        {
            private readonly ILogHandler _next;
            internal Forwarder(ILogHandler next) => _next = next;
            public void LogException(Exception error, UnityEngine.Object context) => _next.LogException(error, context);
            public void LogFormat(LogType type, UnityEngine.Object context, string format, params object[] args) => _next.LogFormat(type, context, format, args);
        }

        [SetUp] public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "YLDomainC-Logger-" + Guid.NewGuid().ToString("N"));
        [TearDown]
        public void Cleanup()
        {
            foreach (var item in _owned)
            {
                item.Dispose();
            }

            _owned.Clear();
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_directory)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_directory), true);
            }

            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_directory)))
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_directory));
            }
        }

        private ILogHandler Create(ILogHandler sink, bool file = true, Dictionary<LogType, bool> console = null, string directory = null)
        {
            Assert.That(SessionType, Is.Not.Null, "YL LoggerSession must exist.");
            var optionsType = SessionType.Assembly.GetType("YLFramework.Logging.LoggerOptions");
            var options = Activator.CreateInstance(optionsType);
            optionsType.GetProperty("WriteToFile").SetValue(options, file);
            optionsType.GetProperty("Directory").SetValue(options, directory ?? _directory);
            if (console != null)
            {
                optionsType.GetProperty("ConsoleFilter").SetValue(options, console);
            }

            var session = (ILogHandler)Activator.CreateInstance(SessionType, sink, options);
            _owned.Add((IDisposable)session);
            return session;
        }

        private object Read(object session, string property) => SessionType.GetProperty(property).GetValue(session);

        [Test]
        public void FileIsLazyAndSessionsAreUniqueAndExclusiveAfterDispose()
        {
            var first = Create(new Sink());
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_directory)), Is.False);
            first.LogFormat(LogType.Log, null, "session {0}", 1);
            var path1 = (string)Read(first, "LogFilePath");
            ((IDisposable)first).Dispose();
            using (var file = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path1), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(file.Length, Is.GreaterThan(0));
            }

            var second = Create(new Sink());
            second.LogFormat(LogType.Log, null, "session {0}", 2);
            ((IDisposable)second).Dispose();
            var path2 = (string)Read(second, "LogFilePath");
            Assert.That(path2, Is.Not.EqualTo(path1));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path1)), Does.Contain("session 1").And.Not.Contain("session 2"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path2)), Does.Contain("session 2"));
        }

        [Test]
        public void FiltersDoNotSuppressEventsAndEveryLogTypeIsFormatted()
        {
            var sink = new Sink();
            var filter = new Dictionary<LogType, bool> { [LogType.Warning] = true };
            var session = Create(sink, false, filter);
            var messages = new List<string>();
            Action<LogType, string> listener = (type, text) => messages.Add(type + ":" + text);
            SessionType.GetEvent("OnLogEvent").AddEventHandler(session, listener);
            filter[LogType.Log] = true;
            foreach (var type in new[] { LogType.Log, LogType.Warning, LogType.Error, LogType.Assert })
            {
                session.LogFormat(type, null, "value {0}", 7);
            }

            session.LogException(new InvalidOperationException("exception-marker"), null);
            Assert.That(sink.Messages, Is.EqualTo(new[] { "value 7" }), "会话必须复制过滤配置，不能并发读可变字典。");
            Assert.That(messages.Count, Is.EqualTo(5));
            Assert.That(messages[4], Does.Contain("exception-marker"));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_directory)), Is.False);
        }

        [Test]
        public void ReentrantAndThrowingListenersCannotRecurseOrBlockLaterListeners()
        {
            var session = Create(new Sink());
            var calls = 0;
            SessionType.GetEvent("OnLogEvent").AddEventHandler(session, (Action<LogType, string>)((_, __) =>
            {
                session.LogFormat(LogType.Log, null, "nested");
                throw new InvalidOperationException("subscriber-marker");
            }));
            SessionType.GetEvent("OnLogEvent").AddEventHandler(session, (Action<LogType, string>)((_, __) => calls++));
            Assert.DoesNotThrow(() => session.LogFormat(LogType.Log, null, "outer"));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(Read(session, "SuppressedRecursiveCount"), Is.EqualTo(1));
            Assert.That(((Exception)Read(session, "LastError")).Message, Does.Contain("subscriber-marker"));
        }

        [Test]
        public void ParallelLoggingWritesCompleteRecordsAndDisposedChainStillForwards()
        {
            var sink = new Sink();
            var session = Create(sink);
            Parallel.For(0, 100, i => session.LogFormat(LogType.Log, null, "record-{0}", i));
            ((IDisposable)session).Dispose();
            var lines = File.ReadAllLines(global::YLFramework.Testing.PackageTestPaths.Physical((string)Read(session, "LogFilePath"))).Where(line => line.Contains("record-")).ToArray();
            Assert.That(lines.Length, Is.EqualTo(100));
            Assert.That(lines.Distinct().Count(), Is.EqualTo(100));
            session.LogFormat(LogType.Log, null, "after-close");
            Assert.That(sink.Messages.Count, Is.EqualTo(101));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical((string)Read(session, "LogFilePath"))), Does.Not.Contain("after-close"));
        }

        [Test]
        public void FileFailureIsObservableWithoutBreakingConsoleOrEvents()
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_directory), "not a directory");
            var sink = new Sink();
            var session = Create(sink);
            session.LogFormat(LogType.Error, null, "still-visible");
            Assert.That(sink.Messages, Is.EqualTo(new[] { "still-visible" }));
            Assert.That(Read(session, "LastError"), Is.InstanceOf<IOException>());
        }

        [Test]
        public void FormattingReentryIsGuardedBeforeCallingUserToString()
        {
            var sink = new Sink();
            var session = Create(sink, false);
            session.LogFormat(LogType.Log, null, "{0}", new LoggingValue { Handler = session });
            Assert.That(sink.Messages, Is.EqualTo(new[] { "formatted-value" }));
            Assert.That(Read(session, "SuppressedRecursiveCount"), Is.EqualTo(1));
        }

        [Test]
        public void LaterHandlerCanRetainClosedSessionAfterAnotherSessionEnables()
        {
            var sink = new Sink();
            var old = Create(sink, false);
            ((IDisposable)old).Dispose();
            var current = Create(new Forwarder(old), false);
            current.LogFormat(LogType.Log, null, "through-closed-session");
            Assert.That(sink.Messages, Is.EqualTo(new[] { "through-closed-session" }));
            Assert.That(Read(old, "SuppressedRecursiveCount"), Is.EqualTo(0));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Pooling;

namespace YLFramework.Tests
{
    public class PoolSessionResetTests
    {
        [Test]
        public void ResetAttemptsLaterCallbacksAndAggregatesFailuresWithoutOpeningTheGate()
        {
            var state = typeof(ObjectPool<>).Assembly.GetType("YLFramework.Singletons.SingletonSessionState", true);
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var reset = state.GetMethod("ResetForNewSession", flags);
            var callbacks = (List<Action>)state.GetField("_sessionResets", flags).GetValue(null);
            var stopped = state.GetField("_isExecutionStopping", flags);
            var first = new InvalidOperationException("first-session-reset");
            var last = new ApplicationException("last-session-reset");
            var order = new List<int>();
            Action a = () => { order.Add(1); throw first; };
            Action b = () => order.Add(2);
            Action c = () => { order.Add(3); throw last; };
            lock (callbacks)
            {
                callbacks.Add(a);
                callbacks.Add(b);
                callbacks.Add(c);
            }
            try
            {
                var error = Assert.Throws<TargetInvocationException>(() => reset.Invoke(null, null));
                Assert.That(error.InnerException, Is.TypeOf<AggregateException>());
                Assert.That(((AggregateException)error.InnerException).InnerExceptions, Is.EqualTo(new Exception[] { first, last }));
                Assert.That(order, Is.EqualTo(new[] { 1, 2, 3 }));
                Assert.That(stopped.GetValue(null), Is.True);
            }
            finally
            {
                lock (callbacks)
                {
                    callbacks.Remove(a);
                    callbacks.Remove(b);
                    callbacks.Remove(c);
                }
                // 仅撤销本fixture注入；健康初始化才重开门禁，不清空别人的注册表。
                reset.Invoke(null, null);
            }
            Assert.That(stopped.GetValue(null), Is.False);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.SmartFormat.PersistentVariables;
using UnityEngine.TestTools;
using YLFramework.Binding;
using YLFramework.Events;
using YLFramework.Localization;

namespace YLFramework.Tests.Localization
{
    public class LocalizationVariableTests
    {
        private static IDisposable Variable<T>(LocalizedStringBinding binding, string name, IReadOnlyBindableProperty<T> source)
        {
            return binding.BindVariable(name, source);
        }

        [UnityTest]
        public IEnumerator PropertyChangesKeepNumericTypeAndUseOfficialLocaleCulture()
        {
            using var fixture = new LocalizationFixture();
            var count = new CountingProperty<int>(1000);
            var values = new List<string>();
            using var session = new LocalizationSession();
            session.BindString(new LocalizedString(fixture.Strings, "count"), values.Add,
                binding => Variable(binding, "count", count));
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(values, Is.EqualTo(new[] { 1000.ToString("N0", fixture.English.Identifier.CultureInfo) }));
            count.Value = 1000;
            yield return null;
            Assert.That(values.Count, Is.EqualTo(1));
            count.Value = 2000;
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(values[values.Count - 1], Is.EqualTo(2000.ToString("N0", fixture.English.Identifier.CultureInfo)));
            count.SetValueWithoutCallback(3000);
            yield return null;
            Assert.That(values[values.Count - 1], Is.EqualTo(2000.ToString("N0", fixture.English.Identifier.CultureInfo)));
            count.ForceNotify();
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(values[values.Count - 1], Is.EqualTo(3000.ToString("N0", fixture.English.Identifier.CultureInfo)));
            LocalizationSettings.SelectedLocale = fixture.French;
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(values[values.Count - 1], Is.EqualTo(3000.ToString("N0", fixture.French.Identifier.CultureInfo)));
            Assert.That(count.Registrations, Is.EqualTo(1));
            session.Dispose();
            Assert.That(count.Registrations, Is.Zero);
        }

        [UnityTest]
        public IEnumerator TwoConsumersKeepPrivateVariablesWithoutMutatingTheTemplate()
        {
            using var fixture = new LocalizationFixture();
            using var first = new LocalizationSession();
            using var second = new LocalizationSession();
            var template = new LocalizedString(fixture.Strings, "count");
            var firstValue = "";
            var secondValue = "";
            var firstCount = new CountingProperty<int>(2);
            var secondCount = new CountingProperty<int>(7);
            first.BindString(template, value => firstValue = value, binding => Variable(binding, "count", firstCount));
            second.BindString(template, value => secondValue = value, binding => Variable(binding, "count", secondCount));
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(firstValue, Is.EqualTo("2"));
            Assert.That(secondValue, Is.EqualTo("7"));
            firstCount.Value = 3;
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(firstValue, Is.EqualTo("3"));
            Assert.That(secondValue, Is.EqualTo("7"));
            Assert.That(template.Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator VariableChangesDuringTableLoadAreNotLost()
        {
            using var fixture = new LocalizationFixture { DelayTables = true };
            using var session = new LocalizationSession();
            var count = new CountingProperty<int>(1);
            var values = new List<string>();
            session.BindString(new LocalizedString(fixture.Strings, "count"), values.Add,
                binding => Variable(binding, "count", count));
            for (var i = 0; i < 8 && fixture.PendingTableLoads == 0; i++)
                yield return null;
            Assert.That(fixture.PendingTableLoads, Is.EqualTo(1));
            count.Value = 4;
            count.Value = 9;
            Assert.That(values, Is.Empty);
            fixture.CompletePending();
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(values, Is.EqualTo(new[] { "9" }));
        }

        [UnityTest]
        public IEnumerator ReleasingTheVariableUnregistersAndFreezesItsLastValue()
        {
            using var fixture = new LocalizationFixture();
            using var session = new LocalizationSession();
            var count = new CountingProperty<int>(5);
            var values = new List<string>();
            IDisposable variable = null;
            session.BindString(new LocalizedString(fixture.Strings, "count"), values.Add,
                binding => variable = Variable(binding, "count", count));
            for (var i = 0; i < 3; i++)
                yield return null;
            variable.Dispose();
            variable.Dispose();
            Assert.That(count.Registrations, Is.Zero);
            count.Value = 6;
            LocalizationSettings.SelectedLocale = fixture.French;
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(values[values.Count - 1], Is.EqualTo("5"));
        }

        [Test]
        public void FailedConfigurationUnregistersTheWholeAttempt()
        {
            using var fixture = new LocalizationFixture();
            using var session = new LocalizationSession();
            var count = new CountingProperty<int>(1);
            Assert.Throws<InvalidOperationException>(() => session.BindString(
                new LocalizedString(fixture.Strings, "count"), _ => { }, binding =>
                {
                    Variable(binding, "count", count);
                    throw new InvalidOperationException("Configuration failed");
                }));
            Assert.That(count.Registrations, Is.Zero);
        }

        [Test]
        public void ExistingTemplateVariableCannotBeSilentlyReplaced()
        {
            using var fixture = new LocalizationFixture();
            using var session = new LocalizationSession();
            var count = new CountingProperty<int>(1);
            var template = new LocalizedString(fixture.Strings, "count");
            template.Add("count", new IntVariable { Value = 13 });
            Assert.Throws<ArgumentException>(() => session.BindString(
                template, _ => { }, binding => Variable(binding, "count", count)));
            Assert.That(count.Registrations, Is.Zero);
            Assert.That(((IntVariable)template["count"]).Value, Is.EqualTo(13));
        }

        [UnityTest]
        public IEnumerator ReleasedOwnedSlotCanBeExplicitlyBoundToANewSource()
        {
            using var fixture = new LocalizationFixture();
            using var session = new LocalizationSession();
            var first = new CountingProperty<int>(1);
            var second = new CountingProperty<int>(8);
            var values = new List<string>();
            IDisposable token = null;
            var binding = session.BindString(new LocalizedString(fixture.Strings, "count"), values.Add,
                current => token = current.BindVariable("count", first));
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.Throws<ArgumentException>(() => binding.BindVariable("count", second));
            Assert.That(second.Registrations, Is.Zero);
            token.Dispose();
            binding.BindVariable("count", second);
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(values[values.Count - 1], Is.EqualTo("8"));
            Assert.That(first.Registrations, Is.Zero);
            Assert.That(second.Registrations, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator VariableTokenRejectsBackgroundReleaseWithoutLosingItsRegistration()
        {
            using var fixture = new LocalizationFixture();
            using var session = new LocalizationSession();
            var source = new CountingProperty<int>(1);
            IDisposable token = null;
            session.BindString(new LocalizedString(fixture.Strings, "count"), _ => { },
                binding => token = binding.BindVariable("count", source));
            var attempt = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    token.Dispose();
                    return null;
                }
                catch (Exception error) { return error; }
            });
            while (!attempt.IsCompleted)
                yield return null;
            Assert.That(attempt.Result, Is.TypeOf<InvalidOperationException>());
            Assert.That(source.Registrations, Is.EqualTo(1));
            token.Dispose();
            Assert.That(source.Registrations, Is.Zero);
        }

        [Test]
        public void OneReleaseFailureDoesNotPreventOtherVariableCleanupAndItsTokenCanRetry()
        {
            using var fixture = new LocalizationFixture();
            var session = new LocalizationSession();
            var first = new CountingProperty<int>(1) { ReleaseThrows = true };
            var second = new CountingProperty<int>(2);
            IDisposable token = null;
            session.BindString(new LocalizedString(fixture.Strings, "count"), _ => { }, binding =>
            {
                token = binding.BindVariable("count", first);
                binding.BindVariable("extra", second);
            });
            Assert.Throws<AggregateException>(session.Dispose);
            Assert.That(first.Registrations, Is.EqualTo(1));
            Assert.That(second.Registrations, Is.Zero);
            first.ReleaseThrows = false;
            token.Dispose();
            Assert.That(first.Registrations, Is.Zero);
        }

        [Test]
        public void ScopeRetainsFailedCleanupEvenWhenTheCallerDoesNotKeepVariableTokens()
        {
            using var fixture = new LocalizationFixture();
            var session = new LocalizationSession();
            var source = new CountingProperty<int>(1) { ReleaseThrows = true };
            session.BindString(new LocalizedString(fixture.Strings, "count"), _ => { },
                binding => binding.BindVariable("count", source));
            Assert.Throws<AggregateException>(session.Dispose);
            Assert.That(session.IsDisposed, Is.True);
            Assert.That(source.Registrations, Is.EqualTo(1));
            source.ReleaseThrows = false;
            session.Dispose();
            Assert.That(source.Registrations, Is.Zero,
                "The owner must retain failed cleanup, not require a separately saved child token.");
        }

        private sealed class CountingProperty<T> : BindableProperty<T>
        {
            public int Registrations
            {
                get; private set;
            }
            public bool ReleaseThrows;
            public CountingProperty(T value) : base(value) { }
            public override IUnregister Register(Action<T> callback)
            {
                var registration = base.Register(callback);
                Registrations++;
                return new CustomUnregister(() =>
                {
                    if (ReleaseThrows)
                        throw new InvalidOperationException("Source unregister failed");
                    registration.Unregister();
                    Registrations--;
                });
            }
        }
    }
}

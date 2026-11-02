using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace YLFramework.Tests.Extensions.Data
{
    public class LocalizationLogicTests
    {
        private static Type Required(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Localization." + name)).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, "Missing localization behavior: " + name);
            return type;
        }

        private static SystemLanguage[] Candidates(SystemLanguage request, params SystemLanguage[] available)
        {
            return ((IEnumerable<SystemLanguage>)Required("LocalizationFallback").GetMethod("GetCandidates")
                .Invoke(null, new object[] { request, available })).ToArray();
        }

        [Test]
        public void NonChineseRequestPrefersEnglishBeforeExplicitChinese()
        {
            Assert.That(Candidates(SystemLanguage.Japanese, SystemLanguage.Chinese, SystemLanguage.English),
                Is.EqualTo(new[] { SystemLanguage.Japanese, SystemLanguage.English, SystemLanguage.Chinese }));
        }

        [TestCase(SystemLanguage.ChineseSimplified, SystemLanguage.Chinese, SystemLanguage.ChineseTraditional)]
        [TestCase(SystemLanguage.ChineseTraditional, SystemLanguage.Chinese, SystemLanguage.ChineseSimplified)]
        [TestCase(SystemLanguage.Chinese, SystemLanguage.ChineseSimplified, SystemLanguage.ChineseTraditional)]
        public void ChineseRequestUsesApplicableVariantOrder(SystemLanguage request, SystemLanguage second, SystemLanguage third)
        {
            Assert.That(Candidates(request, third, SystemLanguage.English, second),
                Is.EqualTo(new[] { request, second, third, SystemLanguage.English }));
        }

        [Test]
        public void ExplicitFallbackOrderIsStableDeduplicatedAndSkipsUnknown()
        {
            Assert.That(Candidates(SystemLanguage.Japanese, SystemLanguage.French, SystemLanguage.Unknown, SystemLanguage.German, SystemLanguage.French),
                Is.EqualTo(new[] { SystemLanguage.Japanese, SystemLanguage.English, SystemLanguage.French, SystemLanguage.German }));
        }

        private static object Entry(int id, string value)
        {
            var type = Required("LanguageInfo");
            var entry = Activator.CreateInstance(type);
            type.GetField("id").SetValue(entry, id);
            type.GetField("str").SetValue(entry, value);
            return entry;
        }

        private static IList Entries(params object[] entries)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Required("LanguageInfo")));
            foreach (var entry in entries)
            {
                list.Add(entry);
            }

            return list;
        }

        private static object Find(object index, IList entries, int id)
        {
            try
            {
                return index.GetType().GetMethod("Find").Invoke(index, new object[] { entries, id, "fixture/English" });
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException;
            }
        }

        [Test]
        public void EntryCacheDetectsEqualCountReplacementAndKeyEdit()
        {
            var index = Activator.CreateInstance(Required("LocalizationEntryIndex"), true);
            var first = Entry(1, "old");
            var entries = Entries(first);
            Assert.That(Find(index, entries, 1), Is.SameAs(first));
            var replacement = Entry(2, "new");
            entries[0] = replacement;
            Assert.That(Find(index, entries, 1), Is.Null);
            Assert.That(Find(index, entries, 2), Is.SameAs(replacement));
            replacement.GetType().GetField("id").SetValue(replacement, 3);
            Assert.That(Find(index, entries, 2), Is.Null);
            Assert.That(Find(index, entries, 3), Is.SameAs(replacement));
        }

        [Test]
        public void EntryCacheRejectsDuplicateAndNullEntriesWithoutRetainingPartialIndex()
        {
            var index = Activator.CreateInstance(Required("LocalizationEntryIndex"), true);
            var entries = Entries(Entry(7, "a"), Entry(7, "b"));
            Assert.That(() => Find(index, entries, 7), Throws.InvalidOperationException.With.Message.Contains("7").And.Message.Contains("fixture/English"));
            entries.RemoveAt(1);
            Assert.That(Find(index, entries, 7), Is.SameAs(entries[0]));
            entries.Add(null);
            Assert.That(() => Find(index, entries, 7), Throws.InvalidOperationException.With.Message.Contains("1"));
        }

        [Test]
        public void MissingEntryDoesNotReturnEarlierCachedValue()
        {
            var index = Activator.CreateInstance(Required("LocalizationEntryIndex"), true);
            var entries = Entries(Entry(9, "a"));
            Assert.That(Find(index, entries, 9), Is.Not.Null);
            entries.Clear();
            Assert.That(Find(index, entries, 9), Is.Null);
        }

        [Test]
        public void MissingRequestedEntrySearchesAllLanguagesInFallbackOrder()
        {
            var method = Required("LocalizationFallback").GetMethod("Find");
            Assert.That(method, Is.Not.Null, "Per-entry fallback must use the same candidate order.");
            var seen = new List<SystemLanguage>();
            Func<SystemLanguage, string> lookup = language =>
            {
                seen.Add(language);
                return language == SystemLanguage.English ? "english" : language == SystemLanguage.Chinese ? "chinese" : null;
            };
            var result = method.MakeGenericMethod(typeof(string)).Invoke(null,
                new object[] { SystemLanguage.Japanese, new[] { SystemLanguage.Chinese, SystemLanguage.English }, lookup });
            Assert.That(result, Is.EqualTo("english"));
            Assert.That(seen, Is.EqualTo(new[] { SystemLanguage.Japanese, SystemLanguage.English }));
        }

        [Test]
        public void AllEntriesMissingReturnsNullAfterStableExplicitFallback()
        {
            var method = Required("LocalizationFallback").GetMethod("Find");
            Assert.That(method, Is.Not.Null);
            var seen = new List<SystemLanguage>();
            Func<SystemLanguage, string> lookup = language =>
            {
                seen.Add(language);
                return null;
            };
            Assert.That(method.MakeGenericMethod(typeof(string)).Invoke(null,
                new object[] { SystemLanguage.Japanese, new[] { SystemLanguage.French, SystemLanguage.German }, lookup }), Is.Null);
            Assert.That(seen, Is.EqualTo(new[] { SystemLanguage.Japanese, SystemLanguage.English, SystemLanguage.French, SystemLanguage.German }));
        }

        [Test]
        public void TextCompositionFormatsArgumentsAndAppliesRichTextOnce()
        {
            var type = Required("AppendData");
            var args = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            var appends = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            var argument = Activator.CreateInstance(type);
            type.GetField("id").SetValue(argument, 2);
            args.Add(argument);
            var append = Activator.CreateInstance(type);
            type.GetField("id").SetValue(append, 3);
            type.GetField("richText").SetValue(append, "<b>{0}</b>");
            appends.Add(append);
            Func<int, string> lookup = id => id == 1 ? "Hello {0}" : id == 2 ? "World" : "!";
            var value = Required("LocalizationText").GetMethod("Compose").Invoke(null, new object[] { 1, lookup, args, appends });
            Assert.That(value, Is.EqualTo("Hello World<b>!</b>"));
        }

        [Test]
        public void TextCompositionFormatErrorIdentifiesRequestedId()
        {
            var type = Required("AppendData");
            var args = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            args.Add(Activator.CreateInstance(type));
            Func<int, string> lookup = id => id == 17 ? "{4}" : "value";
            var method = Required("LocalizationText").GetMethod("Compose");
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { 17, lookup, args, null }));
            Assert.That(error.InnerException, Is.TypeOf<FormatException>().And.Message.Contains("17"));
        }
    }
}

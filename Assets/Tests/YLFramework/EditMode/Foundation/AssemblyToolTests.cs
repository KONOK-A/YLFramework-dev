using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NUnit.Framework;

namespace YLFramework.Foundation.Tests
{
    public class AssemblyToolTests
    {
        private static Type Find(string name)
        {
            Assert.That(typeof(AssemblyTool).Assembly, Is.SameAs(typeof(ApplicationTool).Assembly));
            return AssemblyTool.GetType(name);
        }

        [Test]
        public void FullNamesAreOrdinalAndSupportNestedAndOpenGenericTypes()
        {
            Assert.That(Find(null), Is.Null);
            Assert.That(Find(" \t"), Is.Null);
            Assert.That(Find("String"), Is.Null);
            Assert.That(Find("system.string"), Is.Null);
            Assert.That(Find(typeof(string).AssemblyQualifiedName), Is.Null);
            Assert.That(Find("System.String"), Is.SameAs(typeof(string)));
            Assert.That(Find(typeof(AssemblyToolTests).FullName), Is.SameAs(typeof(AssemblyToolTests)));
            Assert.That(Find(typeof(Nested).FullName), Is.SameAs(typeof(Nested)));
            Assert.That(Find("System.Collections.Generic.List`1"), Is.SameAs(typeof(List<>)));
        }

        [Test]
        public void FailedLookupCanSeeAnAssemblyLoadedLater()
        {
            string name = "LookupProbe.Late" + Guid.NewGuid().ToString("N");
            Assert.That(Find(name), Is.Null);
            var cache = (Dictionary<string, Type>)typeof(AssemblyTool)
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(Dictionary<string, Type>)).GetValue(null);
            Assert.That(cache.ContainsKey(name), Is.False, "A miss must not become a cached null.");
            var type = Define(name);
            Assert.That(Find(name), Is.SameAs(type));
            Assert.That(cache[name], Is.SameAs(type), "Successful discovery must be memoized.");
            Assert.That(Find(name), Is.SameAs(type));
        }

        [Test]
        public void DuplicateFullNamesUseFirstLoadedMatchAndKeepSuccessfulCache()
        {
            string name = "LookupProbe.Duplicate" + Guid.NewGuid().ToString("N");
            var first = Define(name);
            Assert.That(Find(name), Is.SameAs(first));
            Define(name);
            Assert.That(Find(name), Is.SameAs(first));

            string uncachedName = "LookupProbe.Uncached" + Guid.NewGuid().ToString("N");
            var expected = Define(uncachedName);
            Define(uncachedName);
            Assert.That(Find(uncachedName), Is.SameAs(expected));
        }

        private static Type Define(string name)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("LookupProbe" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            return assembly.DefineDynamicModule("Probe").DefineType(name, TypeAttributes.Public).CreateType();
        }

        private class Nested
        {
        }
    }
}

using System;
using System.Reflection;
using NUnit.Framework;

namespace YLFramework.Tests.Extensions.Shared
{
    public class ExcelFieldContractTests
    {
        private static Type Contract(string name)
        {
            var type = typeof(YLFramework.YLFrameworkRoot).Assembly.GetType("YLFramework.Excel." + name);
            Assert.That(type, Is.Not.Null, "缺少跨组字段契约：" + name);
            return type;
        }

        [TestCase("ExcelIgnore")]
        [TestCase("ExcelScriptableObjectId")]
        public void FieldMarkersCanBeConstructedAndDiscoveredAsAttributes(string name)
        {
            var attribute = Activator.CreateInstance(Contract(name));
            Assert.That(attribute, Is.InstanceOf<Attribute>());
            Assert.That(attribute.GetType().Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
        }

        [Test]
        public void ExplicitColumnNameRemainsALightweightMainPackageFieldContract()
        {
            var type = Contract("ExcelColumnNameAttribute");
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(type.IsSealed, Is.True);
            var usage = type.GetCustomAttribute<AttributeUsageAttribute>();
            Assert.That(usage.ValidOn, Is.EqualTo(AttributeTargets.Field));
            Assert.That(usage.AllowMultiple, Is.False);
            var column = Activator.CreateInstance(type, "old_column_name");
            Assert.That(type.GetProperty("Name").GetValue(column), Is.EqualTo("old_column_name"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void ExplicitColumnNameRejectsMissingOrWhitespaceNames(string name)
        {
            var constructor = Contract("ExcelColumnNameAttribute").GetConstructor(new[] { typeof(string) });
            var error = Assert.Throws<TargetInvocationException>(() => constructor.Invoke(new object[] { name }));
            Assert.That(error.InnerException, Is.InstanceOf<ArgumentException>());
        }

        [TestCase("None", 0)]
        [TestCase("Int", 1)]
        [TestCase("Float", 2)]
        [TestCase("Double", 3)]
        [TestCase("String", 4)]
        [TestCase("Bool", 5)]
        [TestCase("Long", 6)]
        [TestCase("Json", 7)]
        [TestCase("ObjectReference", 8)]
        [TestCase("Enum", 9)]
        public void SerializedFieldKindsRetainTheirSourceValues(string name, int value)
        {
            Assert.That(Convert.ToInt32(Enum.Parse(Contract("ExcelFieldsType"), name)), Is.EqualTo(value));
        }

        [Test]
        public void CustomConverterKeepsTheExistingFieldAssignmentBoundary()
        {
            var type = Contract("CustomExcelFieldImportAndExport");
            var kind = Contract("ExcelFieldsType");
            Assert.That(typeof(Attribute).IsAssignableFrom(type), Is.True);
            Assert.That(type.IsAbstract, Is.True);
            foreach (var name in new[] { "Import", "Export" })
            {
                var method = type.GetMethod(name, new[] { typeof(object), typeof(object), kind });
                Assert.That(method, Is.Not.Null, name);
                Assert.That(method.IsAbstract, Is.True, name);
                Assert.That(method.ReturnType, Is.EqualTo(typeof(object)), name);
            }
            var assignment = type.GetMethod("SetValue", new[] { typeof(object), typeof(FieldInfo), typeof(object) });
            Assert.That(assignment, Is.Not.Null);
            Assert.That(assignment.IsAbstract, Is.True);
            Assert.That(assignment.ReturnType, Is.EqualTo(typeof(void)));
        }
    }
}

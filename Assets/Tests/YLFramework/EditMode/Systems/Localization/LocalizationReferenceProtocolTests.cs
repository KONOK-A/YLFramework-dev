using System;
using NUnit.Framework;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.SmartFormat.PersistentVariables;
using YLFramework.Excel;

namespace YLFramework.Tests.Localization
{
    public class LocalizationReferenceProtocolTests
    {
        public sealed class Row
        {
            public LocalizedString title;
            public LocalizedSprite image;
        }

        private static CustomExcelFieldImportAndExport Converter()
        {
            var type = Type.GetType("YLFramework.Localization.LocalizedExcelReferenceAttribute, YLFramework.Localization");
            Assert.That(type, Is.Not.Null, "Excel must convert official references without a second translation data model.");
            return (CustomExcelFieldImportAndExport)Activator.CreateInstance(type);
        }

        [Test]
        public void LongKeyIdRoundTripsAsTextWithoutPrecisionLoss()
        {
            var converter = Converter();
            var row = new Row();
            const string json = "{\"tableGuid\":\"123456781234123412341234567890ab\",\"entryId\":\"9007199254740993\"}";
            converter.SetValue(row, typeof(Row).GetField("title"), converter.Import(row, json, ExcelFieldsType.Json));
            Assert.That(row.title.TableEntryReference.KeyId, Is.EqualTo(9007199254740993L));
            Assert.That(row.title.TableReference.TableCollectionNameGuid.ToString("N"), Is.EqualTo("123456781234123412341234567890ab"));
            Assert.That(converter.Export(row, row.title, ExcelFieldsType.Json), Is.EqualTo(json));
        }

        [Test]
        public void ReadableReferencesPreserveTheOfficialConcreteFieldType()
        {
            var converter = Converter();
            var row = new Row();
            converter.SetValue(row, typeof(Row).GetField("image"), converter.Import(row,
                "{\"table\":\"Items\",\"entry\":\"icon.sword\"}", ExcelFieldsType.Json));
            Assert.That(row.image, Is.TypeOf<LocalizedSprite>());
            Assert.That(row.image.TableReference.TableCollectionName, Is.EqualTo("Items"));
            Assert.That(row.image.TableEntryReference.Key, Is.EqualTo("icon.sword"));
        }

        [TestCase("{\"tableGuid\":\"123456781234123412341234567890ab\",\"entryId\":9007199254740993}")]
        [TestCase("{\"table\":\"Items\",\"entryId\":\"3\"}")]
        [TestCase("{\"table\":\"Items\",\"entry\":\"x\",\"unknown\":true}")]
        [TestCase("{\"table\":\"Items\",\"table\":\"Other\",\"entry\":\"x\"}")]
        [TestCase("{\"tableGuid\":\"not-a-guid\",\"entryId\":\"3\"}")]
        [TestCase("{\"tableGuid\":\"123456781234123412341234567890ab\",\"entryId\":\"9223372036854775808\"}")]
        public void MalformedAmbiguousOrLossyInputIsRejected(string json)
        {
            var converter = Converter();
            Assert.Catch(() => converter.Import(new Row(), json, ExcelFieldsType.Json));
        }

        [Test]
        public void BlankReferencesRequireAnExplicitOptionalField()
        {
            var converter = Converter();
            Assert.Catch(() => converter.Import(new Row(), "", ExcelFieldsType.Json));
            converter.GetType().GetProperty("AllowEmpty").SetValue(converter, true);
            var row = new Row();
            converter.SetValue(row, typeof(Row).GetField("title"), converter.Import(row, "", ExcelFieldsType.Json));
            Assert.That(row.title.IsEmpty, Is.True);
            Assert.That(converter.Export(row, row.title, ExcelFieldsType.Json), Is.EqualTo(""));
        }

        [Test]
        public void ChangingOnlyIdentityPreservesAuthoredFallbackAndLocalVariables()
        {
            var converter = Converter();
            var reference = new LocalizedString("Old", "old") { FallbackState = FallbackBehavior.DontUseFallback };
            reference.Add("quantity", new IntVariable { Value = 7 });
            var row = new Row { title = reference };
            converter.SetValue(row, typeof(Row).GetField("title"), converter.Import(row,
                "{\"table\":\"Items\",\"entry\":\"name.sword\"}", ExcelFieldsType.Json));
            Assert.That(row.title.FallbackState, Is.EqualTo(FallbackBehavior.DontUseFallback));
            Assert.That(((IntVariable)row.title["quantity"]).Value, Is.EqualTo(7));
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YLFramework.Excel;

namespace YLFramework.Tests.Extensions.Data
{
    [TestFixture]
    [Category("YL.Module.Excel")]
    public class ExcelAuthoringTests
    {
        public enum Mode : long
        {
            Off = 0,
            On = 4294967296
        }
        [Serializable]
        public class Row
        {
            [ExcelScriptableObjectId] public int Id;
            public string Name;
            public Mode State;
            public List<int> Numbers;
            public long Count;
            public float Rate;
            [ExcelIgnore] public string Ignored;
            [Scale] public int Scaled;
        }
        public class ExplicitColumnRow
        {
            [ExcelScriptableObjectId] public int id;
            [ExcelColumnName("old_custom_column"), UnityEngine.Serialization.FormerlySerializedAs("unity_alias_only")] public string renamed;
            [UnityEngine.Serialization.FormerlySerializedAs("not_a_workbook_column")] public string plain;
        }
        public sealed class ScaleAttribute : CustomExcelFieldImportAndExport
        {
            public override object Import(object owner, object value, ExcelFieldsType type)
            {
                if (!(owner is Row))
                {
                    throw new InvalidOperationException("Converter did not receive the actual staged row.");
                }

                return int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 10;
            }
            public override object Export(object owner, object value, ExcelFieldsType type) => (int)value / 10;
            public override void SetValue(object owner, FieldInfo field, object value) => field.SetValue(owner, value);
        }

        private string _folder;
        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Environment.GetEnvironmentVariable("YL_EXCEL_TEST_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "YLExcelTests"), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_folder));
        }
        private string FilePath(string name) => Path.Combine(_folder, name);
        private static Type Api => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Integrations.Excel.Editor.ExcelAuthoring")).FirstOrDefault(t => t != null);
        internal static object Call(string name, Type type, params object[] args)
        {
            Assert.That(Api, Is.Not.Null, "Optional Excel authoring implementation is missing.");
            var method = Api.GetMethods().Single(m => m.Name == name && m.IsGenericMethodDefinition == (type != null) &&
                m.GetParameters().Length >= args.Length && m.GetParameters().Skip(args.Length).All(parameter => parameter.IsOptional));
            if (type != null)
            {
                method = method.MakeGenericMethod(type);
            }

            if (args.Length < method.GetParameters().Length)
            {
                args = args.Concat(Enumerable.Repeat(Type.Missing, method.GetParameters().Length - args.Length)).ToArray();
            }

            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }
        private static object Get(object target, string name) => target.GetType().GetProperty(name).GetValue(target);
        internal static string Commit(object plan, bool confirmed) => plan.GetType().GetMethod("Commit").Invoke(plan, new object[] { confirmed }).ToString();
        internal static string[] Errors(object plan) => ((IEnumerable)Get(plan, "Errors")).Cast<object>().Select(x => x.ToString()).ToArray();
        private static List<Row> Original() => new List<Row> { new Row { Id = 1, Name = "old", Numbers = new List<int> { 8 }, State = Mode.Off, Count = 3, Rate = 0.5f, Scaled = 20 }, new Row { Id = 9, Name = "remove", Numbers = new List<int>() } };
        private void Export(IEnumerable<Row> rows, string path) => Call("ListToExcel", typeof(Row), rows, path, "Sheet1");
        private object Preview(string path, List<Row> target) => Call("PreviewList", typeof(Row), path, target, null);

        [Test]
        public void ExplicitColumnsWorkAcrossAssembliesWithoutBorrowingUnityAliases()
        {
            string workbook = FilePath("explicit-column-contract.xlsx"), json = FilePath("explicit-column-contract.json");
            Call("ListToExcel", typeof(ExplicitColumnRow), new[] { new ExplicitColumnRow { id = 7, renamed = "stable", plain = "plain" } }, workbook, "Sheet1");
            using (var plan = (IDisposable)Call("PreviewJson", null, workbook, json, "", null))
            {
                Assert.That(Errors(plan), Is.Empty);
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }
            var row = JArray.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(json))).Single();
            Assert.That((string)row["old_custom_column"], Is.EqualTo("stable"));
            Assert.That((string)row["plain"], Is.EqualTo("plain"));
            Assert.That(row["renamed"], Is.Null);
            Assert.That(row["unity_alias_only"], Is.Null);
            Assert.That(row["not_a_workbook_column"], Is.Null);
        }

#if YL_GAMEPLAY_PACKAGE
        [Test]
        public void EveryExistingDomainColumnHasAnExplicitAssemblyIndependentName()
        {
            var owners = new[] { typeof(YLFramework.Skill.SkillsConfig), typeof(YLFramework.Skill.SkillConfigData), typeof(YLFramework.Buff.BuffsConfig),
                typeof(YLFramework.Buff.BuffConfigData), typeof(YLFramework.Buff.EffectData), typeof(YLFramework.Mission.MissionsConfig),
                typeof(YLFramework.Mission.MissionConfigData), typeof(YLFramework.Mission.MissionParameter), typeof(YLFramework.Mission.MissionCondition),
                typeof(YLFramework.Equipment.EquipmentsConfig), typeof(YLFramework.Equipment.EquipmentConfigData),
                typeof(YLFramework.DataAssets.ScriptableObjectConfigs), typeof(YLFramework.DataAssets.ScriptableObjectData) };
            int count = 0;
            foreach (var field in owners.SelectMany(type => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)))
            {
                var aliases = field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>().ToArray();
                if (aliases.Length == 0 || field.GetCustomAttribute<ExcelIgnore>() != null)
                {
                    continue;
                }
                count++;
                Assert.That(field.GetCustomAttribute<ExcelColumnNameAttribute>()?.Name, Is.EqualTo(aliases.Single().oldName), field.DeclaringType.FullName + "." + field.Name);
            }
            Assert.That(count, Is.EqualTo(32), "All pre-split shipped workbook aliases must remain explicit.");
        }
#endif

#if YL_GAMEPLAY_PACKAGE
        [TestCase("skills.xlsx", typeof(YLFramework.Skill.SkillConfigData))]
        [TestCase("buffs.xlsx", typeof(YLFramework.Buff.BuffConfigData))]
        [TestCase("missions.xlsx", typeof(YLFramework.Mission.MissionConfigData))]
        [TestCase("mission-parameters.xlsx", typeof(YLFramework.Mission.MissionParameter))]
        [TestCase("equipments.xlsx", typeof(YLFramework.Equipment.EquipmentConfigData))]
#endif
        [TestCase("dataassets.xlsx", typeof(YLFramework.DataAssets.ScriptableObjectData))]
        public void FrozenPreSplitWorkbooksImportAndCurrentExporterKeepsTheirActualColumns(string file, Type type)
        {
            const string Root = "Assets/Tests/YLFramework/Fixtures/NamespaceMigration/AssemblySplitV1/";
            var identity = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "FixtureIdentity.json")));
            var workbook = identity["workbooks"].Cast<JObject>().Single(row => (string)row["path"] == "Excel/" + file);
            var headers = workbook["headers"].Values<string>().ToArray();
            var frozenJson = FilePath("frozen-" + file + ".json");
            using (var plan = (IDisposable)Call("PreviewJson", null, global::YLFramework.Testing.PackageTestPaths.Physical(Root + "Excel/" + file), frozenJson, "", (string)workbook["sheet"]))
            {
                Assert.That(Errors(plan), Is.Empty);
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }
            var frozen = JArray.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(frozenJson))).Cast<JObject>().Single();
            Assert.That(frozen.Properties().Select(property => property.Name), Is.EqualTo(headers), "The actual captured workbook, not synthesized JSON, is the old input.");
            object value = typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type) ? UnityEngine.ScriptableObject.CreateInstance(type) : Activator.CreateInstance(type);
            try
            {
                var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
                rows.Add(value);
                string currentWorkbook = FilePath("current-" + file), currentJson = FilePath("current-" + file + ".json");
                if (value is UnityEngine.ScriptableObject)
                {
                    Call("ScriptableObjectToExcel", type, rows, currentWorkbook, (string)workbook["sheet"], type);
                }
                else
                {
                    Call("ListToExcel", type, rows, currentWorkbook, (string)workbook["sheet"]);
                }
                using (var plan = (IDisposable)Call("PreviewJson", null, currentWorkbook, currentJson, "", (string)workbook["sheet"]))
                {
                    Assert.That(Errors(plan), Is.Empty);
                    Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
                }
                var current = JArray.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(currentJson))).Cast<JObject>().Single();
                Assert.That(current.Properties().Select(property => property.Name), Is.EqualTo(headers), "New CLR/assembly ownership must not rename an existing worksheet column.");
            }
            finally { if (value is UnityEngine.Object asset) { UnityEngine.Object.DestroyImmediate(asset); } }
        }

        [Test]
        public void LossyConversionAlreadyPresentAtPreviewStillReportsActualFieldUpdate()
        {
            var rows = new List<Row> { new Row { Id = 1, Name = "same", Scaled = 21 } };
            string path = FilePath("lossy-existing.xlsx");
            Export(rows, path);
            using (var plan = (IDisposable)Preview(path, rows))
            {
                Assert.That(Errors(plan), Is.Empty);
                var changes = ((IEnumerable)Get(plan, "Changes")).Cast<object>().ToArray();
                Assert.That(changes.Length, Is.EqualTo(1), "Export(21) and Export(20) both equal 2, but confirmation still overwrites the actual value.");
                Assert.That(Get(changes[0], "Kind").ToString(), Is.EqualTo("Update"));
                Assert.That((string)Get(changes[0], "Before"), Does.Contain("\"Scaled\":21"));
                Assert.That((string)Get(changes[0], "After"), Does.Contain("\"Scaled\":20"));
                Assert.That(rows[0].Scaled, Is.EqualTo(21));
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }
            Assert.That(rows[0].Scaled, Is.EqualTo(20));
        }

        internal static void WriteWorkbook(string path, string[] names, string[] types, params object[][] values)
        {
            var backend = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MiniExcelLibs.MiniExcel")).FirstOrDefault(t => t != null)
                ?? Type.GetType("MiniExcelLibs.MiniExcel, MiniExcel", true);
            var rows = new List<Dictionary<string, object>>();
            foreach (var row in new[] { names.Cast<object>().ToArray(), types.Cast<object>().ToArray(), names.Select(n => (object)("note:" + n)).ToArray() }.Concat(values))
            {
                rows.Add(row.Select((v, i) => new { v, i }).ToDictionary(x => "C" + x.i, x => x.v));
            }

            backend.GetMethods().Single(m => m.Name == "SaveAs" && m.GetParameters()[0].ParameterType == typeof(string))
                .Invoke(null, new object[] { path, rows, false, "Sheet1", Enum.Parse(backend.Assembly.GetType("MiniExcelLibs.ExcelType"), "XLSX"), null, false });
        }

        [Test]
        public void RealXlsxRoundTripKeepsThreeHeadersEnumJsonLongAndCustomConversion()
        {
            var source = new List<Row> { new Row { Id = 2, Name = "name", State = Mode.On, Numbers = new List<int> { 1, 3 }, Count = 9007199254740993, Rate = 1.25f, Scaled = 70, Ignored = "secret" } };
            string path = FilePath("roundtrip.xlsx");
            Export(source, path);
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Take(2), Is.EqualTo(new byte[] { 80, 75 }));
            var target = Original();
            using (var plan = (IDisposable)Preview(path, target))
            {
                Assert.That(Errors(plan), Is.Empty);
                Assert.That(target[0].Name, Is.EqualTo("old"), "Preview must not mutate targets.");
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }
            Assert.That(target.Count, Is.EqualTo(1));
            Assert.That(target[0].Id, Is.EqualTo(2));
            Assert.That(target[0].State, Is.EqualTo(Mode.On));
            Assert.That(target[0].Numbers, Is.EqualTo(new[] { 1, 3 }));
            Assert.That(target[0].Count, Is.EqualTo(9007199254740993));
            Assert.That(target[0].Rate, Is.EqualTo(1.25f));
            Assert.That(target[0].Scaled, Is.EqualTo(70));
            Assert.That(target[0].Ignored, Is.Null);
        }
        [Test]
        public void PreviewReportsAddsUpdatesDeletesAndCancelPreservesReferences()
        {
            var target = Original();
            var before = target.ToArray();
            string path = FilePath("changes.xlsx");
            Export(new[] { new Row { Id = 1, Name = "new", Numbers = new List<int>() }, new Row { Id = 2, Name = "add", Numbers = new List<int>() } }, path);
            using (var plan = (IDisposable)Preview(path, target))
            {
                var changes = ((IEnumerable)Get(plan, "Changes")).Cast<object>().Select(c => Get(c, "Kind").ToString() + ":" + Get(c, "Key")).ToArray();
                Assert.That(changes, Is.EquivalentTo(new[] { "Update:1", "Add:2", "Delete:9" }));
                Assert.That(Commit(plan, false), Is.EqualTo("Cancelled"));
                Assert.That(Commit(plan, true), Is.EqualTo("Cancelled"), "Cancelled plans cannot later write.");
            }
            Assert.That(target, Is.EqualTo(before));
            Assert.That(target[0].Name, Is.EqualTo("old"));
        }

#if YL_GAMEPLAY_PACKAGE
        [TestCase(typeof(YLFramework.Buff.BuffConfigData), "buff_id", "id", "buff_name", "buffName")]
        [TestCase(typeof(YLFramework.Skill.SkillConfigData), "id", "id", "skill_name", "skillName")]
        [TestCase(typeof(YLFramework.Equipment.EquipmentConfigData), "id", "id", "equipment_name", "equipmentName")]
        public void ExistingDomainWorkbookHeadersRemainStableAfterFieldRenames(Type type, string oldId,
            string currentId, string oldName, string currentName)
        {
            var source = UnityEngine.ScriptableObject.CreateInstance(type);
            try
            {
                type.GetField(currentId).SetValue(source, 7);
                type.GetField(currentName).SetValue(source, "legacy workbook value");
                var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
                rows.Add(source);
                var workbook = FilePath("domain-headers.xlsx");
                var json = FilePath("domain-headers.json");
                Call("ScriptableObjectToExcel", type, rows, workbook, "Sheet1");
                using (var plan = (IDisposable)Call("PreviewJson", null, workbook, json, "", null))
                {
                    Assert.That(Errors(plan), Is.Empty);
                    Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
                }
                var row = JArray.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(json))).Single();
                Assert.That((int?)row[oldId], Is.EqualTo(7), oldId);
                Assert.That((string)row[oldName], Is.EqualTo("legacy workbook value"), oldName);
                if (oldId != currentId)
                {
                    Assert.That(row[currentId], Is.Null, "Do not silently create a new ID column.");
                }
                Assert.That(row[currentName], Is.Null, "C# authoring names must not rename shipped worksheet columns.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

#endif
        [Test]
        public void InvalidNumberAndJsonGiveCoordinatesAndPreserveEntireList()
        {
            string path = FilePath("bad.xlsx");
            WriteWorkbook(path, new[] { "Id", "Rate", "Numbers" }, new[] { "int", "float", "json" }, new object[] { 1, "not-float", "[broken" });
            var target = Original();
            var before = target.ToArray();
            using (var plan = (IDisposable)Preview(path, target))
            {
                Assert.That(Errors(plan).Any(e => e.Contains("B4")), Is.True);
                Assert.That(Errors(plan).Any(e => e.Contains("C4")), Is.True);
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(target, Is.EqualTo(before));
            Assert.That(target[0].Numbers, Is.EqualTo(new[] { 8 }));
        }

        [Test]
        public void RuntimeRootsDoNotStoreUnusedWorkbookPaths()
        {
            Assert.That(typeof(YLFramework.DataAssets.ScriptableObjectConfigs).GetField("excel_path"), Is.Null);
            Assert.That(typeof(YLFramework.Localization.MultiLanguageConfigs).GetField("excel_path"), Is.Null);
        }
        [Test]
        public void DuplicateHeaderDuplicateIdAndUnknownEnumAreNotSilentlyDropped()
        {
            string duplicateHeader = FilePath("header.xlsx");
            WriteWorkbook(duplicateHeader, new[] { "Id", "Id" }, new[] { "int", "int" }, new object[] { 1, 1 });
            using (var plan = (IDisposable)Preview(duplicateHeader, Original()))
            {
                Assert.That(Errors(plan), Is.Not.Empty);
            }

            string duplicateId = FilePath("id.xlsx");
            WriteWorkbook(duplicateId, new[] { "Id", "State" }, new[] { "int", "enum" }, new object[] { 1, "Off" }, new object[] { 1, "bad-enum" });
            using (var plan = (IDisposable)Preview(duplicateId, Original()))
            {
                Assert.That(Errors(plan).Any(e => e.Contains("B5")), Is.True);
                Assert.That(Errors(plan).Any(e => e.Contains("duplicate") || e.Contains("Duplicate")), Is.True);
            }
        }
        [Test]
        public void ChangedSourceOrTargetAfterPreviewRefusesStaleCommit()
        {
            string path = FilePath("stale.xlsx");
            Export(new[] { new Row { Id = 1, Name = "new", Numbers = new List<int>() } }, path);
            var target = Original();
            using (var plan = (IDisposable)Preview(path, target))
            {
                target[0].Name = "user-edit";
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
                Assert.That(target[0].Name, Is.EqualTo("user-edit"));
            }
            using (var plan = (IDisposable)Preview(path, target))
            {
                File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "changed");
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }
        }
        [Test]
        public void JsonPreviewCancellationAndLockedDestinationPreservePreviousFile()
        {
            string path = FilePath("json.xlsx"), json = FilePath("out.json");
            WriteWorkbook(path, new[] { "Id", "State", "Numbers", "_private" }, new[] { "int", "enum", "json", "string" }, new object[] { 7, "On", "[2,4]", "hidden" });
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(json), "old-json");
            using (var plan = (IDisposable)Call("PreviewJson", null, path, json, "_", null))
            {
                Assert.That(Commit(plan, false), Is.EqualTo("Cancelled"));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(json)), Is.EqualTo("old-json"));
            }
            using (var plan = (IDisposable)Call("PreviewJson", null, path, json, "_", null))
            using (File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(json), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }

            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(json)), Is.EqualTo("old-json"));
            using (var plan = (IDisposable)Call("PreviewJson", null, path, json, "_", null))
            {
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }

            var data = JArray.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(json)));
            Assert.That((int)data[0]["Id"], Is.EqualTo(7));
            Assert.That((string)data[0]["State"], Is.EqualTo("On"));
            Assert.That(data[0]["Numbers"].ToObject<int[]>(), Is.EqualTo(new[] { 2, 4 }));
            Assert.That(data[0]["_private"], Is.Null);
        }
        [Test]
        public void BadWorkbookAndCancelledJsonNeverCreateDestination()
        {
            string path = FilePath("corrupt.xlsx"), json = FilePath("untouched.json");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "not a zip");
            using (var plan = (IDisposable)Call("PreviewJson", null, path, json, "", null))
            {
                Assert.That(Errors(plan), Is.Not.Empty);
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(json)), Is.False);
        }
        [Test]
        public void InvariantCultureAndRowsAfterThirtyBlankRowsArePreserved()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string path = FilePath("gaps.xlsx");
                var rows = new List<object[]> { new object[] { 1, "1.25" } };
                rows.AddRange(Enumerable.Range(0, 35).Select(i => new object[] { null, null }));
                rows.Add(new object[] { 2, "2.5" });
                WriteWorkbook(path, new[] { "Id", "Rate" }, new[] { "int", "float" }, rows.ToArray());
                var target = new List<Row>();
                using (var plan = (IDisposable)Preview(path, target))
                {
                    Assert.That(Errors(plan), Is.Empty);
                    Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
                }
                Assert.That(target.Select(r => r.Rate), Is.EqualTo(new[] { 1.25f, 2.5f }));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        public class ReferenceRow
        {
            public int Id;
            public UnityEngine.Object Reference;
        }
        [Test]
        public void MalformedGuidIsDiagnosedBeforeCallingAssetDatabaseAndKeepsList()
        {
            string path = FilePath("guid.xlsx");
            WriteWorkbook(path, new[] { "Id", "Reference" }, new[] { "int", "objectreference" }, new object[] { 1, "not-a-guid" });
            var old = new ReferenceRow { Id = 9 };
            var target = new List<ReferenceRow> { old };
            using (var plan = (IDisposable)Call("PreviewList", typeof(ReferenceRow), path, target, null))
            {
                Assert.That(Errors(plan).Any(e => e.Contains("B4") && e.Contains("GUID")), Is.True);
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(target.Single(), Is.SameAs(old));
        }
        [Test]
        public void CustomConversionExceptionCannotPartiallyReplaceList()
        {
            string path = FilePath("custom-error.xlsx");
            WriteWorkbook(path, new[] { "Id", "Scaled" }, new[] { "int", "int" }, new object[] { 1, 2 }, new object[] { 2, "bad" });
            var target = Original();
            using (var plan = (IDisposable)Preview(path, target))
            {
                Assert.That(Errors(plan).Any(e => e.Contains("B5")), Is.True);
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(target[0].Scaled, Is.EqualTo(20));
            Assert.That(target[1].Id, Is.EqualTo(9));
        }
        [Test]
        public void LockedWorkbookExportKeepsBytesAndCleansOnlyItsTemporaryFile()
        {
            string path = FilePath("locked.xlsx");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "previous bytes");
            using (File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => Export(Original(), path));
            }

            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo("previous bytes"));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_folder), ".*.tmp.xlsx"), Is.Empty);
        }
        [Test]
        public void DuplicateTargetsAndIncompleteHeadersFailWithoutWrites()
        {
            string path = FilePath("short.xlsx");
            WriteWorkbook(path, new[] { "Id" }, new[] { "int" }, new object[] { 1 });
            var targets = new List<Row> { new Row { Id = 1 }, new Row { Id = 1 } };
            using (var plan = (IDisposable)Preview(path, targets))
            {
                Assert.That(Errors(plan).Any(e => e.Contains("Duplicate target ID")), Is.True);
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }
            WriteWorkbook(FilePath("wrong-type.xlsx"), new[] { "Id" }, new[] { "mystery" }, new object[] { 1 });
            using (var plan = (IDisposable)Preview(FilePath("wrong-type.xlsx"), Original()))
            {
                Assert.That(Errors(plan).Any(e => e.Contains("A2")), Is.True);
            }
        }
        [Test]
        public void NumericalEnumUsesItsRealUnderlyingTypeAndInvalidValueFails()
        {
            string path = FilePath("numeric-enum.xlsx");
            WriteWorkbook(path, new[] { "Id", "State" }, new[] { "int", "enum" }, new object[] { 1, "4294967296" });
            var target = new List<Row>();
            using (var plan = (IDisposable)Preview(path, target))
            {
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }

            Assert.That(target[0].State, Is.EqualTo(Mode.On));
            string invalid = FilePath("undefined-enum.xlsx");
            WriteWorkbook(invalid, new[] { "Id", "State" }, new[] { "int", "enum" }, new object[] { 1, 2 });
            using (var plan = (IDisposable)Preview(invalid, target))
            {
                Assert.That(Errors(plan).Any(e => e.Contains("B4")), Is.True);
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(target[0].State, Is.EqualTo(Mode.On));
        }
        [Test]
        public void DisposeCancelsAndSuccessfulCommitCannotBeRepeated()
        {
            string path = FilePath("once.xlsx");
            Export(new[] { new Row { Id = 2, Numbers = new List<int>() } }, path);
            var target = Original();
            var cancelled = (IDisposable)Preview(path, target);
            cancelled.Dispose();
            Assert.That(Commit(cancelled, true), Is.EqualTo("Cancelled"));
            using (var plan = (IDisposable)Preview(path, target))
            {
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
                target[0].Name = "subsequent-edit";
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
                Assert.That(target[0].Name, Is.EqualTo("subsequent-edit"));
            }
        }
        public class UnsignedRow
        {
            public int Id;
            public uint Number;
            public ulong Count;
        }
        [Test]
        public void UnsignedIntegerBoundariesSurviveListAndJsonExportsWithoutTruncation()
        {
            string path = FilePath("unsigned.xlsx"), json = FilePath("unsigned.json");
            Call("ListToExcel", typeof(UnsignedRow), new[] { new UnsignedRow { Id = 1, Number = uint.MaxValue, Count = ulong.MaxValue } }, path, "Sheet1");
            var target = new List<UnsignedRow>();
            using (var plan = (IDisposable)Call("PreviewList", typeof(UnsignedRow), path, target, null))
            {
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }

            Assert.That(target[0].Number, Is.EqualTo(uint.MaxValue));
            Assert.That(target[0].Count, Is.EqualTo(ulong.MaxValue));
            using (var plan = (IDisposable)Call("PreviewJson", null, path, json, "", null))
            {
                Assert.That(Errors(plan), Is.Empty);
                Assert.That(Commit(plan, true), Is.EqualTo("Applied"));
            }
            var data = JArray.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(json)));
            Assert.That((uint)data[0]["Number"], Is.EqualTo(uint.MaxValue));
            Assert.That((ulong)data[0]["Count"], Is.EqualTo(ulong.MaxValue));
        }
        [Test]
        public void LossyCustomExportAndIgnoredFieldCannotHideTargetEditsAfterPreview()
        {
            string path = FilePath("hidden-edits.xlsx");
            Export(new[] { new Row { Id = 1, Scaled = 50 } }, path);
            var target = Original();
            using (var plan = (IDisposable)Preview(path, target))
            {
                target[0].Scaled = 21;
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
                Assert.That(target[0].Scaled, Is.EqualTo(21));
            }
            using (var plan = (IDisposable)Preview(path, target))
            {
                target[0].Ignored = "new user edit";
                Assert.That(Commit(plan, true), Is.EqualTo("Failed"));
                Assert.That(target[0].Ignored, Is.EqualTo("new user edit"));
            }
        }
        [Test]
        public void WriterExceptionAfterPartialTemporaryOutputKeepsDestinationAndRemovesTemporary()
        {
            string path = FilePath("fault.json");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "original");
            Assert.That(Api, Is.Not.Null);
            var files = Api.Assembly.GetType("YLFramework.Integrations.Excel.Editor.ExcelFiles", true);
            Action<string> writer = temporary =>
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(temporary), "partial");
                throw new IOException("injected writer failure");
            };
            var error = Assert.Throws<TargetInvocationException>(() => files.GetMethod("WriteAtomic", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { path, writer }));
            Assert.That(error.InnerException, Is.TypeOf<IOException>());
            Assert.That(error.InnerException.Message, Is.EqualTo("injected writer failure"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo("original"));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_folder)).Select(Path.GetFileName), Is.EqualTo(new[] { "fault.json" }));
        }
        [Test]
        public void EmptyBaseListTemplateIncludesBoundDerivedColumnsAndRejectsIncompatibleBinding()
        {
            string path = FilePath("bound-template.xlsx");
            Call("ScriptableObjectToExcel", typeof(YLFramework.DataAssets.ScriptableObjectData), new List<YLFramework.DataAssets.ScriptableObjectData>(), path, "Sheet1", typeof(ExcelBoundDataFixture));
            var backend = Type.GetType("MiniExcelLibs.MiniExcel, MiniExcel", true);
            var query = backend.GetMethods().Single(m => m.Name == "Query" && !m.IsGenericMethod && m.GetParameters()[0].ParameterType == typeof(string));
            var rows = (IEnumerable)query.Invoke(null, new object[] { path, false, null, Enum.Parse(backend.Assembly.GetType("MiniExcelLibs.ExcelType"), "XLSX"), "A1", null });
            var headers = ((IDictionary<string, object>)rows.Cast<object>().First()).Values.Select(v => Convert.ToString(v)).ToArray();
            Assert.That(headers, Does.Contain("power"));
            Assert.That(headers, Does.Contain("flavor"));
            Assert.That(headers, Does.Contain("id"));
            Assert.That(headers, Does.Not.Contain("parent"));
            byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            Assert.Throws<ArgumentException>(() => Call("ScriptableObjectToExcel", typeof(YLFramework.DataAssets.ScriptableObjectData), new List<YLFramework.DataAssets.ScriptableObjectData>(), path, "Sheet1", typeof(string)));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before));
        }
    }
}

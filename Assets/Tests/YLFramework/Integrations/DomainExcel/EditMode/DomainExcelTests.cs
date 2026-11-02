#if UNITY_EDITOR && YL_DOMAIN_EXCEL_PACKAGE
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using YLFramework.Mission;
using YLFramework.Integrations.DomainExcel.Editor;
using YLFramework.Integrations.Excel.Editor;

namespace YLFramework.Domain.Excel.Tests
{
    public class DomainExcelTests
    {
        private string _path;
        [SetUp] public void Setup() => _path = Path.Combine(Path.GetTempPath(), "YLDomainC-params-" + Guid.NewGuid().ToString("N") + ".xlsx");
        [TearDown]
        public void Cleanup()
        {
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_path)))
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_path));
        }

        [Test]
        public void MissionParametersRoundTripRequiresConfirmation()
        {
            DomainExcelCommands.ExportParameters(new[] { new MissionParameter { name = "coins", type = MissionParamType.Int, description = "author-data" } }, _path);
            var values = new List<MissionParameter> { new MissionParameter { name = "old" } };
            using (var cancel = DomainExcelCommands.PreviewParameters(_path, values))
            {
                Assert.That(cancel.Errors, Is.Empty);
                Assert.That(cancel.Changes.Count, Is.EqualTo(2));
                Assert.That(cancel.Commit(false), Is.EqualTo(ExcelCommitResult.Cancelled));
                Assert.That(values[0].name, Is.EqualTo("old"));
            }
            using var plan = DomainExcelCommands.PreviewParameters(_path, values);
            Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            Assert.That(values[0].name, Is.EqualTo("coins"));
            Assert.That(values[0].type, Is.EqualTo(MissionParamType.Int));
            Assert.That(values[0].description, Is.EqualTo("author-data"));
        }

        [Test]
        public void DuplicateParameterNamesAreRealWorkbookErrors()
        {
            DomainExcelCommands.ExportParameters(new[] { new MissionParameter { name = "same" }, new MissionParameter { name = "same" } }, _path);
            var values = new List<MissionParameter>();
            using var plan = DomainExcelCommands.PreviewParameters(_path, values);
            Assert.That(plan.Errors, Is.Not.Empty);
            Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
            Assert.That(values, Is.Empty);
        }

        [Test]
        public void WorkbookChangeAfterPreviewCannotWriteTarget()
        {
            DomainExcelCommands.ExportParameters(new[] { new MissionParameter { name = "original" } }, _path);
            var values = new List<MissionParameter>();
            using var plan = DomainExcelCommands.PreviewParameters(_path, values);
            DomainExcelCommands.ExportParameters(new[] { new MissionParameter { name = "replacement" } }, _path);
            Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
            Assert.That(values, Is.Empty);
        }
    }
}
#endif
